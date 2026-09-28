using System.Collections.ObjectModel;
using System.ComponentModel;
using Beans.Windows.Rebuild.Infrastructure.Navigation;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Library;
using Beans.Windows.Rebuild.Services.Playback;
using Beans.Windows.Rebuild.Services.BeansPlaylists;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beans.Windows.Rebuild.Pages.Library;

public sealed class LibraryPlaylistCard(BeansPlaylist playlist)
{
    public BeansPlaylist Playlist { get; } = playlist;
    public string Title => Playlist.Title;
    public string TrackCountText => Playlist.TrackCountText;
    public string CoverUri => Playlist.Tracks.Select(track => track.CoverUri).FirstOrDefault(uri => !string.IsNullOrWhiteSpace(uri)) ?? "ms-appx:///Assets/Home/aurora-shell.png";
}

public sealed partial class LibraryPage : UserControl, INotifyPropertyChanged
{
    private readonly INavigationService _navigation;
    private readonly IPlaybackService? _player;
    private readonly IUserLibraryService _library;
    private readonly IBeansPlaylistService? _beansPlaylists;
    private readonly ObservableCollection<PlaybackHistoryEntry> _history = [];
    public ObservableCollection<LibraryPlaylistCard> BeansPlaylists { get; } = [];
    private bool _beansLoaded;
    private bool _beansLoading;
    public Visibility BeansEmptyVisibility => _beansLoaded && BeansPlaylists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    private bool _loaded;
    public event PropertyChangedEventHandler? PropertyChanged;

    public LibraryPage(INavigationService navigation, IPlaybackService? player, IUserLibraryService library, IBeansPlaylistService? beansPlaylists = null)
    {
        _navigation = navigation;
        _player = player;
        _library = library;
        _beansPlaylists = beansPlaylists;
        InitializeComponent();
        HistoryList.ItemsSource = _history;
        Loaded += LibraryPage_Loaded;
    }

    private async void LibraryPage_Loaded(object sender, RoutedEventArgs e)
    {
        var playlists = LoadBeansPlaylistsAsync();
        if (!_loaded)
        {
            _loaded = true;
            await RefreshAsync();
        }
        await playlists;
    }

    private async void RefreshPlaylists_Click(object sender, RoutedEventArgs e)
    {
        await LoadBeansPlaylistsAsync();
    }

    private async void Track_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PlaybackHistoryEntry entry) return;
        if (!entry.Item.TryCreateSearchResult(out var item) || item is null)
        {
            StatusText.Text = entry.PlaybackBoundary;
            return;
        }
        if (_player is null) { StatusText.Text = "播放服务尚未连接"; return; }
        var result = await _player.PlaySearchResultAsync(item, true);
        if (!result.IsSuccess) { StatusText.Text = result.SafeMessage; return; }
        StatusText.Text = $"正在播放“{entry.Title}”";
        await RefreshAsync();
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _library.ClearPlaybackHistoryAsync();
            await RefreshAsync();
            StatusText.Text = "播放记录已清空";
        }
        catch (Exception) { StatusText.Text = "暂时无法清空播放记录，请重试。"; }
    }

    private void Library_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < 900;
        LibraryShortcuts.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 1, GridUnitType.Star);
        LibraryShortcuts.ColumnDefinitions[2].Width = new GridLength(stacked ? 0 : 1, GridUnitType.Star);
        for (var i = 0; i < LibraryShortcuts.Children.Count; i++)
        {
            Grid.SetColumn((FrameworkElement)LibraryShortcuts.Children[i], stacked ? 0 : i);
            Grid.SetRow((FrameworkElement)LibraryShortcuts.Children[i], stacked ? i : 0);
        }
    }

    private void Favorites_Click(object sender, RoutedEventArgs e) => _navigation.Navigate("favorites");
    private void Queue_Click(object sender, RoutedEventArgs e) => _navigation.Navigate("queue");
    private void History_Click(object sender, RoutedEventArgs e) => HistoryHeading.StartBringIntoView();

    private void CreatePlaylist_Click(object sender, RoutedEventArgs e) => _navigation.Navigate("playlists");

    private void BeansPlaylist_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LibraryPlaylistCard { Playlist: var playlist })
            _navigation.Navigate("playlist", new PlatformRouteParameter("beans", playlist.Id, playlist.Title));
    }

    private void ImportLocal_Click(object sender, RoutedEventArgs e) => _navigation.Navigate("local");

    private async Task RefreshAsync()
    {
        try
        {
            var snapshot = await _library.GetSnapshotAsync();
            _history.Clear();
            foreach (var entry in snapshot.RecentHistory) _history.Add(entry);
            FavoriteTrackCountText.Text = $"{snapshot.Favorites.Count(entry => entry.Item.Kind == LibraryItemKind.Track)} 首";
            FavoriteContentCountText.Text = $"{snapshot.Favorites.Count(entry => entry.Item.Kind != LibraryItemKind.Track)} 项";
            HistoryCountText.Text = $"{_history.Count} 首";
            HistoryEmptyPanel.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryPanel.Visibility = _history.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ClearHistoryButton.IsEnabled = _history.Count > 0;
            StatusText.Text = snapshot.RecoveryStatus switch
            {
                LibraryRecoveryStatus.RestoredFromBackup => "音乐库已从安全备份恢复",
                LibraryRecoveryStatus.ResetAfterCorruption => "音乐库文件损坏，已安全重置",
                _ => "本机音乐库已加载"
            };
        }
        catch (Exception)
        {
            StatusText.Text = "暂时无法读取本机音乐库";
            HistoryEmptyPanel.Visibility = Visibility.Visible;
            HistoryPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadBeansPlaylistsAsync()
    {
        if (_beansPlaylists is null || _beansLoading) return;
        _beansLoading = true;
        try
        {
            var playlists = (await _beansPlaylists.GetPlaylistsAsync()).Select(p => new LibraryPlaylistCard(p)).ToArray();
            BeansPlaylists.Clear();
            foreach (var playlist in playlists) BeansPlaylists.Add(playlist);
            _beansLoaded = true;
            BeansPlaylistCountText.Text = $"{BeansPlaylists.Count} 个";
            BeansStatusText.Text = BeansPlaylists.Count > 0 ? $"已加载 {BeansPlaylists.Count} 个 Beans 歌单" : "还没有 Beans 歌单";
            PropertyChanged?.Invoke(this, new(nameof(BeansEmptyVisibility)));
        }
        catch { BeansStatusText.Text = _beansLoaded ? "Beans 歌单读取失败，已保留当前列表，请刷新重试" : "Beans 歌单读取失败，请刷新重试"; }
        finally { _beansLoading = false; }
    }
}
