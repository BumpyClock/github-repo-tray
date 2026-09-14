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

        var activity = LoadSectionAsync(
            $"users/{Uri.EscapeDataString(user.Login)}/events?per_page={ItemLimit}",
            ParseActivity, previous?.Activity, cancellationToken);
        var authored = LoadSectionAsync(SearchEndpoint($"is:pr is:open author:{user.Login}"),
            root => ParsePullRequests(root, reviewRequested: false), previous?.PullRequests, cancellationToken);
        var reviews = LoadSectionAsync(SearchEndpoint($"is:pr is:open review-requested:{user.Login}"),
            root => ParsePullRequests(root, reviewRequested: true), previous?.ReviewRequests, cancellationToken);
        var repositories = LoadSectionAsync(
            $"user/repos?sort=pushed&direction=desc&per_page={ItemLimit}&affiliation=owner,collaborator,organization_member",
            ParseRepositories, previous?.Repositories, cancellationToken);
        var contributions = LoadContributionsAsync(user.Login, previous?.Contributions, cancellationToken);
        await Task.WhenAll(activity, authored, reviews, repositories, contributions).ConfigureAwait(false);
        return new DashboardSnapshot(user, await activity, await authored, await reviews, await repositories)
        {
            Contributions = await contributions
        };
    }

    private static string SearchEndpoint(string query) =>
        $"search/issues?q={Uri.EscapeDataString(query)}&sort=updated&order=desc&per_page={ItemLimit}";

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
        catch (GitHubException exception)
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

    private static IReadOnlyList<DashboardItem> ParsePullRequests(JsonElement root, bool reviewRequested)
    {
        if (OptionalBool(root, "incomplete_results"))
        {
            throw new GitHubException("GitHub search returned incomplete results. Refresh again; previously loaded results are retained.");
        }
        return root.GetProperty("items").EnumerateArray().Take(ItemLimit).Select(pull =>
        {
            var url = GitHubUrl(Text(pull, "html_url"));
            var path = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (path.Length < 4 || path[2] != "pull")
            {
                throw new JsonException("Expected a pull request URL.");
            }
            var number = pull.GetProperty("number").GetInt32();
            if (number < 1)
            {
                throw new JsonException("Invalid pull request number.");
            }
            var detail = reviewRequested ? "Review requested" : OptionalBool(pull, "draft") ? "Draft pull request" : "Open pull request";
            return new DashboardItem(url.AbsoluteUri, $"#{number} {Text(pull, "title")}",
                $"{path[0]}/{path[1]}", detail, Date(pull, "updated_at"), url);
        }).ToArray();
    }

    private static IReadOnlyList<DashboardItem> ParseActivity(JsonElement root) =>
        root.EnumerateArray().Take(ItemLimit).Select(ParseEvent)
            .OrderByDescending(item => item.UpdatedAt).ToArray();

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
        return new DashboardItem(Text(item, "id"), title, repository, detail, Date(item, "created_at"), url);
    }

    private static Uri GitHubUrl(string value)
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
