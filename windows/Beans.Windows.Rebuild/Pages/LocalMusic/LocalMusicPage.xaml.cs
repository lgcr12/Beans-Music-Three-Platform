using Beans.Windows.Rebuild.Infrastructure.Navigation;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.LocalMusic;
using Beans.Windows.Rebuild.Services.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using Windows.Storage.Pickers;
using WinRT.Interop;
using LocalIndexedTrack = Beans.Windows.Rebuild.Services.LocalMusic.LocalTrack;

namespace Beans.Windows.Rebuild.Pages.LocalMusic;

public sealed partial class LocalMusicPage : UserControl
{
    private readonly ILocalMusicCatalog _catalog;
    private readonly ILocalPlaybackSourceFactory _sourceFactory;
    private readonly IPlaybackService _player;
    private readonly Window _ownerWindow;
    private readonly ObservableCollection<LocalIndexedTrack> _tracks = [];
    private readonly ObservableCollection<LocalMusicGroup> _groups = [];
    private readonly ObservableCollection<LocalIndexedTrack> _groupTracks = [];
    private IReadOnlyList<LocalIndexedTrack> _indexedTracks = [];
    private string _selectedCategory = "Tracks";
    private int _folderCount;
    private CancellationTokenSource? _scanCancellation;
    private bool _loaded;

    public LocalMusicPage(
        ILocalMusicCatalog catalog,
        ILocalPlaybackSourceFactory sourceFactory,
        IPlaybackService player,
        Window ownerWindow)
    {
        _catalog = catalog;
        _sourceFactory = sourceFactory;
        _player = player;
        _ownerWindow = ownerWindow;
        InitializeComponent();
        TrackList.ItemsSource = _tracks;
        GroupList.ItemsSource = _groups;
        GroupTrackList.ItemsSource = _groupTracks;
        _catalog.ProgressChanged += Catalog_ProgressChanged;
        Loaded += LocalMusicPage_Loaded;
        Unloaded += LocalMusicPage_Unloaded;
    }

    private async void LocalMusicPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshAsync();
    }

    private void LocalMusicPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _catalog.ProgressChanged -= Catalog_ProgressChanged;
        _scanCancellation?.Cancel();
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_ownerWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            var before = (await _catalog.GetFoldersAsync()).Count;
            await _catalog.AddFolderAsync(folder.Path);
            var after = (await _catalog.GetFoldersAsync()).Count;
            StatusText.Text = after == before ? "该目录已经添加" : "目录已添加，准备建立本地音乐索引";
            await RefreshAsync();
            await ScanAsync();
        }
        catch (UnauthorizedAccessException) { StatusText.Text = "无法访问该目录，请重新选择。"; }
        catch (IOException) { StatusText.Text = "本地目录暂时不可用，请重新选择。"; }
        catch (Exception) { StatusText.Text = "无法添加该目录，请重试。"; }
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private void CancelScan_Click(object sender, RoutedEventArgs e)
    {
        _scanCancellation?.Cancel();
        StatusText.Text = "正在取消本地音乐扫描";
    }

    private async Task ScanAsync()
    {
        if (_scanCancellation is not null) return;
        _scanCancellation = new CancellationTokenSource();
        ProgressPanel.Visibility = Visibility.Visible;
        CancelScanButton.Visibility = Visibility.Visible;
        RescanButton.IsEnabled = false;
        try
        {
            var token = _scanCancellation.Token;
            var summary = await Task.Run(() => _catalog.ScanAsync(token), token);
            StatusText.Text = summary.Status switch
            {
                LocalScanStatus.Completed => $"扫描完成，新增 {summary.AddedCount} 首，更新 {summary.UpdatedCount} 首",
                LocalScanStatus.Cancelled => "扫描已取消，已发现的歌曲已保留。",
                LocalScanStatus.PartiallyFailed => "扫描完成，但部分文件无法读取。",
                _ => summary.SafeMessage
            };
        }
        catch (OperationCanceledException) { StatusText.Text = "扫描已取消，已发现的歌曲已保留。"; }
        catch (Exception) { StatusText.Text = "本地音乐索引暂时不可用，请重试。"; }
        finally
        {
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            ProgressPanel.Visibility = Visibility.Collapsed;
            CancelScanButton.Visibility = Visibility.Collapsed;
            RescanButton.IsEnabled = true;
            await RefreshAsync();
        }
    }

    private async void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LocalMusicFolder folder })
        {
            await _catalog.RemoveFolderAsync(folder.Id, cancellationToken: CancellationToken.None);
            await RefreshAsync();
            StatusText.Text = "目录已移除，真实文件未删除。";
        }
    }

    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        _selectedCategory = tag;
        TracksCategoryButton.Style = (Style)Application.Current.Resources[ tag == "Tracks" ? "SecondaryButtonStyle" : "GhostButtonStyle"];
        AlbumsCategoryButton.Style = (Style)Application.Current.Resources[ tag == "Albums" ? "SecondaryButtonStyle" : "GhostButtonStyle"];
        ArtistsCategoryButton.Style = (Style)Application.Current.Resources[ tag == "Artists" ? "SecondaryButtonStyle" : "GhostButtonStyle"];
        RenderSelectedCategory();
    }

    private void Group_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LocalMusicGroup group) return;
        _groupTracks.Clear();
        foreach (var track in group.Tracks) _groupTracks.Add(track);
        SelectedGroupTitle.Text = group.Name;
        SelectedGroupDetails.Text = $"{group.SecondaryText} · {group.TrackCountText} · 可播放时长 {group.DurationText}";
        SelectedGroupPanel.Visibility = Visibility.Visible;
        if (group.AvailableTrackCount == 0)
            StatusText.Text = "该分组中的文件均已移动或删除，请重新扫描目录。";
    }

    private void CloseGroup_Click(object sender, RoutedEventArgs e)
    {
        SelectedGroupPanel.Visibility = Visibility.Collapsed;
        _groupTracks.Clear();
    }

    private async void Track_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LocalIndexedTrack track) await PlayAsync(track);
    }

    private async void QueueTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LocalIndexedTrack track })
        {
            var source = await _sourceFactory.CreateAsync(track);
            if (!source.IsSuccess) { StatusText.Text = source.SafeMessage; return; }
            _player.AddToQueue(new PlaybackItem(
                track.Id, track.Title, track.Artist, track.Album, track.ArtworkUri, source.SourceUri!,
                track.Duration, "本地音乐", track.Format, PlatformId.Local, track.Id, SearchDataOrigin.Live));
            StatusText.Text = "已加入播放队列";
        }
    }

    private async Task PlayAsync(LocalIndexedTrack track)
    {
        var source = await _sourceFactory.CreateAsync(track);
        if (!source.IsSuccess) { StatusText.Text = source.SafeMessage; return; }
        await _catalog.RecordPlaybackStartedAsync(track.Id);
        await _player.PlayAsync(new PlaybackItem(
            track.Id, track.Title, track.Artist, track.Album, track.ArtworkUri, source.SourceUri!,
            track.Duration, "本地音乐", track.Format, PlatformId.Local, track.Id, SearchDataOrigin.Live), true);
        StatusText.Text = "正在播放本地音乐";
        await RefreshRecentlyPlayedAsync();
    }

    private async Task RefreshAsync()
    {
        var folders = await _catalog.GetFoldersAsync();
        var tracks = await _catalog.GetTracksAsync(includeMissing: true);
        var availableTracks = tracks.Where(track => !track.IsMissing).ToArray();
        _folderCount = folders.Count;
        _indexedTracks = tracks;
        _tracks.Clear();
        foreach (var track in tracks.OrderBy(track => track.IsMissing).ThenBy(track => track.Title, StringComparer.OrdinalIgnoreCase)) _tracks.Add(track);
        TrackCountText.Text = $"{availableTracks.Length} 首";
        FolderCountText.Text = $"{folders.Count} 个";
        FolderEmptyPanel.Visibility = folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var latestScan = folders.Where(folder => folder.LastScanAt is not null).Select(folder => folder.LastScanAt!.Value).OrderByDescending(value => value).FirstOrDefault();
        LastScanText.Text = latestScan == default ? "尚未扫描" : latestScan.LocalDateTime.ToString("g");
        RenderFolders(folders);
        RenderSelectedCategory();
        await RefreshRecentlyPlayedAsync();
    }

    private void RenderSelectedCategory()
    {
        SelectedGroupPanel.Visibility = Visibility.Collapsed;
        _groupTracks.Clear();
        var hasIndexedTracks = _indexedTracks.Count > 0;

        if (_selectedCategory == "Tracks")
        {
            ContentSectionTitle.Text = "本地歌曲";
            TrackList.Visibility = Visibility.Visible;
            GroupBrowser.Visibility = Visibility.Collapsed;
            _groups.Clear();
            EmptyPanel.Visibility = hasIndexedTracks ? Visibility.Collapsed : Visibility.Visible;
            if (!hasIndexedTracks) SetEmptyState("还没有本地音乐", EmptyCatalogDescription("歌曲"));
            return;
        }

        TrackList.Visibility = Visibility.Collapsed;
        GroupBrowser.Visibility = Visibility.Visible;
        var groups = _selectedCategory == "Albums"
            ? LocalMusicGrouping.ByAlbum(_indexedTracks)
            : LocalMusicGrouping.ByArtist(_indexedTracks);
        _groups.Clear();
        foreach (var group in groups) _groups.Add(group);

        var isAlbum = _selectedCategory == "Albums";
        ContentSectionTitle.Text = isAlbum ? "本地专辑" : "本地艺术家";
        EmptyPanel.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (groups.Count == 0)
            SetEmptyState(isAlbum ? "还没有可显示的专辑" : "还没有可显示的艺术家", EmptyCatalogDescription(isAlbum ? "专辑" : "艺术家"));
    }

    private string EmptyCatalogDescription(string contentName) => _folderCount == 0
        ? $"添加一个音乐文件夹后，{contentName}会显示在这里。"
        : $"已添加的目录中还没有建立可显示的{contentName}索引，请重新扫描。";

    private void SetEmptyState(string title, string description)
    {
        EmptyTitle.Text = title;
        EmptyDescription.Text = description;
    }

    private async Task RefreshRecentlyPlayedAsync()
    {
        RecentlyPlayedText.Text = $"{(await _catalog.GetRecentlyPlayedAsync(20)).Count} 首";
    }

    private void RenderFolders(IReadOnlyList<LocalMusicFolder> folders)
    {
        FolderListPanel.Children.Clear();
        foreach (var folder in folders)
        {
            var panel = new Grid { ColumnSpacing = 8 };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var state = folder.IsAvailable ? (folder.LastScanAt is null ? "待扫描" : $"{folder.TrackCount} 首") : "不可用";
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = folder.DisplayName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = state, Style = (Style)Application.Current.Resources["CaptionTextStyle"] });
            text.Children.Add(new TextBlock { Text = folder.LastScanAt is { } scanned ? $"最近扫描 {scanned.LocalDateTime:g}" : "尚未扫描", Style = (Style)Application.Current.Resources["CaptionTextStyle"] });
            ToolTipService.SetToolTip(text, folder.Path);
            panel.Children.Add(text);
            var remove = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 13 }, Tag = folder, Style = (Style)Application.Current.Resources["IconButtonStyle"] };
            remove.Click += RemoveFolder_Click;
            Grid.SetColumn(remove, 1);
            panel.Children.Add(remove);
            FolderListPanel.Children.Add(new Border { Style = (Style)Application.Current.Resources["SurfacePanelStyle"], Padding = new Thickness(12, 8, 12, 8), Child = panel });
        }
    }

    private void Catalog_ProgressChanged(object? sender, LocalScanProgress progress)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ProgressPanel.Visibility = progress.Status == LocalScanStatus.Scanning ? Visibility.Visible : Visibility.Collapsed;
            ScanProgress.Value = progress.ProgressRatio ?? 0;
            ProgressText.Text = progress.TotalFiles is { } total
                ? $"已处理 {progress.ProcessedFiles} / {total} 个文件 · 新增 {progress.AddedCount} · 更新 {progress.UpdatedCount} · 失败 {progress.FailedCount}"
                : progress.SafeMessage;
            StatusText.Text = progress.SafeMessage;
        });
    }
}
