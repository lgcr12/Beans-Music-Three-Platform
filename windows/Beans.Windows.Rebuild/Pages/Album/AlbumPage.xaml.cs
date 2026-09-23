using System.ComponentModel;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Pages.Artist;
using Beans.Windows.Rebuild.Services.Details;
using Beans.Windows.Rebuild.Services.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beans.Windows.Rebuild.Pages.Album;

public sealed partial class AlbumPage : UserControl, INotifyPropertyChanged
{
    private readonly IPlaybackService? _player;
    private readonly IOnlineMusicDetailService? _detailsService;
    private readonly PlatformRouteParameter? _route;
    private CancellationTokenSource? _loadCancellation;
    private bool _loaded;
    public string Title { get; private set; }
    public string Artist { get; private set; } = "";
    public string CoverUri { get; private set; } = "ms-appx:///Assets/Branding/beans-icon.png";
    public PlatformId Platform { get; private set; }
    public IReadOnlyList<DetailTrackPreview> Tracks { get; private set; } = [];
    private string _statusText = "准备加载专辑详情";
    public string StatusText { get => _statusText; private set { if (_statusText == value) return; _statusText = value; PropertyChanged?.Invoke(this, new(nameof(StatusText))); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public AlbumPage(PlatformRouteParameter? route = null) : this(route, null, null) { }

    public AlbumPage(PlatformRouteParameter? route, IPlaybackService? player) : this(route, player, null) { }

    public AlbumPage(PlatformRouteParameter? route, IPlaybackService? player, IOnlineMusicDetailService? detailsService)
    {
        _player = player;
        _route = route;
        _detailsService = detailsService;
        Title = string.IsNullOrWhiteSpace(route?.Title) ? "专辑详情" : route.Title;
        Platform = PlatformIdExtensions.TryParseStableId(route?.PlatformId ?? string.Empty, out var parsed) ? parsed : PlatformId.Beans;
        InitializeComponent();
        Loaded += Page_Loaded;
        Unloaded += (_, _) => _loadCancellation?.Cancel();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        if (_route is null || Platform is not (PlatformId.QqMusic or PlatformId.NetEaseMusic)) { StatusText = "该专辑没有可用的在线平台详情标识"; return; }
        if (_detailsService is null) { StatusText = "在线详情服务尚未连接"; return; }
        _loadCancellation = new CancellationTokenSource();
        StatusText = "正在加载真实专辑详情…";
        OnlineMusicDetailResponse response;
        try
        {
            response = await _detailsService.GetDetailAsync(new OnlineMusicDetailQuery(Platform,
                OnlineMusicDetailKind.Album, _route.NativeId, _route.Title), _loadCancellation.Token);
        }
        catch (OperationCanceledException) when (_loadCancellation.IsCancellationRequested) { return; }
        if (!response.IsSuccess || response.Content is null) { StatusText = response.SafeMessage; return; }
        Title = response.Content.Title;
        Artist = response.Content.Subtitle;
        CoverUri = response.Content.CoverUri;
        Tracks = response.Content.Tracks.Select(ArtistPage.ToTrack).ToArray();
        StatusText = response.SafeMessage;
        Bindings.Update();
    }

    private async void Track_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not DetailTrackPreview track) return;
        if (!track.TryCreateSearchResult(out var item) || item is null) { StatusText = track.RestrictionState; return; }
        if (_player is null) { StatusText = "播放服务尚未连接"; return; }
        var result = await _player.PlaySearchResultAsync(item, true);
        StatusText = result.IsSuccess ? $"正在播放“{track.Title}”" : result.SafeMessage;
    }
}
