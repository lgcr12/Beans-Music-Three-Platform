using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Beans.Windows.Rebuild.Services.Platforms.NetEase;

internal static class NetEaseWeapi
{
    private static readonly byte[] FixedKey = Encoding.UTF8.GetBytes("0CoJUm6Qyw8W8jud");
    private static readonly byte[] FixedIv = Encoding.UTF8.GetBytes("0102030405060708");
    private static readonly BigInteger Modulus = new(
        Convert.FromHexString("00e0b509f6259df8642dbc35662901477df22677ec152b5ff68ace615bb7b725152b3ab17a876aea8a5aa76d2e417629ec4ee341f56135fccf695280104e0312ecbda92557c93870114af6c9d05c4f7f0c3685b7a46bee255932575cce10b424d813cfe4875d3e82047b97ddef52741d546b8e289dc6935b3ece0462db0a22b8e7"),
        isUnsigned: true,
        isBigEndian: true);
    private const string SecretAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public static FormUrlEncodedContent CreateContent(IReadOnlyDictionary<string, object?> payload)
    {
        var publicPayload = new Dictionary<string, object?>(payload, StringComparer.Ordinal)
        {
            ["csrf_token"] = string.Empty
        };
        var json = JsonSerializer.Serialize(publicPayload);
        var secret = RandomSecret();
        var firstPass = EncryptCbc(Encoding.UTF8.GetBytes(json), FixedKey);
        var secondPass = EncryptCbc(Encoding.UTF8.GetBytes(Convert.ToBase64String(firstPass)), Encoding.UTF8.GetBytes(secret));
        var reversedSecret = Encoding.UTF8.GetBytes(new string(secret.Reverse().ToArray()));
        var encryptedSecret = BigInteger.ModPow(
            new BigInteger(reversedSecret, isUnsigned: true, isBigEndian: true),
            65537,
            Modulus).ToByteArray(isUnsigned: true, isBigEndian: true);

        return new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["params"] = Convert.ToBase64String(secondPass),
            ["encSecKey"] = Convert.ToHexString(PadLeft(encryptedSecret, 128)).ToLowerInvariant()
        });
    }

    private static byte[] EncryptCbc(byte[] input, byte[] key)
    {
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = FixedIv;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(input, 0, input.Length);
    }

    private static string RandomSecret()
    {
        Span<byte> random = stackalloc byte[16];
        RandomNumberGenerator.Fill(random);
        var chars = new char[random.Length];
        for (var index = 0; index < random.Length; index++)
            chars[index] = SecretAlphabet[random[index] % SecretAlphabet.Length];
        return new string(chars);
    }

    private static byte[] PadLeft(byte[] value, int length)
    {
        if (value.Length >= length) return value[^length..];
        var padded = new byte[length];
        value.CopyTo(padded, length - value.Length);
        return padded;
    }
}
