using GitHubTray.Core;

namespace GitHubTray_App.ViewModels;

internal readonly record struct ContributionViewportAnchor(bool IsAtPresent, double RightEdgeWeek);
internal readonly record struct ContributionCellLayout(
    double CellSize, double WeekPitch, double RowPitch, double LeftInset, double TopInset, double StrokeThickness)
{
    public double PlotHeight => 7 * RowPitch + ContributionViewport.TopInset + ContributionViewport.ScrollbarSpace;
}

internal static class ContributionViewport
{
    public const double PlotHeight = 154;
    public const double ScrollbarSpace = 12;
    public const double TopInset = 8;

    public static ContributionCellLayout GetCellLayout(
        double rasterizationScale, double viewportWidth, int weeks, ContributionCellSizePreset preset)
    {
        var scale = rasterizationScale;
        var rowPixels = Math.Floor((PlotHeight - TopInset - ScrollbarSpace) * scale / 7);
        var gapPixels = Math.Max(2, Math.Round(2 * scale));
        var maximumCellPixels = Math.Max(1, rowPixels - gapPixels);
        var mediumCellPixels = Math.Max(1, viewportWidth * scale / Math.Max(1, weeks) - gapPixels);
        var baseCellPixels = preset switch
        {
            ContributionCellSizePreset.Small => mediumCellPixels * 0.75,
            ContributionCellSizePreset.Medium => mediumCellPixels,
            ContributionCellSizePreset.Large => Math.Max(16 * scale, mediumCellPixels * 2),
            _ => throw new ArgumentOutOfRangeException(nameof(preset))
        };
        var cellPixels = Math.Clamp(baseCellPixels, 1, maximumCellPixels);
        var pitchPixels = cellPixels + gapPixels;
        var actualRowPixels = Math.Ceiling(pitchPixels);
        return new(cellPixels / scale, pitchPixels / scale, actualRowPixels / scale,
            Math.Floor(gapPixels / 2) / scale, Math.Floor((actualRowPixels - cellPixels) / 2) / scale, 1 / scale);
    }

    public static ContributionCellLayout GetWeekCell(ContributionCellLayout layout, int week, double scale)
    {
        var columnPixels = Math.Round((week + 1) * layout.WeekPitch * scale) - Math.Round(week * layout.WeekPitch * scale);
        var gapPixels = Math.Round((layout.WeekPitch - layout.CellSize) * scale);
        var cellPixels = Math.Max(1, columnPixels - gapPixels);
        return new(cellPixels / scale, columnPixels / scale, layout.RowPitch, Math.Floor(gapPixels / 2) / scale,
            Math.Floor((layout.RowPitch * scale - cellPixels) / 2) / scale, 1 / scale);
    }

    public static ContributionViewportAnchor Capture(double offset, double viewportWidth, double weekPitch, int weeks)
    {
        var extent = weeks * weekPitch;
        return new(
            viewportWidth <= 0 || extent - viewportWidth - offset <= 1,
            (offset + viewportWidth) / weekPitch);
    }

    public static double Restore(ContributionViewportAnchor anchor, double viewportWidth, double weekPitch, int weeks)
    {
        var maximum = Math.Max(0, weeks * weekPitch - viewportWidth);
        return anchor.IsAtPresent ? maximum
            : Math.Clamp(anchor.RightEdgeWeek * weekPitch - viewportWidth, 0, maximum);
    }

    public static double RevealWeek(int week, double offset, double viewportWidth, double weekPitch, int weeks)
    {
        var left = week * weekPitch;
        var right = left + weekPitch;
        var target = left < offset ? left : right > offset + viewportWidth ? right - viewportWidth : offset;
        return Math.Clamp(target, 0, Math.Max(0, weeks * weekPitch - viewportWidth));
    }
}
