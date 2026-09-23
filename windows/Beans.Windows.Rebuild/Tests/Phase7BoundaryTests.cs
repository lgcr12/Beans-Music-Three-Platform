using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Accounts;
using Beans.Windows.Rebuild.Services.Playback;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class Phase7BoundaryTests
{
    [Fact]
    public async Task PreviewAuthDoesNotClaimAuthorization()
    {
        var service = new PreviewPlatformAuthService();
        var state = await service.GetStateAsync(PlatformId.QqMusic, CancellationToken.None);
        var result = await service.AuthorizeAsync(PlatformId.QqMusic, CancellationToken.None);

        Assert.Equal(CredentialState.NotAuthorized, state.CredentialState);
        Assert.False(result.IsSuccess);
        Assert.Contains("授权", result.SafeMessage);
    }

    [Theory]
    [InlineData(PlatformId.QqMusic)]
    [InlineData(PlatformId.NetEaseMusic)]
    public async Task OnlinePlaybackBoundaryNeverFabricatesUrl(PlatformId platform)
    {
        var resolver = new OnlinePlaybackBoundaryResolver(platform);
        var result = await resolver.ResolveAsync(new PlaybackSourceRequest(new MusicIdentity(platform, "track-1")), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Source);
        Assert.Equal(PlaybackRestriction.RequiresAuthorization, result.Restriction);
        Assert.Contains("官方播放源", result.SafeMessage);
        Assert.DoesNotContain("http", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }
}
