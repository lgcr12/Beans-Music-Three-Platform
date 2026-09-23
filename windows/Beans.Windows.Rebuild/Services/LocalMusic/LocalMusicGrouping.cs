namespace Beans.Windows.Rebuild.Services.LocalMusic;

public enum LocalMusicGroupKind
{
    Album,
    Artist
}

public sealed record LocalMusicGroup(
    string Id,
    LocalMusicGroupKind Kind,
    string Name,
    string SecondaryText,
    string ArtworkUri,
    IReadOnlyList<LocalTrack> Tracks)
{
    public int AvailableTrackCount => Tracks.Count(track => !track.IsMissing);
    public int MissingTrackCount => Tracks.Count - AvailableTrackCount;
    public string TrackCountText => MissingTrackCount == 0
        ? $"{AvailableTrackCount} 首"
        : $"{AvailableTrackCount} 首可用 · {MissingTrackCount} 首文件缺失";
    public string DurationText
    {
        get
        {
            var duration = TimeSpan.FromTicks(Tracks.Where(track => !track.IsMissing).Sum(track => track.Duration.Ticks));
            return duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
        }
    }
    public string SummaryText => string.Join(" · ", Tracks.Take(3).Select(track => track.Title));
}

public static class LocalMusicGrouping
{
    private const string UnknownAlbum = "未知专辑";
    private const string UnknownArtist = "未知艺术家";
    private const string DefaultArtwork = "ms-appx:///Assets/Branding/beans-icon.png";

    public static IReadOnlyList<LocalMusicGroup> ByAlbum(IEnumerable<LocalTrack> tracks) =>
        tracks
            .GroupBy(
                track => new AlbumKey(ValueOrFallback(track.Album, UnknownAlbum), AlbumContributor(track)),
                AlbumKeyComparer.Instance)
            .Select(group =>
            {
                var ordered = OrderAlbumTracks(group).ToArray();
                return new LocalMusicGroup(
                    $"album:{NormalizeKey(group.Key.Name)}:{NormalizeKey(group.Key.Contributor)}",
                    LocalMusicGroupKind.Album,
                    group.Key.Name,
                    group.Key.Contributor,
                    Artwork(ordered),
                    ordered);
            })
            .OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(group => group.SecondaryText, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    public static IReadOnlyList<LocalMusicGroup> ByArtist(IEnumerable<LocalTrack> tracks) =>
        tracks
            .GroupBy(TrackArtist, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group
                    .OrderBy(track => ValueOrFallback(track.Album, UnknownAlbum), StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(track => track.DiscNumber ?? int.MaxValue)
                    .ThenBy(track => track.TrackNumber ?? int.MaxValue)
                    .ThenBy(track => track.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                var albumCount = ordered
                    .Select(track => ValueOrFallback(track.Album, UnknownAlbum))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                return new LocalMusicGroup(
                    $"artist:{NormalizeKey(group.Key)}",
                    LocalMusicGroupKind.Artist,
                    group.Key,
                    $"{albumCount} 张专辑",
                    Artwork(ordered),
                    ordered);
            })
            .OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private static IEnumerable<LocalTrack> OrderAlbumTracks(IEnumerable<LocalTrack> tracks) => tracks
        .OrderBy(track => track.DiscNumber ?? int.MaxValue)
        .ThenBy(track => track.TrackNumber ?? int.MaxValue)
        .ThenBy(track => track.Title, StringComparer.CurrentCultureIgnoreCase);

    private static string AlbumContributor(LocalTrack track) =>
        ValueOrFallback(
            string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist,
            UnknownArtist);

    private static string TrackArtist(LocalTrack track) =>
        ValueOrFallback(
            string.IsNullOrWhiteSpace(track.Artist) ? track.AlbumArtist : track.Artist,
            UnknownArtist);

    private static string Artwork(IEnumerable<LocalTrack> tracks) => tracks
        .Select(track => track.ArtworkUri)
        .FirstOrDefault(uri => !string.IsNullOrWhiteSpace(uri)) ?? DefaultArtwork;

    private static string ValueOrFallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string NormalizeKey(string value) => value.Trim().ToUpperInvariant();

    private sealed record AlbumKey(string Name, string Contributor);

    private sealed class AlbumKeyComparer : IEqualityComparer<AlbumKey>
    {
        public static AlbumKeyComparer Instance { get; } = new();

        public bool Equals(AlbumKey? x, AlbumKey? y) =>
            x is not null && y is not null &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Contributor, y.Contributor);

        public int GetHashCode(AlbumKey value) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name),
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Contributor));
    }
}
