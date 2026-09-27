using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace Jevdexer.App;

/// <summary>
/// shadcn/ui's zinc palette as brushes (Themes/Controls.xaml reads them as DynamicResource). Mode is
/// "system", "light" or "dark" (settings `theme`); "system" follows Windows' app theme, live.
///
/// The brushes are created once and never frozen: a theme change animates each brush's colour, and
/// because every control holds the same brush instances, the whole UI crossfades together.
/// </summary>
internal static class Theme
{
    public const string System = "system", Light = "light", Dark = "dark";

    private static readonly Duration Crossfade = TimeSpan.FromMilliseconds(260);

    // shadcn zinc tokens (v3 hex values). Keys match shadcn's CSS variable names.
    private static readonly Dictionary<string, (string Light, string Dark)> Tokens = new()
    {
        ["Background"] = ("#FFFFFF", "#09090B"),
        ["Foreground"] = ("#09090B", "#FAFAFA"),
        ["Popover"] = ("#FFFFFF", "#09090B"),
        ["PopoverForeground"] = ("#09090B", "#FAFAFA"),
        ["Primary"] = ("#18181B", "#FAFAFA"),
        ["PrimaryForeground"] = ("#FAFAFA", "#18181B"),
        ["Muted"] = ("#F4F4F5", "#27272A"),
        ["MutedForeground"] = ("#71717A", "#A1A1AA"),
        ["Accent"] = ("#F4F4F5", "#27272A"),
        ["AccentForeground"] = ("#18181B", "#FAFAFA"),
        ["Border"] = ("#E4E4E7", "#27272A"),
        ["Input"] = ("#E4E4E7", "#27272A"),
        ["Ring"] = ("#A1A1AA", "#52525B"),
        ["Destructive"] = ("#DC2626", "#F87171"),
        ["Success"] = ("#16A34A", "#4ADE80"),
    };

    public static string Mode { get; private set; } = System;

    public static bool IsDark => Mode switch
    {
        Dark => true,
        Light => false,
        _ => !SystemAppsLight(),
    };

    /// <summary>Sets the mode. The first call creates the brushes; later calls crossfade them.</summary>
    public static void Apply(ResourceDictionary resources, string mode, bool animate = true)
    {
        Mode = mode is Light or Dark ? mode : System;
        var dark = IsDark;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        foreach (var (key, (light, darkHex)) in Tokens)
        {
            var target = (Color)ColorConverter.ConvertFromString(dark ? darkHex : light);
            if (resources.Contains(key) && resources[key] is SolidColorBrush { IsFrozen: false } brush)
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty,
                    animate ? new ColorAnimation(target, Crossfade) { EasingFunction = ease } : new ColorAnimation(target, TimeSpan.Zero));
            }
            else resources[key] = Unfreezable(target);
        }
    }

    /// <summary>
    /// WPF freezes Freezable resources (app- and window-level) when they are looked up, and a frozen
    /// brush cannot be animated. A Freezable with a binding on it can never be frozen, so each token
    /// gets a one-time binding for its starting colour.
    /// </summary>
    private static SolidColorBrush Unfreezable(Color color)
    {
        var brush = new SolidColorBrush();
        BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty, new Binding { Source = color, Mode = BindingMode.OneTime });
        return brush;
    }

    /// <summary>Re-applies when Windows' theme changes while in "system" mode.</summary>
    public static void FollowSystem(ResourceDictionary resources)
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Mode == System)
                Application.Current.Dispatcher.Invoke(() => Apply(resources, System));
        };
    }

    public static bool SystemAppsLight() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is not 0;

    /// <summary>The taskbar has its own setting; the tray icon contrasts with it.</summary>
    public static bool TaskbarLight() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is not 0;
}
