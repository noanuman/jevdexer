using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Jevdexer.App;

/// <summary>
/// The Settings sidebar: a vertical list of RadioButtons (style `NavItem`) whose highlight glides
/// to the newly selected page, like <see cref="TabsList"/> does sideways.
/// </summary>
[ContentProperty(nameof(Items))]
public sealed class NavList : Grid
{
    private static readonly Duration Slide = TimeSpan.FromMilliseconds(220);

    private readonly StackPanel _panel = new();
    private readonly Border _indicator = new()
    {
        CornerRadius = new CornerRadius(6),
        VerticalAlignment = VerticalAlignment.Top,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Opacity = 0,
    };
    private readonly TranslateTransform _y = new();

    public ObservableCollection<UIElement> Items { get; } = [];

    public NavList()
    {
        _indicator.SetResourceReference(Border.BackgroundProperty, "Accent");
        _indicator.RenderTransform = _y;
        Children.Add(_indicator);
        Children.Add(_panel);

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

    private void MoveTo(RadioButton? item, bool animate)
    {
        if (item is null || !IsLoaded || item.ActualHeight == 0) return;
        var y = item.TransformToAncestor(_panel).Transform(new Point(0, 0)).Y;
        if (animate && _indicator.Opacity > 0)
        {
            _y.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, Slide) { EasingFunction = Motion.EaseOut });
            _indicator.BeginAnimation(HeightProperty, new DoubleAnimation(item.ActualHeight, Slide) { EasingFunction = Motion.EaseOut });
        }
        else
        {
            _y.BeginAnimation(TranslateTransform.YProperty, null);
            _indicator.BeginAnimation(HeightProperty, null);
            _y.Y = y;
            _indicator.Height = item.ActualHeight;
            _indicator.Opacity = 1;
        }
    }
}
