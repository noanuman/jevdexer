using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Jevdexer.App;

/// <summary>
/// A Lucide icon: a 24x24 stroked geometry (Themes/Icons.xaml) scaled to Size, so the stroke scales
/// with it the way it does on the web (2px at 24, 1.33px at 16). Colour inherits Foreground like text.
/// </summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(Icon), new PropertyMetadata(null));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(Icon),
            new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(typeof(Icon),
            new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>Rotates continuously (one turn per second) while true: loading and re-indexing.</summary>
    public static readonly DependencyProperty SpinProperty =
        DependencyProperty.Register(nameof(Spin), typeof(bool), typeof(Icon), new PropertyMetadata(false, OnSpinChanged));

    public bool Spin { get => (bool)GetValue(SpinProperty); set => SetValue(SpinProperty, value); }

    private readonly RotateTransform _rotation = new();

    private static void OnSpinChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var icon = (Icon)d;
        if ((bool)e.NewValue)
        {
            icon._rotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            // Finish the turn instead of snapping back.
            var from = icon._rotation.Angle % 360;
            icon._rotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(from, 360, TimeSpan.FromMilliseconds((360 - from) / 360 * 1000)) { FillBehavior = FillBehavior.Stop });
        }
    }

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private readonly Viewbox _box;

    public Icon()
    {
        var path = new Path
        {
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
        path.SetBinding(Path.DataProperty, new System.Windows.Data.Binding(nameof(Data)) { Source = this });

        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        _box = new Viewbox { Child = canvas, Stretch = Stretch.Uniform, RenderTransform = _rotation, RenderTransformOrigin = new Point(0.5, 0.5) };
        AddVisualChild(_box);
        AddLogicalChild(_box);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _box;

    protected override Size MeasureOverride(Size constraint)
    {
        _box.Measure(new Size(Size, Size));
        return new Size(Size, Size);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _box.Arrange(new Rect(0, 0, Size, Size));
        return new Size(Size, Size);
    }
}
