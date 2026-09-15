using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace GitHubTray_App.Controls;

/// <summary>
/// Places children left to right and starts a new line when the current one is full.
/// Label chips need this so a PR keeps showing its labels instead of clipping them
/// to a single row; WinUI has no built-in wrapping panel for a plain ItemsControl.
/// </summary>
public sealed class FlowPanel : Panel
{
    public static readonly DependencyProperty ItemSpacingProperty = DependencyProperty.Register(
        nameof(ItemSpacing), typeof(double), typeof(FlowPanel), new PropertyMetadata(6d, OnSpacingChanged));

    public static readonly DependencyProperty LineSpacingProperty = DependencyProperty.Register(
        nameof(LineSpacing), typeof(double), typeof(FlowPanel), new PropertyMetadata(4d, OnSpacingChanged));

    public double ItemSpacing
    {
        get => (double)GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    public double LineSpacing
    {
        get => (double)GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var limit = availableSize.Width;
        double widest = 0, height = 0, lineWidth = 0, lineHeight = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(limit, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (StartsNewLine(lineWidth, size.Width, limit))
            {
                widest = Math.Max(widest, lineWidth);
                height += lineHeight + LineSpacing;
                lineWidth = 0;
                lineHeight = 0;
            }
            lineWidth += (lineWidth > 0 ? ItemSpacing : 0) + size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        widest = Math.Max(widest, lineWidth);
        return new Size(double.IsInfinity(limit) ? widest : Math.Min(widest, limit), height + lineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (StartsNewLine(x, size.Width, finalSize.Width))
            {
                x = 0;
                y += lineHeight + LineSpacing;
                lineHeight = 0;
            }
            if (x > 0) x += ItemSpacing;
            // A chip wider than the line keeps its measured width and trims its own text.
            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        return new Size(finalSize.Width, y + lineHeight);
    }

    private bool StartsNewLine(double lineWidth, double childWidth, double limit) =>
        lineWidth > 0 && !double.IsInfinity(limit) && lineWidth + ItemSpacing + childWidth > limit;

    private static void OnSpacingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((FlowPanel)sender).InvalidateMeasure();
}
