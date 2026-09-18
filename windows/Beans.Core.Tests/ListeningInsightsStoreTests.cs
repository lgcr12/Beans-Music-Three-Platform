using Beans.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Beans.Core.Tests;

public sealed class ListeningInsightsStoreTests
{
    [Fact]
    public async Task PlaysAndFavoritesSurviveStoreRecreation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"beans-insights-{Guid.NewGuid():N}.sqlite");
        try
        {
            var track = new LocalMusicTrack(
                "track-1",
                Path.Combine(Path.GetTempPath(), "song.mp3"),
                "测试歌曲",
                ".mp3",
                1024,
                DateTimeOffset.UtcNow);
            var first = new ListeningInsightsStore(path);
            await first.RecordPlayAsync(track);
            await first.RecordPlayAsync(track);
            Assert.True(await first.ToggleFavoriteAsync(track.Id));

            var reopened = new ListeningInsightsStore(path);
            var snapshot = await reopened.SnapshotAsync();

            var insight = Assert.Single(snapshot);
            Assert.Equal(2, insight.PlayCount);
            Assert.True(insight.IsFavorite);
            Assert.Equal(track.Title, insight.Track.Title);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
