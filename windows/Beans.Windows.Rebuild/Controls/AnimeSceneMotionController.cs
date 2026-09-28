using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Beans.Windows.Rebuild.Controls;

// All scenery shares ONE fill transform. HUD ornaments share the existing HUD's
// design coordinates. No render-loop timers and no ordinary-player visualizer.
internal sealed class AnimeSceneMotionController : IDisposable
{
#if DEBUG
    internal static int ActiveInstances { get; private set; }
#endif
    private readonly Grid _host;
    private readonly Canvas _hud;
    private readonly FrameworkElement _queue;
    private readonly Button _play;
    private readonly Compositor _compositor;
    private readonly Canvas _scene = new() { Width = 2048, Height = 1152, IsHitTestVisible = false };
    private readonly Canvas _motion = new() { Width = 2048, Height = 1152, IsHitTestVisible = false };
    private readonly Canvas _particles = new() { Width = 1660, Height = 948, IsHitTestVisible = false };
    private readonly List<AnimationController> _loops = [];
    // Composition keeps references to keyframes/easings while committing on its
    // render thread. Do not Close() these shared objects immediately after Start.
    private readonly List<CompositionObject> _animationResources = [];
    private readonly List<(Visual Visual, string Property)> _animated = [];
    private readonly List<UIElement> _decorations = [];
    private readonly List<(Image Image, RoutedEventHandler Opened, ExceptionRoutedEventHandler Failed)> _images = [];
    private readonly List<(Button Button, PointerEventHandler Enter, PointerEventHandler Exit, PointerEventHandler Down, PointerEventHandler Up)> _buttons = [];
    private readonly AnimeMotionPolicy _policy = new();
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly AnimeSceneDefinition _definition;
    private readonly Ellipse _playHalo;
    private readonly Canvas _sparkles = new() { Width = 46, Height = 42, IsHitTestVisible = false, Opacity = 0 };
    private readonly Grid _sheen = new() { IsHitTestVisible = false };
    private int _opened;
    private int _settleRevision;
    private bool _building = true, _failed, _disposed, _playing, _visible = true, _reduced;
    private bool _ready;
    public AnimeSceneKind Scene => _definition.Kind;
    public bool Ready => _ready;
    internal AnimeMotionState MotionState => _policy.State;
    public event Action<bool>? ReadinessChanged;

    public AnimeSceneMotionController(Grid host, Canvas hud, FrameworkElement queue, Button play, Button favorite,
        IEnumerable<Button> buttons, AnimeSceneDefinition definition)
    {
        (_host, _hud, _queue, _play, _definition) = (host, hud, queue, play, definition);
#if DEBUG
        ActiveInstances++;
#endif
        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        host.IsHitTestVisible = false;
        host.Opacity = 0;
        host.Visibility = Visibility.Visible;
        host.SizeChanged += HostSizeChanged;
        var fill = new Viewbox { Stretch = Stretch.UniformToFill, Child = _scene, IsHitTestVisible = false };
        host.Children.Add(fill);
        AddImage(_scene, "background.png");
        _scene.Children.Add(_motion);
        foreach (var (name, seconds) in new[] { ("hair", 4.9), ("cloth", 5.8), ("leaves", 4.2) })
        {
            var minus = AddImage(_motion, name + "-minus.png");
            var plate = AddImage(_motion, name + "-plus.png");
            ConfigurePoseMotion(name, minus, plate, seconds);
        }
        var water = AddImage(_motion, "water.png");
        var waterShift = Scene == AnimeSceneKind.Summer ? 8f : 5f;
        Loop(water, "Opacity", Scene == AnimeSceneKind.Summer ? 4.9 : 6.2,
            (0, .14f), (.32f, .88f), (.68f, .42f), (1, .14f));
        Loop(water, "Translation.X", Scene == AnimeSceneKind.Summer ? 9.5 : 12.5,
            (0, -waterShift), (.5f, waterShift), (1, -waterShift));
        Loop(water, "Translation.Y", 7.8, (0, -1.5f), (.5f, 2.5f), (1, -1.5f));
        var cloud = AddImage(_motion, "cloud.png");
        var cloudShift = Scene == AnimeSceneKind.Summer ? 15f : 11f;
        Loop(cloud, "Translation.X", Scene == AnimeSceneKind.Summer ? 39 : 48,
            (0, -cloudShift), (.5f, cloudShift), (1, -cloudShift));
        Loop(cloud, "Translation.Y", 31, (0, 1.5f), (.5f, -4), (1, 1.5f));
        Loop(cloud, "Opacity", 18, (0, .72f), (.5f, 1), (1, .72f));
        // Tiny whole-frame drift gives the independently moving plates depth,
        // while staying well below a face-distorting camera move.
        Loop(_scene, "Translation.X", 46, (0, -3), (.5f, 3), (1, -3));
        Loop(_scene, "Translation.Y", 37, (0, -1.5f), (.5f, 1.5f), (1, -1.5f));
        AddSceneOrnament();
        AddParticlesAndVisitor();
        AddDecoration(_particles);
        _playHalo = new Ellipse { Width = 76, Height = 76, Stroke = Brush(100, 250, 250, 255), StrokeThickness = 1.2,
            Fill = Brush(18, 230, 240, 255), IsHitTestVisible = false };
        AddDecoration(_playHalo);
        _play.SizeChanged += PlaySizeChanged;
        host.DispatcherQueue.TryEnqueue(() => { if (!_disposed) UpdateHaloLayout(); });
        Loop(_playHalo, "Opacity", 4, (0, .2f), (.5f, .7f), (1, .2f));
        AddStickers();
        AddQueueSheen();
        foreach (var button in buttons) WireButton(button);
        _settle.Tick += Settled;
        _building = false;
        TryReady();
    }

    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));
    private static Path Drawing(string data, string stroke = "#D9FFFFFF", string fill = "Transparent", double width = 1.2) =>
        (Path)XamlReader.Load($"<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{data}' Stroke='{stroke}' Fill='{fill}' StrokeThickness='{width}' IsHitTestVisible='False'/>");

    private Image AddImage(Canvas canvas, string filename)
    {
        var image = new Image { Width = 2048, Height = 1152, Stretch = Stretch.Fill, IsHitTestVisible = false };
        RoutedEventHandler opened = (_, _) => { _opened++; TryReady(); };
        ExceptionRoutedEventHandler failed = (_, _) => Fail();
        image.ImageOpened += opened; image.ImageFailed += failed;
        _images.Add((image, opened, failed));
        canvas.Children.Add(image);
        image.Source = new BitmapImage(new Uri($"ms-appx:///{_definition.Folder}/{filename}"));
        return image;
    }
    private void TryReady()
    {
        if (_building || _failed || _disposed || _ready || _opened != _images.Count) return;
        _ready = true;
        _host.Opacity = 1;
        ReadinessChanged?.Invoke(true);
        Update(_playing, _visible, _reduced);
    }

    private void ConfigurePoseMotion(string name, Image minus, Image plus, double seconds)
    {
        var summer = Scene == AnimeSceneKind.Summer;
        var center = name switch
        {
            "hair" => summer ? new Vector3(430, 510, 0) : new Vector3(205, 560, 0),
            "cloth" => summer ? new Vector3(500, 920, 0) : new Vector3(430, 1035, 0),
            _ => summer ? new Vector3(177, 90, 0) : new Vector3(295, 86, 0),
        };
        ElementCompositionPreview.GetElementVisual(minus).CenterPoint = center;
        ElementCompositionPreview.GetElementVisual(plus).CenterPoint = center;

        var peak = name == "leaves" ? 1f : .96f;
        var floor = name == "leaves" ? .04f : .12f;
        Loop(plus, "Opacity", seconds,
            (0, .42f), (.18f, peak), (.43f, .56f), (.68f, floor), (.86f, .24f), (1, .42f));

        var shift = name switch { "hair" => summer ? 4.5f : 3.4f, "cloth" => summer ? 3.3f : 2.6f, _ => summer ? 7f : 4.5f };
        Loop(plus, "Translation.X", seconds * 1.08,
            (0, -shift * .35f), (.25f, shift), (.52f, shift * .1f), (.75f, -shift), (1, -shift * .35f));
        Loop(plus, "Translation.Y", seconds * 1.17,
            (0, 0), (.3f, name == "leaves" ? -2.8f : -1.2f), (.65f, name == "leaves" ? 2.2f : 1.4f), (1, 0));

        // The foliage plate gets a separate slow breath so the branch canopy is
        // visibly alive even between foreground particles.
        if (name == "leaves")
        {
            Loop(plus, "Scale.X", summer ? 7.8 : 9.2, (0, .994f), (.5f, 1.012f), (1, .994f));
            Loop(plus, "Scale.Y", summer ? 6.8 : 8.4, (0, 1.006f), (.5f, .994f), (1, 1.006f));
            Loop(minus, "Opacity", seconds * 1.06, (0, .82f), (.5f, .38f), (1, .82f));
        }
    }
    private void Fail()
    {
        if (_disposed || _failed) return;
        _failed = true; _ready = false; _host.Opacity = 0;
        ReadinessChanged?.Invoke(false);
        Update(_playing, _visible, true);
    }
    private void HostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _host.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Math.Max(0, e.NewSize.Width), Math.Max(0, e.NewSize.Height)) };
        if (!_building) UpdateHaloLayout();
    }
    private void PlaySizeChanged(object sender, SizeChangedEventArgs e) => UpdateHaloLayout();
    private void UpdateHaloLayout()
    {
        var point = _play.TransformToVisual(_hud).TransformPoint(new Point());
        Canvas.SetLeft(_playHalo, point.X + _play.ActualWidth / 2 - 38);
        Canvas.SetTop(_playHalo, point.Y + _play.ActualHeight / 2 - 38);
    }

    private void AddSceneOrnament()
    {
        var ornament = new Canvas { Width = 40, Height = 92, IsHitTestVisible = false };
        Canvas.SetLeft(ornament, 154); Canvas.SetTop(ornament, 242);
        ornament.Children.Add(Drawing("M20,0 L20,25", "#BA365576"));
        if (Scene == AnimeSceneKind.Summer)
        {
            ornament.Children.Add(Drawing("M8,47 C7,18 33,18 32,47 Q20,52 8,47 Z", "#CDFFFFFF", "#25D7F1FF"));
            ornament.Children.Add(Drawing("M20,40 L20,65 M15,65 L25,65 L24,90 L15,89 Z", "#D7FFFFFF", "#80B0DCEB"));
            ornament.Children.Add(Drawing("M13,41 Q11,31 18,28", "#E8FFFFFF"));
        }
        else
        {
            ornament.Children.Add(Drawing("M20,25 C4,25 4,48 20,48 C36,48 36,25 20,25 Z M14,48 L6,77 Q20,87 34,77 L26,48", "#D3FFF8FD", "#B8FFF7FC"));
            ornament.Children.Add(Drawing("M15,36 L16,36 M24,36 L25,36 M17,41 Q20,44 23,41 M13,50 L27,50", "#C1787F9B"));
        }
        _scene.Children.Add(ornament);
        ElementCompositionPreview.GetElementVisual(ornament).CenterPoint = new Vector3(20, 0, 0);
        var sway = Scene == AnimeSceneKind.Summer ? 5f : 2.5f;
        Loop(ornament, "RotationAngleInDegrees", 5.4, (0, -sway), (.5f, sway), (1, -sway));
    }

    private void AddParticlesAndVisitor()
    {
        if (Scene == AnimeSceneKind.Spring) AddFallingPetals(); else AddSummerBreeze();
        AddDistantVisitor();
    }

    private void AddFallingPetals()
    {
        var random = new Random(143);
        for (var i = 0; i < _definition.ParticleCount; i++)
        {
            var depth = i % _definition.ParticleDepthLayers;
            var near = depth == 0;
            var mid = depth is 1 or 3;
            var petal = Drawing((i % 3) switch
                {
                    0 => "M0,5 Q2,-2 7,1 L8,3 L10,1 Q15,9 4,17 Q-2,12 0,5 Z",
                    1 => "M1,3 Q7,-3 10,2 L9,4 L12,4 Q13,13 2,16 Q-2,10 1,3 Z",
                    _ => "M1,7 Q3,-1 9,1 Q15,4 9,11 Q5,17 1,13 Q-1,10 1,7 Z",
                },
                near ? "#C6B95791" : mid ? "#9BD487AC" : "#70E0A7C4",
                near ? "#F2F3AFCF" : mid ? "#DDEFC4DA" : "#B8FFE4F0", near ? .8 : .55);
            petal.Width = near ? 24 + random.Next(10) : mid ? 14 + random.Next(9) : 8 + random.Next(7);
            petal.Height = petal.Width * 1.25; petal.Stretch = Stretch.Fill;
            // Narrow lanes beside the controls can fall from the very top. The
            // central lanes fade in below the controls, never across text/face.
            var edgeLane = i % 5 == 0;
            var x = edgeLane ? 1170 + random.Next(68) : 570 + random.Next(540);
            Canvas.SetLeft(petal, x); Canvas.SetTop(petal, -72);
            _particles.Children.Add(petal);
            ElementCompositionPreview.GetElementVisual(petal).CenterPoint = new Vector3((float)petal.Width / 2, (float)petal.Height / 2, 0);
            var seconds = near ? 9 + random.Next(5) : mid ? 14 + random.Next(7) : 21 + random.Next(8);
            var direction = random.Next(2) == 0 ? -1f : 1f;
            var drift = near ? 82 + random.Next(58) : mid ? 48 + random.Next(42) : 24 + random.Next(32);
            var spin = direction * (near ? 430 + random.Next(260) : mid ? 300 + random.Next(220) : 190 + random.Next(160));
            var loopStart = _loops.Count;
            Loop(petal, "Translation.Y", seconds, (0, 0), (.24f, 245), (.52f, 565), (.78f, 850), (1, 1085));
            Loop(petal, "Translation.X", seconds, (0, 0), (.18f, direction * drift * .62f), (.4f, -direction * drift * .36f),
                (.62f, direction * drift), (.82f, direction * drift * .12f), (1, -direction * drift * .55f));
            Loop(petal, "RotationAngleInDegrees", seconds, (0, random.Next(-80, 45)), (.32f, spin * .34f), (.68f, spin * .72f), (1, spin));
            Loop(petal, "Scale.X", seconds, (0, .86f), (.18f, .18f), (.38f, 1.06f), (.61f, .24f), (.82f, .96f), (1, .42f));
            Loop(petal, "Scale.Y", seconds, (0, .94f), (.42f, 1.08f), (.76f, .88f), (1, 1.02f));
            var opacity = near ? .96f : mid ? .78f : .55f;
            Loop(petal, "Opacity", seconds, (0, 0), (edgeLane ? .05f : .25f, 0), (edgeLane ? .09f : .29f, opacity),
                (.86f, opacity), (.97f, .28f), (1, 0));
            PhaseLoops(loopStart, (float)random.NextDouble());
        }
    }

    private void AddSummerBreeze()
    {
        var random = new Random(74);
        // Short translucent strokes travel with the hair direction. They never
        // span the entire screen or cross the lyric/queue area.
        for (var i = 0; i < _definition.WindStreakCount; i++)
        {
            var near = i % 3 == 0;
            var wind = Drawing(i % 2 == 0
                ? "M0,20 C65,-8 124,42 218,9 M48,29 Q120,47 174,24 M104,2 Q145,-8 196,5"
                : "M0,13 C52,-7 118,29 202,7 M70,22 Q132,35 188,16 M24,2 Q66,-7 108,5",
                "#86FFFFFF", "Transparent", near ? 2.25 : 1.35);
            wind.Stroke = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), GradientStops =
                { new GradientStop { Color = Colors.Transparent, Offset = 0 }, new GradientStop { Color = Color.FromArgb(near ? (byte)225 : (byte)165, 188, 238, 255), Offset = .45 }, new GradientStop { Color = Colors.Transparent, Offset = 1 } } };
            Canvas.SetLeft(wind, 690 + i % 3 * 58); Canvas.SetTop(wind, 292 + i * 61);
            _particles.Children.Add(wind);
            var first = _loops.Count;
            var period = 4.4 + i * .48;
            Loop(wind, "Translation.X", period, (0, 285), (.48f, 40), (1, -285));
            Loop(wind, "Translation.Y", period, (0, 15), (.35f, -8), (.72f, 5), (1, -3));
            Loop(wind, "Opacity", period, (0, 0), (.12f, near ? .86f : .58f), (.68f, near ? .72f : .46f), (.94f, 0), (1, 0));
            Loop(wind, "Scale.X", period, (0, .72f), (.3f, 1.12f), (.76f, .92f), (1, .68f));
            PhaseLoops(first, i * .103f);
        }
        for (var i = 0; i < _definition.ParticleCount; i++)
        {
            // HUD coordinates explicitly avoid girl, controls, both fixed copy areas,
            // lyrics and queue. Particles live in the central scenic corridor.
            var depth = i % _definition.ParticleDepthLayers;
            var near = depth == 0;
            var x = 710 + random.Next(370); var y = 330 + random.Next(435);
            FrameworkElement particle = i % 3 == 0
                ? Drawing("M0,7 Q6,-3 17,1 Q15,12 0,7 Z M1,7 L15,2",
                    near ? "#D06B925F" : "#9A7FA477", near ? "#D8CBE89F" : "#A9B8D691", near ? .9 : .65)
                : new Ellipse { Width = near ? 5 + random.Next(4) : 2 + random.Next(4), Height = near ? 5 + random.Next(4) : 2 + random.Next(4), Fill = Brush(near ? (byte)225 : (byte)170, 255, 244, 204) };
            if (particle is Path leaf)
            {
                leaf.Width = near ? 21 + random.Next(8) : 13 + random.Next(7);
                leaf.Height = leaf.Width * .62;
                leaf.Stretch = Stretch.Fill;
            }
            Canvas.SetLeft(particle, x); Canvas.SetTop(particle, y);
            _particles.Children.Add(particle);
            var seconds = near ? 5.8 + random.NextDouble() * 3.2 : 8 + random.Next(7);
            var direction = random.Next(2) == 0 ? -1f : 1f;
            var first = _loops.Count;
            Loop(particle, "Translation.X", seconds, (0, 205), (.48f, -20), (1, -285));
            Loop(particle, "Translation.Y", seconds, (0, 8), (.22f, -24), (.48f, 17), (.72f, -14), (1, 6));
            Loop(particle, "Opacity", seconds, (0, 0), (.09f, near ? .94f : .72f), (.78f, near ? .82f : .6f), (1, 0));
            Loop(particle, "RotationAngleInDegrees", seconds, (0, -25 * direction), (.5f, 110 * direction), (1, 255 * direction));
            Loop(particle, "Scale.X", seconds, (0, .65f), (.3f, 1.08f), (.62f, .42f), (1, .9f));
            PhaseLoops(first, (float)random.NextDouble());
        }
    }
    private void PhaseLoops(int first, float progress)
    { for (var i = first; i < _loops.Count; i++) _loops[i].Progress = progress; }

    private void AddDistantVisitor()
    {
        var visitor = Scene == AnimeSceneKind.Summer
            ? Drawing("M0,8 L28,0 L17,19 L12,12 Z M12,12 L28,0 M12,12 L12,18 L16,15", "#CF6F8DA4", "#D9FFFFFF", .7)
            : Drawing("M0,9 Q8,0 14,8 Q20,0 28,5 M14,8 L16,11", "#B3446683", "Transparent", 1.3);
        Canvas.SetLeft(visitor, 706); Canvas.SetTop(visitor, 378);
        _particles.Children.Add(visitor);
        var period = Scene == AnimeSceneKind.Summer ? 57 : 63;
        Loop(visitor, "Translation.X", period, (0, 0), (.80f, 0), (1, 400));
        Loop(visitor, "Translation.Y", period, (0, 0), (.8f, 0), (.9f, -17), (1, -6));
        Loop(visitor, "Opacity", period, (0, 0), (.8f, 0), (.83f, .8f), (.96f, .8f), (1, 0));
    }

    private void AddStickers()
    {
        var color = Scene == AnimeSceneKind.Spring ? "#DF9C719E" : "#CF426A83";
        var cat = Drawing("M3,27 L4,7 L12,13 Q18,10 23,13 L31,6 L32,27 Q18,35 3,27 Z M10,21 L12,21 M23,21 L25,21 M16,25 Q18,28 20,25 M0,23 L8,24 M28,24 L36,22 M2,27 L9,26", color);
        Canvas.SetLeft(cat, 1280); Canvas.SetTop(cat, 511); AddDecoration(cat);
        var star = Drawing("M8,0 L10,6 L16,8 L10,10 L8,16 L6,10 L0,8 L6,6 Z M26,11 L27,15 L31,16 L27,17 L26,21 L25,17 L21,16 L25,15 Z", "#DBFFFFFF");
        Canvas.SetLeft(star, 1112); Canvas.SetTop(star, 225); AddDecoration(star);
        _sparkles.Children.Add(Drawing("M8,1 L10,7 L16,9 L10,11 L8,17 L6,11 L0,9 L6,7 Z M33,19 L35,26 L42,28 L35,30 L33,37 L31,30 L24,28 L31,26 Z", color));
        Canvas.SetLeft(_sparkles, 1075); Canvas.SetTop(_sparkles, 190); AddDecoration(_sparkles);
    }
    private void AddDecoration(UIElement element) { _hud.Children.Add(element); _decorations.Add(element); }
    private void AddQueueSheen()
    {
        if (_queue is not Grid grid) return;
        var streak = new Border { Width = 54, HorizontalAlignment = HorizontalAlignment.Left,
            Background = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
                GradientStops = { new GradientStop { Color = Colors.Transparent, Offset = 0 }, new GradientStop { Color = Color.FromArgb(22, 255, 255, 255), Offset = .5 }, new GradientStop { Color = Colors.Transparent, Offset = 1 } } } };
        _sheen.Children.Add(streak);
        grid.Children.Add(_sheen);
        grid.SizeChanged += QueueSizeChanged;
        Loop(streak, "Translation.X", 16, (0, -65), (.72f, -65), (.98f, 320), (1, 320));
        Loop(streak, "Opacity", 16, (0, 0), (.72f, 0), (.76f, 1), (.95f, 1), (1, 0));
    }
    private void QueueSizeChanged(object sender, SizeChangedEventArgs e) => _sheen.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
    private void WireButton(Button button)
    {
        PointerEventHandler enter = (_, _) => ButtonScale(button, 1.035f);
        PointerEventHandler exit = (_, _) => ButtonScale(button, 1);
        PointerEventHandler down = (_, _) => ButtonScale(button, .965f);
        PointerEventHandler up = (_, _) => ButtonScale(button, 1);
        button.PointerEntered += enter; button.PointerExited += exit; button.PointerPressed += down; button.PointerReleased += up; button.PointerCanceled += up; button.PointerCaptureLost += up;
        _buttons.Add((button, enter, exit, down, up));
    }
    private void ButtonScale(Button button, float scale)
    {
        if (_disposed || _reduced || !_visible) return;
        var v = ElementCompositionPreview.GetElementVisual(button);
        v.CenterPoint = new Vector3((float)button.ActualWidth / 2, (float)button.ActualHeight / 2, 0);
        var animation = _compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1, new Vector3(scale, scale, 1)); animation.Duration = TimeSpan.FromMilliseconds(160);
        v.StartAnimation("Scale", animation);
    }
    public void FavoriteSparkle()
    {
        if (_disposed || _reduced || !_visible) return;
        var v = ElementCompositionPreview.GetElementVisual(_sparkles);
        var a = _compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, 0); a.InsertKeyFrame(.2f, 1); a.InsertKeyFrame(1, 0); a.Duration = TimeSpan.FromMilliseconds(650);
        v.StartAnimation("Opacity", a);
    }

    private void Loop(UIElement element, string property, double seconds, params (float Time, float Value)[] frames)
    {
        if (property.StartsWith("Translation.", StringComparison.Ordinal)) ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        CompositionEasingFunction easing = seconds < 12
            ? _compositor.CreateCubicBezierEasingFunction(new Vector2(.4f, 0), new Vector2(.6f, 1))
            : _compositor.CreateLinearEasingFunction();
        foreach (var (time, value) in frames) animation.InsertKeyFrame(time, value, easing);
        animation.Duration = TimeSpan.FromSeconds(seconds); animation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation(property, animation);
        _animationResources.Add(animation); _animationResources.Add(easing);
        _animated.Add((visual, property));
        if (visual.TryGetAnimationController(property) is { } controller) { controller.Pause(); _loops.Add(controller); }
    }
    private void Fade(UIElement element, float opacity, int milliseconds)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, opacity); animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        visual.StartAnimation("Opacity", animation);
    }
    public void Update(bool playing, bool visible, bool reduced)
    {
        if (_disposed) return;
        (_playing, _visible, _reduced) = (playing, visible, reduced);
        var before = _policy.State;
        var revision = _policy.Update(playing, visible, reduced, _ready && !_failed);
        if (before == _policy.State && _policy.State != AnimeMotionState.Static) return;
        _settle.Stop();
        switch (_policy.State)
        {
            case AnimeMotionState.Playing:
                foreach (var loop in _loops) loop.Resume();
                Fade(_motion, 1, 600); Fade(_particles, 1, 600); Fade(_sheen, 1, 600);
                _playHalo.Visibility = Visibility.Visible;
                break;
            case AnimeMotionState.Settling:
                Fade(_motion, 0, 600); Fade(_particles, 0, 600); Fade(_sheen, 0, 600);
                _playHalo.Visibility = Visibility.Collapsed;
                _settleRevision = revision; _settle.Start();
                break;
            default:
                foreach (var loop in _loops) loop.Pause();
                ResetOpacity(_motion, 0); ResetOpacity(_particles, 0); ResetOpacity(_sheen, 0);
                _playHalo.Visibility = Visibility.Collapsed;
                ResetOpacity(_sparkles, 0);
                foreach (var b in _buttons) { var v = ElementCompositionPreview.GetElementVisual(b.Button); v.StopAnimation("Scale"); v.Scale = Vector3.One; }
                break;
        }
    }
    private static void ResetOpacity(UIElement element, float value)
    { var v = ElementCompositionPreview.GetElementVisual(element); v.StopAnimation("Opacity"); v.Opacity = value; }
    private void Settled(object? sender, object e)
    {
        _settle.Stop();
        if (_policy.CompleteSettling(_settleRevision)) foreach (var loop in _loops) loop.Pause();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _policy.Dispose();
#if DEBUG
        ActiveInstances--;
#endif
        _settle.Stop(); _settle.Tick -= Settled;
        _host.SizeChanged -= HostSizeChanged; _queue.SizeChanged -= QueueSizeChanged;
        _play.SizeChanged -= PlaySizeChanged;
        foreach (var (visual, property) in _animated) visual.StopAnimation(property);
        _loops.Clear();
        _animationResources.Clear();
        foreach (var (img, opened, failed) in _images) { img.ImageOpened -= opened; img.ImageFailed -= failed; img.Source = null; }
        foreach (var b in _buttons)
        {
            b.Button.PointerEntered -= b.Enter; b.Button.PointerExited -= b.Exit; b.Button.PointerPressed -= b.Down;
            b.Button.PointerReleased -= b.Up; b.Button.PointerCanceled -= b.Up; b.Button.PointerCaptureLost -= b.Up;
            ElementCompositionPreview.GetElementVisual(b.Button).StopAnimation("Scale");
        }
        foreach (var element in _decorations) _hud.Children.Remove(element);
        if (_queue is Grid grid) grid.Children.Remove(_sheen);
        _host.Children.Clear(); _host.Visibility = Visibility.Collapsed;
        ReadinessChanged = null;
    }
}
