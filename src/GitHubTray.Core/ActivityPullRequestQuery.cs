using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GitHubTray.Core;

internal sealed record ActivityPullRequestReference(string Repository, int Number);

internal static class ActivityPullRequestQuery
{
    internal static string Query(IReadOnlyList<ActivityPullRequestReference> references)
    {
        if (references.Count is < 1 or > DashboardService.ItemLimit)
            throw new ArgumentException("A bounded batch of pull requests is required.", nameof(references));
        var selections = references.Select((reference, index) =>
        {
            var segments = reference.Repository.Split('/');
            if (segments.Length != 2 || reference.Number < 1 ||
                !Regex.IsMatch(segments[0], @"\A[A-Za-z0-9-]{1,100}\z", RegexOptions.CultureInvariant) ||
                !Regex.IsMatch(segments[1], @"\A[A-Za-z0-9_.-]{1,100}\z", RegexOptions.CultureInvariant) ||
                segments[1] is "." or "..")
                throw new ArgumentException("Invalid pull request reference.", nameof(references));
            return $$"""
                pr{{index}}: repository(owner: "{{segments[0]}}", name: "{{segments[1]}}") {
                  pullRequest(number: {{reference.Number}}) {
                    {{PullRequestParser.Selection}}
                  }
                }
                """;
        });
        return "query ActivityPullRequests {\nviewer { login }\n" + string.Join("\n", selections) + "\n}";
    }

    internal static bool IsSupportedQuery(string query)
    {
        if (!query.StartsWith("query ActivityPullRequests {", StringComparison.Ordinal)) return false;
        var matches = Regex.Matches(query,
            "pr[0-9]+: repository\\(owner: \"([A-Za-z0-9-]{1,100})\", name: \"([A-Za-z0-9_.-]{1,100})\"\\) \\{\\s+pullRequest\\(number: ([0-9]+)\\)",
            RegexOptions.CultureInvariant);
        if (matches.Count is < 1 or > DashboardService.ItemLimit) return false;
        var references = new List<ActivityPullRequestReference>();
        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
                number < 1 || match.Groups[2].Value is "." or "..")
                return false;
            references.Add(new($"{match.Groups[1].Value}/{match.Groups[2].Value}", number));
        }
        return query == Query(references);
    }

    internal static IReadOnlyList<DashboardItem> Hydrate(
        JsonElement root, string login, IReadOnlyList<DashboardItem> activity)
    {
        var data = PullRequestParser.ValidateResponse(root, login);
        var index = 0;
        return activity.Select(item =>
        {
            if (item.PullRequestActivity is not { } summary) return item;
            var repository = data.GetProperty($"pr{index++}");
            if (repository.ValueKind == JsonValueKind.Null ||
                repository.GetProperty("pullRequest").ValueKind == JsonValueKind.Null)
                throw new GitHubException("A pull request in recent activity is no longer accessible. Previously loaded activity is retained.");
            var pull = PullRequestParser.ParsePullRequest(repository.GetProperty("pullRequest"));
            if (pull.Repository != item.Repository || pull.PullRequest!.Number != summary.Number)
                throw new JsonException("Activity pull request identity mismatch.");
            return pull with
            {
                UpdatedAt = item.UpdatedAt,
                PullRequestActivity = summary
            };
        }).ToImmutableArray();
    }
}
