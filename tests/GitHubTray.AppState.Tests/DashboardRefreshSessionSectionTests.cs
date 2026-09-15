using System.Collections;
using GitHubTray.Core;
using GitHubTray.Core.Tests;

namespace GitHubTray.AppState.Tests;

public sealed class DashboardRefreshSessionSectionTests
{
    [Fact]
    public async Task PartialInitialFailurePublishesVerifiedSuccessesAndMarksFailedSectionsUnavailable()
    {
        await using var fixture = new RefreshSessionFixture();
        var responses = RefreshResponses.Success();
        responses.Activity = responses.Activity with { Failure = new GitHubException("Activity unavailable") };
        responses.ReviewRequests = new ApiReply("not-json");
        responses.Contributions = new ApiReply("""{"errors":[{"message":"Contribution permission denied"}]}""");
        fixture.Api.Use(responses);

        await fixture.RefreshAsync();

        var state = fixture.Session.State;
        Assert.True(state.IsAccountVerified);
        Assert.False(state.IsRefreshing);
        Assert.Null(state.Error);
        Assert.Equal("octocat", state.LastKnownLogin);
        var snapshot = Assert.IsType<DashboardSnapshot>(state.Snapshot);
        foreach (var section in new[] { snapshot.Activity, snapshot.ReviewRequests })
        {
            Assert.Empty(section.Items);
            Assert.Null(section.UpdatedAt);
            Assert.False(section.IsStale);
            Assert.False(string.IsNullOrWhiteSpace(section.Error));
        }
        Assert.Equal("Activity unavailable", snapshot.Activity.Error);
        Assert.Contains("unexpected response", snapshot.ReviewRequests.Error);
        Assert.Equal("#42 Improve first", Assert.Single(snapshot.PullRequests.Items).Title);
        Assert.Equal("octocat/first", Assert.Single(snapshot.Repositories.Items).Title);
        Assert.NotNull(snapshot.PullRequests.UpdatedAt);
        Assert.NotNull(snapshot.Repositories.UpdatedAt);
        Assert.Null(snapshot.PullRequests.Error);
        Assert.Null(snapshot.Repositories.Error);
        Assert.Null(snapshot.Contributions.Calendar);
        Assert.Null(snapshot.Contributions.UpdatedAt);
        Assert.False(snapshot.Contributions.IsStale);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Contributions.Error));
        Assert.Equal(8, fixture.Api.Requests.Length);
    }

    [Fact]
    public async Task PartialFailureRetainsStaleDataWhileEmptySuccessesClearPreviouslyLoadedSections()
    {
        await using var fixture = new RefreshSessionFixture();
        await fixture.RefreshAsync();
        var originalState = fixture.Session.State;
        var original = SessionAssertions.Success(originalState);
        var responses = RefreshResponses.Success(revision: "next");
        responses.Activity = responses.Activity with { Failure = new GitHubException("Offline") };
        responses.ReviewRequests = new ApiReply("""{"errors":[{"message":"Partial results"}],"data":null}""");
        responses.PullRequests = new ApiReply(PullRequestTestData.Empty().ToJsonString());
        responses.Repositories = new ApiReply("[]");
        responses.Contributions = RefreshResponses.Calendar("octocat", allZero: true);
        fixture.Api.Use(responses);

        await fixture.RefreshAsync();

        var state = fixture.Session.State;
        Assert.True(state.IsAccountVerified);
        Assert.False(state.IsRefreshing);
        Assert.Null(state.Error);
        var snapshot = Assert.IsType<DashboardSnapshot>(state.Snapshot);
        Assert.Equal(original.Activity.Items.ToArray(), snapshot.Activity.Items.ToArray());
        Assert.Equal(original.Activity.UpdatedAt, snapshot.Activity.UpdatedAt);
        Assert.True(snapshot.Activity.IsStale);
        Assert.Equal("Offline", snapshot.Activity.Error);
        Assert.Equal(original.ReviewRequests.Items.ToArray(), snapshot.ReviewRequests.Items.ToArray());
        Assert.Equal(original.ReviewRequests.UpdatedAt, snapshot.ReviewRequests.UpdatedAt);
        Assert.True(snapshot.ReviewRequests.IsStale);
        Assert.Contains("could not load", snapshot.ReviewRequests.Error);
        foreach (var section in new[] { snapshot.PullRequests, snapshot.Repositories })
        {
            Assert.Empty(section.Items);
            Assert.NotNull(section.UpdatedAt);
            Assert.Null(section.Error);
            Assert.False(section.IsStale);
        }
        var calendar = Assert.IsType<ContributionCalendar>(snapshot.Contributions.Calendar);
        Assert.Equal(0, calendar.TotalContributions);
        Assert.NotEmpty(calendar.Weeks);
        Assert.All(calendar.Weeks.SelectMany(week => week.Days), day =>
        {
            Assert.Equal(0, day.Count);
            Assert.Equal(ContributionLevel.None, day.Level);
        });
        Assert.NotNull(snapshot.Contributions.UpdatedAt);
        Assert.Null(snapshot.Contributions.Error);
        Assert.False(snapshot.Contributions.IsStale);
        SessionAssertions.Success(originalState);
        Assert.Equal(16, fixture.Api.Requests.Length);
    }

    [Fact]
    public async Task MalformedCalendarRetainsOnlyTheCalendarWhileOtherSectionsAdvance()
    {
        await using var fixture = new RefreshSessionFixture();
        await fixture.RefreshAsync();
        var original = SessionAssertions.Success(fixture.Session.State);
        var responses = RefreshResponses.Success(revision: "new");
        responses.Contributions = new ApiReply("{}");
        fixture.Api.Use(responses);

        await fixture.RefreshAsync();

        Assert.Null(fixture.Session.State.Error);
        Assert.True(fixture.Session.State.IsAccountVerified);
        var snapshot = Assert.IsType<DashboardSnapshot>(fixture.Session.State.Snapshot);
        Assert.Equal("new", Assert.Single(snapshot.Activity.Items).Id);
        Assert.Equal("#42 Improve new", Assert.Single(snapshot.PullRequests.Items).Title);
        Assert.Equal("octocat/new", Assert.Single(snapshot.Repositories.Items).Title);
        Assert.All(SessionAssertions.Sections(snapshot), section =>
        {
            Assert.Null(section.Error);
            Assert.False(section.IsStale);
            Assert.NotNull(section.UpdatedAt);
        });
        Assert.True(snapshot.Contributions.IsStale);
        Assert.Contains("unexpected contribution calendar", snapshot.Contributions.Error);
        Assert.Equal(original.Contributions.UpdatedAt, snapshot.Contributions.UpdatedAt);
        var retained = Assert.IsType<ContributionCalendar>(snapshot.Contributions.Calendar);
        Assert.Equal(115, retained.TotalContributions);
        Assert.Equal(original.Contributions.Calendar!.Weeks.SelectMany(week => week.Days),
            retained.Weeks.SelectMany(week => week.Days));
        Assert.Equal(16, fixture.Api.Requests.Length);
    }

    [Fact]
    public async Task PublishedCollectionsAreDeeplyReadOnlyAndPriorStatesRemainUnchangedAcrossRefreshAndStop()
    {
        await using var fixture = new RefreshSessionFixture();
        await fixture.RefreshAsync();
        var published = fixture.Session.State;
        var snapshot = SessionAssertions.Success(published);
        foreach (var section in SessionAssertions.Sections(snapshot))
        {
            AssertReadOnly(section.Items);
        }
        var pull = Assert.IsType<PullRequestDetails>(snapshot.PullRequests.Items[0].PullRequest);
        AssertReadOnly<PullRequestLabel>(pull.Labels);
        AssertReadOnly<PullRequestCheck>(pull.Checks.Items);
        var calendar = Assert.IsType<ContributionCalendar>(snapshot.Contributions.Calendar);
        AssertReadOnly(calendar.Weeks);
        Assert.All(calendar.Weeks, week => AssertReadOnly(week.Days));
        Assert.Equal(new DateOnly(2023, 3, 1), calendar.Weeks[0].Days[0].Date);
        Assert.Equal(11, calendar.Weeks[0].Days[1].Count);
        Assert.Equal(ContributionLevel.First, calendar.Weeks[0].Days[1].Level);

        var gate = fixture.Gate();
        var responses = RefreshResponses.Success(revision: "replacement");
        responses.InitialUser = responses.InitialUser with { Gate = gate };
        responses.Contributions = RefreshResponses.Calendar("octocat", allZero: true);
        fixture.Api.Use(responses);
        var refresh = fixture.Session.RefreshAsync();
        var loading = fixture.Session.State;
        Assert.True(loading.IsRefreshing);
        await gate.EnteredAsync();
        gate.Release();
        await refresh.WaitAsync(RefreshSessionFixture.Timeout);
        var replacement = fixture.Session.State;
        Assert.Equal("replacement", Assert.Single(replacement.Snapshot!.Activity.Items).Id);
        Assert.Equal(0, replacement.Snapshot.Contributions.Calendar!.TotalContributions);
        Assert.False(replacement.IsRefreshing);

        await fixture.Session.ShutdownAsync().WaitAsync(RefreshSessionFixture.Timeout);

        SessionAssertions.Success(published);
        Assert.True(loading.IsRefreshing);
        Assert.False(loading.IsStopping);
        Assert.Equal("first", Assert.Single(loading.Snapshot!.Activity.Items).Id);
        Assert.Equal(115, loading.Snapshot.Contributions.Calendar!.TotalContributions);
        Assert.False(replacement.IsStopping);
        Assert.Equal("replacement", Assert.Single(replacement.Snapshot.Activity.Items).Id);
        Assert.Equal(0, replacement.Snapshot.Contributions.Calendar.TotalContributions);
        Assert.Equal(11, calendar.Weeks[0].Days[1].Count);
        Assert.True(fixture.Session.State.IsStopping);
    }

    private static void AssertReadOnly<T>(IReadOnlyList<T> items)
    {
        Assert.NotEmpty(items);
        var original = items.ToArray();
        if (items is IList<T> generic)
        {
            Assert.True(generic.IsReadOnly);
            // Arrays report IsReadOnly but still permit index assignment.
            Assert.Throws<NotSupportedException>(() => generic[0] = items[0]);
            Assert.Throws<NotSupportedException>(() => generic.Add(items[0]));
            Assert.Throws<NotSupportedException>(() => generic.RemoveAt(0));
            Assert.Throws<NotSupportedException>(() => generic.Clear());
        }
        if (items is IList nonGeneric)
        {
            Assert.True(nonGeneric.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => nonGeneric[0] = items[0]);
            Assert.Throws<NotSupportedException>(() => nonGeneric.Add(items[0]));
            Assert.Throws<NotSupportedException>(() => nonGeneric.RemoveAt(0));
            Assert.Throws<NotSupportedException>(() => nonGeneric.Clear());
        }
        Assert.Equal(original, items.ToArray());
    }
}
