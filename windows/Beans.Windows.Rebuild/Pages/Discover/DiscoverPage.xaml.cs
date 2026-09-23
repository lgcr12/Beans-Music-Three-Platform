using Beans.Windows.Rebuild.Infrastructure.Navigation;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Beans.Windows.Rebuild.Pages.Discover;

public sealed partial class DiscoverPage : UserControl
{
    private readonly INavigationService _navigation;
    private bool _restoringScroll;

    public DiscoverPage(DiscoverViewModel viewModel, INavigationService navigation)
    {
        ViewModel = viewModel;
        _navigation = navigation;
        InitializeComponent();
        PlatformPicker.SetPlatforms(ViewModel.Platforms, ViewModel.CurrentPlatformId);
        ViewModel.StateChanged += ViewModel_StateChanged;
        Unloaded += DiscoverPage_Unloaded;
        Loaded += DiscoverPage_Loaded;
    }

    public DiscoverViewModel ViewModel { get; }

    public void ApplyResponsiveState(double width)
    {
        var state = width >= 1360 ? "Wide" : width >= 1180 ? "Medium" : width >= 1024 ? "Compact" : "Narrow";
        VisualStateManager.GoToState(this, state, false);
    }

    private async void DiscoverPage_Loaded(object sender, RoutedEventArgs e)
    {
        Render();
        await ViewModel.InitializeAsync();
    }

    private void DiscoverPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveScrollOffset(ContentScroll.VerticalOffset);
        ViewModel.StateChanged -= ViewModel_StateChanged;
        ViewModel.Dispose();
    }

    private void ViewModel_StateChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        var platform = ViewModel.CurrentPlatform;
        var state = ViewModel.CurrentState;
        var content = state.Content;
        PlatformPicker.Select(platform.StableId);
        SubtitleText.Text = ViewModel.Subtitle;
        StatusBadge.Platform = platform.Id;
        StatusText.Text = ViewModel.StatusText;
        var dailyStatus = ViewModel.DailyRecommendationStatusText;
        LoginButton.Content = string.IsNullOrEmpty(dailyStatus)
            ? platform.AuthorizationState == AuthorizationState.Expired ? "重新登录" : "账号与登录"
            : dailyStatus;
        LoginButton.IsEnabled = dailyStatus != "当前版本暂不支持 QQ 音乐每日推荐";
        RefreshProgress.Visibility = state.IsRefreshing ? Visibility.Visible : Visibility.Collapsed;
        InitialLoading.Visibility = state.IsLoading && !state.HasContent ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = state.ErrorState is null ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = state.ErrorState ?? string.Empty;
        EmptyPanel.Visibility = state.IsLoaded && content is null ? Visibility.Visible : Visibility.Collapsed;

        if (content is null) return;
        HeroBadge.Platform = platform.Id;
        HeroTitle.Text = content.HeroContent.Title;
        HeroSubtitle.Text = content.HeroContent.Subtitle;
        HeroAction.Content = content.HeroContent.ActionLabel;
        HeroImage.Source = new BitmapImage(new Uri(content.HeroContent.ImageUri));
        RankingList.ItemsSource = content.Rankings;
        RecommendedList.ItemsSource = content.RecommendedPlaylists;
        RenderRecommendedState(content, state);
        SquareList.ItemsSource = content.PlaylistSquare;
        var squareState = content.SectionStates?.FirstOrDefault(section => section.SectionId == "playlist-square");
        SquareStatusText.Text = content.IsPreview
            ? "预览内容"
            : squareState?.ErrorCode == PlatformErrorCode.Unsupported ? "当前阶段未接入" : content.SafeStatusText;
        RenderCategories(content.Categories, state.SelectedCategory);

        if (!_restoringScroll)
        {
            _restoringScroll = true;
            ContentScroll.ChangeView(null, state.ScrollOffset, null, true);
            _restoringScroll = false;
        }
        DispatcherQueue.TryEnqueue(UpdateRankingCardWidths);
    }

    private void RenderRecommendedState(PlatformDiscoveryContent content, PlatformDiscoveryState state)
    {
        var section = content.SectionStates?.FirstOrDefault(item => item.SectionId == "recommended-playlists");
        if (content.RecommendedPlaylists.Count > 0)
        {
            RecommendedList.Visibility = Visibility.Visible;
            RecommendedStatePanel.Visibility = Visibility.Collapsed;
            return;
        }

        RecommendedList.Visibility = Visibility.Collapsed;
        RecommendedStatePanel.Visibility = Visibility.Visible;
        RecommendedStateText.Text = state.IsRefreshing
            ? $"正在加载 {ViewModel.CurrentPlatform.DisplayName} 精选歌单…"
            : section?.ErrorCode == PlatformErrorCode.Unsupported
                ? $"当前版本暂不支持 {ViewModel.CurrentPlatform.DisplayName} 推荐歌单"
                : section?.DataOrigin == DiscoveryDataOrigin.CacheStale
                    ? "网络不可用，正在显示上次内容"
                    : section is { IsSuccess: false } && !string.IsNullOrWhiteSpace(section.SafeMessage)
                        ? section.SafeMessage
                        : "暂无可显示的推荐歌单";
    }

    private void RankingList_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateRankingCardWidths();

    private void UpdateRankingCardWidths()
    {
        if (RankingList.ActualWidth <= 0) return;
        var columns = RankingList.ActualWidth >= 1040 ? 4 : RankingList.ActualWidth >= 740 ? 3 : 2;
        var gap = 12 * (columns - 1);
        var cardWidth = Math.Clamp((RankingList.ActualWidth - gap) / columns, 200, 280);
        for (var index = 0; index < RankingList.Items.Count; index++)
        {
            if (RankingList.ContainerFromIndex(index) is ListViewItem item) item.Width = cardWidth;
        }
    }

    private void RenderCategories(IReadOnlyList<string> categories, string? selected)
    {
        CategoryPanel.Children.Clear();
        foreach (var category in categories)
        {
            var button = new Button
            {
                Content = category,
                Tag = category,
                MinHeight = 34,
                Padding = new Thickness(13, 5, 13, 5),
                Style = (Style)Application.Current.Resources[selected == category ? "SecondaryButtonStyle" : "GhostButtonStyle"]
            };
            button.Click += Category_Click;
            CategoryPanel.Children.Add(button);
        }
    }

    private async void PlatformPicker_PlatformChanged(object? sender, string platformId)
    {
        ViewModel.SaveScrollOffset(ContentScroll.VerticalOffset);
        await ViewModel.SwitchPlatformAsync(platformId);
        ContentScroll.ChangeView(null, ViewModel.CurrentState.ScrollOffset, null, true);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();
    private void Login_Click(object sender, RoutedEventArgs e) => _navigation.Navigate("accounts", new PlatformRouteParameter(ViewModel.CurrentPlatformId, "account", ViewModel.CurrentPlatform.DisplayName));

    private void HeroAction_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Content?.HeroContent.TrackId is { Length: > 0 } nativeId)
        {
            var route = ViewModel.Content.Rankings.Any(ranking => ranking.Id == nativeId) ? "ranking-detail" : "playlist";
            _navigation.Navigate(route, new PlatformRouteParameter(ViewModel.CurrentPlatformId, nativeId, ViewModel.Content.HeroContent.Title));
            return;
        }
        if (ViewModel.CurrentPlatform.AuthorizationState is AuthorizationState.SignedOut or AuthorizationState.Expired)
        {
            Login_Click(sender, e);
            return;
        }
        ContentScroll.ChangeView(null, 240, null, false);
    }

    private void RankingList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RankingSummary ranking) _navigation.Navigate("ranking-detail", new PlatformRouteParameter(ranking.PlatformId, ranking.Id, ranking.Title));
    }

    private void PlaylistList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MusicPlaylist playlist) _navigation.Navigate("playlist", new PlatformRouteParameter(playlist.Identity.Platform.ToStableId(), playlist.Identity.NativeId, playlist.Title));
    }

    private async void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string category }) await ViewModel.SelectCategoryAsync(category);
    }

    private void ContentScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_restoringScroll) ViewModel.SaveScrollOffset(ContentScroll.VerticalOffset);
    }
}
