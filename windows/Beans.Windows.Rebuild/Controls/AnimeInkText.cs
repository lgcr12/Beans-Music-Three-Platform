using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Beans.Windows.Rebuild.Controls;

// The light is masked by the actual glyphs, so no rectangular highlight crosses the scenery.
public sealed class AnimeInkText : Grid
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(AnimeInkText), new PropertyMetadata(string.Empty, Changed));
    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(AnimeInkText), new PropertyMetadata(22d, Changed));
    public static readonly DependencyProperty CharacterSpacingProperty = DependencyProperty.Register(nameof(CharacterSpacing), typeof(int), typeof(AnimeInkText), new PropertyMetadata(80, Changed));
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(AnimeInkText), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(nameof(ReduceMotion), typeof(bool), typeof(AnimeInkText), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty CenteredProperty = DependencyProperty.Register(nameof(Centered), typeof(bool), typeof(AnimeInkText), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty SweepSecondsProperty = DependencyProperty.Register(nameof(SweepSeconds), typeof(double), typeof(AnimeInkText), new PropertyMetadata(3d, Changed));
    public static readonly DependencyProperty LyricModeProperty = DependencyProperty.Register(nameof(LyricMode), typeof(bool), typeof(AnimeInkText), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty MotionEnabledProperty = DependencyProperty.Register(nameof(MotionEnabled), typeof(bool), typeof(AnimeInkText), new PropertyMetadata(true, Changed));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public int CharacterSpacing { get => (int)GetValue(CharacterSpacingProperty); set => SetValue(CharacterSpacingProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }
    public bool Centered { get => (bool)GetValue(CenteredProperty); set => SetValue(CenteredProperty, value); }
    public double SweepSeconds { get => (double)GetValue(SweepSecondsProperty); set => SetValue(SweepSecondsProperty, value); }
    public bool LyricMode { get => (bool)GetValue(LyricModeProperty); set => SetValue(LyricModeProperty, value); }
    public bool MotionEnabled { get => (bool)GetValue(MotionEnabledProperty); set => SetValue(MotionEnabledProperty, value); }

    private readonly Grid _glowHost = new() { IsHitTestVisible = false };
    private readonly Grid _glyphs = new() { IsHitTestVisible = false };
    private readonly TextBlock _ink = CreateText();
    private readonly TextBlock _light = CreateText();
    private readonly Storyboard _sweep = new();
    private SpriteVisual? _glow;
    private DropShadow? _shadow;
    private bool _wasActive;

    public AnimeInkText()
    {
        IsHitTestVisible = false;
        Children.Add(_glowHost);
        _glyphs.Children.Add(_ink);
        _glyphs.Children.Add(_light);
        Children.Add(_glyphs);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(_light, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Loaded += (_, _) => { Update(); UpdateGlow(); };
        SizeChanged += (_, _) => UpdateGlow();
        Unloaded += (_, _) => { _sweep.Stop(); ElementCompositionPreview.SetElementChildVisual(_glowHost, null); _glow?.Dispose(); _shadow?.Dispose(); _glow = null; _shadow = null; };
    }

    private static TextBlock CreateText() => new()
    {
        FontFamily = new FontFamily("ms-appx:///Assets/Fonts/KleeOne/KleeOne-Regular.ttf#Klee One"),
        FontWeight = Microsoft.UI.Text.FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Colors.White)
    };

    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs _) => ((AnimeInkText)sender).Update();

    private void Update()
    {
        if (IsLoaded) ElementCompositionPreview.SetIsTranslationEnabled(_glyphs, true);
        var becameActive = IsActive && !_wasActive;
        _wasActive = IsActive;
        foreach (var text in new[] { _ink, _light })
        {
            text.Text = Text;
            text.FontSize = FontSize;
            text.FontFamily = new FontFamily("ms-appx:///Assets/Fonts/KleeOne/KleeOne-Regular.ttf#Klee One");
            text.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            text.LineHeight = FontSize * (LyricMode ? 1.55 : 1.65);
            text.CharacterSpacing = CharacterSpacing;
            text.TextAlignment = Centered ? TextAlignment.Center : TextAlignment.Left;
        }
        _sweep.Stop();
        _sweep.Children.Clear();
        _ink.Opacity = LyricMode ? 1 : IsActive ? .9 : .86;
        _ink.Foreground = new SolidColorBrush(LyricMode
            ? IsActive ? ColorHelper.FromArgb(255, 19, 47, 79) : ColorHelper.FromArgb(255, 35, 61, 83)
            : IsActive && !ReduceMotion ? ColorHelper.FromArgb(255, 156, 216, 248) : Colors.White);
        _light.Visibility = IsActive && !ReduceMotion && MotionEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded && IsActive && !ReduceMotion && MotionEnabled)
        {
            var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
            _sweep.RepeatBehavior = RepeatBehavior.Forever;
            var offsets = new[] { -.34, -.16, 0d, .16, .34 };
            // Daylight lyrics keep a dark ink silhouette even while the blue sheen passes.
            var sheen = LyricMode ? ColorHelper.FromArgb(255, 48, 101, 145) : Colors.White;
            var shoulder = LyricMode ? ColorHelper.FromArgb(190, 38, 79, 117) : ColorHelper.FromArgb(200, 212, 247, 255);
            var colors = new[] { Colors.Transparent, shoulder, sheen, shoulder, Colors.Transparent };
            for (var i = 0; i < offsets.Length; i++)
            {
                var stop = new GradientStop { Color = colors[i], Offset = offsets[i] - .4 };
                brush.GradientStops.Add(stop);
                var animation = new DoubleAnimation { From = offsets[i] - .4, To = offsets[i] + 1.4, Duration = TimeSpan.FromSeconds(Math.Clamp(SweepSeconds, 1.8, 3.2)), EnableDependentAnimation = true };
                Storyboard.SetTarget(animation, stop);
                Storyboard.SetTargetProperty(animation, "Offset");
                _sweep.Children.Add(animation);
            }
            _light.Foreground = brush;
            _sweep.Begin();
        }
        if (IsLoaded && LyricMode && becameActive && !ReduceMotion && MotionEnabled)
        {
            // Animate the glyph container only; never introduce a lyric backdrop.
            ElementCompositionPreview.SetIsTranslationEnabled(_glyphs, true);
            var visual = ElementCompositionPreview.GetElementVisual(_glyphs);
            var rise = visual.Compositor.CreateScalarKeyFrameAnimation();
            rise.InsertKeyFrame(0, 4); rise.InsertKeyFrame(1, 0); rise.Duration = TimeSpan.FromMilliseconds(240);
            visual.StartAnimation("Translation.Y", rise);
            var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, .78f); fade.InsertKeyFrame(1, 1); fade.Duration = TimeSpan.FromMilliseconds(240);
            visual.StartAnimation("Opacity", fade);
        }
        else if (IsLoaded && (ReduceMotion || !MotionEnabled || !IsActive))
        {
            var visual = ElementCompositionPreview.GetElementVisual(_glyphs);
            visual.StopAnimation("Translation.Y"); visual.Properties.InsertVector3("Translation", System.Numerics.Vector3.Zero);
            visual.StopAnimation("Opacity"); visual.Opacity = 1;
        }
        UpdateGlow();
    }

    private void UpdateGlow()
    {
        if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) return;
        if (_glow is null)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            _shadow = compositor.CreateDropShadow();
            _shadow.Color = Microsoft.UI.ColorHelper.FromArgb(255, 24, 53, 91);
            _shadow.Mask = _ink.GetAlphaMask();
            _glow = compositor.CreateSpriteVisual();
            _glow.Shadow = _shadow;
            ElementCompositionPreview.SetElementChildVisual(_glowHost, _glow);
        }
        _glow.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
        _shadow!.Color = LyricMode ? ColorHelper.FromArgb(255, 247, 251, 255) : ColorHelper.FromArgb(255, 24, 53, 91);
        _shadow.BlurRadius = LyricMode ? 2.5f : IsActive ? 7 : 5;
        _shadow.Offset = LyricMode ? Vector3.Zero : new Vector3(0, 1, 0);
        _shadow.Opacity = LyricMode ? .85f : IsActive ? .9f : .8f;
    }
}
