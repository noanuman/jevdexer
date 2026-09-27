using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Jevdexer.App;

/// <summary>
/// The mascot, drawn from its vector outline (brand/mascot.svg) so it can move: it breathes (a slow
/// squash and stretch from its base), blinks at random intervals, and its eyes can look toward a
/// point. Set <see cref="Animated"/> false for stills.
/// </summary>
public sealed class Mascot : Viewbox
{
    private const string BodyPath =
        "M57.90 8.08 C54.94 8.19 51.98 9.04 49.03 10.46 C46.07 11.89 43.11 14.27 40.16 16.63 C37.20 18.99 34.24 22.47 31.29 24.63 " +
        "C28.33 26.80 25.28 27.48 22.42 29.61 C19.55 31.74 16.16 34.62 14.09 37.40 C12.01 40.17 10.90 43.31 9.98 46.27 C9.06 49.22 9.31 52.18 8.57 55.14 " +
        "C7.83 58.09 6.34 61.05 5.54 64.01 C4.75 66.96 3.11 70.08 3.81 72.88 C4.51 75.67 7.29 78.95 9.76 80.77 C12.23 82.59 15.67 82.36 18.63 83.80 " +
        "C21.59 85.25 24.54 88.11 27.50 89.43 C30.46 90.74 33.41 91.36 36.37 91.70 C39.33 92.04 42.28 91.66 45.24 91.48 C48.20 91.30 51.15 90.76 54.11 90.62 " +
        "C57.07 90.47 60.02 90.78 62.98 90.62 C65.94 90.46 68.89 90.35 71.85 89.64 C74.81 88.94 77.78 88.11 80.72 86.40 C83.66 84.69 87.17 82.02 89.48 79.37 " +
        "C91.79 76.72 93.43 73.45 94.57 70.50 C95.70 67.54 96.21 64.58 96.30 61.63 C96.39 58.67 95.97 55.71 95.11 52.76 C94.24 49.80 92.91 46.85 91.10 43.89 " +
        "C89.30 40.93 86.40 37.98 84.29 35.02 C82.18 32.06 80.07 29.11 78.45 26.15 C76.83 23.19 76.50 20.00 74.55 17.28 C72.61 14.56 69.54 11.35 66.77 9.82 " +
        "C63.99 8.28 60.85 7.98 57.90 8.08 Z";

    private static readonly Color Ink = Color.FromRgb(0x0b, 0x0b, 0x0c);
    private static readonly Color Paper = Color.FromRgb(0xf4, 0xf2, 0xee);

    private readonly ScaleTransform _breath = new(1, 1, 50, 91);
    private readonly ScaleTransform _blink = new(1, 1);
    private readonly TranslateTransform _look = new();
    private readonly TranslateTransform _pupils = new();
    private readonly DispatcherTimer _followTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _blinkTimer = new();
    private static readonly Random Rng = new();

    public static readonly DependencyProperty AnimatedProperty =
        DependencyProperty.Register(nameof(Animated), typeof(bool), typeof(Mascot), new PropertyMetadata(true, (d, _) => ((Mascot)d).Sync()));

    /// <summary>Breathing and blinking (default on).</summary>
    public bool Animated
    {
        get => (bool)GetValue(AnimatedProperty);
        set => SetValue(AnimatedProperty, value);
    }

    public static readonly DependencyProperty FollowCursorProperty =
        DependencyProperty.Register(nameof(FollowCursor), typeof(bool), typeof(Mascot), new PropertyMetadata(false, (d, _) => ((Mascot)d).Sync()));

    /// <summary>The pupils follow the mouse pointer, anywhere on screen (the tour's welcome).</summary>
    public bool FollowCursor
    {
        get => (bool)GetValue(FollowCursorProperty);
        set => SetValue(FollowCursorProperty, value);
    }

    public Mascot()
    {
        Stretch = Stretch.Uniform;
        var canvas = new Canvas { Width = 100, Height = 100 };

        var shade = new RadialGradientBrush
        {
            Center = new Point(0.38, 0.3), GradientOrigin = new Point(0.38, 0.3), RadiusX = 0.8, RadiusY = 0.8,
            GradientStops = { new(Color.FromRgb(0x2b, 0x2b, 0x2f), 0), new(Color.FromRgb(0x14, 0x14, 0x16), 0.55), new(Color.FromRgb(0x07, 0x07, 0x08), 1) },
        };
        // A rim of light along the top edge: invisible on light backgrounds, it keeps the black
        // blob from vanishing on dark ones (the dark theme, a dark taskbar).
        var rim = new LinearGradientBrush(Color.FromArgb(110, 255, 255, 255), Color.FromArgb(18, 255, 255, 255), 90);
        var body = new Path { Data = Geometry.Parse(BodyPath), Fill = shade, Stroke = rim, StrokeThickness = 0.9 };

        // The face is one group: it blinks (eyes only, scale Y around the eye line) and looks around.
        var face = new Canvas { RenderTransform = _look };
        var eyes = new Canvas { RenderTransform = _blink };
        _blink.CenterY = 55;
        eyes.Children.Add(Eye(39.65, 56.85, 6.72, 7.03, 42.32, 56.62, 4.93, _pupils));
        eyes.Children.Add(Eye(69.78, 52.96, 6.45, 6.84, 67.49, 53.40, 5.03, _pupils));
        face.Children.Add(eyes);
        face.Children.Add(new Path
        {
            Data = Geometry.Parse("M51.41 60.87 Q55.52 63.53 59.63 60.65"),
            Stroke = new SolidColorBrush(Paper), StrokeThickness = 1.57, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });

        var figure = new Canvas { Width = 100, Height = 100, RenderTransform = _breath };
        figure.Children.Add(body);
        figure.Children.Add(face);
        canvas.Children.Add(figure);
        Child = canvas;

        _blinkTimer.Tick += (_, _) => Blink();
        _followTimer.Tick += (_, _) => Follow();
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) => Sync();
    }

    /// <summary>An eye: the white, and the pupil with its shine, which moves (<paramref name="pupils"/>) clipped to the white.</summary>
    private static Canvas Eye(double cx, double cy, double rx, double ry, double px, double py, double pr, Transform pupils)
    {
        var eye = new Canvas { Clip = new EllipseGeometry(new Point(cx, cy), rx, ry) };
        eye.Children.Add(Place(new Ellipse { Width = rx * 2, Height = ry * 2, Fill = new SolidColorBrush(Paper) }, cx - rx, cy - ry));
        var pupil = new Canvas { RenderTransform = pupils };
        pupil.Children.Add(Place(new Ellipse { Width = pr * 2, Height = pr * 2, Fill = new SolidColorBrush(Ink) }, px - pr, py - pr));
        var shine = pr * 0.28;
        pupil.Children.Add(Place(new Ellipse { Width = shine * 2, Height = shine * 2, Fill = new SolidColorBrush(Paper) }, px + pr * 0.35 - shine, py - pr * 0.4 - shine));
        eye.Children.Add(pupil);
        return eye;
    }

    /// <summary>
    /// Moves the pupils toward the pointer: the direction from the mascot, further the further
    /// away the pointer is (up to 400 px), eased so they glide rather than jump.
    /// </summary>
    private void Follow()
    {
        if (PresentationSource.FromVisual(this) is null || !GetCursorPos(out var cursor)) return;
        var here = PointToScreen(new Point(ActualWidth / 2, ActualHeight * 0.55));
        var (dx, dy) = (cursor.X - here.X, cursor.Y - here.Y);
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var reach = Math.Min(1, distance / 400) * 2.4; // viewBox units: about half the eye's free room
        var (tx, ty) = distance < 1 ? (0.0, 0.0) : (dx / distance * reach, dy / distance * reach * 0.8);
        _pupils.X += (tx - _pupils.X) * 0.2;
        _pupils.Y += (ty - _pupils.Y) * 0.2;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorPoint { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    private static UIElement Place(UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        return e;
    }

    /// <summary>Glance toward a direction, -1..1 on each axis (0, 0 is straight ahead).</summary>
    public void LookAt(double x, double y)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _look.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(Math.Clamp(x, -1, 1) * 2.2, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        _look.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(Math.Clamp(y, -1, 1) * 1.6, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    /// <summary>A small happy hop (for a found result, or a click).</summary>
    public void Hop()
    {
        var up = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        up.KeyFrames.Add(new EasingDoubleKeyFrame(0.9, KeyTime.FromPercent(0.18)));
        up.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromPercent(0.5), new CubicEase { EasingMode = EasingMode.EaseOut }));
        up.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new ElasticEase { Oscillations = 1, Springiness = 4 }));
        _breath.BeginAnimation(ScaleTransform.ScaleYProperty, up);
        _breath.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(420)) { From = 1.06 });
        if (Animated && IsVisible) Dispatcher.BeginInvoke(Breathe, DispatcherPriority.Background);
    }

    private void Sync()
    {
        if (Animated && IsLoaded && IsVisible && !SystemParameters.ClientAreaAnimation) Stop();
        else if (Animated && IsLoaded && IsVisible) Start();
        else Stop();
    }

    private void Start()
    {
        if (FollowCursor) _followTimer.Start();
        Breathe();
        _blinkTimer.Interval = TimeSpan.FromMilliseconds(Rng.Next(1200, 3200));
        _blinkTimer.Start();
    }

    private void Stop()
    {
        _blinkTimer.Stop();
        _followTimer.Stop();
        _breath.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _breath.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    /// <summary>A slow squash and stretch from the base, like the blob is breathing.</summary>
    private void Breathe()
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        _breath.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.025, TimeSpan.FromSeconds(1.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        _breath.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.99, TimeSpan.FromSeconds(1.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
    }

    /// <summary>One blink (sometimes two), then waits 2.5 to 6 seconds.</summary>
    private void Blink()
    {
        var blink = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(Rng.Next(4) == 0 ? 520 : 190) };
        blink.KeyFrames.Add(new EasingDoubleKeyFrame(0.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        blink.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(190))));
        if (blink.Duration.TimeSpan.TotalMilliseconds > 400)
        {
            blink.KeyFrames.Add(new EasingDoubleKeyFrame(0.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(330))));
            blink.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520))));
        }
        _blink.BeginAnimation(ScaleTransform.ScaleYProperty, blink);
        _blinkTimer.Interval = TimeSpan.FromMilliseconds(Rng.Next(2500, 6000));
    }
}
