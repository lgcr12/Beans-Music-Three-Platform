import Foundation
import GRDB
import Combine

struct BeansPlaylistMetadata: Codable {
    let id: UUID
    let name: String
    let createdAt: Date
}

struct BeansPlaylistItemPayload: Codable {
    let playlistID: UUID
    let song: Song
}

struct BeansThemeSyncPayload: Codable {
    let referenceStyle: String
    let accent: String
    let customAccentHex: String?
    let backgroundHex: String
    let backgroundSyncAll: Bool
    let uiStyle: String
    let fontScalePercent: Int?
}

struct BeansPreferenceSyncPayload: Codable {
    let enabledPlatforms: [String]
    let playerEffectMode: String?
    let playerEffectIntensity: Double?
    let lyricStylePreset: String?
    let lyricFontSize: Int?
    let lyricLineSpacing: Int?
    let lyricTranslation: Bool?
    let lyricAlignment: String?
}

struct BeansHistorySyncPayload: Codable {
    let song: Song
    let playedAt: Date
    let playCount: Int
}

struct BeansPlaybackSyncPayload: Codable {
    let queue: [Song]
    let currentIndex: Int
    let progress: Double
    let duration: Double
    let playMode: PlayMode
    let rate: Double
    let savedAt: Date
}

struct BeansPlatformMirrorPayload: Codable {
    let platform: String
    let playlists: [Playlist]
    let updatedAt: Date
}

@MainActor
final class BeansPlatformMirrorStore: ObservableObject {
    static let shared = BeansPlatformMirrorStore()
    @Published private(set) var netease: [Playlist]
    @Published private(set) var qq: [Playlist]
    private let defaults = UserDefaults.standard

    private init() {
        netease = Self.load("netease")
        qq = Self.load("qq")
    }

    func update(platform: String, playlists: [Playlist], sync: Bool = true) {
        if platform == "netease" { netease = playlists } else if platform == "qq" { qq = playlists } else { return }
        if let data = try? JSONEncoder().encode(playlists) { defaults.set(data, forKey: "beans.platformMirror.\(platform)") }
        if sync {
            BeansAccountStore.shared.queueSync(
                entityType: "platformMirror",
                entityID: "\(platform):playlists",
                payload: BeansPlatformMirrorPayload(platform: platform, playlists: playlists, updatedAt: Date())
            )
        }
    }

    func applyRemote(_ payload: BeansPlatformMirrorPayload, deleted: Bool) {
        update(platform: payload.platform, playlists: deleted ? [] : payload.playlists, sync: false)
    }

    private static func load(_ platform: String) -> [Playlist] {
        guard let data = UserDefaults.standard.data(forKey: "beans.platformMirror.\(platform)") else { return [] }
        return (try? JSONDecoder().decode([Playlist].self, from: data)) ?? []
    }
}

struct BeansRemoteSyncChange {
    let entityType: String
    let entityID: String
    let deleted: Bool
    let payload: Data
}

/// 只保存密文的本地同步日志。服务端和 SQLite 都不能从这些记录中读取用户内容。
actor BeansSyncStore {
    static let shared = BeansSyncStore()

    private let database: DatabaseQueue?

    private init() {
        do {
            let support = try FileManager.default.url(
                for: .applicationSupportDirectory,
                in: .userDomainMask,
                appropriateFor: nil,
                create: true
            )
            let directory = support.appendingPathComponent("Beans", isDirectory: true)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let queue = try DatabaseQueue(path: directory.appendingPathComponent("sync.sqlite").path)
            var migrator = DatabaseMigrator()
            migrator.registerMigration("encrypted-sync-v1") { db in
                try db.create(table: "sync_state") { table in
                    table.column("user_id", .text).notNull()
                    table.column("entity_type", .text).notNull()
                    table.column("entity_id", .text).notNull()
                    table.column("revision", .integer).notNull().defaults(to: 0)
                    table.primaryKey(["user_id", "entity_type", "entity_id"])
                }
                try db.create(table: "sync_outbox") { table in
                    table.column("id", .text).primaryKey()
                    table.column("user_id", .text).notNull()
                    table.column("entity_type", .text).notNull()
                    table.column("entity_id", .text).notNull()
                    table.column("base_revision", .integer).notNull()
                    table.column("deleted", .boolean).notNull()
                    table.column("ciphertext", .text).notNull()
                    table.column("updated_at", .datetime).notNull()
                    table.uniqueKey(["user_id", "entity_type", "entity_id"])
                }
                try db.create(table: "sync_account") { table in
                    table.column("user_id", .text).primaryKey()
                    table.column("cursor", .integer).notNull().defaults(to: 0)
                    table.column("seeded", .boolean).notNull().defaults(to: false)
                }
            }
            try migrator.migrate(queue)
            database = queue
        } catch {
            database = nil
            BeansLogger.shared.log("同步数据库初始化失败：\(error.localizedDescription)", level: .error)
        }
    }

    func accountState(userID: UUID) throws -> (cursor: Int64, seeded: Bool) {
        guard let database else { throw SyncError.databaseUnavailable }
        return try database.write { db in
            try ensureAccount(userID, db: db)
            let row = try Row.fetchOne(db, sql: "SELECT cursor, seeded FROM sync_account WHERE user_id = ?", arguments: [userID.uuidString])
            return (row?["cursor"] ?? 0, row?["seeded"] ?? false)
        }
    }

    func markSeeded(userID: UUID) throws {
        guard let database else { throw SyncError.databaseUnavailable }
        try database.write { db in
            try ensureAccount(userID, db: db)
            try db.execute(sql: "UPDATE sync_account SET seeded = 1 WHERE user_id = ?", arguments: [userID.uuidString])
        }
    }

    func enqueue(userID: UUID, entityType: String, entityID: String, deleted: Bool, ciphertext: Data) throws {
        guard let database else { throw SyncError.databaseUnavailable }
        try database.write { db in
            try ensureAccount(userID, db: db)
            let revision = try Int64.fetchOne(
                db,
                sql: "SELECT revision FROM sync_state WHERE user_id = ? AND entity_type = ? AND entity_id = ?",
                arguments: [userID.uuidString, entityType, entityID]
            ) ?? 0
            try db.execute(
                sql: """
                INSERT INTO sync_outbox (id, user_id, entity_type, entity_id, base_revision, deleted, ciphertext, updated_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(user_id, entity_type, entity_id) DO UPDATE SET
                    id = excluded.id,
                    base_revision = excluded.base_revision,
                    deleted = excluded.deleted,
                    ciphertext = excluded.ciphertext,
                    updated_at = excluded.updated_at
                """,
                arguments: [UUID().uuidString, userID.uuidString, entityType, entityID, revision, deleted, ciphertext.base64EncodedString(), Date()]
            )
        }
    }

    func ingest(userID: UUID, page: BeansSyncPage, vaultKey: Data) throws -> [BeansRemoteSyncChange] {
        guard let database else { throw SyncError.databaseUnavailable }
        var changes: [BeansRemoteSyncChange] = []
        try database.write { db in
            try ensureAccount(userID, db: db)
            for record in page.records {
                let known = try Int64.fetchOne(
                    db,
                    sql: "SELECT revision FROM sync_state WHERE user_id = ? AND entity_type = ? AND entity_id = ?",
                    arguments: [userID.uuidString, record.entityType, record.entityId]
                ) ?? 0
                guard record.revision > known else { continue }

                let hasPending = try Bool.fetchOne(
                    db,
                    sql: "SELECT EXISTS(SELECT 1 FROM sync_outbox WHERE user_id = ? AND entity_type = ? AND entity_id = ?)",
                    arguments: [userID.uuidString, record.entityType, record.entityId]
                ) ?? false

                try db.execute(
                    sql: """
                    INSERT INTO sync_state (user_id, entity_type, entity_id, revision) VALUES (?, ?, ?, ?)
                    ON CONFLICT(user_id, entity_type, entity_id) DO UPDATE SET revision = excluded.revision
                    """,
                    arguments: [userID.uuidString, record.entityType, record.entityId, record.revision]
                )
                try db.execute(
                    sql: "UPDATE sync_outbox SET base_revision = ? WHERE user_id = ? AND entity_type = ? AND entity_id = ?",
                    arguments: [record.revision, userID.uuidString, record.entityType, record.entityId]
                )

                if !hasPending,
                   let encrypted = Data(base64Encoded: record.ciphertext) {
                    let payload = try BeansAccountCrypto.decrypt(encrypted, vaultKey: vaultKey)
                    changes.append(BeansRemoteSyncChange(entityType: record.entityType, entityID: record.entityId, deleted: record.deleted, payload: payload))
                }
            }
            try db.execute(
                sql: "UPDATE sync_account SET cursor = MAX(cursor, ?) WHERE user_id = ?",
                arguments: [page.cursor, userID.uuidString]
            )
        }
        return changes
    }

    func pending(userID: UUID, deviceID: UUID, limit: Int = 200) throws -> [BeansSyncEnvelope] {
        guard let database else { throw SyncError.databaseUnavailable }
        return try database.read { db in
            let rows = try Row.fetchAll(
                db,
                sql: """
                SELECT id, entity_type, entity_id, base_revision, deleted, ciphertext, updated_at
                FROM sync_outbox WHERE user_id = ? ORDER BY updated_at LIMIT ?
                """,
                arguments: [userID.uuidString, limit]
            )
            return rows.compactMap { row in
                guard let id = UUID(uuidString: row["id"]) else { return nil }
                return BeansSyncEnvelope(
                    id: id,
                    entityType: row["entity_type"],
                    entityId: row["entity_id"],
                    deviceId: deviceID,
                    baseRevision: row["base_revision"],
                    revision: 0,
                    deleted: row["deleted"],
                    ciphertext: row["ciphertext"],
                    updatedAt: row["updated_at"]
                )
            }
        }
    }

    func acknowledge(userID: UUID, records: [BeansSyncEnvelope]) throws {
        guard let database else { throw SyncError.databaseUnavailable }
        try database.write { db in
            try ensureAccount(userID, db: db)
            for record in records {
                try db.execute(sql: "DELETE FROM sync_outbox WHERE user_id = ? AND id = ?", arguments: [userID.uuidString, record.id.uuidString])
                try db.execute(
                    sql: """
                    INSERT INTO sync_state (user_id, entity_type, entity_id, revision) VALUES (?, ?, ?, ?)
                    ON CONFLICT(user_id, entity_type, entity_id) DO UPDATE SET revision = MAX(revision, excluded.revision)
                    """,
                    arguments: [userID.uuidString, record.entityType, record.entityId, record.revision]
                )
                try db.execute(sql: "UPDATE sync_account SET cursor = MAX(cursor, ?) WHERE user_id = ?", arguments: [record.revision, userID.uuidString])
            }
        }
    }

    private func ensureAccount(_ userID: UUID, db: Database) throws {
        try db.execute(
            sql: "INSERT OR IGNORE INTO sync_account (user_id, cursor, seeded) VALUES (?, 0, 0)",
            arguments: [userID.uuidString]
        )
    }

    enum SyncError: LocalizedError {
        case databaseUnavailable
        var errorDescription: String? { "本地同步数据库不可用" }
    }
}
