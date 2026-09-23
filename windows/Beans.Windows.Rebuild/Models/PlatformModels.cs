using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Beans.Windows.Rebuild.Models;

public enum AuthorizationState
{
    Unknown,
    SignedOut,
    SigningIn,
    Authorized,
    Expired,
    Error
}

public sealed record MusicPlatformDescriptor(
    PlatformId Id,
    string DisplayName,
    string ShortDisplayName,
    string LogoGlyph,
    string BrandColor,
    bool IsEnabled,
    bool IsAvailable,
    AuthorizationState AuthorizationState,
    bool SupportsAnonymousDiscovery,
    bool SupportsDailyRecommendations,
    bool SupportsRankings,
    bool SupportsRecommendedPlaylists,
    bool SupportsPlaylistSquare,
    string SafeStatusText)
{
    public string StableId => Id.ToStableId();
    public string AuthorizationLabel => AuthorizationState switch
    {
        AuthorizationState.Authorized => "已授权",
        AuthorizationState.Expired => "登录已失效",
        AuthorizationState.SigningIn => "登录中",
        AuthorizationState.Error => "状态异常",
        AuthorizationState.SignedOut => SupportsAnonymousDiscovery ? "未登录 · 可浏览公开内容" : "未登录",
        _ => SafeStatusText
    };
}

public sealed record DiscoveryRequest(
    string PlatformId,
    string? Category = null,
    int Page = 1,
    bool ForceRefresh = false);

public sealed record DiscoveryHero(
    string Title,
    string Subtitle,
    string ImageUri,
    string ActionLabel,
    string? TrackId = null);

public sealed record RankingSummary(
    string Id,
    string Title,
    string UpdatedText,
    string ImageUri,
    IReadOnlyList<string> TopTracks,
    string PlatformId,
    string SourceBadgeText = "")
{
    public global::Beans.Windows.Rebuild.Models.PlatformId Platform =>
        PlatformIdExtensions.TryParseStableId(PlatformId, out var platform) ? platform : (global::Beans.Windows.Rebuild.Models.PlatformId)(-1);
}

public sealed record DiscoverySectionState(
    string SectionId,
    bool IsSuccess,
    PlatformErrorCode? ErrorCode,
    string SafeMessage,
    DiscoveryDataOrigin DataOrigin,
    DateTimeOffset LoadedAt,
    bool IsStale);

public sealed record PlatformDiscoveryContent(
    string PlatformId,
    DiscoveryHero HeroContent,
    IReadOnlyList<MusicTrack> DailyRecommendations,
    IReadOnlyList<RankingSummary> Rankings,
    IReadOnlyList<MusicPlaylist> RecommendedPlaylists,
    IReadOnlyList<MusicPlaylist> PlaylistSquare,
    IReadOnlyList<string> Categories,
    bool IsAuthorized,
    bool IsPartialSuccess,
    DateTimeOffset LoadedAt,
    string SafeMessage,
    DiscoveryDataOrigin DataOrigin = DiscoveryDataOrigin.Preview,
    bool IsStale = false,
    bool IsPreview = true,
    string SafeStatusText = "预览内容 · 在线服务尚未连接",
    IReadOnlyList<DiscoverySectionState>? SectionStates = null);

public sealed class PlatformDiscoveryState : INotifyPropertyChanged
{
    private PlatformDiscoveryContent? _content;
    private bool _isLoading;
    private bool _isRefreshing;
    private bool _isLoaded;
    private string? _errorState;
    private string _safeMessage = "准备加载";
    private string? _selectedCategory;
    private double _scrollOffset;
    private DateTimeOffset? _lastLoadedAt;
    private bool _isStale;

    public PlatformDiscoveryState(string platformId) => PlatformId = platformId;
    public string PlatformId { get; }
    public PlatformDiscoveryContent? Content { get => _content; private set => Set(ref _content, value); }
    public bool IsLoading { get => _isLoading; internal set => Set(ref _isLoading, value); }
    public bool IsRefreshing { get => _isRefreshing; internal set => Set(ref _isRefreshing, value); }
    public bool IsLoaded { get => _isLoaded; internal set => Set(ref _isLoaded, value); }
    public string? ErrorState { get => _errorState; internal set => Set(ref _errorState, value); }
    public string SafeMessage { get => _safeMessage; internal set => Set(ref _safeMessage, value); }
    public string? SelectedCategory { get => _selectedCategory; internal set => Set(ref _selectedCategory, value); }
    public double ScrollOffset { get => _scrollOffset; internal set => Set(ref _scrollOffset, value); }
    public DateTimeOffset? LastLoadedAt { get => _lastLoadedAt; internal set => Set(ref _lastLoadedAt, value); }
    public bool IsStale { get => _isStale; internal set => Set(ref _isStale, value); }
    public bool HasContent => Content is not null;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void SetContent(PlatformDiscoveryContent content)
    {
        Content = content;
        IsLoaded = true;
        LastLoadedAt = content.LoadedAt;
        IsStale = content.IsStale;
        ErrorState = null;
        SafeMessage = content.SafeStatusText;
        OnPropertyChanged(nameof(HasContent));
    }

    internal void SetError(string message, bool stale)
    {
        ErrorState = message;
        SafeMessage = message;
        IsStale = stale;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record PlatformRouteParameter(
    string PlatformId,
    string NativeId,
    string Title,
    string? NativeKind = null);
