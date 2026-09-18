import CryptoKit
import Foundation
import SwiftUI
import UIKit

@MainActor
final class BeansAccountStore: ObservableObject {
    static let shared = BeansAccountStore()
    typealias Account = BeansAccountRecord

    enum AuthState: Equatable {
        case signedOut
        case signingIn
        case awaitingVerification(UUID)
        case signedIn
        case failed(String)
    }

    enum LinkedPlatform: String, CaseIterable, Codable, Identifiable {
        case qq
        case netease
        var id: String { rawValue }
        var title: String { self == .qq ? "QQ 音乐" : "网易云音乐" }
        var icon: String { self == .qq ? "q.circle.fill" : "cloud.fill" }
    }

    @Published private(set) var account: Account?
    @Published private(set) var authState: AuthState = .signedOut
    @Published private(set) var linkedPlatforms: Set<LinkedPlatform> = []
    @Published private(set) var lastSyncAt: Date?
    @Published private(set) var devices: [BeansDeviceRecord] = []
    @Published private(set) var developmentVerificationCode: String?
    @Published private(set) var isSyncing = false
    @Published private(set) var syncError: String?
    @Published var serverURL: String

    private let defaults = UserDefaults.standard
    private let api = BeansAPIClient.shared
    private let syncStore = BeansSyncStore.shared
    private let accountKey = "beans.account.profile.v2"
    private let syncKey = "beans.account.lastSync"
    private let serverKey = "beans.account.serverURL"
    private let deviceKey = "beans.account.deviceID"
    private var pendingVaultKey: Data?
    private var vaultKey: Data?
    private var vaultVersion: Int64 = 0

    private init() {
        serverURL = defaults.string(forKey: serverKey)
            ?? (Bundle.main.object(forInfoDictionaryKey: "BeansServerURL") as? String)
            ?? Self.defaultServerURL
        if let data = defaults.data(forKey: accountKey), let saved = try? JSONDecoder().decode(Account.self, from: data) {
            account = saved
        }
        vaultKey = BeansSecureStore.shared.data(for: BeansSecureKey.vaultKey)
        lastSyncAt = defaults.object(forKey: syncKey) as? Date
        authState = account != nil && vaultKey != nil ? .signedIn : .signedOut
        Task {
            try? await api.configure(serverURL: serverURL)
            if isSignedIn { await restoreVaultAndSync() }
        }
    }

    var isSignedIn: Bool { account != nil && authState == .signedIn && vaultKey != nil }
    var isLocalPreviewMode: Bool { false }
    var currentDeviceID: UUID { deviceInput.id }
    var needsServerConfiguration: Bool {
        let trimmed = serverURL.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let host = URL(string: trimmed)?.host?.lowercased() else { return true }
#if os(iOS) && !targetEnvironment(simulator) && !targetEnvironment(macCatalyst)
        return host == "localhost" || host == "127.0.0.1" || host == "::1"
#else
        return false
#endif
    }

    func register(nickname: String, email: String, password: String) async throws {
        try validate(nickname: nickname, email: email, password: password)
        try await configureAPI()
        authState = .signingIn
        do {
            let profile = BeansCryptoProfile(salt: BeansAccountCrypto.randomData(count: 16))
            let keys = try await BeansAccountCrypto.derive(password: password, profile: profile)
            let newVaultKey = BeansAccountCrypto.randomData(count: 32)
            let wrapped = try BeansAccountCrypto.wrapVaultKey(newVaultKey, using: keys.vaultWrappingKey)
            let pending = try await api.startRegistration(
                nickname: nickname.trimmingCharacters(in: .whitespacesAndNewlines),
                email: normalizedEmail(email), authSecret: keys.authSecret, profile: profile,
                wrappedVaultKey: wrapped, device: deviceInput)
            pendingVaultKey = newVaultKey
            developmentVerificationCode = pending.developmentCode
            authState = .awaitingVerification(pending.registrationId)
        } catch {
            authState = .failed(error.localizedDescription)
            throw error
        }
    }

    func verifyEmail(code: String) async throws {
        guard case .awaitingVerification(let registrationID) = authState, let pendingVaultKey else {
            throw AccountError.noPendingVerification
        }
        do {
            let session = try await api.verifyEmail(registrationID: registrationID, code: code)
            await completeLogin(account: session.account, tokens: session.tokenPair, vaultKey: pendingVaultKey)
            self.pendingVaultKey = nil
            developmentVerificationCode = nil
            try await uploadPlatformVault()
        } catch {
            authState = .failed(error.localizedDescription)
            throw error
        }
    }

    func login(email: String, password: String) async throws {
        guard !email.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, password.count >= 8 else {
            throw AccountError.invalidCredentials
        }
        try await configureAPI()
        authState = .signingIn
        do {
            let challenge = try await api.loginChallenge(email: normalizedEmail(email))
            let keys = try await BeansAccountCrypto.derive(password: password, profile: challenge.cryptoProfile)
            let session = try await api.login(email: normalizedEmail(email), authSecret: keys.authSecret, device: deviceInput)
            guard let wrapped = Data(base64Encoded: session.wrappedVaultKey) else { throw BeansCryptoError.invalidVaultKey }
            let unlocked = try BeansAccountCrypto.unwrapVaultKey(wrapped, using: keys.vaultWrappingKey)
            await completeLogin(account: session.account, tokens: session.tokenPair, vaultKey: unlocked)
            await restoreVaultAndSync()
        } catch {
            authState = .failed(error.localizedDescription)
            throw error
        }
    }

    func logout() {
        Task { await api.logout(); await api.clearTokens() }
        account = nil
        authState = .signedOut
        linkedPlatforms = []
        devices = []
        lastSyncAt = nil
        vaultKey = nil
        vaultVersion = 0
        defaults.removeObject(forKey: accountKey)
        defaults.removeObject(forKey: syncKey)
        BeansSecureStore.shared.remove(BeansSecureKey.vaultKey)
    }

    func reconcilePlatformLinks(qqLoggedIn: Bool, neteaseLoggedIn: Bool) {
        var next = Set<LinkedPlatform>()
        if qqLoggedIn { next.insert(.qq) }
        if neteaseLoggedIn { next.insert(.netease) }
        guard next != linkedPlatforms else { return }
        linkedPlatforms = next
        if isSignedIn { Task { try? await uploadPlatformVault() } }
    }

    func markSynced() {
        guard isSignedIn else { return }
        Task {
            guard !isSyncing else { return }
            isSyncing = true
            defer { isSyncing = false }
            do {
                try await uploadPlatformVault()
                try await synchronizeContent()
                syncError = nil
            } catch {
                syncError = error.localizedDescription
            }
        }
    }

    func queueSync<T: Encodable>(entityType: String, entityID: String, payload: T, deleted: Bool = false) {
        guard isSignedIn, let account, let vaultKey else { return }
        Task {
            do {
                try await enqueue(account: account, vaultKey: vaultKey, entityType: entityType, entityID: entityID, payload: payload, deleted: deleted)
                try await synchronizeContentIfIdle()
            } catch {
                syncError = error.localizedDescription
            }
        }
    }

    func updateServerURL(_ value: String) {
        serverURL = value.trimmingCharacters(in: .whitespacesAndNewlines)
        defaults.set(serverURL, forKey: serverKey)
        Task { try? await api.configure(serverURL: serverURL) }
    }

    func makeQRLoginContext() async throws -> BeansQRLoginContext {
        try await configureAPI()
        let pair = BeansAccountCrypto.makeQRKeyPair()
        let exchangeSecret = BeansAccountCrypto.randomData(count: 32)
        let session = try await api.createQRSession(device: deviceInput, publicKey: pair.publicKey, exchangeSecret: exchangeSecret)
        return BeansQRLoginContext(session: session, payload: "beans://account/qr?session=\(session.id.uuidString)", exchangeSecret: exchangeSecret, privateKey: pair.privateKey)
    }

    func pollQRLogin(_ context: BeansQRLoginContext) async throws {
        while Date() < context.session.expiresAt {
            try Task.checkCancellation()
            let status = try await api.getQRSession(context.session.id)
            if status.status == "expired" { throw AccountError.qrExpired }
            if status.status == "approved" {
                let exchange = try await api.exchangeQRSession(context, device: deviceInput)
                guard let envelope = Data(base64Encoded: exchange.encryptedVaultKey) else { throw BeansCryptoError.invalidVaultKey }
                let unlocked = try BeansAccountCrypto.decryptVaultKeyFromQR(envelope, targetPrivateKey: context.privateKey)
                await completeLogin(account: exchange.account, tokens: exchange.tokenPair, vaultKey: unlocked)
                await restoreVaultAndSync()
                return
            }
            try await Task.sleep(nanoseconds: 2_000_000_000)
        }
        throw AccountError.qrExpired
    }

    func qrSession(from payload: String) async throws -> BeansQRSession {
        guard let components = URLComponents(string: payload), components.scheme == "beans",
              let raw = components.queryItems?.first(where: { $0.name == "session" })?.value,
              let id = UUID(uuidString: raw) else { throw AccountError.invalidQR }
        return try await api.getQRSession(id)
    }

    func approveQRSession(_ session: BeansQRSession, verificationCode: String) async throws {
        guard isSignedIn, let vaultKey, let targetKey = Data(base64Encoded: session.ephemeralPublicKey) else { throw AccountError.notSignedIn }
        guard session.verificationCode == verificationCode else { throw AccountError.invalidVerificationCode }
        let envelope = try BeansAccountCrypto.encryptVaultKeyForQR(vaultKey, targetPublicKey: targetKey)
        try await api.approveQRSession(session.id, code: verificationCode, encryptedVaultKey: envelope)
    }

    func refreshDevices() async {
        guard isSignedIn else { return }
        devices = (try? await api.listDevices()) ?? []
    }

    func revokeDevice(_ id: UUID) async throws {
        try await api.revokeDevice(id)
        if id == currentDeviceID { logout() }
        else { await refreshDevices() }
    }

    func startPasswordReset(email: String) async throws -> String? {
        try await configureAPI()
        return try await api.startPasswordReset(email: normalizedEmail(email))
    }

    func completePasswordReset(email: String, code: String, newPassword: String) async throws {
        guard newPassword.count >= 8 else { throw AccountError.passwordTooShort }
        try await configureAPI()
        let profile = BeansCryptoProfile(salt: BeansAccountCrypto.randomData(count: 16))
        let keys = try await BeansAccountCrypto.derive(password: newPassword, profile: profile)
        let newVaultKey = BeansAccountCrypto.randomData(count: 32)
        let wrapped = try BeansAccountCrypto.wrapVaultKey(newVaultKey, using: keys.vaultWrappingKey)
        try await api.completePasswordReset(email: normalizedEmail(email), code: code, authSecret: keys.authSecret, profile: profile, wrappedVaultKey: wrapped)
        logout()
    }

    private func configureAPI() async throws { try await api.configure(serverURL: serverURL) }

    private static var defaultServerURL: String {
#if targetEnvironment(simulator) || targetEnvironment(macCatalyst)
        return "http://localhost:8080"
#else
        // 真机上的 localhost 指向 iPhone 本身，必须由用户配置可访问的后端地址。
        return ""
#endif
    }

    private func completeLogin(account: Account, tokens: BeansTokenPair, vaultKey: Data) async {
        self.account = account
        self.vaultKey = vaultKey
        authState = .signedIn
        await api.restoreTokens(tokens)
        if let data = try? JSONEncoder().encode(account) { defaults.set(data, forKey: accountKey) }
        _ = BeansSecureStore.shared.set(vaultKey, for: BeansSecureKey.vaultKey)
        await refreshDevices()
    }

    private func uploadPlatformVault() async throws {
        guard let vaultKey else { throw AccountError.notSignedIn }
        let netease = NetEaseAPI.shared.credentialSnapshot()
        let bundle = BeansPlatformCredentialBundle(
            qq: QQMusicAuth.shared.isLoggedIn ? QQMusicAuth.shared.credentialSnapshot() : nil,
            netease: netease.isEmpty ? nil : netease,
            updatedAt: Date())
        let encrypted = try BeansAccountCrypto.encrypt(JSONEncoder().encode(bundle), vaultKey: vaultKey)
        let nextVersion = max(1, vaultVersion + 1)
        try await api.putVault(BeansVaultEnvelope(version: nextVersion, ciphertext: encrypted.base64EncodedString(), updatedAt: Date()))
        vaultVersion = nextVersion
        recordSync()
    }

    private func restoreVaultAndSync() async {
        guard let vaultKey else { return }
        isSyncing = true
        defer { isSyncing = false }
        do {
            let envelope = try await api.getVault()
            guard let ciphertext = Data(base64Encoded: envelope.ciphertext) else { throw BeansCryptoError.encryptionFailed }
            let bundle = try JSONDecoder().decode(BeansPlatformCredentialBundle.self, from: BeansAccountCrypto.decrypt(ciphertext, vaultKey: vaultKey))
            var warnings: [String] = []
            if let qq = bundle.qq {
                QQMusicAuth.shared.restoreCredentialSnapshot(qq)
                do {
                    if !((try? await QQMusicAuth.shared.validateRestoredCredential()) ?? true) {
                        QQMusicAuth.shared.logout()
                        warnings.append("QQ 音乐需要重新授权")
                    }
                }
            }
            if let netease = bundle.netease {
                NetEaseAPI.shared.restoreCredentialSnapshot(netease)
                do { _ = try await NetEaseAPI.shared.account() }
                catch let error as NetEaseError where error.isAuthorizationFailure {
                    NetEaseAPI.shared.clearCookies()
                    warnings.append("网易云音乐需要重新授权")
                }
                catch { warnings.append("网易云音乐授权已恢复，当前网络下暂未验证") }
            }
            vaultVersion = envelope.version
            var restored = Set<LinkedPlatform>()
            if QQMusicAuth.shared.isLoggedIn { restored.insert(.qq) }
            if !NetEaseAPI.shared.credentialSnapshot().isEmpty { restored.insert(.netease) }
            linkedPlatforms = restored
            try await synchronizeContent()
            syncError = warnings.isEmpty ? nil : warnings.joined(separator: "；")
            recordSync()
            await refreshDevices()
        } catch let BeansAPIError.server(status, _) where status == 404 {
            do {
                try await uploadPlatformVault()
                try await synchronizeContent()
            } catch { syncError = error.localizedDescription }
        } catch { syncError = error.localizedDescription }
    }

    private func synchronizeContentIfIdle() async throws {
        guard !isSyncing else { return }
        isSyncing = true
        defer { isSyncing = false }
        try await synchronizeContent()
        syncError = nil
    }

    private func synchronizeContent() async throws {
        guard let account, let vaultKey else { throw AccountError.notSignedIn }
        let initial = try await syncStore.accountState(userID: account.id)
        let receivedRemote = try await pullRemoteChanges(account: account, vaultKey: vaultKey)

        if !initial.seeded {
            if initial.cursor == 0 && !receivedRemote {
                try await seedCurrentDevice(account: account, vaultKey: vaultKey)
            }
            try await syncStore.markSeeded(userID: account.id)
        }

        var conflictRetries = 0
        while true {
            let pending = try await syncStore.pending(userID: account.id, deviceID: deviceInput.id)
            guard !pending.isEmpty else { break }
            do {
                for record in pending {
                    let accepted = try await api.pushSync([record])
                    try await syncStore.acknowledge(userID: account.id, records: accepted)
                }
            } catch let BeansAPIError.server(status, _) where status == 409 && conflictRetries < 2 {
                conflictRetries += 1
                _ = try await pullRemoteChanges(account: account, vaultKey: vaultKey)
                continue
            }
        }
        recordSync()
    }

    @discardableResult
    private func pullRemoteChanges(account: Account, vaultKey: Data) async throws -> Bool {
        var received = false
        while true {
            let state = try await syncStore.accountState(userID: account.id)
            let page = try await api.pullSync(cursor: state.cursor)
            received = received || !page.records.isEmpty
            let changes = try await syncStore.ingest(userID: account.id, page: page, vaultKey: vaultKey)
            for change in changes { applyRemote(change) }
            if !page.hasMore { return received }
        }
    }

    private func seedCurrentDevice(account: Account, vaultKey: Data) async throws {
        for playlist in LocalLibraryStore.shared.playlists {
            let metadata = BeansPlaylistMetadata(id: playlist.id, name: playlist.name, createdAt: playlist.createdAt)
            try await enqueue(account: account, vaultKey: vaultKey, entityType: "playlist", entityID: playlist.id.uuidString, payload: metadata)
            for song in playlist.songs {
                let item = BeansPlaylistItemPayload(playlistID: playlist.id, song: song)
                try await enqueue(account: account, vaultKey: vaultKey, entityType: "playlistItem", entityID: playlistItemID(playlist.id, song.identityKey), payload: item)
            }
        }
        for song in FavoritesStore.shared.qqFavoriteSongs + FavoritesStore.shared.neteaseFavoriteSongs {
            try await enqueue(account: account, vaultKey: vaultKey, entityType: "favorite", entityID: favoriteID(song), payload: song)
        }
        for song in PlayerManager.shared.history {
            let payload = BeansHistorySyncPayload(
                song: song,
                playedAt: Date(),
                playCount: PlayerManager.shared.playCounts[song.identityKey, default: 0]
            )
            try await enqueue(account: account, vaultKey: vaultKey, entityType: "history", entityID: favoriteID(song), payload: payload)
        }
        if !PlayerManager.shared.queue.isEmpty {
            let player = PlayerManager.shared
            let payload = BeansPlaybackSyncPayload(
                queue: player.queue,
                currentIndex: player.currentIndex,
                progress: player.progress,
                duration: player.duration,
                playMode: player.playMode,
                rate: player.rate,
                savedAt: Date()
            )
            try await enqueue(account: account, vaultKey: vaultKey, entityType: "playback", entityID: "current", payload: payload)
        }
        let mirrors = BeansPlatformMirrorStore.shared
        if !mirrors.netease.isEmpty {
            try await enqueue(account: account, vaultKey: vaultKey, entityType: "platformMirror", entityID: "netease:playlists", payload: BeansPlatformMirrorPayload(platform: "netease", playlists: mirrors.netease, updatedAt: Date()))
        }
        if !mirrors.qq.isEmpty {
            try await enqueue(account: account, vaultKey: vaultKey, entityType: "platformMirror", entityID: "qq:playlists", payload: BeansPlatformMirrorPayload(platform: "qq", playlists: mirrors.qq, updatedAt: Date()))
        }
        try await enqueue(account: account, vaultKey: vaultKey, entityType: "theme", entityID: "current", payload: ThemeStore.shared.syncPayload)
        try await enqueue(account: account, vaultKey: vaultKey, entityType: "preference", entityID: "platforms", payload: PlatformPreferenceStore.shared.syncPayload)
    }

    private func enqueue<T: Encodable>(account: Account, vaultKey: Data, entityType: String, entityID: String, payload: T, deleted: Bool = false) async throws {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        let encrypted = try BeansAccountCrypto.encrypt(encoder.encode(payload), vaultKey: vaultKey)
        try await syncStore.enqueue(userID: account.id, entityType: entityType, entityID: entityID, deleted: deleted, ciphertext: encrypted)
    }

    private func applyRemote(_ change: BeansRemoteSyncChange) {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        switch change.entityType {
        case "playlist":
            guard let value = try? decoder.decode(BeansPlaylistMetadata.self, from: change.payload) else { return }
            LocalLibraryStore.shared.applyRemote(value, deleted: change.deleted)
        case "playlistItem":
            guard let value = try? decoder.decode(BeansPlaylistItemPayload.self, from: change.payload) else { return }
            LocalLibraryStore.shared.applyRemote(value, deleted: change.deleted)
        case "favorite":
            guard let song = try? decoder.decode(Song.self, from: change.payload) else { return }
            FavoritesStore.shared.applyRemote(song, deleted: change.deleted)
        case "theme":
            guard !change.deleted, let value = try? decoder.decode(BeansThemeSyncPayload.self, from: change.payload) else { return }
            ThemeStore.shared.applyRemote(value)
        case "preference":
            guard !change.deleted, let value = try? decoder.decode(BeansPreferenceSyncPayload.self, from: change.payload) else { return }
            PlatformPreferenceStore.shared.applyRemote(value)
        case "history":
            guard let value = try? decoder.decode(BeansHistorySyncPayload.self, from: change.payload) else { return }
            PlayerManager.shared.applyRemoteHistory(value, deleted: change.deleted)
        case "playback":
            guard !change.deleted, let value = try? decoder.decode(BeansPlaybackSyncPayload.self, from: change.payload) else { return }
            PlayerManager.shared.applyRemotePlayback(value)
        case "platformMirror":
            guard let value = try? decoder.decode(BeansPlatformMirrorPayload.self, from: change.payload) else { return }
            BeansPlatformMirrorStore.shared.applyRemote(value, deleted: change.deleted)
        default:
            break
        }
    }

    nonisolated static func stableEntitySuffix(_ value: String) -> String {
        SHA256.hash(data: Data(value.utf8)).prefix(12).map { String(format: "%02x", $0) }.joined()
    }

    nonisolated static func playlistItemID(_ playlistID: UUID, _ songIdentity: String) -> String {
        "\(playlistID.uuidString):\(stableEntitySuffix(songIdentity))"
    }

    nonisolated static func favoriteID(_ song: Song) -> String {
        "\(song.source.rawValue):\(stableEntitySuffix(song.identityKey))"
    }

    private func playlistItemID(_ playlistID: UUID, _ songIdentity: String) -> String { Self.playlistItemID(playlistID, songIdentity) }
    private func favoriteID(_ song: Song) -> String { Self.favoriteID(song) }

    private func recordSync() {
        lastSyncAt = Date()
        defaults.set(lastSyncAt, forKey: syncKey)
    }

    private var deviceInput: BeansDeviceInput {
        let id: UUID
        if let raw = defaults.string(forKey: deviceKey), let saved = UUID(uuidString: raw) { id = saved }
        else { id = UUID(); defaults.set(id.uuidString, forKey: deviceKey) }
        #if targetEnvironment(macCatalyst)
        let platform = "macos"
        #else
        let platform = "ios"
        #endif
        return BeansDeviceInput(id: id, name: UIDevice.current.name, platform: platform)
    }

    private func normalizedEmail(_ value: String) -> String { value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() }

    private func validate(nickname: String, email: String, password: String) throws {
        guard nickname.trimmingCharacters(in: .whitespacesAndNewlines).count >= 2 else { throw AccountError.invalidNickname }
        guard email.contains("@"), email.contains(".") else { throw AccountError.invalidEmail }
        guard password.count >= 8 else { throw AccountError.passwordTooShort }
    }

    enum AccountError: LocalizedError {
        case invalidNickname, invalidEmail, passwordTooShort, invalidCredentials, noPendingVerification
        case qrExpired, invalidQR, invalidVerificationCode, notSignedIn
        var errorDescription: String? {
            switch self {
            case .invalidNickname: return "昵称至少需要 2 个字符"
            case .invalidEmail: return "请输入有效的邮箱地址"
            case .passwordTooShort: return "密码至少需要 8 个字符"
            case .invalidCredentials: return "请输入邮箱和不少于 8 位的密码"
            case .noPendingVerification: return "请先提交注册信息"
            case .qrExpired: return "登录二维码已过期，请刷新"
            case .invalidQR: return "这不是有效的 Beans 登录二维码"
            case .invalidVerificationCode: return "设备校验码不一致"
            case .notSignedIn: return "请先登录 Beans 账号"
            }
        }
    }
}

struct BeansAccountSheet: View {
    @ObservedObject private var store = BeansAccountStore.shared
    @EnvironmentObject private var theme: ThemeStore
    @EnvironmentObject private var auth: AuthStore
    @Environment(\.dismiss) private var dismiss
    @State private var mode: Mode = .login
    @State private var nickname = ""
    @State private var email = ""
    @State private var password = ""
    @State private var verificationCode = ""
    @State private var message = ""
    @State private var showQR = false
    @State private var showApprove = false
    @State private var showPasswordReset = false
    @State private var serverURL = BeansAccountStore.shared.serverURL
    @State private var showServerField = BeansAccountStore.shared.needsServerConfiguration

    private enum Mode: String, CaseIterable { case login = "登录", register = "注册" }

    var body: some View {
        BeansNavigationStack {
            ZStack {
                GlassBackdrop(customColor: theme.backgroundSyncAll ? theme.customBackground : nil)
                ScrollView {
                    VStack(alignment: .leading, spacing: 18) {
                        accountHeader
                        if let account = store.account, store.isSignedIn { signedInView(account) }
                        else if case .awaitingVerification = store.authState { verificationView }
                        else { credentialForm }
                        serverField
                        if !message.isEmpty { Text(message).font(BeansFont.appFont(12)).foregroundStyle(Color.beansComment) }
                    }.padding(20)
                }.beansScrollIndicatorsHidden()
            }
            .navigationTitle("Beans 账号").navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("完成") { dismiss() } } }
        }
        .sheet(isPresented: $showQR) { BeansQRLoginSheet().environmentObject(theme) }
        .sheet(isPresented: $showApprove) { BeansQRApprovalSheet().environmentObject(theme) }
        .sheet(isPresented: $showPasswordReset) { BeansPasswordResetSheet().environmentObject(theme) }
    }

    private var accountHeader: some View {
        HStack(spacing: 12) {
            Image("OnboardingLogo").resizable().scaledToFill().frame(width: 56, height: 56).clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
            VStack(alignment: .leading, spacing: 4) {
                Text("Beans 本体账号").font(BeansFont.appFont(22, .bold)).foregroundStyle(Color.beansLabel)
                Text("登录一次，三端恢复授权并共享歌单").font(BeansFont.appFont(12)).foregroundStyle(Color.beansComment)
            }
        }
    }

    private var credentialForm: some View {
        VStack(spacing: 14) {
            Picker("账号操作", selection: $mode) { ForEach(Mode.allCases, id: \.self) { Text($0.rawValue).tag($0) } }.pickerStyle(.segmented)
            if mode == .register { field("昵称", text: $nickname, icon: "person") }
            field("邮箱", text: $email, icon: "envelope")
            SecureField("密码（至少 8 位）", text: $password).textFieldStyle(BeansAccountFieldStyle(icon: "lock"))
            Button { submit() } label: {
                Group { if store.authState == .signingIn { ProgressView().tint(.white) } else { Text(mode == .login ? "登录 Beans 账号" : "创建 Beans 账号") } }
                    .font(BeansFont.appFont(15, .semibold)).foregroundStyle(.white).frame(maxWidth: .infinity).frame(height: 48)
                    .background(LinearGradient.beansAccent, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
            }.buttonStyle(.plain).disabled(store.authState == .signingIn)
            Button { showQR = true } label: { Label("使用扫码登录本体账号", systemImage: "qrcode.viewfinder").frame(maxWidth: .infinity).frame(height: 44) }.buttonStyle(.bordered)
            Button("忘记密码") { showPasswordReset = true }.font(BeansFont.appFont(11)).buttonStyle(.plain).foregroundStyle(Color.beansComment)
        }
    }

    private var verificationView: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label("验证码已发送到邮箱", systemImage: "envelope.badge").font(BeansFont.appFont(15, .semibold)).foregroundStyle(Color.beansLabel)
            field("6 位验证码", text: $verificationCode, icon: "number")
            if let code = store.developmentVerificationCode { Text("开发环境验证码：\(code)").font(BeansFont.appFont(11)).foregroundStyle(Color.beansAmber) }
            Button { verify() } label: { Text("验证并登录").frame(maxWidth: .infinity).frame(height: 44) }.buttonStyle(.borderedProminent)
        }.padding(16).background(BeansGlass(shape: RoundedRectangle(cornerRadius: 20, style: .continuous)))
    }

    private var serverField: some View {
        DisclosureGroup("账号服务", isExpanded: $showServerField) {
            TextField("https://beans.example.com", text: $serverURL).textFieldStyle(BeansAccountFieldStyle(icon: "server.rack"))
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
            Text("iPhone 真机需填写可访问的局域网或公网地址；localhost 仅适用于模拟器和当前电脑。")
                .font(BeansFont.appFont(11))
                .foregroundStyle(store.needsServerConfiguration ? Color.orange : Color.beansComment)
            Button("保存服务地址") {
                store.updateServerURL(serverURL)
                message = store.needsServerConfiguration ? "该地址无法供 iPhone 真机使用，请更换服务地址" : "服务地址已保存"
            }.buttonStyle(.bordered)
        }.font(BeansFont.appFont(13, .medium)).foregroundStyle(Color.beansComment)
    }

    private func field(_ title: String, text: Binding<String>, icon: String) -> some View { TextField(title, text: text).textFieldStyle(BeansAccountFieldStyle(icon: icon)) }

    private func submit() {
        Task {
            do {
                if mode == .login { try await store.login(email: email, password: password); message = "登录成功，已恢复授权与歌单" }
                else { try await store.register(nickname: nickname, email: email, password: password); message = "请输入邮箱中的验证码" }
            } catch { message = error.localizedDescription }
        }
    }

    private func verify() { Task { do { try await store.verifyEmail(code: verificationCode); message = "邮箱验证成功" } catch { message = error.localizedDescription } } }

    @ViewBuilder private func signedInView(_ account: BeansAccountStore.Account) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Image(systemName: "checkmark.seal.fill").foregroundStyle(Color.beansSage)
                VStack(alignment: .leading) { Text(account.nickname).font(BeansFont.appFont(16, .semibold)).foregroundStyle(Color.beansLabel); Text(account.email).font(BeansFont.appFont(12)).foregroundStyle(Color.beansComment) }
                Spacer()
                Button("退出") { auth.logout(); QQMusicAuth.shared.logout(); store.logout() }.foregroundStyle(.red)
            }
            Text("平台授权保险库").font(BeansFont.appFont(14, .semibold)).foregroundStyle(Color.beansLabel)
            ForEach(BeansAccountStore.LinkedPlatform.allCases) { platform in
                HStack { Label(platform.title, systemImage: platform.icon); Spacer(); Label(store.linkedPlatforms.contains(platform) ? "已加密同步" : "未授权", systemImage: store.linkedPlatforms.contains(platform) ? "checkmark.circle.fill" : "circle").font(BeansFont.appFont(11)).foregroundStyle(store.linkedPlatforms.contains(platform) ? Color.beansSage : Color.beansComment) }
            }
            Button { store.markSynced() } label: { Label(store.isSyncing ? "正在同步" : "立即同步", systemImage: "arrow.triangle.2.circlepath") }.buttonStyle(.borderedProminent).disabled(store.isSyncing)
            Button { showApprove = true } label: { Label("扫描并确认新设备", systemImage: "qrcode.viewfinder") }.buttonStyle(.bordered)
            if let date = store.lastSyncAt { Text("上次同步：\(date.formatted(date: .abbreviated, time: .shortened))").font(BeansFont.appFont(11)).foregroundStyle(Color.beansComment) }
            if let error = store.syncError { Label(error, systemImage: "exclamationmark.triangle.fill").font(BeansFont.appFont(11)).foregroundStyle(.orange) }
            if !store.devices.isEmpty {
                Text("登录设备").font(BeansFont.appFont(14, .semibold)).foregroundStyle(Color.beansLabel)
                ForEach(store.devices) { device in
                    HStack {
                        Image(systemName: device.platform == "windows" ? "desktopcomputer" : device.platform == "macos" ? "laptopcomputer" : "iphone")
                        VStack(alignment: .leading, spacing: 2) {
                            Text(device.name).font(BeansFont.appFont(13))
                            Text(device.id == store.currentDeviceID ? "本机" : device.lastSeenAt.formatted(date: .abbreviated, time: .shortened)).font(BeansFont.appFont(11)).foregroundStyle(Color.beansComment)
                        }
                        Spacer()
                        if !device.revoked {
                            Button(device.id == store.currentDeviceID ? "退出本机" : "撤销") { Task { try? await store.revokeDevice(device.id) } }.font(BeansFont.appFont(11)).foregroundStyle(.red)
                        } else { Text("已撤销").font(BeansFont.appFont(11)).foregroundStyle(Color.beansComment) }
                    }
                }
            }
        }.padding(16).background(BeansGlass(shape: RoundedRectangle(cornerRadius: 20, style: .continuous)))
    }
}

struct BeansPasswordResetSheet: View {
    @ObservedObject private var store = BeansAccountStore.shared
    @EnvironmentObject private var theme: ThemeStore
    @Environment(\.dismiss) private var dismiss
    @State private var email = ""
    @State private var code = ""
    @State private var password = ""
    @State private var sent = false
    @State private var developmentCode: String?
    @State private var message = "重置后旧的跨端保险库将无法恢复；当前设备的 QQ 音乐和网易云授权保持不变。"

    var body: some View {
        BeansNavigationStack {
            ZStack {
                GlassBackdrop(customColor: theme.customBackground)
                VStack(alignment: .leading, spacing: 16) {
                    Label("安全重置密码", systemImage: "lock.rotation").font(BeansFont.appFont(22, .bold)).foregroundStyle(Color.beansLabel)
                    Text(message).font(BeansFont.appFont(11)).foregroundStyle(Color.beansComment)
                    TextField("邮箱", text: $email).textFieldStyle(BeansAccountFieldStyle(icon: "envelope"))
                    if sent {
                        TextField("6 位验证码", text: $code).textFieldStyle(BeansAccountFieldStyle(icon: "number"))
                        SecureField("新密码（至少 8 位）", text: $password).textFieldStyle(BeansAccountFieldStyle(icon: "lock"))
                        if let developmentCode { Text("开发环境验证码：\(developmentCode)").font(BeansFont.appFont(11)).foregroundStyle(Color.beansAmber) }
                        Button("重置密码并清空旧跨端保险库", role: .destructive) {
                            Task {
                                do {
                                    try await store.completePasswordReset(email: email, code: code, newPassword: password)
                                    message = "密码已重置，请使用新密码登录；当前设备的平台授权仍可继续使用。"
                                } catch { message = error.localizedDescription }
                            }
                        }.buttonStyle(.borderedProminent)
                    } else {
                        Button("发送邮箱验证码") {
                            Task {
                                do { developmentCode = try await store.startPasswordReset(email: email); sent = true; message = "验证码已发送，10 分钟内有效。" }
                                catch { message = error.localizedDescription }
                            }
                        }.buttonStyle(.borderedProminent)
                    }
                    Spacer()
                }.padding(24)
            }
            .navigationTitle("忘记密码").navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("完成") { dismiss() } } }
        }
    }
}

private struct BeansAccountFieldStyle: TextFieldStyle {
    let icon: String
    func _body(configuration: TextField<Self._Label>) -> some View {
        HStack(spacing: 10) { Image(systemName: icon).foregroundStyle(Color.beansAmber); configuration }
            .padding(.horizontal, 14).frame(height: 46).background(BeansGlass(shape: RoundedRectangle(cornerRadius: 14, style: .continuous)))
    }
}

struct BeansQRLoginSheet: View {
    @ObservedObject private var store = BeansAccountStore.shared
    @EnvironmentObject private var theme: ThemeStore
    @Environment(\.dismiss) private var dismiss
    @State private var context: BeansQRLoginContext?
    @State private var message = "正在创建安全登录会话…"

    var body: some View {
        BeansNavigationStack {
            VStack(spacing: 18) {
                if let context {
                    QRCodeView(text: context.payload).frame(width: 230, height: 230).padding(16).background(.white, in: RoundedRectangle(cornerRadius: 22, style: .continuous))
                    Text("设备校验码：\(context.session.verificationCode)").font(BeansFont.appFont(18, .bold)).foregroundStyle(Color.beansAmber)
                } else { ProgressView().tint(Color.beansAmber) }
                Text("使用另一台已登录的 Beans 设备扫码确认").font(BeansFont.appFont(15, .semibold)).foregroundStyle(Color.beansLabel)
                Text(message).font(BeansFont.appFont(12)).foregroundStyle(Color.beansComment).multilineTextAlignment(.center)
                Button("完成") { dismiss() }.buttonStyle(.borderedProminent)
            }.padding(28).frame(maxWidth: .infinity, maxHeight: .infinity).background(GlassBackdrop(customColor: theme.customBackground))
                .navigationTitle("扫码登录").navigationBarTitleDisplayMode(.inline)
        }
        .task {
            do {
                let created = try await store.makeQRLoginContext()
                context = created
                message = "二维码 2 分钟有效，确认后本机会自动登录"
                try await store.pollQRLogin(created)
                message = "登录成功，已恢复授权和歌单"
                try? await Task.sleep(nanoseconds: 700_000_000)
                dismiss()
            } catch is CancellationError { }
            catch { message = error.localizedDescription }
        }
    }
}

struct BeansQRApprovalSheet: View {
    @ObservedObject private var store = BeansAccountStore.shared
    @EnvironmentObject private var theme: ThemeStore
    @Environment(\.dismiss) private var dismiss
    @State private var payload = ""
    @State private var code = ""
    @State private var session: BeansQRSession?
    @State private var message = "扫描二维码，或粘贴二维码内容"

    var body: some View {
        BeansNavigationStack {
            VStack(alignment: .leading, spacing: 16) {
                BeansQRScannerView(scannedPayload: $payload).frame(maxWidth: .infinity).frame(height: 220).clipShape(RoundedRectangle(cornerRadius: 20, style: .continuous))
                TextField("beans://account/qr…", text: $payload).textFieldStyle(BeansAccountFieldStyle(icon: "qrcode"))
                if let session {
                    Text("目标设备：\(session.device.name)").font(BeansFont.appFont(15, .semibold)).foregroundStyle(Color.beansLabel)
                    Text("系统：\(session.device.platform) · \(session.expiresAt.formatted(date: .omitted, time: .shortened)) 到期").font(BeansFont.appFont(11)).foregroundStyle(Color.beansComment)
                    TextField("目标设备显示的 6 位校验码", text: $code).textFieldStyle(BeansAccountFieldStyle(icon: "number"))
                    Button("确认登录此设备") { approve(session) }.buttonStyle(.borderedProminent)
                } else { Button("读取登录请求") { loadSession() }.buttonStyle(.borderedProminent).disabled(payload.isEmpty) }
                Text(message).font(BeansFont.appFont(11)).foregroundStyle(Color.beansComment)
                Spacer()
            }.padding(20).background(GlassBackdrop(customColor: theme.customBackground)).navigationTitle("确认新设备")
                .toolbar { ToolbarItem(placement: .confirmationAction) { Button("关闭") { dismiss() } } }
        }
        .onChange(of: payload) { value in if value.hasPrefix("beans://") { loadSession() } }
    }

    private func loadSession() {
        Task { do { session = try await store.qrSession(from: payload); code = session?.verificationCode ?? ""; message = "请核对两台设备显示的校验码" } catch { message = error.localizedDescription } }
    }

    private func approve(_ value: BeansQRSession) {
        Task { do { try await store.approveQRSession(value, verificationCode: code); message = "已安全确认新设备"; try? await Task.sleep(nanoseconds: 700_000_000); dismiss() } catch { message = error.localizedDescription } }
    }
}
