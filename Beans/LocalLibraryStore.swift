import Foundation

/// 本地歌单（保存在设备本机，不依赖任何平台账号）
struct LocalPlaylist: Identifiable, Codable, Hashable {
    var id = UUID()
    var name: String
    var songs: [Song] = []
    var createdAt = Date()

    enum CodingKeys: String, CodingKey { case id, name, songs, createdAt }

    init(id: UUID = UUID(), name: String, songs: [Song] = [], createdAt: Date = Date()) {
        self.id = id
        self.name = name
        self.songs = songs
        self.createdAt = createdAt
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? "未命名歌单"
        songs = try c.decodeIfPresent([Song].self, forKey: .songs) ?? []
        createdAt = try c.decodeIfPresent(Date.self, forKey: .createdAt) ?? .distantPast
    }
}

/// 本地音乐库：本地歌单的创建 / 删除 / 收藏歌曲，UserDefaults JSON 持久化（覆盖安装不丢失）
final class LocalLibraryStore: ObservableObject {
    static let shared = LocalLibraryStore()

    @Published var playlists: [LocalPlaylist] {
        didSet { save() }
    }

    private let defaults = UserDefaults.standard
    private let key = "beans.localLibrary.playlists"

    private init() {
        if let data = defaults.data(forKey: key),
           let list = try? JSONDecoder().decode([LocalPlaylist].self, from: data) {
            playlists = list
        } else {
            playlists = []
        }
    }

    @discardableResult
    func createPlaylist(name: String) -> LocalPlaylist {
        let playlist = LocalPlaylist(name: name)
        playlists.append(playlist)
        queueMetadata(playlist)
        return playlist
    }

    func deletePlaylist(id: UUID) {
        guard let playlist = playlists.first(where: { $0.id == id }) else { return }
        let metadata = BeansPlaylistMetadata(id: playlist.id, name: playlist.name, createdAt: playlist.createdAt)
        queueSync(entityType: "playlist", entityID: playlist.id.uuidString, payload: metadata, deleted: true)
        for song in playlist.songs {
            let item = BeansPlaylistItemPayload(playlistID: id, song: song)
            queueSync(entityType: "playlistItem", entityID: BeansAccountStore.playlistItemID(id, song.identityKey), payload: item, deleted: true)
        }
        playlists.removeAll { $0.id == id }
    }

    func renamePlaylist(id: UUID, name: String) {
        guard let idx = playlists.firstIndex(where: { $0.id == id }) else { return }
        playlists[idx].name = name
        queueMetadata(playlists[idx])
    }

    /// 添加歌曲到本地歌单（按 identityKey 去重）
    func addSong(_ song: Song, to id: UUID) {
        guard let idx = playlists.firstIndex(where: { $0.id == id }) else { return }
        guard !playlists[idx].songs.contains(where: { $0.identityKey == song.identityKey }) else { return }
        playlists[idx].songs.append(song)
        queueItem(song, playlistID: id)
    }

    /// 批量导入歌曲到本地歌单（按 identityKey 去重），返回实际新增数量。
    @discardableResult
    func addSongs(_ songs: [Song], to id: UUID) -> Int {
        guard let idx = playlists.firstIndex(where: { $0.id == id }) else { return 0 }
        var existing = Set(playlists[idx].songs.map(\.identityKey))
        var added = 0
        for song in songs where !existing.contains(song.identityKey) {
            playlists[idx].songs.append(song)
            queueItem(song, playlistID: id)
            existing.insert(song.identityKey)
            added += 1
        }
        return added
    }

    func removeSong(playlistID: UUID, songIdentity: String) {
        guard let idx = playlists.firstIndex(where: { $0.id == playlistID }) else { return }
        guard let song = playlists[idx].songs.first(where: { $0.identityKey == songIdentity }) else { return }
        playlists[idx].songs.removeAll { $0.identityKey == songIdentity }
        let item = BeansPlaylistItemPayload(playlistID: playlistID, song: song)
        queueSync(entityType: "playlistItem", entityID: BeansAccountStore.playlistItemID(playlistID, songIdentity), payload: item, deleted: true)
    }

    func applyRemote(_ metadata: BeansPlaylistMetadata, deleted: Bool) {
        if deleted {
            playlists.removeAll { $0.id == metadata.id }
        } else if let index = playlists.firstIndex(where: { $0.id == metadata.id }) {
            playlists[index].name = metadata.name
        } else {
            playlists.append(LocalPlaylist(id: metadata.id, name: metadata.name, createdAt: metadata.createdAt))
        }
    }

    func applyRemote(_ item: BeansPlaylistItemPayload, deleted: Bool) {
        if playlists.firstIndex(where: { $0.id == item.playlistID }) == nil {
            playlists.append(LocalPlaylist(id: item.playlistID, name: "同步歌单"))
        }
        guard let index = playlists.firstIndex(where: { $0.id == item.playlistID }) else { return }
        playlists[index].songs.removeAll { $0.identityKey == item.song.identityKey }
        if !deleted { playlists[index].songs.append(item.song) }
    }

    private func save() {
        if let data = try? JSONEncoder().encode(playlists) {
            defaults.set(data, forKey: key)
        }
    }

    private func queueMetadata(_ playlist: LocalPlaylist) {
        let value = BeansPlaylistMetadata(id: playlist.id, name: playlist.name, createdAt: playlist.createdAt)
        queueSync(entityType: "playlist", entityID: playlist.id.uuidString, payload: value)
    }

    private func queueItem(_ song: Song, playlistID: UUID) {
        let value = BeansPlaylistItemPayload(playlistID: playlistID, song: song)
        queueSync(entityType: "playlistItem", entityID: BeansAccountStore.playlistItemID(playlistID, song.identityKey), payload: value)
    }

    private func queueSync<T: Encodable>(entityType: String, entityID: String, payload: T, deleted: Bool = false) {
        Task { @MainActor in
            BeansAccountStore.shared.queueSync(entityType: entityType, entityID: entityID, payload: payload, deleted: deleted)
        }
    }
}
