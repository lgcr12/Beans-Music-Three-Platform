using System.ComponentModel;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Details;
using Beans.Windows.Rebuild.Services.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beans.Windows.Rebuild.Pages.Artist;

public sealed partial class ArtistPage : UserControl, INotifyPropertyChanged
{
    private readonly IPlaybackService? _player;
    private readonly IOnlineMusicDetailService? _detailsService;
    private readonly PlatformRouteParameter? _route;
    private CancellationTokenSource? _loadCancellation;
    private bool _loaded;
    public ArtistPreviewContent ArtistContent { get; private set; }
    private string _statusText = "准备加载歌手详情";
    public string StatusText { get => _statusText; private set { if (_statusText == value) return; _statusText = value; PropertyChanged?.Invoke(this, new(nameof(StatusText))); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public ArtistPage(PlatformRouteParameter? route = null) : this(route, null, null) { }

    public ArtistPage(PlatformRouteParameter? route, IPlaybackService? player) : this(route, player, null) { }

    public ArtistPage(PlatformRouteParameter? route, IPlaybackService? player, IOnlineMusicDetailService? detailsService)
    {
        _player = player;
        _route = route;
        _detailsService = detailsService;
        ArtistContent = EmptyContent(route);
        InitializeComponent();
        Loaded += Page_Loaded;
        Unloaded += (_, _) => _loadCancellation?.Cancel();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        if (_route is null || !PlatformIdExtensions.TryParseStableId(_route.PlatformId, out var platform) ||
            platform is not (PlatformId.QqMusic or PlatformId.NetEaseMusic)) { StatusText = "该歌手没有可用的在线平台详情标识"; return; }
        if (_detailsService is null) { StatusText = "在线详情服务尚未连接"; return; }
        _loadCancellation = new CancellationTokenSource();
        StatusText = "正在加载真实歌手详情…";
        OnlineMusicDetailResponse response;
        try
        {
            response = await _detailsService.GetDetailAsync(new OnlineMusicDetailQuery(platform,
                OnlineMusicDetailKind.Artist, _route.NativeId, _route.Title), _loadCancellation.Token);
        }
        catch (OperationCanceledException) when (_loadCancellation.IsCancellationRequested) { return; }
        if (!response.IsSuccess || response.Content is null) { StatusText = response.SafeMessage; return; }
        ArtistContent = Map(response.Content);
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

    private static ArtistPreviewContent EmptyContent(PlatformRouteParameter? route)
    {
        var platform = PlatformIdExtensions.TryParseStableId(route?.PlatformId ?? string.Empty, out var parsed) ? parsed : PlatformId.Beans;
        return new(route?.NativeId ?? string.Empty, string.IsNullOrWhiteSpace(route?.Title) ? "歌手详情" : route.Title,
            "在线详情尚未加载。", "ms-appx:///Assets/Branding/beans-icon.png", platform, [], []);
    }

    private static ArtistPreviewContent Map(OnlineMusicDetailContent content) => new(content.NativeId, content.Title,
        content.Description, content.CoverUri, content.Platform,
        content.Tracks.Select(ToTrack).ToArray(),
        content.RelatedCollections.Where(item => item.Kind == OnlineMusicDetailKind.Album)
            .Select(item => new DetailAlbumPreview(item.NativeId, item.Title, item.Subtitle, item.CoverUri, item.Platform)).ToArray());

    internal static DetailTrackPreview ToTrack(SearchResultItem track) => new(track.NativeId, track.Title, track.Artist,
        track.Album, track.DurationText, track.Platform, false, track.RestrictionState, track.DataOrigin, track.CoverUri, track.Quality);
}
