using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Playback;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class PlaybackStatePolicyTests
{
    [Theory]
    [InlineData(42, true, 42, true)]
    [InlineData(42, false, 42, false)]
    [InlineData(-4, true, 0, true)]
    public void QualitySwitchCapturesPositionAndPlaybackState(double seconds, bool playing, double expectedSeconds, bool expectedPlaying)
    {
        var resume = QualitySwitchResume.Capture(TimeSpan.FromSeconds(seconds), playing);

        Assert.Equal(expectedSeconds, resume.Position.TotalSeconds);
        Assert.Equal(expectedPlaying, resume.ShouldPlay);
    }

    [Theory]
    [InlineData(50, 180, 50)]
    [InlineData(200, 180, 180)]
    [InlineData(50, 0, 0)]
    public void QualitySwitchClampsPositionToNewDuration(double position, double duration, double expected)
    {
        var resume = QualitySwitchResume.Capture(TimeSpan.FromSeconds(position), true);

        Assert.Equal(expected, resume.ClampTo(TimeSpan.FromSeconds(duration)).TotalSeconds);
    }

    [Fact]
    public void ReplaceQueueReplacesEvenWhenRequestedTrackIsAlreadyCurrent()
    {
        var current = CreateTrack("current");
        var stale = CreateTrack("stale");
        var queue = new List<PlaybackItem> { current, stale };

        PlaybackQueuePolicy.Replace(queue, [current]);

        Assert.Collection(queue, item => Assert.Same(current, item));
    }

    [Fact]
    public void CurrentQueueIndexUsesTrackIdWhenResolvedSourceFieldsDiffer()
    {
        var current = CreateTrack("track-b") with { SourceUri = "https://cdn.example/new-quality-b.mp3", QualityLabel = "无损" };
        var queue = new[] { CreateTrack("track-a"), CreateTrack("track-b"), CreateTrack("track-c") };

        Assert.Equal(1, PlaybackQueuePolicy.IndexOf(queue, current));
        Assert.Equal("track-c", queue[PlaybackQueuePolicy.IndexOf(queue, current) + 1].Id);
    }

    [Fact]
    public void MissingCurrentIdIsReportedInsteadOfFallingBackToQueueHead()
    {
        var queue = new[] { CreateTrack("track-a"), CreateTrack("track-b") };

        Assert.Equal(-1, PlaybackQueuePolicy.IndexOf(queue, "not-in-queue"));
    }

    [Theory]
    [InlineData(0, false, 1)]
    [InlineData(2, false, -1)]
    [InlineData(2, true, 0)]
    [InlineData(-1, true, -1)]
    public void NextIndexRespectsQueueBoundaryAndRepeatMode(int currentIndex, bool repeatAll, int expected)
    {
        var queue = new[] { CreateTrack("a"), CreateTrack("b"), CreateTrack("c") };

        Assert.Equal(expected < 0 ? null : expected,
            PlaybackQueuePolicy.NextIndex(queue, currentIndex, repeatAll));
    }

    [Fact]
    public void RepeatedQueueReplacementKeepsPlaylistOrderWithoutDuplicates()
    {
        var tracks = new[] { CreateTrack("a"), CreateTrack("b"), CreateTrack("c") };
        var queue = new List<PlaybackItem>();
        PlaybackQueuePolicy.Replace(queue, tracks);

        Assert.Equal(new[] { "a", "b", "c" }, queue.Select(item => item.Id));
    }

    [Fact]
    public void ShuffleBagDoesNotRepeatTracksUntilEveryQueuedTrackHasPlayed()
    {
        var tracks = new[] { CreateTrack("a"), CreateTrack("b"), CreateTrack("c"), CreateTrack("d") };
        var bag = new PlaybackShuffleBag();
        bag.Reset(tracks, "a");
        var played = new List<string> { "a" };

        while (bag.RemainingCount > 0)
            played.Add(bag.Take(tracks, played[^1]));

        Assert.Equal(tracks.Select(track => track.Id).Order(StringComparer.Ordinal), played.Order(StringComparer.Ordinal));
        Assert.Equal(played.Count, played.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ShuffleBagAddsNewQueueItemsToCurrentRound()
    {
        var tracks = new[] { CreateTrack("a"), CreateTrack("b"), CreateTrack("c") };
        var bag = new PlaybackShuffleBag();
        bag.Reset(tracks.Take(2), "a");
        bag.Add("c");

        var played = new[] { "a", bag.Take(tracks, "a") };

        Assert.Contains(played[1], new[] { "b", "c" });
        Assert.Equal(1, bag.RemainingCount);
    }

    [Fact]
    public void ShuffleBagDoesNotKeepDuplicateCandidatesWhenQueueItemsAreAddedAgain()
    {
        var tracks = new[] { CreateTrack("a"), CreateTrack("b"), CreateTrack("c") };
        var bag = new PlaybackShuffleBag();
        bag.Reset(tracks, "a");
        bag.Add("b");
        bag.Add("c");

        Assert.Equal(2, bag.RemainingCount);
        var played = new List<string>();
        while (bag.RemainingCount > 0)
            played.Add(bag.Take(tracks, played.Count == 0 ? "a" : played[^1]));
        Assert.Equal(2, played.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static PlaybackItem CreateTrack(string id) => new(
        id, id, "artist", "album", "", "https://audio.example/" + id,
        TimeSpan.FromMinutes(3));
}
