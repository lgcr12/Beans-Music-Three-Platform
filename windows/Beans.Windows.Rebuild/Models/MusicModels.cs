namespace Beans.Windows.Rebuild.Models;

public enum PlatformId
{
    QqMusic,
    NetEaseMusic,
    KuGouMusic,
    Beans,
    Local
}

public static class PlatformIdExtensions
{
    public static string ToStableId(this PlatformId platform) => platform switch
    {
        PlatformId.QqMusic => "qq",
        PlatformId.NetEaseMusic => "netease",
        PlatformId.KuGouMusic => "kugou",
        PlatformId.Beans => "beans",
        PlatformId.Local => "local",
        _ => "unknown"
    };

    public static bool TryParseStableId(string? value, out PlatformId platform)
    {
        platform = value?.Trim().ToLowerInvariant() switch
        {
            "qq" => PlatformId.QqMusic,
            "netease" => PlatformId.NetEaseMusic,
            "kugou" => PlatformId.KuGouMusic,
            "beans" => PlatformId.Beans,
            "local" => PlatformId.Local,
            _ => default
        };
        return value?.Trim().ToLowerInvariant() is "qq" or "netease" or "kugou" or "beans" or "local";
    }

    public static string ToDisplayName(this PlatformId platform) => platform switch
    {
        PlatformId.QqMusic => "QQ 音乐",
        PlatformId.NetEaseMusic => "网易云音乐",
        PlatformId.KuGouMusic => "酷狗音乐",
        PlatformId.Beans => "Beans 歌单",
        PlatformId.Local => "本地音乐",
        _ => "未知来源"
    };
}

public readonly record struct MusicIdentity(PlatformId Platform, string NativeId);

public enum AvailabilityState
{
    Available,
    LoginRequired,
    SubscriptionRequired,
    RegionRestricted,
    Unavailable
}

public enum AudioQuality
{
    Standard,
    High,
    VeryHigh,
    Lossless,
    HiRes
}

public sealed record MusicArtist(MusicIdentity Identity, string Name, Uri? Artwork, object? PlatformPayload = null);

public sealed record MusicAlbum(
    MusicIdentity Identity,
    string Title,
    IReadOnlyList<MusicArtist> Artists,
    Uri? Cover,
    int TrackCount,
    object? PlatformPayload = null);

public sealed record MusicTrack(
    MusicIdentity Identity,
    string Title,
    IReadOnlyList<MusicArtist> Artists,
    MusicAlbum? Album,
    Uri? Cover,
    TimeSpan Duration,
    AvailabilityState Availability,
    AudioQuality? BestQuality,
    object? PlatformPayload = null,
    string? SafeErrorCode = null);

public sealed record MusicPlaylist(
    MusicIdentity Identity,
    string Title,
    string Creator,
    Uri? Cover,
    int? TrackCount,
    object? PlatformPayload = null,
    long? PlayCount = null,
    string? Category = null,
    bool IsPlayable = false,
    string SourceBadgeText = "",
    string? PayloadReference = null)
{
    public string CoverUri => Cover?.AbsoluteUri ?? string.Empty;
    public string TrackCountText => TrackCount?.ToString() ?? string.Empty;
}

public sealed record PlaylistDetail(MusicPlaylist Playlist, string Description, IReadOnlyList<MusicTrack> Tracks);
public sealed record RankingList(MusicIdentity Identity, string Title, Uri? Cover);
public sealed record RankingDetail(RankingList Ranking, IReadOnlyList<MusicTrack> Tracks, DateTimeOffset UpdatedAt);
public sealed record SearchResult(IReadOnlyList<MusicTrack> Tracks, IReadOnlyList<MusicAlbum> Albums, IReadOnlyList<MusicArtist> Artists, IReadOnlyList<MusicPlaylist> Playlists);
public sealed record LyricLine(TimeSpan Timestamp, string Text, string? Translation = null, IReadOnlyList<LyricWord>? Words = null);
public sealed record LyricWord(TimeSpan Start, TimeSpan Duration, string Text);
public sealed record LyricDocument(IReadOnlyList<LyricLine> Lines, bool IsInstrumental, TimeSpan Offset, PlatformId Source);
public sealed record PlaybackSource(Uri Uri, AudioQuality Quality, DateTimeOffset? ExpiresAt, IReadOnlyDictionary<string, string>? SafeHeaders = null);
public sealed record PlatformAccount(PlatformId Platform, string DisplayName, Uri? Avatar, CredentialState CredentialState);
public enum CredentialState { NotAuthorized, Valid, Expiring, Expired, Checking, Error }
public sealed record DownloadTask(string Id, MusicTrack Track, string FilePath, long BytesReceived, long? TotalBytes, string State);
public sealed record LocalTrack(MusicIdentity Identity, string FilePath, string Title, string Artist, string Album, TimeSpan Duration, string Format, int? SampleRate, Uri? Cover);
public sealed record PlayHistoryItem(MusicTrack Track, DateTimeOffset PlayedAt, TimeSpan PlayedDuration);
public sealed record QueueItem(string Id, MusicTrack Track, DateTimeOffset AddedAt);
public sealed record UserMusicStatistics(long TotalMinutes, int FavoriteCount, IReadOnlyList<MusicArtist> TopArtists);
public sealed record PlatformHomeContent(IReadOnlyList<MusicPlaylist> FeaturedPlaylists, IReadOnlyList<RankingList> Rankings);
public sealed record PlatformComment(string Id, string Author, string Content, DateTimeOffset CreatedAt, int LikeCount);
