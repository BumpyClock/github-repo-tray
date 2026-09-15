using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GitHubTray.Core;

internal static class PullRequestParser
{
    internal const int CheckLimit = 100;
    internal const int LabelLimit = 10;

    internal static readonly string Selection = $$"""
        number title url updatedAt state isDraft headRefName baseRefName reviewDecision
        repository { nameWithOwner }
        author { login avatarUrl }
        comments { totalCount }
        labels(first: {{LabelLimit}}) { totalCount nodes { name color } }
        commits(last: 1) {
          nodes {
            commit {
              oid
              statusCheckRollup {
                state
                contexts(first: {{CheckLimit}}) {
                  totalCount
                  nodes {
                    __typename
                    ... on CheckRun { name status conclusion }
                    ... on StatusContext { context state }
                  }
                }
              }
            }
          }
        }
        """;

    internal static string Query(string login, bool reviewRequested)
    {
        if (!Regex.IsMatch(login, @"\A[A-Za-z0-9-]{1,100}\z", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Invalid GitHub login.", nameof(login));
        }
        var role = reviewRequested ? "review-requested" : "author";
        var stateFilter = reviewRequested ? "is:open " : "";
        return $$"""
            query PullRequests {
              viewer { login }
              search(query: "is:pr {{stateFilter}}{{role}}:{{login}} sort:updated-desc", type: ISSUE, first: {{DashboardService.ItemLimit}}) {
                nodes {
                  ... on PullRequest {
                    {{Selection}}
                  }
                }
              }
            }
            """;
    }

    // Permit only generated operations at the CLI boundary, not arbitrary GraphQL arguments.
    internal static bool IsSupportedQuery(string query)
    {
        var match = Regex.Match(query,
            "search\\(query: \"is:pr (?:is:open )?(author|review-requested):([A-Za-z0-9-]{1,100}) sort:updated-desc\"",
            RegexOptions.CultureInvariant);
        return (match.Success && query == Query(match.Groups[2].Value, match.Groups[1].Value == "review-requested"))
            || ActivityPullRequestQuery.IsSupportedQuery(query);
    }

    internal static IReadOnlyList<DashboardItem> Parse(JsonElement root, string login, bool reviewRequested)
    {
        var data = ValidateResponse(root, login);
        var nodes = data.GetProperty("search").GetProperty("nodes");
        if (nodes.GetArrayLength() > DashboardService.ItemLimit)
        {
            throw new JsonException("Too many pull requests.");
        }
        return nodes.EnumerateArray().Select(pull => ParsePullRequest(pull, reviewRequested)).ToImmutableArray();
    }

    internal static JsonElement ValidateResponse(JsonElement root, string login)
    {
        root.TryGetProperty("data", out var data);
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("viewer", out var viewer) && viewer.ValueKind == JsonValueKind.Object &&
            !string.Equals(Text(viewer, "login"), login, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitHubAccountChangedException(
                "The GitHub account changed while loading pull requests. Refresh again to load the current account.");
        }
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind != JsonValueKind.Null)
        {
            if (errors.GetArrayLength() > 0)
            {
                throw new GitHubException(
                    "GitHub could not load pull requests and checks. Check your gh account permissions and refresh.");
            }
        }
        if (!string.Equals(Text(data.GetProperty("viewer"), "login"), login, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitHubAccountChangedException("The GitHub account changed while loading pull requests. Refresh again.");
        }

        return data;
    }

    internal static DashboardItem ParsePullRequest(JsonElement pull, bool reviewRequested = false)
    {
        var number = Count(pull, "number");
        if (number == 0) throw new JsonException("Invalid pull request number.");
        var title = Text(pull, "title");
        var repository = Text(pull.GetProperty("repository"), "nameWithOwner");
        var url = DashboardService.GitHubUrl(Text(pull, "url"));
        var path = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path.Length != 4 || path[2] != "pull" ||
            $"{path[0]}/{path[1]}" != repository || path[3] != number.ToString(CultureInfo.InvariantCulture))
        {
            throw new JsonException("Invalid pull request URL.");
        }
        var author = pull.GetProperty("author");
        var authorLogin = author.ValueKind == JsonValueKind.Null ? "Deleted user" : Text(author, "login");
        var avatar = author.ValueKind == JsonValueKind.Null
            ? null
            : ParseAvatarUrl(OptionalText(author, "avatarUrl"));
        var labels = pull.GetProperty("labels");
        var labelCount = Count(labels, "totalCount");
        var labelItems = labels.GetProperty("nodes").EnumerateArray().Select(label =>
        {
            var color = Text(label, "color");
            if (!Regex.IsMatch(color, @"\A[0-9a-fA-F]{6}\z", RegexOptions.CultureInvariant))
                throw new JsonException("Invalid label color.");
            return new PullRequestLabel(Text(label, "name"), color);
        }).ToImmutableArray();
        if (labelItems.Length != Math.Min(labelCount, LabelLimit))
            throw new JsonException("Incomplete label response.");
        var isDraft = pull.GetProperty("isDraft").GetBoolean();
        var state = Text(pull, "state") switch
        {
            "OPEN" => PullRequestState.Open,
            "CLOSED" => PullRequestState.Closed,
            "MERGED" => PullRequestState.Merged,
            _ => throw new JsonException("Unknown pull request state.")
        };
        var details = new PullRequestDetails(number, title, authorLogin, avatar, isDraft,
            Text(pull, "headRefName"), Text(pull, "baseRefName"), OptionalText(pull, "reviewDecision"),
            Count(pull.GetProperty("comments"), "totalCount"), labelItems, labelCount, ParseChecks(pull))
        {
            State = state
        };
        return new DashboardItem(url.AbsoluteUri, $"#{number} {title}", repository,
            state == PullRequestState.Merged ? "Merged pull request"
                : state == PullRequestState.Closed ? "Closed pull request"
                : reviewRequested ? "Review requested" : isDraft ? "Draft pull request" : "Open pull request",
            DateTimeOffset.Parse(Text(pull, "updatedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal), url)
        {
            PullRequest = details
        };
    }

    private static Uri? ParseAvatarUrl(string? value)
    {
        if (value is null)
        {
            return null;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var avatar) || !IsGitHubAvatarUrl(avatar))
        {
            throw new JsonException("Expected a GitHub avatar URL.");
        }
        return avatar;
    }

    internal static bool IsGitHubAvatarUrl(Uri avatar) =>
        avatar.Scheme == Uri.UriSchemeHttps &&
        avatar.Host == "avatars.githubusercontent.com" &&
        avatar.IsDefaultPort &&
        avatar.UserInfo.Length == 0;

    private static CommitChecks ParseChecks(JsonElement pull)
    {
        var commits = pull.GetProperty("commits").GetProperty("nodes");
        if (commits.GetArrayLength() == 0) return new(null, CheckRollupState.Unknown, [], 0);
        if (commits.GetArrayLength() != 1) throw new JsonException("Expected the latest commit only.");
        var commit = commits[0].GetProperty("commit");
        var oid = Text(commit, "oid");
        if (!Regex.IsMatch(oid, @"\A[0-9a-fA-F]{40,64}\z", RegexOptions.CultureInvariant))
            throw new JsonException("Invalid commit ID.");
        var rollup = commit.GetProperty("statusCheckRollup");
        if (rollup.ValueKind == JsonValueKind.Null) return new(oid, CheckRollupState.NoChecks, [], 0);
        var state = Text(rollup, "state") switch
        {
            "SUCCESS" => CheckRollupState.Passed,
            "FAILURE" or "ERROR" => CheckRollupState.Failed,
            "PENDING" or "EXPECTED" => CheckRollupState.Pending,
            _ => CheckRollupState.Unknown
        };
        var contexts = rollup.GetProperty("contexts");
        var count = Count(contexts, "totalCount");
        var checks = contexts.GetProperty("nodes").EnumerateArray().Select(ParseCheck).ToImmutableArray();
        if (checks.Length != Math.Min(count, CheckLimit)) throw new JsonException("Incomplete checks response.");
        return new(oid, state, checks, count);
    }

    private static PullRequestCheck ParseCheck(JsonElement check) => Text(check, "__typename") switch
    {
        "CheckRun" => new(Text(check, "name"), Text(check, "status") switch
        {
            "IN_PROGRESS" => CheckState.Running,
            "QUEUED" or "WAITING" or "PENDING" or "REQUESTED" => CheckState.Pending,
            "COMPLETED" => OptionalText(check, "conclusion") switch
            {
                "SUCCESS" => CheckState.Passed,
                "FAILURE" or "TIMED_OUT" or "STARTUP_FAILURE" => CheckState.Failed,
                "NEUTRAL" => CheckState.Neutral,
                "SKIPPED" => CheckState.Skipped,
                "CANCELLED" => CheckState.Cancelled,
                "ACTION_REQUIRED" => CheckState.ActionRequired,
                _ => CheckState.Unknown
            },
            _ => CheckState.Unknown
        }),
        "StatusContext" => new(Text(check, "context"), Text(check, "state") switch
        {
            "SUCCESS" => CheckState.Passed,
            "FAILURE" or "ERROR" => CheckState.Failed,
            "PENDING" or "EXPECTED" => CheckState.Pending,
            _ => CheckState.Unknown
        }),
        _ => throw new JsonException("Unknown check context type.")
    };

    private static int Count(JsonElement element, string name) =>
        element.GetProperty(name).GetInt32() is >= 0 and var count ? count : throw new JsonException($"Invalid {name}.");

    private static string Text(JsonElement element, string name) =>
        OptionalText(element, name) is { Length: > 0 } value ? value : throw new JsonException($"Missing {name}.");

    private static string? OptionalText(JsonElement element, string name) =>
        element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : element.GetProperty(name).GetString();
}
