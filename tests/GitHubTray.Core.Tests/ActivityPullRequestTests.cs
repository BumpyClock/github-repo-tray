using System.Text.Json;
using System.Text.Json.Nodes;
using GitHubTray.Core;

namespace GitHubTray.Core.Tests;

public sealed class ActivityPullRequestTests
{
    [Fact]
    public async Task GroupsReviewsCommentsAndPrEventsIntoOneCardAlongsideOtherActivity()
    {
        var api = new ActivityApi();
        var snapshot = await new DashboardService(api).RefreshAsync();
        Assert.Null(snapshot.Activity.Error);
        Assert.Equal(3, snapshot.Activity.Items.Count);
        var pull = snapshot.Activity.Items[0];
        Assert.Equal("https://github.com/octocat/tray/pull/42", pull.Url.AbsoluteUri);
        Assert.Equal("#42 Improve tray", pull.Title);
        Assert.Equal("2026-09-14T12:30:00+00:00", pull.UpdatedAt.ToString("s") + pull.UpdatedAt.ToString("zzz"));
        Assert.Equal(3, pull.PullRequestActivity!.EventCount);
        Assert.Equal("Merged a pull request", pull.PullRequestActivity.LatestAction);
        Assert.Equal(PullRequestState.Merged, pull.PullRequest!.State);
        Assert.Equal(CheckRollupState.Pending, pull.PullRequest.Checks.State);
        Assert.Equal("Pushed commits", snapshot.Activity.Items[1].Title);
        Assert.Null(snapshot.Activity.Items[1].PullRequest);
        Assert.Equal("Opened an issue", snapshot.Activity.Items[2].Title);
        Assert.Null(snapshot.Activity.Items[2].PullRequestActivity);
        Assert.Single(api.ActivityQueries);
        Assert.Contains("pullRequest(number: 42)", api.ActivityQueries[0]);
        Assert.DoesNotContain("pullRequest(number: 7)", api.ActivityQueries[0]);
    }

    [Fact]
    public async Task RepositoryAndNumberIdentifyTheCardNotJustNumberOrAction()
    {
        var api = new ActivityApi
        {
            Events = JsonNode.Parse("""
                [
                  {"id":"a","type":"PullRequestEvent","repo":{"name":"octocat/tray"},"payload":{"action":"opened","pull_request":{"number":42}},"created_at":"2026-09-14T12:30:00Z"},
                  {"id":"b","type":"PullRequestEvent","repo":{"name":"octocat/other"},"payload":{"action":"closed","pull_request":{"number":42}},"created_at":"2026-09-14T12:00:00Z"}
                ]
                """)!.AsArray()
        };
        api.Batch["data"]!["pr0"]!["pullRequest"]!["state"] = "OPEN";
        var other = PullRequestTestData.Pull(PullRequestTestData.Response(revision: "other"));
        other["state"] = "CLOSED";
        api.Batch["data"]!["pr1"] = new JsonObject { ["pullRequest"] = other.DeepClone() };
        var snapshot = await new DashboardService(api).RefreshAsync();
        Assert.Null(snapshot.Activity.Error);
        Assert.Equal(2, snapshot.Activity.Items.Count);
        Assert.Equal(["octocat/tray", "octocat/other"], snapshot.Activity.Items.Select(item => item.Repository));
        Assert.Equal([PullRequestState.Open, PullRequestState.Closed], snapshot.Activity.Items.Select(item => item.PullRequest!.State));
        Assert.All(snapshot.Activity.Items, item => Assert.Equal(1, item.PullRequestActivity!.EventCount));
    }

    [Fact]
    public async Task DuplicateEventIdsDoNotInflateGroupedCounts()
    {
        var api = new ActivityApi();
        api.Events.Add(api.Events[0]!.DeepClone());
        var snapshot = await new DashboardService(api).RefreshAsync();
        Assert.Equal(3, snapshot.Activity.Items[0].PullRequestActivity!.EventCount);
    }

    [Fact]
    public async Task NonPrActivityDoesNotMakeAnAdditionalGraphQlRequest()
    {
        var api = new ActivityApi { Events = new JsonArray(JsonNode.Parse("""
            {"id":"p","type":"PushEvent","repo":{"name":"octocat/tray"},"payload":{"ref":"refs/heads/main"},"created_at":"2026-09-14T12:00:00Z"}
            """)) };
        var snapshot = await new DashboardService(api).RefreshAsync();
        Assert.Single(snapshot.Activity.Items);
        Assert.Empty(api.ActivityQueries);
        Assert.Null(snapshot.Activity.Error);
    }

    [Fact]
    public async Task GroupedCardsRetainLastSuccessWhenCurrentDetailsCannotBeLoaded()
    {
        var api = new ActivityApi();
        var service = new DashboardService(api);
        var previous = await service.RefreshAsync();
        Assert.Null(previous.Activity.Error);
        api.Batch = new JsonObject { ["data"] = null, ["errors"] = new JsonArray(new JsonObject { ["message"] = "forbidden" }) };
        var next = await service.RefreshAsync(previous);
        Assert.True(next.Activity.IsStale);
        Assert.Same(previous.Activity.Items, next.Activity.Items);
        Assert.Equal(previous.Activity.UpdatedAt, next.Activity.UpdatedAt);
        Assert.Null(next.PullRequests.Error);
    }

    [Fact]
    public async Task BatchViewerMismatchRejectsTheEntireDashboard()
    {
        var api = new ActivityApi();
        api.Batch["data"]!["viewer"]!["login"] = "another-user";
        await Assert.ThrowsAsync<GitHubAccountChangedException>(() => new DashboardService(api).RefreshAsync());
    }

    [Fact]
    public async Task MissingPrDetailsAreUnavailableNotFabricatedCards()
    {
        var api = new ActivityApi();
        api.Batch["data"]!["pr0"]!["pullRequest"] = null;
        var snapshot = await new DashboardService(api).RefreshAsync();
        Assert.NotNull(snapshot.Activity.Error);
        Assert.Empty(snapshot.Activity.Items);
        Assert.Null(snapshot.Activity.UpdatedAt);
    }

    private sealed class ActivityApi : IGitHubApi
    {
        internal JsonArray Events { get; set; } = JsonNode.Parse("""
            [
              {"id":"review","type":"PullRequestReviewEvent","repo":{"name":"octocat/tray"},"payload":{"action":"created","pull_request":{"number":42},"review":{"html_url":"https://github.com/octocat/tray/pull/42#pullrequestreview-1"}},"created_at":"2026-09-14T10:00:00Z"},
              {"id":"comment","type":"IssueCommentEvent","repo":{"name":"octocat/tray"},"payload":{"action":"created","issue":{"number":42,"pull_request":{},"html_url":"https://github.com/octocat/tray/pull/42"},"comment":{"html_url":"https://github.com/octocat/tray/pull/42#issuecomment-1"}},"created_at":"2026-09-14T11:00:00Z"},
              {"id":"push","type":"PushEvent","repo":{"name":"octocat/tray"},"payload":{"ref":"refs/heads/main"},"created_at":"2026-09-14T12:00:00Z"},
              {"id":"merge","type":"PullRequestEvent","repo":{"name":"octocat/tray"},"payload":{"action":"merged","number":42,"pull_request":{"number":42}},"created_at":"2026-09-14T12:30:00Z"},
              {"id":"issue","type":"IssuesEvent","repo":{"name":"octocat/tray"},"payload":{"action":"opened","issue":{"number":7,"html_url":"https://github.com/octocat/tray/issues/7"}},"created_at":"2026-09-14T09:00:00Z"}
            ]
            """)!.AsArray();

        internal JsonObject Batch { get; set; } = MakeBatch();
        internal List<string> ActivityQueries { get; } = [];

        private static JsonObject MakeBatch()
        {
            var pull = PullRequestTestData.Pull(PullRequestTestData.Response()).DeepClone();
            pull["state"] = "MERGED";
            return new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["viewer"] = new JsonObject { ["login"] = "octocat" },
                    ["pr0"] = new JsonObject { ["pullRequest"] = pull }
                }
            };
        }

        public Task<string> GetAsync(string endpoint, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(endpoint == "user"
                ? """{"login":"octocat","html_url":"https://github.com/octocat"}"""
                : endpoint.StartsWith("users/", StringComparison.Ordinal) ? Events.ToJsonString() : "[]");
        }

        public Task<string> QueryAsync(string query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (query.Contains("query ActivityPullRequests", StringComparison.Ordinal))
            {
                ActivityQueries.Add(query);
                return Task.FromResult(Batch.ToJsonString());
            }
            return Task.FromResult(query.Contains("contributionsCollection", StringComparison.Ordinal)
                ? ContributionTestData.Response().ToJsonString()
                : PullRequestTestData.Empty().ToJsonString());
        }
    }
}
