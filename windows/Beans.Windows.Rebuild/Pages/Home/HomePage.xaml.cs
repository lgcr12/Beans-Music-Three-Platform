using Beans.Windows.Rebuild.Controls.PlaylistCard;
using Beans.Windows.Rebuild.Infrastructure.Navigation;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Playback;
using Beans.Windows.Rebuild.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beans.Windows.Rebuild.Pages.Home;

public sealed partial class HomePage : UserControl
{
    private readonly INavigationService _navigation;
    private bool _isLoaded;
    public HomeViewModel ViewModel { get; }
    public IPlaybackService Player { get; }

    public HomePage(HomeViewModel viewModel, IPlaybackService player, INavigationService navigation)
    {
        ViewModel = viewModel;
        Player = player;
        _navigation = navigation;
        InitializeComponent();

        foreach (var card in PlaylistCards()) card.PlayRequested += Playlist_PlayRequested;
        ViewModel.StateChanged += ViewModel_StateChanged;
        Loaded += HomePage_Loaded;
        Unloaded += HomePage_Unloaded;
        ApplyContentState();
    }

    public void ApplyResponsiveState(double windowWidth)
    {
        var state = windowWidth >= 1360 ? "Wide" : windowWidth >= 1180 ? "Medium" : windowWidth >= 1024 ? "Compact" : "Narrow";
        VisualStateManager.GoToState(this, state, false);
        ApplyPlaylistCards();
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded) return;
        _isLoaded = true;
        try { await ViewModel.InitializeAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception) { PageStatusText.Text = "首页暂时无法加载，请稍后重试"; }
        ApplyContentState();
    }

    private void HomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        ViewModel.StateChanged -= ViewModel_StateChanged;
    }

    private void ViewModel_StateChanged(object? sender, EventArgs e) => ApplyContentState();

    private void ApplyContentState()
    {
        ApplyPlaylistCards();
        HeroActionButton.IsEnabled = ViewModel.HasHeroTarget && !ViewModel.IsLoading;
        PlaylistCardsPanel.Visibility = ViewModel.HasPlaylists ? Visibility.Visible : Visibility.Collapsed;
        RecentTracksList.Visibility = ViewModel.HasTracks ? Visibility.Visible : Visibility.Collapsed;
        TrackEmptyText.Visibility = ViewModel.HasTracks ? Visibility.Collapsed : Visibility.Visible;
        RecommendationList.Visibility = ViewModel.HasRecommendations ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyPlaylistCards()
    {
        var cards = PlaylistCards();
        for (var index = 0; index < cards.Length; index++)
        {
            if (index >= ViewModel.Playlists.Count)
            {
                cards[index].Visibility = Visibility.Collapsed;
                continue;
            }
            ApplyPlaylist(cards[index], ViewModel.Playlists[index]);
            cards[index].Visibility = IsCardAllowedByWidth(index) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private bool IsCardAllowedByWidth(int index)
    {
        var width = ActualWidth;
        var limit = width >= 1180 ? 4 : width >= 1024 ? 3 : 2;
        return index < limit;
    }

    private PlaylistCard[] PlaylistCards() => [PlaylistCard1, PlaylistCard2, PlaylistCard3, PlaylistCard4];

    private static void ApplyPlaylist(PlaylistCard card, HomePlaylist playlist)
    {
        card.Title = playlist.Title;
        card.Subtitle = playlist.Subtitle;
        card.ImageUri = playlist.ImageUri;
        card.PlayCount = playlist.PlayCount;
        card.SourcePlatform = playlist.SourcePlatform;
        card.Tag = playlist.Id;
    }

    private void HeroAction_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.HeroTarget is { } target) _navigation.Navigate(target.Route, target.Parameter);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.InitializeAsync(true); }
        catch (OperationCanceledException) { }
        catch (Exception) { PageStatusText.Text = "首页刷新失败，请稍后重试"; }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => Player.TogglePlayPause();
    private void Previous_Click(object sender, RoutedEventArgs e) => Player.Previous();
    private void Next_Click(object sender, RoutedEventArgs e) => Player.Next();
    private void Shuffle_Click(object sender, RoutedEventArgs e) => Player.ToggleShuffle();
    private void Repeat_Click(object sender, RoutedEventArgs e) => Player.CycleRepeatMode();
    private void Favorite_Click(object sender, RoutedEventArgs e) => Player.ToggleFavorite();
    private void ViewMorePlaylists_Click(object sender, RoutedEventArgs e) => _navigation.Navigate("playlists");
    private void ViewMoreTracks_Click(object sender, RoutedEventArgs e) => _navigation.Navigate("library");
    private void ViewLibrary_Click(object sender, RoutedEventArgs e) => _navigation.Navigate("library");

    private void Playlist_PlayRequested(object? sender, EventArgs e)
    {
        if (sender is PlaylistCard card)
            _navigation.Navigate("playlist", new PlatformRouteParameter(card.SourcePlatform.ToStableId(), card.Tag as string ?? string.Empty, card.Title));
    }

    private async void RecentTracks_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HomeTrack track) await PlayAsync(track.PlaybackTarget);
    }

    private async void Recommendations_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HomeRecommendation item) await PlayAsync(item.PlaybackTarget);
    }

    private async Task PlayAsync(SearchResultItem item)
    {
        try
        {
            var result = await Player.PlaySearchResultAsync(item, true);
            PageStatusText.Text = result.IsSuccess ? $"正在播放“{item.Title}”" : result.SafeMessage;
        }
        catch (OperationCanceledException) { PageStatusText.Text = "播放请求已取消"; }
        catch (Exception) { PageStatusText.Text = "暂时无法播放该歌曲，请稍后重试"; }
    }
}
