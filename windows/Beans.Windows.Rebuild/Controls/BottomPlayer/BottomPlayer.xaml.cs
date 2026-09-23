using Beans.Windows.Rebuild.Services.Playback;
using Beans.Windows.Rebuild.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Beans.Windows.Rebuild.Controls.BottomPlayer;

public sealed partial class BottomPlayer : UserControl
{
    private bool _isSeeking;
    public IPlaybackService Player { get; }
    public event EventHandler<string>? RouteRequested;

    public BottomPlayer(IPlaybackService player)
    {
        Player = player;
        InitializeComponent();
        ProgressSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(ProgressSlider_PointerPressed), true);
        ProgressSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(ProgressSlider_PointerReleased), true);
        Player.PropertyChanged += Player_PropertyChanged;
        UpdatePlaybackModeVisual();
    }

    public void ApplyResponsiveState(double width)
    {
        var state = width >= 1360 ? "Wide" : width >= 1180 ? "Medium" : "Compact";
        VisualStateManager.GoToState(this, state, false);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => Player.TogglePlayPause();
    private void Previous_Click(object sender, RoutedEventArgs e) => Player.Previous();
    private void Next_Click(object sender, RoutedEventArgs e) => Player.Next();
    private void PlaybackMode_Click(object sender, RoutedEventArgs e)
    {
        if (Player.IsShuffleEnabled)
        {
            Player.ToggleShuffle();
            SetRepeatMode(PlaybackRepeatMode.All);
            return;
        }

        switch (Player.RepeatMode)
        {
            case PlaybackRepeatMode.Off:
                Player.ToggleShuffle();
                break;
            case PlaybackRepeatMode.All:
                Player.CycleRepeatMode();
                break;
            case PlaybackRepeatMode.One:
                Player.CycleRepeatMode();
                break;
        }
    }

    private async void QualityOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<AudioQuality>(tag, out var quality)) return;
        await Player.SetQualityAsync(quality);
        QualityButton.Flyout?.Hide();
    }
    private void Favorite_Click(object sender, RoutedEventArgs e) => Player.ToggleFavorite();
    private void OpenPlayer_Tapped(object sender, TappedRoutedEventArgs e) => RouteRequested?.Invoke(this, "player");
    private void Lyrics_Click(object sender, RoutedEventArgs e) => RouteRequested?.Invoke(this, "lyrics");
    private void Queue_Click(object sender, RoutedEventArgs e) => RouteRequested?.Invoke(this, "queue");
    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Player.SetVolume(e.NewValue);
    private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e) { _isSeeking = true; Player.BeginSeek(); }
    private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e) { if (!_isSeeking) return; _isSeeking = false; Player.EndSeek(TimeSpan.FromSeconds(ProgressSlider.Value)); }
    private void ProgressSlider_KeyUp(object sender, KeyRoutedEventArgs e) => Player.Seek(TimeSpan.FromSeconds(ProgressSlider.Value));

    private void Player_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPlaybackService.IsShuffleEnabled) or nameof(IPlaybackService.RepeatMode))
            UpdatePlaybackModeVisual();
    }

    private void SetRepeatMode(PlaybackRepeatMode target)
    {
        for (var i = 0; i < 3 && Player.RepeatMode != target; i++) Player.CycleRepeatMode();
    }

    private void UpdatePlaybackModeVisual()
    {
        var (glyph, label, active) = Player.IsShuffleEnabled
            ? ("\uE8B1", "随机播放", true)
            : Player.RepeatMode switch
            {
                PlaybackRepeatMode.All => ("\uE8EE", "列表循环", true),
                PlaybackRepeatMode.One => ("\uE8EE", "单曲循环", true),
                _ => ("\uE8EE", "顺序播放", false)
            };
        PlaybackModeIcon.Glyph = glyph;
        PlaybackModeIcon.Opacity = active ? 1 : 0.68;
        ToolTipService.SetToolTip(PlaybackModeButton, label);
    }
}
