using Beans.Windows.Rebuild.Infrastructure.Navigation;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Playback;
using Beans.Windows.Rebuild.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beans.Windows.Rebuild.Pages.Search;

public sealed partial class SearchPage : UserControl
{
    private readonly INavigationService _navigation;
    private readonly IPlaybackService _player;
    private bool _initialized;
    public SearchViewModel ViewModel { get; }

    public SearchPage(SearchViewModel viewModel, IPlaybackService player, INavigationService navigation, string? initialQuery)
    {
        ViewModel = viewModel;
        _player = player;
        _navigation = navigation;
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            await ViewModel.InitializeAsync(initialQuery);
            Render();
        };
        Unloaded += SearchPage_Unloaded;
    }

    public void ApplyResponsiveState(double width)
    {
        var compact = width < 1180;
        SourceRail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SourceRailColumn.Width = compact ? new GridLength(0) : new GridLength(270);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Render();

    private void Render()
    {
        QueryTitle.Text = string.IsNullOrWhiteSpace(ViewModel.QueryText) ? "搜索" : $"搜索“{ViewModel.QueryText}”";
        ResultSummary.Text = ViewModel.StatusText;
        PartialHint.Text = ViewModel.IsPartialSuccess ? "部分来源不可用，已显示可用结果" : string.Empty;
        LoadingPanel.Visibility = ViewModel.IsSearching ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = !ViewModel.IsSearching && !ViewModel.HasResults && !string.IsNullOrWhiteSpace(ViewModel.QueryText) ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = ViewModel.HasSources && ViewModel.Sources.All(source => source.State == SearchSourceState.CatalogUnavailable)
            ? "添加本地音乐目录后，可在这里搜索歌曲"
            : ViewModel.StatusText;
        NoticePanel.Visibility = string.IsNullOrWhiteSpace(ViewModel.NoticeText) ? Visibility.Collapsed : Visibility.Visible;
        NoticeText.Text = ViewModel.NoticeText;
        SuggestionsPanel.Visibility = ViewModel.HasSuggestions && !ViewModel.IsSearching ? Visibility.Visible : Visibility.Collapsed;
        LocalBoundaryText.Visibility = ViewModel.Sources.Any(source => source.Platform == PlatformId.Local) ? Visibility.Collapsed : Visibility.Visible;
        ApplySelection(new[] { AggregateScopeButton, QqScopeButton, NetEaseScopeButton, LocalScopeButton }, ViewModel.Scope.ToString());
        ApplySelection(new[] { AllFilterButton, TracksFilterButton, AlbumsFilterButton, ArtistsFilterButton, PlaylistsFilterButton }, ViewModel.Filter.ToString());
    }

    private static void ApplySelection(IEnumerable<Button> buttons, string selected)
    {
        foreach (var button in buttons) button.Style = (Style)Application.Current.Resources[button.Tag?.ToString() == selected ? "SecondaryButtonStyle" : "GhostButtonStyle"];
    }

    private async void Scope_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<SearchSourceScope>(tag, out var scope)) await ViewModel.SetScopeAsync(scope);
    }

    private async void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<SearchResultFilter>(tag, out var filter)) await ViewModel.SetFilterAsync(filter);
    }

    private async void Suggestion_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchSuggestion suggestion) await ViewModel.SubmitSuggestionAsync(suggestion);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ViewModel.SearchAsync(true);
    private void Back_Click(object sender, RoutedEventArgs e) => _navigation.Navigate("home");

    private async void Result_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchResultItem item) await PlayAsync(item);
    }

    private async void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not SearchResultItem item) return;
        var menu = new MenuFlyout();
        var canAttemptPlayback = CanAttemptPlayback(item);
        var play = new MenuFlyoutItem { Text = "播放", IsEnabled = canAttemptPlayback };
        play.Click += async (_, _) => await PlayAsync(item);
        var next = new MenuFlyoutItem { Text = "下一首播放", IsEnabled = canAttemptPlayback };
        next.Click += async (_, _) => await QueueItemAsync(item, true);
        var queue = new MenuFlyoutItem { Text = "加入队列", IsEnabled = canAttemptPlayback };
        queue.Click += async (_, _) => await QueueItemAsync(item, false);
        menu.Items.Add(play);
        menu.Items.Add(next);
        menu.Items.Add(queue);
        menu.Items.Add(new MenuFlyoutSeparator());
        AddRouteItem(menu, "打开歌手", "artist", item, item.Artist);
        AddRouteItem(menu, "打开专辑", "album", item, item.Album);
        AddRouteItem(menu, "打开歌单", "playlist", item, item.Title);
        menu.Items.Add(new MenuFlyoutItem { Text = "添加到歌单（后续阶段开放）", IsEnabled = false });
        menu.Items.Add(new MenuFlyoutItem { Text = "收藏（后续阶段开放）", IsEnabled = false });
        menu.ShowAt(button);
    }

    private void AddRouteItem(MenuFlyout menu, string text, string route, SearchResultItem item, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        var entry = new MenuFlyoutItem { Text = text };
        entry.Click += (_, _) => _navigation.Navigate(route, new PlatformRouteParameter(item.Platform.ToStableId(), item.NativeId, title));
        menu.Items.Add(entry);
    }

    private async Task PlayAsync(SearchResultItem item)
    {
        if (!CanAttemptPlayback(item)) { ViewModel.SetNotice(PlaybackNotice(item)); return; }
        var result = await _player.PlaySearchResultAsync(item, true);
        if (!result.IsSuccess)
        {
            ViewModel.SetNotice(result.SafeMessage);
            return;
        }
        foreach (var sibling in ViewModel.Results
                     .Where(value => value.ResultType == SearchResultType.Track && value.StableId != item.StableId)
                     .Take(50))
        {
            var queued = await _player.QueueSearchResultAsync(sibling);
            if (!queued.IsSuccess) break;
        }
    }

    private async Task QueueItemAsync(SearchResultItem item, bool next)
    {
        if (!CanAttemptPlayback(item)) { ViewModel.SetNotice(PlaybackNotice(item)); return; }
        var result = await _player.QueueSearchResultAsync(item, next);
        if (!result.IsSuccess) ViewModel.SetNotice(result.SafeMessage);
    }

    private static bool CanAttemptPlayback(SearchResultItem item) => item.ResultType == SearchResultType.Track &&
        (item.IsPlayable || item.Platform is PlatformId.QqMusic or PlatformId.NetEaseMusic);

    private static string PlaybackNotice(SearchResultItem item) => item.Platform is PlatformId.QqMusic or PlatformId.NetEaseMusic
        ? "请先登录后播放该平台歌曲"
        : item.RestrictionState.Length > 0 ? item.RestrictionState : "文件不可用";

    private void SearchPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.LeavePage();
    }
}
