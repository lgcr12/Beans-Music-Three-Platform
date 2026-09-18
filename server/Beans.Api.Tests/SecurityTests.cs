using Beans.Api.Contracts;
using Beans.Api.Services;
using Xunit;

namespace Beans.Api.Tests;

public sealed class SecurityTests
{
    [Fact]
    public void VerificationCode_IsAlwaysSixDigits()
    {
        for (var index = 0; index < 100; index++)
        {
            var code = VerificationService.NewCode();
            Assert.Matches("^[0-9]{6}$", code);
        }
    }

    [Fact]
    public void VerificationCode_UsesConstantTimeCompatibleHash()
    {
        var hash = VerificationService.HashCode("123456");
        Assert.True(VerificationService.Matches("123456", hash));
        Assert.False(VerificationService.Matches("123457", hash));
    }

    [Fact]
    public void OpaqueTokens_AreRandomAndHashDeterministically()
    {
        var first = TokenService.NewOpaqueToken();
        var second = TokenService.NewOpaqueToken();
        Assert.NotEqual(first, second);
        Assert.Equal(TokenService.HashOpaqueToken(first), TokenService.HashOpaqueToken(first));
        Assert.NotEqual(TokenService.HashOpaqueToken(first), TokenService.HashOpaqueToken(second));
    }

    [Theory]
    [InlineData("ios")]
    [InlineData("macos")]
    [InlineData("windows")]
    public void SupportedPlatforms_AreAccepted(string platform) => Assert.True(ContractValidation.IsPlatform(platform));

    [Fact]
    public void PlaintextAndShortSecrets_AreRejected()
    {
        Assert.False(ContractValidation.IsBase64Bytes("password", 32));
        Assert.False(ContractValidation.IsBase64Bytes(Convert.ToBase64String(new byte[8]), 32));
        Assert.True(ContractValidation.IsBase64Bytes(Convert.ToBase64String(new byte[32]), 32));
    }
}
