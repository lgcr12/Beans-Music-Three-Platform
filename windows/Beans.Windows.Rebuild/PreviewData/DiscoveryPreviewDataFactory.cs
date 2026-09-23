using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.PreviewData;

internal static class DiscoveryPreviewDataFactory
{
    public static MusicPlaylist Playlist(PlatformId platform, string id, string title, string creator, string image, int count) =>
        new(new MusicIdentity(platform, id), title, creator, new Uri(image), count, "PreviewData");

    public static MusicTrack Track(PlatformId platform, string id, string title, string artist, string image) =>
        new(
            new MusicIdentity(platform, id),
            title,
            [new MusicArtist(new MusicIdentity(platform, $"artist-{id}"), artist, null, "PreviewData")],
            null,
            new Uri(image),
            TimeSpan.FromMinutes(4),
            AvailabilityState.LoginRequired,
            null,
            "PreviewData");
}
