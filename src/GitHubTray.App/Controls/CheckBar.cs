using GitHubTray_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace GitHubTray_App.Controls;

/// <summary>
/// Draws check outcomes as one bar of colored runs, so the proportion of failures to
/// passes is read rather than counted. Runs are ordered worst first by the view model.
/// Checks GitHub did not return are drawn as a dashed run, because absence must look
/// like absence and never like a pass.
/// </summary>
public sealed partial class CheckBar : Panel
{
    private const double CornerRadius = 2;
    private const double DashThickness = 1;

    private static readonly AccessibilitySettings Accessibility = new();

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<CheckBarSegment>), typeof(CheckBar),
        new PropertyMetadata(null, OnSegmentsChanged));

    public static readonly DependencyProperty IsStaleProperty = DependencyProperty.Register(
        nameof(IsStale), typeof(bool), typeof(CheckBar), new PropertyMetadata(false, OnStaleChanged));

    public IReadOnlyList<CheckBarSegment>? Segments
    {
        get => (IReadOnlyList<CheckBarSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    /// <summary>Results for an older commit are held back visually instead of presenting as settled.</summary>
    public bool IsStale
    {
        get => (bool)GetValue(IsStaleProperty);
        set => SetValue(IsStaleProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(availableSize);
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        return new Size(width, CheckBarLayout.BarHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var weights = Segments is null ? [] : Segments.Select(segment => segment.Weight).ToArray();
        var widths = CheckBarLayout.Distribute(weights, finalSize.Width);
        double x = 0;
        for (var index = 0; index < Children.Count && index < widths.Length; index++)
        {
            Children[index].Arrange(new Rect(x, 0, widths[index], finalSize.Height));
            x += widths[index] + CheckBarLayout.SegmentSpacing;
        }
        return finalSize;
    }

    private void Rebuild()
    {
        Children.Clear();
        if (Segments is null) return;
        foreach (var segment in Segments) Children.Add(CreateRun(segment));
        InvalidateMeasure();
    }

    private static Rectangle CreateRun(CheckBarSegment segment)
    {
        var run = new Rectangle { RadiusX = CornerRadius, RadiusY = CornerRadius };
        if (segment.IsLoaded) run.Fill = ThemeBrushes.Segment(segment.Tone);
        else
        {
            run.Stroke = ThemeBrushes.Get(ThemeBrushes.TrackKey);
            run.StrokeThickness = DashThickness;
            run.StrokeDashArray = [2, 2];
        }
        return run;
    }

    private void ApplyFreshness() =>
        // High contrast trades opacity for legibility, so staleness is left to the verdict there.
        Opacity = IsStale && !Accessibility.HighContrast ? 0.45 : 1;

    private static void OnSegmentsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((CheckBar)sender).Rebuild();

    private static void OnStaleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((CheckBar)sender).ApplyFreshness();
}
