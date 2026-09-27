using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Jevdexer.App;

/// <summary>
/// The app's motion, in one place so every surface moves the same way. Short (under 200 ms) and
/// eased out: things arrive quickly and settle.
/// </summary>
internal static class Motion
{
    public static readonly Duration Enter = TimeSpan.FromMilliseconds(160);
    public static readonly Duration Exit = TimeSpan.FromMilliseconds(110);
    public static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Fade in, rising <paramref name="rise"/> px and growing from <paramref name="scale"/>.</summary>
    public static void In(FrameworkElement element, double rise = 6, double scale = 0.985)
    {
        var (translate, zoom) = Transforms(element);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Enter) { EasingFunction = EaseOut });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rise, 0, Enter) { EasingFunction = EaseOut });
        zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, 1, Enter) { EasingFunction = EaseOut });
        zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, 1, Enter) { EasingFunction = EaseOut });
    }

    /// <summary>Fade out, then run <paramref name="done"/> (hide or close the window).</summary>
    public static void Out(FrameworkElement element, Action done, double scale = 0.985)
    {
        var (_, zoom) = Transforms(element);
        var fade = new DoubleAnimation(element.Opacity, 0, Exit) { EasingFunction = EaseOut };
        fade.Completed += (_, _) => done();
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, Exit));
        zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, Exit));
    }

    /// <summary>Animates an element's opacity; used for hover and selection fills.</summary>
    public static void Fade(UIElement element, double to, double ms = 120) =>
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });

    private static (TranslateTransform, ScaleTransform) Transforms(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup { Children: [TranslateTransform t, ScaleTransform s] }) return (t, s);
        var translate = new TranslateTransform();
        var zoom = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = new TransformGroup { Children = { translate, zoom } };
        return (translate, zoom);
    }
}
