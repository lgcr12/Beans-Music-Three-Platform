using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Library;

public enum LibraryItemKind
{
    Track,
    Playlist,
    Album,
    Artist
}

public enum LibraryRecoveryStatus
{
    Normal,
    RestoredFromBackup,
    ResetAfterCorruption
}

/// <summary>
/// A deliberately small, provider-neutral metadata snapshot. Playback addresses,
/// provider payloads, cookies and credentials are not part of this contract and
/// therefore cannot be persisted by the user-library store.
/// </summary>
public sealed record LibraryMediaSnapshot(
    LibraryItemKind Kind,
    PlatformId Platform,
    string NativeId,
    string Title,
    string Artist = "",
    string Album = "",
    string CoverUri = "",
    long? DurationMilliseconds = null,
    string Quality = "未知",
    SearchDataOrigin DataOrigin = SearchDataOrigin.Live)
{
    public string StableKey => $"{Platform.ToStableId()}:{Kind.ToString().ToLowerInvariant()}:{NativeId}";
    public string PlatformName => Platform.ToDisplayName();
    public string DurationText => DurationMilliseconds is { } value && value >= 0
        ? TimeSpan.FromMilliseconds(value).ToString(value >= 3_600_000 ? @"h\:mm\:ss" : @"mm\:ss")
        : "";

    public static bool TryCreate(SearchResultItem item, out LibraryMediaSnapshot? snapshot)
    {
        snapshot = null;
        if (item.DataOrigin == SearchDataOrigin.Preview || string.IsNullOrWhiteSpace(item.NativeId) || string.IsNullOrWhiteSpace(item.Title))
            return false;

        var kind = item.ResultType switch
        {
            SearchResultType.Track => LibraryItemKind.Track,
            SearchResultType.Playlist => LibraryItemKind.Playlist,
            SearchResultType.Album => LibraryItemKind.Album,
            SearchResultType.Artist => LibraryItemKind.Artist,
            _ => LibraryItemKind.Track
        };
        snapshot = new LibraryMediaSnapshot(
            kind,
            item.Platform,
            item.NativeId,
            item.Title,
            item.Artist,
            item.Album,
            item.CoverUri,
            item.Duration is { } duration ? (long)duration.TotalMilliseconds : null,
            item.Quality,
            item.DataOrigin);
        return true;
    }

    public bool TryCreateSearchResult(out SearchResultItem? item)
    {
        item = null;
        if (Kind != LibraryItemKind.Track || DataOrigin == SearchDataOrigin.Preview || Platform is not (PlatformId.QqMusic or PlatformId.NetEaseMusic))
            return false;

        item = new SearchResultItem(
            SearchResultType.Track,
            Platform,
            NativeId,
            StableKey,
            Title,
            Subtitle: string.Join(" · ", new[] { Artist, Album }.Where(value => !string.IsNullOrWhiteSpace(value))),
            Artist: Artist,
            Album: Album,
            CoverUri: CoverUri,
            Duration: DurationMilliseconds is { } milliseconds ? TimeSpan.FromMilliseconds(milliseconds) : null,
            Quality: Quality,
            IsPlayable: false,
            RestrictionState: "需要平台授权并解析播放地址",
            SourceDisplayName: PlatformName,
            SourceBadgeText: PlatformName,
            DataOrigin: DataOrigin,
            PayloadReference: StableKey,
            PlaybackUri: null);
        return true;
    }
}

public sealed record FavoriteEntry(LibraryMediaSnapshot Item, DateTimeOffset AddedAt)
{
    public string StableKey => Item.StableKey;
    public string Title => Item.Title;
    public string Artist => Item.Artist;
    public string Album => Item.Album;
    public string CoverUri => Item.CoverUri;
    public string PlatformName => Item.PlatformName;
    public string KindText => Item.Kind switch
    {
        LibraryItemKind.Track => "歌曲",
        LibraryItemKind.Playlist => "歌单",
        LibraryItemKind.Album => "专辑",
        LibraryItemKind.Artist => "歌手",
        _ => "内容"
    };
    public string AddedAtText => AddedAt.LocalDateTime.ToString("g");
}

public sealed record PlaybackHistoryEntry(
    LibraryMediaSnapshot Item,
    DateTimeOffset LastPlayedAt,
    long LastPlayedDurationMilliseconds,
    int PlayCount)
{
    public string StableKey => Item.StableKey;
    public string Title => Item.Title;
    public string Artist => Item.Artist;
    public string Album => Item.Album;
    public string CoverUri => Item.CoverUri;
    public string PlatformName => Item.PlatformName;
    public string DurationText => Item.DurationText;
    public string LastPlayedAtText => LastPlayedAt.LocalDateTime.ToString("g");
    public string PlaybackBoundary => Item.Platform == PlatformId.Local
        ? "请在本地音乐页播放"
        : "播放时解析最新地址";
}

public sealed record UserLibrarySnapshot(
    IReadOnlyList<FavoriteEntry> Favorites,
    IReadOnlyList<PlaybackHistoryEntry> RecentHistory,
    LibraryRecoveryStatus RecoveryStatus,
    DateTimeOffset LoadedAt);

public sealed record UserLibraryOptions(
    string? StoragePath = null,
    int MaximumFavorites = 2_000,
    int MaximumHistoryEntries = 200)
{
    public string EffectiveStoragePath => StoragePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BeansMusic", "Rebuild", "user-library.json");
}

public interface IUserLibraryService
{
    Task<UserLibrarySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<bool> IsFavoriteAsync(LibraryItemKind kind, PlatformId platform, string nativeId, CancellationToken cancellationToken = default);
    Task<bool> SetFavoriteAsync(LibraryMediaSnapshot item, bool isFavorite, CancellationToken cancellationToken = default);
    Task RecordPlaybackAsync(LibraryMediaSnapshot item, TimeSpan playedDuration, CancellationToken cancellationToken = default);
    Task ClearPlaybackHistoryAsync(CancellationToken cancellationToken = default);
}
