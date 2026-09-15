using System.Text.Json;
using System.Text.Json.Nodes;
using GitHubTray.Core;

namespace GitHubTray.Core.Tests;

public sealed class PullRequestTests
{
    [Fact]
    public void ParsesRichMetadataAndChecksFromTheLatestCommit()
    {
        var item = Parse(PullRequestTestData.Response());
        var pull = Assert.IsType<PullRequestDetails>(item.PullRequest);
        Assert.Equal("Improve tray", pull.Title);
        Assert.Equal(42, pull.Number);
        Assert.Equal("octocat", pull.AuthorLogin);
        Assert.Equal("avatars.githubusercontent.com", pull.AuthorAvatarUrl!.Host);
        Assert.Equal("fix/tray", pull.HeadRefName);
        Assert.Equal("main", pull.BaseRefName);
        Assert.Equal("REVIEW_REQUIRED", pull.ReviewDecision);
        Assert.Equal(2, pull.CommentCount);
        Assert.Equal("enhancement", Assert.Single(pull.Labels).Name);
        Assert.Equal(1, pull.LabelCount);
        Assert.Equal(CheckRollupState.Pending, pull.Checks.State);
        Assert.Equal([CheckState.Running, CheckState.Pending, CheckState.Passed], pull.Checks.Items.Select(check => check.State));
        Assert.Equal(3, pull.Checks.TotalCount);
        Assert.False(pull.Checks.IsTruncated);
        Assert.Equal("0123456789012345678901234567890123456789", pull.Checks.CommitOid);
    }

    [Theory]
    [InlineData("IN_PROGRESS", null, CheckState.Running)]
    [InlineData("WAITING", null, CheckState.Pending)]
    [InlineData("QUEUED", null, CheckState.Pending)]
    [InlineData("PENDING", null, CheckState.Pending)]
    [InlineData("REQUESTED", null, CheckState.Pending)]
    [InlineData("COMPLETED", "SUCCESS", CheckState.Passed)]
    [InlineData("COMPLETED", "FAILURE", CheckState.Failed)]
    [InlineData("COMPLETED", "TIMED_OUT", CheckState.Failed)]
    [InlineData("COMPLETED", "STARTUP_FAILURE", CheckState.Failed)]
    [InlineData("COMPLETED", "ACTION_REQUIRED", CheckState.ActionRequired)]
    [InlineData("COMPLETED", "CANCELLED", CheckState.Cancelled)]
    [InlineData("COMPLETED", "NEUTRAL", CheckState.Neutral)]
    [InlineData("COMPLETED", "SKIPPED", CheckState.Skipped)]
    [InlineData("COMPLETED", null, CheckState.Unknown)]
    [InlineData("FUTURE_STATE", "SUCCESS", CheckState.Unknown)]
    public void CheckRunStatesNeverConfuseIncompleteOrNonPassingChecksWithSuccess(
        string status, string? conclusion, CheckState expected)
    {
        var response = PullRequestTestData.Response();
        var check = PullRequestTestData.Contexts(response)["nodes"]![0]!;
        check["status"] = status;
        check["conclusion"] = conclusion;
        Assert.Equal(expected, Parse(response).PullRequest!.Checks.Items[0].State);
    }

    [Theory]
    [InlineData("SUCCESS", CheckState.Passed)]
    [InlineData("ERROR", CheckState.Failed)]
    [InlineData("FAILURE", CheckState.Failed)]
    [InlineData("PENDING", CheckState.Pending)]
    [InlineData("EXPECTED", CheckState.Pending)]
    [InlineData("FUTURE_STATE", CheckState.Unknown)]
    public void LegacyCommitStatusesShareTheCheckPresentation(string state, CheckState expected)
    {
        var response = PullRequestTestData.Response();
        PullRequestTestData.Contexts(response)["nodes"]![2]!["state"] = state;
        Assert.Equal(expected, Parse(response).PullRequest!.Checks.Items[2].State);
    }

    [Fact]
    public void MissingRollupAndMissingCommitAreDistinctFromPassedChecks()
    {
        var response = PullRequestTestData.Response();
        PullRequestTestData.Commit(response)["statusCheckRollup"] = null;
        var checks = Parse(response).PullRequest!.Checks;
        Assert.Equal(CheckRollupState.NoChecks, checks.State);
        Assert.Empty(checks.Items);

        PullRequestTestData.Pull(response)["commits"]!["nodes"] = new JsonArray();
        checks = Parse(response).PullRequest!.Checks;
        Assert.Equal(CheckRollupState.Unknown, checks.State);
        Assert.Null(checks.CommitOid);
    }

    [Fact]
    public void DeletedAuthorsDraftsAndNoReviewDecisionDoNotLoseThePullRequest()
    {
        var response = PullRequestTestData.Response();
        var pull = PullRequestTestData.Pull(response);
        pull["author"] = null;
        pull["isDraft"] = true;
        pull["reviewDecision"] = null;
        var parsed = Parse(response);
        Assert.Equal("Deleted user", parsed.PullRequest!.AuthorLogin);
        Assert.Null(parsed.PullRequest.AuthorAvatarUrl);
        Assert.True(parsed.PullRequest.IsDraft);
        Assert.Null(parsed.PullRequest.ReviewDecision);
        Assert.Equal("Draft pull request", parsed.Detail);
    }

    [Fact]
    public void TruncatedChecksRetainTheAuthoritativeRollupAndActualTotal()
    {
        var response = PullRequestTestData.Response();
        var contexts = PullRequestTestData.Contexts(response);
        var source = contexts["nodes"]![0]!.DeepClone();
        contexts["nodes"] = new JsonArray(Enumerable.Range(0, 100).Select(_ => source.DeepClone()).ToArray());
        contexts["totalCount"] = 150;
        PullRequestTestData.Commit(response)["statusCheckRollup"]!["state"] = "FAILURE";
        var checks = Parse(response).PullRequest!.Checks;
        Assert.Equal(CheckRollupState.Failed, checks.State);
        Assert.Equal(150, checks.TotalCount);
        Assert.Equal(100, checks.Items.Length);
        Assert.True(checks.IsTruncated);
    }

    [Theory]
    [InlineData("labels")]
    [InlineData("checks")]
    [InlineData("avatar")]
    [InlineData("label-color")]
    [InlineData("commit")]
    [InlineData("repository")]
    public void InvalidOrUnexpectedlyPartialMetadataRejectsTheSection(string invalid)
    {
        var response = PullRequestTestData.Response();
        var pull = PullRequestTestData.Pull(response);
        switch (invalid)
        {
            case "labels": pull["labels"]!["totalCount"] = 3; break;
            case "checks": PullRequestTestData.Contexts(response)["totalCount"] = 5; break;
            case "avatar": pull["author"]!["avatarUrl"] = "https://untrusted.example/avatar.png"; break;
            case "label-color": pull["labels"]!["nodes"]![0]!["color"] = "red"; break;
            case "commit": PullRequestTestData.Commit(response)["oid"] = "not-a-commit"; break;
            case "repository": pull["repository"]!["nameWithOwner"] = "other/repo"; break;
        }
        Assert.Throws<JsonException>(() => Parse(response));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GraphQlFailureRetainsTheWholeSameAccountCardAndTimestamp(bool partialData)
    {
        var api = new PullApi();
        var service = new DashboardService(api);
        var previous = await service.RefreshAsync();
        var response = partialData ? PullRequestTestData.Response() : new JsonObject { ["data"] = null };
        response["errors"] = JsonNode.Parse("""[{"message":"private sensitive backend diagnostic"}]""");
        api.Response = response;
        var snapshot = await service.RefreshAsync(previous);
        Assert.True(snapshot.PullRequests.IsStale);
        Assert.Same(previous.PullRequests.Items, snapshot.PullRequests.Items);
        Assert.Equal(previous.PullRequests.UpdatedAt, snapshot.PullRequests.UpdatedAt);
        Assert.Contains("could not load", snapshot.PullRequests.Error);
        Assert.DoesNotContain("sensitive", snapshot.PullRequests.Error);
        Assert.Null(snapshot.Activity.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentGraphQlViewerRejectsAllNewDataEvenWithPartialErrors(bool hasErrors)
    {
        var api = new PullApi { Response = PullRequestTestData.Response("another-account") };
        if (hasErrors) api.Response["errors"] = JsonNode.Parse("""[{"message":"Partial failure"}]""");
        await Assert.ThrowsAsync<GitHubAccountChangedException>(() => new DashboardService(api).RefreshAsync());
    }

    [Fact]
    public async Task AccountSwitchDoesNotRetainPreviousAccountsChecksOnFailure()
    {
        var api = new PullApi();
        var service = new DashboardService(api);
        var previous = await service.RefreshAsync();
        api.Login = "new-user";
        api.Response = new JsonObject { ["data"] = null, ["errors"] = new JsonArray(new JsonObject { ["message"] = "forbidden" }) };
        var snapshot = await service.RefreshAsync(previous);
        Assert.Empty(snapshot.PullRequests.Items);
        Assert.Null(snapshot.PullRequests.UpdatedAt);
        Assert.False(snapshot.PullRequests.IsStale);
    }

    [Fact]
    public async Task FetchesTwoBoundedQueriesRatherThanOneRequestPerPullRequest()
    {
        var api = new PullApi();
        await new DashboardService(api).RefreshAsync();
        Assert.Equal(2, api.PullQueries.Count);
        Assert.All(api.PullQueries, query =>
        {
            Assert.Contains("first: 30", query);
            Assert.Contains("commits(last: 1)", query);
            Assert.Contains("contexts(first: 100)", query);
            Assert.Contains("labels(first: 10)", query);
        });
        Assert.Contains(api.PullQueries, query => query.Contains("review-requested:octocat"));
        Assert.Contains(api.PullQueries, query => query.Contains("author:octocat"));
        Assert.DoesNotContain("is:open", api.PullQueries.Single(query => query.Contains("author:octocat")));
        Assert.Contains("is:open", api.PullQueries.Single(query => query.Contains("review-requested:octocat")));
    }

    [Theory]
    [InlineData("OPEN", PullRequestState.Open, "Open pull request")]
    [InlineData("CLOSED", PullRequestState.Closed, "Closed pull request")]
    [InlineData("MERGED", PullRequestState.Merged, "Merged pull request")]
    public void PullRequestStateIsIndependentOfCiState(string state, PullRequestState expected, string detail)
    {
        var response = PullRequestTestData.Response();
        PullRequestTestData.Pull(response)["state"] = state;
        var item = Parse(response);
        Assert.Equal(expected, item.PullRequest!.State);
        Assert.Equal(detail, item.Detail);
        Assert.Equal(CheckRollupState.Pending, item.PullRequest.Checks.State);
    }

    [Fact]
    public async Task ANewHeadReplacesTheWholeCheckSnapshotAndEmptySearchClearsCards()
    {
        var api = new PullApi();
        var service = new DashboardService(api);
        var previous = await service.RefreshAsync();
        var oldChecks = previous.PullRequests.Items[0].PullRequest!.Checks;
        PullRequestTestData.Commit(api.Response)["oid"] = new string('a', 40);
        PullRequestTestData.Commit(api.Response)["statusCheckRollup"] = null;

        var next = await service.RefreshAsync(previous);

        var checks = next.PullRequests.Items[0].PullRequest!.Checks;
        Assert.Equal(new string('a', 40), checks.CommitOid);
        Assert.Equal(CheckRollupState.NoChecks, checks.State);
        Assert.Empty(checks.Items);
        Assert.Equal(3, oldChecks.Items.Length);
        api.Response = PullRequestTestData.Empty();

        var empty = await service.RefreshAsync(next);

        Assert.Empty(empty.PullRequests.Items);
        Assert.NotNull(empty.PullRequests.UpdatedAt);
        Assert.Null(empty.PullRequests.Error);
    }

    [Fact]
    public void ExcessSearchResultsAreRejectedInsteadOfSilentlyDiscarded()
    {
        var response = PullRequestTestData.Response();
        var pull = PullRequestTestData.Pull(response).DeepClone();
        response["data"]!["search"]!["nodes"] = new JsonArray(
            Enumerable.Range(0, 31).Select(_ => pull.DeepClone()).ToArray());
        Assert.Throws<JsonException>(() => Parse(response));
    }

    private static DashboardItem Parse(JsonObject response)
    {
        using var document = JsonDocument.Parse(response.ToJsonString());
        return Assert.Single(PullRequestParser.Parse(document.RootElement, "octocat", false));
    }

    private sealed class PullApi : IGitHubApi
    {
        internal string Login { get; set; } = "octocat";
        internal JsonObject Response { get; set; } = PullRequestTestData.Response();
        internal List<string> PullQueries { get; } = [];

        public Task<string> GetAsync(string endpoint, CancellationToken cancellationToken = default) =>
            Task.FromResult(endpoint == "user"
                ? JsonSerializer.Serialize(new
                {
                    id = string.Equals(Login, "octocat", StringComparison.OrdinalIgnoreCase) ? 1 : 2,
                    login = Login,
                    html_url = $"https://github.com/{Login}"
                }) : "[]");

        public Task<string> QueryAsync(string query, CancellationToken cancellationToken = default)
        {
            if (query.Contains("query PullRequests", StringComparison.Ordinal))
            {
                PullQueries.Add(query);
                return Task.FromResult(Response.ToJsonString());
            }
            return Task.FromResult(ContributionTestData.Response(Login).ToJsonString());
        }
    }
}
