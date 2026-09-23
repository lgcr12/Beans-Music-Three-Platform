using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Platforms;
using Beans.Windows.Rebuild.Services.Platforms.QQ;

namespace Beans.Windows.Rebuild.ViewModels;

public sealed class DiscoverViewModel : IDisposable
{
    private readonly IMusicPlatformRegistry _registry;
    private readonly IMusicDiscoveryService _discovery;
    private readonly Dictionary<string, PlatformDiscoveryState> _states;
    private CancellationTokenSource? _requestCancellation;
    private long _requestVersion;

    public DiscoverViewModel(IMusicPlatformRegistry registry, IMusicDiscoveryService discovery)
    {
        _registry = registry;
        _discovery = discovery;
        Platforms = registry.GetEnabledPlatforms()
            .Where(platform => platform.Id is PlatformId.QqMusic or PlatformId.NetEaseMusic)
            .ToArray();
        _states = Platforms.ToDictionary(platform => platform.StableId, platform => new PlatformDiscoveryState(platform.StableId));
        var current = registry.GetCurrentPlatform();
        CurrentPlatformId = current is not null && _states.ContainsKey(current.StableId) ? current.StableId : Platforms.First(platform => platform.IsEnabled).StableId;
    }

    public IReadOnlyList<MusicPlatformDescriptor> Platforms { get; }
    public string CurrentPlatformId { get; private set; }
    public MusicPlatformDescriptor CurrentPlatform => _registry.GetPlatform(CurrentPlatformId)!;
    public PlatformDiscoveryState CurrentState => _states[CurrentPlatformId];
    public PlatformDiscoveryContent? Content => CurrentState.Content;
    public IReadOnlyDictionary<string, PlatformDiscoveryState> States => _states;

    public string Subtitle => CurrentPlatformId switch
    {
        "qq" => "发现 QQ 音乐中的热门歌曲与精选歌单",
        "netease" => "发现网易云音乐中的公开歌单与热门榜单",
        _ => "发现不同平台的独立内容"
    };

    public string StatusText
    {
        get
        {
            if (CurrentState.Content is not { } content)
                return $"{CurrentPlatform.DisplayName} · {CurrentState.SafeMessage}";

            return content.DataOrigin switch
            {
                DiscoveryDataOrigin.Live => $"{CurrentPlatform.DisplayName} · 公开内容 · 更新于 {content.LoadedAt:HH:mm}",
                DiscoveryDataOrigin.CacheFresh => $"{CurrentPlatform.DisplayName} · 来自缓存 · 更新于 {content.LoadedAt:HH:mm}",
                DiscoveryDataOrigin.CacheStale => $"{CurrentPlatform.DisplayName} · 网络不可用，正在显示上次内容",
                _ => $"{CurrentPlatform.DisplayName} · 预览内容 · 在线服务尚未连接"
            };
        }
    }

    public string DailyRecommendationStatusText
    {
        get
        {
            var daily = CurrentState.Content?.SectionStates?.FirstOrDefault(section => section.SectionId == "daily-recommendations");
            return (CurrentPlatformId, daily?.ErrorCode) switch
            {
                ("netease", PlatformErrorCode.Unauthorized) => "登录网易云音乐后查看每日推荐",
                ("qq", PlatformErrorCode.Unauthorized) => "登录 QQ 音乐后查看每日推荐",
                ("qq", PlatformErrorCode.Unsupported) => "当前版本暂不支持 QQ 音乐每日推荐",
                _ => string.Empty
            };
        }
    }

    public event EventHandler? StateChanged;

    public Task InitializeAsync() => LoadCurrentAsync(false);

    public async Task SwitchPlatformAsync(string platformId)
    {
        if (!_states.ContainsKey(platformId) || !_registry.IsEnabled(platformId) || CurrentPlatformId == platformId) return;
        CancelCurrentRequest();
        _registry.SetCurrentPlatform(platformId);
        CurrentPlatformId = platformId;
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (CurrentState.IsLoaded && !CurrentState.IsStale) return;
        await LoadCurrentAsync(false);
    }

    public async Task RefreshAsync()
    {
        await LoadCurrentAsync(true);
    }

    public async Task SelectCategoryAsync(string category)
    {
        if (string.IsNullOrWhiteSpace(category) || CurrentState.SelectedCategory == category) return;
        var platformId = CurrentPlatformId;
        var state = CurrentState;
        state.SelectedCategory = category;
        var version = BeginRequest(state, true);
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var items = await _discovery.GetPlaylistSquareAsync(platformId, category, 1, _requestCancellation!.Token);
            if (!IsCurrent(version, platformId) || state.Content is null) return;
            state.SetContent(state.Content with { PlaylistSquare = items });
        }
        catch (OperationCanceledException) { }
        catch { if (IsCurrent(version, platformId)) state.SetError("歌单分类暂时无法刷新", state.HasContent); }
        finally
        {
            if (IsCurrent(version, platformId))
            {
                state.IsRefreshing = false;
                state.IsLoading = false;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public void SaveScrollOffset(double offset) => CurrentState.ScrollOffset = Math.Max(0, offset);

    private async Task LoadCurrentAsync(bool forceRefresh)
    {
        var platformId = CurrentPlatformId;
        var state = CurrentState;
        var version = BeginRequest(state, forceRefresh || state.HasContent);
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var request = new DiscoveryRequest(platformId, state.SelectedCategory, 1, forceRefresh);
            var content = await _discovery.GetDiscoveryAsync(platformId, request, _requestCancellation!.Token);
            if (!IsCurrent(version, platformId)) return;
            state.SelectedCategory ??= content.Categories.FirstOrDefault();
            state.SetContent(content);
        }
        catch (OperationCanceledException) { }
        catch (PreviewContentUnavailableException) { if (IsCurrent(version, platformId)) state.SetError("在线服务尚未连接", state.HasContent); }
        catch (PlatformDiscoveryUnavailableException exception) { if (IsCurrent(version, platformId)) state.SetError(exception.Message, state.HasContent); }
        catch { if (IsCurrent(version, platformId)) state.SetError("暂时无法加载该平台内容，请稍后重试", state.HasContent); }
        finally
        {
            if (IsCurrent(version, platformId))
            {
                state.IsLoading = false;
                state.IsRefreshing = false;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private long BeginRequest(PlatformDiscoveryState state, bool refreshing)
    {
        CancelCurrentRequest();
        _requestCancellation = new CancellationTokenSource();
        var version = ++_requestVersion;
        state.IsRefreshing = refreshing;
        state.IsLoading = !state.HasContent;
        state.ErrorState = null;
        return version;
    }

    private bool IsCurrent(long version, string platformId) => version == _requestVersion && platformId == CurrentPlatformId;

    private void CancelCurrentRequest()
    {
        _requestCancellation?.Cancel();
        _requestCancellation?.Dispose();
        _requestCancellation = null;
    }

    public void Dispose() => CancelCurrentRequest();
}
