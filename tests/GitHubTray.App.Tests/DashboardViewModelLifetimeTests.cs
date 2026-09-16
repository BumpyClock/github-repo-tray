using System.Runtime.CompilerServices;
using GitHubTray.AppState;
using GitHubTray.Core;
using GitHubTray_App.ViewModels;
using Microsoft.UI.Dispatching;

namespace GitHubTray.App.Tests;

public sealed class DashboardViewModelLifetimeTests
{
    [Fact]
    public async Task HideReleasesPresentationButPreservesSessionSettingsAndRefreshCadence()
    {
        await using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        await vm.InitializeAsync();
        vm.SetPanelVisible(true);
        var snapshot = fixture.Session.State.Snapshot;
        var retired = CapturePresentation(vm);
        vm.SelectedSection = vm.Sections[2];
        var selected = vm.SelectedSection;
        vm.IsSettingsOpen = true;
        vm.RefreshMinutesText = "17";
        vm.ContributionCellSize = ContributionCellSizePreset.Large;
        vm.SettingsWarning = "Keep this warning";
        vm.SettingsStatus = "Keep this status";
        vm.RefreshError = "Keep this refresh error";
        vm.ActionError = "Keep this action error";
        vm.Sections[0].Error = "Keep this section error";
        var updatedLabel = vm.Sections[0].UpdatedLabel;
        var requests = fixture.Api.RequestCount;
        Assert.All(fixture.Dispatcher.Timers, timer => Assert.True(timer.IsRunning));

        vm.SetPanelVisible(false);
        vm.SetPanelVisible(false);

        AssertPresentationReleased(vm);
        Assert.Same(snapshot, fixture.Session.State.Snapshot);
        Assert.True(fixture.Session.State.IsAccountVerified);
        Assert.False(fixture.Session.State.IsStopping);
        Assert.Same(selected, vm.SelectedSection);
        Assert.True(vm.IsSettingsOpen);
        Assert.Equal("17", vm.RefreshMinutesText);
        Assert.Equal(ContributionCellSizePreset.Large, vm.ContributionCellSize);
        Assert.Equal("Keep this warning", vm.SettingsWarning);
        Assert.Equal("Keep this status", vm.SettingsStatus);
        Assert.Equal("Keep this refresh error", vm.RefreshError);
        Assert.Equal("Keep this action error", vm.ActionError);
        Assert.Equal("Keep this section error", vm.Sections[0].Error);
        Assert.Equal(updatedLabel, vm.Sections[0].UpdatedLabel);
        Assert.True(fixture.Dispatcher.Timers[0].IsRunning);
        Assert.False(fixture.Dispatcher.Timers[1].IsRunning);
        Assert.True(fixture.Dispatcher.Timers[2].IsRunning);
        Assert.Equal(requests, fixture.Api.RequestCount);
        Collect();
        Assert.All(retired, reference => Assert.False(reference.IsAlive));
        fixture.Dispatcher.Timers[0].Fire();
        Assert.True(fixture.Api.RequestCount > requests);
        AssertPresentationReleased(vm);
        GC.KeepAlive(vm);
    }

    [Fact]
    public async Task SameSnapshotRevealRebuildsRowsAndDefersChecksButRepeatedRevealReusesRows()
    {
        await using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        await vm.InitializeAsync();
        vm.SetPanelVisible(true);
        var first = vm.Sections[1].Items[0];
        var snapshot = fixture.Session.State.Snapshot;
        Assert.NotEmpty(first.PullRequest!.CheckGroups);
        vm.SetPanelVisible(true);
        Assert.Same(first, vm.Sections[1].Items[0]);

        vm.SetPanelVisible(false);
        vm.SetPanelVisible(true);

        var replacement = vm.Sections[1].Items[0];
        Assert.NotSame(first, replacement);
        Assert.False(replacement.PullRequest!.HasCreatedCheckDetails);
        Assert.Same(snapshot, fixture.Session.State.Snapshot);
        Assert.Same(snapshot!.Contributions.Calendar, vm.VisibleContributionCalendar);
        Assert.NotEmpty(vm.CopilotDisplay.Quotas);
        vm.SetPanelVisible(true);
        Assert.Same(replacement, vm.Sections[1].Items[0]);
    }

    [Fact]
    public async Task HiddenRefreshUpdatesSessionWithoutRecreatingRowsUntilReveal()
    {
        await using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        await vm.InitializeAsync();
        vm.SetPanelVisible(true);
        Assert.NotEmpty(vm.Sections[0].Items);
        vm.SetPanelVisible(false);
        var requests = fixture.Api.RequestCount;

        await vm.RefreshAsync();

        Assert.True(fixture.Api.RequestCount > requests);
        Assert.True(fixture.Session.State.IsAccountVerified);
        Assert.Empty(fixture.Session.State.Snapshot!.Activity.Items);
        AssertPresentationReleased(vm);

        vm.SetPanelVisible(true);

        Assert.True(vm.IsAccountVerified);
        Assert.Empty(vm.Sections[0].Items);
        Assert.NotEmpty(vm.Sections[1].Items);
    }

    [Fact]
    public async Task HiddenIdentityFailureRevealsSavedRowsAsUnverifiedAndKeepsRecoveryUsable()
    {
        await using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        await vm.InitializeAsync();
        vm.SetPanelVisible(true);
        vm.SetPanelVisible(false);
        fixture.Api.FailIdentity = true;

        await vm.RefreshAsync();
        vm.SetPanelVisible(true);

        Assert.False(vm.IsAccountVerified);
        Assert.True(vm.HasDisplayableData);
        Assert.All(vm.Sections, section => Assert.NotEmpty(section.Items));
        Assert.NotNull(vm.VisibleContributionCalendar);
        Assert.NotEmpty(vm.CopilotDisplay.Quotas);
        Assert.Equal("Saved account · verification failed", vm.AccountDisplayName);
        Assert.Contains("Saved github.com account: @octocat", vm.AccountDescription);
        Assert.Contains("Fixture identity unavailable", vm.RefreshError);
        Assert.Contains("remains unverified", vm.RefreshError);

        fixture.Api.FailIdentity = false;
        await vm.RefreshAsync();

        Assert.True(vm.IsAccountVerified);
        Assert.Equal("seed", vm.Sections[1].Items[0].Item.Id);
        Assert.NotNull(vm.VisibleContributionCalendar);
    }

    [Fact]
    public async Task CacheInvalidationSurvivesRevealWithoutRetainingAReplacedSnapshot()
    {
        await using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        await vm.InitializeAsync();
        vm.SetPanelVisible(true);
        await vm.ClearCachedDataAsync();
        var retired = CaptureSnapshot(fixture.Session);
        var status = vm.CacheClearStatus;
        vm.SetPanelVisible(false);
        vm.SetPanelVisible(true);
        Assert.Contains("invalidated", vm.AccountDescription);
        Assert.Equal(status, vm.CacheClearStatus);
        vm.SetPanelVisible(false);

        // The owner can advance before the UI continuation publishes the replacement.
        await fixture.Session.RefreshAsync();
        Collect();

        Assert.False(retired.IsAlive);
        AssertPresentationReleased(vm);
        vm.SetPanelVisible(true);
        Assert.DoesNotContain("invalidated", vm.AccountDescription);
        GC.KeepAlive(vm);
    }

    [Theory]
    [InlineData(DashboardRefreshReason.Manual)]
    [InlineData(DashboardRefreshReason.Periodic)]
    public async Task ConfirmedAccountSwitchClearsVisibleRowsBeforeBlockedSectionsComplete(
        DashboardRefreshReason reason)
    {
        await using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        await vm.InitializeAsync();
        vm.SetPanelVisible(true);
        Assert.NotEmpty(vm.Sections[0].Items);
        var gate = fixture.Api.BlockRepositories();
        fixture.Api.Login = "different-account";

        var refresh = reason is DashboardRefreshReason.Manual
            ? vm.RefreshAsync()
            : StartPeriodicRefresh(fixture);
        await gate.EnteredAsync();

        Assert.False(refresh.IsCompleted);
        Assert.False(vm.HasDisplayableData);
        Assert.All(vm.Sections, section => Assert.Empty(section.Items));
        Assert.Null(vm.VisibleContributionCalendar);
        Assert.Empty(vm.CopilotDisplay.Quotas);
        Assert.Contains("@different-account", vm.AccountHandle);

        gate.Release();
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task GraphQlMismatchClearsVisibleRowsBeforeAnotherSectionDrains()
    {
        await using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        await vm.InitializeAsync();
        vm.SetPanelVisible(true);
        Assert.NotEmpty(vm.Sections[0].Items);
        var gate = fixture.Api.BlockRepositories();
        fixture.Api.ContributionLogin = "different-account";

        var refresh = vm.RefreshAsync();
        await gate.EnteredAsync();
        await fixture.Api.ContributionReturned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(refresh.IsCompleted);
        Assert.False(vm.HasDisplayableData);
        Assert.All(vm.Sections, section => Assert.Empty(section.Items));
        Assert.Contains("account changed", vm.RefreshError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@octocat", vm.AccountHandle);

        gate.Release();
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static Task StartPeriodicRefresh(Fixture fixture)
    {
        fixture.Dispatcher.Timers[0].Fire();
        return fixture.Session.RefreshAsync(DashboardRefreshReason.Periodic);
    }

    private static void AssertPresentationReleased(DashboardViewModel vm)
    {
        Assert.All(vm.Sections, section => Assert.Empty(section.Items));
        Assert.Null(vm.Contributions.Calendar);
        Assert.Null(vm.VisibleContributionCalendar);
        Assert.Empty(vm.CopilotDisplay.Quotas);
        Assert.False(vm.IsAccountVerified);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CapturePresentation(DashboardViewModel vm)
    {
        var references = new List<WeakReference> { new(vm.CopilotDisplay) };
        foreach (var section in vm.Sections)
        {
            foreach (var row in section.Items)
            {
                references.Add(new(row));
                if (row.PullRequest is { } card)
                {
                    references.Add(new(card));
                    references.Add(new(card.Checks));
                    references.Add(new(card.CheckGroups));
                }
            }
        }
        return references.ToArray();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureSnapshot(DashboardRefreshSession session) =>
        new(session.State.Snapshot!);

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture()
        {
            var now = DateTimeOffset.UtcNow;
            var item = new DashboardItem(
                "seed", "Seed PR", "octocat/tray", "Open pull request", now,
                new Uri("https://github.com/octocat/tray/pull/42"))
            {
                PullRequest = new(42, "Seed PR", "octocat", null, false, "feature", "main",
                    null, 0, [], 0,
                    new("abcdef1234", CheckRollupState.Passed, [new("build", CheckState.Passed)], 1))
            };
            var section = new DashboardSection([item], now, null);
            var snapshot = new DashboardSnapshot(
                new("github.com", 1, "octocat", "Octocat", new Uri("https://github.com/octocat")),
                section, section, section, section)
            {
                Contributions = new(
                    new(1, [new(new DateOnly(2026, 9, 13),
                        [new(new DateOnly(2026, 9, 13), 1, ContributionLevel.First)])]),
                    now, null),
                Copilot = new(
                    new("individual",
                        [new(CopilotQuotaKind.PremiumInteractions, CopilotQuotaAvailability.Limited,
                            75, false, now.AddDays(1))]),
                    now, null)
            };
            Session = new(new DashboardService(Api), snapshot);
            var startup = DashboardStartup.StartIfPrimary(true, _ => new(
                Session,
                new SettingsStore(Path.Combine(Environment.CurrentDirectory, "unused-lifetime-settings.json")),
                Task.FromResult(new AppSettings())))!;
            ViewModel = new(startup, Dispatcher);
        }

        public FixtureApi Api { get; } = new();
        public DispatcherQueue Dispatcher { get; } = new();
        public DashboardRefreshSession Session { get; }
        public DashboardViewModel ViewModel { get; }

        public async ValueTask DisposeAsync()
        {
            // Restore the queued preference before shutdown so this fixture never writes settings.
            ViewModel.ContributionCellSize = ContributionCellSizePreset.Medium;
            await ViewModel.ShutdownAsync();
        }
    }

    private sealed class FixtureApi : IGitHubApi
    {
        public int RequestCount { get; private set; }
        public bool FailIdentity { get; set; }
        public string Login { get; set; } = "octocat";
        public string? ContributionLogin { get; set; }
        public TaskCompletionSource ContributionReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TestGate? repositoryGate;

        public TestGate BlockRepositories() => repositoryGate = new();

        public async Task<string> GetAsync(
            string endpoint,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            if (endpoint.StartsWith("user/repos?", StringComparison.Ordinal) &&
                repositoryGate is not null)
            {
                await repositoryGate.WaitAsync(cancellationToken);
            }
            if (endpoint != "user")
            {
                return "[]";
            }
            if (FailIdentity)
            {
                throw new GitHubException("Fixture identity unavailable");
            }
            var id = string.Equals(Login, "octocat", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                id,
                login = Login,
                name = Login,
                html_url = $"https://github.com/{Login}"
            });
        }

        public Task<string> QueryAsync(string query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            if (ContributionLogin is not null &&
                query.Contains("contributionsCollection", StringComparison.Ordinal))
            {
                ContributionReturned.TrySetResult();
                return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new
                {
                    data = new { viewer = new { login = ContributionLogin } }
                }));
            }
            return Task.FromResult("{}");
        }
    }

    private sealed class TestGate
    {
        private readonly TaskCompletionSource entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnteredAsync() => entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        public void Release() => released.TrySetResult();

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await released.Task.WaitAsync(cancellationToken);
        }
    }
}
