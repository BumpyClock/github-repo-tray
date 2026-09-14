using System.Globalization;
using GitHubTray.Core;
using GitHubTray_App.ViewModels;

namespace GitHubTray.App.Tests;

public sealed class ContributionSelectionTests
{
    [Fact]
    public void InitialCalendarSelectsLatestRealDayAndUsesDateBasedGeometry()
    {
        var vm = new ContributionHeatmapViewModel();
        var observations = ObserveDescriptions(vm);
        var latest = Day(2024, 3, 11, 47);
        // Deliberately unordered, sparse, and starting midweek across leap day.
        var calendar = Calendar(latest, Day(2024, 2, 29, 1),
            Day(2024, 3, 3, 307), Day(2024, 2, 28, 11));

        var change = vm.UpdateCalendar(calendar);

        Assert.Null(change.Previous);
        Assert.Equal("", change.PreviousDescription);
        Assert.True(change.ValueChanged);
        var current = AssertSelection(vm, change, 3, latest);
        Assert.Equal(3, vm.WeekCount);
        Assert.Collection(vm.Days,
            day => Assert.Equal(new ContributionPlotDay(Day(2024, 2, 28, 11), 0, 3), day),
            day => Assert.Equal(new ContributionPlotDay(Day(2024, 2, 29, 1), 0, 4), day),
            day => Assert.Equal(new ContributionPlotDay(Day(2024, 3, 3, 307), 1, 0), day),
            day => Assert.Equal(new ContributionPlotDay(latest, 2, 1), day));
        AssertDescriptionNotification(observations, 3, current);
    }

    [Fact]
    public void RefreshPreservesSelectedDateWhenItsIndexAndColumnChangeWithoutNotifyingDescription()
    {
        var vm = new ContributionHeatmapViewModel();
        vm.UpdateCalendar(Calendar(ContinuousDays()));
        var selected = vm.SelectIndex(11);
        var observations = ObserveDescriptions(vm);

        // The first Sunday shifts from February 25 to March 3.
        var change = vm.UpdateCalendar(Calendar(ContinuousDays().Skip(7).ToArray()));

        Assert.Equal(selected.Current, change.Previous);
        Assert.Equal(2, change.Previous!.PlotDay.WeekIndex);
        var current = AssertSelection(vm, change, 4, Day(2024, 3, 10, 1_207));
        Assert.Equal(1, current.PlotDay.WeekIndex);
        Assert.Equal(0, current.PlotDay.DayIndex);
        Assert.Equal(selected.CurrentDescription, change.CurrentDescription);
        Assert.False(change.ValueChanged);
        Assert.Empty(observations);
    }

    [Fact]
    public void ChangedCountOnSameDatePublishesOneDescriptionWithFinalIndexDateAndCount()
    {
        var vm = new ContributionHeatmapViewModel();
        vm.UpdateCalendar(Calendar(ContinuousDays()));
        var selected = vm.SelectIndex(11);
        var observations = ObserveDescriptions(vm);
        var refreshed = ContinuousDays().Skip(7)
            .Select(day => day.Date == new DateOnly(2024, 3, 10)
                ? day with { Count = 9_001 }
                : day)
            .ToArray();

        var change = vm.UpdateCalendar(Calendar(refreshed));

        Assert.Equal(selected.Current, change.Previous);
        Assert.Equal(selected.CurrentDescription, change.PreviousDescription);
        var current = AssertSelection(vm, change, 4, Day(2024, 3, 10, 9_001));
        Assert.Equal(1, current.PlotDay.WeekIndex);
        Assert.True(change.ValueChanged);
        Assert.NotEqual(change.PreviousDescription, change.CurrentDescription);
        AssertDescriptionNotification(observations, 4, current);
    }

    [Fact]
    public void UnchangedRefreshKeepsNonLatestSelectionAndNeverPublishesTransientEmptyDescription()
    {
        var vm = new ContributionHeatmapViewModel();
        vm.UpdateCalendar(Calendar(ContinuousDays()));
        var selected = vm.SelectIndex(5);
        var observations = ObserveDescriptions(vm);

        // New calendar/day instances, not an identity-based short circuit.
        var change = vm.UpdateCalendar(Calendar(ContinuousDays()));

        Assert.Equal(selected.Current, change.Previous);
        AssertSelection(vm, change, 5, Day(2024, 3, 4, 17));
        Assert.Equal(change.Previous, change.Current);
        Assert.False(change.ValueChanged);
        Assert.Empty(observations);
    }

    [Fact]
    public void MissingSelectedDateFallsBackToLatestReturnedDay()
    {
        var vm = new ContributionHeatmapViewModel();
        vm.UpdateCalendar(Calendar(ContinuousDays()));
        var selected = vm.SelectIndex(11);
        var observations = ObserveDescriptions(vm);
        var latest = Day(2024, 3, 13, 71);

        var change = vm.UpdateCalendar(Calendar(Day(2024, 3, 9, 6), latest));

        Assert.Equal(selected.Current, change.Previous);
        var current = AssertSelection(vm, change, 1, latest);
        Assert.Equal(1, current.PlotDay.WeekIndex);
        Assert.Equal(3, current.PlotDay.DayIndex);
        Assert.True(change.ValueChanged);
        AssertDescriptionNotification(observations, 1, current);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NullOrEmptyCalendarClearsSelectionOnceAndRepeatedNullIsUnchanged(bool isNull)
    {
        var vm = new ContributionHeatmapViewModel();
        var selected = vm.UpdateCalendar(Calendar(ContinuousDays()));
        var observations = ObserveDescriptions(vm);

        var change = vm.UpdateCalendar(isNull ? null : new ContributionCalendar(0, []));

        Assert.Equal(selected.Current, change.Previous);
        Assert.Equal(selected.CurrentDescription, change.PreviousDescription);
        Assert.Null(change.Current);
        Assert.Equal("", change.CurrentDescription);
        Assert.True(change.ValueChanged);
        AssertEmptySelection(vm);
        AssertDescriptionNotification(observations, -1, null);

        observations.Clear();
        AssertEmptyChange(vm.UpdateCalendar(null));
        AssertEmptyChange(vm.UpdateCalendar(null));
        AssertEmptySelection(vm);
        Assert.Empty(observations);
    }

    [Fact]
    public void SelectionRequestsWithoutDailyDataAreNoOps()
    {
        var vm = new ContributionHeatmapViewModel();
        var observations = ObserveDescriptions(vm);

        AssertEmptyChange(vm.UpdateCalendar(null));
        AssertEmptyChange(vm.UpdateCalendar(new ContributionCalendar(0,
            [new ContributionWeek(new DateOnly(2024, 2, 25), [])])));
        AssertEmptyChange(vm.SelectIndex(int.MinValue));
        AssertEmptyChange(vm.SelectIndex(0));
        AssertEmptyChange(vm.SelectIndex(int.MaxValue));
        AssertEmptyChange(vm.MoveSelection(-7));
        AssertEmptyChange(vm.MoveSelection(-1));
        AssertEmptyChange(vm.MoveSelection(0));
        AssertEmptyChange(vm.MoveSelection(1));
        AssertEmptyChange(vm.MoveSelection(7));

        AssertEmptySelection(vm);
        Assert.Empty(observations);
    }

    [Theory]
    [InlineData(8, -1, 7)]
    [InlineData(8, 1, 9)]
    [InlineData(8, -7, 1)]
    [InlineData(8, 7, 15)]
    [InlineData(0, -1, 0)]
    [InlineData(0, -7, 0)]
    [InlineData(19, 1, 19)]
    [InlineData(19, 7, 19)]
    [InlineData(3, -7, 0)]
    [InlineData(16, 7, 19)]
    [InlineData(8, 0, 8)]
    public void DayAndWeekMovesClampAndNotifyOnlyForChangedSelection(
        int startIndex, int offset, int expectedIndex)
    {
        var days = ContinuousDays();
        var vm = new ContributionHeatmapViewModel();
        vm.UpdateCalendar(Calendar(days));
        vm.SelectIndex(startIndex);
        var before = new ContributionSelectionSnapshot(vm.SelectedDay!, vm.SelectedDayDescription);
        var observations = ObserveDescriptions(vm);

        var change = vm.MoveSelection(offset);

        Assert.Equal(before, change.Previous);
        var current = AssertSelection(vm, change, expectedIndex, days[expectedIndex]);
        Assert.Equal(startIndex != expectedIndex, change.ValueChanged);
        if (startIndex == expectedIndex)
        {
            Assert.Equal(change.Previous, change.Current);
            Assert.Empty(observations);
        }
        else
        {
            AssertDescriptionNotification(observations, expectedIndex, current);
        }
    }

    [Theory]
    [InlineData(8, 0, 0)] // Home
    [InlineData(8, 19, 19)] // End
    [InlineData(8, int.MinValue, 0)]
    [InlineData(8, int.MaxValue, 19)]
    [InlineData(8, -1, 0)]
    [InlineData(8, 20, 19)]
    [InlineData(0, int.MinValue, 0)]
    [InlineData(19, int.MaxValue, 19)]
    [InlineData(8, 8, 8)]
    public void IndexSelectionSupportsHomeEndClampingAndRepeatedSelection(
        int startIndex, int requestedIndex, int expectedIndex)
    {
        var days = ContinuousDays();
        var vm = new ContributionHeatmapViewModel();
        vm.UpdateCalendar(Calendar(days));
        vm.SelectIndex(startIndex);
        var before = new ContributionSelectionSnapshot(vm.SelectedDay!, vm.SelectedDayDescription);
        var observations = ObserveDescriptions(vm);

        var change = vm.SelectIndex(requestedIndex);

        Assert.Equal(before, change.Previous);
        var current = AssertSelection(vm, change, expectedIndex, days[expectedIndex]);
        Assert.Equal(startIndex != expectedIndex, change.ValueChanged);
        if (startIndex == expectedIndex)
        {
            Assert.Equal(change.Previous, change.Current);
            Assert.Empty(observations);
        }
        else
        {
            AssertDescriptionNotification(observations, expectedIndex, current);
        }
    }

    [Theory]
    [InlineData(1, 1, 2)] // March 2 is absent: move forward to March 8.
    [InlineData(2, -1, 1)] // March 7 is absent: move backward to March 1.
    [InlineData(1, 7, 2)] // Exact date a week ahead.
    [InlineData(2, -7, 1)] // Exact date a week behind.
    [InlineData(2, 7, 4)] // March 11 is before the target: continue to March 22.
    [InlineData(3, -7, 1)] // March 8 is after the target: continue to March 1.
    [InlineData(0, -7, 0)]
    [InlineData(4, 7, 4)]
    public void SparseCalendarMovesToFirstRealDayAtOrBeyondTargetInRequestedDirection(
        int startIndex, int offset, int expectedIndex)
    {
        var days = new[]
        {
            Day(2024, 2, 28, 0),
            Day(2024, 3, 1, 11),
            Day(2024, 3, 8, 1),
            Day(2024, 3, 11, 99),
            Day(2024, 3, 22, 4)
        };
        var vm = new ContributionHeatmapViewModel();
        vm.UpdateCalendar(Calendar(days));
        vm.SelectIndex(startIndex);
        var before = new ContributionSelectionSnapshot(vm.SelectedDay!, vm.SelectedDayDescription);
        var observations = ObserveDescriptions(vm);

        var change = vm.MoveSelection(offset);

        Assert.Equal(before, change.Previous);
        var current = AssertSelection(vm, change, expectedIndex, days[expectedIndex]);
        Assert.Equal(days.Length, vm.Days.Count);
        Assert.Equal(startIndex != expectedIndex, change.ValueChanged);
        if (startIndex == expectedIndex)
        {
            Assert.Empty(observations);
        }
        else
        {
            AssertDescriptionNotification(observations, expectedIndex, current);
        }
    }

    [Fact]
    public void ReturnedSnapshotsRemainUnchangedAfterRefreshMovementAndClear()
    {
        var vm = new ContributionHeatmapViewModel();
        vm.UpdateCalendar(Calendar(ContinuousDays()));
        var retained = vm.SelectIndex(11);
        var previousDescription = retained.PreviousDescription;
        var currentDescription = retained.CurrentDescription;

        vm.UpdateCalendar(Calendar(ContinuousDays().Skip(7)
            .Select(day => day.Date == new DateOnly(2024, 3, 10)
                ? day with { Count = 9_001 }
                : day).ToArray()));
        vm.MoveSelection(7);
        vm.UpdateCalendar(null);

        Assert.Equal(new ContributionPlotDay(Day(2024, 3, 18, 13), 3, 1),
            retained.Previous!.PlotDay);
        Assert.Equal(new ContributionPlotDay(Day(2024, 3, 10, 1_207), 2, 0),
            retained.Current!.PlotDay);
        Assert.Equal(previousDescription, retained.Previous.Description);
        Assert.Equal(currentDescription, retained.Current.Description);
        Assert.Equal(previousDescription, retained.PreviousDescription);
        Assert.Equal(currentDescription, retained.CurrentDescription);
        Assert.True(retained.ValueChanged);
        AssertEmptySelection(vm);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, "", false)]
    [InlineData("", null, false)]
    [InlineData("", "", false)]
    [InlineData("same", "same", false)]
    [InlineData(null, "value", true)]
    [InlineData("value", null, true)]
    [InlineData("value", "VALUE", true)]
    [InlineData("\u00e9", "e\u0301", true)]
    public void ChangeUsesOrdinalDescriptionsAndMapsAbsentSnapshotsToEmpty(
        string? previousDescription, string? currentDescription, bool isValueChanged)
    {
        var plotDay = new ContributionPlotDay(Day(2024, 2, 29, 11), 0, 4);
        var change = new ContributionSelectionChange(
            previousDescription is null ? null : new ContributionSelectionSnapshot(plotDay, previousDescription),
            currentDescription is null ? null : new ContributionSelectionSnapshot(plotDay, currentDescription));

        Assert.Equal(previousDescription ?? "", change.PreviousDescription);
        Assert.Equal(currentDescription ?? "", change.CurrentDescription);
        Assert.Equal(isValueChanged, change.ValueChanged);
    }

    private static ContributionDay Day(int year, int month, int day, int count) =>
        new(new DateOnly(year, month, day), count,
            count == 0 ? ContributionLevel.None : ContributionLevel.First);

    private static ContributionDay[] ContinuousDays()
    {
        int[] counts = [0, 11, 1, 99, 4, 17, 2, 301, 8, 53, 6, 1_207, 3, 71, 19, 5, 211, 23, 47, 13];
        var first = new DateOnly(2024, 2, 28);
        return counts.Select((count, index) =>
            new ContributionDay(first.AddDays(index), count,
                count == 0 ? ContributionLevel.None : ContributionLevel.First)).ToArray();
    }

    private static ContributionCalendar Calendar(params ContributionDay[] days) =>
        new(days.Sum(day => day.Count),
            days.GroupBy(day => day.Date.AddDays(-(int)day.Date.DayOfWeek))
                .Select(week => new ContributionWeek(week.Key, week.ToArray())).ToArray());

    private static ContributionSelectionSnapshot AssertSelection(
        ContributionHeatmapViewModel vm, ContributionSelectionChange change,
        int expectedIndex, ContributionDay expectedDay)
    {
        var current = Assert.IsType<ContributionSelectionSnapshot>(change.Current);
        Assert.Equal(expectedIndex, vm.SelectedIndex);
        Assert.Equal(expectedDay, current.PlotDay.Day);
        Assert.Equal(current.PlotDay, vm.SelectedDay);
        Assert.Equal(current.Description, vm.SelectedDayDescription);
        Assert.Equal(current.Description, change.CurrentDescription);
        // Check the selected date/count without fixing localized weekday/month names or English prose.
        Assert.Contains(expectedDay.Date.ToString("ddd, d MMM yyyy", CultureInfo.CurrentCulture),
            current.Description, StringComparison.Ordinal);
        Assert.Contains($" · {expectedDay.Count.ToString("N0", CultureInfo.CurrentCulture)} ",
            current.Description, StringComparison.Ordinal);
        return current;
    }

    private static List<(int Index, ContributionPlotDay? PlotDay, string Description)> ObserveDescriptions(
        ContributionHeatmapViewModel vm)
    {
        var observations = new List<(int Index, ContributionPlotDay? PlotDay, string Description)>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ContributionHeatmapViewModel.SelectedDayDescription))
            {
                observations.Add((vm.SelectedIndex, vm.SelectedDay, vm.SelectedDayDescription));
            }
        };
        return observations;
    }

    private static void AssertDescriptionNotification(
        List<(int Index, ContributionPlotDay? PlotDay, string Description)> observations,
        int expectedIndex, ContributionSelectionSnapshot? expected)
    {
        var observation = Assert.Single(observations);
        Assert.Equal(expectedIndex, observation.Index);
        Assert.Equal(expected?.PlotDay, observation.PlotDay);
        Assert.Equal(expected?.Description ?? "", observation.Description);
    }

    private static void AssertEmptyChange(ContributionSelectionChange change)
    {
        Assert.Null(change.Previous);
        Assert.Null(change.Current);
        Assert.Equal("", change.PreviousDescription);
        Assert.Equal("", change.CurrentDescription);
        Assert.False(change.ValueChanged);
    }

    private static void AssertEmptySelection(ContributionHeatmapViewModel vm)
    {
        Assert.Equal(-1, vm.SelectedIndex);
        Assert.Null(vm.SelectedDay);
        Assert.Equal("", vm.SelectedDayDescription);
        Assert.Empty(vm.Days);
        Assert.Equal(0, vm.WeekCount);
    }
}
