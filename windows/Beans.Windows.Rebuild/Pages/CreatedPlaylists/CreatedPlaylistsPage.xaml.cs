using System.Collections.ObjectModel;
using System.ComponentModel;
using Beans.Windows.Rebuild.Infrastructure.Navigation;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.PlatformLibrary;
using Beans.Windows.Rebuild.Services.BeansPlaylists;
using Beans.Windows.Rebuild.Services.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beans.Windows.Rebuild.Pages.CreatedPlaylists;

public sealed class PlatformPlaylistCard
{
    public PlatformPlaylistCard() { }
    public PlatformPlaylistCard(PlatformUserPlaylist playlist) => Playlist = playlist;
    public PlatformUserPlaylist Playlist { get; set; } = null!;
    public string Title => Playlist.Title;
    public string CoverUri => string.IsNullOrWhiteSpace(Playlist.CoverUri)
        ? "ms-appx:///Assets/Branding/beans-icon.png"
        : Playlist.CoverUri;
    public string ProviderLine => $"{Playlist.Platform.ToDisplayName()} · {Playlist.Creator}";
    public string TrackCountText => Playlist.TrackCount is { } count ? $"{count} 首" : "曲目数量未知";
}

public sealed partial class CreatedPlaylistsPage : UserControl, INotifyPropertyChanged
{
    private readonly IPlatformLibraryService? _library;
    private readonly INavigationService? _navigation;
    private readonly PlatformId? _platformFilter;
    private readonly IBeansPlaylistService? _beansPlaylists;
    private CancellationTokenSource? _loadCancellation;
    private bool _isLoading;
    private string _statusText = "准备加载平台歌单";
    private string _sourceStatusText = "QQ 音乐 · 未检查　网易云音乐 · 未检查";

    public CreatedPlaylistsPage() : this(null, null) { }

    public CreatedPlaylistsPage(IPlatformLibraryService? library, INavigationService? navigation, PlatformId? platformFilter = null, IBeansPlaylistService? beansPlaylists = null)
    {
        _library = library;
        _navigation = navigation;
        _platformFilter = platformFilter;
        _beansPlaylists = beansPlaylists;
        InitializeComponent();
        Loaded += Page_Loaded;
        Unloaded += (_, _) => _loadCancellation?.Cancel();
    }

    public ObservableCollection<PlatformPlaylistCard> Playlists { get; } = [];
    public ObservableCollection<BeansPlaylist> BeansPlaylists { get; } = [];
    public Visibility BeansEmptyVisibility => BeansPlaylists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string PageTitle => _platformFilter switch
    {
        PlatformId.QqMusic => "QQ 音乐歌单",
        PlatformId.NetEaseMusic => "网易云音乐歌单",
        _ => "创建的歌单"
    };
    public string PageSubtitle => _platformFilter switch
    {
        PlatformId.QqMusic => "仅显示 QQ 音乐账号下的歌单，点击进入独立详情页。",
        PlatformId.NetEaseMusic => "仅显示网易云音乐账号下的歌单，点击进入独立详情页。",
        _ => "来自已登录的 QQ 音乐和网易云音乐；每个平台的歌单互不混合。"
    };
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value, nameof(StatusText)); }
    public string SourceStatusText { get => _sourceStatusText; private set => Set(ref _sourceStatusText, value, nameof(SourceStatusText)); }
    public Visibility LoadingVisibility => _isLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => !_isLoading && Playlists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ContentVisibility => !_isLoading && Playlists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync(false);
        await LoadBeansPlaylistsAsync();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync(true);

    private async void CreateBeansPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_beansPlaylists is null) return;
        var box = new TextBox { PlaceholderText = "例如：通勤歌单", MinWidth = 320 };
        var dialog = new ContentDialog
        {
            Title = "新建 Beans 歌单",
            Content = box,
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(box.Text)) return;
        try
        {
            await _beansPlaylists.CreateAsync(box.Text, CancellationToken.None);
            await LoadBeansPlaylistsAsync();
            StatusText = "Beans 歌单已创建";
        }
        catch (ArgumentException exception) { StatusText = exception.Message; }
        catch { StatusText = "Beans 歌单暂时无法创建"; }
    }

    private async Task LoadBeansPlaylistsAsync()
    {
        if (_beansPlaylists is null) return;
        try
        {
            BeansPlaylists.Clear();
            foreach (var playlist in await _beansPlaylists.GetPlaylistsAsync()) BeansPlaylists.Add(playlist);
            PropertyChanged?.Invoke(this, new(nameof(BeansEmptyVisibility)));
        }
        catch { StatusText = "Beans 歌单暂时无法读取"; }
    }

    private async Task LoadAsync(bool forceRefresh)
    {
        if (_library is null)
        {
            StatusText = "平台歌单服务尚未连接";
            RaiseState();
            return;
        }

        _loadCancellation?.Cancel();
        _loadCancellation = new CancellationTokenSource();
        _isLoading = true;
        StatusText = "正在运行账号探针并加载平台歌单…";
        RaiseState();

        try
        {
            IReadOnlyList<PlatformLibrarySnapshot> snapshots = _platformFilter is { } platform
                ? new[] { await _library.LoadAsync(platform, forceRefresh, _loadCancellation.Token) }
                : await _library.LoadAllAsync(forceRefresh, _loadCancellation.Token);
            Playlists.Clear();
            var playlists = PlatformPlaylistDeduplicator.Deduplicate(
                snapshots.SelectMany(snapshot => snapshot.Playlists), _platformFilter);
            foreach (var playlist in playlists
                         .OrderBy(playlist => playlist.Platform)
                         .ThenBy(playlist => playlist.Title, StringComparer.CurrentCultureIgnoreCase))
                Playlists.Add(new PlatformPlaylistCard(playlist));

            SourceStatusText = string.Join("　", snapshots.Select(snapshot =>
                $"{snapshot.Platform.ToDisplayName()} · {snapshot.Probe.SafeMessage} · {snapshot.Probe.MembershipLabel}"));
            StatusText = Playlists.Count > 0
                ? $"已导入 {Playlists.Count} 个平台歌单 · 更新于 {DateTime.Now:t}"
                : snapshots.Any(snapshot => snapshot.State == PlatformLibraryState.Error)
                    ? "部分平台暂时不可用，未加载到歌单"
                    : "没有加载到平台歌单";
        }
        catch (OperationCanceledException) when (_loadCancellation.IsCancellationRequested) { }
        catch
        {
            StatusText = "平台歌单暂时无法加载";
        }
        finally
        {
            _isLoading = false;
            RaiseState();
        }
    }

    private void Qq_Click(object sender, RoutedEventArgs e) => _navigation?.Navigate("playlists-qq");
    private void NetEase_Click(object sender, RoutedEventArgs e) => _navigation?.Navigate("playlists-netease");
    private void All_Click(object sender, RoutedEventArgs e) => _navigation?.Navigate("playlists");

    private void Playlist_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PlatformPlaylistCard card || _navigation is null) return;
        _navigation.Navigate("playlist", new PlatformRouteParameter(
            card.Playlist.Platform.ToStableId(),
            card.Playlist.NativeId,
            card.Playlist.Title,
            card.Playlist.NativeKind));
    }

    private void BeansPlaylist_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BeansPlaylist playlist || _navigation is null) return;
        _navigation.Navigate("playlist", new PlatformRouteParameter("beans", playlist.Id, playlist.Title));
    }

    private void RaiseState()
    {
        PropertyChanged?.Invoke(this, new(nameof(LoadingVisibility)));
        PropertyChanged?.Invoke(this, new(nameof(EmptyVisibility)));
        PropertyChanged?.Invoke(this, new(nameof(ContentVisibility)));
    }

    private void Set(ref string field, string value, string propertyName)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new(propertyName));
    }
}
