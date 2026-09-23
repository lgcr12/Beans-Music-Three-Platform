using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Library;
using Beans.Windows.Rebuild.Services.LocalMusic;
using LocalIndexedTrack = Beans.Windows.Rebuild.Services.LocalMusic.LocalTrack;

namespace Beans.Windows.Rebuild.Services.MusicUniverse;

public static class MusicUniverseAnalyzer
{
    private const int MaximumInsightsPerSection = 12;
    private const int MaximumRelationships = 16;

    public static MusicUniverseSnapshot Build(
        UserLibrarySnapshot library,
        IReadOnlyList<LocalIndexedTrack> localTracks)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(localTracks);

        var evidence = new Dictionary<string, Evidence>(StringComparer.Ordinal);
        foreach (var track in localTracks.Where(track => !track.IsMissing))
        {
            var item = new LibraryMediaSnapshot(
                LibraryItemKind.Track,
                PlatformId.Local,
                track.Id,
                Clean(track.Title),
                Clean(track.Artist, track.AlbumArtist),
                Clean(track.Album),
                track.ArtworkUri,
                (long)Math.Max(0, track.Duration.TotalMilliseconds),
                track.Format,
                SearchDataOrigin.Live);
            Merge(evidence, item, isIndexed: true, isFavorite: false, isHistory: track.LastPlayedAt is not null, playCount: track.PlayCount);
        }

        foreach (var favorite in library.Favorites)
            Merge(evidence, favorite.Item, isIndexed: false, isFavorite: true, isHistory: false, playCount: 0);

        foreach (var history in library.RecentHistory)
            Merge(evidence, history.Item, isIndexed: false, isFavorite: false, isHistory: true, playCount: history.PlayCount);

        var values = evidence.Values.ToArray();
        var trackEvidence = values.Where(item => item.Item.Kind == LibraryItemKind.Track).ToArray();
        var artistInsights = BuildArtists(values);
        var albumInsights = BuildAlbums(values);
        var sourceInsights = BuildSources(values);
        var relationships = BuildRelationships(values);
        var indexedTrackCount = localTracks.Count(track => !track.IsMissing);
        var favoriteCount = library.Favorites.Count;
        var historyCount = library.RecentHistory.Count;
        var missingArtists = trackEvidence.Count(item => string.IsNullOrWhiteSpace(item.Item.Artist));
        var missingAlbums = trackEvidence.Count(item => string.IsNullOrWhiteSpace(item.Item.Album));
        var totalPlayCount = values.Sum(item => item.PlayCount);

        return new MusicUniverseSnapshot(
            [
                new("真实歌曲", $"{trackEvidence.Length} 首", "按平台和原生歌曲标识去重"),
                new("歌手关系", $"{artistInsights.Count} 位", missingArtists == 0 ? "仅统计有歌手元数据的内容" : $"另有 {missingArtists} 首缺少歌手元数据"),
                new("专辑关系", $"{albumInsights.Count} 张", missingAlbums == 0 ? "仅统计有专辑元数据的内容" : $"另有 {missingAlbums} 首缺少专辑元数据"),
                new("播放证据", $"{totalPlayCount} 次", $"来自 {historyCount} 条去重播放记录")
            ],
            artistInsights.Take(MaximumInsightsPerSection).ToArray(),
            albumInsights.Take(MaximumInsightsPerSection).ToArray(),
            sourceInsights,
            relationships.Take(MaximumRelationships).ToArray(),
            indexedTrackCount,
            favoriteCount,
            historyCount,
            missingArtists,
            missingAlbums,
            library.RecoveryStatus,
            library.LoadedAt);
    }

    private static IReadOnlyList<MusicUniverseArtistInsight> BuildArtists(IReadOnlyList<Evidence> evidence) =>
        evidence
            .SelectMany(item => ArtistNames(item.Item).Select(name => (Name: name, Evidence: item)))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.Select(item => item.Evidence).DistinctBy(item => item.Item.StableKey).ToArray();
                var albums = items.Select(item => Clean(item.Item.Album)).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                var sources = items.Select(item => item.Item.Platform).Distinct().OrderBy(SourceOrder).ToArray();
                return new MusicUniverseArtistInsight(
                    group.First().Name,
                    items.Count(item => item.Item.Kind == LibraryItemKind.Track),
                    albums,
                    items.Count(item => item.IsFavorite),
                    items.Sum(item => item.PlayCount),
                    sources.Length,
                    string.Join("、", sources.Select(platform => platform.ToDisplayName())));
            })
            .OrderByDescending(item => item.PlayCount)
            .ThenByDescending(item => item.FavoriteCount)
            .ThenByDescending(item => item.TrackCount)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private static IReadOnlyList<MusicUniverseAlbumInsight> BuildAlbums(IReadOnlyList<Evidence> evidence) =>
        evidence
            .SelectMany(item => AlbumKeys(item.Item).Select(key => (Key: key, Evidence: item)))
            .GroupBy(item => item.Key, AlbumKeyComparer.Instance)
            .Select(group =>
            {
                var items = group.Select(item => item.Evidence).DistinctBy(item => item.Item.StableKey).ToArray();
                var sources = items.Select(item => item.Item.Platform).Distinct().OrderBy(SourceOrder).ToArray();
                return new MusicUniverseAlbumInsight(
                    group.Key.Title,
                    group.Key.Artist,
                    items.Count(item => item.Item.Kind == LibraryItemKind.Track),
                    items.Count(item => item.IsFavorite),
                    items.Sum(item => item.PlayCount),
                    sources.Length,
                    string.Join("、", sources.Select(platform => platform.ToDisplayName())));
            })
            .OrderByDescending(item => item.PlayCount)
            .ThenByDescending(item => item.FavoriteCount)
            .ThenByDescending(item => item.TrackCount)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private static IReadOnlyList<MusicUniverseSourceInsight> BuildSources(IReadOnlyList<Evidence> evidence) =>
        evidence
            .GroupBy(item => item.Item.Platform)
            .Select(group => new MusicUniverseSourceInsight(
                group.Key,
                group.Key.ToDisplayName(),
                group.Count(),
                group.Count(item => item.Item.Kind == LibraryItemKind.Track),
                group.Count(item => item.IsFavorite),
                group.Count(item => item.IsHistory),
                group.Sum(item => item.PlayCount),
                group.SelectMany(item => ArtistNames(item.Item)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                group.SelectMany(item => AlbumKeys(item.Item)).Distinct(AlbumKeyComparer.Instance).Count()))
            .OrderBy(item => SourceOrder(item.Platform))
            .ToArray();

    private static IReadOnlyList<MusicUniverseRelationship> BuildRelationships(IReadOnlyList<Evidence> evidence)
    {
        var artistAlbums = evidence
            .Where(item => item.Item.Kind == LibraryItemKind.Track)
            .SelectMany(item => AlbumKeys(item.Item).SelectMany(album => ArtistNames(item.Item).Select(artist => (Artist: artist, Album: album.Title, Evidence: item))))
            .GroupBy(item => (Artist: item.Artist.ToUpperInvariant(), Album: item.Album.ToUpperInvariant()))
            .Select(group =>
            {
                var first = group.First();
                var count = group.Select(item => item.Evidence.Item.StableKey).Distinct(StringComparer.Ordinal).Count();
                return new MusicUniverseRelationship(first.Artist, "收录于", first.Album, count, "由歌曲的歌手与专辑元数据建立");
            });

        var sourceArtists = evidence
            .SelectMany(item => ArtistNames(item.Item).Select(artist => (Artist: artist, Evidence: item)))
            .GroupBy(item => (item.Evidence.Item.Platform, Artist: item.Artist.ToUpperInvariant()))
            .Select(group =>
            {
                var first = group.First();
                var count = group.Select(item => item.Evidence.Item.StableKey).Distinct(StringComparer.Ordinal).Count();
                return new MusicUniverseRelationship(first.Evidence.Item.Platform.ToDisplayName(), "包含歌手", first.Artist, count, "由本地索引、收藏或播放记录建立");
            });

        return artistAlbums.Concat(sourceArtists)
            .OrderByDescending(item => item.EvidenceCount)
            .ThenBy(item => item.Left, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Right, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> ArtistNames(LibraryMediaSnapshot item)
    {
        var value = item.Kind == LibraryItemKind.Artist ? item.Title : item.Artist;
        value = Clean(value);
        if (value.Length > 0) yield return value;
    }

    private static IEnumerable<AlbumKey> AlbumKeys(LibraryMediaSnapshot item)
    {
        var title = Clean(item.Kind == LibraryItemKind.Album ? item.Title : item.Album);
        if (title.Length == 0) yield break;
        yield return new AlbumKey(title, Clean(item.Artist));
    }

    private static void Merge(
        IDictionary<string, Evidence> target,
        LibraryMediaSnapshot item,
        bool isIndexed,
        bool isFavorite,
        bool isHistory,
        int playCount)
    {
        if (item.DataOrigin == SearchDataOrigin.Preview || string.IsNullOrWhiteSpace(item.NativeId) || string.IsNullOrWhiteSpace(item.Title)) return;
        var safePlayCount = Math.Max(0, playCount);
        if (!target.TryGetValue(item.StableKey, out var existing))
        {
            target[item.StableKey] = new Evidence(item, isIndexed, isFavorite, isHistory, safePlayCount);
            return;
        }

        target[item.StableKey] = existing with
        {
            Item = PreferMetadata(existing.Item, item),
            IsIndexed = existing.IsIndexed || isIndexed,
            IsFavorite = existing.IsFavorite || isFavorite,
            IsHistory = existing.IsHistory || isHistory,
            PlayCount = Math.Max(existing.PlayCount, safePlayCount)
        };
    }

    private static LibraryMediaSnapshot PreferMetadata(LibraryMediaSnapshot left, LibraryMediaSnapshot right) => left with
    {
        Title = Choose(left.Title, right.Title),
        Artist = Choose(left.Artist, right.Artist),
        Album = Choose(left.Album, right.Album),
        CoverUri = Choose(left.CoverUri, right.CoverUri),
        DurationMilliseconds = left.DurationMilliseconds ?? right.DurationMilliseconds,
        Quality = Choose(left.Quality, right.Quality)
    };

    private static string Choose(string left, string right) => string.IsNullOrWhiteSpace(left) ? Clean(right) : Clean(left);
    private static string Clean(string? value, string? fallback = null) => string.IsNullOrWhiteSpace(value) ? fallback?.Trim() ?? string.Empty : value.Trim();
    private static int SourceOrder(PlatformId platform) => platform switch
    {
        PlatformId.Local => 0,
        PlatformId.QqMusic => 1,
        PlatformId.NetEaseMusic => 2,
        PlatformId.Beans => 3,
        _ => 4
    };

    private sealed record Evidence(
        LibraryMediaSnapshot Item,
        bool IsIndexed,
        bool IsFavorite,
        bool IsHistory,
        int PlayCount);

    private sealed record AlbumKey(string Title, string Artist);

    private sealed class AlbumKeyComparer : IEqualityComparer<AlbumKey>
    {
        public static AlbumKeyComparer Instance { get; } = new();

        public bool Equals(AlbumKey? x, AlbumKey? y) => x is not null && y is not null &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Title, y.Title) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Artist, y.Artist);

        public int GetHashCode(AlbumKey value) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Title),
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Artist));
    }
}
