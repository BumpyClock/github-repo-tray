using System.Collections.Immutable;
using GitHubTray.Core;
using GitHubTray_App.ViewModels;

namespace GitHubTray.App.Tests;

public sealed class DashboardSnapshotPresentationTests
{
    [Fact]
    public void CompletedWarmStartKeepsCachedProvenanceWithoutClaimingActiveRefresh()
    {
        var cachedAt = new DateTimeOffset(2026, 9, 14, 20, 0, 0, TimeSpan.Zero);
        var cachedList = new DashboardSection([], cachedAt, null)
        {
            Source = DashboardSectionSource.Cached
        };
        var contributions = new ContributionSection(
            new ContributionCalendar(0, []),
            cachedAt,
            null)
        {
            Source = DashboardSectionSource.Cached
        };
        var copilot = new CopilotUsageSection(
            new CopilotUsage("individual", ImmutableArray.Create(
                new CopilotQuota(
                    CopilotQuotaKind.PremiumInteractions,
                    CopilotQuotaAvailability.Limited,
                    75,
                    false,
                    cachedAt.AddDays(1)))),
            cachedAt,
            null)
        {
            Source = DashboardSectionSource.Cached
        };
        var snapshot = new DashboardSnapshot(
            new GitHubUser(
                "github.com",
                1,
                "octocat",
                "Octocat",
                new Uri("https://github.com/octocat")),
            cachedList,
            cachedList,
            cachedList,
            cachedList)
        {
            Contributions = contributions,
            Copilot = copilot
        };

        var completedAccount = DashboardSnapshotPresentation.AccountDescription(
            snapshot,
            isRefreshing: false);
        var refreshingAccount = DashboardSnapshotPresentation.AccountDescription(
            snapshot,
            isRefreshing: true);
        var unverifiedAccount = DashboardSnapshotPresentation.AccountDescription(
            snapshot,
            isRefreshing: true,
            isVerified: false);
        var completedContributions = DashboardSnapshotPresentation.CachedContributionStatus(
            contributions,
            isRefreshing: false);
        var refreshingContributions = DashboardSnapshotPresentation.CachedContributionStatus(
            contributions,
            isRefreshing: true);
        var completedCopilot = CopilotUsageViewModel.Create(
            copilot,
            displayable: true,
            refreshing: false,
            cachedAt);
        var refreshingCopilot = CopilotUsageViewModel.Create(
            copilot,
            displayable: true,
            refreshing: true,
            cachedAt);

        Assert.Contains("retained data from this device", completedAccount);
        Assert.DoesNotContain("live requests continue", completedAccount);
        Assert.Contains("live requests continue", refreshingAccount);
        Assert.Contains("Saved github.com account: @octocat", unverifiedAccount);
        Assert.Contains("original timestamps", unverifiedAccount);
        Assert.Contains("verification continues", unverifiedAccount);
        Assert.StartsWith("Cached from ", completedContributions);
        Assert.DoesNotContain("refreshing", completedContributions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refreshing contributions", refreshingContributions);
        Assert.StartsWith("Cached from ", completedCopilot.Status);
        Assert.DoesNotContain("refreshing", completedCopilot.Status, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Cached from ", refreshingCopilot.Status);
        Assert.DoesNotContain("refreshing", refreshingCopilot.Status, StringComparison.OrdinalIgnoreCase);
    }
}
