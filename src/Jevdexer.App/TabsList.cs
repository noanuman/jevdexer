using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Jevdexer.App;

/// <summary>
/// A tabs list with a sliding indicator: the selected tab's white pill glides to the newly
/// checked RadioButton instead of jumping. Children are RadioButtons styled with `Tab`.
/// </summary>
[ContentProperty(nameof(Items))]
public sealed class TabsList : Border
{
    private static readonly Duration Slide = TimeSpan.FromMilliseconds(220);

    private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal };
    private readonly Border _indicator = new()
    {
        CornerRadius = new CornerRadius(6),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Stretch,
        Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Direction = 270, Opacity = 0.1 },
        Opacity = 0,
    };
    private readonly TranslateTransform _x = new();

    /// <summary>The tabs. Each one moves into the internal panel as XAML adds it.</summary>
    public ObservableCollection<UIElement> Items { get; } = [];

    public TabsList()
    {
        SetResourceReference(BackgroundProperty, "Muted");
        _indicator.SetResourceReference(BackgroundProperty, "Background");
        _indicator.RenderTransform = _x;
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(3);
        HorizontalAlignment = HorizontalAlignment.Left;

        var grid = new Grid();
        grid.Children.Add(_indicator);
        grid.Children.Add(_panel);
        Child = grid;

        Items.CollectionChanged += (_, e) =>
        {
            foreach (UIElement item in e.NewItems ?? Array.Empty<UIElement>()) _panel.Children.Add(item);
        };

        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is RadioButton r) MoveTo(r, animate: true);
        }));
        Loaded += (_, _) => MoveTo(Checked(), animate: false);
        SizeChanged += (_, _) => MoveTo(Checked(), animate: false);
    }

    private RadioButton? Checked() => _panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);

    private void MoveTo(RadioButton? tab, bool animate)
    {
        if (tab is null || !IsLoaded || tab.ActualWidth == 0) return;
        var x = tab.TransformToAncestor(_panel).Transform(new Point(0, 0)).X;
        var ease = Motion.EaseOut;
        if (animate && _indicator.Opacity > 0)
        {
            _x.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, Slide) { EasingFunction = ease });
            _indicator.BeginAnimation(WidthProperty, new DoubleAnimation(tab.ActualWidth, Slide) { EasingFunction = ease });
        }
        else
        {
            _x.BeginAnimation(TranslateTransform.XProperty, null);
            _indicator.BeginAnimation(WidthProperty, null);
            _x.X = x;
            _indicator.Width = tab.ActualWidth;
            _indicator.Opacity = 1;
        }
    }
}
