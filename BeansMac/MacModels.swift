import AVFoundation
import Foundation

struct MacSong: Identifiable, Codable, Hashable {
    let id: UUID
    var title: String
    var artist: String
    var album: String
    var duration: TimeInterval
    var filePath: String
    var addedAt: Date

    init(id: UUID = UUID(), title: String, artist: String = "未知艺术家", album: String = "本地音乐", duration: TimeInterval, filePath: String, addedAt: Date = .now) {
        self.id = id
        self.title = title
        self.artist = artist.isEmpty ? "未知艺术家" : artist
        self.album = album.isEmpty ? "本地音乐" : album
        self.duration = duration
        self.filePath = filePath
        self.addedAt = addedAt
    }

    var url: URL { URL(fileURLWithPath: filePath) }
    var durationText: String {
        let total = max(0, Int(duration.rounded()))
        return String(format: "%d:%02d", total / 60, total % 60)
    }
}

struct MacPlaylist: Identifiable, Codable, Hashable {
    let id: UUID
    var name: String
    var songIDs: [UUID]
    var createdAt: Date

    init(id: UUID = UUID(), name: String, songIDs: [UUID] = [], createdAt: Date = .now) {
        self.id = id
        self.name = name
        self.songIDs = songIDs
        self.createdAt = createdAt
    }
}

@MainActor
final class MacLibraryStore: ObservableObject {
    @Published private(set) var songs: [MacSong] = [] { didSet { save() } }
    @Published private(set) var playlists: [MacPlaylist] = [] { didSet { save() } }

    private struct LibrarySnapshot: Codable {
        var songs: [MacSong]
        var playlists: [MacPlaylist]
    }

    private let storageURL: URL

    init() {
        let directory = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Beans Music", isDirectory: true)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        storageURL = directory.appendingPathComponent("local-library.json")
        guard let data = try? Data(contentsOf: storageURL),
              let snapshot = try? JSONDecoder().decode(LibrarySnapshot.self, from: data) else { return }
        songs = snapshot.songs.filter { FileManager.default.fileExists(atPath: $0.filePath) }
        playlists = snapshot.playlists
        removeMissingSongsFromPlaylists()
    }

    func importFiles(_ urls: [URL]) {
        for url in urls {
            guard !songs.contains(where: { $0.filePath == url.path }) else { continue }
            guard let song = Self.readSong(url: url) else { continue }
            songs.append(song)
        }
        songs.sort { $0.addedAt > $1.addedAt }
    }

    @discardableResult
    func createPlaylist(named name: String) -> MacPlaylist? {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        let playlist = MacPlaylist(name: trimmed)
        playlists.append(playlist)
        return playlist
    }

    func renamePlaylist(_ id: UUID, to name: String) {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, let index = playlists.firstIndex(where: { $0.id == id }) else { return }
        playlists[index].name = trimmed
    }

    func deletePlaylist(_ id: UUID) {
        playlists.removeAll { $0.id == id }
    }

    func add(_ song: MacSong, to playlistID: UUID) {
        guard let index = playlists.firstIndex(where: { $0.id == playlistID }), !playlists[index].songIDs.contains(song.id) else { return }
        playlists[index].songIDs.append(song.id)
    }

    func remove(_ songID: UUID, from playlistID: UUID) {
        guard let index = playlists.firstIndex(where: { $0.id == playlistID }) else { return }
        playlists[index].songIDs.removeAll { $0 == songID }
    }

    func deleteSong(_ id: UUID) {
        songs.removeAll { $0.id == id }
        removeMissingSongsFromPlaylists()
    }

    func songs(in playlist: MacPlaylist) -> [MacSong] {
        let positions = Dictionary(uniqueKeysWithValues: playlist.songIDs.enumerated().map { ($1, $0) })
        return songs.filter { playlist.songIDs.contains($0.id) }.sorted { positions[$0.id, default: 0] < positions[$1.id, default: 0] }
    }

    private func removeMissingSongsFromPlaylists() {
        let valid = Set(songs.map(\.id))
        for index in playlists.indices {
            playlists[index].songIDs.removeAll { !valid.contains($0) }
        }
    }

    private func save() {
        let snapshot = LibrarySnapshot(songs: songs, playlists: playlists)
        guard let data = try? JSONEncoder().encode(snapshot) else { return }
        try? data.write(to: storageURL, options: .atomic)
    }

    private static func readSong(url: URL) -> MacSong? {
        let asset = AVURLAsset(url: url)
        let metadata = asset.commonMetadata
        let title = metadata.first(where: { $0.commonKey?.rawValue == "title" })?.stringValue ?? url.deletingPathExtension().lastPathComponent
        let artist = metadata.first(where: { $0.commonKey?.rawValue == "artist" })?.stringValue ?? "未知艺术家"
        let album = metadata.first(where: { $0.commonKey?.rawValue == "albumName" })?.stringValue ?? "本地音乐"
        let duration = (try? AVAudioPlayer(contentsOf: url).duration) ?? 0
        return MacSong(title: title, artist: artist, album: album, duration: duration, filePath: url.path)
    }
}
