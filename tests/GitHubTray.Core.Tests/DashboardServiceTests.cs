using System.Text.Json;
using GitHubTray.Core;

namespace GitHubTray.Core.Tests;

public sealed class DashboardServiceTests
{
    [Fact]
    public async Task RefreshLoadsAllFourSectionsForTheAuthenticatedUser()
    {
        var api = new FakeApi();
        var snapshot = await new DashboardService(api).RefreshAsync();

        Assert.Equal("octocat", snapshot.User.Login);
        Assert.Equal("The Octocat", snapshot.User.DisplayName);
        Assert.Equal("Pushed commits", Assert.Single(snapshot.Activity.Items).Title);
        Assert.Equal("main", snapshot.Activity.Items[0].Detail);
        Assert.Equal("#42 Improve tray", Assert.Single(snapshot.PullRequests.Items).Title);
        Assert.Equal("Review requested", Assert.Single(snapshot.ReviewRequests.Items).Detail);
        Assert.Equal("octocat/tray", Assert.Single(snapshot.Repositories.Items).Title);
        Assert.Contains(api.Queries, query => query.Contains("author:octocat"));
        Assert.Contains(api.Queries, query => query.Contains("review-requested:octocat"));
        Assert.NotNull(snapshot.Copilot.Usage);
        Assert.All(api.Endpoints.Where(endpoint => endpoint != "user" && endpoint != CopilotUsageParser.Endpoint),
            endpoint => Assert.Contains("per_page=30", endpoint));
    }

    [Fact]
    public async Task FailedSectionRetainsLastSuccessWithoutBlockingOtherSections()
    {
        var api = new FakeApi();
        var service = new DashboardService(api);
        var previous = await service.RefreshAsync();
        api.FailActivity = true;

        var refreshed = await service.RefreshAsync(previous);

        Assert.Same(previous.Activity.Items, refreshed.Activity.Items);
        Assert.Equal(previous.Activity.UpdatedAt, refreshed.Activity.UpdatedAt);
        Assert.True(refreshed.Activity.IsStale);
        Assert.Equal("Offline", refreshed.Activity.Error);
        Assert.Null(refreshed.PullRequests.Error);
        Assert.False(refreshed.PullRequests.IsStale);
        Assert.True(refreshed.PullRequests.UpdatedAt >= previous.PullRequests.UpdatedAt);
    }

    [Fact]
    public async Task AccountSwitchNeverRetainsPreviousAccountsPrivateData()
    {
        var api = new FakeApi();
        var service = new DashboardService(api);
        var previous = await service.RefreshAsync();
        api.Login = "different-user";
        api.FailActivity = true;

        var refreshed = await service.RefreshAsync(previous);

        Assert.Equal("different-user", refreshed.User.Login);
        Assert.Empty(refreshed.Activity.Items);
        Assert.Null(refreshed.Activity.UpdatedAt);
        Assert.False(refreshed.Activity.IsStale);
        Assert.NotNull(refreshed.Activity.Error);
    }

    [Fact]
    public async Task FailedInitialLoadIsUnavailableRatherThanAnEmptySuccess()
    {
        var api = new FakeApi { FailActivity = true };
        var snapshot = await new DashboardService(api).RefreshAsync();
        Assert.Empty(snapshot.Activity.Items);
        Assert.Null(snapshot.Activity.UpdatedAt);
        Assert.NotNull(snapshot.Activity.Error);
    }

    [Fact]
    public async Task FailedUserLookupDoesNotRequestAccountSpecificData()
    {
        var api = new FakeApi { FailUser = true };
        await Assert.ThrowsAsync<GitHubException>(() => new DashboardService(api).RefreshAsync());
        Assert.Equal(["user"], api.Endpoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountChangeDuringRefreshRejectsTheEntireSnapshot(bool hasPrevious)
    {
        var api = new FakeApi();
        var service = new DashboardService(api);
        var previous = hasPrevious ? await service.RefreshAsync() : null;
        api.AfterCopilot = () => api.Login = "different-account";

        var error = await Assert.ThrowsAsync<GitHubAccountChangedException>(() => service.RefreshAsync(previous));

        Assert.Contains("account changed during refresh", error.Message);
        Assert.Equal("user", api.Endpoints[^1]);
        if (previous is not null)
        {
            Assert.Equal("octocat", previous.User.Login);
        }
    }

    [Fact]
    public async Task FailedFinalAccountCheckDoesNotPublishFetchedSections()
    {
        var api = new FakeApi();
        api.AfterQuery = () => api.FailUser = true;

        await Assert.ThrowsAsync<GitHubException>(() => new DashboardService(api).RefreshAsync());

        Assert.Equal(2, api.Endpoints.Count(endpoint => endpoint == "user"));
        Assert.Equal("user", api.Endpoints[^1]);
    }

    [Fact]
    public async Task FinalAccountCheckAcceptsCaseOnlyLoginChanges()
    {
        var api = new FakeApi();
        api.AfterQuery = () => api.Login = "OCTOCAT";

        var snapshot = await new DashboardService(api).RefreshAsync();

        Assert.Equal("octocat", snapshot.User.Login);
        Assert.Single(snapshot.Repositories.Items);
        Assert.Equal(2, api.Endpoints.Count(endpoint => endpoint == "user"));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("""{"login":"invalid/user","html_url":"https://github.com/user"}""")]
    public async Task MalformedIdentityIsReportedAsBoundaryFailure(string response)
    {
        var api = new FakeApi { UserResponse = response };
        var error = await Assert.ThrowsAsync<GitHubException>(() => new DashboardService(api).RefreshAsync());
        Assert.Contains("unexpected response", error.Message);
        Assert.Single(api.Endpoints);
    }

    [Theory]
    [InlineData("file:///C:/secret")]
    [InlineData("https://github.com.evil.example/octocat/tray/pull/42")]
    [InlineData("https://user:password@github.com/octocat/tray/pull/42")]
    [InlineData("http://github.com/octocat/tray/pull/42")]
    [InlineData("https://github.com:444/octocat/tray/pull/42")]
    public async Task UnsafeLinksInvalidateTheSectionInsteadOfReachingTheShell(string url)
    {
        var api = new FakeApi { PullUrl = url };
        var snapshot = await new DashboardService(api).RefreshAsync();
        Assert.Empty(snapshot.PullRequests.Items);
        Assert.Contains("unexpected response", snapshot.PullRequests.Error);
        Assert.Null(snapshot.Activity.Error);
    }

    [Fact]
    public async Task SuccessfulEmptyResponseClearsOldItems()
    {
        var api = new FakeApi();
        var service = new DashboardService(api);
        var previous = await service.RefreshAsync();
        api.ActivityResponse = "[]";
        var snapshot = await service.RefreshAsync(previous);
        Assert.Empty(snapshot.Activity.Items);
        Assert.Null(snapshot.Activity.Error);
        Assert.NotNull(snapshot.Activity.UpdatedAt);
    }

    [Fact]
    public async Task UnknownEventTypesStillHaveSafeRepositoryLinksAndTimestamps()
    {
        var api = new FakeApi
        {
            ActivityResponse = """[{"id":"new","type":"FutureEvent","repo":{"name":"org/repo"},"payload":{},"created_at":"2026-09-01T09:30:00Z"}]"""
        };
        var snapshot = await new DashboardService(api).RefreshAsync();
        var item = Assert.Single(snapshot.Activity.Items);
        Assert.Equal("Repository activity", item.Title);
        Assert.Equal("https://github.com/org/repo", item.Url.AbsoluteUri);
    }

    [Fact]
    public async Task ActivityIsBoundedEvenIfTheServerReturnsMoreItems()
    {
        var api = new FakeApi
        {
            ActivityResponse = JsonSerializer.Serialize(Enumerable.Range(0, 40).Select(index => new
            {
                id = index.ToString(),
                type = "PublicEvent",
                repo = new { name = "org/repo" },
                payload = new { },
                created_at = "2026-09-01T09:30:00Z"
            }))
        };
        var snapshot = await new DashboardService(api).RefreshAsync();
        Assert.Equal(30, snapshot.Activity.Items.Count);
    }

    [Fact]
    public async Task ActivityIsOrderedByEventTimeRatherThanIngestionOrder()
    {
        var api = new FakeApi
        {
            ActivityResponse = """
                [
                    {"id":"older","type":"PublicEvent","repo":{"name":"org/repo"},"payload":{},"created_at":"2026-09-01T09:30:00Z"},
                    {"id":"newer","type":"PublicEvent","repo":{"name":"org/repo"},"payload":{},"created_at":"2026-09-01T10:30:00Z"}
                ]
                """
        };
        var snapshot = await new DashboardService(api).RefreshAsync();
        Assert.Equal(["newer", "older"], snapshot.Activity.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task NewestEventsBeyondTheInputLimitAreRetained()
    {
        var api = new FakeApi
        {
            ActivityResponse = JsonSerializer.Serialize(Enumerable.Range(0, 40).Select(index => new
            {
                id = index.ToString(),
                type = "PublicEvent",
                repo = new { name = "org/repo" },
                payload = new { },
                created_at = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index)
            }))
        };

        var snapshot = await new DashboardService(api).RefreshAsync();

        Assert.Equal(Enumerable.Range(10, 30).Reverse().Select(index => index.ToString()),
            snapshot.Activity.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task PartialGraphQlResultsDoNotSilentlyReplaceTheLastCompleteResults()
    {
        var api = new FakeApi();
        var service = new DashboardService(api);
        var previous = await service.RefreshAsync();
        api.IncompleteSearch = true;
        var snapshot = await service.RefreshAsync(previous);
        Assert.True(snapshot.PullRequests.IsStale);
        Assert.Contains("could not load", snapshot.PullRequests.Error);
        Assert.Same(previous.PullRequests.Items, snapshot.PullRequests.Items);
    }

    [Fact]
    public async Task CancellationPropagatesInsteadOfBecomingAnOfflineResult()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DashboardService(new FakeApi()).RefreshAsync(cancellationToken: cancellation.Token));
    }

    [Theory]
    [InlineData("https://evil.example/user")]
    [InlineData("--hostname=evil.example")]
    [InlineData("/user")]
    [InlineData("")]
    public async Task CliRejectsNonRelativeOrOptionEndpoints(string endpoint)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new GitHubCliApi().GetAsync(endpoint));
    }

    private sealed class FakeApi : IGitHubApi
    {
        public List<string> Endpoints { get; } = [];
        public List<string> Queries { get; } = [];
        public string Login { get; set; } = "octocat";
        public string? UserResponse { get; set; }
        public bool FailUser { get; set; }
        public bool FailActivity { get; set; }
        public bool IncompleteSearch { get; set; }
        public Action? AfterQuery { get; set; }
        public Action? AfterCopilot { get; set; }
        public string PullUrl { get; set; } = "https://github.com/octocat/tray/pull/42";
        public string ActivityResponse { get; set; } =
            """[{"id":"1","type":"PushEvent","repo":{"name":"octocat/tray"},"payload":{"ref":"refs/heads/main"},"created_at":"2026-09-01T09:30:00Z"}]""";

        public Task<string> QueryAsync(string query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries.Add(query);
            if (query.Contains("query PullRequests", StringComparison.Ordinal))
            {
                var pulls = PullRequestTestData.Response(Login);
                PullRequestTestData.Pull(pulls)["url"] = PullUrl;
                if (IncompleteSearch)
                {
                    pulls["errors"] = System.Text.Json.Nodes.JsonNode.Parse("""[{"message":"partial response"}]""");
                }
                return Task.FromResult(pulls.ToJsonString());
            }
            var response = ContributionTestData.Response(Login).ToJsonString();
            AfterQuery?.Invoke();
            return Task.FromResult(response);
        }

        public Task<string> GetAsync(string endpoint, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Endpoints.Add(endpoint);
            if (endpoint == CopilotUsageParser.Endpoint)
            {
                var response = CopilotTestData.Response(Login).ToJsonString();
                AfterCopilot?.Invoke();
                return Task.FromResult(response);
            }
            if (endpoint == "user")
            {
                if (FailUser)
                {
                    throw new GitHubException("Signed out");
                }
                return Task.FromResult(UserResponse ?? JsonSerializer.Serialize(new
                {
                    login = Login, name = "The Octocat", html_url = $"https://github.com/{Login}"
                }));
            }
            if (endpoint.StartsWith("users/", StringComparison.Ordinal))
            {
                if (FailActivity)
                {
                    throw new GitHubException("Offline");
                }
                return Task.FromResult(ActivityResponse);
            }
            if (endpoint.StartsWith("user/repos?", StringComparison.Ordinal))
            {
                return Task.FromResult("""[{"full_name":"octocat/tray","html_url":"https://github.com/octocat/tray","description":"A native tray app","language":"C#","private":true,"archived":false,"pushed_at":null,"updated_at":"2026-09-01T10:30:00Z"}]""");
            }
            throw new InvalidOperationException($"Unexpected endpoint: {endpoint}");
        }
    }
}
