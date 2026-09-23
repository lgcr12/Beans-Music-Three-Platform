using System.Collections.ObjectModel;
using System.ComponentModel;
using Beans.Windows.Rebuild.Infrastructure.Navigation;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Lyrics;
using Beans.Windows.Rebuild.Services.Playback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Text;
using Windows.UI.Text;
using Windows.UI;

namespace Beans.Windows.Rebuild.Pages.Player;

public sealed record PlayerVisualStyleOption(
    string Key,
    string Name,
    Color Background,
    Color Accent,
    Color Text,
    Color Muted,
    byte OverlayAlpha,
    string BackgroundAsset,
    string OverlayAsset);

public sealed class PlayerLyricRow : INotifyPropertyChanged
{
    private bool _active;
    private Brush _foreground = new SolidColorBrush(Colors.White);
    private double _opacity = 0.42;
    private double _fontSize = 17;
    private double _translationFontSize = 12;
    private double _translationOpacity = 0.48;
    private FontWeight _weight = FontWeights.Normal;
    public PlayerLyricRow(TimeSpan timestamp, string text, string translation) { Timestamp = timestamp; Text = text; Translation = translation; }
    public TimeSpan Timestamp { get; }
    public string Text { get; }
    public string Translation { get; }
    public bool IsActive { get => _active; private set { if (_active == value) return; _active = value; OnChanged(nameof(IsActive)); } }
    public Brush Foreground { get => _foreground; private set { _foreground = value; OnChanged(nameof(Foreground)); } }
    public double Opacity { get => _opacity; private set { _opacity = value; OnChanged(nameof(Opacity)); } }
    public double FontSize { get => _fontSize; private set { _fontSize = value; OnChanged(nameof(FontSize)); } }
    public double TranslationFontSize { get => _translationFontSize; private set { _translationFontSize = value; OnChanged(nameof(TranslationFontSize)); } }
    public double TranslationOpacity { get => _translationOpacity; private set { _translationOpacity = value; OnChanged(nameof(TranslationOpacity)); } }
    public FontWeight Weight { get => _weight; private set { _weight = value; OnChanged(nameof(Weight)); } }
    public double SizeScale { get; set; } = 1;
    public void ApplyStyle(PlayerVisualStyleOption style, int distance)
    {
        var active = distance == 0;
        IsActive = active;
        FontSize = (active ? 30 : distance == 1 ? 21 : 17) * SizeScale;
        TranslationFontSize = Math.Max(10, FontSize * 0.46);
        Opacity = active ? 1 : distance == 1 ? 0.78 : Math.Max(0.25, 0.62 - (Math.Min(distance, 4) * 0.08));
        TranslationOpacity = active ? 0.7 : Math.Min(0.52, Opacity * 0.7);
        Weight = active ? FontWeights.SemiBold : FontWeights.Normal;
        Foreground = new SolidColorBrush(active
            ? Color.FromArgb(255, 184, 255, 229)
            : style.Text);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new(name));
}

public sealed partial class PlayerPage : UserControl
{
    private readonly IPlaybackService _player;
    private readonly INavigationService _navigation;
    private readonly ILyricsService _lyrics;
    private CancellationTokenSource? _lyricsCancellation;
    private LyricDocument? _document;
    private int _activeIndex = -1;
    private bool _isSeeking;
    private PlayerVisualStyleOption _selectedStyle;
    private bool _motionStarted;
    private readonly Storyboard _waveStoryboard = new();
    private readonly Storyboard _backdropStoryboard = new();
    private readonly Storyboard _overlayStoryboard = new();
    private readonly Storyboard _lyricSweepStoryboard = new();

    public IPlaybackService Player => _player;
    public ObservableCollection<PlayerLyricRow> Lines { get; } = [];
    public IReadOnlyList<PlayerVisualStyleOption> VisualStyles { get; } =
    [
        new("aurora", "极光青", Color.FromArgb(255, 5, 37, 48), Color.FromArgb(255, 70, 245, 221), Colors.White, Color.FromArgb(255, 180, 220, 220), 205, "aurora.png", "aurora-glow.png"),
        new("ocean", "深海蓝", Color.FromArgb(255, 2, 24, 48), Color.FromArgb(255, 79, 215, 255), Colors.White, Color.FromArgb(255, 171, 205, 225), 205, "ocean.png", "ocean-glow.png"),
        new("mist", "浅色雾光", Color.FromArgb(255, 225, 242, 241), Color.FromArgb(255, 12, 160, 145), Color.FromArgb(255, 17, 57, 58), Color.FromArgb(255, 86, 126, 126), 210, "mist.png", "mist-glow.png"),
        new("film", "暖色胶片", Color.FromArgb(255, 51, 34, 27), Color.FromArgb(255, 255, 184, 81), Colors.White, Color.FromArgb(255, 222, 192, 160), 210, "film.png", "film-glow.png"),
        new("stage", "动漫舞台", Color.FromArgb(255, 31, 20, 66), Color.FromArgb(255, 255, 113, 222), Colors.White, Color.FromArgb(255, 216, 190, 238), 205, "stage.png", "stage-glow.png"),
        new("live", "现场蓝光", Color.FromArgb(255, 5, 37, 48), Color.FromArgb(255, 108, 241, 255), Colors.White, Color.FromArgb(255, 190, 226, 235), 150, "live.png", "live-glow.png"),
        new("minimal", "极简黑", Color.FromArgb(255, 16, 18, 20), Color.FromArgb(255, 255, 255, 255), Colors.White, Color.FromArgb(255, 180, 184, 190), 220, "ocean.png", "ocean-glow.png")
    ];

    public PlayerPage() : this(App.Services.GetRequiredService<IPlaybackService>(), App.Services.GetRequiredService<INavigationService>(), App.Services.GetRequiredService<ILyricsService>()) { }

    public PlayerPage(IPlaybackService player, INavigationService navigation, ILyricsService lyrics)
    {
        _player = player;
        _navigation = navigation;
        _lyrics = lyrics;
        _selectedStyle = VisualStyles[0];
        InitializeComponent();
        StyleSelector.SelectedIndex = 0;
        ProgressSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(ProgressSlider_PointerPressed), true);
        ProgressSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(ProgressSlider_PointerReleased), true);
        _player.PropertyChanged += Player_PropertyChanged;
        Loaded += PlayerPage_Loaded;
        Unloaded += PlayerPage_Unloaded;
        ApplyStyle(_selectedStyle);
        AddWaveAnimation(WaveOneTransform, -180, 180, 12);
        AddWaveAnimation(WaveTwoTransform, 160, -160, 9);
        AddBackdropAnimation(BackdropScale, 1.04, 1.09, 18);
        AddOverlayAnimation(OverlayScale, OverlayTransform, 1.02, 1.08, -18, 18, 22);
        AddLyricSweepAnimation();
    }

    private async void PlayerPage_Loaded(object sender, RoutedEventArgs e)
    {
        StartMotion();
        await LoadLyricsAsync();
    }

    private void PlayerPage_Unloaded(object sender, RoutedEventArgs e)
    {
        CancelLyrics();
        StopMotion();
    }
    private void Back_Click(object sender, RoutedEventArgs e) => _navigation.Navigate("home");
    private void Queue_Click(object sender, RoutedEventArgs e) => _navigation.Navigate("queue");
    private void Lyrics_Click(object sender, RoutedEventArgs e) => _navigation.Navigate("lyrics");
    private void PlayPause_Click(object sender, RoutedEventArgs e) => _player.TogglePlayPause();
    private void Previous_Click(object sender, RoutedEventArgs e) => _player.Previous();
    private void Next_Click(object sender, RoutedEventArgs e) => _player.Next();
    private void Shuffle_Click(object sender, RoutedEventArgs e) => _player.ToggleShuffle();
    private void Repeat_Click(object sender, RoutedEventArgs e) => _player.CycleRepeatMode();
    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => _player.SetVolume(e.NewValue);
    private async void QualityOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<AudioQuality>(tag, out var quality)) return;
        await _player.SetQualityAsync(quality);
        QualityButton.Flyout?.Hide();
    }
    private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e) { _isSeeking = true; _player.BeginSeek(); }
    private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e) { if (!_isSeeking) return; _isSeeking = false; _player.EndSeek(TimeSpan.FromSeconds(ProgressSlider.Value)); }
    private void ProgressSlider_KeyUp(object sender, KeyRoutedEventArgs e) => _player.Seek(TimeSpan.FromSeconds(ProgressSlider.Value));
    private void StyleSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (StyleSelector.SelectedItem is PlayerVisualStyleOption style) ApplyStyle(style); }
    private void LyricSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        foreach (var line in Lines)
            line.SizeScale = e.NewValue;
        if (_document is not null) UpdateActiveLine();
    }

    private void Player_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IPlaybackService.Current)) _ = LoadLyricsAsync();
        else if (e.PropertyName == nameof(IPlaybackService.PositionSeconds)) UpdateActiveLine();
        else if (e.PropertyName == nameof(IPlaybackService.IsPlaying)) UpdateMotionSpeed();
    }

    private async Task LoadLyricsAsync()
    {
        CancelLyrics();
        Lines.Clear();
        _document = null;
        _activeIndex = -1;
        if (_player.Current is not { } current)
        {
            LyricStatePanel.Visibility = Visibility.Visible;
            LyricList.Visibility = Visibility.Collapsed;
            LyricSweep.Visibility = Visibility.Collapsed;
            return;
        }
        var cancellation = new CancellationTokenSource();
        _lyricsCancellation = cancellation;
        try
        {
            var result = await _lyrics.GetLyricsAsync(LyricsRequest.FromPlaybackItem(current), cancellation.Token);
            if (cancellation.IsCancellationRequested || _lyricsCancellation != cancellation) return;
            if (!result.IsSuccess || result.Document is not { } document)
            {
                LyricStatePanel.Visibility = Visibility.Visible;
                LyricList.Visibility = Visibility.Collapsed;
                LyricSweep.Visibility = Visibility.Collapsed;
                return;
            }
            _document = document;
            foreach (var line in document.Lines) Lines.Add(new PlayerLyricRow(line.Timestamp, line.Text, line.Translation ?? string.Empty));
            LyricStatePanel.Visibility = Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LyricList.Visibility = Lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            LyricSweep.Visibility = Lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            LyricSweep.Opacity = Lines.Count == 0 ? 0 : 0.2;
            UpdateActiveLine();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch
        {
            LyricStatePanel.Visibility = Visibility.Visible;
            LyricList.Visibility = Visibility.Collapsed;
            LyricSweep.Visibility = Visibility.Collapsed;
        }
        finally { if (_lyricsCancellation == cancellation) _lyricsCancellation = null; cancellation.Dispose(); }
    }

    private void UpdateActiveLine()
    {
        if (_document is null || Lines.Count == 0) return;
        var position = TimeSpan.FromSeconds(Math.Max(0, _player.PositionSeconds)) - _document.Offset;
        var index = -1;
        for (var i = 0; i < Lines.Count && Lines[i].Timestamp <= position; i++) index = i;
        _activeIndex = Math.Clamp(index, -1, Lines.Count - 1);
        for (var i = 0; i < Lines.Count; i++) Lines[i].ApplyStyle(_selectedStyle, Math.Abs(i - _activeIndex));
        if (_activeIndex >= 0)
        {
            LyricList.ScrollIntoView(Lines[_activeIndex], ScrollIntoViewAlignment.Leading);
            SideCurrentLyric.Text = Lines[_activeIndex].Text;
            SideNextLyric.Text = _activeIndex + 1 < Lines.Count ? Lines[_activeIndex + 1].Text : string.Empty;
        }
        LyricProgress.Value = _player.DurationSeconds <= 0 ? 0 : Math.Clamp(_player.PositionSeconds / _player.DurationSeconds, 0, 1);
        LyricProgressText.Text = $"{_player.PositionText} / {_player.DurationText}";
    }

    private void ApplyStyle(PlayerVisualStyleOption style)
    {
        _selectedStyle = style;
        RootGrid.Background = new SolidColorBrush(style.Background);
        BackdropImage.Source = new BitmapImage(new Uri($"ms-appx:///Assets/Player/Backgrounds/{style.BackgroundAsset}"));
        BackdropOverlayImage.Source = new BitmapImage(new Uri($"ms-appx:///Assets/Player/Overlays/{style.OverlayAsset}"));
        BackdropTint.Opacity = style.OverlayAlpha / 255d;
        LocalBlurVeil.Opacity = style.Key == "mist" ? 0.08 : 0.12;
        var accent = new SolidColorBrush(style.Accent);
        var text = new SolidColorBrush(style.Text);
        WaveOne.Background = accent;
        WaveTwo.Background = accent;
        HeaderTitle.Foreground = text;
        HeaderSource.Foreground = new SolidColorBrush(style.Muted);
        LyricProgress.Foreground = accent;
        LyricSweep.Visibility = Lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        LyricSweep.Opacity = Lines.Count == 0 ? 0 : 0.2;
        for (var i = 0; i < Lines.Count; i++) Lines[i].ApplyStyle(style, Math.Abs(i - _activeIndex));
    }

    private void CancelLyrics() { _lyricsCancellation?.Cancel(); _lyricsCancellation = null; }

    private void AddWaveAnimation(TranslateTransform target, double from, double to, double seconds)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromSeconds(seconds)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "X");
        _waveStoryboard.Children.Add(animation);
    }

    private void AddBackdropAnimation(ScaleTransform target, double from, double to, double seconds)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromSeconds(seconds)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "ScaleX");
        _backdropStoryboard.Children.Add(animation);
        var yAnimation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromSeconds(seconds)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(yAnimation, target);
        Storyboard.SetTargetProperty(yAnimation, "ScaleY");
        _backdropStoryboard.Children.Add(yAnimation);
    }

    private void AddOverlayAnimation(ScaleTransform scale, TranslateTransform translate, double from, double to, double xFrom, double xTo, double seconds)
    {
        var scaleAnimation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromSeconds(seconds)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(scaleAnimation, scale);
        Storyboard.SetTargetProperty(scaleAnimation, "ScaleX");
        _overlayStoryboard.Children.Add(scaleAnimation);
        var yAnimation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromSeconds(seconds)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(yAnimation, scale);
        Storyboard.SetTargetProperty(yAnimation, "ScaleY");
        _overlayStoryboard.Children.Add(yAnimation);
        var xAnimation = new DoubleAnimation
        {
            From = xFrom,
            To = xTo,
            Duration = new Duration(TimeSpan.FromSeconds(seconds * 1.4)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(xAnimation, translate);
        Storyboard.SetTargetProperty(xAnimation, "X");
        _overlayStoryboard.Children.Add(xAnimation);
    }

    private void AddLyricSweepAnimation()
    {
        var animation = new DoubleAnimation
        {
            From = -360,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(4.2)),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(animation, LyricSweepTransform);
        Storyboard.SetTargetProperty(animation, "X");
        _lyricSweepStoryboard.Children.Add(animation);
        var breath = new DoubleAnimation
        {
            From = 0.08,
            To = 0.24,
            Duration = new Duration(TimeSpan.FromSeconds(2.4)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(breath, LyricSweep);
        Storyboard.SetTargetProperty(breath, "Opacity");
        _lyricSweepStoryboard.Children.Add(breath);
    }

    private void UpdateMotionSpeed()
    {
        if (!_motionStarted) return;
        var multiplier = _player.IsPlaying ? 1d : 2.6d;
        _backdropStoryboard.Stop();
        _overlayStoryboard.Stop();
        _lyricSweepStoryboard.Stop();
        foreach (var animation in _backdropStoryboard.Children)
            animation.Duration = new Duration(TimeSpan.FromSeconds(18 * multiplier));
        for (var i = 0; i < _overlayStoryboard.Children.Count; i++)
            _overlayStoryboard.Children[i].Duration = new Duration(TimeSpan.FromSeconds((i == 2 ? 30 : 22) * multiplier));
        _lyricSweepStoryboard.Children[0].Duration = new Duration(TimeSpan.FromSeconds(4.2 * multiplier));
        _lyricSweepStoryboard.Children[1].Duration = new Duration(TimeSpan.FromSeconds(2.4 * multiplier));
        _backdropStoryboard.Begin();
        _overlayStoryboard.Begin();
        _lyricSweepStoryboard.Begin();
    }

    private void StartMotion()
    {
        if (_motionStarted) return;
        _motionStarted = true;
        _waveStoryboard.Begin();
        _backdropStoryboard.Begin();
        _overlayStoryboard.Begin();
        _lyricSweepStoryboard.Begin();
        UpdateMotionSpeed();
    }

    private void StopMotion()
    {
        if (!_motionStarted) return;
        _motionStarted = false;
        _waveStoryboard.Stop();
        _backdropStoryboard.Stop();
        _overlayStoryboard.Stop();
        _lyricSweepStoryboard.Stop();
    }
}
