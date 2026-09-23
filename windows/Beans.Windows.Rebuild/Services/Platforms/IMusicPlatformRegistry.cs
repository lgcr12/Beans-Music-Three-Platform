using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Platforms;

public interface IMusicPlatformRegistry
{
    PlatformId? CurrentPlatformId { get; }
    IReadOnlyList<IMusicPlatformService> EnabledPlatforms { get; }
    IReadOnlyList<MusicPlatformDescriptor> GetAllPlatforms();
    IReadOnlyList<MusicPlatformDescriptor> GetEnabledPlatforms();
    MusicPlatformDescriptor? GetPlatform(string platformId);
    MusicPlatformDescriptor? GetCurrentPlatform();
    IMusicPlatformService? Get(PlatformId platformId);
    void SetCurrent(PlatformId platformId);
    void SetCurrentPlatform(string platformId);
    bool IsEnabled(string platformId);
    void SetEnabled(string platformId, bool enabled);
    AuthorizationState GetAuthorizationState(string platformId);
    Task<AuthorizationState> RefreshAuthorizationStateAsync(string platformId, CancellationToken cancellationToken);
    event EventHandler<PlatformStateChangedEventArgs>? PlatformStateChanged;
    event EventHandler<CurrentPlatformChangedEventArgs>? CurrentPlatformChanged;
    Task<IReadOnlyDictionary<PlatformId, PlatformSearchOutcome>> SearchAllAsync(string query, int page, int pageSize, CancellationToken cancellationToken);
}

public sealed record PlatformStateChangedEventArgs(string PlatformId, MusicPlatformDescriptor Descriptor);
public sealed record CurrentPlatformChangedEventArgs(string PreviousPlatformId, string CurrentPlatformId);

public sealed record PlatformSearchOutcome(SearchResult? Result, string? SafeErrorCode)
{
    public bool IsSuccess => Result is not null;
}
