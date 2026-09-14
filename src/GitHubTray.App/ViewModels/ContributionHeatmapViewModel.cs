using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GitHubTray.Core;

namespace GitHubTray_App.ViewModels;

public sealed record ContributionPlotDay(ContributionDay Day, int WeekIndex, int DayIndex)
{
    public string Description => $"{Day.Count:N0} {(Day.Count == 1 ? "contribution" : "contributions")} on {Day.Date.ToString("D", CultureInfo.CurrentCulture)}";
    public string AutomationId => $"ContributionDay_{Day.Date:yyyy-MM-dd}";
}

/// <summary>Calendar geometry uses actual dates; missing slots never become zero-count days.</summary>
public sealed partial class ContributionHeatmapViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDays))]
    public partial IReadOnlyList<ContributionPlotDay> Days { get; set; } = [];

    [ObservableProperty]
    public partial string Summary { get; set; } = "Not loaded";

    [ObservableProperty]
    public partial string Range { get; set; } = "";

    [ObservableProperty]
    public partial string RangeDescription { get; set; } = "";

    [ObservableProperty]
    public partial string AccessibleSummary { get; set; } = "Contributions. No calendar is displayed.";

    [ObservableProperty]
    public partial string SelectedDayDescription { get; set; } = "";

    public int WeekCount { get; private set; }
    public int SelectedIndex { get; private set; } = -1;
    public bool HasDays => Days.Count > 0;
    public ContributionPlotDay? SelectedDay => SelectedIndex >= 0 && SelectedIndex < Days.Count ? Days[SelectedIndex] : null;

    public void UpdateCalendar(ContributionCalendar? calendar)
    {
        var previousDate = SelectedDay?.Day.Date;
        var days = calendar?.Weeks.SelectMany(week => week.Days).OrderBy(day => day.Date).ToArray() ?? [];
        SelectedIndex = -1;
        SelectedDayDescription = "";

        if (days.Length == 0)
        {
            Days = [];
            WeekCount = 0;
            Summary = calendar is null ? "Not loaded" : $"{calendar.TotalContributions:N0} · last 12 months";
            Range = "";
            RangeDescription = "";
            AccessibleSummary = calendar is null
                ? "Contributions. No calendar is displayed."
                : $"{calendar.TotalContributions:N0} contributions. No daily data was returned.";
            return;
        }

        var first = days[0].Date;
        var last = days[^1].Date;
        var firstSunday = first.AddDays(-(int)first.DayOfWeek);
        WeekCount = (last.DayNumber - firstSunday.DayNumber) / 7 + 1;
        Days = days.Select(day => new ContributionPlotDay(
            day,
            (day.Date.DayNumber - firstSunday.DayNumber) / 7,
            (int)day.Date.DayOfWeek)).ToArray();
        Summary = $"{calendar!.TotalContributions:N0} · last 12 months";
        Range = $"{first.ToString("MMM yyyy", CultureInfo.CurrentCulture)} – {last.ToString("MMM yyyy", CultureInfo.CurrentCulture)}";
        RangeDescription = $"{first.ToString("D", CultureInfo.CurrentCulture)} through {last.ToString("D", CultureInfo.CurrentCulture)}";
        AccessibleSummary = $"{calendar.TotalContributions:N0} contributions in the last 12 months. {RangeDescription}. Sunday-first calendar.";
        var selectedIndex = Array.FindIndex(days, day => day.Date == previousDate);
        SelectIndex(selectedIndex >= 0 ? selectedIndex : days.Length - 1);
    }

    public void SelectIndex(int index)
    {
        if (!HasDays)
        {
            return;
        }

        SelectedIndex = Math.Clamp(index, 0, Days.Count - 1);
        var day = Days[SelectedIndex].Day;
        SelectedDayDescription = $"{day.Date.ToString("ddd, d MMM yyyy", CultureInfo.CurrentCulture)} · {day.Count:N0} {(day.Count == 1 ? "contribution" : "contributions")}";
    }

    public void MoveSelection(int dateOffset)
    {
        if (SelectedDay is not { } selected)
        {
            return;
        }

        var target = selected.Day.Date.AddDays(dateOffset);
        // Normally every date is returned. Also handle a partial calendar without
        // inventing entries: choose the nearest real day in the requested direction.
        var index = SelectedIndex;
        if (dateOffset > 0)
        {
            while (index < Days.Count - 1 && Days[index].Day.Date < target)
            {
                index++;
            }
        }
        else
        {
            while (index > 0 && Days[index].Day.Date > target)
            {
                index--;
            }
        }

        SelectIndex(index);
    }
}
