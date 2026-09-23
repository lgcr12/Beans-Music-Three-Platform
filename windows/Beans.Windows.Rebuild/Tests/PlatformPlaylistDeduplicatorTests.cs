using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.PlatformLibrary;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class PlatformPlaylistDeduplicatorTests
{
    [Fact]
    public void SamePlatformVisibleIdentityKeepsPreferredRecord()
    {
        var playlists = new PlatformUserPlaylist[]
        {
            new(PlatformId.QqMusic, "first", "diss", "耳朵怀孕系列", " 橙满集 ", "https://img/one", null),
            new(PlatformId.QqMusic, "second", "tid", "耳朵怀孕系列", "橙满集", "https://img/two", 5)
        };

        var result = PlatformPlaylistDeduplicator.Deduplicate(playlists);

        var item = Assert.Single(result);
        Assert.Equal("second", item.NativeId);
        Assert.Equal(5, item.TrackCount);
    }

    [Fact]
    public void DifferentPlatformsRemainSeparate()
    {
        var playlists = new PlatformUserPlaylist[]
        {
            new(PlatformId.QqMusic, "qq-1", "playlist", "同名歌单", "同一作者", "", 3),
            new(PlatformId.NetEaseMusic, "netease-1", "created", "同名歌单", "同一作者", "", 3)
        };

        var result = PlatformPlaylistDeduplicator.Deduplicate(playlists);

        Assert.Equal(2, result.Count);
    }
}
