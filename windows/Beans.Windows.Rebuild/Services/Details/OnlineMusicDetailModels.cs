using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Details;

public enum OnlineMusicDetailKind
{
    Playlist,
    Artist,
    Album,
    Ranking
}

public enum OnlineMusicDetailState
{
    Succeeded,
    Empty,
    InvalidRequest,
    Unsupported,
    Unauthorized,
    Error
}

public sealed record OnlineMusicDetailQuery(
    PlatformId Platform,
    OnlineMusicDetailKind Kind,
    string NativeId,
    string FallbackTitle = "",
    bool ForceRefresh = false);

public sealed record OnlineMusicCollectionItem(
    string NativeId,
    string Title,
    string Subtitle,
    string CoverUri,
    PlatformId Platform,
    OnlineMusicDetailKind Kind);

public sealed record OnlineMusicDetailContent(
    PlatformId Platform,
    OnlineMusicDetailKind Kind,
    string NativeId,
    string Title,
    string Subtitle,
    string Description,
    string CoverUri,
    IReadOnlyList<SearchResultItem> Tracks,
    IReadOnlyList<OnlineMusicCollectionItem> RelatedCollections,
    SearchDataOrigin DataOrigin,
    DateTimeOffset LoadedAt)
{
    public string PlatformDisplayName => Platform.ToDisplayName();
    public bool HasTracks => Tracks.Count > 0;
    public bool CanAttemptPlayback => Tracks.Any(track => track.ResultType == SearchResultType.Track && track.DataOrigin != SearchDataOrigin.Preview);
}

public sealed record OnlineMusicDetailResponse(
    OnlineMusicDetailState State,
    OnlineMusicDetailContent? Content,
    string SafeMessage,
    PlatformErrorCode? ErrorCode = null)
{
    public bool IsSuccess => State is OnlineMusicDetailState.Succeeded or OnlineMusicDetailState.Empty && Content is not null;

    public static OnlineMusicDetailResponse Success(OnlineMusicDetailContent content, string safeMessage = "详情已更新") =>
        new(content.Tracks.Count == 0 ? OnlineMusicDetailState.Empty : OnlineMusicDetailState.Succeeded, content, safeMessage);

    public static OnlineMusicDetailResponse Failure(OnlineMusicDetailState state, string safeMessage, PlatformErrorCode? errorCode = null) =>
        new(state, null, safeMessage, errorCode);
}

public interface IOnlineMusicDetailAdapter
{
    PlatformId Platform { get; }
    bool Supports(OnlineMusicDetailKind kind);
    Task<OnlineMusicDetailResponse> GetDetailAsync(OnlineMusicDetailQuery query, CancellationToken cancellationToken);
}
