using System.Globalization;
using System.Text.Json;

namespace GitHubTray.Core;

public sealed class DashboardService(IGitHubApi api)
{
    public const int ItemLimit = 30;

    public async Task<DashboardSnapshot> RefreshAsync(
        DashboardSnapshot? previous = null,
        CancellationToken cancellationToken = default)
    {
        var user = await ReadAsync("user", ParseUser, cancellationToken).ConfigureAwait(false);
        // Never reuse private data from another gh account after an account switch.
        if (!string.Equals(previous?.User.Login, user.Login, StringComparison.OrdinalIgnoreCase))
        {
            previous = null;
        }

        var activity = LoadActivityAsync(user.Login, previous?.Activity, cancellationToken);
        var authored = LoadPullRequestsAsync(user.Login, false, previous?.PullRequests, cancellationToken);
        var reviews = LoadPullRequestsAsync(user.Login, true, previous?.ReviewRequests, cancellationToken);
        var repositories = LoadSectionAsync(
            $"user/repos?sort=pushed&direction=desc&per_page={ItemLimit}&affiliation=owner,collaborator,organization_member",
            ParseRepositories, previous?.Repositories, cancellationToken);
        var contributions = LoadContributionsAsync(user.Login, previous?.Contributions, cancellationToken);
        var copilot = LoadCopilotUsageAsync(user.Login, previous?.Copilot, cancellationToken);
        await Task.WhenAll(activity, authored, reviews, repositories, contributions, copilot).ConfigureAwait(false);
        var verifiedUser = await ReadAsync("user", ParseUser, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(user.Login, verifiedUser.Login, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitHubAccountChangedException("The GitHub account changed during refresh. No new data was displayed. Refresh again to load the current account.");
        }
        return new DashboardSnapshot(user, await activity, await authored, await reviews, await repositories)
        {
            Contributions = await contributions,
            Copilot = await copilot
        };
    }

    private async Task<CopilotUsageSection> LoadCopilotUsageAsync(
        string login, CopilotUsageSection? previous, CancellationToken cancellationToken)
    {
        try
        {
            var json = await api.GetAsync(CopilotUsageParser.Endpoint, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var usage = CopilotUsageParser.Parse(document.RootElement, login);
            return new(usage, DateTimeOffset.UtcNow, null);
        }
        catch (GitHubException exception) when (exception is not GitHubAccountChangedException)
        {
            return new(previous?.Usage, previous?.UpdatedAt, exception.Message);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or
                                         InvalidOperationException or KeyNotFoundException or
                                         OverflowException or ArgumentOutOfRangeException)
        {
            return new(previous?.Usage, previous?.UpdatedAt,
                "GitHub returned an unexpected Copilot usage response. Refresh again or update GitHub Tray if this persists.");
        }
    }

    private async Task<DashboardSection> LoadPullRequestsAsync(
        string login, bool reviewRequested, DashboardSection? previous, CancellationToken cancellationToken)
    {
        try
        {
            var json = await api.QueryAsync(PullRequestParser.Query(login, reviewRequested), cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var items = PullRequestParser.Parse(document.RootElement, login, reviewRequested);
            return new DashboardSection(items, DateTimeOffset.UtcNow, null);
        }
        catch (GitHubException exception) when (exception is not GitHubAccountChangedException)
        {
            return new DashboardSection(previous?.Items ?? [], previous?.UpdatedAt, exception.Message);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or
                                         InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            return new DashboardSection(previous?.Items ?? [], previous?.UpdatedAt,
                "GitHub returned an unexpected response for pull requests and checks. Refresh again or update GitHub Tray if this persists.");
        }
    }

    private async Task<DashboardSection> LoadActivityAsync(
        string login, DashboardSection? previous, CancellationToken cancellationToken)
    {
        try
        {
            var items = await ReadAsync($"users/{Uri.EscapeDataString(login)}/events?per_page={ItemLimit}",
                ParseActivity, cancellationToken).ConfigureAwait(false);
            var references = items.Where(item => item.PullRequestActivity is not null)
                .Select(item => new ActivityPullRequestReference(item.Repository, item.PullRequestActivity!.Number)).ToArray();
            if (references.Length > 0)
            {
                var json = await api.QueryAsync(ActivityPullRequestQuery.Query(references), cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                items = ActivityPullRequestQuery.Hydrate(document.RootElement, login, items);
            }
            return new DashboardSection(items, DateTimeOffset.UtcNow, null);
        }
        catch (GitHubException exception) when (exception is not GitHubAccountChangedException)
        {
            return new DashboardSection(previous?.Items ?? [], previous?.UpdatedAt, exception.Message);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or
                                         InvalidOperationException or KeyNotFoundException or OverflowException or ArgumentException)
        {
            return new DashboardSection(previous?.Items ?? [], previous?.UpdatedAt,
                "GitHub returned an unexpected response for recent activity. Refresh again or update GitHub Tray if this persists.");
        }
    }

    private async Task<ContributionSection> LoadContributionsAsync(
        string login, ContributionSection? previous, CancellationToken cancellationToken)
    {
        try
        {
            var json = await api.QueryAsync(ContributionCalendarParser.Query, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var calendar = ContributionCalendarParser.Parse(
                document.RootElement, login, DateOnly.FromDateTime(DateTime.UtcNow));
            return new ContributionSection(calendar, DateTimeOffset.UtcNow, null);
        }
        catch (GitHubException exception) when (exception is not GitHubAccountChangedException)
        {
            return new ContributionSection(previous?.Calendar, previous?.UpdatedAt, exception.Message);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or
                                         InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            return new ContributionSection(previous?.Calendar, previous?.UpdatedAt,
                "GitHub returned an unexpected contribution calendar. Refresh again or update GitHub Tray if this persists.");
        }
    }

    private async Task<DashboardSection> LoadSectionAsync(
        string endpoint,
        Func<JsonElement, IReadOnlyList<DashboardItem>> parse,
        DashboardSection? previous,
        CancellationToken cancellationToken)
    {
        try
        {
            var items = await ReadAsync(endpoint, parse, cancellationToken).ConfigureAwait(false);
            return new DashboardSection(items, DateTimeOffset.UtcNow, null);
        }
        catch (GitHubException exception)
        {
            return new DashboardSection(previous?.Items ?? [], previous?.UpdatedAt, exception.Message);
        }
    }

    private async Task<T> ReadAsync<T>(
        string endpoint,
        Func<JsonElement, T> parse,
        CancellationToken cancellationToken)
    {
        var json = await api.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(json);
            return parse(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            throw new GitHubException("GitHub returned an unexpected response. Refresh again or update GitHub Tray if this persists.", exception);
        }
    }

    private static GitHubUser ParseUser(JsonElement root)
    {
        var login = Text(root, "login");
        if (login.Length > 100 || login.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new JsonException("Invalid GitHub login.");
        }
        return new GitHubUser(login, OptionalText(root, "name") ?? login, GitHubUrl(Text(root, "html_url")));
    }

    private static IReadOnlyList<DashboardItem> ParseRepositories(JsonElement root) =>
        root.EnumerateArray().Take(ItemLimit).Select(repo =>
        {
            var fullName = Text(repo, "full_name");
            var description = OptionalText(repo, "description");
            var visibility = OptionalBool(repo, "private") ? "Private" : "Public";
            var archived = OptionalBool(repo, "archived") ? " / Archived" : "";
            return new DashboardItem(
                fullName, fullName, OptionalText(repo, "language") ?? "Repository",
                $"{visibility}{archived}" + (string.IsNullOrWhiteSpace(description) ? "" : $" / {description}"),
                OptionalDate(repo, "pushed_at") ?? Date(repo, "updated_at"),
                GitHubUrl(Text(repo, "html_url")));
        }).ToArray();

    private static IReadOnlyList<DashboardItem> ParseActivity(JsonElement root) =>
        root.EnumerateArray().Select(ParseEvent)
            .OrderByDescending(item => item.UpdatedAt).DistinctBy(item => item.Id).Take(ItemLimit)
            .GroupBy(item => item.PullRequestActivity is { } pull
                ? $"pr:{item.Repository}/{pull.Number}" : $"event:{item.Id}")
            .Select(group =>
            {
                var latest = group.First();
                return latest.PullRequestActivity is { } activity
                    ? latest with { PullRequestActivity = activity with { EventCount = group.Count() } }
                    : latest;
            })
            .ToArray();

    private static DashboardItem ParseEvent(JsonElement item)
    {
        var repository = Text(item.GetProperty("repo"), "name");
        var segments = repository.Split('/');
        if (segments.Length != 2 || segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new JsonException("Invalid repository name.");
        }
        var repositoryUrl = new Uri($"https://github.com/{Uri.EscapeDataString(segments[0])}/{Uri.EscapeDataString(segments[1])}");
        var type = Text(item, "type");
        var payload = item.GetProperty("payload");
        var action = OptionalText(payload, "action");
        var title = type switch
        {
            "PushEvent" => "Pushed commits",
            "PullRequestEvent" => $"{Capitalize(action ?? "updated")} a pull request",
            "PullRequestReviewEvent" => "Reviewed a pull request",
            "PullRequestReviewCommentEvent" => "Commented on a pull request",
            "IssuesEvent" => $"{Capitalize(action ?? "updated")} an issue",
            "IssueCommentEvent" => "Commented on an issue or pull request",
            "CreateEvent" => $"Created a {OptionalText(payload, "ref_type") ?? "reference"}",
            "DeleteEvent" => $"Deleted a {OptionalText(payload, "ref_type") ?? "reference"}",
            "ForkEvent" => "Forked a repository",
            "WatchEvent" => "Starred a repository",
            "ReleaseEvent" => $"{Capitalize(action ?? "published")} a release",
            "PublicEvent" => "Made a repository public",
            "MemberEvent" => "Updated a collaborator",
            "GollumEvent" => "Updated the wiki",
            _ => "Repository activity"
        };
        var detail = type == "PushEvent"
            ? (OptionalText(payload, "ref") ?? "Commits").Replace("refs/heads/", "", StringComparison.Ordinal)
            : OptionalText(payload, "ref") ?? "";
        var url = repositoryUrl;
        foreach (var key in new[] { "comment", "review", "pull_request", "issue", "release", "forkee" })
        {
            if (payload.TryGetProperty(key, out var subject) && subject.ValueKind == JsonValueKind.Object)
            {
                detail = OptionalText(subject, "title") ?? OptionalText(subject, "name") ?? detail;
                if (OptionalText(subject, "html_url") is { } target)
                {
                    url = GitHubUrl(target);
                    break;
                }
            }
        }
        PullRequestActivity? activity = null;
        var isPull = payload.TryGetProperty("pull_request", out var pull) && pull.ValueKind == JsonValueKind.Object;
        if (!isPull && type == "IssueCommentEvent" && payload.TryGetProperty("issue", out var issue) &&
            issue.TryGetProperty("pull_request", out _))
        {
            pull = issue;
            isPull = true;
            title = "Commented on a pull request";
        }
        if (isPull)
        {
            var number = pull.GetProperty("number").GetInt32();
            if (number < 1) throw new JsonException("Invalid activity pull request number.");
            activity = new PullRequestActivity(number, title, 1);
            url = new Uri($"{repositoryUrl.AbsoluteUri}/pull/{number}");
        }
        return new DashboardItem(Text(item, "id"), title, repository, detail, Date(item, "created_at"), url)
        {
            PullRequestActivity = activity
        };
    }

    internal static Uri GitHubUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
        {
            throw new JsonException("Expected an HTTPS github.com URL.");
        }
        return uri;
    }

    private static string Text(JsonElement element, string name) =>
        OptionalText(element, name) is { Length: > 0 } value ? value : throw new JsonException($"Missing {name}.");

    private static string? OptionalText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static bool OptionalBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null && value.GetBoolean();

    private static DateTimeOffset Date(JsonElement element, string name) =>
        OptionalDate(element, name) ?? throw new JsonException($"Missing {name}.");

    private static DateTimeOffset? OptionalDate(JsonElement element, string name) =>
        OptionalText(element, name) is { } value
            ? DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
            : null;

    private static string Capitalize(string value) =>
        value.Length > 0 ? char.ToUpperInvariant(value[0]) + value[1..] : value;
}
