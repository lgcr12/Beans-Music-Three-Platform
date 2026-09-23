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

public sealed partial class LibraryPage : UserControl, INotifyPropertyChanged
{
    private readonly INavigationService _navigation;
    private readonly IPlaybackService? _player;
    private readonly IUserLibraryService _library;
    private readonly IBeansPlaylistService? _beansPlaylists;
    private readonly ObservableCollection<PlaybackHistoryEntry> _history = [];
    public ObservableCollection<BeansPlaylist> BeansPlaylists { get; } = [];
    public Visibility BeansEmptyVisibility => BeansPlaylists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        if (_loaded) return;
        _loaded = true;
        await RefreshAsync();
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

    private void CreatePlaylist_Click(object sender, RoutedEventArgs e) => _navigation.Navigate("playlists");

    private void BeansPlaylist_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BeansPlaylist playlist)
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
        if (_beansPlaylists is null) return;
        try
        {
            BeansPlaylists.Clear();
            foreach (var playlist in await _beansPlaylists.GetPlaylistsAsync()) BeansPlaylists.Add(playlist);
            PropertyChanged?.Invoke(this, new(nameof(BeansEmptyVisibility)));
        }
        catch { }
    }
}
