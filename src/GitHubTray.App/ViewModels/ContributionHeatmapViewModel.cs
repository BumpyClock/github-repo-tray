using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GitHubTray.Core;

namespace GitHubTray_App.ViewModels;

public sealed record ContributionPlotDay(ContributionDay Day, int WeekIndex, int DayIndex)
{
    public string Description => $"{Day.Count:N0} {(Day.Count == 1 ? "contribution" : "contributions")} on {Day.Date.ToString("D", CultureInfo.CurrentCulture)}";
    public string AutomationId => $"ContributionDay_{Day.Date:yyyy-MM-dd}";
}

public sealed record ContributionSelectionSnapshot(ContributionPlotDay PlotDay, string Description);

public readonly record struct ContributionSelectionChange(
    ContributionSelectionSnapshot? Previous,
    ContributionSelectionSnapshot? Current)
{
    public string PreviousDescription => Previous?.Description ?? "";
    public string CurrentDescription => Current?.Description ?? "";
    public bool ValueChanged => !string.Equals(PreviousDescription, CurrentDescription, StringComparison.Ordinal);
}

/// <summary>Calendar geometry uses actual dates; missing slots never become zero-count days.</summary>
public sealed partial class ContributionHeatmapViewModel : ObservableObject
{
    private ContributionSelectionSnapshot? _selection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDays))]
    public partial IReadOnlyList<ContributionPlotDay> Days { get; set; } = [];

    [ObservableProperty]
    public partial string Summary { get; set; } = "Not loaded";

    [ObservableProperty]
    public partial string AccessibleSummary { get; set; } = "Contributions. No calendar is displayed.";

    public int SelectedIndex { get; private set; } = -1;
    public bool HasDays => Days.Count > 0;
    public ContributionPlotDay? SelectedDay => _selection?.PlotDay;
    public string SelectedDayDescription => _selection?.Description ?? "";

    public ContributionSelectionChange UpdateCalendar(ContributionCalendar? calendar)
    {
        var previous = _selection;
        var previousDate = previous?.PlotDay.Day.Date;
        var days = calendar?.Weeks.SelectMany(week => week.Days).OrderBy(day => day.Date).ToArray() ?? [];

        if (days.Length == 0)
        {
            Days = [];
            Summary = calendar is null ? "Not loaded" : $"{calendar.TotalContributions:N0} · 12 months";
            AccessibleSummary = calendar is null
                ? "Contributions. No calendar is displayed."
                : $"{calendar.TotalContributions:N0} contributions. No daily data was returned.";
            return SetSelection(-1, previous);
        }

        var first = days[0].Date;
        var last = days[^1].Date;
        var firstSunday = first.AddDays(-(int)first.DayOfWeek);
        Days = days.Select(day => new ContributionPlotDay(
            day,
            (day.Date.DayNumber - firstSunday.DayNumber) / 7,
            (int)day.Date.DayOfWeek)).ToArray();
        Summary = $"{calendar!.TotalContributions:N0} · 12 months";
        var rangeDescription = $"{first.ToString("D", CultureInfo.CurrentCulture)} through {last.ToString("D", CultureInfo.CurrentCulture)}";
        AccessibleSummary = $"{calendar.TotalContributions:N0} contributions in the last 12 months. {rangeDescription}. Sunday-first calendar.";
        var selectedIndex = Array.FindIndex(days, day => day.Date == previousDate);
        return SetSelection(selectedIndex >= 0 ? selectedIndex : days.Length - 1, previous);
    }

    public ContributionSelectionChange SelectIndex(int index)
    {
        if (!HasDays)
        {
            return new(_selection, _selection);
        }

        return SetSelection(Math.Clamp(index, 0, Days.Count - 1), _selection);
    }

    public ContributionSelectionChange MoveSelection(int dateOffset)
    {
        if (SelectedDay is not { } selected || dateOffset == 0)
        {
            return new(_selection, _selection);
        }

        var targetDayNumber = (long)selected.Day.Date.DayNumber + dateOffset;
        // Normally every date is returned. Also handle a partial calendar without
        // inventing entries: choose the nearest real day in the requested direction.
        var index = SelectedIndex;
        if (dateOffset > 0)
        {
            while (index < Days.Count - 1 && Days[index].Day.Date.DayNumber < targetDayNumber)
            {
                index++;
            }
        }
        else
        {
            while (index > 0 && Days[index].Day.Date.DayNumber > targetDayNumber)
            {
                index--;
            }
        }

        return index == SelectedIndex ? new(_selection, _selection) : SelectIndex(index);
    }

    private ContributionSelectionChange SetSelection(int index, ContributionSelectionSnapshot? previous)
    {
        ContributionSelectionSnapshot? current = null;
        if (index >= 0)
        {
            var plotDay = Days[index];
            var day = plotDay.Day;
            current = new(plotDay, $"{day.Date.ToString("ddd, d MMM yyyy", CultureInfo.CurrentCulture)} · {day.Count:N0} {(day.Count == 1 ? "contribution" : "contributions")}");
        }

        var change = new ContributionSelectionChange(previous, current);
        SelectedIndex = index;
        _selection = current;
        // Observers must see the final index, day, and description together.
        if (change.ValueChanged)
        {
            OnPropertyChanged(nameof(SelectedDayDescription));
        }

        return change;
    }
}
