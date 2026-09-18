import Foundation

actor BeansAPIClient {
    static let shared = BeansAPIClient()

    private var baseURL: URL?
    private var tokens: BeansTokenPair?
    private let encoder: JSONEncoder
    private let decoder: JSONDecoder

    private init() {
        encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        if let data = BeansSecureStore.shared.data(for: BeansSecureKey.accessToken),
           let saved = try? decoder.decode(BeansTokenPair.self, from: data) {
            tokens = saved
        }
    }

    func configure(serverURL: String) throws {
        let trimmed = serverURL.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let url = URL(string: trimmed), let scheme = url.scheme?.lowercased(), ["http", "https"].contains(scheme), url.host != nil else {
            throw BeansAPIError.invalidServerURL
        }
#if os(iOS) && !targetEnvironment(simulator) && !targetEnvironment(macCatalyst)
        let host = url.host?.lowercased() ?? ""
        if host == "localhost" || host == "127.0.0.1" || host == "::1" {
            throw BeansAPIError.loopbackUnavailable
        }
#endif
        baseURL = url
    }

    func restoreTokens(_ value: BeansTokenPair) {
        tokens = value
        if let data = try? encoder.encode(value) { _ = BeansSecureStore.shared.set(data, for: BeansSecureKey.accessToken) }
    }

    func clearTokens() {
        tokens = nil
        BeansSecureStore.shared.remove(BeansSecureKey.accessToken)
        BeansSecureStore.shared.remove(BeansSecureKey.refreshToken)
    }

    func startRegistration(nickname: String, email: String, authSecret: Data, profile: BeansCryptoProfile, wrappedVaultKey: Data, device: BeansDeviceInput) async throws -> BeansPendingRegistration {
        struct Body: Encodable { let nickname: String; let email: String; let authSecret: String; let cryptoProfile: BeansCryptoProfile; let wrappedVaultKey: String; let device: BeansDeviceInput }
        return try await send("/v1/auth/register/start", method: "POST", body: Body(nickname: nickname, email: email, authSecret: authSecret.base64EncodedString(), cryptoProfile: profile, wrappedVaultKey: wrappedVaultKey.base64EncodedString(), device: device), authorized: false)
    }

    func verifyEmail(registrationID: UUID, code: String) async throws -> BeansAuthSession {
        struct Body: Encodable { let registrationId: UUID; let code: String }
        return try await send("/v1/auth/verify-email", method: "POST", body: Body(registrationId: registrationID, code: code), authorized: false)
    }

    func loginChallenge(email: String) async throws -> BeansAuthChallenge {
        struct Body: Encodable { let email: String }
        return try await send("/v1/auth/login/challenge", method: "POST", body: Body(email: email), authorized: false)
    }

    func login(email: String, authSecret: Data, device: BeansDeviceInput) async throws -> BeansAuthSession {
        struct Body: Encodable { let email: String; let authSecret: String; let device: BeansDeviceInput }
        return try await send("/v1/auth/login", method: "POST", body: Body(email: email, authSecret: authSecret.base64EncodedString(), device: device), authorized: false)
    }

    func logout() async { _ = try? await sendEmpty("/v1/auth/logout", method: "POST", authorized: true) }

    func getVault() async throws -> BeansVaultEnvelope {
        try await send("/v1/vault/", method: "GET", authorized: true)
    }

    func putVault(_ value: BeansVaultEnvelope) async throws {
        _ = try await sendEmpty("/v1/vault/", method: "PUT", body: value, authorized: true)
    }

    func createQRSession(device: BeansDeviceInput, publicKey: Data, exchangeSecret: Data) async throws -> BeansQRSession {
        struct Body: Encodable { let device: BeansDeviceInput; let ephemeralPublicKey: String; let exchangeSecret: String }
        return try await send("/v1/qr-sessions/", method: "POST", body: Body(device: device, ephemeralPublicKey: publicKey.base64EncodedString(), exchangeSecret: exchangeSecret.base64EncodedString()), authorized: false)
    }

    func getQRSession(_ id: UUID) async throws -> BeansQRSession {
        try await send("/v1/qr-sessions/\(id)", method: "GET", authorized: false)
    }

    func approveQRSession(_ id: UUID, code: String, encryptedVaultKey: Data) async throws {
        struct Body: Encodable { let verificationCode: String; let encryptedVaultKey: String }
        _ = try await sendEmpty("/v1/qr-sessions/\(id)/approve", method: "POST", body: Body(verificationCode: code, encryptedVaultKey: encryptedVaultKey.base64EncodedString()), authorized: true)
    }

    func exchangeQRSession(_ context: BeansQRLoginContext, device: BeansDeviceInput) async throws -> BeansQRExchangeResult {
        struct Body: Encodable { let device: BeansDeviceInput; let ephemeralPublicKey: String; let exchangeSecret: String }
        let publicKey = try BeansAccountCrypto.makePublicKey(privateKey: context.privateKey)
        return try await send("/v1/qr-sessions/\(context.session.id)/exchange", method: "POST", body: Body(device: device, ephemeralPublicKey: publicKey.base64EncodedString(), exchangeSecret: context.exchangeSecret.base64EncodedString()), authorized: false)
    }

    func pullSync(cursor: Int64) async throws -> BeansSyncPage {
        try await send("/v1/sync/?cursor=\(cursor)", method: "GET", authorized: true)
    }

    func listDevices() async throws -> [BeansDeviceRecord] {
        try await send("/v1/devices/", method: "GET", authorized: true)
    }

    func revokeDevice(_ id: UUID) async throws {
        _ = try await sendEmpty("/v1/devices/\(id)", method: "DELETE", authorized: true)
    }

    func startPasswordReset(email: String) async throws -> String? {
        struct Body: Encodable { let email: String }
        struct Response: Decodable { let developmentCode: String? }
        let body = try encoder.encode(Body(email: email))
        let (data, response) = try await execute("/v1/auth/password/reset/start", method: "POST", bodyData: body, authorized: false)
        guard (200..<300).contains(response.statusCode) else { throw decodeError(data, status: response.statusCode) }
        guard !data.isEmpty else { return nil }
        return try? decoder.decode(Response.self, from: data).developmentCode
    }

    func completePasswordReset(email: String, code: String, authSecret: Data, profile: BeansCryptoProfile, wrappedVaultKey: Data) async throws {
        struct Body: Encodable { let email: String; let code: String; let authSecret: String; let cryptoProfile: BeansCryptoProfile; let wrappedVaultKey: String }
        let body = Body(email: email, code: code, authSecret: authSecret.base64EncodedString(), cryptoProfile: profile, wrappedVaultKey: wrappedVaultKey.base64EncodedString())
        _ = try await sendEmpty("/v1/auth/password/reset/complete", method: "POST", body: body, authorized: false)
    }

    func pushSync(_ records: [BeansSyncEnvelope]) async throws -> [BeansSyncEnvelope] {
        try await send("/v1/sync/batch", method: "POST", body: records, authorized: true)
    }

    private func send<Response: Decodable>(_ path: String, method: String, authorized: Bool, retrying: Bool = false) async throws -> Response {
        try await send(path, method: method, bodyData: nil, authorized: authorized, retrying: retrying)
    }

    private func send<Body: Encodable, Response: Decodable>(_ path: String, method: String, body: Body, authorized: Bool, retrying: Bool = false) async throws -> Response {
        try await send(path, method: method, bodyData: try encoder.encode(body), authorized: authorized, retrying: retrying)
    }

    private func send<Response: Decodable>(_ path: String, method: String, bodyData: Data?, authorized: Bool, retrying: Bool) async throws -> Response {
        let (data, response) = try await execute(path, method: method, bodyData: bodyData, authorized: authorized)
        if response.statusCode == 401, authorized, !retrying, try await refreshIfPossible() {
            return try await send(path, method: method, bodyData: bodyData, authorized: true, retrying: true)
        }
        guard (200..<300).contains(response.statusCode) else { throw decodeError(data, status: response.statusCode) }
        do { return try decoder.decode(Response.self, from: data) }
        catch { throw BeansAPIError.invalidResponse }
    }

    private func sendEmpty<Body: Encodable>(_ path: String, method: String, body: Body, authorized: Bool) async throws -> Bool {
        try await sendEmpty(path, method: method, bodyData: try encoder.encode(body), authorized: authorized)
    }

    private func sendEmpty(_ path: String, method: String, authorized: Bool) async throws -> Bool {
        try await sendEmpty(path, method: method, bodyData: nil, authorized: authorized)
    }

    private func sendEmpty(_ path: String, method: String, bodyData: Data?, authorized: Bool, retrying: Bool = false) async throws -> Bool {
        let (data, response) = try await execute(path, method: method, bodyData: bodyData, authorized: authorized)
        if response.statusCode == 401, authorized, !retrying, try await refreshIfPossible() {
            return try await sendEmpty(path, method: method, bodyData: bodyData, authorized: true, retrying: true)
        }
        guard (200..<300).contains(response.statusCode) else { throw decodeError(data, status: response.statusCode) }
        return true
    }

    private func execute(_ path: String, method: String, bodyData: Data?, authorized: Bool) async throws -> (Data, HTTPURLResponse) {
        guard let baseURL, let url = URL(string: path, relativeTo: baseURL) else { throw BeansAPIError.invalidServerURL }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.timeoutInterval = 25
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let bodyData { request.httpBody = bodyData; request.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        if authorized, let token = tokens?.accessToken { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await URLSession.shared.data(for: request)
        } catch let error as URLError {
            BeansLogger.shared.log("Beans 账号服务连接失败：code=\(error.code.rawValue)", level: .error)
            throw BeansAPIError.network
        } catch {
            throw BeansAPIError.network
        }
        guard let http = response as? HTTPURLResponse else { throw BeansAPIError.network }
        return (data, http)
    }

    private func refreshIfPossible() async throws -> Bool {
        guard let refresh = tokens?.refreshToken else { return false }
        struct Body: Encodable { let refreshToken: String }
        let pair: BeansTokenPair = try await send("/v1/auth/refresh", method: "POST", body: Body(refreshToken: refresh), authorized: false)
        restoreTokens(pair)
        return true
    }

    private func decodeError(_ data: Data, status: Int) -> Error {
        struct Problem: Decodable { let detail: String?; let title: String? }
        let problem = try? decoder.decode(Problem.self, from: data)
        return BeansAPIError.server(status, problem?.detail ?? problem?.title ?? "服务器请求失败")
    }
}

enum BeansAPIError: LocalizedError {
    case invalidServerURL, loopbackUnavailable, network, invalidResponse, server(Int, String)

    var errorDescription: String? {
        switch self {
        case .invalidServerURL: return "请先配置有效的 Beans 账号服务地址"
        case .loopbackUnavailable: return "真机不能使用 localhost，请填写电脑的局域网地址或已部署的公网服务地址"
        case .network: return "无法连接 Beans 账号服务，请检查服务地址、网络和后端是否已启动"
        case .invalidResponse: return "Beans 账号服务返回了无法识别的数据"
        case .server(_, let message): return message
        }
    }
}
