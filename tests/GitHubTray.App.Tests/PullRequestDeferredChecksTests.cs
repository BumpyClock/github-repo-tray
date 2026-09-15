using System.Collections.Immutable;
using GitHubTray.Core;
using GitHubTray_App.ViewModels;

namespace GitHubTray.App.Tests;

public sealed class PullRequestDeferredChecksTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CardConstructionAndVisibleSummariesDoNotMaterializeIndividualChecks()
    {
        var vm = Create("old", CheckState.Passed, isStale: true, totalCount: 142);

        Assert.True(vm.HasChecks);
        Assert.True(vm.IsChecksTruncated);
        Assert.Equal("Showing 100 of 142 checks", vm.CheckCountLabel);
        Assert.Equal("100 passed", vm.CheckStateCounts);
        Assert.Equal("Stale · Checks successful · partial list", vm.ChecksSummary);
        Assert.Contains("Stale", vm.ChecksAccessibleName);
        Assert.Contains("Stale", vm.AccessibleName);
        Assert.NotEmpty(vm.Metadata);
        Assert.NotEmpty(vm.ChecksExplanation);
        vm.UpdateRelativeTimestamp(Now.AddHours(1));

        Assert.False(vm.HasCreatedCheckDetails);
    }

    [Fact]
    public void FirstDetailsRequestCreatesOrderedModelsOnceAndReusesThem()
    {
        var vm = Create("old", CheckState.Failed);
        Assert.False(vm.HasCreatedCheckDetails);

        var first = vm.Checks;

        Assert.True(vm.HasCreatedCheckDetails);
        Assert.Equal(100, first.Count);
        Assert.Equal("old check 0", first[0].Name);
        Assert.Equal("old check 99", first[^1].Name);
        Assert.All(first, check => Assert.Equal("Failed", check.State));
        Assert.Same(first, vm.Checks);
        Assert.Same(first[0], vm.Checks[0]);
    }

    [Fact]
    public void FlyoutHasNoItemsSourceBeforeOpeningAndRetainsContentAcrossRepeatOpens()
    {
        var vm = Create("old", CheckState.Passed);
        var flyout = new PullRequestChecksFlyoutState();

        Assert.Null(flyout.Items);
        Assert.Null(flyout.GetOpenData(vm));
        Assert.False(vm.HasCreatedCheckDetails);

        var first = flyout.Open(vm);
        Assert.Same(vm.Checks, first);
        Assert.Same(first, flyout.Items);
        Assert.Same(vm, flyout.GetOpenData(vm));
        flyout.Close();

        Assert.Null(flyout.GetOpenData(vm));
        Assert.Same(first, flyout.Items);
        Assert.Same(first, flyout.Open(vm));
    }

    [Fact]
    public void RecyclingClearsOldItemsAndAuthorizationWithoutEagerlyProjectingTheReplacement()
    {
        var old = Create("old", CheckState.Passed);
        var replacement = Create("new", CheckState.Failed, isStale: true);
        var flyout = new PullRequestChecksFlyoutState();
        var oldItems = flyout.Open(old);

        // Even before recycling cleanup runs, an action must match the control's current Data.
        Assert.Null(flyout.GetOpenData(replacement));
        flyout.Reset();
        Assert.Null(flyout.Items);
        Assert.Null(flyout.GetOpenData(old));
        Assert.False(replacement.HasCreatedCheckDetails);

        var replacementItems = flyout.Open(replacement);
        Assert.NotSame(oldItems, replacementItems);
        Assert.Equal("new check 0", replacementItems![0].Name);
        Assert.Equal("Failed", replacementItems[0].State);
        Assert.Same(replacement, flyout.GetOpenData(replacement));
        Assert.Null(flyout.GetOpenData(old));
        Assert.Equal("Stale · 100 failed", replacement.ChecksSummary);
    }

    [Fact]
    public void EmptyOrClearedDataDoesNotAcquireAnOldFlyoutTarget()
    {
        var vm = Create("old", CheckState.Passed, itemCount: 0);
        var flyout = new PullRequestChecksFlyoutState();
        Assert.False(vm.HasChecks);
        Assert.False(vm.HasCreatedCheckDetails);
        Assert.Empty(flyout.Open(vm)!);
        flyout.Reset();

        Assert.Null(flyout.Open(null));
        Assert.Null(flyout.Items);
        Assert.Null(flyout.GetOpenData(vm));
    }

    private static PullRequestCardViewModel Create(
        string prefix, CheckState state, bool isStale = false, int itemCount = 100, int? totalCount = null)
    {
        var items = Enumerable.Range(0, itemCount)
            .Select(index => new PullRequestCheck($"{prefix} check {index}", state)).ToImmutableArray();
        var aggregate = itemCount == 0 ? CheckRollupState.NoChecks
            : state == CheckState.Failed ? CheckRollupState.Failed : CheckRollupState.Passed;
        var details = new PullRequestDetails(42, "Improve startup", "octocat", null, false,
            "feature", "main", null, 0, [], 0, new CommitChecks("abcdef123456", aggregate, items, totalCount ?? itemCount));
        var item = new DashboardItem("pr-42", "#42 Improve startup", "owner/repo", "Open pull request",
            Now, new Uri("https://github.com/owner/repo/pull/42")) { PullRequest = details };
        return new(item, isStale, Now);
    }
}
