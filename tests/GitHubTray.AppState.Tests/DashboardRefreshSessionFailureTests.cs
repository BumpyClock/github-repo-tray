using System.ComponentModel;
using GitHubTray.Core;

namespace GitHubTray.AppState.Tests;

public sealed class DashboardRefreshSessionFailureTests
{
    [Theory]
    [InlineData("io", false)]
    [InlineData("io", true)]
    [InlineData("win32", false)]
    [InlineData("win32", true)]
    [InlineData("access", false)]
    [InlineData("access", true)]
    public async Task ExpectedOperatingSystemFaultsAreVisibleFailClosedAndDoNotRetry(string kind, bool faultAsTask)
    {
        foreach (var phase in new[] { "initial", "section", "final" })
        {
            await using var fixture = new RefreshSessionFixture();
            await fixture.RefreshAsync();
            var previous = fixture.Session.State;
            Exception failure = kind switch
            {
                "io" => new IOException("Transport pipe unavailable"),
                "win32" => new Win32Exception(2, "Transport process unavailable"),
                "access" => new UnauthorizedAccessException("Transport access denied"),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            var responses = RefreshResponses.Success(revision: "must-not-publish");
            SetFailure(responses, phase, failure, faultAsTask);
            fixture.Api.Use(responses);

            await fixture.RefreshAsync();

            SessionAssertions.Unverified(fixture.Session.State, "octocat");
            Assert.Equal("An unexpected refresh error occurred. Check GitHub CLI and try again.",
                fixture.Session.State.Error);
            var expectedRequests = 8 + RequestsThrough(phase);
            Assert.Equal(expectedRequests, fixture.Api.Requests.Length);
            SessionAssertions.Success(previous);

            fixture.Api.Use(RefreshResponses.Success(revision: "recovered"));
            await fixture.RefreshAsync();
            SessionAssertions.Success(fixture.Session.State, revision: "recovered");
            Assert.Equal(expectedRequests + 8, fixture.Api.Requests.Length);
        }
    }

    [Theory]
    [InlineData("initial", false)]
    [InlineData("initial", true)]
    [InlineData("section", false)]
    [InlineData("section", true)]
    [InlineData("final", false)]
    [InlineData("final", true)]
    public async Task UnexpectedFaultsPropagateClearPublishedDataAndLeaveFutureRefreshUsable(string phase, bool faultAsTask)
    {
        await using var fixture = new RefreshSessionFixture();
        await fixture.RefreshAsync();
        var previous = fixture.Session.State;
        var failure = new InvalidOperationException($"Unexpected failure in {phase}");
        var responses = RefreshResponses.Success(revision: "must-not-publish");
        SetFailure(responses, phase, failure, faultAsTask);
        fixture.Api.Use(responses);

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RefreshAsync());

        Assert.Same(failure, observed);
        Assert.Null(fixture.Session.State.Snapshot);
        Assert.False(fixture.Session.State.IsAccountVerified);
        Assert.False(fixture.Session.State.IsRefreshing);
        Assert.False(fixture.Session.State.IsStopping);
        Assert.Equal("octocat", fixture.Session.State.LastKnownLogin);
        var expectedRequests = 8 + RequestsThrough(phase);
        Assert.Equal(expectedRequests, fixture.Api.Requests.Length);
        SessionAssertions.Success(previous);

        fixture.Api.Use(RefreshResponses.Success(revision: "recovered"));
        await fixture.RefreshAsync();
        SessionAssertions.Success(fixture.Session.State, revision: "recovered");
        Assert.Equal(expectedRequests + 8, fixture.Api.Requests.Length);
    }

    [Fact]
    public async Task CoalescedUnexpectedFailureIsObservableByEveryWaiterWithoutStrandingTheSession()
    {
        await using var fixture = new RefreshSessionFixture();
        var gate = fixture.Gate();
        var failure = new InvalidOperationException("Unexpected identity failure");
        var responses = RefreshResponses.Success();
        responses.InitialUser = responses.InitialUser with { Gate = gate, Failure = failure };
        fixture.Api.Use(responses);
        var first = fixture.Session.RefreshAsync();
        await gate.EnteredAsync();
        var waiters = Enumerable.Range(0, 12).Select(_ => fixture.Session.RefreshAsync()).ToArray();
        Assert.All(waiters, task => Assert.Same(first, task));
        Assert.True(fixture.Session.State.IsRefreshing);

        gate.Release();
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task.WhenAll(waiters.Append(first)).WaitAsync(RefreshSessionFixture.Timeout));

        Assert.Same(failure, observed);
        Assert.All(waiters, task =>
        {
            Assert.True(task.IsFaulted);
            Assert.Same(failure, Assert.Single(task.Exception!.InnerExceptions));
        });
        Assert.False(fixture.Session.State.IsRefreshing);
        Assert.Null(fixture.Session.State.Snapshot);
        Assert.False(fixture.Session.State.IsAccountVerified);
        Assert.Single(fixture.Api.Requests);

        fixture.Api.Use(RefreshResponses.Success(revision: "recovered"));
        await fixture.RefreshAsync();
        SessionAssertions.Success(fixture.Session.State, revision: "recovered");
        Assert.Equal(9, fixture.Api.Requests.Length);
    }

    [Fact]
    public async Task RepeatedExplicitIdentityFailuresDoNotQueueAutomaticRetries()
    {
        await using var fixture = new RefreshSessionFixture();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var gate = fixture.Gate();
            var responses = RefreshResponses.Success();
            responses.InitialUser = responses.InitialUser with
            {
                Gate = gate,
                Failure = new GitHubException($"Offline attempt {attempt}")
            };
            fixture.Api.Use(responses);
            var refresh = fixture.Session.RefreshAsync();
            await gate.EnteredAsync();
            Assert.Same(refresh, fixture.Session.RefreshAsync());
            Assert.Equal(attempt + 1, fixture.Api.Requests.Length);
            gate.Release();
            await refresh.WaitAsync(RefreshSessionFixture.Timeout);

            SessionAssertions.Unverified(fixture.Session.State, null);
            Assert.Equal($"Offline attempt {attempt}", fixture.Session.State.Error);
            Assert.Equal(attempt + 1, fixture.Api.Requests.Length);
        }
    }

    private static int RequestsThrough(string phase) => phase switch
    {
        "initial" => 1,
        "section" => 7,
        "final" => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(phase))
    };

    private static void SetFailure(RefreshResponses responses, string phase, Exception failure, bool faultAsTask)
    {
        switch (phase)
        {
            case "initial":
                responses.InitialUser = responses.InitialUser with { Failure = failure, FaultAsTask = faultAsTask };
                break;
            case "section":
                responses.Repositories = responses.Repositories with { Failure = failure, FaultAsTask = faultAsTask };
                break;
            case "final":
                responses.FinalUser = responses.FinalUser with { Failure = failure, FaultAsTask = faultAsTask };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(phase));
        }
    }
}
