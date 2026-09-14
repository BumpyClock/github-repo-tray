using System.Globalization;
using System.Text.Json.Nodes;

namespace GitHubTray.Core.Tests;

internal static class ContributionTestData
{
    internal static JsonObject Response(
        string login = "octocat", DateOnly? from = null, DateOnly? to = null, bool allZero = false)
    {
        var firstDate = from ?? new DateOnly(2023, 3, 1);
        var lastDate = to ?? new DateOnly(2024, 3, 1);
        var counts = new[] { 0, 11, 1, 99, 4 };
        var levels = new[] { "NONE", "FIRST_QUARTILE", "SECOND_QUARTILE", "THIRD_QUARTILE", "FOURTH_QUARTILE" };
        var weeks = new JsonArray();
        JsonArray? days = null;
        var total = 0;
        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            if (days is null || date.DayOfWeek == DayOfWeek.Sunday)
            {
                days = [];
                weeks.Add(new JsonObject { ["firstDay"] = Iso(date), ["contributionDays"] = days });
            }
            var index = date.DayNumber - firstDate.DayNumber;
            var count = !allZero && index < counts.Length ? counts[index] : 0;
            var level = !allZero && index < levels.Length ? levels[index] : "NONE";
            days.Add(new JsonObject
            {
                ["date"] = Iso(date),
                ["weekday"] = (int)date.DayOfWeek,
                ["contributionCount"] = count,
                ["contributionLevel"] = level
            });
            total += count;
        }
        return new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["viewer"] = new JsonObject
                {
                    ["login"] = login,
                    ["contributionsCollection"] = new JsonObject
                    {
                        ["contributionCalendar"] = new JsonObject
                        {
                            ["totalContributions"] = total,
                            ["weeks"] = weeks
                        }
                    }
                }
            }
        };
    }

    internal static JsonObject Calendar(JsonObject response) =>
        response["data"]!["viewer"]!["contributionsCollection"]!["contributionCalendar"]!.AsObject();

    internal static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
