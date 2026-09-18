import Foundation

enum CredentialProbePlatform: String, Codable, CaseIterable, Identifiable {
    case qq
    case netease

    var id: String { rawValue }

    var title: String {
        switch self {
        case .qq: return "QQ 音乐"
        case .netease: return "网易云音乐"
        }
    }
}

enum CredentialProbeMode: String, Codable {
    case manual
    case automatic
}

enum CredentialProbeStatus: String, Codable {
    case notAuthorized
    case neverChecked
    case checking
    case valid
    case limited
    case invalid
    case networkError

    var title: String {
        switch self {
        case .notAuthorized: return "未授权"
        case .neverChecked: return "未检测"
        case .checking: return "检测中"
        case .valid: return "有效"
        case .limited: return "播放权限受限"
        case .invalid: return "已失效"
        case .networkError: return "网络异常"
        }
    }

    var needsAttention: Bool { self == .limited || self == .invalid }
}

struct PlatformCredentialProbeResult: Codable, Equatable, Identifiable {
    let platform: CredentialProbePlatform
    let mode: CredentialProbeMode
    let status: CredentialProbeStatus
    let loginValid: Bool?
    let membership: String?
    let playbackReady: Bool?
    let checkedAt: Date
    let nextCheckAt: Date
    let reasonCode: String?

    var id: String { platform.rawValue }
}

struct CredentialProbeBanner: Identifiable, Equatable {
    let id = UUID()
    let platform: CredentialProbePlatform
    let message: String
}

@MainActor
final class CredentialProbeService: ObservableObject {
    static let shared = CredentialProbeService()

    @Published private(set) var results: [CredentialProbePlatform: PlatformCredentialProbeResult] = [:]
    @Published private(set) var checkingPlatforms = Set<CredentialProbePlatform>()
    @Published var banner: CredentialProbeBanner?
    @Published var automaticEnabled: Bool {
        didSet { defaults.set(automaticEnabled, forKey: Self.automaticEnabledKey) }
    }

    private static let automaticEnabledKey = "beans.credentialProbe.automaticEnabled"
    private static let resultsKey = "beans.credentialProbe.results.v1"
    private static let normalInterval: TimeInterval = 24 * 60 * 60
    private static let retryInterval: TimeInterval = 60 * 60

    private let defaults = UserDefaults.standard
    private var tasks: [CredentialProbePlatform: Task<PlatformCredentialProbeResult, Never>] = [:]

    private init() {
        defaults.register(defaults: [Self.automaticEnabledKey: true])
        automaticEnabled = defaults.bool(forKey: Self.automaticEnabledKey)
        if let data = defaults.data(forKey: Self.resultsKey),
           let saved = try? JSONDecoder().decode([CredentialProbePlatform: PlatformCredentialProbeResult].self, from: data) {
            results = saved
        }
    }

    func result(for platform: CredentialProbePlatform, authorized: Bool) -> PlatformCredentialProbeResult? {
        guard authorized else { return nil }
        return results[platform]
    }

    func status(for platform: CredentialProbePlatform, authorized: Bool) -> CredentialProbeStatus {
        guard authorized else { return .notAuthorized }
        if checkingPlatforms.contains(platform) { return .checking }
        return results[platform]?.status ?? .neverChecked
    }

    func runAll(mode: CredentialProbeMode, auth: AuthStore) async {
        async let qq = probe(.qq, mode: mode, auth: auth)
        async let netease = probe(.netease, mode: mode, auth: auth)
        _ = await (qq, netease)
    }

    func runAutomaticIfDue(auth: AuthStore, now: Date = Date()) async {
        guard automaticEnabled else { return }
        let duePlatforms = CredentialProbePlatform.allCases.filter { platform in
            let authorized = platform == .qq ? QQMusicAuth.shared.isLoggedIn : auth.isLoggedIn
            guard authorized else { return false }
            return results[platform].map { $0.nextCheckAt <= now } ?? true
        }
        for platform in duePlatforms {
            _ = await probe(platform, mode: .automatic, auth: auth)
        }
    }

    @discardableResult
    func probe(
        _ platform: CredentialProbePlatform,
        mode: CredentialProbeMode,
        auth: AuthStore
    ) async -> PlatformCredentialProbeResult {
        if let task = tasks[platform] { return await task.value }

        checkingPlatforms.insert(platform)
        let task = Task { @MainActor [weak self] in
            guard let self else {
                return Self.notAuthorizedResult(platform: platform, mode: mode)
            }
            switch platform {
            case .qq: return await self.probeQQ(mode: mode)
            case .netease: return await self.probeNetEase(mode: mode, auth: auth)
            }
        }
        tasks[platform] = task
        let previous = results[platform]
        let result = await task.value
        tasks[platform] = nil
        checkingPlatforms.remove(platform)
        results[platform] = result
        persist()

        if mode == .automatic,
           previous?.status == .valid,
           result.status.needsAttention {
            banner = CredentialProbeBanner(
                platform: platform,
                message: "\(platform.title)\(result.status == .invalid ? "授权已失效" : "播放权限需要重新授权")"
            )
        }
        BeansLogger.shared.log(
            "凭证探针：平台=\(platform.rawValue) 模式=\(mode.rawValue) 状态=\(result.status.rawValue) 播放权限=\(result.playbackReady.map { $0 ? "可用" : "受限" } ?? "未检测") 原因=\(result.reasonCode ?? "none")",
            level: result.status == .networkError ? .debug : .info
        )
        return result
    }

    func dismissBanner() { banner = nil }

    func clear(platform: CredentialProbePlatform) {
        results.removeValue(forKey: platform)
        persist()
    }

    private func probeQQ(mode: CredentialProbeMode) async -> PlatformCredentialProbeResult {
        let auth = QQMusicAuth.shared
        guard auth.isLoggedIn else { return Self.notAuthorizedResult(platform: .qq, mode: mode) }
        let now = Date()
        do {
            guard try await auth.validateRestoredCredential() else {
                return makeResult(.qq, mode, .invalid, auth.vipBadge, false, now, "profile_rejected")
            }
            await auth.fetchVIPStatus()
            guard auth.hasPlaybackCredential else {
                return makeResult(.qq, mode, .limited, auth.vipBadge, false, now, "missing_playback_credential")
            }
            guard let sample = await qqProbeSong() else {
                return makeResult(.qq, mode, .valid, auth.vipBadge, nil, now, "sample_unavailable")
            }
            let resolution = try await QQMusicAPI.shared.resolveSongURL(
                songmid: sample.qqMid ?? "",
                mediaMid: sample.qqMediaMid,
                quality: .exhigh
            )
            if let result = resolution.result {
                return makeResult(.qq, mode, .valid, auth.vipBadge, true, now, "playable_\(result.br.lowercased())")
            }
            switch resolution.failure {
            case .missingPlaybackCredential:
                return makeResult(.qq, mode, .limited, auth.vipBadge, false, now, "missing_playback_credential")
            case .serviceRejected(let code):
                return makeResult(.qq, mode, .limited, auth.vipBadge, false, now, code.map { "vkey_\($0)" } ?? "vkey_rejected")
            case .cdnRejected(let statusCode):
                let status: CredentialProbeStatus = statusCode == nil ? .networkError : .limited
                return makeResult(.qq, mode, status, auth.vipBadge, status == .limited ? false : nil, now, statusCode.map { "cdn_\($0)" } ?? "cdn_network_error")
            case .invalidResponse, .none:
                return makeResult(.qq, mode, .networkError, auth.vipBadge, nil, now, "invalid_response")
            }
        } catch {
            return makeResult(.qq, mode, .networkError, auth.vipBadge, nil, now, "request_failed")
        }
    }

    private func qqProbeSong() async -> Song? {
        if let recent = PlayerManager.shared.history.first(where: {
            $0.source == .qq && $0.isVIP && $0.qqMid?.isEmpty == false
        }) {
            return recent
        }
        guard let songs = try? await QQMusicAPI.shared.searchSongs(keyword: "周杰伦 晴天", limit: 8) else { return nil }
        return songs.first(where: {
            $0.name == "晴天" && ($0.artists.contains("周杰伦") || $0.artists.lowercased().contains("jay chou")) && $0.qqMid?.isEmpty == false
        })
    }

    private func probeNetEase(mode: CredentialProbeMode, auth: AuthStore) async -> PlatformCredentialProbeResult {
        guard auth.isLoggedIn else { return Self.notAuthorizedResult(platform: .netease, mode: mode) }
        let now = Date()
        do {
            let account = try await NetEaseAPI.shared.account()
            await auth.refreshAccount()
            return makeResult(.netease, mode, .valid, account.vipBadge, nil, now, account.vipBadge == nil ? "account_valid" : "membership_valid")
        } catch let error as NetEaseError {
            if error.isAuthorizationFailure {
                return makeResult(.netease, mode, .invalid, nil, false, now, "unauthorized")
            }
            return makeResult(.netease, mode, .networkError, auth.user?.vipBadge, nil, now, "request_failed")
        } catch {
            return makeResult(.netease, mode, .networkError, auth.user?.vipBadge, nil, now, "request_failed")
        }
    }

    private func makeResult(
        _ platform: CredentialProbePlatform,
        _ mode: CredentialProbeMode,
        _ status: CredentialProbeStatus,
        _ membership: String?,
        _ playbackReady: Bool?,
        _ now: Date,
        _ reason: String?
    ) -> PlatformCredentialProbeResult {
        let interval = status == .networkError ? Self.retryInterval : Self.normalInterval
        return PlatformCredentialProbeResult(
            platform: platform,
            mode: mode,
            status: status,
            loginValid: status == .networkError ? nil : (status == .valid || status == .limited),
            membership: membership,
            playbackReady: playbackReady,
            checkedAt: now,
            nextCheckAt: now.addingTimeInterval(interval),
            reasonCode: reason
        )
    }

    private static func notAuthorizedResult(
        platform: CredentialProbePlatform,
        mode: CredentialProbeMode
    ) -> PlatformCredentialProbeResult {
        let now = Date()
        return PlatformCredentialProbeResult(
            platform: platform,
            mode: mode,
            status: .notAuthorized,
            loginValid: false,
            membership: nil,
            playbackReady: nil,
            checkedAt: now,
            nextCheckAt: now.addingTimeInterval(normalInterval),
            reasonCode: "not_authorized"
        )
    }

    private func persist() {
        if let data = try? JSONEncoder().encode(results) {
            defaults.set(data, forKey: Self.resultsKey)
        }
    }
}
