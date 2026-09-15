using GitHubTray.Core;
using GitHubTray.Core.Tests;

namespace GitHubTray.AppState.Tests;

public sealed class DashboardRefreshSessionCopilotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopilotFailureIsIsolatedAndRetainsOnlyVerifiedSameAccountUsage(bool hasPrevious)
    {
        await using var fixture = new RefreshSessionFixture();
        DashboardSnapshot? previous = null;
        if (hasPrevious)
        {
            await fixture.RefreshAsync();
            previous = fixture.Session.State.Snapshot;
        }
        var responses = RefreshResponses.Success(revision: "new");
        responses.Copilot = responses.Copilot with { Failure = new GitHubException("Usage denied") };
        fixture.Api.Use(responses);

        await fixture.RefreshAsync();

        var snapshot = Assert.IsType<DashboardSnapshot>(fixture.Session.State.Snapshot);
        Assert.Null(fixture.Session.State.Error);
        Assert.Equal("new", snapshot.Activity.Items[0].Id);
        Assert.NotNull(snapshot.Contributions.Calendar);
        Assert.Null(snapshot.Contributions.Error);
        Assert.Equal("Usage denied", snapshot.Copilot.Error);
        Assert.Equal(hasPrevious, snapshot.Copilot.IsStale);
        Assert.Equal(previous?.Copilot.Usage, snapshot.Copilot.Usage);
        Assert.Equal(previous?.Copilot.UpdatedAt, snapshot.Copilot.UpdatedAt);

        fixture.Api.Use(RefreshResponses.Success());
        await fixture.RefreshAsync();
        SessionAssertions.Success(fixture.Session.State);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("sensitive unexpected response")]
    [InlineData("""{"login":"octocat","copilot_plan":"enterprise","quota_snapshots":{}}""")]
    public async Task MalformedCopilotResponseHasFixedErrorAndDoesNotReplaceGoodData(string body)
    {
        await using var fixture = new RefreshSessionFixture();
        await fixture.RefreshAsync();
        var original = fixture.Session.State.Snapshot!.Copilot;
        var responses = RefreshResponses.Success();
        responses.Copilot = new(body);
        fixture.Api.Use(responses);

        await fixture.RefreshAsync();

        var section = fixture.Session.State.Snapshot!.Copilot;
        Assert.True(section.IsStale);
        Assert.Equal(original.Usage, section.Usage);
        Assert.Equal(original.UpdatedAt, section.UpdatedAt);
        Assert.Equal("GitHub returned an unexpected Copilot usage response. Refresh again or update GitHub Tray if this persists.",
            section.Error);
    }

    [Fact]
    public async Task CopilotAccountMismatchRejectsTheWholeRefresh()
    {
        await using var fixture = new RefreshSessionFixture();
        await fixture.RefreshAsync();
        var responses = RefreshResponses.Success();
        responses.Copilot = new(CopilotTestData.Response("someone-else").ToJsonString());
        fixture.Api.Use(responses);

        await fixture.RefreshAsync();

        SessionAssertions.Unverified(fixture.Session.State, "octocat");
        Assert.Null(fixture.Session.State.Snapshot);
        Assert.Contains("account changed while loading Copilot usage", fixture.Session.State.Error);
    }

    [Fact]
    public async Task PendingCopilotRequestSharesRefreshAndIsDrainedOnShutdown()
    {
        await using var fixture = new RefreshSessionFixture();
        var gate = fixture.Gate();
        var responses = RefreshResponses.Success();
        responses.Copilot = responses.Copilot with { Gate = gate };
        fixture.Api.Use(responses);
        var refresh = fixture.Session.RefreshAsync();
        await gate.EnteredAsync();
        Assert.Same(refresh, fixture.Session.RefreshAsync());
        Assert.Null(fixture.Session.State.Snapshot);

        await fixture.Session.ShutdownAsync().WaitAsync(RefreshSessionFixture.Timeout);
        await refresh.WaitAsync(RefreshSessionFixture.Timeout);

        Assert.True(fixture.Session.State.IsStopping);
        Assert.Null(fixture.Session.State.Snapshot);
        Assert.Null(fixture.Session.State.Error);
        Assert.Single(fixture.Api.Requests, request => request.Route == ApiRoute.Copilot);
        Assert.DoesNotContain(fixture.Api.Requests, request => request.Route == ApiRoute.FinalUser);
    }
}
