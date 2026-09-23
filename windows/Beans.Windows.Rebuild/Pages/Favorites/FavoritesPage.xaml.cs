using System.Collections.ObjectModel;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Library;
using Beans.Windows.Rebuild.Services.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beans.Windows.Rebuild.Pages.Favorites;

public sealed partial class FavoritesPage : UserControl
{
    private readonly IUserLibraryService _library;
    private readonly IPlaybackService _player;
    private readonly ObservableCollection<FavoriteEntry> _favorites = [];
    private bool _loaded;

    public FavoritesPage(IUserLibraryService library, IPlaybackService player)
    {
        _library = library;
        _player = player;
        InitializeComponent();
        FavoritesList.ItemsSource = _favorites;
        Loaded += FavoritesPage_Loaded;
    }

    private async void Favorite_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FavoriteEntry entry) return;
        if (!entry.Item.TryCreateSearchResult(out var item) || item is null)
        {
            StatusText.Text = entry.Item.Platform == PlatformId.Local
                ? "本地收藏不保存文件路径，请到本地音乐页播放。"
                : "当前收藏类型暂不支持直接播放。";
            return;
        }

        try
        {
            var result = await _player.PlaySearchResultAsync(item, true);
            StatusText.Text = result.IsSuccess ? $"正在播放“{entry.Title}”" : result.SafeMessage;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "播放请求已取消";
        }
        catch (Exception)
        {
            StatusText.Text = "暂时无法播放该收藏，请重试。";
        }
    }

    private async void FavoritesPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshAsync();
    }

    private async void RemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FavoriteEntry entry }) return;
        try
        {
            await _library.SetFavoriteAsync(entry.Item, false);
            await RefreshAsync();
            StatusText.Text = $"已从本机收藏移除“{entry.Title}”";
        }
        catch (Exception)
        {
            StatusText.Text = "暂时无法更新收藏，请重试。";
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var snapshot = await _library.GetSnapshotAsync();
            _favorites.Clear();
            foreach (var entry in snapshot.Favorites) _favorites.Add(entry);
            CountText.Text = $"{_favorites.Count} 项";
            EmptyPanel.Visibility = _favorites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            FavoritesPanel.Visibility = _favorites.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            StatusText.Text = snapshot.RecoveryStatus switch
            {
                LibraryRecoveryStatus.RestoredFromBackup => "本地收藏已从安全备份恢复",
                LibraryRecoveryStatus.ResetAfterCorruption => "收藏文件损坏，已安全重置",
                _ => "仅保存在本机"
            };
        }
        catch (Exception)
        {
            EmptyPanel.Visibility = Visibility.Visible;
            FavoritesPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = "暂时无法读取本地收藏";
        }
    }
}
