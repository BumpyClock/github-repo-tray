using GitHubTray.Core;
using GitHubTray_App.ViewModels;

namespace GitHubTray.App.Tests;

public sealed class PullRequestHistoryPresentationTests
{
    private static readonly DateTimeOffset EventTime = new(2026, 9, 14, 16, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(PullRequestState.Open, false, "Open")]
    [InlineData(PullRequestState.Open, true, "Draft")]
    [InlineData(PullRequestState.Closed, false, "Closed")]
    [InlineData(PullRequestState.Closed, true, "Closed")]
    [InlineData(PullRequestState.Merged, false, "Merged")]
    [InlineData(PullRequestState.Merged, true, "Merged")]
    public void CurrentStateWinsOverDraftMetadata(PullRequestState state, bool draft, string label)
    {
        var vm = Present(state, draft);

        Assert.Equal(label, vm.StateLabel);
        Assert.Equal(state, vm.State);
        Assert.Equal(state == PullRequestState.Open && draft, vm.IsDraft);
        Assert.NotEmpty(vm.StateGlyph);
        Assert.Contains($"Current state: {label}.", vm.AccessibleName);
        Assert.True(vm.CanOpenChecks); // Closed/merged cards remain navigable.
        Assert.Equal("No checks", vm.ChecksSummary); // State does not manufacture CI success.
    }

    [Theory]
    [InlineData(PullRequestState.Open)]
    [InlineData(PullRequestState.Closed)]
    [InlineData(PullRequestState.Merged)]
    public void StaleStateDoesNotClaimToBeCurrent(PullRequestState state)
    {
        var vm = new PullRequestCardViewModel(Item(state), isStale: true, EventTime);

        Assert.StartsWith("Last known state:", vm.StateDescription);
        Assert.Contains("Section refresh failed.", vm.StateDescription);
        Assert.DoesNotContain("Current state:", vm.AccessibleName);
        Assert.Equal("Stale · No checks", vm.ChecksSummary);
    }

    [Theory]
    [InlineData(1, "Latest: Commented on pull request · 1 event")]
    [InlineData(7, "Latest: Commented on pull request · 7 events")]
    public void GroupedActivityShowsLatestActionAndActualLoadedEventCount(int count, string expected)
    {
        var vm = new PullRequestCardViewModel(Item(PullRequestState.Merged) with
        {
            PullRequestActivity = new(42, "Commented on pull request", count)
        }, false, EventTime.AddMinutes(9));

        Assert.True(vm.HasActivity);
        Assert.Equal(count, vm.ActivityEventCount);
        Assert.Equal(expected, vm.ActivitySummary);
        Assert.Equal("Merged", vm.StateLabel); // Commenting after merge does not reopen a PR.
        Assert.Equal("#42 · @alex · 9m ago", vm.Metadata);
        Assert.Contains("Latest activity", vm.MetadataToolTip);
        Assert.DoesNotContain("Updated", vm.MetadataToolTip);
        Assert.Contains("not the full PR history", vm.ActivityToolTip);
        Assert.Contains(expected, vm.AccessibleName);
        Assert.Contains("Latest activity 9m ago", vm.AccessibleName);
        Assert.Equal("A current PR title", vm.Title); // Not an event title or duplicate #number.
    }

    [Theory]
    [InlineData(PullRequestState.Open, "Closed pull request")]
    [InlineData(PullRequestState.Merged, "Opened pull request")]
    [InlineData(PullRequestState.Closed, "Reopened pull request")]
    public void LatestFeedEventIsNotUsedToInferCurrentState(PullRequestState state, string action)
    {
        var vm = new PullRequestCardViewModel(Item(state) with
        {
            PullRequestActivity = new(42, action, 3)
        }, false, EventTime);

        Assert.Equal(state.ToString(), vm.StateLabel);
        Assert.Contains(action, vm.ActivitySummary);
        Assert.Contains($"Current state: {state}.", vm.AccessibleName);
    }

    [Fact]
    public void MyPullRequestsHaveNoInventedActivityHistory()
    {
        var vm = Present(PullRequestState.Merged);

        Assert.False(vm.HasActivity);
        Assert.Equal(0, vm.ActivityEventCount);
        Assert.Empty(vm.ActivitySummary);
        Assert.Empty(vm.ActivityToolTip);
        Assert.Contains("Updated", vm.MetadataToolTip);
        Assert.DoesNotContain("Latest activity", vm.MetadataToolTip);
    }

    [Fact]
    public void ActivityClockUsesEventTimestampAndNotifiesAccessibleMetadata()
    {
        var item = Item(PullRequestState.Closed) with
        {
            PullRequestActivity = new(42, "Closed pull request", 5)
        };
        var vm = new PullRequestCardViewModel(item, false, EventTime);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        vm.UpdateRelativeTimestamp(EventTime.AddHours(2));

        Assert.EndsWith("2h ago", vm.Metadata);
        Assert.Contains("Latest activity 2h ago", vm.AccessibleName);
        Assert.Contains(nameof(PullRequestCardViewModel.Metadata), notifications);
        Assert.Contains(nameof(PullRequestCardViewModel.AccessibleName), notifications);
        Assert.Equal("Latest: Closed pull request · 5 events", vm.ActivitySummary);
    }

    [Fact]
    public void RefreshCanReplaceOpenHistoryWithMergedStateWithoutChangingCheckSemantics()
    {
        var before = Present(PullRequestState.Open);
        var mergedItem = Item(PullRequestState.Merged) with
        {
            PullRequestActivity = new(42, "Merged pull request", 6)
        };
        mergedItem = mergedItem with
        {
            PullRequest = mergedItem.PullRequest! with
            {
                Checks = new("1234567890", CheckRollupState.Passed, [new("build", CheckState.Passed)], 100)
            }
        };
        var after = new PullRequestCardViewModel(mergedItem, false, EventTime);

        Assert.Equal("Open", before.StateLabel);
        Assert.Equal("Merged", after.StateLabel);
        Assert.Equal("Latest: Merged pull request · 6 events", after.ActivitySummary);
        Assert.Equal("Checks successful · partial list", after.ChecksSummary);
        Assert.Equal("Showing 1 of 100 checks", after.CheckCountLabel);
        Assert.DoesNotContain("100 passed", after.ChecksSummary);
    }

    [Theory]
    [InlineData(PullRequestState.Closed)]
    [InlineData(PullRequestState.Merged)]
    public void HistoricalStateDoesNotTurnUnknownChecksIntoPassed(PullRequestState state)
    {
        var item = Item(state);
        item = item with
        {
            PullRequest = item.PullRequest! with
            {
                Checks = new(null, CheckRollupState.Unknown, [], 0),
                ReviewDecision = "APPROVED"
            }
        };
        var vm = new PullRequestCardViewModel(item, false, EventTime);

        Assert.Equal("Checks unknown", vm.ChecksSummary);
        Assert.Equal("Approved", vm.ReviewDecisionText);
        Assert.Equal(state.ToString(), vm.StateLabel);
    }

    [Fact]
    public void MissingActivityDescriptionDoesNotInventActionsOrCounts()
    {
        var vm = new PullRequestCardViewModel(Item(PullRequestState.Open) with
        {
            PullRequestActivity = new(42, "", 0)
        }, false, EventTime);

        Assert.Equal("Latest: Activity recorded · Event count unavailable", vm.ActivitySummary);
        Assert.DoesNotContain("1 event", vm.ActivitySummary);
    }

    private static PullRequestCardViewModel Present(PullRequestState state, bool draft = false) =>
        new(Item(state, draft), false, EventTime);

    private static DashboardItem Item(PullRequestState state, bool draft = false) =>
        new("pr-42", "#42 A current PR title", "owner/repository", "Pull request",
            EventTime, new Uri("https://github.com/owner/repository/pull/42"))
        {
            PullRequest = new(42, "A current PR title", "alex", null, draft, "feature", "main", null, 0, [], 0,
                new("abcdef1234", CheckRollupState.NoChecks, [], 0)) { State = state }
        };
}
