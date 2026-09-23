using System.ComponentModel;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Infrastructure.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Beans.Windows.Rebuild.Pages.Library;
using Beans.Windows.Rebuild.Services.Details;
using Beans.Windows.Rebuild.Services.Playback;
using Beans.Windows.Rebuild.Services.PlatformLibrary;
using Beans.Windows.Rebuild.Services.BeansPlaylists;
using Beans.Windows.Rebuild.Services.Library;

namespace Beans.Windows.Rebuild.Pages.Playlist;

public sealed partial class PlaylistPage : UserControl, INotifyPropertyChanged
{
    private readonly IPlaybackService? _player;
    private readonly IOnlineMusicDetailService? _detailsService;
    private readonly PlatformRouteParameter? _route;
    private readonly IPlatformLibraryService? _platformLibrary;
    private readonly IBeansPlaylistService? _beansPlaylists;
    private readonly INavigationService? _navigation;
    private CancellationTokenSource? _loadCancellation;
    private readonly HashSet<string> _selectedTrackIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private string _filterText = string.Empty;
    public PlaylistPreviewDetails Details { get; private set; }
    public IReadOnlyList<LibraryTrackPreview> FilteredTracks { get; private set; } = [];
    private string _statusText = "准备加载歌单详情";
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value) return;
            _statusText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool CanImportToBeans => _beansPlaylists is not null && _route?.PlatformId is "qq" or "netease";
    public string SelectionText => _selectedTrackIds.Count == 0 ? "未选择歌曲" : $"已选择 {_selectedTrackIds.Count} 首";
    public string FilterText
    {
        get => _filterText;
        private set
        {
            if (_filterText == value) return;
            _filterText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilterText)));
            ApplyFilter();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilterClearVisibility)));
        }
    }
    public string FilteredCountText => string.IsNullOrWhiteSpace(FilterText)
        ? $"共 {Details.Tracks.Count} 首"
        : $"显示 {FilteredTracks.Count} / {Details.Tracks.Count} 首";
    public Visibility FilterClearVisibility => string.IsNullOrWhiteSpace(FilterText)
        ? Visibility.Collapsed
        : Visibility.Visible;
    public Visibility NoFilterResultsVisibility => Details.Tracks.Count > 0 && FilteredTracks.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public PlaylistPage() : this((PlatformRouteParameter?)null, null, null, null, null, null) { }

    public PlaylistPage(string? playlistId) : this(playlistId, null) { }

    public PlaylistPage(string? playlistId, IPlaybackService? player)
        : this(string.IsNullOrWhiteSpace(playlistId) ? null : new PlatformRouteParameter("beans", playlistId, "歌单详情"), player, null, null, null, null) { }

    public PlaylistPage(
        PlatformRouteParameter? route,
        IPlaybackService? player,
        IOnlineMusicDetailService? detailsService,
        IPlatformLibraryService? platformLibrary = null,
        IBeansPlaylistService? beansPlaylists = null,
        INavigationService? navigation = null)
    {
        _player = player;
        _route = route;
        _detailsService = detailsService;
        _platformLibrary = platformLibrary;
        _beansPlaylists = beansPlaylists;
        _navigation = navigation;
        Details = EmptyDetails(route);
        FilteredTracks = Details.Tracks;
        InitializeComponent();
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        try { await LoadAsync(); }
        catch (OperationCanceledException) when (_loadCancellation?.IsCancellationRequested == true) { }
        catch { StatusText = "歌单详情暂时无法打开，请返回后重试"; }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e) => _loadCancellation?.Cancel();

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        var route = _route?.PlatformId switch
        {
            "qq" => "playlists-qq",
            "netease" => "playlists-netease",
            "beans" => "playlists",
            _ => "playlists"
        };
        _navigation?.Navigate(route);
    }

    private async void ImportToBeans_Click(object sender, RoutedEventArgs e)
    {
        if (_beansPlaylists is null) { StatusText = "Beans 歌单服务尚未连接"; return; }
        var selectedTracks = Details.Tracks
            .Where(track => _selectedTrackIds.Contains(track.Id))
            .ToArray();
        if (selectedTracks.Length == 0) { StatusText = "请先选择要导入的歌曲"; return; }

        var tracks = new List<LibraryMediaSnapshot>();
        foreach (var preview in selectedTracks)
        {
            if (preview.TryCreateSearchResult(out var item) && item is not null && LibraryMediaSnapshot.TryCreate(item, out var snapshot) && snapshot is not null)
                tracks.Add(snapshot);
        }
        if (tracks.Count == 0) { StatusText = "所选歌曲没有可导入的真实标识"; return; }

        IReadOnlyList<BeansPlaylist> playlists;
        try { playlists = await _beansPlaylists.GetPlaylistsAsync(); }
        catch { StatusText = "Beans 歌单暂时无法读取"; return; }

        var picker = new ComboBox
        {
            ItemsSource = playlists,
            DisplayMemberPath = nameof(BeansPlaylist.Title),
            SelectedIndex = playlists.Count > 0 ? 0 : -1,
            IsEnabled = playlists.Count > 0,
            MinWidth = 300,
            PlaceholderText = "选择已有 Beans 歌单"
        };
        var titleBox = new TextBox
        {
            Header = "新建歌单名称（选择“新建并导入”时使用）",
            Text = Details.Playlist.Title,
            MinWidth = 300
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = $"将导入 {tracks.Count} 首歌曲；重复歌曲会自动跳过。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(picker);
        content.Children.Add(titleBox);
        var dialog = new ContentDialog
        {
            Title = "导入到 Beans 歌单",
            Content = content,
            PrimaryButtonText = playlists.Count > 0 ? "导入到选中歌单" : "导入",
            SecondaryButtonText = "新建并导入",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;

        BeansPlaylist? target;
        if (result == ContentDialogResult.Secondary)
        {
            if (string.IsNullOrWhiteSpace(titleBox.Text)) { StatusText = "请输入新歌单名称"; return; }
            try { target = await _beansPlaylists.CreateAsync(titleBox.Text, CancellationToken.None); }
            catch (ArgumentException exception) { StatusText = exception.Message; return; }
        }
        else
        {
            target = picker.SelectedItem as BeansPlaylist;
            if (target is null) { StatusText = "请先选择 Beans 歌单，或使用新建并导入"; return; }
        }

        try
        {
            var added = await _beansPlaylists.AddTracksAsync(target.Id, tracks);
            StatusText = added == 0
                ? $"歌曲已存在于“{target.Title}”"
                : $"已导入 {added} 首歌曲到“{target.Title}”";
            _selectedTrackIds.Clear();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionText)));
        }
        catch { StatusText = "导入 Beans 歌单失败"; }
    }

    private void TrackSelect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not string trackId)
            return;

        if (checkBox.IsChecked == true)
            _selectedTrackIds.Add(trackId);
        else
            _selectedTrackIds.Remove(trackId);

        RaiseSelectionState();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var visibleTrackIds = FilteredTracks
            .Select(track => track.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (visibleTrackIds.Length == 0)
        {
            SelectAllCheckBox.IsChecked = false;
            return;
        }

        if (SelectAllCheckBox.IsChecked == true)
        {
            foreach (var id in visibleTrackIds) _selectedTrackIds.Add(id);
        }
        else
        {
            foreach (var id in visibleTrackIds) _selectedTrackIds.Remove(id);
        }
        RaiseSelectionState();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        _selectedTrackIds.Clear();
        RaiseSelectionState();
    }

    private void RaiseSelectionState()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionText)));
        UpdateSelectAllState();
    }

    private void UpdateSelectAllState()
    {
        if (SelectAllCheckBox is null) return;
        var visibleTrackIds = FilteredTracks
            .Select(track => track.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectedCount = visibleTrackIds.Count(id => _selectedTrackIds.Contains(id));
        SelectAllCheckBox.IsChecked = visibleTrackIds.Length > 0 && selectedCount == visibleTrackIds.Length
            ? true
            : selectedCount > 0 ? null : false;
    }

    private void TrackFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox) FilterText = textBox.Text;
    }

    private void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        FilterText = string.Empty;
        TrackFilterBox.Text = string.Empty;
    }

    private void ApplyFilter()
    {
        var query = FilterText.Trim();
        FilteredTracks = string.IsNullOrWhiteSpace(query)
            ? Details.Tracks
            : Details.Tracks.Where(track =>
                    Contains(track.Title, query) ||
                    Contains(track.Artist, query) ||
                    Contains(track.Album, query))
                .ToArray();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredTracks)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredCountText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NoFilterResultsVisibility)));
        UpdateSelectAllState();
    }

    private static bool Contains(string? value, string query) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private async Task LoadAsync()
    {
        _selectedTrackIds.Clear();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionText)));
        if (_route is null || !PlatformIdExtensions.TryParseStableId(_route.PlatformId, out var platform))
        {
            StatusText = "该歌单没有可用的在线平台详情标识";
            return;
        }
        _loadCancellation?.Cancel();
        _loadCancellation = new CancellationTokenSource();
        var loadToken = _loadCancellation.Token;
        if (platform == PlatformId.Beans)
        {
            await LoadBeansPlaylistAsync(_route.NativeId, loadToken);
            return;
        }
        if (platform is not (PlatformId.QqMusic or PlatformId.NetEaseMusic))
        {
            StatusText = "该歌单没有可用的平台详情标识";
            return;
        }
        if (_detailsService is null) { StatusText = "在线详情服务尚未连接"; return; }
        if (_platformLibrary is not null && !string.IsNullOrWhiteSpace(_route.NativeKind))
        {
            await LoadAuthorizedPlaylistAsync(platform, loadToken);
            return;
        }
        StatusText = "正在加载真实歌单详情…";
        OnlineMusicDetailResponse response;
        try
        {
            response = await _detailsService.GetDetailAsync(
                new OnlineMusicDetailQuery(platform, OnlineMusicDetailKind.Playlist, _route.NativeId, _route.Title), loadToken);
        }
        catch (OperationCanceledException) when (_loadCancellation.IsCancellationRequested) { return; }
        if (!response.IsSuccess || response.Content is null) { StatusText = response.SafeMessage; return; }
        Details = Map(response.Content);
        StatusText = response.SafeMessage;
        ApplyFilter();
        Bindings.Update();
    }

    private async Task LoadBeansPlaylistAsync(string playlistId, CancellationToken cancellationToken)
    {
        _selectedTrackIds.Clear();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionText)));
        if (_beansPlaylists is null) { StatusText = "Beans 歌单服务尚未连接"; return; }
        try
        {
            var playlist = (await _beansPlaylists.GetPlaylistsAsync(cancellationToken)).FirstOrDefault(item => item.Id == playlistId);
            if (playlist is null) { StatusText = "Beans 歌单不存在或已被删除"; return; }
            var items = playlist.Tracks.Select(track => track.TryCreateSearchResult(out var item) ? item : null).OfType<SearchResultItem>().ToArray();
            var content = new OnlineMusicDetailContent(PlatformId.Beans, OnlineMusicDetailKind.Playlist,
                playlist.Id, playlist.Title, "Beans 歌单 · 仅保存在本机", "音乐文件和歌单元数据只保存在本机。",
                items.FirstOrDefault()?.CoverUri ?? "ms-appx:///Assets/Branding/beans-icon.png", items, [],
                SearchDataOrigin.Live, playlist.UpdatedAt);
            Details = Map(content);
            StatusText = items.Length == 0 ? "该 Beans 歌单暂无歌曲" : $"已加载 {items.Length} 首歌曲";
            ApplyFilter();
            Bindings.Update();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { StatusText = "Beans 歌单暂时无法加载"; }
    }

    private async Task LoadAuthorizedPlaylistAsync(PlatformId platform, CancellationToken cancellationToken)
    {
        _selectedTrackIds.Clear();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionText)));
        StatusText = "正在通过已授权账号加载歌单曲目…";
        try
        {
            var snapshot = await _platformLibrary!.LoadAsync(platform, false, cancellationToken);
            var playlist = snapshot.Playlists.FirstOrDefault(item =>
                item.NativeId == _route!.NativeId &&
                item.NativeKind.Equals(_route.NativeKind, StringComparison.OrdinalIgnoreCase));
            if (playlist is null)
            {
                StatusText = "平台歌单已更新，请返回歌单列表后重试";
                return;
            }

            var tracks = await _platformLibrary.LoadPlaylistTracksAsync(playlist, cancellationToken);
            var items = tracks.Select(ToSearchItem).ToArray();
            var content = new OnlineMusicDetailContent(
                platform,
                OnlineMusicDetailKind.Playlist,
                playlist.NativeId,
                playlist.Title,
                $"{platform.ToDisplayName()} · {playlist.Creator}",
                "通过本机保存的官方平台授权只读加载。",
                playlist.CoverUri,
                items,
                [],
                SearchDataOrigin.Live,
                DateTimeOffset.UtcNow);
            Details = Map(content);
            StatusText = items.Length == 0 ? "该歌单暂无歌曲" : $"已加载 {items.Length} 首歌曲";
            ApplyFilter();
            Bindings.Update();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (UnauthorizedAccessException)
        {
            StatusText = $"请重新登录{platform.ToDisplayName()}";
        }
        catch
        {
            StatusText = "平台歌单曲目暂时无法加载";
        }
    }

    private static SearchResultItem ToSearchItem(PlatformPlaylistTrack track) => new(
        SearchResultType.Track,
        track.Platform,
        track.NativeId,
        $"{track.Platform.ToStableId()}:track:{track.NativeId}",
        track.Title,
        Subtitle: $"{track.Artist} · {track.Album}",
        Artist: track.Artist,
        Album: track.Album,
        CoverUri: SafeCoverUri(track.CoverUri),
        Duration: track.Duration,
        Quality: track.Quality,
        IsPlayable: false,
        RestrictionState: track.Availability switch
        {
            AvailabilityState.SubscriptionRequired => "需要有效会员权益",
            AvailabilityState.RegionRestricted => "当前地区不可用",
            AvailabilityState.Unavailable => "平台未提供播放源",
            _ => "播放时将解析官方播放源"
        },
        SourceDisplayName: track.Platform.ToDisplayName(),
        SourceBadgeText: track.Platform.ToDisplayName(),
        DataOrigin: SearchDataOrigin.Live,
        PayloadReference: $"{track.Platform.ToStableId()}:track:{track.NativeId}",
        ProviderMediaId: track.ProviderMediaId);

    private async void PlayAll_Click(object sender, RoutedEventArgs e)
    {
        var items = Details.Tracks.Select(track => track.TryCreateSearchResult(out var item) ? item : null).OfType<SearchResultItem>().ToArray();
        if (items.Length == 0) { StatusText = "当前歌单只有预览条目，无法播放"; return; }
        if (_player is null) { StatusText = "播放服务尚未连接"; return; }
        var first = await _player.PlaySearchResultAsync(items[0], true);
        if (!first.IsSuccess) { StatusText = first.SafeMessage; return; }
        foreach (var item in items.Skip(1))
        {
            var queued = await _player.QueueSearchResultAsync(item);
            if (!queued.IsSuccess) { StatusText = queued.SafeMessage; return; }
        }
        StatusText = $"正在播放 · 已加入 {items.Length} 首";
    }

    private async void QueueAll_Click(object sender, RoutedEventArgs e)
    {
        var items = Details.Tracks.Select(track => track.TryCreateSearchResult(out var item) ? item : null).OfType<SearchResultItem>().ToArray();
        if (items.Length == 0) { StatusText = "当前歌单只有预览条目，无法加入队列"; return; }
        if (_player is null) { StatusText = "播放服务尚未连接"; return; }
        foreach (var item in items)
        {
            var queued = await _player.QueueSearchResultAsync(item);
            if (!queued.IsSuccess) { StatusText = queued.SafeMessage; return; }
        }
        StatusText = $"已加入 {items.Length} 首歌曲";
    }

    private async void Track_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LibraryTrackPreview track) await PlayTrackAsync(track);
    }

    private async void TrackAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LibraryTrackPreview track) await PlayTrackAsync(track);
    }

    private async Task PlayTrackAsync(LibraryTrackPreview track)
    {
        if (!track.TryCreateSearchResult(out var item) || item is null) { StatusText = track.PlaybackBoundary; return; }
        if (_player is null) { StatusText = "播放服务尚未连接"; return; }
        var allItems = Details.Tracks
            .Select(value => value.TryCreateSearchResult(out var resultItem) ? resultItem : null)
            .OfType<SearchResultItem>()
            .ToArray();
        var result = await _player.PlaySearchResultAsync(item, true);
        if (result.IsSuccess)
        {
            foreach (var sibling in allItems.Where(value => value.StableId != item.StableId))
            {
                var queued = await _player.QueueSearchResultAsync(sibling);
                if (!queued.IsSuccess) break;
            }
        }
        StatusText = result.IsSuccess ? $"正在播放“{track.Title}”" : result.SafeMessage;
    }

    private static PlaylistPreviewDetails EmptyDetails(PlatformRouteParameter? route)
    {
        var platform = PlatformIdExtensions.TryParseStableId(route?.PlatformId ?? string.Empty, out var parsed) ? parsed : PlatformId.Beans;
        var playlist = new LibraryPlaylistPreview(route?.NativeId ?? string.Empty,
            string.IsNullOrWhiteSpace(route?.Title) ? "歌单详情" : route.Title, "等待加载在线详情",
            "ms-appx:///Assets/Branding/beans-icon.png", platform, 0, false);
        return new PlaylistPreviewDetails(playlist, "在线详情尚未加载。", []);
    }

    private static PlaylistPreviewDetails Map(OnlineMusicDetailContent content)
    {
        var tracks = content.Tracks.Select(track => new LibraryTrackPreview(track.NativeId, track.Title, track.Artist,
            track.Album, track.DurationText, SafeCoverUri(track.CoverUri), track.Platform, false, track.RestrictionState,
            track.DataOrigin, track.Quality, track.ProviderMediaId)).ToArray();
        return new PlaylistPreviewDetails(
            new LibraryPlaylistPreview(content.NativeId, content.Title, content.Subtitle, SafeCoverUri(content.CoverUri),
                content.Platform, tracks.Length, content.CanAttemptPlayback),
            content.Description, tracks);
    }

    private static string SafeCoverUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return FallbackCoverUri;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return FallbackCoverUri;
        return uri.Scheme switch
        {
            "http" or "https" or "ms-appx" or "ms-appdata" => uri.ToString(),
            _ => FallbackCoverUri
        };
    }

    private const string FallbackCoverUri = "ms-appx:///Assets/Branding/beans-icon.png";
}
