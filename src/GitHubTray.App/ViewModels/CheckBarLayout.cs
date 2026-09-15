namespace GitHubTray_App.ViewModels;

/// <summary>
/// One run of the check bar. A loaded run stands for outcomes GitHub actually
/// reported. An unloaded run stands for checks it did not return, and is the one
/// run presentation must never draw as a result.
/// </summary>
public sealed record CheckBarSegment(StatusTone Tone, int Weight, bool IsLoaded);

/// <summary>
/// Width distribution for the check bar. Kept free of WinUI so the proportions and
/// the minimum-width floor can be verified without a UI thread, in the same way
/// <see cref="ContributionViewport"/> separates heatmap geometry from its control.
/// </summary>
public static class CheckBarLayout
{
    public const double BarHeight = 4;
    public const double SegmentSpacing = 2;

    /// <summary>A run narrower than this reads as a gap rather than an outcome.</summary>
    public const double MinSegmentWidth = 3;

    /// <summary>
    /// Splits <paramref name="width"/> across <paramref name="weights"/>, holding every run at
    /// or above the floor so a single failure among hundreds of checks stays visible. Returned
    /// widths fall on whole pixels and sum to the content width, so runs never drift apart.
    /// </summary>
    public static double[] Distribute(IReadOnlyList<int> weights, double width,
        double spacing = SegmentSpacing, double minWidth = MinSegmentWidth)
    {
        var count = weights.Count;
        if (count == 0) return [];
        var content = width - spacing * (count - 1);
        if (!double.IsFinite(content) || content <= 0) return new double[count];

        // Too narrow to honor the floor: split evenly so every run keeps some width
        // instead of letting the largest ones crowd the rest out of existence.
        var floor = content < minWidth * count ? content / count : minWidth;
        return Round(Share(weights, content, floor), content);
    }

    private static double[] Share(IReadOnlyList<int> weights, double content, double floor)
    {
        var count = weights.Count;
        var exact = new double[count];
        var pinned = new bool[count];
        while (true)
        {
            var free = content;
            long freeWeight = 0;
            for (var index = 0; index < count; index++)
            {
                if (pinned[index]) free -= floor;
                else freeWeight += Math.Max(0, weights[index]);
            }

            var pinnedAny = false;
            for (var index = 0; index < count; index++)
            {
                if (pinned[index]) continue;
                if (Portion(weights[index], free, freeWeight) >= floor) continue;
                pinned[index] = true;
                pinnedAny = true;
            }

            // Each pass pins at least one run, so this settles within `count` passes.
            if (pinnedAny) continue;
            for (var index = 0; index < count; index++)
                exact[index] = pinned[index] ? floor : Portion(weights[index], free, freeWeight);
            return exact;
        }
    }

    private static double Portion(int weight, double free, long freeWeight) =>
        freeWeight <= 0 ? 0 : free * Math.Max(0, weight) / freeWeight;

    /// <summary>Rounds shared edges rather than widths, so runs stay flush at whole pixels.</summary>
    private static double[] Round(double[] exact, double content)
    {
        var widths = new double[exact.Length];
        double cursor = 0, drawn = 0;
        for (var index = 0; index < exact.Length; index++)
        {
            cursor = Math.Min(cursor + exact[index], content);
            var edge = Math.Round(cursor, MidpointRounding.AwayFromZero);
            widths[index] = Math.Max(0, edge - drawn);
            drawn = edge;
        }
        return widths;
    }
}
