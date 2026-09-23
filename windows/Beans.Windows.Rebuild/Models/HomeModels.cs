namespace Beans.Windows.Rebuild.Models;

public sealed record HomePlaylist(
    string Id,
    string Title,
    string Subtitle,
    string ImageUri,
    string PlayCount,
    PlatformId SourcePlatform);

public sealed record HomeTrack(
    string Id,
    int Index,
    string Title,
    string Artist,
    string Album,
    string Duration,
    string ImageUri,
    PlatformId SourcePlatform,
    SearchResultItem PlaybackTarget,
    bool IsCurrent = false);

public sealed record HomeRecommendation(
    string Id,
    string Title,
    string Artist,
    string ImageUri,
    PlatformId SourcePlatform,
    SearchResultItem PlaybackTarget);

public sealed record HomeRouteTarget(string Route, PlatformRouteParameter Parameter);
