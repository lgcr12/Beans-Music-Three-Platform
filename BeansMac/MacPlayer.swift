import AVFoundation
import Foundation

enum MacPlayMode: String, CaseIterable, Identifiable {
    case sequence
    case repeatOne
    case shuffle

    var id: String { rawValue }
    var icon: String {
        switch self {
        case .sequence: return "repeat"
        case .repeatOne: return "repeat.1"
        case .shuffle: return "shuffle"
        }
    }
}

@MainActor
final class MacPlayer: NSObject, ObservableObject, AVAudioPlayerDelegate {
    @Published private(set) var queue: [MacSong] = []
    @Published private(set) var currentIndex = 0
    @Published private(set) var isPlaying = false
    @Published private(set) var currentTime: TimeInterval = 0
    @Published var volume: Double = 0.8 { didSet { audioPlayer?.volume = Float(volume) } }
    @Published var playMode: MacPlayMode = .sequence

    private var audioPlayer: AVAudioPlayer?
    private var timer: Timer?

    var currentSong: MacSong? { queue.indices.contains(currentIndex) ? queue[currentIndex] : nil }
    var duration: TimeInterval { audioPlayer?.duration ?? currentSong?.duration ?? 0 }

    override init() {
        super.init()
        NotificationCenter.default.addObserver(forName: .beansMacTogglePlayback, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.toggle() }
        }
        NotificationCenter.default.addObserver(forName: .beansMacNextSong, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.next() }
        }
        NotificationCenter.default.addObserver(forName: .beansMacPreviousSong, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.previous() }
        }
    }

    deinit { timer?.invalidate() }

    func play(_ songs: [MacSong], startingAt index: Int = 0) {
        guard !songs.isEmpty else { return }
        queue = songs
        currentIndex = min(max(index, 0), songs.count - 1)
        loadCurrentSong(autoplay: true)
    }

    func toggle() {
        guard audioPlayer != nil else {
            guard !queue.isEmpty else { return }
            loadCurrentSong(autoplay: true)
            return
        }
        if isPlaying {
            audioPlayer?.pause()
            isPlaying = false
        } else {
            audioPlayer?.play()
            isPlaying = true
        }
    }

    func next() {
        guard !queue.isEmpty else { return }
        if playMode == .shuffle { currentIndex = Int.random(in: queue.indices) }
        else { currentIndex = (currentIndex + 1) % queue.count }
        loadCurrentSong(autoplay: true)
    }

    func previous() {
        guard !queue.isEmpty else { return }
        currentIndex = (currentIndex - 1 + queue.count) % queue.count
        loadCurrentSong(autoplay: true)
    }

    func seek(to time: TimeInterval) {
        audioPlayer?.currentTime = min(max(0, time), duration)
        currentTime = audioPlayer?.currentTime ?? 0
    }

    func cycleMode() {
        let modes = MacPlayMode.allCases
        guard let index = modes.firstIndex(of: playMode) else { return }
        playMode = modes[(index + 1) % modes.count]
    }

    nonisolated func audioPlayerDidFinishPlaying(_ player: AVAudioPlayer, successfully flag: Bool) {
        Task { @MainActor [weak self] in self?.handlePlaybackFinished() }
    }

    private func handlePlaybackFinished() {
        if playMode == .repeatOne {
            audioPlayer?.currentTime = 0
            audioPlayer?.play()
        } else {
            next()
        }
    }

    private func loadCurrentSong(autoplay: Bool) {
        guard let song = currentSong else { return }
        do {
            let newPlayer = try AVAudioPlayer(contentsOf: song.url)
            newPlayer.delegate = self
            newPlayer.volume = Float(volume)
            newPlayer.prepareToPlay()
            audioPlayer = newPlayer
            currentTime = 0
            if autoplay { newPlayer.play(); isPlaying = true }
            startTimer()
        } catch {
            isPlaying = false
            next()
        }
    }

    private func startTimer() {
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in
                guard let self else { return }
                self.currentTime = self.audioPlayer?.currentTime ?? 0
                self.isPlaying = self.audioPlayer?.isPlaying ?? false
            }
        }
    }
}
