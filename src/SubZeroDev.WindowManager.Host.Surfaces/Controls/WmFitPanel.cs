using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace SubZeroDev.WindowManager.Host.Surfaces.Controls;

/// <summary>
/// Lays children out in a row (or column) like a <see cref="StackPanel"/>, but when they do not fit the
/// length it is given, it shares that length out: children asking for less than an equal share keep
/// their size, and the rest split what is left. A running-windows strip with many windows shrinks its
/// buttons, as a taskbar does, instead of pushing the newest past the panel's edge.
/// </summary>
public sealed partial class WmFitPanel : Panel
{
    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(WmFitPanel),
            new PropertyMetadata(Orientation.Horizontal, (d, _) => ((WmFitPanel)d).InvalidateMeasure()));

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(WmFitPanel),
            new PropertyMetadata(0d, (d, _) => ((WmFitPanel)d).InvalidateMeasure()));

    private double[] _lengths = [];

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var horizontal = Orientation == Orientation.Horizontal;
        var visible = Children.Where(child => child.Visibility == Visibility.Visible).ToList();
        var open = horizontal ? new Size(double.PositiveInfinity, availableSize.Height) : new Size(availableSize.Width, double.PositiveInfinity);
        foreach (var child in visible)
        {
            child.Measure(open);
        }

        var wanted = visible.Select(child => Along(child.DesiredSize, horizontal)).ToArray();
        var gaps = Math.Max(0, visible.Count - 1) * Spacing;
        var room = (horizontal ? availableSize.Width : availableSize.Height) - gaps;
        _lengths = double.IsFinite(room) && wanted.Sum() > room ? Share(wanted, Math.Max(0, room)) : wanted;

        double along = 0;
        double across = 0;
        for (var i = 0; i < visible.Count; i++)
        {
            if (_lengths[i] < wanted[i])
            {
                visible[i].Measure(horizontal ? new Size(_lengths[i], availableSize.Height) : new Size(availableSize.Width, _lengths[i]));
            }

            along += _lengths[i];
            across = Math.Max(across, Across(visible[i].DesiredSize, horizontal));
        }

        along += gaps;
        return horizontal ? new Size(along, across) : new Size(across, along);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var horizontal = Orientation == Orientation.Horizontal;
        var visible = Children.Where(child => child.Visibility == Visibility.Visible).ToList();
        double offset = 0;
        for (var i = 0; i < visible.Count; i++)
        {
            var length = i < _lengths.Length ? _lengths[i] : Along(visible[i].DesiredSize, horizontal);
            visible[i].Arrange(horizontal
                ? new Rect(offset, 0, length, finalSize.Height)
                : new Rect(0, offset, finalSize.Width, length));
            offset += length + Spacing;
        }

        return finalSize;
    }

    /// <summary>Water-fills <paramref name="room"/>: the smallest wants are met first, the rest share equally.</summary>
    internal static double[] Share(IReadOnlyList<double> wanted, double room)
    {
        var lengths = new double[wanted.Count];
        var order = Enumerable.Range(0, wanted.Count).OrderBy(i => wanted[i]).ToList();
        var left = room;
        for (var k = 0; k < order.Count; k++)
        {
            var share = left / (order.Count - k);
            var length = Math.Min(wanted[order[k]], share);
            lengths[order[k]] = length;
            left -= length;
        }

        return lengths;
    }

    private static double Along(Size size, bool horizontal) => horizontal ? size.Width : size.Height;

    private static double Across(Size size, bool horizontal) => horizontal ? size.Height : size.Width;
}
