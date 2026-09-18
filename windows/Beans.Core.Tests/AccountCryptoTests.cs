using Beans.Core;
using Xunit;

namespace Beans.Core.Tests;

public sealed class AccountCryptoTests
{
    [Fact]
    public async Task DerivationIsDeterministicAndSeparatesKeys()
    {
        var profile = new CryptoProfile("argon2id-v1", Convert.ToBase64String(new byte[16]), 1024, 1, 1);
        var first = await AccountCrypto.DeriveAsync("beans-test-password", profile, TestContext.Current.CancellationToken);
        var second = await AccountCrypto.DeriveAsync("beans-test-password", profile, TestContext.Current.CancellationToken);
        Assert.Equal(first.AuthSecret, second.AuthSecret);
        Assert.NotEqual(first.AuthSecret, first.VaultWrappingKey);
    }

    [Fact]
    public void VaultEncryptionRoundTrips()
    {
        var key = AccountCrypto.RandomBytes(32);
        var value = AccountCrypto.RandomBytes(32);
        Assert.Equal(value, AccountCrypto.Decrypt(AccountCrypto.Encrypt(value, key), key));
    }

    [Fact]
    public void QrVaultTransferRoundTrips()
    {
        var target = AccountCrypto.MakeQrKeyPair();
        var vaultKey = AccountCrypto.RandomBytes(32);
        var envelope = AccountCrypto.EncryptVaultKeyForQr(vaultKey, target.PublicKey);
        Assert.Equal(vaultKey, AccountCrypto.DecryptVaultKeyFromQr(envelope, target.PrivateKey));
    }

    [Fact]
    public void PlatformCredentialPolicyRequiresProviderSessionCookies()
    {
        Assert.True(PlatformCredentialPolicy.LooksUsable("netease", new Dictionary<string, string> { ["MUSIC_U"] = "encrypted-session" }));
        Assert.True(PlatformCredentialPolicy.LooksUsable("qq", new Dictionary<string, string> { ["uin"] = "1", ["qm_keyst"] = "encrypted-session" }));
        Assert.False(PlatformCredentialPolicy.LooksUsable("netease", new Dictionary<string, string> { ["__csrf"] = "only-csrf" }));
    }

    [Fact]
    public void PlatformMirrorUsesLongStableIds()
    {
        var playlist = new MirrorPlaylist(9_223_372_036_854_000_000L, "收藏", null, 12, "Beans", "qq");
        Assert.Equal(9_223_372_036_854_000_000L, playlist.Id);
    }
}
