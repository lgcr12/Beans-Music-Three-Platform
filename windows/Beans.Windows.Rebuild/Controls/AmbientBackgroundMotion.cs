using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Beans.Windows.Rebuild.Controls;

// One clock keeps the background continuous when playback is paused or resumed.
internal sealed class AmbientBackgroundMotion
{
    private readonly Canvas _canvas;
    private readonly ScaleTransform _scale;
    private readonly TranslateTransform _pan;
    private readonly Image _overlay;
    private readonly Image _personImage;
    private readonly CompositeTransform _personTransform;
    private readonly Canvas _visualizerCanvas;
    private readonly Canvas _ringsCanvas;
    private readonly Canvas _windCanvas;
    private readonly ScaleTransform _windScale = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Stopwatch _clock = new();
    private readonly List<(Ellipse Dot, TranslateTransform Position, double X, double Y, double Speed)> _particles = [];
    private readonly List<Rectangle> _visualizerBars = [];
    private readonly List<(Ellipse Ring, ScaleTransform Scale, double Phase)> _rings = [];
    private readonly List<(Microsoft.UI.Xaml.Shapes.Path Wisp, TranslateTransform Position, double Phase, double Speed)> _windWisps = [];
    private double _lastTime;
    private double _phase;
    private bool _playing;

    public AmbientBackgroundMotion(Canvas canvas, ScaleTransform scale, TranslateTransform pan, Image overlay, Canvas visualizerCanvas, Canvas ringsCanvas, Canvas windCanvas, Image personImage, CompositeTransform personTransform)
    {
        (_canvas, _scale, _pan, _overlay, _visualizerCanvas, _ringsCanvas, _windCanvas, _personImage, _personTransform) = (canvas, scale, pan, overlay, visualizerCanvas, ringsCanvas, windCanvas, personImage, personTransform);
        _windCanvas.RenderTransform = _windScale;
        _windCanvas.RenderTransformOrigin = new global::Windows.Foundation.Point(0, 0);
        var random = new Random(2718);
        for (var i = 0; i < 38; i++)
        {
            var size = 2.2 + random.NextDouble() * 4.8;
            var position = new TranslateTransform();
            var dot = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(Color.FromArgb(220, 185, 255, 244)),
                Opacity = 0, RenderTransform = position, IsHitTestVisible = false };
            canvas.Children.Add(dot);
            _particles.Add((dot, position, .18 + random.NextDouble() * .8, random.NextDouble(), .012 + random.NextDouble() * .024));
        }

        // A restrained live-stage equalizer gives the reference composition a pulse
        // without sampling audio or adding a high-cost visualizer pipeline.
        for (var i = 0; i < 56; i++)
        {
            var bar = new Rectangle
            {
                Width = 3,
                Height = 8,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(190, 144, 255, 232)),
                Opacity = 0,
                IsHitTestVisible = false
            };
            visualizerCanvas.Children.Add(bar);
            _visualizerBars.Add(bar);
        }

        // Two soft rings are intentionally low opacity and remain beneath the lyric
        // column. They read as stage light rather than a foreground ornament.
        for (var i = 0; i < 2; i++)
        {
            var transform = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
            var ring = new Ellipse
            {
                Width = i == 0 ? 270 : 420,
                Height = i == 0 ? 270 : 420,
                Stroke = new SolidColorBrush(Color.FromArgb(i == 0 ? (byte)58 : (byte)34, 118, 255, 227)),
                StrokeThickness = i == 0 ? 1.5 : 1,
                Fill = new SolidColorBrush(Colors.Transparent),
                Opacity = 0,
                RenderTransformOrigin = new global::Windows.Foundation.Point(.5, .5),
                RenderTransform = transform,
                IsHitTestVisible = false
            };
            ringsCanvas.Children.Add(ring);
            _rings.Add((ring, transform, i * 2.4));
        }

        // Hair-like light wisps sit over the singer's silhouette. They are kept
        // narrow and translucent so the lyric column remains completely clear.
        var wispDefinitions = new[]
        {
            (80d, 270d, 145d, 185d, 236d, 80d),
            (118d, 340d, 185d, 250d, 300d, 130d),
            (170d, 390d, 238d, 300d, 360d, 190d),
            (215d, 425d, 285d, 330d, 410d, 250d),
            (255d, 300d, 310d, 245d, 390d, 160d)
        };
        for (var i = 0; i < wispDefinitions.Length; i++)
        {
            var d = wispDefinitions[i];
            var figure = new PathFigure { StartPoint = new global::Windows.Foundation.Point(d.Item1, d.Item2), IsClosed = false };
            figure.Segments.Add(new BezierSegment
            {
                Point1 = new global::Windows.Foundation.Point(d.Item3, d.Item4),
                Point2 = new global::Windows.Foundation.Point(d.Item5 - 35, d.Item6 + 45),
                Point3 = new global::Windows.Foundation.Point(d.Item5, d.Item6)
            });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            var position = new TranslateTransform();
            var wisp = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = geometry,
                Stroke = new SolidColorBrush(Color.FromArgb(132, 166, 255, 239)),
                StrokeThickness = i % 2 == 0 ? 2.2 : 1.4,
                Opacity = 0,
                RenderTransform = position,
                IsHitTestVisible = false
            };
            windCanvas.Children.Add(wisp);
            _windWisps.Add((wisp, position, i * .9, .72 + i * .08));
        }
        _timer.Tick += (_, _) => Tick();
    }

    public void Start(bool playing)
    {
        _playing = playing;
        _lastTime = 0;
        _clock.Restart();
        _timer.Start();
        _canvas.Visibility = Visibility.Visible;
        _visualizerCanvas.Visibility = Visibility.Visible;
        _ringsCanvas.Visibility = Visibility.Visible;
        _windCanvas.Visibility = Visibility.Visible;
        _personImage.Visibility = _personImage.Source is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetPlaying(bool playing) => _playing = playing;

    public void Stop()
    {
        _timer.Stop();
        _clock.Stop();
        _canvas.Visibility = Visibility.Collapsed;
        _visualizerCanvas.Visibility = Visibility.Collapsed;
        _ringsCanvas.Visibility = Visibility.Collapsed;
        _windCanvas.Visibility = Visibility.Collapsed;
        _personImage.Visibility = Visibility.Collapsed;
    }

    private void Tick()
    {
        var now = _clock.Elapsed.TotalSeconds;
        _phase += Math.Min(.15, now - _lastTime) * (_playing ? 1 : .32);
        _lastTime = now;
        _scale.ScaleX = _scale.ScaleY = 1.04 + .012 * Math.Sin(_phase * .17);
        _pan.X = 8 * Math.Sin(_phase * .12);
        _pan.Y = 5 * Math.Cos(_phase * .1);
        _overlay.Opacity = .13 + .07 * Math.Sin(_phase * .45);
        if (_personImage.Visibility == Visibility.Visible)
        {
            var breeze = Math.Sin(_phase * .82);
            _personTransform.ScaleX = _personTransform.ScaleY = 1.004 + .008 * (.5 + .5 * Math.Sin(_phase * .31));
            _personTransform.TranslateX = 2.5 * breeze;
            _personTransform.TranslateY = 1.4 * Math.Cos(_phase * .64);
            _personTransform.Rotation = .28 * Math.Sin(_phase * .58);
            _personImage.Opacity = .24 + .08 * (.5 + .5 * Math.Sin(_phase * .44));
        }
        if (_windCanvas.ActualWidth > 0 && _windCanvas.ActualHeight > 0)
        {
            _windScale.ScaleX = _windCanvas.ActualWidth / 1536d;
            _windScale.ScaleY = _windCanvas.ActualHeight / 1024d;
        }
        foreach (var wisp in _windWisps)
        {
            var sway = Math.Sin(_phase * wisp.Speed + wisp.Phase);
            wisp.Position.X = 7.5 * sway;
            wisp.Position.Y = 3.2 * Math.Cos(_phase * wisp.Speed * .83 + wisp.Phase);
            wisp.Wisp.Opacity = .18 + .32 * (.5 + .5 * Math.Sin(_phase * wisp.Speed * .7 + wisp.Phase));
        }
        foreach (var particle in _particles)
        {
            var progress = (particle.Y + _phase * particle.Speed) % 1;
            particle.Position.X = particle.X * _canvas.ActualWidth + 22 * Math.Sin(_phase * .35 + particle.Y * 6);
            particle.Position.Y = (1 - progress) * _canvas.ActualHeight;
            particle.Dot.Opacity = .58 * Math.Sin(Math.PI * progress);
        }

        var width = _visualizerCanvas.ActualWidth;
        var height = _visualizerCanvas.ActualHeight;
        if (width > 0 && height > 0)
        {
            const double gap = 5.2;
            var totalWidth = (_visualizerBars.Count - 1) * gap;
            var startX = width * .55 - totalWidth / 2;
            var baseline = height * .78;
            for (var i = 0; i < _visualizerBars.Count; i++)
            {
                var normalized = i / (double)(_visualizerBars.Count - 1);
                var envelope = Math.Sin(normalized * Math.PI);
                var pulse = .5 + .5 * Math.Sin(_phase * 2.1 + i * .41);
                var secondary = .5 + .5 * Math.Sin(_phase * 1.17 - i * .19);
                var barHeight = 5 + envelope * (10 + 28 * pulse * secondary);
                var bar = _visualizerBars[i];
                bar.Height = barHeight;
                bar.Opacity = .22 + .34 * envelope;
                Canvas.SetLeft(bar, startX + i * gap);
                Canvas.SetTop(bar, baseline - barHeight);
            }
        }

        foreach (var ring in _rings)
        {
            var pulse = .5 + .5 * Math.Sin(_phase * .34 + ring.Phase);
            ring.Scale.ScaleX = ring.Scale.ScaleY = .94 + pulse * .13;
            ring.Ring.Opacity = .1 + pulse * .2;
            Canvas.SetLeft(ring.Ring, width * .69 - ring.Ring.Width / 2);
            Canvas.SetTop(ring.Ring, height * .61 - ring.Ring.Height / 2);
        }
    }
}
