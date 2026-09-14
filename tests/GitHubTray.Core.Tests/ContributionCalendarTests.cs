using System.Text.Json;
using System.Text.Json.Nodes;
using GitHubTray.Core;

namespace GitHubTray.Core.Tests;

public sealed class ContributionCalendarTests
{
    [Fact]
    public async Task LoadsTheReturnedYearIncludingLeapDayAndPartialBoundaryWeeks()
    {
        var api = new FakeApi();
        var started = DateTimeOffset.UtcNow;

        var snapshot = await new DashboardService(api).RefreshAsync();

        var section = snapshot.Contributions;
        var calendar = Assert.IsType<ContributionCalendar>(section.Calendar);
        Assert.Equal(115, calendar.TotalContributions);
        Assert.Equal(53, calendar.Weeks.Count);
        Assert.Equal(4, calendar.Weeks[0].Days.Count);
        Assert.Equal(6, calendar.Weeks[^1].Days.Count);
        var days = calendar.Weeks.SelectMany(week => week.Days).ToArray();
        Assert.Equal(367, days.Length);
        Assert.Equal(new DateOnly(2023, 3, 1), days[0].Date);
        Assert.Equal(new DateOnly(2024, 3, 1), days[^1].Date);
        Assert.Contains(days, day => day.Date == new DateOnly(2024, 2, 29));
        Assert.Equal([0, 11, 1, 99, 4], days.Take(5).Select(day => day.Count));
        Assert.Equal([ContributionLevel.None, ContributionLevel.First, ContributionLevel.Second,
            ContributionLevel.Third, ContributionLevel.Fourth], days.Take(5).Select(day => day.Level));
        Assert.InRange(section.UpdatedAt!.Value, started, DateTimeOffset.UtcNow);
        Assert.Null(section.Error);
        Assert.False(section.IsStale);
        AssertOtherSectionsLoaded(snapshot);
        Assert.Equal(5, api.Endpoints.Count);
        Assert.Equal("user", api.Endpoints[0]);
        Assert.Equal(
            "queryContributionCalendar{viewer{logincontributionsCollection{contributionCalendar{totalContributionsweeks{firstDaycontributionDays{dateweekdaycontributionCountcontributionLevel}}}}}}",
            string.Concat(Assert.Single(api.Queries).Where(character => !char.IsWhiteSpace(character))));
    }

    [Fact]
    public async Task ARealAllZeroCalendarWithFiftyFourWeeksIsSuccessfulNotUnavailable()
    {
        var api = new FakeApi
        {
            Response = ContributionTestData.Response(from: new DateOnly(2025, 3, 1),
                to: new DateOnly(2026, 3, 1), allZero: true).ToJsonString()
        };

        var section = (await new DashboardService(api).RefreshAsync()).Contributions;

        Assert.Null(section.Error);
        Assert.NotNull(section.UpdatedAt);
        Assert.Equal(0, section.Calendar!.TotalContributions);
        Assert.Equal(54, section.Calendar.Weeks.Count);
        var days = section.Calendar.Weeks.SelectMany(week => week.Days).ToArray();
        Assert.Equal(366, days.Length);
        Assert.All(days, day =>
        {
            Assert.Equal(0, day.Count);
            Assert.Equal(ContributionLevel.None, day.Level);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GraphQlErrorsRejectEvenPartialDataWithoutDisablingTheRestSections(bool includeData)
    {
        var response = includeData ? ContributionTestData.Response() : new JsonObject { ["data"] = null };
        response["errors"] = new JsonArray(new JsonObject { ["message"] = "private/repository fixture-secret", ["type"] = "FORBIDDEN" });

        var snapshot = await new DashboardService(new FakeApi { Response = response.ToJsonString() }).RefreshAsync();

        Assert.Null(snapshot.Contributions.Calendar);
        Assert.Null(snapshot.Contributions.UpdatedAt);
        Assert.False(snapshot.Contributions.IsStale);
        Assert.Equal("GitHub could not load the contribution calendar. Check your gh account permissions and refresh.",
            snapshot.Contributions.Error);
        AssertOtherSectionsLoaded(snapshot);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("transport")]
    [InlineData("graphql")]
    public async Task FailedRefreshRetainsOnlyTheSameAccountsLastSuccessfulCalendar(string failure)
    {
        var api = new FakeApi();
        var service = new DashboardService(api);
        var previous = await service.RefreshAsync();
        switch (failure)
        {
            case "malformed":
                api.Response = "{}";
                break;
            case "transport":
                api.QueryFailure = new GitHubException("Safe offline message");
                break;
            case "graphql":
                api.Response = """{"errors":[{"message":"fixture-secret"}],"data":null}""";
                break;
        }

        var snapshot = await service.RefreshAsync(previous);

        Assert.Same(previous.Contributions.Calendar, snapshot.Contributions.Calendar);
        Assert.Equal(previous.Contributions.UpdatedAt, snapshot.Contributions.UpdatedAt);
        Assert.True(snapshot.Contributions.IsStale);
        Assert.NotNull(snapshot.Contributions.Error);
        Assert.DoesNotContain("fixture-secret", snapshot.Contributions.Error);
        AssertOtherSectionsLoaded(snapshot);

        api.QueryFailure = null;
        api.Response = ContributionTestData.Response(allZero: true).ToJsonString();
        var recovered = await service.RefreshAsync(snapshot);
        Assert.Null(recovered.Contributions.Error);
        Assert.False(recovered.Contributions.IsStale);
        Assert.NotSame(previous.Contributions.Calendar, recovered.Contributions.Calendar);
        Assert.Equal(0, recovered.Contributions.Calendar!.TotalContributions);
        Assert.True(recovered.Contributions.UpdatedAt >= previous.Contributions.UpdatedAt);
    }

    [Fact]
    public async Task AccountSwitchInvalidatesEveryPreviouslyLoadedSection()
    {
        var api = new FakeApi();
        var service = new DashboardService(api);
        var previous = await service.RefreshAsync();
        api.Login = "different-account";
        api.FailRestSections = true;
        api.QueryFailure = new GitHubException("Safe offline message");

        var snapshot = await service.RefreshAsync(previous);

        Assert.Equal("different-account", snapshot.User.Login);
        Assert.Null(snapshot.Contributions.Calendar);
        Assert.Null(snapshot.Contributions.UpdatedAt);
        Assert.False(snapshot.Contributions.IsStale);
        Assert.NotNull(snapshot.Contributions.Error);
        Assert.All(RestSections(snapshot), section =>
        {
            Assert.Empty(section.Items);
            Assert.Null(section.UpdatedAt);
            Assert.NotNull(section.Error);
            Assert.False(section.IsStale);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ViewerMismatchNeverPublishesAnotherAccountsCalendar(bool hasPrevious)
    {
        var api = new FakeApi();
        var service = new DashboardService(api);
        var previous = hasPrevious ? await service.RefreshAsync() : null;
        api.Response = ContributionTestData.Response("other-viewer", allZero: true).ToJsonString();

        var snapshot = await service.RefreshAsync(previous);

        Assert.Same(previous?.Contributions.Calendar, snapshot.Contributions.Calendar);
        Assert.Equal(previous?.Contributions.UpdatedAt, snapshot.Contributions.UpdatedAt);
        Assert.Equal(hasPrevious, snapshot.Contributions.IsStale);
        Assert.Equal("The GitHub account changed while loading contributions. Refresh again to load the current account.",
            snapshot.Contributions.Error);
        AssertOtherSectionsLoaded(snapshot);
    }

    [Fact]
    public async Task ViewerLoginComparisonIsCaseInsensitive()
    {
        var api = new FakeApi { Response = ContributionTestData.Response("OCTOCAT").ToJsonString() };
        var snapshot = await new DashboardService(api).RefreshAsync();
        Assert.NotNull(snapshot.Contributions.Calendar);
        Assert.Null(snapshot.Contributions.Error);
    }

    [Fact]
    public async Task FailedUserLookupDoesNotRequestGraphQl()
    {
        var api = new FakeApi { FailUser = true };

        await Assert.ThrowsAsync<GitHubException>(() => new DashboardService(api).RefreshAsync());

        Assert.Equal(["user"], api.Endpoints);
        Assert.Empty(api.Queries);
    }

    [Fact]
    public async Task CancellationDuringGraphQlPropagatesInsteadOfReturningStaleData()
    {
        var api = new FakeApi();
        var service = new DashboardService(api);
        var previous = await service.RefreshAsync();
        var queryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.BeforeQuery = async cancellationToken =>
        {
            queryStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        using var cancellation = new CancellationTokenSource();
        var refresh = service.RefreshAsync(previous, cancellation.Token);
        await queryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => refresh.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task AllFiveSectionsStartInParallelAfterTheUserHasResolved()
    {
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var api = new FakeApi
        {
            BeforeSection = async cancellationToken =>
            {
                if (Interlocked.Increment(ref started) == 5)
                {
                    allStarted.SetResult();
                }
                await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
        };

        var snapshot = await new DashboardService(api).RefreshAsync();

        Assert.Equal(5, started);
        Assert.Equal("user", api.Endpoints[0]);
        Assert.NotNull(snapshot.Contributions.Calendar);
        AssertOtherSectionsLoaded(snapshot);
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("missing-data")]
    [InlineData("null-viewer")]
    [InlineData("missing-calendar")]
    [InlineData("empty-weeks")]
    [InlineData("null-weeks")]
    [InlineData("too-many-weeks")]
    [InlineData("empty-days")]
    [InlineData("too-many-days")]
    [InlineData("malformed-date")]
    [InlineData("non-iso-date")]
    [InlineData("impossible-date")]
    [InlineData("unordered-days")]
    [InlineData("duplicate-date")]
    [InlineData("date-gap")]
    [InlineData("wrong-weekday")]
    [InlineData("weekday-out-of-range")]
    [InlineData("wrong-first-day")]
    [InlineData("outside-week")]
    [InlineData("unordered-weeks")]
    [InlineData("interior-partial-week")]
    [InlineData("negative-count")]
    [InlineData("fractional-count")]
    [InlineData("overflow-count")]
    [InlineData("negative-total")]
    [InlineData("inconsistent-total")]
    [InlineData("sum-overflow")]
    [InlineData("unknown-level")]
    [InlineData("wrong-case-level")]
    [InlineData("zero-with-color")]
    [InlineData("positive-with-no-color")]
    [InlineData("future-date")]
    [InlineData("malformed-errors")]
    [InlineData("missing-day-field")]
    public async Task MalformedCalendarsAreUnavailableRatherThanFabricatedEmptySuccess(string defect)
    {
        var api = new FakeApi { Response = MalformedResponse(defect) };

        var snapshot = await new DashboardService(api).RefreshAsync();

        Assert.Null(snapshot.Contributions.Calendar);
        Assert.Null(snapshot.Contributions.UpdatedAt);
        Assert.False(snapshot.Contributions.IsStale);
        Assert.Equal("GitHub returned an unexpected contribution calendar. Refresh again or update GitHub Tray if this persists.",
            snapshot.Contributions.Error);
        AssertOtherSectionsLoaded(snapshot);
    }

    private static string MalformedResponse(string defect)
    {
        var response = ContributionTestData.Response();
        var calendar = ContributionTestData.Calendar(response);
        var weeks = calendar["weeks"]!.AsArray();
        var days = weeks[0]!["contributionDays"]!.AsArray();
        var day = days[0]!.AsObject();
        switch (defect)
        {
            case "invalid-json": return "not-json";
            case "missing-data": return "{}";
            case "null-viewer": response["data"]!["viewer"] = null; break;
            case "missing-calendar": response["data"]!["viewer"]!["contributionsCollection"]!.AsObject().Remove("contributionCalendar"); break;
            case "empty-weeks": calendar["weeks"] = new JsonArray(); break;
            case "null-weeks": calendar["weeks"] = null; break;
            case "too-many-weeks":
                while (weeks.Count < 55) weeks.Add(weeks[^1]!.DeepClone());
                break;
            case "empty-days": weeks[0]!["contributionDays"] = new JsonArray(); break;
            case "too-many-days":
                while (days.Count < 8) days.Add(day.DeepClone());
                break;
            case "malformed-date": day["date"] = "not-a-date"; break;
            case "non-iso-date": day["date"] = "2023-3-1"; break;
            case "impossible-date": day["date"] = "2023-02-29"; break;
            case "unordered-days":
                var first = days[0]!.DeepClone();
                days[0] = days[1]!.DeepClone();
                days[1] = first;
                break;
            case "duplicate-date": days[1] = day.DeepClone(); break;
            case "date-gap": days[1]!["date"] = "2023-03-03"; days[1]!["weekday"] = 5; break;
            case "wrong-weekday": day["weekday"] = 2; break;
            case "weekday-out-of-range": day["weekday"] = 7; break;
            case "wrong-first-day": weeks[0]!["firstDay"] = "2023-02-28"; break;
            case "outside-week": days[^1]!["date"] = "2023-03-05"; days[^1]!["weekday"] = 0; break;
            case "unordered-weeks":
                var second = weeks[1]!.DeepClone();
                weeks[1] = weeks[2]!.DeepClone();
                weeks[2] = second;
                break;
            case "interior-partial-week": weeks[1]!["contributionDays"]!.AsArray().RemoveAt(6); break;
            case "negative-count": day["contributionCount"] = -1; break;
            case "fractional-count": day["contributionCount"] = 0.5; break;
            case "overflow-count": day["contributionCount"] = (long)int.MaxValue + 1; break;
            case "negative-total": calendar["totalContributions"] = -1; break;
            case "inconsistent-total": calendar["totalContributions"] = 116; break;
            case "sum-overflow":
                days[1]!["contributionCount"] = int.MaxValue;
                days[2]!["contributionCount"] = int.MaxValue;
                calendar["totalContributions"] = 0;
                break;
            case "unknown-level": day["contributionLevel"] = "FIFTH_QUARTILE"; break;
            case "wrong-case-level": day["contributionLevel"] = "none"; break;
            case "zero-with-color": day["contributionLevel"] = "FIRST_QUARTILE"; break;
            case "positive-with-no-color": days[1]!["contributionLevel"] = "NONE"; break;
            case "future-date":
                var future = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
                return ContributionTestData.Response(from: future, to: future, allZero: true).ToJsonString();
            case "malformed-errors": response["errors"] = new JsonObject(); break;
            case "missing-day-field": day.Remove("contributionCount"); break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }
        return response.ToJsonString();
    }

    private static DashboardSection[] RestSections(DashboardSnapshot snapshot) =>
        [snapshot.Activity, snapshot.PullRequests, snapshot.ReviewRequests, snapshot.Repositories];

    private static void AssertOtherSectionsLoaded(DashboardSnapshot snapshot) =>
        Assert.All(RestSections(snapshot), section =>
        {
            Assert.Null(section.Error);
            Assert.NotNull(section.UpdatedAt);
            Assert.False(section.IsStale);
        });

    private sealed class FakeApi : IGitHubApi
    {
        public string Login { get; set; } = "octocat";
        public string Response { get; set; } = ContributionTestData.Response().ToJsonString();
        public bool FailUser { get; set; }
        public bool FailRestSections { get; set; }
        public GitHubException? QueryFailure { get; set; }
        public Func<CancellationToken, Task>? BeforeSection { get; init; }
        public Func<CancellationToken, Task>? BeforeQuery { get; set; }
        public List<string> Endpoints { get; } = [];
        public List<string> Queries { get; } = [];

        public async Task<string> GetAsync(string endpoint, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Endpoints.Add(endpoint);
            if (endpoint == "user")
            {
                if (FailUser) throw new GitHubException("Signed out");
                return JsonSerializer.Serialize(new { login = Login, name = Login, html_url = $"https://github.com/{Login}" });
            }
            if (BeforeSection is not null) await BeforeSection(cancellationToken);
            if (FailRestSections) throw new GitHubException("Safe offline message");
            if (endpoint.StartsWith("search/issues?", StringComparison.Ordinal)) return """{"items":[]}""";
            if (endpoint.StartsWith("users/", StringComparison.Ordinal) || endpoint.StartsWith("user/repos?", StringComparison.Ordinal)) return "[]";
            throw new InvalidOperationException($"Unexpected endpoint: {endpoint}");
        }

        public async Task<string> QueryAsync(string query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Contains("user", Endpoints);
            Queries.Add(query);
            if (BeforeSection is not null) await BeforeSection(cancellationToken);
            if (BeforeQuery is not null) await BeforeQuery(cancellationToken);
            if (QueryFailure is not null) throw QueryFailure;
            return Response;
        }
    }
}
