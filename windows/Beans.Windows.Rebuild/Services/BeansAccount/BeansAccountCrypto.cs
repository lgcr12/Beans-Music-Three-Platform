using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Beans.Windows.Rebuild.Services.BeansAccount;

public sealed record BeansDerivedKeys(byte[] AuthSecret, byte[] VaultWrappingKey);

public interface IBeansAccountCryptography
{
    Task<BeansDerivedKeys> DeriveAsync(string password, BeansCryptoProfile profile, CancellationToken cancellationToken = default);
    byte[] RandomBytes(int count);
    byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key);
    byte[] Decrypt(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> key);
}

public sealed class BeansAccountCryptography : IBeansAccountCryptography
{
    public async Task<BeansDerivedKeys> DeriveAsync(
        string password,
        BeansCryptoProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        ValidateProfile(profile);

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            var argon = new Argon2id(passwordBytes)
            {
                Salt = Convert.FromBase64String(profile.Salt),
                MemorySize = profile.MemoryKiB,
                Iterations = profile.Iterations,
                DegreeOfParallelism = profile.Parallelism
            };
            cancellationToken.ThrowIfCancellationRequested();
            var root = await argon.GetBytesAsync(32).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new BeansDerivedKeys(
                    Hkdf(root, "beans-auth-v1"),
                    Hkdf(root, "beans-vault-wrap-v1"));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(root);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public byte[] RandomBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return RandomNumberGenerator.GetBytes(count);
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key)
    {
        ValidateKey(key);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var envelope = new byte[nonce.Length + ciphertext.Length + tag.Length];
        nonce.CopyTo(envelope, 0);
        ciphertext.CopyTo(envelope, nonce.Length);
        tag.CopyTo(envelope, nonce.Length + ciphertext.Length);
        CryptographicOperations.ZeroMemory(ciphertext);
        return envelope;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> key)
    {
        ValidateKey(key);
        if (envelope.Length < 29) throw new CryptographicException("Beans 密文无效");

        var plaintext = new byte[envelope.Length - 28];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(
                envelope[..12],
                envelope[12..^16],
                envelope[^16..],
                plaintext);
            return plaintext;
        }
        catch (AuthenticationTagMismatchException exception)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new CryptographicException("Beans 密文验证失败", exception);
        }
    }

    public static BeansCryptoProfile CreateRegistrationProfile() => new(
        "argon2id-v1",
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)),
        65_536,
        3,
        2);

    private static void ValidateProfile(BeansCryptoProfile profile)
    {
        if (profile.Algorithm != "argon2id-v1"
            || profile.MemoryKiB is < 32_768 or > 262_144
            || profile.Iterations is < 2 or > 10
            || profile.Parallelism is < 1 or > 8)
        {
            throw new CryptographicException("Beans 账号加密参数不受支持");
        }

        try
        {
            if (Convert.FromBase64String(profile.Salt).Length < 16)
                throw new CryptographicException("Beans 账号加密参数无效");
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("Beans 账号加密参数无效", exception);
        }
    }

    private static byte[] Hkdf(ReadOnlySpan<byte> input, string info)
    {
        var output = new byte[32];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            input,
            output,
            ReadOnlySpan<byte>.Empty,
            Encoding.UTF8.GetBytes(info));
        return output;
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32) throw new CryptographicException("Beans 加密密钥无效");
    }
}
