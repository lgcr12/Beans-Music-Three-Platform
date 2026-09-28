using Beans.Windows.Rebuild.Controls;
using Beans.Windows.Rebuild.Services.Anime;
using Beans.Windows.Rebuild.Services.Platforms;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace Beans.Windows.Rebuild.Pages.Player;

public sealed partial class PlayerPage
{
#if DEBUG
    internal AnimeSceneMotionController? ValidationMotion => _animeMotion;
    internal bool ValidationDisposed => _animeDisposed;
    internal void ValidationReduceMotion(bool value) { _reduceMotion = value; UpdateAnimeMotion(); }
#endif
    private AnimeSceneSelection? _animeSelection;
    private AnimeSceneMotionController? _animeMotion;
    private AnimeSession? _animeSession;
    private IPlatformPreferenceStore? _animePreferences;
    private UISettings? _systemUiSettings;
    private Window? _animeWindow;
    private bool _animeWindowVisible = true;
    private bool _animeDisposed;

    private void InitializeAnimeSceneSettings(IPlatformPreferenceStore? preferences, AnimeSession? session)
    {
        if (!_animeMode) return;
        _animePreferences = preferences;
        _animeSession = session;
        // Explicit local QA override; never persisted and never tied to music data.
        var preview = Environment.GetEnvironmentVariable("BEANS_ANIME_SCENE_PREVIEW");
        _animeSelection = preview is "summer" or "spring"
            ? new(() => preview == "summer" ? 0 : 1) : new();
        _systemUiSettings = new UISettings();
        _systemUiSettings.AnimationsEnabledChanged += SystemAnimationsChanged;
        if (session is not null) session.Changed += AnimeSettingsChanged;
        ReadAnimeMotionPreference();
    }
    internal void AttachAnimeWindow(Window owner)
    {
        if (!_animeMode) return;
        _animeWindow = owner;
        owner.AppWindow.Changed += AnimeWindowChanged;
        owner.VisibilityChanged += AnimeVisibilityChanged;
        ReadAnimeWindowVisibility();
    }
    private void AnimeWindowChanged(AppWindow sender, AppWindowChangedEventArgs args) => ReadAnimeWindowVisibility();
    private void AnimeVisibilityChanged(object sender, WindowVisibilityChangedEventArgs args) => ReadAnimeWindowVisibility();
    private void ReadAnimeWindowVisibility()
    {
        _animeWindowVisible = _animeWindow is null || (_animeWindow.AppWindow.IsVisible &&
            _animeWindow.AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized });
        UpdateAnimeMotion();
    }
    private void SystemAnimationsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() => { if (!_animeDisposed) { ReadAnimeMotionPreference(); UpdateAnimeMotion(); } });
    private void AnimeSettingsChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() => { if (!_animeDisposed) { ReadAnimeMotionPreference(); UpdateAnimeMotion(); } });
    private void ReadAnimeMotionPreference() => _reduceMotion = _animePreferences?.GetString("settings.reduce-motion") == "true"
        || _animeSession?.State.ReduceMotion == true || _systemUiSettings?.AnimationsEnabled == false;

    private void LoadAnimeScene()
    {
        if (_animeMotion is not null || _animeSelection is null) return;
        if (!_animeSelection.Scene.IsAvailable(path => File.Exists(Path.Combine(AppContext.BaseDirectory, path)))) return;
        _animeMotion = new(AnimeSceneHost, AnimeHudCanvas, AnimeQueueCard, AnimePlayButton, AnimeFavoriteButton,
            [AnimeBackButton, AnimePlayButton, AnimePreviousButton, AnimeNextButton, AnimeShuffleButton, AnimeFavoriteButton, AnimeQueueButton, AnimeFullscreenButton], _animeSelection.Scene);
        _animeMotion.ReadinessChanged += AnimeSceneReadinessChanged;
        UpdateAnimeMotion();
    }
    private void AnimeSceneReadinessChanged(bool ready)
    {
        BackdropImage.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        if (!ready) BackdropImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/Player/Backgrounds/anime-player-youth-v2.png"));
    }
    private void UpdateAnimeMotion()
    {
        if (!_animeMode || _animeDisposed) return;
        _animeMotion?.Update(_player.IsPlaying, _animeWindowVisible, _reduceMotion);
        foreach (var row in Lines)
        {
            row.ReduceMotion = _reduceMotion;
            row.MotionEnabled = _player.IsPlaying && _animeWindowVisible;
        }
    }
    private void DisposeAnimeScene()
    {
        if (!_animeMode || _animeDisposed) return;
        _animeDisposed = true;
        if (_animeWindow is not null)
        {
            _animeWindow.AppWindow.Changed -= AnimeWindowChanged;
            _animeWindow.VisibilityChanged -= AnimeVisibilityChanged;
        }
        if (_systemUiSettings is not null) _systemUiSettings.AnimationsEnabledChanged -= SystemAnimationsChanged;
        if (_animeSession is not null) _animeSession.Changed -= AnimeSettingsChanged;
        _animeMotion?.Dispose(); _animeMotion = null;
        foreach (var row in Lines) row.MotionEnabled = false;
    }
}
