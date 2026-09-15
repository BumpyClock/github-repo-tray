using GitHubTray.Core;
using GitHubTray_App.ViewModels;

namespace GitHubTray.App.Tests;

public sealed class ContributionSummaryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void LoadedSummaryUsesTheCompactPeriodLabel(int count)
    {
        var date = new DateOnly(2026, 9, 14);
        var day = new ContributionDay(date, count, count == 0 ? ContributionLevel.None : ContributionLevel.First);
        var calendar = new ContributionCalendar(count, [new ContributionWeek(date.AddDays(-1), [day])]);
        var model = new ContributionHeatmapViewModel();

        model.UpdateCalendar(calendar);

        Assert.Equal($"{count:N0} \u00b7 12 months", model.Summary);
    }

    [Fact]
    public void EmptyCalendarUsesTheSameCompactPeriodLabel()
    {
        var model = new ContributionHeatmapViewModel();
        model.UpdateCalendar(new ContributionCalendar(0, []));
        Assert.Equal("0 \u00b7 12 months", model.Summary);
    }
}
