using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace Beans.Windows.Rebuild.Controls;

public sealed class AnimePortalTransition : Grid
{
    private const int DurationMs = 1500;
    private const int OpeningStartMs = 330;
    private readonly Canvas _scene = new() { IsHitTestVisible = false };
    private readonly RectangleGeometry _viewport = new();
    private readonly List<Storyboard> _storyboards = [];
    private readonly List<(Image Image, List<Point> Vertices)> _reflections = [];
    private CancellationTokenSource? _cancellation;
    private WriteableBitmap? _glowTexture;
    private Visual? _pageVisual;
    private Vector3 _pageScale;
    private Vector3 _pageCenter;
    private bool _running;

    public AnimePortalTransition()
    {
        IsHitTestVisible = false;
        Canvas.SetZIndex(this, 50);
        Clip = _viewport;
        Children.Add(_scene);
        SizeChanged += (_, _) => UpdateViewport();
        Unloaded += (_, _) => _cancellation?.Cancel();
    }

    // The host reserves both players in Margin. Only content above the player is
    // captured, fractured and animated; enteringContent excludes the player too.
    public async Task TransitionAsync(bool reducedMotion, bool returning, Action switchView,
        FrameworkElement? enteringContent = null)
    {
        ArgumentNullException.ThrowIfNull(switchView);
        if (_running) return;
        _running = true;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0)
            {
                switchView();
                return;
            }
            UpdateViewport();
            Background = new SolidColorBrush(Colors.Transparent);
            IsHitTestVisible = true;
            // Capture before adding effects. This is the actual outgoing page,
            // not decorative glass shapes or an unrelated illustration.
            var snapshot = reducedMotion ? null : await CaptureAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            if (snapshot is null)
            {
                await FadeAsync(switchView, cancellation.Token);
                return;
            }

            var animation = CreateMirrorBreak(snapshot, returning);
            // Install the opaque, intact old-page tiles BEFORE changing the live
            // page. No wall-clock delay can expose the old page through a moving
            // gap, or suddenly swap the destination halfway through the reveal.
            UpdateLayout();
            switchView();
            await CaptureReflectionsAsync(enteringContent, cancellation.Token);
#if DEBUG
            if (Tools.PortalVisualValidation.Enabled && Parent is FrameworkElement captureSurface && !string.IsNullOrWhiteSpace(Tools.PortalVisualValidation.DirectoryPath))
                await Tools.PortalVisualValidation.CaptureAsync(animation, captureSurface, cancellation.Token);
#endif
            StartPageRebound(enteringContent);
            await PlayAsync(animation, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            cancellation.Cancel();
            foreach (var storyboard in _storyboards) storyboard.Stop();
            _storyboards.Clear();
            if (_pageVisual is not null)
            {
                _pageVisual.StopAnimation("Scale");
                _pageVisual.Scale = _pageScale;
                _pageVisual.CenterPoint = _pageCenter;
                _pageVisual = null;
            }
            // Releases all cropped page bitmaps, geometry and animation targets.
            _scene.Children.Clear();
            _reflections.Clear();
            Background = null;
            IsHitTestVisible = false;
            _cancellation = null;
            _running = false;
        }
    }

    private sealed record PageSnapshot(byte[] Pixels, int Width, int Height, double ScaleX, double ScaleY);

    private async Task<PageSnapshot?> CaptureAsync()
    {
        if (Parent is not FrameworkElement surface) return null;
        try
        {
            var capture = new RenderTargetBitmap();
            // Cap readback cost on high-DPI monitors; no disk I/O or persistent cache.
            var scale = Math.Min(1, 1600 / Math.Max(surface.ActualWidth, surface.ActualHeight));
            await capture.RenderAsync(surface, Math.Max(1, (int)(surface.ActualWidth * scale)),
                Math.Max(1, (int)(surface.ActualHeight * scale)));
            if (capture.PixelWidth == 0 || capture.PixelHeight == 0) return null;
            var source = (await capture.GetPixelsAsync()).ToArray();
            var sx = capture.PixelWidth / surface.ActualWidth;
            var sy = capture.PixelHeight / surface.ActualHeight;
            var origin = TransformToVisual(surface).TransformPoint(new Point());
            var left = Math.Clamp((int)Math.Round(origin.X * sx), 0, capture.PixelWidth - 1);
            var top = Math.Clamp((int)Math.Round(origin.Y * sy), 0, capture.PixelHeight - 1);
            var width = Math.Min(capture.PixelWidth - left, Math.Max(1, (int)Math.Round(ActualWidth * sx)));
            var height = Math.Min(capture.PixelHeight - top, Math.Max(1, (int)Math.Round(ActualHeight * sy)));
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
                Buffer.BlockCopy(source, ((top + y) * capture.PixelWidth + left) * 4, pixels, y * width * 4, width * 4);
            return new PageSnapshot(pixels, width, height, width / ActualWidth, height / ActualHeight);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or COMException)
        {
            // Some surfaces cannot be read back. Navigation still gets a short fade.
            return null;
        }
    }

    private async Task FadeAsync(Action switchView, CancellationToken token)
    {
        var veil = new Border { Width = ActualWidth, Height = ActualHeight, Background = Brush(255, 3, 10, 25), Opacity = 0 };
        _scene.Children.Add(veil);
        var fadeOut = new Storyboard();
        Track(fadeOut, veil, "Opacity", (0, 0), (50, 1));
        await PlayAsync(fadeOut, token);
        token.ThrowIfCancellationRequested();
        switchView();
        var fadeIn = new Storyboard();
        Track(fadeIn, veil, "Opacity", (0, 1), (50, 0));
        await PlayAsync(fadeIn, token);
    }

    private async Task CaptureReflectionsAsync(FrameworkElement? content, CancellationToken token)
    {
        if (content is null) return;
        content.UpdateLayout();
        if (content.ActualWidth <= 0 || content.ActualHeight <= 0) return;
        try
        {
            // The destination is already laid out under the intact old-page
            // tiles. Read only its content, so this never exposes a hidden frame.
            var bitmap = new RenderTargetBitmap();
            var scale = Math.Min(1, 960 / Math.Max(content.ActualWidth, content.ActualHeight));
            await bitmap.RenderAsync(content, Math.Max(1, (int)(content.ActualWidth * scale)),
                Math.Max(1, (int)(content.ActualHeight * scale)));
            token.ThrowIfCancellationRequested();
            if (bitmap.PixelWidth < 5 || bitmap.PixelHeight < 1) return;
            var environment = new PageSnapshot((await bitmap.GetPixelsAsync()).ToArray(),
                bitmap.PixelWidth, bitmap.PixelHeight, bitmap.PixelWidth / ActualWidth, bitmap.PixelHeight / ActualHeight);
            foreach (var (image, points) in _reflections)
            {
                var left = Math.Max(0, (int)Math.Floor(points.Min(p => p.X) * environment.ScaleX));
                var top = Math.Max(0, (int)Math.Floor(points.Min(p => p.Y) * environment.ScaleY));
                var right = Math.Min(environment.Width, (int)Math.Ceiling(points.Max(p => p.X) * environment.ScaleX));
                var bottom = Math.Min(environment.Height, (int)Math.Ceiling(points.Max(p => p.Y) * environment.ScaleY));
                if (right > left && bottom > top)
                    image.Source = CutMirrorTile(environment, points, left, top, right - left, bottom - top, true);
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or COMException)
        {
            // Reflection is optional; the outgoing snapshot still supplies every
            // opaque glass face if this destination has no readable XAML surface.
        }
    }

    private void UpdateViewport()
    {
        _viewport.Rect = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight));
        _scene.Width = Math.Max(0, ActualWidth);
        _scene.Height = Math.Max(0, ActualHeight);
    }

    private Storyboard CreateMirrorBreak(PageSnapshot snapshot, bool returning)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var center = new Point(width * .49, height * .5);
        var storyboard = new Storyboard { Duration = Milliseconds(DurationMs) };

        // A restrained dark layer makes the opening feel like light breaking into
        // a dark room. It fades only after the center has begun to reveal the
        // destination, so this remains a fracture reveal rather than a crossfade.
        var darkness = new Rectangle { Width = width, Height = height,
            Fill = Brush(235, 2, 8, 20), Opacity = 0 };
        _scene.Children.Add(darkness);
        Track(storyboard, darkness, "Opacity", (0, .32), (260, .32), (560, .10),
            (850, 0));

        var penumbra = new Image { Width = width, Height = height, Stretch = Stretch.Fill,
            Source = CreatePenumbraTexture(), Opacity = 1 };
        _scene.Children.Add(penumbra);
        Track(storyboard, penumbra, "Opacity", (0, 1), (600, 1), (900, .5), (1200, .1), (DurationMs, 0));

        var halo = AddGlow(_scene, center, 240, 190);
        Track(storyboard, halo, "Opacity", (0, 0), (300, .02), (560, .12),
            (820, .23), (1100, .18), (1450, 0));

        var mesh = PortalFractureMesh.Create();
        foreach (var fragment in mesh.OrderBy(f => f.Depth))
        {
            var points = fragment.Points.Select(p => new Point(p.X * width, p.Y * height)).ToList();
            AddMirrorPiece(storyboard, snapshot, points, fragment, center, returning);
        }
        AddCracks(storyboard, mesh);
        AddFlyingSplinters(storyboard, center);
        var flash = AddGlow(_scene, center, 110, 110);
        Track(storyboard, flash, "Opacity", (0, 0), (95, .38), (145, .72),
            (220, .28), (360, .08), (520, 0));
        ScaleTrack(storyboard, (CompositeTransform)flash.RenderTransform,
            (0, .03), (145, 1), (270, .42), (520, .55));
        return storyboard;
    }

    private void AddMirrorPiece(Storyboard storyboard, PageSnapshot snapshot, List<Point> vertices,
        PortalFractureMesh.Fragment fragment, Point center, bool returning)
    {
        var left = Math.Max(0, (int)Math.Floor(vertices.Min(p => p.X) * snapshot.ScaleX));
        var top = Math.Max(0, (int)Math.Floor(vertices.Min(p => p.Y) * snapshot.ScaleY));
        var right = Math.Min(snapshot.Width, (int)Math.Ceiling(vertices.Max(p => p.X) * snapshot.ScaleX));
        var bottom = Math.Min(snapshot.Height, (int)Math.Ceiling(vertices.Max(p => p.Y) * snapshot.ScaleY));
        var width = (right - left) / snapshot.ScaleX;
        var height = (bottom - top) / snapshot.ScaleY;
        var local = vertices.Select(p => new Point(p.X - left / snapshot.ScaleX, p.Y - top / snapshot.ScaleY)).ToArray();
        var centroid = new Point(vertices.Average(p => p.X), vertices.Average(p => p.Y));
        var transform = new CompositeTransform { CenterX = centroid.X - left / snapshot.ScaleX, CenterY = centroid.Y - top / snapshot.ScaleY };
        var side = fragment.Side;
        var index = fragment.Delay;
        var projection = new PlaneProjection
        {
            CenterOfRotationX = (centroid.X - left / snapshot.ScaleX) / width,
            CenterOfRotationY = (centroid.Y - top / snapshot.ScaleY) / height
        };
        var piece = new Canvas { Width = width, Height = height, RenderTransform = transform, Projection = projection };
        Canvas.SetLeft(piece, left / snapshot.ScaleX);
        Canvas.SetTop(piece, top / snapshot.ScaleY);
        piece.Children.Add(new Image { Width = width, Height = height, Stretch = Stretch.Fill,
            Source = CutMirrorTile(snapshot, vertices, left, top, right - left, bottom - top) });
        // Darken only the old-page tiles. A global veil would also dim the new
        // page in the open gap and turn this back into an ordinary crossfade.
        var tint = new Polygon { Fill = Brush(255, 3, 12, 28), Opacity = 0 };
        foreach (var point in local) tint.Points.Add(point);
        piece.Children.Add(tint);
        Track(storyboard, tint, "Opacity", (0, 0), (180, .76), (540, .76), (1220, .66), (DurationMs, .5));
        var refraction = new Image { Width = width, Height = height, Stretch = Stretch.Fill, Opacity = 0 };
        piece.Children.Add(refraction);
        _reflections.Add((refraction, vertices));
        var reflection = new Polygon
        {
            Opacity = 0,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
                GradientStops = { Stop(0, 109, 210, 255, 0), Stop(18, 190, 242, 255, .35),
                    Stop(60, 155, 215, 255, .47), Stop(0, 109, 170, 255, .51), Stop(12, 229, 167, 255, 1) }
            }
        };
        foreach (var point in local) reflection.Points.Add(point);
        piece.Children.Add(reflection);
        AddBevel(storyboard, piece, local, fragment);
        AddGlassFacets(storyboard, piece, local, fragment);
        _scene.Children.Add(piece);
        var depth = fragment.Depth;
        var launch = OpeningStartMs + fragment.Delay;
        var normalizedDistance = Math.Sqrt(Math.Pow(centroid.X / ActualWidth - .49, 2) + Math.Pow(centroid.Y / ActualHeight - .5, 2));
        var finish = (int)(1080 + Math.Min(1, normalizedDistance / .48) * 390) - depth * 18;
        var angle = Math.Atan2(centroid.Y - center.Y, centroid.X - center.X);
        // Break as two opposing mirror halves. The original radial angle still
        // contributes a small vertical drift, but the dominant motion is left /
        // right so the opening reads as a pane splitting in two.
        var dx = side * ActualWidth * (.66 + depth * .085);
        var dy = Math.Sin(angle) * ActualHeight * (.035 + depth * .022);
        var spin = (7 + index % 19) * Math.Sin(angle) * side * (returning ? -1 : 1);
        Track(storyboard, reflection, "Opacity", (0, 0), (launch, 0), (launch + 190, .48), (1120, .38), (DurationMs, 0));
        Track(storyboard, refraction, "Opacity", (0, 0), (launch, 0), (launch + 230, .6), (finish, .4));
        // Keep the old picture opaque throughout the visible opening. Fade only
        // the final edge remnants after the tiles have travelled off the viewport.
        Track(storyboard, piece, "Opacity", (0, 1), (finish - 70, 1), (finish, 0));
        Move(storyboard, transform, "TranslateX", launch, finish, 0, dx);
        Move(storyboard, transform, "TranslateY", launch, finish, 0, dy);
        Move(storyboard, transform, "Rotation", launch, finish, 0, spin);
        Move(storyboard, transform, "ScaleX", launch, finish, 1, .94 + depth * .075);
        Move(storyboard, transform, "ScaleY", launch, finish, 1, .96 + depth * .04);
        Move(storyboard, projection, "RotationX", launch, finish, 0, Math.Sin(angle) * (10 + depth * 8));
        Move(storyboard, projection, "RotationY", launch, finish, 0, -side * (14 + depth * 11));
    }

    private static WriteableBitmap CutMirrorTile(PageSnapshot snapshot, List<Point> polygon, int left, int top, int width, int height,
        bool refracted = false)
    {
        // Scanline masking keeps each fragment's original page pixels aligned.
        // Outside the shared fracture polygon, pixels remain fully transparent.
        var points = polygon.Select(p => new Point(p.X * snapshot.ScaleX, p.Y * snapshot.ScaleY)).ToArray();
        var pixels = new byte[width * height * 4];
        var intersections = new List<double>(points.Length);
        for (var row = 0; row < height; row++)
        {
            var y = top + row + .5;
            intersections.Clear();
            for (var i = 0; i < points.Length; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Length];
                if ((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y))
                    intersections.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
            }
            intersections.Sort();
            for (var i = 0; i + 1 < intersections.Count; i += 2)
            {
                var start = Math.Clamp((int)Math.Ceiling(intersections[i] - .5), left, left + width);
                var end = Math.Clamp((int)Math.Ceiling(intersections[i + 1] - .5), left, left + width);
                if (end > start && !refracted)
                    Buffer.BlockCopy(snapshot.Pixels, ((top + row) * snapshot.Width + start) * 4,
                        pixels, (row * width + start - left) * 4, (end - start) * 4);
                else if (refracted)
                {
                    for (var x = start; x < end; x++)
                    {
                        var distance = double.MaxValue;
                        for (var edge = 0; edge < points.Length; edge++)
                        {
                            var a = points[edge]; var b = points[(edge + 1) % points.Length];
                            var dx = b.X - a.X; var dy = b.Y - a.Y;
                            var length = dx * dx + dy * dy;
                            if (length < .0001) continue;
                            var t = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / length, 0, 1);
                            var ex = x - a.X - t * dx; var ey = y - a.Y - t * dy;
                            distance = Math.Min(distance, Math.Sqrt(ex * ex + ey * ey));
                        }
                        // The picture refracts only near a fracture. The center
                        // of each pane keeps the opaque outgoing UI, not a fade.
                        var band = 13 * snapshot.ScaleX;
                        var strength = Math.Pow(Math.Max(0, 1 - distance / band), 1.7) * .6;
                        if (strength < .005) continue;
                        var sampleX = Math.Clamp((int)(snapshot.Width - x + 24 * snapshot.ScaleX), 2, snapshot.Width - 3);
                        var sampleY = Math.Clamp((int)(y - 18 * snapshot.ScaleY), 0, snapshot.Height - 1);
                        var source = (sampleY * snapshot.Width + sampleX) * 4;
                        var target = (row * width + x - left) * 4;
                        pixels[target] = (byte)(snapshot.Pixels[source + 8] * strength);
                        pixels[target + 1] = (byte)(snapshot.Pixels[source + 1] * strength);
                        pixels[target + 2] = (byte)(snapshot.Pixels[source - 8 + 2] * strength);
                        pixels[target + 3] = (byte)(255 * strength);
                    }
                }
            }
        }
        var bitmap = new WriteableBitmap(width, height);
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();
        return bitmap;
    }

    private void AddCracks(Storyboard storyboard, IReadOnlyList<PortalFractureMesh.Fragment> mesh)
    {
        var cracks = new Canvas { Width = ActualWidth, Height = ActualHeight };
        _scene.Children.Add(cracks);
        Track(storyboard, cracks, "Opacity", (0, 1), (OpeningStartMs, 1), (450, 0));
        var edges = new HashSet<(Vector2, Vector2)>();
        var candidates = new List<(Vector2 A, Vector2 B, double Length)>();
        var impact = new Vector2(.49f, .5f);
        foreach (var fragment in mesh)
        for (var i = 0; i < fragment.Points.Length; i++)
        {
            var a = fragment.Points[i]; var b = fragment.Points[(i + 1) % fragment.Points.Length];
            if ((a.X == b.X && a.X is 0 or 1) || (a.Y == b.Y && a.Y is 0 or 1)) continue;
            if (edges.Contains((b, a)) || !edges.Add((a, b))) continue;
            if (Vector2.DistanceSquared(a, impact) > Vector2.DistanceSquared(b, impact)) (a, b) = (b, a);
            candidates.Add((a, b, Vector2.Distance(a, b)));
        }
        // Keep the fracture readable as a handful of main branches. Bevels and
        // small glass facets carry the fine detail; every tiny mesh edge should
        // not become a full-screen luminous line.
        foreach (var (a, b, _) in candidates.OrderBy(e => Vector2.Distance(e.A, impact)).Take(26))
        {
            for (var j = 0; j < 5; j++)
            {
                var start = Vector2.Lerp(a, b, j / 5f);
                var end = Vector2.Lerp(a, b, (j + 1) / 5f);
                Point[] line = [new(start.X * ActualWidth, start.Y * ActualHeight), new(end.X * ActualWidth, end.Y * ActualHeight)];
                var section = new Canvas { Opacity = 0 };
                AddStroke(section, line, Brush(24, 102, 188, 210), 3.2);
                AddStroke(section, line, Brush(135, 176, 244, 230), 1.05);
                AddStroke(section, line, Brush(245, 229, 249, 220), .38);
                cracks.Children.Add(section);
                var time = 75 + (int)(Vector2.Distance(start, impact) * 360);
                Track(storyboard, section, "Opacity", (0, 0), (time, 0), (time + 18, 1));
            }
        }
    }

    private static void AddBevel(Storyboard storyboard, Canvas piece, Point[] vertices, PortalFractureMesh.Fragment fragment)
    {
        var bevel = new Canvas { Opacity = 0 };
        var center = new Point(vertices.Average(p => p.X), vertices.Average(p => p.Y));
        for (var i = 0; i < vertices.Length; i++)
        {
            var a = vertices[i]; var b = vertices[(i + 1) % vertices.Length];
            // Inset each edge toward the interior. The broad refractive bevel and
            // narrow white crest form a visible glass thickness, not a neon outline.
            var thickness = .009 + (i + fragment.Delay) % 5 * .006;
            var insetA = new Point(a.X + (center.X - a.X) * thickness, a.Y + (center.Y - a.Y) * thickness);
            var insetB = new Point(b.X + (center.X - b.X) * thickness * 1.8, b.Y + (center.Y - b.Y) * thickness * 1.8);
            var facet = new Polygon
            {
                Points = { a, b, insetB, insetA },
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
                    GradientStops = { Stop(200, 214, 247, 255, 0), Stop(65, 84, 195, 247, .28),
                        Stop(210, 251, 207, 255, .55), Stop(50, 32, 76, 145, .83), Stop(5, 14, 28, 55, 1) }
                }
            };
            bevel.Children.Add(facet);
            AddStroke(bevel, [a, b], Brush(18, 92, 192, 220), 4.5);
            AddStroke(bevel, [a, b], Brush(130, 198, 241, 235), .62);
            if (i % 2 == 0) AddStroke(bevel, [insetA, insetB], Brush(95, 210, 245, 225), .42);
        }
        piece.Children.Add(bevel);
        var start = OpeningStartMs + fragment.Delay;
        Track(storyboard, bevel, "Opacity", (0, 0), (start, 0), (start + 140, .9), (1120, .8), (DurationMs, .22));
    }

    private static void AddGlassFacets(Storyboard storyboard, Canvas piece, Point[] vertices, PortalFractureMesh.Fragment fragment)
    {
        var facets = new Canvas { Opacity = 0 };
        var center = new Point(vertices.Average(p => p.X), vertices.Average(p => p.Y));
        for (var i = 0; i < vertices.Length; i++)
        {
            var a = vertices[i]; var b = vertices[(i + 1) % vertices.Length];
            var pink = (i + fragment.Depth) % 3 == 0;
            var tip = new Point(a.X * .18 + b.X * .18 + center.X * .64,
                a.Y * .18 + b.Y * .18 + center.Y * .64);
            var facet = new Polygon
            {
                Points = { a, b, tip },
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(fragment.Side < 0 ? 1 : 0, .1),
                    EndPoint = new Point(fragment.Side < 0 ? 0 : 1, .9),
                    GradientStops =
                    {
                        Stop(0, 180, 225, 255, 0),
                        Stop((byte)(pink ? 80 : 50), (byte)(pink ? 240 : 118), (byte)(pink ? 187 : 230), 255, .18),
                        Stop(6, 130, 200, 255, .38),
                        Stop(0, 120, 195, 255, 1)
                    }
                }
            };
            facets.Children.Add(facet);
            // Fine branch fractures travel with their parent glass, so they do
            // not remain suspended across the newly revealed destination.
            if (i % 2 == 0)
            {
                var root = new Point(a.X * .58 + b.X * .42, a.Y * .58 + b.Y * .42);
                var kink = new Point(root.X * .64 + center.X * .36, root.Y * .64 + center.Y * .36);
                var end = new Point(kink.X * .7 + a.X * .3, kink.Y * .7 + a.Y * .3);
                AddStroke(facets, [root, kink, end], Brush(95, 154, 232, 255), .55);
            }
        }
        piece.Children.Add(facets);
        var start = OpeningStartMs + fragment.Delay;
        Track(storyboard, facets, "Opacity", (0, 0), (160, 0), (start, .08), (start + 240, .48), (DurationMs, .15));
    }

    private void AddFlyingSplinters(Storyboard storyboard, Point center)
    {
        var random = new Random(3748);
        for (var i = 0; i < 8; i++)
        {
            var side = i % 2 == 0 ? -1 : 1;
            var size = 7 + random.NextDouble() * 26;
            var transform = new CompositeTransform { CenterX = size * .5, CenterY = size * .35 };
            var splinter = new Polygon
            {
                Opacity = 0, RenderTransform = transform,
                Points = { new(0, size * .3), new(size, 0), new(size * .65, size * .9), new(size * .3, size * .56) },
                Fill = new LinearGradientBrush
                {
                    StartPoint = new(0, 0), EndPoint = new(1, 1),
                    GradientStops = { Stop(35, 67, 173, 255, 0), Stop(190, 198, 234, 255, .25),
                        Stop(12, 70, 146, 220, .48), Stop(160, 244, 189, 255, 1) }
                },
                Stroke = Brush(200, 215, 244, 255), StrokeThickness = .6
            };
            Canvas.SetLeft(splinter, center.X + side * (8 + random.NextDouble() * 75));
            Canvas.SetTop(splinter, center.Y + (random.NextDouble() - .5) * ActualHeight * .24);
            _scene.Children.Add(splinter);
            var start = 350 + i * 22;
            var finish = 1160 + i * 24;
            Track(storyboard, splinter, "Opacity", (0, 0), (start, 0), (start + 65, .4 + random.NextDouble() * .5), (finish - 120, .4), (finish, 0));
            Move(storyboard, transform, "TranslateX", start, finish, 0, side * ActualWidth * (.48 + random.NextDouble() * .3));
            Move(storyboard, transform, "TranslateY", start, finish, 0, (random.NextDouble() - .5) * ActualHeight * .65);
            Move(storyboard, transform, "Rotation", start, finish, 0, side * (25 + random.NextDouble() * 115));
            Move(storyboard, transform, "ScaleX", start, finish, .5, 1 + random.NextDouble());
            Move(storyboard, transform, "ScaleY", start, finish, .5, 1 + random.NextDouble());
        }
    }

    private static void AddStroke(Canvas canvas, Point[] points, Brush brush, double thickness)
    {
        // Geometry is owned by one Path only (WinUI rejects shared PathGeometry).
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = points[0], IsClosed = false, IsFilled = false };
        foreach (var point in points.Skip(1)) figure.Segments.Add(new LineSegment { Point = point });
        geometry.Figures.Add(figure);
        canvas.Children.Add(new ShapePath { Data = geometry, Stroke = brush, StrokeThickness = thickness });
    }

    private Image AddGlow(Canvas parent, Point center, double width, double height)
    {
        var image = new Image
        {
            Source = _glowTexture ??= CreateGlowTexture(), Width = width, Height = height,
            Stretch = Stretch.Fill, Opacity = 0,
            RenderTransform = new CompositeTransform { CenterX = width / 2, CenterY = height / 2 }
        };
        Canvas.SetLeft(image, center.X - width / 2);
        Canvas.SetTop(image, center.Y - height / 2);
        parent.Children.Add(image);
        return image;
    }

    private static WriteableBitmap CreateGlowTexture()
    {
        const int size = 192;
        var bitmap = new WriteableBitmap(size, size);
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var nx = (x + .5) / size * 2 - 1;
            var ny = (y + .5) / size * 2 - 1;
            var radius = Math.Sqrt(nx * nx + ny * ny);
            var core = Math.Exp(-radius * radius * 32);
            var pink = Math.Clamp((nx - ny + 1) / 2, 0, 1);
            var alpha = Math.Clamp(Math.Pow(Math.Max(0, 1 - radius), 2) * 1.5, 0, 1);
            var offset = (y * size + x) * 4;
            pixels[offset] = (byte)(255 * alpha);
            pixels[offset + 1] = (byte)((145 + 90 * (1 - pink) + 20 * core) * alpha);
            pixels[offset + 2] = (byte)((65 + 165 * pink + 25 * core) * alpha);
            pixels[offset + 3] = (byte)(255 * alpha);
        }
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();
        return bitmap;
    }

    private static WriteableBitmap CreatePenumbraTexture()
    {
        // Light reaches the center first. This soft shadow is behind all opaque
        // shards and never cuts an artificial aperture into the outgoing page.
        const int size = 256;
        var bitmap = new WriteableBitmap(size, size);
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var nx = (x / (double)(size - 1) - .49) * 2;
            var ny = (y / (double)(size - 1) - .5) * 2;
            var radius = Math.Sqrt(nx * nx + ny * ny * .7);
            var t = Math.Clamp((radius - .12) / .9, 0, 1);
            var alpha = .88 * t * t * (3 - 2 * t);
            var offset = (y * size + x) * 4;
            pixels[offset] = (byte)(20 * alpha);
            pixels[offset + 1] = (byte)(8 * alpha);
            pixels[offset + 2] = (byte)(2 * alpha);
            pixels[offset + 3] = (byte)(255 * alpha);
        }
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();
        return bitmap;
    }

    private void StartPageRebound(FrameworkElement? content)
    {
        if (content is null) return;
        content.UpdateLayout();
        _pageVisual = ElementCompositionPreview.GetElementVisual(content);
        _pageScale = _pageVisual.Scale;
        _pageCenter = _pageVisual.CenterPoint;
        _pageVisual.CenterPoint = new Vector3((float)content.ActualWidth / 2, (float)content.ActualHeight / 2, 0);
        var compositor = _pageVisual.Compositor;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(DurationMs);
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, .7f), new Vector2(.25f, 1));
        // The revealed page stays still while the mirror opens. Settle only once
        // the departing tiles are near the outer edges, within the content slot.
        animation.InsertKeyFrame(0, _pageScale);
        animation.InsertKeyFrame(.78f, _pageScale);
        animation.InsertKeyFrame(.87f, _pageScale * new Vector3(.995f, .995f, 1), ease);
        animation.InsertKeyFrame(1, _pageScale, ease);
        _pageVisual.StartAnimation("Scale", animation);
    }

    private async Task PlayAsync(Storyboard storyboard, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, object args) => completion.TrySetResult();
        storyboard.Completed += Completed;
        _storyboards.Add(storyboard);
        using var registration = token.Register(() => completion.TrySetCanceled(token));
        try { storyboard.Begin(); await completion.Task; }
        finally { storyboard.Completed -= Completed; }
    }

    private static void Track(Storyboard storyboard, DependencyObject target, string property, params (int Time, double Value)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        foreach (var frame in frames)
            animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(frame.Time)), Value = frame.Value });
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private static void ScaleTrack(Storyboard storyboard, CompositeTransform target, params (int Time, double Value)[] frames)
    {
        Track(storyboard, target, "ScaleX", frames);
        Track(storyboard, target, "ScaleY", frames);
    }

    private static void Move(Storyboard storyboard, DependencyObject target, string property,
        int start, int finish, double from, double to)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = from });
        animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(start)), Value = from });
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(finish)), Value = to,
            KeySpline = new KeySpline { ControlPoint1 = new Point(.42, .08), ControlPoint2 = new Point(.75, .65) }
        });
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }
    private static Duration Milliseconds(int value) => new(TimeSpan.FromMilliseconds(value));
    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));
    private static GradientStop Stop(byte a, byte r, byte g, byte b, double offset) => new() { Color = Color.FromArgb(a, r, g, b), Offset = offset };
}
