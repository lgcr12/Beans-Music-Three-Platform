using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.PlatformLibrary;

public enum PlatformMembershipState
{
    Unknown,
    Inactive,
    Active
}

public enum PlatformLibraryState
{
    NotAuthorized,
    Succeeded,
    Empty,
    Partial,
    Error,
    Unsupported
}

public sealed record PlatformProbeSnapshot(
    PlatformId Platform,
    CredentialState CredentialState,
    string DisplayName,
    string? NativeUserId,
    PlatformMembershipState MembershipState,
    string MembershipLabel,
    DateTimeOffset CheckedAt,
    string SafeMessage);

public sealed record PlatformUserPlaylist(
    PlatformId Platform,
    string NativeId,
    string NativeKind,
    string Title,
    string Creator,
    string CoverUri,
    int? TrackCount,
    bool IsFavoriteCollection = false);

public sealed record PlatformPlaylistTrack(
    PlatformId Platform,
    string NativeId,
    string? ProviderMediaId,
    string Title,
    string Artist,
    string Album,
    string CoverUri,
    TimeSpan? Duration,
    string Quality,
    AvailabilityState Availability);

public sealed record PlatformLibrarySnapshot(
    PlatformId Platform,
    PlatformProbeSnapshot Probe,
    IReadOnlyList<PlatformUserPlaylist> Playlists,
    PlatformLibraryState State,
    SearchDataOrigin DataOrigin,
    DateTimeOffset LoadedAt,
    string SafeMessage,
    bool IsPartialSuccess = false);

public interface IPlatformLibraryAdapter
{
    PlatformId Platform { get; }

    Task<PlatformLibrarySnapshot> LoadAsync(bool forceRefresh, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlatformPlaylistTrack>> LoadPlaylistTracksAsync(
        PlatformUserPlaylist playlist,
        CancellationToken cancellationToken);
}

public interface IPlatformLibraryService
{
    Task<IReadOnlyList<PlatformLibrarySnapshot>> LoadAllAsync(
        bool forceRefresh,
        CancellationToken cancellationToken = default);

    Task<PlatformLibrarySnapshot> LoadAsync(
        PlatformId platform,
        bool forceRefresh,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlatformPlaylistTrack>> LoadPlaylistTracksAsync(
        PlatformUserPlaylist playlist,
        CancellationToken cancellationToken = default);
}
