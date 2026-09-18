import Argon2Swift
import CryptoKit
import Foundation
import Security

enum BeansAccountCrypto {
    struct DerivedKeys {
        let authSecret: Data
        let vaultWrappingKey: SymmetricKey
    }

    struct QRKeyPair {
        let privateKey: Data
        let publicKey: Data
    }

    private struct QRVaultEnvelope: Codable {
        let senderPublicKey: Data
        let ciphertext: Data
    }

    static func randomData(count: Int) -> Data {
        var bytes = [UInt8](repeating: 0, count: count)
        let status = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        precondition(status == errSecSuccess, "无法生成安全随机数")
        return Data(bytes)
    }

    static func derive(password: String, profile: BeansCryptoProfile) async throws -> DerivedKeys {
        guard profile.algorithm == "argon2id-v1", let saltData = Data(base64Encoded: profile.salt) else {
            throw BeansCryptoError.invalidProfile
        }
        let root = try await Task.detached(priority: .userInitiated) {
            try Argon2Swift.hashPasswordString(
                password: password,
                salt: Salt(bytes: saltData),
                iterations: profile.iterations,
                memory: profile.memoryKiB,
                parallelism: profile.parallelism,
                length: 32,
                type: .id
            ).hashData()
        }.value
        let rootKey = SymmetricKey(data: root)
        let authKey = HKDF<SHA256>.deriveKey(inputKeyMaterial: rootKey, salt: Data(), info: Data("beans-auth-v1".utf8), outputByteCount: 32)
        let vaultKey = HKDF<SHA256>.deriveKey(inputKeyMaterial: rootKey, salt: Data(), info: Data("beans-vault-wrap-v1".utf8), outputByteCount: 32)
        return DerivedKeys(authSecret: authKey.data, vaultWrappingKey: vaultKey)
    }

    static func wrapVaultKey(_ vaultKey: Data, using wrappingKey: SymmetricKey) throws -> Data {
        guard vaultKey.count == 32, let combined = try AES.GCM.seal(vaultKey, using: wrappingKey).combined else {
            throw BeansCryptoError.encryptionFailed
        }
        return combined
    }

    static func unwrapVaultKey(_ envelope: Data, using wrappingKey: SymmetricKey) throws -> Data {
        let box = try AES.GCM.SealedBox(combined: envelope)
        let key = try AES.GCM.open(box, using: wrappingKey)
        guard key.count == 32 else { throw BeansCryptoError.invalidVaultKey }
        return key
    }

    static func encrypt(_ data: Data, vaultKey: Data) throws -> Data {
        guard vaultKey.count == 32, let combined = try AES.GCM.seal(data, using: SymmetricKey(data: vaultKey)).combined else {
            throw BeansCryptoError.encryptionFailed
        }
        return combined
    }

    static func decrypt(_ combined: Data, vaultKey: Data) throws -> Data {
        guard vaultKey.count == 32 else { throw BeansCryptoError.invalidVaultKey }
        return try AES.GCM.open(AES.GCM.SealedBox(combined: combined), using: SymmetricKey(data: vaultKey))
    }

    static func makeQRKeyPair() -> QRKeyPair {
        let key = Curve25519.KeyAgreement.PrivateKey()
        return QRKeyPair(privateKey: key.rawRepresentation, publicKey: key.publicKey.rawRepresentation)
    }

    static func makePublicKey(privateKey: Data) throws -> Data {
        try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: privateKey).publicKey.rawRepresentation
    }

    static func encryptVaultKeyForQR(_ vaultKey: Data, targetPublicKey: Data) throws -> Data {
        let target = try Curve25519.KeyAgreement.PublicKey(rawRepresentation: targetPublicKey)
        let sender = Curve25519.KeyAgreement.PrivateKey()
        let shared = try sender.sharedSecretFromKeyAgreement(with: target)
        let key = shared.hkdfDerivedSymmetricKey(using: SHA256.self, salt: Data(), sharedInfo: Data("beans-qr-v1".utf8), outputByteCount: 32)
        guard let ciphertext = try AES.GCM.seal(vaultKey, using: key).combined else { throw BeansCryptoError.encryptionFailed }
        return try JSONEncoder().encode(QRVaultEnvelope(senderPublicKey: sender.publicKey.rawRepresentation, ciphertext: ciphertext))
    }

    static func decryptVaultKeyFromQR(_ envelopeData: Data, targetPrivateKey: Data) throws -> Data {
        let envelope = try JSONDecoder().decode(QRVaultEnvelope.self, from: envelopeData)
        let target = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: targetPrivateKey)
        let sender = try Curve25519.KeyAgreement.PublicKey(rawRepresentation: envelope.senderPublicKey)
        let shared = try target.sharedSecretFromKeyAgreement(with: sender)
        let key = shared.hkdfDerivedSymmetricKey(using: SHA256.self, salt: Data(), sharedInfo: Data("beans-qr-v1".utf8), outputByteCount: 32)
        let vault = try AES.GCM.open(AES.GCM.SealedBox(combined: envelope.ciphertext), using: key)
        guard vault.count == 32 else { throw BeansCryptoError.invalidVaultKey }
        return vault
    }
}

private extension SymmetricKey {
    var data: Data { withUnsafeBytes { Data($0) } }
}

enum BeansCryptoError: LocalizedError {
    case invalidProfile, invalidVaultKey, encryptionFailed

    var errorDescription: String? {
        switch self {
        case .invalidProfile: return "账号加密参数无效"
        case .invalidVaultKey: return "账号保险库密钥无效"
        case .encryptionFailed: return "账号保险库加密失败"
        }
    }
}
