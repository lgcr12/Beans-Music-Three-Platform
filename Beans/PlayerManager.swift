import AVFoundation
import MediaPlayer
import SwiftUI
import UIKit

enum PlayMode: String, CaseIterable, Identifiable, Codable {
    case sequential
    case repeatOne
    case shuffle

    var id: String { rawValue }

    var icon: String {
        switch self {
        case .sequential: return "repeat"
        case .repeatOne: return "repeat.1"
        case .shuffle: return "shuffle"
        }
    }

    var title: String {
        switch self {
        case .sequential: return "顺序播放"
        case .repeatOne: return "单曲循环"
        case .shuffle: return "随机播放"
        }
    }
}

final class PlaybackClock: ObservableObject {
    @Published private(set) var progress: Double = 0
    @Published private(set) var duration: Double = 0

    func update(progress: Double? = nil, duration: Double? = nil) {
        let apply = {
            if let progress, abs(progress - self.progress) > 0.01 {
                self.progress = progress
            }
            if let duration, abs(duration - self.duration) > 0.01 {
                self.duration = duration
            }
        }
        if Thread.isMainThread {
            apply()
        } else {
            DispatchQueue.main.async(execute: apply)
        }
    }
}

final class PlayerManager: NSObject, ObservableObject {
    static let shared = PlayerManager()
    @Published var queue: [Song] = []
    @Published var currentIndex = 0
    @Published var isPlaying = false
    @Published var isBuffering = false
    @Published var loadFailed = false
    /// 切歌代次：防止旧歌的 URL 解析任务覆盖新歌（快速切歌时）
    private var loadGeneration = 0
    let clock = PlaybackClock()
    var progress: Double = 0 {
        didSet { clock.update(progress: progress) }
    }
    var duration: Double = 0 {
        didSet { clock.update(duration: duration) }
    }
    @Published var playMode: PlayMode = .sequential
    @Published var rate: Double = 1.0
    @Published var sleepTimerEndsAt: Date?
    @Published var sleepTimerRemaining: Int = 0
    @Published var history: [Song] = []
    @Published var playCounts: [String: Int] = [:]

    private var player: AVPlayer?
    private var timeObserver: Any?
    private var endObserver: NSObjectProtocol?
    private var failureObserver: NSObjectProtocol?
    private var itemStatusObserver: NSKeyValueObservation?
    private var timeControlStatusObserver: NSKeyValueObservation?
    /// QQ 官方 vkey 地址交给 AVPlayer 后仍可能因 CDN 节点或音质不可用而失败。
    private var attemptedQQOfficialBRsBySong: [String: Set<String>] = [:]
    private var playbackRecoveryInFlightSongKey: String?
    private var playbackConfirmed = false
    private var pendingThirdPartyVIPNotice: ThirdPartyVIPNotice?
    private var sessionConfigured = false
    private var playOrder: [Int] = []
    private var orderPosition = 0
    private var sleepTimer: Timer?
    private var lastCountedSongID: String?
    private var wasPlayingBeforeInterruption = false
    private var lastPublishedProgress: Double = -1
    private var lastPlaybackStateSaveAt: Date = .distantPast
    private var lastPlaybackSyncAt: Date = .distantPast
    private var applyingRemoteSync = false
    private var lastNowPlayingArtworkKey: String?
    private static let nowPlayingArtworkCache = NSCache<NSURL, UIImage>()

    private let historyKey = "beans.history"
    private let countsKey = "beans.playcounts"
    private let playbackStateKey = "beans.playback.state.v1"
    private let audioMixKey = "beans.audio.mixothers.v1"
    private let thirdPartyVIPNoticeKey = "beans.showThirdPartyVIPNotice"
    private let defaults = UserDefaults.standard

    private struct PlaybackState: Codable {
        var queue: [Song]
        var currentIndex: Int
        var progress: Double
        var duration: Double
        var playMode: PlayMode
        var rate: Double
        var savedAt: Date
    }

    private struct ThirdPartyVIPNotice {
        let songKey: String
        let message: String
    }

    var currentSong: Song? {
        queue.indices.contains(currentIndex) ? queue[currentIndex] : nil
    }

    override init() {
        super.init()
        loadHistory()
        loadPlayCounts()
        loadPlaybackState()
        observeInterruptions()
        observeRouteChanges()
        setupRemoteCommands()
    }

    // MARK: - 播放控制

    func play(songs: [Song], startAt index: Int = 0) {
        guard !songs.isEmpty else { return }
        queue = songs
        buildPlayOrder()
        jumpToOrderPosition(min(max(index, 0), songs.count - 1))
    }

    func playSong(_ song: Song, in context: [Song]) {
        play(songs: context, startAt: context.firstIndex(of: song) ?? 0)
    }

    /// 插队播放：把歌曲放到当前歌曲之后并立即播放
    func playNext(_ song: Song) {
        guard !queue.isEmpty else {
            play(songs: [song], startAt: 0)
            return
        }
        let insertAt = currentIndex + 1
        queue.insert(song, at: min(insertAt, queue.count))
        buildPlayOrder()
        jumpToOrderPosition(min(insertAt, queue.count - 1))
        persistCurrentPlaybackState()
    }

    func togglePlayPause() {
        guard let player else {
            resumeRestoredSongIfNeeded()
            return
        }
        if player.timeControlStatus == .playing {
            player.pause()
            isPlaying = false
        } else {
            player.playImmediately(atRate: Float(rate))
            isPlaying = true
        }
        updateNowPlaying()
        persistCurrentPlaybackState()
    }

    func next(manual: Bool = true) {
        guard !queue.isEmpty else { return }
        if playMode == .repeatOne && manual {
            restartCurrent()
            return
        }
        advance()
        loadCurrent()
        persistCurrentPlaybackState()
    }

    func previous() {
        guard !queue.isEmpty else { return }
        // 直接切换到上一首（不再做“播放超过 3 秒先重头播放”的判断）
        if playMode == .shuffle {
            orderPosition = (orderPosition - 1 + playOrder.count) % playOrder.count
            currentIndex = playOrder[orderPosition]
        } else {
            currentIndex = (currentIndex - 1 + queue.count) % queue.count
        }
        loadCurrent()
        persistCurrentPlaybackState()
    }

    func seek(to seconds: Double) {
        let clamped = max(0, min(seconds, max(duration, 0)))
        progress = clamped
        // 用 seek 完成回调同步真实进度：避免暂停状态下拖动进度后，歌词定位与实际播放位置不一致
        player?.seek(
            to: CMTime(seconds: clamped, preferredTimescale: 600),
            toleranceBefore: .zero,
            toleranceAfter: .zero
        ) { [weak self] finished in
            guard let self, finished else { return }
            let actual = self.player?.currentTime().seconds ?? clamped
            if abs(actual - self.progress) > 0.25 {
                self.progress = actual
            }
        }
        updateNowPlaying()
        persistCurrentPlaybackState()
    }

    func seekBy(_ delta: Double) {
        seek(to: progress + delta)
    }

    func togglePlayMode() {
        switch playMode {
        case .sequential: playMode = .repeatOne
        case .repeatOne: playMode = .shuffle
        case .shuffle: playMode = .sequential
        }
        buildPlayOrder()
        persistCurrentPlaybackState()
    }

    func setRate(_ newRate: Double) {
        rate = newRate
        if isPlaying {
            player?.playImmediately(atRate: Float(newRate))
        }
        updateNowPlaying()
        persistCurrentPlaybackState()
    }

    func playQueueIndex(_ index: Int) {
        guard queue.indices.contains(index) else { return }
        jumpToOrderPosition(index)
        persistCurrentPlaybackState()
    }

    func removeFromQueue(at index: Int) {
        guard queue.indices.contains(index), queue.count > 1 else { return }
        let removedID = queue[index].id
        queue.remove(at: index)
        if index < currentIndex {
            currentIndex -= 1
        } else if index == currentIndex {
            currentIndex = min(currentIndex, queue.count - 1)
            loadCurrent()
        }
        buildPlayOrder(avoiding: removedID)
        persistCurrentPlaybackState()
    }

    func retryCurrent() {
        loadFailed = false
        loadCurrent()
    }

    /// 删除单条播放历史（含持久化）
    func removeHistory(at offsets: IndexSet) {
        let removed = offsets.compactMap { history.indices.contains($0) ? history[$0] : nil }
        for index in offsets.sorted(by: >) {
            guard history.indices.contains(index) else { continue }
            history.remove(at: index)
        }
        if let data = try? JSONEncoder().encode(history) {
            defaults.set(data, forKey: historyKey)
        }
        for song in removed { queueHistorySync(song, deleted: true) }
        persistCurrentPlaybackState()
    }

    /// 清空播放历史（含持久化）
    func clearHistory() {
        let removed = history
        history.removeAll()
        defaults.removeObject(forKey: historyKey)
        for song in removed { queueHistorySync(song, deleted: true) }
        persistCurrentPlaybackState()
    }

    /// 清空队列，仅保留当前歌曲
    func clearQueue() {
        guard !queue.isEmpty else { return }
        if let current = currentSong {
            queue = [current]
            currentIndex = 0
        } else {
            queue = []
            currentIndex = 0
        }
        buildPlayOrder()
        persistCurrentPlaybackState()
    }

    // MARK: - 睡眠定时

    func startSleepTimer(minutes: Int) {
        stopSleepTimer()
        sleepTimerEndsAt = Date().addingTimeInterval(TimeInterval(minutes * 60))
        sleepTimerRemaining = minutes * 60
        let timer = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
            guard let self, let end = self.sleepTimerEndsAt else { return }
            let remain = Int(end.timeIntervalSinceNow)
            self.sleepTimerRemaining = max(0, remain)
            if remain <= 0 {
                self.stopSleepTimer()
                self.pausePlayback()
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        sleepTimer = timer
    }

    func stopSleepTimer() {
        sleepTimer?.invalidate()
        sleepTimer = nil
        sleepTimerEndsAt = nil
        sleepTimerRemaining = 0
    }

    var sleepTimerFormatted: String? {
        guard sleepTimerRemaining > 0 else { return nil }
        return String(format: "%d:%02d", sleepTimerRemaining / 60, sleepTimerRemaining % 60)
    }

    private func pausePlayback() {
        player?.pause()
        isPlaying = false
        updateNowPlaying()
        persistCurrentPlaybackState()
    }

    // MARK: - 播放顺序

    private func buildPlayOrder(avoiding removedID: Int? = nil) {
        switch playMode {
        case .shuffle:
            var indices = Array(queue.indices).filter { $0 != removedID }
            indices.shuffle()
            playOrder = indices
            orderPosition = 0
        default:
            playOrder = Array(queue.indices)
            orderPosition = currentIndex
        }
    }

    private func advance() {
        switch playMode {
        case .shuffle:
            guard !playOrder.isEmpty else { return }
            orderPosition = (orderPosition + 1) % playOrder.count
            currentIndex = playOrder[orderPosition]
        default:
            currentIndex = (currentIndex + 1) % queue.count
            orderPosition = currentIndex
        }
    }

    private func jumpToOrderPosition(_ index: Int) {
        currentIndex = index
        if playMode == .shuffle {
            orderPosition = 0
            if let pos = playOrder.firstIndex(of: index) {
                orderPosition = pos
            }
        } else {
            orderPosition = index
        }
        loadCurrent()
        persistCurrentPlaybackState()
    }

    // MARK: - 播放

    private func restartCurrent() {
        seek(to: 0)
        player?.playImmediately(atRate: Float(rate))
        isPlaying = true
        updateNowPlaying()
        persistCurrentPlaybackState()
    }

    private func loadCurrent(resumeAt savedProgress: Double? = nil) {
        guard let song = currentSong else { return }
        loadGeneration += 1
        let generation = loadGeneration
        attemptedQQOfficialBRsBySong.removeValue(forKey: song.identityKey)
        playbackRecoveryInFlightSongKey = nil
        // 切歌立即暂停旧音频，避免新歌加载期间旧歌继续播放造成“切歌卡住”感
        player?.pause()
        duration = song.duration
        progress = 0
        isPlaying = false
        isBuffering = true
        loadFailed = false
        pushHistory(song)
        Task {
            var urlString: String?
            var resolvedThirdParty: UnblockService.Resolved?
            var qqOfficialBR: String?
            var attemptedQQOfficialBRs: [String] = []
            var qqPlaybackFailure: QQMusicAPI.SongURLFailure?
            // 版权受限歌手（周杰伦）：允许第三方音源，但启用严格模式（歌名+歌手+时长三重匹配原唱，校验不过拒绝，绝不播放翻唱）
            // 免费听歌（灰色歌曲解锁）总开关：默认开启，优先使用内置预设音源兜底。
            let enableUnblock = defaults.object(forKey: "beans.enableUnblock") as? Bool ?? true
            let strictUnlock = shouldLockOfficialOnly(song)
            let quality = BeansAudioQuality.current
            BeansLogger.shared.log("▶ 开始播放：\(song.name) - \(song.artists)｜平台=\(song.source.rawValue) id=\(song.id) 音质=\(quality.level) 免费听歌=\(enableUnblock ? "开" : "关") 官方受限=\(strictUnlock ? "是" : "否")", level: .info)
            if song.source == .kugou {
                urlString = try? await KugouMusicAPI.shared.songURL(song: song)
                if urlString == nil {
                    resolvedThirdParty = await kugouFallback(song: song, enableUnblock: enableUnblock)
                }
            } else if song.source == .qq, let mid = song.qqMid {
                // 是否有播放权益以 vkey 实际返回为准；会员接口识别失败时也必须尝试官方地址。
                let officialResolution = try? await QQMusicAPI.shared.resolveSongURL(
                    songmid: mid,
                    mediaMid: song.qqMediaMid,
                    quality: quality
                )
                let officialResult = officialResolution?.result
                qqPlaybackFailure = officialResolution?.failure
                urlString = officialResult?.url
                qqOfficialBR = officialResult?.br
                attemptedQQOfficialBRs = officialResult?.attemptedBRs ?? []
                let resolvedQQOfficialBR = qqOfficialBR
                let resolvedAttemptedQQOfficialBRs = attemptedQQOfficialBRs
                if urlString == nil {
                    (urlString, resolvedThirdParty) = await qqFallback(song: song, quality: quality, enableUnblock: enableUnblock, strict: strictUnlock)
                }
                await MainActor.run {
                    guard generation == self.loadGeneration,
                          self.currentSong?.identityKey == song.identityKey else { return }
                    if let resolvedQQOfficialBR {
                        self.attemptedQQOfficialBRsBySong[song.identityKey, default: []].insert(resolvedQQOfficialBR)
                    }
                    self.attemptedQQOfficialBRsBySong[song.identityKey, default: []].formUnion(resolvedAttemptedQQOfficialBRs)
                }
            } else {
                (urlString, resolvedThirdParty) = await neteaseResolve(song: song, quality: quality, enableUnblock: enableUnblock, strict: strictUnlock)
            }
            if let resolved = resolvedThirdParty {
                let notice = self.thirdPartyVIPNotice(for: song, sourceTitle: resolved.sourceTitle)
                await MainActor.run {
                    guard generation == self.loadGeneration else { return }
                    self.setupPlayer(url: resolved.url, resumeAt: savedProgress, thirdPartyVIPNotice: notice)
                }
                return
            }
            guard let urlString, let url = URL(string: urlString) else {
                let thirdPartyAttempted = resolvedThirdParty != nil
                await MainActor.run {
                    guard generation == self.loadGeneration else { return }
                    self.isBuffering = false
                    self.loadFailed = true
                    if song.source == .qq, let qqPlaybackFailure {
                        BeansLogger.shared.log("QQ 官方播放失败：\(song.name)｜\(qqPlaybackFailure.userMessage)", level: .error)
                        ToastCenter.shared.show(qqPlaybackFailure.userMessage, duration: 4)
                    } else if song.source != .kugou, self.shouldLockOfficialOnly(song) {
                        BeansLogger.shared.log("播放失败：\(song.name) - 未找到原唱音源（官方受限），拒绝翻唱版本", level: .error)
                        ToastCenter.shared.show("《\(song.name)》未找到原唱音源（官方受限），已停止播放，拒绝翻唱版本")
                    } else {
                        let hint = thirdPartyAttempted ? "（第三方音源尝试后无结果）" : "（第三方音源未命中）"
                        BeansLogger.shared.log("播放失败：\(song.name) - 无法解析播放地址\(hint)｜音质=\(quality.level) 免费听歌=\(enableUnblock ? "开" : "关")", level: .error)
                        ToastCenter.shared.show("无法解析播放地址，请稍后重试")
                    }
                }
                return
            }
            let resolvedQQOfficialBR = qqOfficialBR
            let resolvedAttemptedQQOfficialBRs = attemptedQQOfficialBRs
            await MainActor.run {
                guard generation == self.loadGeneration else { return }
                self.setupPlayer(
                    url: url,
                    resumeAt: savedProgress,
                    qqOfficialBR: resolvedQQOfficialBR,
                    attemptedQQOfficialBRs: resolvedAttemptedQQOfficialBRs
                )
            }
        }
    }

    /// 网易云播放地址解析：按设置音质取 URL，VIP/灰色歌曲交给第三方解锁（借鉴 Kumone）
    private func neteaseResolve(song: Song, quality: BeansAudioQuality, enableUnblock: Bool, strict: Bool = false) async -> (String?, UnblockService.Resolved?) {
        var urlString: String?
        var resolved: UnblockService.Resolved?
        let infos = try? await NetEaseAPI.shared.songURLInfo(ids: [song.id], level: quality.level)
        var info = infos?[song.id]
        if (info?.url == nil || info?.freeTrial == true), quality != .standard {
            // 高音质拿不到时自动回落到标准音质
            let fallback = try? await NetEaseAPI.shared.songURLInfo(ids: [song.id], level: "standard")
            info = fallback?[song.id]
        }
        BeansLogger.shared.log("网易云解析：\(song.name) 音质=\(quality.level) 官方URL=\(info?.url == nil ? "无" : "有") 试听=\(info?.freeTrial == true ? "是" : "否")", level: .debug)
        // 试听片段 / 无 URL 一律不直接播放，交给第三方解锁，避免"只能试听"
        if let u = info?.url, info?.freeTrial != true {
            urlString = u
        }
        if urlString == nil, enableUnblock {
            resolved = await UnblockService.resolve(
                name: song.name,
                artists: song.artists,
                neteaseID: song.id,
                songSource: .netease,
                strict: strict
            )
        }
        BeansLogger.shared.log("网易云结果：\(song.name) 官方=\(urlString != nil ? "是" : "否") 第三方=\(resolved != nil ? "命中" : "未用/未命中")", level: .debug)
        return (urlString, resolved)
    }

    /// QQ 歌曲兜底：先在网易云按 歌名+歌手 匹配同名歌曲，免费完整 URL 直接播，VIP/无 URL 交给第三方解锁
    private func qqFallback(song: Song, quality: BeansAudioQuality, enableUnblock: Bool, strict: Bool = false) async -> (String?, UnblockService.Resolved?) {
        var urlString: String?
        var resolved: UnblockService.Resolved?
        if enableUnblock {
            resolved = await UnblockService.resolve(
                name: song.name,
                artists: song.artists,
                neteaseID: 0,
                songSource: .qq,
                qqMid: song.qqMid,
                strict: strict
            )
        }
        if resolved != nil { return (nil, resolved) }
        if let matched = await matchNetEaseSong(name: song.name, artists: song.artists, durationMS: Int(song.duration * 1000), strict: strict) {
            let infos = try? await NetEaseAPI.shared.songURLInfo(ids: [matched.id], level: quality.level)
            var info = infos?[matched.id]
            if (info?.url == nil || info?.freeTrial == true), quality != .standard {
                let fallback = try? await NetEaseAPI.shared.songURLInfo(ids: [matched.id], level: "standard")
                info = fallback?[matched.id]
            }
            // 免费完整 URL 直接用；试听片段 / 无 URL 交给第三方解锁
            if let u = info?.url, info?.freeTrial != true {
                urlString = u
            } else if enableUnblock {
                resolved = await UnblockService.resolve(
                    name: matched.name,
                    artists: matched.artists,
                    neteaseID: matched.id,
                    songSource: .netease,
                    strict: strict
                )
            }
        }
        BeansLogger.shared.log("QQ兜底：\(song.name) 官方=\(urlString != nil ? "是" : "否") 第三方=\(resolved != nil ? "命中" : "未用/未命中")", level: .debug)
        return (urlString, resolved)
    }

    /// 酷狗兜底：官方播放失败后使用内置音源作为备选。
    private func kugouFallback(song: Song, enableUnblock: Bool) async -> UnblockService.Resolved? {
        guard enableUnblock else { return nil }
        let kugouID = song.kugouHash ?? song.kugouAlbumAudioId ?? ""
        if kugouID.isEmpty {
            BeansLogger.shared.log("酷狗兜底跳过：缺少 album_audio_id/hash", level: .debug)
        } else {
            let resolved = await UnblockService.resolve(
                name: song.name,
                artists: song.artists,
                neteaseID: 0,
                songSource: .kugou,
                kugouID: kugouID
            )
            if let resolved {
                BeansLogger.shared.log("酷狗兜底：\(song.name) 酷狗音源=命中", level: .debug)
                return resolved
            }
        }

        let strict = shouldLockOfficialOnly(song)
        if let matched = await matchNetEaseSong(
            name: song.name,
            artists: song.artists,
            durationMS: Int(song.duration * 1000),
            strict: strict
        ) {
            let resolved = await UnblockService.resolve(
                name: matched.name,
                artists: matched.artists,
                neteaseID: matched.id,
                songSource: .netease,
                strict: strict
            )
            BeansLogger.shared.log("酷狗兜底转网易云音源：\(song.name) -> \(matched.name) 第三方=\(resolved != nil ? "命中" : "未命中")", level: .debug)
            return resolved
        }

        BeansLogger.shared.log("酷狗兜底：\(song.name) 第三方=未命中", level: .debug)
        return nil
    }

    /// 版权受限歌手名单：这些歌手的歌曲必须严格校验原唱（第三方搜索会误匹配翻唱，如周杰伦）
    /// 兼容第三方返回的英文歌手名（Jay Chou），统一按别名判断，避免漏判导致播放翻唱
    private func shouldLockOfficialOnly(_ song: Song) -> Bool {
        let artists = song.artists.lowercased()
        return artists.contains("周杰伦") || artists.contains("jay chou") || artists.contains("jaychou")
    }

    /// 在网易云按 歌名+歌手 匹配同名歌曲（QQ vkey 失败时的免费播放兜底）
    private func matchNetEaseSong(name: String, artists: String, durationMS: Int, strict: Bool = false) async -> Song? {
        let keyword = ([name, artists].filter { !$0.isEmpty }).joined(separator: " ")
        guard !keyword.isEmpty,
              let results = try? await NetEaseAPI.shared.search(keyword: keyword, limit: 8),
              !results.isEmpty else { return nil }
        let target = Double(durationMS) / 1000.0
        let artistTokens = artists.lowercased().split(whereSeparator: { $0 == " " || $0 == "/" || $0 == "&" }).map(String.init)
        // 优先：歌手匹配 + 时长接近（兼容 Jay Chou 别名）
        if let hit = results.first(where: { song in
            let durOK = abs(song.duration - target) < 12
            let songArtists = song.artists.lowercased()
            let artistOK = artistTokens.contains { !$0.isEmpty && songArtists.contains($0) }
                || (songArtists.contains("周杰伦") && artists.lowercased().contains("jay chou"))
            return durOK && artistOK
        }) { return hit }
        // 严格模式（周杰伦等版权歌手）：找不到原唱直接放弃，绝不返回翻唱
        if strict { return nil }
        // 其次：仅时长接近（必须足够接近才用，避免张冠李戴）
        if let hit = results.min(by: { abs($0.duration - target) < abs($1.duration - target) }),
           abs(hit.duration - target) < 20 {
            return hit
        }
        // 找不到可靠匹配：宁可播放失败，也不播放错误歌曲
        return nil
    }

    @discardableResult
    private func retryQQOfficialIfNeeded() -> Bool {
        guard let song = currentSong,
              song.source == .qq,
              let mid = song.qqMid,
              !mid.isEmpty else { return false }
        if playbackRecoveryInFlightSongKey == song.identityKey { return true }

        let candidates = ["F000", "M800", "M500", "C400"]
        let attempted = attemptedQQOfficialBRsBySong[song.identityKey] ?? []
        guard let nextBR = candidates.first(where: { !attempted.contains($0) }) else { return false }
        attemptedQQOfficialBRsBySong[song.identityKey, default: []].insert(nextBR)
        playbackRecoveryInFlightSongKey = song.identityKey
        let generation = loadGeneration
        let resume = progress
        BeansLogger.shared.log("QQ 官方地址加载失败，继续切换音质：歌曲=\(song.name)｜BR=\(nextBR)", level: .debug)

        Task {
            let urlString = try? await QQMusicAPI.shared.songURL(
                songmid: mid,
                mediaMid: song.qqMediaMid,
                br: nextBR
            )
            await MainActor.run {
                guard generation == self.loadGeneration,
                      self.currentSong?.identityKey == song.identityKey else {
                    if self.playbackRecoveryInFlightSongKey == song.identityKey {
                        self.playbackRecoveryInFlightSongKey = nil
                    }
                    return
                }
                self.playbackRecoveryInFlightSongKey = nil
                if let urlString, let url = URL(string: urlString) {
                    self.setupPlayer(
                        url: url,
                        resumeAt: resume,
                        qqOfficialBR: nextBR
                    )
                } else if self.retryQQOfficialIfNeeded() {
                    return
                } else {
                    self.loadFailed = true
                    self.isBuffering = false
                    self.isPlaying = false
                    BeansLogger.shared.log("QQ 官方音质均不可播放：\(song.name)", level: .error)
                }
            }
        }
        return true
    }


    private func setupPlayer(
        url: URL,
        resumeAt savedProgress: Double? = nil,
        thirdPartyVIPNotice: ThirdPartyVIPNotice? = nil,
        qqOfficialBR: String? = nil,
        attemptedQQOfficialBRs: [String] = []
    ) {
        guard let loadedSong = currentSong else { return }
        if loadedSong.source == .qq {
            if let qqOfficialBR {
                attemptedQQOfficialBRsBySong[loadedSong.identityKey, default: []].insert(qqOfficialBR)
            }
            attemptedQQOfficialBRsBySong[loadedSong.identityKey, default: []].formUnion(attemptedQQOfficialBRs)
        }
        configureAudioSession()
        UIApplication.shared.beginReceivingRemoteControlEvents()
        removeCurrentObservers()
        pendingThirdPartyVIPNotice = thirdPartyVIPNotice
        // QQ 官方 CDN（isure.stream.qqmusic.qq.com 等）要求 UA/Referer 请求头，
        // 否则裸 GET 会被拒绝（403），导致播放成功却无声、进度条不动。
        let item: AVPlayerItem
        if isQQAudioHost(url.host) {
            var headers = [
                "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:80.0) Gecko/20100101 Firefox/80.0",
                "Referer": "https://y.qq.com/",
                "Origin": "https://y.qq.com",
            ]
            let cookie = QQMusicAuth.shared.cookieHeader
            if !cookie.isEmpty { headers["Cookie"] = cookie }
            let asset = AVURLAsset(url: url, options: [
                "AVURLAssetHTTPHeaderFieldsKey": headers
            ])
            item = AVPlayerItem(asset: asset)
        } else if url.host?.contains("kugou.com") == true || url.host?.contains("kgimg.com") == true {
            var headers = [
                "User-Agent": "Android15-1070-11440-46-0-DiscoveryDRADProtocol-wifi",
                "Referer": "https://www.kugou.com/",
            ]
            let cookie = KugouMusicAuth.shared.cookieHeader
            if !cookie.isEmpty { headers["Cookie"] = cookie }
            let asset = AVURLAsset(url: url, options: [
                "AVURLAssetHTTPHeaderFieldsKey": headers
            ])
            item = AVPlayerItem(asset: asset)
        } else {
            item = AVPlayerItem(url: url)
        }
        let player = AVPlayer(playerItem: item)
        player.rate = Float(rate)
        self.player = player
        playbackConfirmed = false
        itemStatusObserver = item.observe(\.status, options: [.new]) { [weak self] item, _ in
            guard let self, self.player === player, item.status == .failed else { return }
            DispatchQueue.main.async {
                guard self.player === player,
                      self.currentSong?.identityKey == loadedSong.identityKey else { return }
                if self.retryQQOfficialIfNeeded() { return }
                self.loadFailed = true
                self.isBuffering = false
                self.isPlaying = false
                BeansLogger.shared.log("播放地址加载失败：\(item.error?.localizedDescription ?? "未知错误")", level: .error)
            }
        }
        timeControlStatusObserver = player.observe(\.timeControlStatus, options: [.new]) { [weak self] player, _ in
            guard let self, self.player === player else { return }
            guard player.timeControlStatus == .playing, !self.playbackConfirmed else { return }
            self.playbackConfirmed = true
            if let song = self.currentSong {
                BeansLogger.shared.log("▶ 播放成功：\(song.name)｜域名=\(url.host ?? "?")", level: .info)
            }
            self.showPendingThirdPartyVIPNoticeIfNeeded()
        }
        if let savedProgress, savedProgress > 1 {
            let resumeTime = CMTime(seconds: savedProgress, preferredTimescale: 600)
            player.seek(to: resumeTime, toleranceBefore: .zero, toleranceAfter: .zero)
            progress = savedProgress
        }
        player.playImmediately(atRate: Float(rate))
        isPlaying = true
        isBuffering = false
        loadFailed = false
        // 修复：播放次数原先在 loadCurrent 里预计数，URL 加载失败/手动重试也会 +1，
        // 导致统计异常；改为真正开始播放时计数，且同一首歌同一会话只计一次。
        if let song = currentSong, lastCountedSongID != song.identityKey {
            bumpPlayCount(song)
            lastCountedSongID = song.identityKey
        }
        timeObserver = player.addPeriodicTimeObserver(forInterval: CMTime(seconds: 0.2, preferredTimescale: 600), queue: .main) { [weak self] time in
            guard let self, let player = self.player else { return }
            if time.seconds.isFinite {
                if abs(time.seconds - self.lastPublishedProgress) >= 0.18 {
                    self.lastPublishedProgress = time.seconds
                    self.progress = time.seconds
                }
            }
            if let itemDuration = player.currentItem?.duration, itemDuration.isNumeric {
                let seconds = itemDuration.seconds
                if seconds.isFinite, abs(seconds - self.duration) > 0.25 {
                    self.duration = seconds
                }
            }
            if Date().timeIntervalSince(self.lastPlaybackStateSaveAt) > 5 {
                self.persistCurrentPlaybackState()
            }
            let waiting = player.timeControlStatus == .waitingToPlayAtSpecifiedRate
            if waiting != self.isBuffering {
                self.isBuffering = waiting
            }
        }
        endObserver = NotificationCenter.default.addObserver(forName: .AVPlayerItemDidPlayToEndTime, object: item, queue: .main) { [weak self] _ in
            guard let self else { return }
            if self.playMode == .repeatOne {
                self.restartCurrent()
            } else {
                self.advance()
                self.loadCurrent()
                self.persistCurrentPlaybackState()
            }
        }
        failureObserver = NotificationCenter.default.addObserver(forName: .AVPlayerItemFailedToPlayToEndTime, object: item, queue: .main) { [weak self] _ in
            guard let self,
                  self.player?.currentItem === item,
                  self.currentSong?.identityKey == loadedSong.identityKey else { return }
            if self.retryQQOfficialIfNeeded() { return }
            self.loadFailed = true
            self.isBuffering = false
            BeansLogger.shared.log("播放中断：AVPlayerItem 播放失败（解码或网络错误）", level: .error)
        }
        updateNowPlaying()
    }

    private func isQQAudioHost(_ host: String?) -> Bool {
        guard let host = host?.lowercased() else { return false }
        return host.contains("qq.com")
            || host.contains("qqmusic")
            || host.contains("ptqqmusic")
    }

    private func removeCurrentObservers() {
        if let timeObserver {
            player?.removeTimeObserver(timeObserver)
        }
        timeObserver = nil
        if let endObserver {
            NotificationCenter.default.removeObserver(endObserver)
        }
        endObserver = nil
        if let failureObserver {
            NotificationCenter.default.removeObserver(failureObserver)
        }
        failureObserver = nil
        itemStatusObserver = nil
        timeControlStatusObserver = nil
        playbackConfirmed = false
        pendingThirdPartyVIPNotice = nil
        lastPublishedProgress = -1
    }

    private func thirdPartyVIPNotice(for song: Song, sourceTitle: String) -> ThirdPartyVIPNotice? {
        guard song.isVIP else { return nil }
        guard defaults.object(forKey: thirdPartyVIPNoticeKey) as? Bool ?? true else { return nil }
        guard !hasMembership(for: song.source) else { return nil }
        let sourceName = sourceTitle.trimmingCharacters(in: .whitespacesAndNewlines)
        let suffix = sourceName.isEmpty ? "第三方音源" : "第三方音源「\(sourceName)」"
        return ThirdPartyVIPNotice(
            songKey: song.identityKey,
            message: "当前账号未识别到对应会员，《\(song.name)》已通过\(suffix)播放"
        )
    }

    private func showPendingThirdPartyVIPNoticeIfNeeded() {
        guard let notice = pendingThirdPartyVIPNotice else { return }
        guard currentSong?.identityKey == notice.songKey else {
            pendingThirdPartyVIPNotice = nil
            return
        }
        guard defaults.object(forKey: thirdPartyVIPNoticeKey) as? Bool ?? true else {
            pendingThirdPartyVIPNotice = nil
            return
        }
        Task { @MainActor in
            ToastCenter.shared.show(notice.message)
        }
        BeansLogger.shared.log("第三方音源会员歌提醒：\(notice.message)", level: .info)
        pendingThirdPartyVIPNotice = nil
    }

    private func hasMembership(for source: SongSource) -> Bool {
        switch source {
        case .qq:
            return QQMusicAuth.shared.vipBadge != nil
        case .kugou:
            return KugouMusicAuth.shared.vipBadge != nil
        case .netease:
            guard let data = defaults.data(forKey: "beans.user"),
                  let user = try? JSONDecoder().decode(NetEaseUser.self, from: data) else {
                return false
            }
            return user.vipBadge != nil
        }
    }

    private func configureAudioSession() {
        if Self.applyAudioMixPreference(mixesWithOthers) {
            sessionConfigured = true
        } else {
            sessionConfigured = false
        }
    }

    func reactivateAudioSessionIfNeeded() {
        guard currentSong != nil else { return }
        sessionConfigured = false
        configureAudioSession()
        if isPlaying, player?.timeControlStatus != .playing {
            player?.playImmediately(atRate: Float(rate))
        }
    }

    @discardableResult
    static func applyAudioMixPreference(_ mixesWithOthers: Bool) -> Bool {
        do {
            let session = AVAudioSession.sharedInstance()
            // 「与其他音频同时播放」开关：开启时 mixWithOthers，打开其他音频软件也能继续播放；关闭则自动暂停
            if mixesWithOthers {
                try session.setCategory(.playback, mode: .default, options: [.mixWithOthers])
            } else {
                try session.setCategory(.playback, mode: .default)
            }
            try session.setActive(true)
            return true
        } catch {
            BeansLogger.shared.log("音频会话配置失败：\(error.localizedDescription)", level: .error)
            return false
        }
    }

    private func observeRouteChanges() {
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(handleRouteChange(_:)),
            name: AVAudioSession.routeChangeNotification,
            object: AVAudioSession.sharedInstance()
        )
    }

    /// 输出设备变化（插拔耳机 / 切换扬声器 / 来电路由等）后重新激活会话，避免播放无声
    @objc private func handleRouteChange(_ notification: Notification) {
        sessionConfigured = false
        configureAudioSession()
        if isPlaying, player?.timeControlStatus != .playing {
            player?.playImmediately(atRate: Float(rate))
        }
    }

    // MARK: - 来电/中断处理

    private func observeInterruptions() {
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(handleInterruption(_:)),
            name: AVAudioSession.interruptionNotification,
            object: AVAudioSession.sharedInstance()
        )
    }

    @objc private func handleInterruption(_ notification: Notification) {
        guard let info = notification.userInfo,
              let rawType = info[AVAudioSessionInterruptionTypeKey] as? UInt,
              let type = AVAudioSession.InterruptionType(rawValue: rawType) else { return }
        switch type {
        case .began:
            wasPlayingBeforeInterruption = isPlaying
            // 开启「与其他音频同时播放」时，不被其他 App 音频中断，保持继续播放
            guard !mixesWithOthers else { return }
            player?.pause()
            isPlaying = false
        case .ended:
            // 中断结束后系统可能停用了音频会话，重新激活避免无声
            sessionConfigured = false
            configureAudioSession()
            if wasPlayingBeforeInterruption {
                player?.playImmediately(atRate: Float(rate))
                isPlaying = true
            }
        @unknown default:
            break
        }
    }

    // MARK: - 播放历史与统计

    private func pushHistory(_ song: Song) {
        history.removeAll { $0.identityKey == song.identityKey }
        history.insert(song, at: 0)
        if history.count > 50 {
            history = Array(history.prefix(50))
        }
        if let data = try? JSONEncoder().encode(history) {
            defaults.set(data, forKey: historyKey)
        }
        queueHistorySync(song)
    }

    private func loadHistory() {
        guard let data = defaults.data(forKey: historyKey),
              let saved = try? JSONDecoder().decode([Song].self, from: data) else { return }
        history = saved
    }

    private func bumpPlayCount(_ song: Song) {
        playCounts[song.identityKey, default: 0] += 1
        if let data = try? JSONEncoder().encode(playCounts) {
            defaults.set(data, forKey: countsKey)
        }
        queueHistorySync(song)
    }

    private func loadPlayCounts() {
        guard let data = defaults.data(forKey: countsKey),
              let saved = try? JSONDecoder().decode([String: Int].self, from: data) else { return }
        playCounts = saved
    }

    // MARK: - 播放状态恢复

    func persistCurrentPlaybackState() {
        guard !queue.isEmpty else {
            defaults.removeObject(forKey: playbackStateKey)
            return
        }
        let state = PlaybackState(
            queue: queue,
            currentIndex: min(max(currentIndex, 0), queue.count - 1),
            progress: max(0, progress),
            duration: max(duration, currentSong?.duration ?? 0),
            playMode: playMode,
            rate: rate,
            savedAt: Date()
        )
        if let data = try? JSONEncoder().encode(state) {
            defaults.set(data, forKey: playbackStateKey)
            lastPlaybackStateSaveAt = state.savedAt
        }
        if !applyingRemoteSync, Date().timeIntervalSince(lastPlaybackSyncAt) >= 30 {
            lastPlaybackSyncAt = Date()
            let payload = BeansPlaybackSyncPayload(
                queue: state.queue,
                currentIndex: state.currentIndex,
                progress: state.progress,
                duration: state.duration,
                playMode: state.playMode,
                rate: state.rate,
                savedAt: state.savedAt
            )
            Task { @MainActor in
                BeansAccountStore.shared.queueSync(entityType: "playback", entityID: "current", payload: payload)
            }
        }
    }

    private func loadPlaybackState() {
        guard let data = defaults.data(forKey: playbackStateKey),
              let state = try? JSONDecoder().decode(PlaybackState.self, from: data),
              !state.queue.isEmpty else { return }
        queue = state.queue
        currentIndex = min(max(state.currentIndex, 0), state.queue.count - 1)
        progress = max(0, state.progress)
        duration = max(state.duration, queue[currentIndex].duration)
        playMode = state.playMode
        rate = state.rate
        lastPlaybackStateSaveAt = state.savedAt
        buildPlayOrder()
        updateNowPlaying()
    }

    func applyRemoteHistory(_ value: BeansHistorySyncPayload, deleted: Bool) {
        applyingRemoteSync = true
        defer { applyingRemoteSync = false }
        history.removeAll { $0.identityKey == value.song.identityKey }
        if deleted {
            playCounts.removeValue(forKey: value.song.identityKey)
        } else {
            history.insert(value.song, at: 0)
            history = Array(history.prefix(50))
            playCounts[value.song.identityKey] = max(playCounts[value.song.identityKey, default: 0], value.playCount)
        }
        if let data = try? JSONEncoder().encode(history) { defaults.set(data, forKey: historyKey) }
        if let data = try? JSONEncoder().encode(playCounts) { defaults.set(data, forKey: countsKey) }
    }

    func applyRemotePlayback(_ value: BeansPlaybackSyncPayload) {
        guard value.savedAt > lastPlaybackStateSaveAt, !value.queue.isEmpty else { return }
        applyingRemoteSync = true
        queue = value.queue
        currentIndex = min(max(value.currentIndex, 0), value.queue.count - 1)
        progress = max(0, value.progress)
        duration = max(value.duration, value.queue[currentIndex].duration)
        playMode = value.playMode
        rate = value.rate
        lastPlaybackStateSaveAt = value.savedAt
        buildPlayOrder()
        applyingRemoteSync = false
        updateNowPlaying()
    }

    private func queueHistorySync(_ song: Song, deleted: Bool = false) {
        guard !applyingRemoteSync else { return }
        let payload = BeansHistorySyncPayload(song: song, playedAt: Date(), playCount: playCounts[song.identityKey, default: 0])
        let id = "\(song.source.rawValue):\(BeansAccountStore.stableEntitySuffix(song.identityKey))"
        Task { @MainActor in
            BeansAccountStore.shared.queueSync(entityType: "history", entityID: id, payload: payload, deleted: deleted)
        }
    }

    private func resumeRestoredSongIfNeeded() {
        guard currentSong != nil else { return }
        reactivateAudioSessionIfNeeded()
        loadCurrent(resumeAt: progress)
    }

    /// 听歌排行：按播放次数排序的前几首
    var topPlayed: [(song: Song, count: Int)] {
        var result: [(song: Song, count: Int)] = []
        for (key, count) in playCounts {
            if let song = history.first(where: { $0.identityKey == key }) {
                result.append((song, count))
            }
        }
        return result.sorted { $0.count > $1.count }.prefix(8).map { $0 }
    }

    // MARK: - 锁屏/控制中心

    private func updateNowPlaying() {
        guard let song = currentSong else { return }
        var info: [String: Any] = [
            MPMediaItemPropertyTitle: song.name,
            MPMediaItemPropertyArtist: song.artists,
            MPMediaItemPropertyAlbumTitle: song.album,
            MPMediaItemPropertyPlaybackDuration: max(duration, song.duration),
            MPNowPlayingInfoPropertyElapsedPlaybackTime: progress,
            MPNowPlayingInfoPropertyPlaybackRate: isPlaying ? rate : 0.0,
        ]
        if let artworkURL = song.coverURL {
            let artworkKey = song.identityKey + "|" + artworkURL.absoluteString
            if let cached = Self.nowPlayingArtworkCache.object(forKey: artworkURL as NSURL) {
                info[MPMediaItemPropertyArtwork] = MPMediaItemArtwork(boundsSize: cached.size) { _ in cached }
            } else if lastNowPlayingArtworkKey != artworkKey {
                lastNowPlayingArtworkKey = artworkKey
                Task {
                    if let data = try? Data(contentsOf: artworkURL), let image = UIImage(data: data) {
                        Self.nowPlayingArtworkCache.setObject(image, forKey: artworkURL as NSURL)
                        var updated = MPNowPlayingInfoCenter.default().nowPlayingInfo ?? [:]
                        updated[MPMediaItemPropertyArtwork] = MPMediaItemArtwork(boundsSize: image.size) { _ in image }
                        MPNowPlayingInfoCenter.default().nowPlayingInfo = updated
                    }
                }
            }
        } else {
            lastNowPlayingArtworkKey = nil
        }
        MPNowPlayingInfoCenter.default().nowPlayingInfo = info
    }

    private func setupRemoteCommands() {
        let center = MPRemoteCommandCenter.shared()
        center.playCommand.isEnabled = true
        center.pauseCommand.isEnabled = true
        center.nextTrackCommand.isEnabled = true
        center.previousTrackCommand.isEnabled = true
        center.togglePlayPauseCommand.isEnabled = true
        center.changePlaybackPositionCommand.isEnabled = true
        center.playCommand.addTarget { [weak self] _ in
            guard let self else { return .commandFailed }
            if let player = self.player {
                player.playImmediately(atRate: Float(self.rate))
                self.isPlaying = true
                self.updateNowPlaying()
                self.persistCurrentPlaybackState()
            } else {
                self.resumeRestoredSongIfNeeded()
            }
            return .success
        }
        center.pauseCommand.addTarget { [weak self] _ in
            self?.player?.pause()
            self?.isPlaying = false
            self?.updateNowPlaying()
            self?.persistCurrentPlaybackState()
            return .success
        }
        center.nextTrackCommand.addTarget { [weak self] _ in
            self?.next()
            return .success
        }
        center.previousTrackCommand.addTarget { [weak self] _ in
            self?.previous()
            return .success
        }
        center.togglePlayPauseCommand.addTarget { [weak self] _ in
            self?.togglePlayPause()
            return .success
        }
        center.changePlaybackPositionCommand.addTarget { [weak self] event in
            guard let event = event as? MPChangePlaybackPositionCommandEvent else { return .commandFailed }
            self?.seek(to: event.positionTime)
            return .success
        }
    }

    // MARK: - 与其他音频同时播放

    /// 与其他 App 音频混合播放。默认关闭，让系统把 Beans 作为主播放 App 显示到锁屏/灵动岛。
    var mixesWithOthers: Bool {
        get { defaults.object(forKey: audioMixKey) as? Bool ?? false }
        set {
            defaults.set(newValue, forKey: audioMixKey)
            sessionConfigured = false
            configureAudioSession()
        }
    }

}
