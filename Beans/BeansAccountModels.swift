import Foundation

struct BeansCryptoProfile: Codable, Equatable {
    let algorithm: String
    let salt: String
    let memoryKiB: Int
    let iterations: Int
    let parallelism: Int

    init(salt: Data, memoryKiB: Int = 65_536, iterations: Int = 3, parallelism: Int = 2) {
        algorithm = "argon2id-v1"
        self.salt = salt.base64EncodedString()
        self.memoryKiB = memoryKiB
        self.iterations = iterations
        self.parallelism = parallelism
    }
}

struct BeansDeviceInput: Codable, Equatable {
    let id: UUID
    let name: String
    let platform: String
}

struct BeansAccountRecord: Codable, Equatable {
    let id: UUID
    let nickname: String
    let email: String
    let createdAt: Date
}

struct BeansTokenPair: Codable {
    let accessToken: String
    let accessTokenExpiresAt: Date
    let refreshToken: String
    let refreshTokenExpiresAt: Date
}

struct BeansAuthSession: Codable {
    let account: BeansAccountRecord
    let wrappedVaultKey: String
    let accessToken: String
    let accessTokenExpiresAt: Date
    let refreshToken: String
    let refreshTokenExpiresAt: Date

    var tokenPair: BeansTokenPair {
        BeansTokenPair(accessToken: accessToken, accessTokenExpiresAt: accessTokenExpiresAt, refreshToken: refreshToken, refreshTokenExpiresAt: refreshTokenExpiresAt)
    }
}

struct BeansPendingRegistration: Codable {
    let registrationId: UUID
    let expiresAt: Date
    let developmentCode: String?
}

struct BeansAuthChallenge: Codable { let cryptoProfile: BeansCryptoProfile }

struct BeansVaultEnvelope: Codable {
    let version: Int64
    let ciphertext: String
    let updatedAt: Date
}

struct BeansPlatformCredentialBundle: Codable, Equatable {
    var qq: [String: String]?
    var netease: [String: String]?
    var updatedAt: Date
}

struct BeansSyncEnvelope: Codable, Identifiable {
    let id: UUID
    let entityType: String
    let entityId: String
    let deviceId: UUID
    let baseRevision: Int64
    let revision: Int64
    let deleted: Bool
    let ciphertext: String
    let updatedAt: Date
}

struct BeansSyncPage: Codable {
    let cursor: Int64
    let hasMore: Bool
    let records: [BeansSyncEnvelope]
}

struct BeansQRSession: Codable {
    let id: UUID
    let status: String
    let verificationCode: String
    let expiresAt: Date
    let device: BeansDeviceInput
    let ephemeralPublicKey: String
}

struct BeansQRExchangeResult: Codable {
    let account: BeansAccountRecord
    let encryptedVaultKey: String
    let accessToken: String
    let accessTokenExpiresAt: Date
    let refreshToken: String
    let refreshTokenExpiresAt: Date

    var tokenPair: BeansTokenPair {
        BeansTokenPair(accessToken: accessToken, accessTokenExpiresAt: accessTokenExpiresAt, refreshToken: refreshToken, refreshTokenExpiresAt: refreshTokenExpiresAt)
    }
}

struct BeansDeviceRecord: Codable, Identifiable {
    let id: UUID
    let name: String
    let platform: String
    let createdAt: Date
    let lastSeenAt: Date
    let revoked: Bool
}

struct BeansQRLoginContext {
    let session: BeansQRSession
    let payload: String
    let exchangeSecret: Data
    let privateKey: Data
}
