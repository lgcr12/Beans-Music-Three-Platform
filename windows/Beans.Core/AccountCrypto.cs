using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using NSec.Cryptography;
using System.Text.Json;

namespace Beans.Core;

public sealed record DerivedKeys(byte[] AuthSecret, byte[] VaultWrappingKey);

public static class AccountCrypto
{
    private sealed record QrVaultEnvelope(byte[] SenderPublicKey, byte[] Ciphertext);
    public static async Task<DerivedKeys> DeriveAsync(string password, CryptoProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.Algorithm != "argon2id-v1") throw new CryptographicException("不支持的账号加密参数");
        var argon = new Konscious.Security.Cryptography.Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = Convert.FromBase64String(profile.Salt),
            MemorySize = profile.MemoryKiB,
            Iterations = profile.Iterations,
            DegreeOfParallelism = profile.Parallelism
        };
        cancellationToken.ThrowIfCancellationRequested();
        var root = await argon.GetBytesAsync(32);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return new DerivedKeys(
                Hkdf(root, "beans-auth-v1", 32),
                Hkdf(root, "beans-vault-wrap-v1", 32));
        }
        finally { CryptographicOperations.ZeroMemory(root); }
    }

    public static byte[] RandomBytes(int count) => RandomNumberGenerator.GetBytes(count);
    public static byte[] WrapVaultKey(byte[] vaultKey, byte[] wrappingKey) => Encrypt(vaultKey, wrappingKey);
    public static byte[] UnwrapVaultKey(byte[] envelope, byte[] wrappingKey) => Decrypt(envelope, wrappingKey);
    public static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return [.. nonce, .. ciphertext, .. tag];
    }

    public static byte[] Decrypt(byte[] combined, byte[] key)
    {
        if (combined.Length < 29) throw new CryptographicException("保险库密文无效");
        var nonce = combined.AsSpan(0, 12);
        var ciphertext = combined.AsSpan(12, combined.Length - 28);
        var tag = combined.AsSpan(combined.Length - 16, 16);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public static (byte[] PrivateKey, byte[] PublicKey) MakeQrKeyPair()
    {
        using var key = Key.Create(KeyAgreementAlgorithm.X25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (key.Export(KeyBlobFormat.RawPrivateKey), key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    }

    public static byte[] PublicKeyFromPrivate(byte[] privateKey)
    {
        using var key = Key.Import(KeyAgreementAlgorithm.X25519, privateKey, KeyBlobFormat.RawPrivateKey, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    public static byte[] EncryptVaultKeyForQr(byte[] vaultKey, byte[] targetPublicKey)
    {
        using var sender = Key.Create(KeyAgreementAlgorithm.X25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var target = PublicKey.Import(KeyAgreementAlgorithm.X25519, targetPublicKey, KeyBlobFormat.RawPublicKey);
        using var shared = KeyAgreementAlgorithm.X25519.Agree(sender, target) ?? throw new CryptographicException("无法建立扫码密钥");
        var key = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(shared, ReadOnlySpan<byte>.Empty, Encoding.UTF8.GetBytes("beans-qr-v1"), 32);
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(new QrVaultEnvelope(sender.PublicKey.Export(KeyBlobFormat.RawPublicKey), Encrypt(vaultKey, key)), JsonOptions.Default);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static byte[] DecryptVaultKeyFromQr(byte[] envelopeBytes, byte[] targetPrivateKey)
    {
        var envelope = JsonSerializer.Deserialize<QrVaultEnvelope>(envelopeBytes, JsonOptions.Default) ?? throw new CryptographicException("扫码密钥信封无效");
        using var target = Key.Import(KeyAgreementAlgorithm.X25519, targetPrivateKey, KeyBlobFormat.RawPrivateKey, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var sender = PublicKey.Import(KeyAgreementAlgorithm.X25519, envelope.SenderPublicKey, KeyBlobFormat.RawPublicKey);
        using var shared = KeyAgreementAlgorithm.X25519.Agree(target, sender) ?? throw new CryptographicException("无法建立扫码密钥");
        var key = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(shared, ReadOnlySpan<byte>.Empty, Encoding.UTF8.GetBytes("beans-qr-v1"), 32);
        try { return Decrypt(envelope.Ciphertext, key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] Hkdf(byte[] input, string info, int length)
    {
        var output = new byte[length];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, input, output, ReadOnlySpan<byte>.Empty, Encoding.UTF8.GetBytes(info));
        return output;
    }
}
