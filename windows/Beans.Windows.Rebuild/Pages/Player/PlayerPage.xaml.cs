using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Beans.Windows.Rebuild.Services.Platforms;
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
using Windows.System;

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
    public double Scale { get; private set; } = 0.88;
    public FontWeight Weight { get => _weight; private set { _weight = value; OnChanged(nameof(Weight)); } }
    public double SizeScale { get; set; } = 1;
    public bool AnimeMode { get; set; }
    private bool _reduceMotion;
    private bool _motionEnabled = true;
    public bool ReduceMotion { get => _reduceMotion; set { if (_reduceMotion == value) return; _reduceMotion = value; OnChanged(nameof(ReduceMotion)); } }
    public bool MotionEnabled { get => _motionEnabled; set { if (_motionEnabled == value) return; _motionEnabled = value; OnChanged(nameof(MotionEnabled)); } }
    public double SweepSeconds { get; set; } = 3;
    public void ApplyStyle(PlayerVisualStyleOption style, int distance)
    {
        var active = distance == 0;
        IsActive = active;
        FontSize = (AnimeMode ? 20 : (active ? 40 : distance == 1 ? 23 : 19)) * SizeScale;
        TranslationFontSize = Math.Max(10, FontSize * 0.46);
        Opacity = AnimeMode ? (active ? 1 : distance <= 2 ? .96 : .9) : active ? 1 : distance == 1 ? 0.78 : Math.Max(0.25, 0.62 - (Math.Min(distance, 4) * 0.08));
        TranslationOpacity = active ? 0.7 : Math.Min(0.52, Opacity * 0.7);
        Scale = AnimeMode ? 1 : active ? 1 : distance == 1 ? 0.95 : 0.88;
        OnChanged(nameof(Scale));
        Weight = AnimeMode ? FontWeights.Normal : active ? FontWeights.SemiBold : FontWeights.Normal;
        Foreground = new SolidColorBrush(active ? style.Accent : style.Text);
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
    private bool _showTranslation = true;
    private bool _reduceMotion;
    private readonly bool _animeMode;
    private PlayerVisualStyleOption _selectedStyle;
    private bool _motionStarted;
    private bool _isFullscreen;
    private readonly Beans.Windows.Rebuild.Controls.AmbientBackgroundMotion? _ambientMotion;
    private readonly Storyboard _waveStoryboard = new();
    private readonly Storyboard _backdropStoryboard = new();
    private readonly Storyboard _overlayStoryboard = new();
    private readonly Storyboard _lyricSweepStoryboard = new();

    public IPlaybackService Player => _player;
    public event EventHandler<bool>? FullscreenRequested;
    public ObservableCollection<PlayerLyricRow> Lines { get; } = [];
    public ObservableCollection<AnimeQueueEntry> AnimeQueue { get; } = [];
    public IReadOnlyList<PlayerVisualStyleOption> VisualStyles { get; } =
    [
        new("aurora", "极光青", Color.FromArgb(255, 5, 37, 48), Color.FromArgb(255, 70, 245, 221), Colors.White, Color.FromArgb(255, 180, 220, 220), 205, "aurora.png", "aurora-glow.png"),
        new("ocean", "深海蓝", Color.FromArgb(255, 2, 24, 48), Color.FromArgb(255, 79, 215, 255), Colors.White, Color.FromArgb(255, 171, 205, 225), 205, "ocean.png", "ocean-glow.png"),
        new("mist", "浅色雾光", Color.FromArgb(255, 225, 242, 241), Color.FromArgb(255, 12, 160, 145), Color.FromArgb(255, 17, 57, 58), Color.FromArgb(255, 86, 126, 126), 210, "mist.png", "mist-glow.png"),
        new("film", "暖色胶片", Color.FromArgb(255, 51, 34, 27), Color.FromArgb(255, 255, 184, 81), Colors.White, Color.FromArgb(255, 222, 192, 160), 210, "film.png", "film-glow.png"),
        new("anime-idol", "Anime 青春之空", Color.FromArgb(255, 248, 244, 255), Color.FromArgb(255, 126, 93, 232), Colors.White, Color.FromArgb(255, 235, 232, 245), 0, "anime-player-youth-v2.png", string.Empty),
        new("stage", "旧式动漫舞台", Color.FromArgb(255, 31, 20, 66), Color.FromArgb(255, 255, 113, 222), Colors.White, Color.FromArgb(255, 216, 190, 238), 205, "stage.png", "stage-glow.png"),
        new("live", "现场蓝光", Color.FromArgb(255, 5, 37, 48), Color.FromArgb(255, 108, 241, 255), Colors.White, Color.FromArgb(255, 190, 226, 235), 150, "live.png", "live-glow.png"),
        new("lyrics-hero", "歌词现场", Color.FromArgb(255, 4, 26, 38), Color.FromArgb(255, 110, 242, 218), Colors.White, Color.FromArgb(255, 180, 222, 222), 118, "lyrics-hero-v2.png", "live-glow.png"),
        new("minimal", "极简黑", Color.FromArgb(255, 16, 18, 20), Color.FromArgb(255, 255, 255, 255), Colors.White, Color.FromArgb(255, 180, 184, 190), 220, "ocean.png", "ocean-glow.png")
    ];

    public PlayerPage() : this(App.Services.GetRequiredService<IPlaybackService>(), App.Services.GetRequiredService<INavigationService>(), App.Services.GetRequiredService<ILyricsService>()) { }

    public PlayerPage(IPlaybackService player, INavigationService navigation, ILyricsService lyrics, IPlatformPreferenceStore? preferences = null, bool animeMode = false, Services.Anime.AnimeSession? animeSession = null)
    {
        _player = player;
        _navigation = navigation;
        _lyrics = lyrics;
        _animeMode = animeMode;
        // The live-blue composition is the default: it keeps the singer on the
        // left, leaves a quiet lyric field on the right, and makes the ambient
        // visualizer read as part of the stage rather than a floating overlay.
        _selectedStyle = VisualStyles.First(style => style.Key == (animeMode ? "anime-idol" : "live"));
        InitializeComponent();
        if (!animeMode) _ambientMotion = new(BackgroundParticles, BackdropScale, BackdropPan, BackdropOverlayImage, AudioVisualizerCanvas, AmbientRingsCanvas, PersonWindCanvas, PersonMotionImage, PersonMotionTransform);
        UpdateFullscreenButton();
        StyleSelector.SelectedItem = _selectedStyle;
        if (preferences is not null)
        {
            if (double.TryParse(preferences.GetString("settings.lyric-scale"), NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && double.IsFinite(scale))
                LyricSizeSlider.Value = Math.Clamp(scale, .8, 1.4);
            _showTranslation = preferences.GetString("settings.lyric-translation") != "false";
            _reduceMotion = preferences.GetString("settings.reduce-motion") == "true";
        }
        InitializeAnimeSceneSettings(preferences, animeSession);
        ProgressSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(ProgressSlider_PointerPressed), true);
        ProgressSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(ProgressSlider_PointerReleased), true);
        AnimeProgressSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(AnimeProgressSlider_PointerPressed), true);
        AnimeProgressSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(AnimeProgressSlider_PointerReleased), true);
        _player.PropertyChanged += Player_PropertyChanged;
        Loaded += PlayerPage_Loaded;
        Unloaded += PlayerPage_Unloaded;
        ApplyStyle(_selectedStyle);
        AddWaveAnimation(WaveOneTransform, -180, 180, 12);
        AddWaveAnimation(WaveTwoTransform, 160, -160, 9);
        AddLyricSweepAnimation();
    }

    private async void PlayerPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_animeMode)
        {
            _player.Queue.CollectionChanged += AnimeQueue_Changed;
            RefreshAnimeQueue();
            LoadAnimeScene();
        }
        StartMotion();
        RootGrid.Focus(FocusState.Programmatic);
        await LoadLyricsAsync();
    }

    private void PlayerPage_Unloaded(object sender, RoutedEventArgs e)
    {
        CancelLyrics();
        _player.PropertyChanged -= Player_PropertyChanged;
        _player.Queue.CollectionChanged -= AnimeQueue_Changed;
        StopMotion();
        DisposeAnimeScene();
        if (_animeMode) Bindings.StopTracking();
    }
    public void SetFullscreenState(bool fullscreen)
    {
        _isFullscreen = fullscreen;
        UpdateFullscreenButton();
    }
    private void Back_Click(object sender, RoutedEventArgs e) => _navigation.Navigate(_animeMode ? "anime" : "home");
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => FullscreenRequested?.Invoke(this, !_isFullscreen);
    private void Queue_Click(object sender, RoutedEventArgs e) => _navigation.Navigate("queue");
    private void Lyrics_Click(object sender, RoutedEventArgs e) => _navigation.Navigate("lyrics");
    private void PlayPause_Click(object sender, RoutedEventArgs e) => _player.TogglePlayPause();
    private void Previous_Click(object sender, RoutedEventArgs e) => _player.Previous();
    private void Next_Click(object sender, RoutedEventArgs e) => _player.Next();
    private void Shuffle_Click(object sender, RoutedEventArgs e) => _player.ToggleShuffle();
    private void Repeat_Click(object sender, RoutedEventArgs e) => _player.CycleRepeatMode();
    private void Favorite_Click(object sender, RoutedEventArgs e) => _player.ToggleFavorite();
    private void UpdateFullscreenButton()
    {
        if (FullscreenButton is null) return;
        var glyph = _isFullscreen ? "\uE73F" : "\uE740";
        var label = _isFullscreen ? "退出全屏" : "全屏";
        FullscreenGlyph.Glyph = glyph;
        FullscreenLabel.Text = label;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(FullscreenButton, _isFullscreen ? "退出全屏" : "进入全屏");
        if (AnimeFullscreenButton is not null)
        {
            AnimeFullscreenGlyph.Glyph = glyph;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AnimeFullscreenButton, _isFullscreen ? "退出全屏" : "进入全屏");
            ToolTipService.SetToolTip(AnimeFullscreenButton, label);
        }
    }
    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space || e.Handled || e.OriginalSource is ButtonBase or TextBox or ComboBox or Slider) return;
        _player.TogglePlayPause();
        e.Handled = true;
    }
    private void Lyric_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PlayerLyricRow row) return;
        _player.Seek(row.Timestamp + (_document?.Offset ?? TimeSpan.Zero));
        RootGrid.Focus(FocusState.Programmatic);
    }
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
    private void AnimeProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e) { _isSeeking = true; _player.BeginSeek(); }
    private void AnimeProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e) { if (!_isSeeking) return; _isSeeking = false; _player.EndSeek(TimeSpan.FromSeconds(AnimeProgressSlider.Value)); }
    private void AnimeProgressSlider_KeyUp(object sender, KeyRoutedEventArgs e) => _player.Seek(TimeSpan.FromSeconds(AnimeProgressSlider.Value));
    private void StyleSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (StyleSelector.SelectedItem is PlayerVisualStyleOption style) ApplyStyle(style); }
    private void LyricSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        foreach (var line in Lines)
            line.SizeScale = e.NewValue;
        if (_document is not null) UpdateActiveLine();
    }

    private void Player_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IPlaybackService.Current)) { RefreshAnimeQueue(); _ = LoadLyricsAsync(); }
        else if (e.PropertyName == nameof(IPlaybackService.PositionSeconds)) UpdateActiveLine();
        else if (e.PropertyName == nameof(IPlaybackService.IsPlaying))
        {
            RefreshAnimeQueue();
            UpdateMotionSpeed();
        }
        else if (_animeMode && e.PropertyName == nameof(IPlaybackService.IsFavorite) && _player.IsFavorite) _animeMotion?.FavoriteSparkle();
    }

    private void AnimeQueue_Changed(object? sender, NotifyCollectionChangedEventArgs e) => RefreshAnimeQueue();

    private void RefreshAnimeQueue()
    {
        if (!_animeMode) return;
        AnimeQueue.Clear();
        var currentIndex = _player.Current is null ? 0 : _player.Queue.ToList().FindIndex(item => item.Id == _player.Current.Id);
        var start = Math.Max(0, currentIndex);
        // A four-card window of the real queue; no repeated or invented tracks to fill empty slots.
        foreach (var item in _player.Queue.Skip(start).Take(4))
            AnimeQueue.Add(new AnimeQueueEntry(item, item.Id == _player.Current?.Id, _player.IsPlaying));
        AnimeQueueEmpty.Visibility = AnimeQueue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AnimeQueueCard.Height = Math.Max(112, AnimeQueue.Count * 76 + 24);
    }

    private async void AnimeQueueItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlaybackItem item }) await _player.PlayAsync(item);
    }

    private void AnimeQueueGlow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid host || host.Opacity == 0) return;
        var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(host).Compositor;
        var glow = compositor.CreateSpriteVisual();
        glow.RelativeSizeAdjustment = System.Numerics.Vector2.One;
        var shadow = compositor.CreateDropShadow();
        shadow.Color = Color.FromArgb(255, 222, 240, 255);
        shadow.BlurRadius = 12;
        shadow.Opacity = .22f;
        glow.Shadow = shadow;
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(host, glow);
        void Release(object s, RoutedEventArgs args)
        {
            host.Unloaded -= Release;
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(host, null);
            glow.Dispose();
            shadow.Dispose();
        }
        host.Unloaded += Release;
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
            AnimeLyricList.Visibility = Visibility.Collapsed;
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
                AnimeLyricList.Visibility = Visibility.Collapsed;
                LyricSweep.Visibility = Visibility.Collapsed;
                return;
            }
            _document = document;
            for (var i = 0; i < document.Lines.Count; i++)
            {
                var line = document.Lines[i];
                var end = i + 1 < document.Lines.Count ? document.Lines[i + 1].Timestamp : TimeSpan.FromSeconds(_player.DurationSeconds);
                Lines.Add(new PlayerLyricRow(line.Timestamp, line.Text, _showTranslation ? line.Translation ?? string.Empty : string.Empty)
                {
                    SizeScale = LyricSizeSlider.Value, AnimeMode = _animeMode, ReduceMotion = _reduceMotion, MotionEnabled = !_animeMode || (_player.IsPlaying && _animeWindowVisible),
                    SweepSeconds = Math.Clamp((end - line.Timestamp).TotalSeconds, 1.2, 8)
                });
            }
            LyricStatePanel.Visibility = Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LyricList.Visibility = Lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            AnimeLyricList.Visibility = Lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            LyricSweep.Visibility = Lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            LyricSweep.Opacity = Lines.Count == 0 ? 0 : 0.2;
            UpdateActiveLine();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch
        {
            LyricStatePanel.Visibility = Visibility.Visible;
            LyricList.Visibility = Visibility.Collapsed;
            AnimeLyricList.Visibility = Visibility.Collapsed;
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
        var previousIndex = _activeIndex;
        _activeIndex = Math.Clamp(index, -1, Lines.Count - 1);
        var lyricStyle = _animeMode ? _selectedStyle with { Accent = Colors.White, Text = Colors.White } : _selectedStyle;
        for (var i = 0; i < Lines.Count; i++) Lines[i].ApplyStyle(lyricStyle, Math.Abs(i - _activeIndex));
        if (_activeIndex >= 0)
        {
            if (previousIndex != _activeIndex)
            {
                LyricList.ScrollIntoView(Lines[_activeIndex]);
                AnimeLyricList.ScrollIntoView(Lines[_activeIndex]);
                DispatcherQueue.TryEnqueue(CenterCurrentLyric);
            }
            SideCurrentLyric.Text = Lines[_activeIndex].Text;
            SideNextLyric.Text = _activeIndex + 1 < Lines.Count ? Lines[_activeIndex + 1].Text : string.Empty;
        }
        LyricProgress.Value = _player.DurationSeconds <= 0 ? 0 : Math.Clamp(_player.PositionSeconds / _player.DurationSeconds, 0, 1);
        LyricProgressText.Text = $"{_player.PositionText} / {_player.DurationText}";
    }

    private void Player_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 1200;
        SidePanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        LyricInfoColumn.Width = new GridLength(compact ? 0 : .18, GridUnitType.Star);
        SubjectColumn.Width = new GridLength(compact ? .34 : .38, GridUnitType.Star);
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void CenterCurrentLyric()
    {
        if (_animeMode)
        {
            CenterAnimeCurrentLyric();
            return;
        }
        if (_activeIndex < 0 || _activeIndex >= Lines.Count ||
            LyricList.ContainerFromIndex(_activeIndex) is not FrameworkElement row ||
            FindChild<ScrollViewer>(LyricList) is not { } scroll) return;
        var point = row.TransformToVisual(scroll).TransformPoint(new global::Windows.Foundation.Point());
        scroll.ChangeView(null, Math.Clamp(scroll.VerticalOffset + point.Y - (scroll.ViewportHeight - row.ActualHeight) / 2, 0, scroll.ScrollableHeight), null, _reduceMotion);
        if (_reduceMotion) return;
        var transition = new Storyboard();
        var fade = new DoubleAnimation { From = .55, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(320)) };
        Storyboard.SetTarget(fade, row);
        Storyboard.SetTargetProperty(fade, "Opacity");
        transition.Children.Add(fade);
        transition.Begin();
    }

    private void CenterAnimeCurrentLyric()
    {
        if (_activeIndex < 0 || _activeIndex >= Lines.Count ||
            AnimeLyricList.ContainerFromIndex(_activeIndex) is not FrameworkElement row ||
            FindChild<ScrollViewer>(AnimeLyricList) is not { } scroll) return;
        var point = row.TransformToVisual(scroll).TransformPoint(new global::Windows.Foundation.Point());
        scroll.ChangeView(null, Math.Clamp(scroll.VerticalOffset + point.Y - (scroll.ViewportHeight - row.ActualHeight) / 2, 0, scroll.ScrollableHeight), null, _reduceMotion);
    }

    private void ApplyStyle(PlayerVisualStyleOption style)
    {
        _selectedStyle = style;
        RootGrid.Background = _animeMode
            ? new LinearGradientBrush
            {
                StartPoint = new global::Windows.Foundation.Point(0, 0),
                EndPoint = new global::Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(255, 248, 244, 255), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(255, 237, 232, 255), Offset = .5 },
                    new GradientStop { Color = Color.FromArgb(255, 221, 247, 244), Offset = 1 }
                }
            }
            : new SolidColorBrush(style.Background);
        AnimeStageRoot.Visibility = Visibility.Collapsed;
        BackdropImage.Visibility = _animeMode && style.Key == "anime-idol" ? Visibility.Visible : (_animeMode ? Visibility.Collapsed : Visibility.Visible);
        BackdropImage.Opacity = 1;
        AnimeColorGrade.Visibility = _animeMode ? Visibility.Visible : Visibility.Collapsed;
        BackdropOverlayImage.Visibility = _animeMode ? Visibility.Collapsed : Visibility.Visible;
        BackdropTint.Visibility = _animeMode ? Visibility.Collapsed : Visibility.Visible;
        LocalBlurVeil.Visibility = _animeMode ? Visibility.Collapsed : Visibility.Visible;
        if (_animeMode && string.IsNullOrEmpty(style.BackgroundAsset))
        {
            PersonMotionImage.Visibility = Visibility.Collapsed;
            BackdropParticlesForAnime();
        }
        var stageMotion = style.Key is "live" or "lyrics-hero";
        if (!string.IsNullOrEmpty(style.BackgroundAsset))
            BackdropImage.Source = new BitmapImage(new Uri($"ms-appx:///Assets/Player/Backgrounds/{style.BackgroundAsset}"));
        var personAsset = style.Key switch
        {
            "live" => "live-person.png",
            "lyrics-hero" => "lyrics-hero-v2-person.png",
            _ => string.Empty
        };
        PersonMotionImage.Source = string.IsNullOrEmpty(personAsset)
            ? null
            : new BitmapImage(new Uri($"ms-appx:///Assets/Player/Overlays/{personAsset}"));
        PersonMotionImage.Visibility = !_reduceMotion && !string.IsNullOrEmpty(personAsset) ? Visibility.Visible : Visibility.Collapsed;
        PersonMotionImage.Opacity = stageMotion ? 0.28 : 0;
        if (!string.IsNullOrEmpty(style.OverlayAsset))
            BackdropOverlayImage.Source = new BitmapImage(new Uri($"ms-appx:///Assets/Player/Overlays/{style.OverlayAsset}"));
        BackdropOverlayImage.Opacity = style.Key == "lyrics-hero" ? 0.12 : 0.2;
        BackdropTint.Opacity = style.OverlayAlpha / 255d;
        LocalBlurVeil.Opacity = style.Key is "mist" or "lyrics-hero" ? 0.08 : 0.12;
        AudioVisualizerCanvas.Opacity = stageMotion ? 0.86 : 0.48;
        AmbientRingsCanvas.Opacity = stageMotion ? 0.92 : 0.5;
        PersonWindCanvas.Opacity = stageMotion ? 0.95 : 0.3;
        var accent = new SolidColorBrush(style.Accent);
        var text = new SolidColorBrush(style.Text);
        WaveOne.Background = accent;
        WaveTwo.Background = accent;
        HeaderTitle.Foreground = text;
        HeaderSource.Foreground = new SolidColorBrush(style.Muted);
        LyricProgress.Foreground = accent;
        LyricSweep.Visibility = Lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        LyricSweep.Opacity = Lines.Count == 0 ? 0 : 0.2;
        ApplyAnimeChrome();
        var lyricStyle = _animeMode ? style with { Accent = Colors.White, Text = Colors.White } : style;
        for (var i = 0; i < Lines.Count; i++) Lines[i].ApplyStyle(lyricStyle, Math.Abs(i - _activeIndex));
    }

    private void ApplyAnimeChrome()
    {
        AnimeBadge.Visibility = _animeMode ? Visibility.Visible : Visibility.Collapsed;
        if (!_animeMode) return;

        AnimeHud.Visibility = Visibility.Visible;
        BackdropParticlesForAnime();
        HeaderBar.Visibility = Visibility.Collapsed;
        StandardPlayerContent.Visibility = Visibility.Collapsed;
        ControlBar.Visibility = Visibility.Collapsed;

        HeaderBar.Background = new SolidColorBrush(Color.FromArgb(28, 7, 24, 58));
        HeaderBar.BorderBrush = new SolidColorBrush(Color.FromArgb(95, 255, 255, 255));
        HeaderTitle.Foreground = new SolidColorBrush(Colors.White);
        HeaderSource.Foreground = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255));
        BackButton.Foreground = new SolidColorBrush(Colors.White);
        StyleLabel.Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
        SizeLabel.Foreground = StyleLabel.Foreground;
        StyleSelector.Visibility = Visibility.Collapsed;
        StyleLabel.Visibility = Visibility.Collapsed;
        LyricGlassCard.Background = new SolidColorBrush(Colors.Transparent);
        LyricGlassCard.BorderBrush = new SolidColorBrush(Colors.Transparent);
        LyricGlassCard.BorderThickness = new Thickness(0);
        SubjectColumn.Width = new GridLength(.30, GridUnitType.Star);
        LyricInfoColumn.Width = new GridLength(.20, GridUnitType.Star);
        SidePanel.Background = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255));
        SidePanel.BorderBrush = new SolidColorBrush(Color.FromArgb(135, 255, 255, 255));
        ControlBar.Background = new SolidColorBrush(Color.FromArgb(35, 8, 19, 47));
        ControlBar.BorderBrush = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255));
        PlayButton.Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255));
        PlayButton.Foreground = new SolidColorBrush(Colors.White);
        PlayButton.BorderBrush = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
        PlayButton.BorderThickness = new Thickness(2);
        ArtworkBubbleButton.Width = 178;
        ArtworkBubbleButton.Height = 178;
        ArtworkBubbleButton.CornerRadius = new CornerRadius(16);
        ArtworkBubbleButton.Background = new SolidColorBrush(Color.FromArgb(42, 255, 255, 255));
        ArtworkBubbleButton.BorderBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
        ArtworkBubbleButton.BorderThickness = new Thickness(1);
        ArtworkFrame.Width = 162;
        ArtworkFrame.Height = 162;
        ArtworkFrame.CornerRadius = new CornerRadius(12);
        ArtworkFrame.Background = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
        var stickerBackground = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255));
        var stickerBorder = new SolidColorBrush(Color.FromArgb(205, 255, 255, 255));
        foreach (var button in new[] { ShuffleButton, PreviousButton, NextButton, RepeatButton, LyricsButton, QueueButton })
        {
            button.Background = stickerBackground;
            button.BorderBrush = stickerBorder;
            button.BorderThickness = new Thickness(1);
            button.CornerRadius = new CornerRadius(12);
            button.Foreground = new SolidColorBrush(Colors.White);
        }
        foreach (var text in new[] { SideCurrentLyric, SideNextLyric, LyricProgressText })
            text.Foreground = new SolidColorBrush(Color.FromArgb(255, 69, 55, 91));
        foreach (var text in new[] { SideHeader, SideCurrentLabel, SideNextLabel })
            text.Foreground = new SolidColorBrush(Color.FromArgb(255, 69, 55, 91));
        LyricProgress.Foreground = new SolidColorBrush(Colors.White);
        ProgressSlider.Foreground = new SolidColorBrush(Colors.White);
        VolumeSlider.Foreground = new SolidColorBrush(Colors.White);
    }

    private void BackdropParticlesForAnime()
    {
        BackgroundParticles.Visibility = Visibility.Collapsed;
        AmbientRingsCanvas.Visibility = Visibility.Collapsed;
        PersonWindCanvas.Visibility = Visibility.Collapsed;
        AudioVisualizerCanvas.Visibility = Visibility.Collapsed;
        WaveOne.Visibility = Visibility.Collapsed;
        WaveTwo.Visibility = Visibility.Collapsed;
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
        if (_animeMode) { UpdateAnimeMotion(); return; }
        if (!_motionStarted) return;
        _ambientMotion?.SetPlaying(_player.IsPlaying);
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
        _lyricSweepStoryboard.Begin();
    }

    private void StartMotion()
    {
        if (_animeMode) { UpdateAnimeMotion(); return; }
        if (_motionStarted || _reduceMotion) return;
        _motionStarted = true;
        if (_animeMode) return;
        _ambientMotion?.Start(_player.IsPlaying);
        _waveStoryboard.Begin();
        _lyricSweepStoryboard.Begin();
        UpdateMotionSpeed();
    }

    private void StopMotion()
    {
        if (!_motionStarted) return;
        _motionStarted = false;
        _ambientMotion?.Stop();
        _waveStoryboard.Stop();
        _backdropStoryboard.Stop();
        _overlayStoryboard.Stop();
        _lyricSweepStoryboard.Stop();
    }
}
