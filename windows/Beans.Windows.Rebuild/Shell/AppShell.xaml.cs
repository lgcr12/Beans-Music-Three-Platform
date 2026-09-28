using Beans.Windows.Rebuild.Controls.BottomPlayer;
using Beans.Windows.Rebuild.Infrastructure.Navigation;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Pages.Home;
using Beans.Windows.Rebuild.Pages.Discover;
using Beans.Windows.Rebuild.Pages.Placeholder;
using Beans.Windows.Rebuild.Pages.Search;
using Beans.Windows.Rebuild.Pages.LocalMusic;
using Beans.Windows.Rebuild.Pages.Library;
using Beans.Windows.Rebuild.Pages.Playlist;
using Beans.Windows.Rebuild.Pages.Artist;
using Beans.Windows.Rebuild.Pages.Album;
using Beans.Windows.Rebuild.Pages.Ranking;
using Beans.Windows.Rebuild.Pages.Settings;
using Beans.Windows.Rebuild.Pages.Accounts;
using Beans.Windows.Rebuild.Pages.Downloads;
using Beans.Windows.Rebuild.Pages.QueueLyrics;
using Beans.Windows.Rebuild.Pages.Player;
using Beans.Windows.Rebuild.Pages.MusicUniverse;
using Beans.Windows.Rebuild.Pages.Favorites;
using Beans.Windows.Rebuild.Pages.Notifications;
using Beans.Windows.Rebuild.Pages.CreatedPlaylists;
using Beans.Windows.Rebuild.Services.Accounts;
using Beans.Windows.Rebuild.Services.Playback;
using Beans.Windows.Rebuild.Services.LocalMusic;
using Beans.Windows.Rebuild.Services.Lyrics;
using Beans.Windows.Rebuild.Services.Downloads;
using Beans.Windows.Rebuild.Services.Details;
using Beans.Windows.Rebuild.Services.Library;
using Beans.Windows.Rebuild.Services.MusicUniverse;
using Beans.Windows.Rebuild.Services.PlatformLibrary;
using Beans.Windows.Rebuild.Services.BeansAccount;
using Beans.Windows.Rebuild.Services.BeansPlaylists;
using Beans.Windows.Rebuild.Services.Platforms;
using Beans.Windows.Rebuild.Services.Search;
using Beans.Windows.Rebuild.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Windowing;
using Beans.Windows.Rebuild.Pages.Anime;
using Beans.Windows.Rebuild.Services.Anime;

namespace Beans.Windows.Rebuild.Shell;

public sealed partial class AppShell : UserControl
{
    private readonly IServiceProvider _services;
    private readonly INavigationService _navigation;
    private readonly IPlaybackService _player;
    private readonly SearchViewModel _searchViewModel;
    private readonly BottomPlayer _bottomPlayer;
    private readonly Window _ownerWindow;
    private readonly IOnlineMusicDetailService _details;
    private readonly IUserLibraryService _userLibrary;
    private string? _activeNavigationKey;
    private Storyboard? _activePageTransition;
    private bool _navigationBusy;
    public UIElement TitleBarDragRegion => TopBarControl.DragRegion;

    public AppShell(IServiceProvider services, Window ownerWindow)
    {
        _services = services;
        _ownerWindow = ownerWindow;
        _navigation = services.GetRequiredService<INavigationService>();
        _player = services.GetRequiredService<IPlaybackService>();
        _searchViewModel = services.GetRequiredService<SearchViewModel>();
        _details = services.GetRequiredService<IOnlineMusicDetailService>();
        _userLibrary = services.GetRequiredService<IUserLibraryService>();
        InitializeComponent();
        _searchViewModel.PropertyChanged += SearchViewModel_PropertyChanged;

        _navigation.NavigationRequested += Navigation_NavigationRequested;
        _bottomPlayer = new BottomPlayer(_player);
        _bottomPlayer.RouteRequested += (_, route) => _navigation.Navigate(route);
        PlayerHost.Content = _bottomPlayer;

        Navigate(new NavigationRequest("discover"));
        Loaded += (_, _) => ApplyResponsiveState(ActualWidth);
        SizeChanged += (_, args) => ApplyResponsiveState(args.NewSize.Width);
    }

    private void Sidebar_NavigationRequested(object? sender, string route) => _navigation.Navigate(route);
    private void TopBar_SearchTextChanged(object? sender, string text) => _ = _searchViewModel.UpdateDraftAsync(text);
    private void TopBar_SearchSubmitted(object? sender, string query) => _navigation.Navigate("search", query);
    private void TopBar_SuggestionChosen(object? sender, SearchSuggestion suggestion) => _navigation.Navigate("search", suggestion.Text);
    private void TopBar_RouteRequested(object? sender, string route) => _navigation.Navigate(route);

    private void SearchViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SearchViewModel.Suggestions) or nameof(SearchViewModel.HasSuggestions))
            TopBarControl.SetSuggestions(_searchViewModel.Suggestions);
    }
    private void Navigation_NavigationRequested(object? sender, NavigationRequest request) => Navigate(request);

    private async void Navigate(NavigationRequest request)
    {
        if (_navigationBusy) return;
        var leavingAnime = _activeNavigationKey?.StartsWith("anime|", StringComparison.Ordinal) == true && request.Route != "anime";
        var navigationKey = $"{request.Route}|{FormatRouteDetail(request.Parameter)}";
        if (request.Route != "search" && string.Equals(_activeNavigationKey, navigationKey, StringComparison.Ordinal)) return;
        _activeNavigationKey = navigationKey;
        if (request.Route != "search") _searchViewModel.LeavePage();
        if (request.Route == "search" && request.Parameter is string query)
        {
            TopBarControl.SetSearchText(query);
            TopBarControl.HideSuggestions();
        }
        UIElement page;
        try
        {
            page = request.Route switch
            {
            "home" => new HomePage(_services.GetRequiredService<HomeViewModel>(), _player, _navigation),
            "discover" => new DiscoverPage(_services.GetRequiredService<DiscoverViewModel>(), _navigation),
            "anime" => new AnimeShell(
                _services.GetRequiredService<IAnimeCatalogService>(),
                _services.GetRequiredService<IAnimeSearchService>(),
                _services.GetRequiredService<IAnimeSongMatcher>(),
                _services.GetRequiredService<IAnimeExternalLinkService>(),
                _services.GetRequiredService<IBeansPlaylistService>(),
                _player, _services.GetRequiredService<ILyricsService>(), _services.GetRequiredService<AnimeSession>(), _navigation, _services.GetRequiredService<AnimeThemePrefetch>(), request.Parameter as string),
            "search" => new SearchPage(
                _searchViewModel,
                _player,
                _navigation,
                _services.GetRequiredService<IBeansPlaylistService>(),
                request.Parameter as string),
            "local" => new LocalMusicPage(
                _services.GetRequiredService<ILocalMusicCatalog>(),
                _services.GetRequiredService<ILocalPlaybackSourceFactory>(),
                _player,
                _ownerWindow),
            "library" => new LibraryPage(_navigation, _player, _userLibrary, _services.GetRequiredService<IBeansPlaylistService>()),
            "playlists" => new CreatedPlaylistsPage(
                _services.GetRequiredService<IPlatformLibraryService>(),
                _navigation, null, _services.GetRequiredService<IBeansPlaylistService>()),
            "playlists-qq" => new CreatedPlaylistsPage(
                _services.GetRequiredService<IPlatformLibraryService>(), _navigation, PlatformId.QqMusic,
                _services.GetRequiredService<IBeansPlaylistService>()),
            "playlists-netease" => new CreatedPlaylistsPage(
                _services.GetRequiredService<IPlatformLibraryService>(), _navigation, PlatformId.NetEaseMusic,
                _services.GetRequiredService<IBeansPlaylistService>()),
            "favorites" => new FavoritesPage(_userLibrary, _player),
            "playlist" => new PlaylistPage(
                request.Parameter as PlatformRouteParameter,
                _player,
                _details,
                _services.GetRequiredService<IPlatformLibraryService>(),
                _services.GetRequiredService<IBeansPlaylistService>(),
                _navigation),
            "artist" => new ArtistPage(request.Parameter as PlatformRouteParameter, _player, _details),
            "album" => new AlbumPage(request.Parameter as PlatformRouteParameter, _player, _details),
            "ranking-detail" => new RankingDetailPage(request.Parameter as PlatformRouteParameter, _player, _details),
            "settings" => new SettingsPage(_player, _services.GetRequiredService<IPlatformPreferenceStore>()),
            "accounts" => new AccountsPage(
                _services.GetRequiredService<IPlatformAuthService>(),
                _services.GetRequiredService<IPlatformLibraryService>(),
                _services.GetRequiredService<IBeansAccountService>()),
            "downloads" => new DownloadsPage(_services.GetRequiredService<IDownloadManager>(), _ownerWindow),
            "queue" => new QueuePage(_player),
            "lyrics" => new LyricsPage(_player, _services.GetRequiredService<ILyricsService>()),
            "player" => CreatePlayerPage(request.Parameter),
            "universe" => new MusicUniversePage(_services.GetRequiredService<IMusicUniverseService>()),
            "notifications" => new NotificationsPage(),
                _ => new PlaceholderPage(RouteTitle(request.Route), FormatRouteDetail(request.Parameter), _navigation)
            };
        }
        catch
        {
            page = new PlaceholderPage("页面暂时无法打开", "页面初始化失败，请返回后重试。", _navigation);
        }
        void ShowPage()
        {
        TopBarControl.SetPageAccessory(page is DiscoverPage discovery ? discovery.DetachPlatformPicker() : null);
        var fullScreenPlayer = request.Route == "player";
        var animeMode = request.Route == "anime";
        if (!fullScreenPlayer) SetPlayerFullscreen(false);
        _ownerWindow.SetTitleBar(page is AnimeShell animeShell ? animeShell.TitleBarDragRegion : TopBarControl.DragRegion);
        _ownerWindow.AppWindow.TitleBar.ButtonForegroundColor = animeMode ? Microsoft.UI.ColorHelper.FromArgb(255,32,57,88) : Microsoft.UI.Colors.White;
        if(page is AnimeShell captionShell)
        {
            void UpdateCaption(bool dark) => _ownerWindow.AppWindow.TitleBar.ButtonForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.ColorHelper.FromArgb(255,32,57,88);
            captionShell.CaptionThemeChanged += UpdateCaption;
            captionShell.Unloaded += (_,_) => captionShell.CaptionThemeChanged -= UpdateCaption;
            UpdateCaption(captionShell.IsDark);
        }
        var pageOwnsHeading = request.Route is "search" or "playlist" or "settings" or "accounts";
        TopBarControl.SetSearchVisible(!pageOwnsHeading);
        TopBarRow.Height = new GridLength(pageOwnsHeading ? 32 : 86);
        if (!animeMode) PageHost.Content = fullScreenPlayer ? null : page;
        PageHost.Visibility = animeMode ? Visibility.Collapsed : Visibility.Visible;
        FullScreenPlayerHost.Content = fullScreenPlayer ? page : null;
        FullScreenPlayerHost.Visibility = fullScreenPlayer ? Visibility.Visible : Visibility.Collapsed;
        AnimeHost.Content = animeMode ? page : null;
        AnimeHost.Visibility = animeMode ? Visibility.Visible : Visibility.Collapsed;
        SidebarControl.Visibility = fullScreenPlayer || animeMode ? Visibility.Collapsed : Visibility.Visible;
        TopBarControl.Visibility = fullScreenPlayer || animeMode ? Visibility.Collapsed : Visibility.Visible;
        PlayerRow.Height = fullScreenPlayer || animeMode ? new GridLength(0) : new GridLength(96);
        PlayerHost.Visibility = fullScreenPlayer || animeMode ? Visibility.Collapsed : Visibility.Visible;
        if (request.Route != "anime" && !leavingAnime)
            AnimatePageIn(fullScreenPlayer ? FullScreenPlayerHost : PageHost);
        SidebarControl.SelectRoute(request.Route);
        ApplyResponsiveState(ActualWidth);
        }
        if (request.Route == "anime" || leavingAnime)
        {
            _navigationBusy = true;
            try
            {
                await AnimePortal.TransitionAsync(_services.GetRequiredService<AnimeSession>().State.ReduceMotion,
                    leavingAnime, ShowPage, page is AnimeShell anime ? anime.TransitionContent : page as FrameworkElement);
            }
            finally { _navigationBusy = false; }
        }
        else ShowPage();
    }

    private PlayerPage CreatePlayerPage(object? parameter = null)
    {
        var page = new PlayerPage(
            _player,
            _navigation,
            _services.GetRequiredService<ILyricsService>(),
            _services.GetRequiredService<IPlatformPreferenceStore>(),
            string.Equals(parameter as string, "anime", StringComparison.OrdinalIgnoreCase),
            _services.GetRequiredService<AnimeSession>());
        page.AttachAnimeWindow(_ownerWindow);
        page.FullscreenRequested += PlayerPage_FullscreenRequested;
        page.Unloaded += (_, _) => page.FullscreenRequested -= PlayerPage_FullscreenRequested;
        return page;
    }

    private void PlayerPage_FullscreenRequested(object? sender, bool fullscreen)
    {
        SetPlayerFullscreen(fullscreen);
        if (sender is PlayerPage page)
            page.SetFullscreenState(_ownerWindow.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen);
    }

    private void SetPlayerFullscreen(bool fullscreen)
    {
        var kind = fullscreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped;
        if (_ownerWindow.AppWindow.Presenter.Kind != kind)
            _ownerWindow.AppWindow.SetPresenter(kind);
    }

    private void AnimatePageIn(UIElement target)
    {
        _activePageTransition?.Stop();
        target.Opacity = 0.88;
        var storyboard = new Storyboard();
        var opacity = new DoubleAnimation
        {
            From = 0.88,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(120)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacity, target);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        storyboard.Children.Add(opacity);
        storyboard.Completed += (_, _) =>
        {
            target.Opacity = 1;
            if (ReferenceEquals(_activePageTransition, storyboard)) _activePageTransition = null;
        };
        _activePageTransition = storyboard;
        storyboard.Begin();
    }

    private void ApplyResponsiveState(double width)
    {
        if (width <= 0) return;
        var state = width >= 1360 ? "Wide" : width >= 1180 ? "Medium" : width >= 1024 ? "Compact" : "Narrow";
        VisualStateManager.GoToState(this, state, false);
        TopBarControl.ApplyResponsiveState(width);
        _bottomPlayer?.ApplyResponsiveState(width);
        if (PageHost.Content is HomePage homePage) homePage.ApplyResponsiveState(width);
        if (PageHost.Content is DiscoverPage discoverPage) discoverPage.ApplyResponsiveState(width);
        if (PageHost.Content is SearchPage searchPage) searchPage.ApplyResponsiveState(width);
    }

    private static string? FormatRouteDetail(object? parameter) => parameter switch
    {
        PlatformRouteParameter route => $"{route.Title} · {route.PlatformId} · {route.NativeId}",
        null => null,
        _ => parameter.ToString()
    };

    private static string RouteTitle(string route) => route switch
    {
        "discover" => "发现",
        "anime" => "二次元音乐",
        "search" => "搜索",
        "library" => "我的音乐",
        "playlists" => "创建的歌单",
        "playlists-qq" => "QQ 音乐歌单",
        "playlists-netease" => "网易云音乐歌单",
        "favorites" => "收藏夹",
        "local" => "本地音乐",
        "settings" => "设置",
        "notifications" => "通知",
        "accounts" => "账号与平台",
        "lyrics" => "歌词",
        "queue" => "播放队列",
        "player" => "播放器",
        "playlist" => "歌单详情",
        "artist" => "歌手详情",
        "album" => "专辑详情",
        "ranking-detail" => "排行榜详情",
        _ => "功能页面"
    };
}
