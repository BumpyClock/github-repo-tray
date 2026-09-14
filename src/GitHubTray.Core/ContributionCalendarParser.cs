using System.Globalization;
using System.Text.Json;

namespace GitHubTray.Core;

internal static class ContributionCalendarParser
{
    // GitHub defaults contributionsCollection to one year ago through the current time.
    internal const string Query = """
        query ContributionCalendar {
          viewer {
            login
            contributionsCollection {
              contributionCalendar {
                totalContributions
                weeks {
                  firstDay
                  contributionDays {
                    date
                    weekday
                    contributionCount
                    contributionLevel
                  }
                }
              }
            }
          }
        }
        """;

    internal static ContributionCalendar Parse(JsonElement root, string expectedLogin, DateOnly today)
    {
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind != JsonValueKind.Null)
        {
            if (errors.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Invalid GraphQL errors.");
            }
            if (errors.GetArrayLength() > 0)
            {
                throw new GitHubException("GitHub could not load the contribution calendar. Check your gh account permissions and refresh.");
            }
        }

        var viewer = root.GetProperty("data").GetProperty("viewer");
        if (!string.Equals(viewer.GetProperty("login").GetString(), expectedLogin, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitHubAccountChangedException("The GitHub account changed while loading contributions. Refresh again to load the current account.");
        }
        var calendar = viewer.GetProperty("contributionsCollection").GetProperty("contributionCalendar");
        var total = calendar.GetProperty("totalContributions").GetInt32();
        var weekElements = calendar.GetProperty("weeks");
        if (total < 0 || weekElements.GetArrayLength() is < 1 or > 54)
        {
            throw new JsonException("Invalid contribution calendar size or total.");
        }

        var weeks = new List<ContributionWeek>();
        DateOnly? previousDate = null;
        long countedContributions = 0;
        foreach (var week in weekElements.EnumerateArray())
        {
            var firstDay = Date(week, "firstDay");
            var dayElements = week.GetProperty("contributionDays");
            if (dayElements.GetArrayLength() is < 1 or > 7 ||
                (weeks.Count > 0 && firstDay.DayOfWeek != DayOfWeek.Sunday))
            {
                throw new JsonException("Invalid contribution week.");
            }

            var days = new List<ContributionDay>();
            foreach (var day in dayElements.EnumerateArray())
            {
                var date = Date(day, "date");
                var weekday = day.GetProperty("weekday").GetInt32();
                var count = day.GetProperty("contributionCount").GetInt32();
                var level = day.GetProperty("contributionLevel").GetString() switch
                {
                    "NONE" => ContributionLevel.None,
                    "FIRST_QUARTILE" => ContributionLevel.First,
                    "SECOND_QUARTILE" => ContributionLevel.Second,
                    "THIRD_QUARTILE" => ContributionLevel.Third,
                    "FOURTH_QUARTILE" => ContributionLevel.Fourth,
                    _ => throw new JsonException("Unknown contribution level.")
                };
                if (date > today || weekday != (int)date.DayOfWeek || count < 0 ||
                    (count == 0) != (level == ContributionLevel.None) ||
                    (days.Count == 0 && date != firstDay) ||
                    date.DayNumber - weekday != firstDay.DayNumber - (int)firstDay.DayOfWeek ||
                    (previousDate.HasValue && date.DayNumber != previousDate.Value.DayNumber + 1))
                {
                    throw new JsonException("Invalid contribution day or date order.");
                }
                countedContributions += count;
                previousDate = date;
                days.Add(new ContributionDay(date, count, level));
            }
            if (weeks.Count < weekElements.GetArrayLength() - 1 && days[^1].Date.DayOfWeek != DayOfWeek.Saturday)
            {
                throw new JsonException("Incomplete interior contribution week.");
            }
            weeks.Add(new ContributionWeek(firstDay, days.ToArray()));
        }
        if (countedContributions != total)
        {
            throw new JsonException("Contribution calendar total does not match its days.");
        }
        return new ContributionCalendar(total, weeks.ToArray());
    }

    private static DateOnly Date(JsonElement element, string name) =>
        DateOnly.ParseExact(element.GetProperty(name).GetString() ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
