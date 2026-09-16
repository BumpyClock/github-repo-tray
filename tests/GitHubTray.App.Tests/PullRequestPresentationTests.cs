using System.Collections.Immutable;
using GitHubTray.Core;
using GitHubTray_App.ViewModels;

namespace GitHubTray.App.Tests;

public sealed class PullRequestPresentationTests
{
    private static readonly DateTimeOffset UpdatedAt = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(CheckRollupState.Unknown, "Checks unknown")]
    [InlineData(CheckRollupState.NoChecks, "No checks")]
    [InlineData(CheckRollupState.Passed, "Checks unknown")]
    [InlineData(CheckRollupState.Failed, "Checks unknown")]
    [InlineData(CheckRollupState.Pending, "Checks unknown")]
    public void MissingResultsNeverMeanPassed(CheckRollupState aggregate, string expected)
    {
        var vm = Present(Checks(aggregate));

        Assert.Equal(expected, vm.ChecksSummary);
        Assert.False(vm.HasChecks);
        Assert.Equal("0 checks", vm.CheckCountLabel);
        Assert.NotEmpty(vm.EmptyChecksMessage);
    }

    [Theory]
    [InlineData(CheckState.Unknown, "1 unknown", "Unknown", StatusTone.Caution)]
    [InlineData(CheckState.Pending, "1 pending", "Pending", StatusTone.Progress)]
    [InlineData(CheckState.Running, "1 running", "Running", StatusTone.Progress)]
    [InlineData(CheckState.Passed, "1 passed", "Passed", StatusTone.Success)]
    [InlineData(CheckState.Failed, "1 failed", "Failed", StatusTone.Failure)]
    [InlineData(CheckState.Neutral, "1 neutral", "Neutral", StatusTone.Neutral)]
    [InlineData(CheckState.Skipped, "1 skipped", "Skipped", StatusTone.Neutral)]
    [InlineData(CheckState.Cancelled, "1 cancelled", "Cancelled", StatusTone.Caution)]
    [InlineData(CheckState.ActionRequired, "1 awaiting action", "Action required", StatusTone.Caution)]
    public void IndividualOutcomesAreNotCollapsedIntoSuccess(
        CheckState state, string summary, string word, StatusTone tone)
    {
        var vm = Present(Checks(CheckRollupState.Passed, state));

        Assert.Equal(summary, vm.ChecksSummary);
        Assert.Equal(tone, vm.ChecksTone);
        Assert.Equal(word, Assert.Single(vm.Checks).State);
        var group = Assert.Single(vm.CheckGroups);
        Assert.NotEmpty(group.Glyph);
        Assert.Equal(tone, group.Tone);
        Assert.Contains(word, vm.Checks[0].AccessibleName);
    }

    [Fact]
    public void SuccessfulAggregateCanContainNeutralAndSkippedWithoutClaimingAllPassed()
    {
        var vm = Present(Checks(CheckRollupState.Passed, CheckState.Passed, CheckState.Neutral, CheckState.Skipped));

        // Named outcomes, never a vague "mixed" verdict and never "passed".
        Assert.Equal("1 neutral · 1 skipped · 1 passed", vm.ChecksSummary);
        Assert.Equal(StatusTone.Neutral, vm.ChecksTone);
        Assert.Equal("3 checks", vm.CheckCountLabel);
        Assert.False(vm.IsChecksTruncated);
    }

    [Fact]
    public void FailuresLeadTheSummaryAheadOfPassingChecks()
    {
        var vm = Present(Checks(CheckRollupState.Failed,
            CheckState.Passed, CheckState.Failed, CheckState.Passed, CheckState.Running));

        Assert.Equal("1 failed · 1 running · 2 passed", vm.ChecksSummary);
        Assert.Equal(StatusTone.Failure, vm.ChecksTone);
    }

    [Theory]
    [InlineData(CheckRollupState.Failed, "Checks failed · partial list", StatusTone.Failure)]
    [InlineData(CheckRollupState.Pending, "Checks pending · partial list", StatusTone.Progress)]
    [InlineData(CheckRollupState.Unknown, "Checks unknown · partial list", StatusTone.Caution)]
    [InlineData(CheckRollupState.Passed, "Checks successful · partial list", StatusTone.Neutral)]
    public void TruncatedListUsesFullAggregateNotLoadedSuccesses(
        CheckRollupState aggregate, string summary, StatusTone tone)
    {
        var checks = Checks(aggregate, Enumerable.Repeat(CheckState.Passed, 100).ToArray()) with { TotalCount = 142 };
        var vm = Present(checks);

        Assert.Equal(summary, vm.ChecksSummary);
        Assert.Equal(tone, vm.ChecksTone);
        Assert.True(vm.IsChecksTruncated);
        Assert.Equal("Showing 100 of 142 checks", vm.CheckCountLabel);
        Assert.Contains("GitHub returned 100 of 142 checks.", vm.ChecksCaveat);
        Assert.Contains("Results that are missing do not mean those checks passed.", vm.ChecksCaveat);
        Assert.DoesNotContain("100/142", vm.ChecksSummary);
        Assert.DoesNotContain("passed", vm.ChecksSummary);
    }

    [Theory]
    [InlineData(CheckRollupState.Failed, "Checks failed · 1 passed", StatusTone.Failure)]
    [InlineData(CheckRollupState.Pending, "Checks pending · 1 passed", StatusTone.Progress)]
    [InlineData(CheckRollupState.Unknown, "Checks unknown · 1 passed", StatusTone.Caution)]
    [InlineData(CheckRollupState.NoChecks, "Checks unknown · 1 passed", StatusTone.Caution)]
    public void FullAggregateEvidenceIsNotOverriddenByPassedItems(
        CheckRollupState aggregate, string summary, StatusTone tone)
    {
        var vm = Present(Checks(aggregate, CheckState.Passed));

        Assert.Equal(summary, vm.ChecksSummary);
        Assert.Equal(tone, vm.ChecksTone);
    }

    [Fact]
    public void LoadedFailuresAlreadyExplainAFailedAggregate()
    {
        var vm = Present(Checks(CheckRollupState.Failed, CheckState.Failed, CheckState.Passed));

        Assert.Equal("1 failed · 1 passed", vm.ChecksSummary);
        Assert.DoesNotContain("Checks failed · 1 failed", vm.ChecksSummary);
    }

    [Fact]
    public void StaleSuccessfulDataIsExplicitlyStaleEvenAfterClockUpdates()
    {
        var vm = Present(Checks(CheckRollupState.Passed, CheckState.Passed), isStale: true);

        vm.UpdateRelativeTimestamp(UpdatedAt.AddHours(2));

        Assert.Equal("Stale · 1 passed", vm.ChecksSummary);
        // Stale results describe an older commit, so they never present as settled.
        Assert.Equal(StatusTone.Caution, vm.ChecksTone);
        Assert.Contains("section refresh failed", vm.FreshnessDescription);
        Assert.Equal("Last known head abcdef1", vm.HeadCommitLabel);
        Assert.Contains("Stale", vm.ChecksAccessibleName);
        Assert.Contains("Stale", vm.AccessibleName);
        Assert.DoesNotContain("latest", vm.FreshnessDescription, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("live", vm.FreshnessDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefreshReplacementUsesNewCommitOutcomesLabelsAndFreshness()
    {
        var retained = Present(Checks(CheckRollupState.Failed, CheckState.Failed), isStale: true);
        var refreshed = Present(Checks(CheckRollupState.Passed, CheckState.Passed) with { CommitOid = "1234567890" });

        Assert.Equal("Stale · 1 failed", retained.ChecksSummary);
        Assert.Equal("1 passed", refreshed.ChecksSummary);
        Assert.Equal(StatusTone.Success, refreshed.ChecksTone);
        Assert.Equal("Latest head 1234567", refreshed.HeadCommitLabel);
        Assert.False(refreshed.IsStale);
        Assert.Equal("1234567890", refreshed.HeadCommitToolTip);
    }

    [Fact]
    public void LabelOverflowUsesActualTotalRatherThanLoadedNames()
    {
        var labels = Enumerable.Range(1, 10).Select(index => new PullRequestLabel($"label-{index}", "123abc")).ToImmutableArray();
        var details = Details(Checks(CheckRollupState.NoChecks)) with { Labels = labels, LabelCount = 23 };
        var vm = new PullRequestCardViewModel(Item(details), false, UpdatedAt);

        Assert.Equal(
            ["label-1", "label-2", "label-3", "label-4", "label-5", "label-6", "+17"],
            vm.LabelChips.Select(chip => chip.Text));
        Assert.Equal(23, vm.LabelCount);
        Assert.Equal(17, vm.AdditionalLabelCount);
        // The overflow chip carries no label color, so it never fakes one.
        Assert.False(vm.LabelChips[^1].HasColor);
        Assert.All(vm.LabelChips.Take(6), chip => Assert.Equal("123abc", chip.Color));
        Assert.Contains("Showing names for 10 of 23 labels", vm.LabelsToolTip);
        Assert.Contains("label-10", vm.LabelsToolTip);
        Assert.DoesNotContain("label-11", vm.LabelsToolTip);
    }

    [Fact]
    public void EmptyAndSmallLabelSetsDoNotAddAnOverflowChip()
    {
        var empty = Present(Checks(CheckRollupState.NoChecks));
        var details = Details(Checks(CheckRollupState.NoChecks)) with
        {
            Labels = [new("bug", "ffffff"), new("needs review", "000000")], LabelCount = 2
        };
        var small = new PullRequestCardViewModel(Item(details), false, UpdatedAt);

        Assert.False(empty.HasLabels);
        Assert.Empty(empty.LabelChips);
        Assert.Equal("No labels", empty.LabelsToolTip);
        Assert.True(small.HasLabels);
        Assert.Equal(0, small.AdditionalLabelCount);
        Assert.Equal(["bug", "needs review"], small.LabelChips.Select(chip => chip.Text));
        Assert.Equal("2 labels: bug, needs review", small.LabelsToolTip);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("APPROVED", "Approved")]
    [InlineData("CHANGES_REQUESTED", "Changes requested")]
    [InlineData("REVIEW_REQUIRED", "Review required")]
    public void DraftBadgePreservesReviewDecisionForAccessibility(string? decision, string text)
    {
        var details = Details(Checks(CheckRollupState.NoChecks)) with { IsDraft = true, ReviewDecision = decision };
        var vm = new PullRequestCardViewModel(Item(details), false, UpdatedAt);

        Assert.True(vm.IsDraft);
        Assert.Equal(text, vm.ReviewDecisionText);
        Assert.Equal(decision is not null, vm.HasReviewDecision);
        Assert.Equal("Draft", vm.StatusLabel);
        Assert.Equal("StatusDraft", vm.StatusVisualState);
        if (decision is null)
            Assert.DoesNotContain("review required", vm.AccessibleName, StringComparison.OrdinalIgnoreCase);
        else
            Assert.Contains(text, vm.AccessibleName);
    }

    [Theory]
    // A finished pull request reports how it finished, never the review that got it there.
    [InlineData(PullRequestState.Merged, "APPROVED", PullRequestStatusKind.Merged, "Merged")]
    [InlineData(PullRequestState.Merged, "CHANGES_REQUESTED", PullRequestStatusKind.Merged, "Merged")]
    [InlineData(PullRequestState.Closed, "APPROVED", PullRequestStatusKind.Closed, "Closed")]
    // An open pull request reports the review decision, which outranks a bare "Open".
    [InlineData(PullRequestState.Open, "APPROVED", PullRequestStatusKind.Approved, "Approved")]
    [InlineData(PullRequestState.Open, "CHANGES_REQUESTED", PullRequestStatusKind.ChangesRequested, "Changes requested")]
    [InlineData(PullRequestState.Open, "REVIEW_REQUIRED", PullRequestStatusKind.ReviewRequired, "Review required")]
    [InlineData(PullRequestState.Open, null, PullRequestStatusKind.Open, "Open")]
    // An unrecognized decision is not a status worth showing.
    [InlineData(PullRequestState.Open, "SOMETHING_NEW", PullRequestStatusKind.Open, "Open")]
    public void StatusBadgeShowsOneLatestActionableStatus(
        PullRequestState state, string? decision, PullRequestStatusKind kind, string label)
    {
        var details = Details(Checks(CheckRollupState.NoChecks)) with { State = state, ReviewDecision = decision };
        var vm = new PullRequestCardViewModel(Item(details), false, UpdatedAt);

        Assert.Equal(kind, vm.StatusKind);
        Assert.Equal(label, vm.StatusLabel);
        Assert.Equal($"Status{kind}", vm.StatusVisualState);
    }

    [Fact]
    public void DraftOutranksReviewDecisionInTheBadge()
    {
        // Nobody is waiting on a review that the author has not asked for yet.
        var details = Details(Checks(CheckRollupState.NoChecks)) with { IsDraft = true, ReviewDecision = "REVIEW_REQUIRED" };
        var vm = new PullRequestCardViewModel(Item(details), false, UpdatedAt);

        Assert.Equal(PullRequestStatusKind.Draft, vm.StatusKind);
        Assert.Equal("Draft", vm.StatusLabel);
        // The decision still reaches assistive technology through the accessible name.
        Assert.Equal("Review required", vm.ReviewDecisionText);
    }

    [Fact]
    public void ReviewStatusKeepsTheUnderlyingStateInItsTooltip()
    {
        var details = Details(Checks(CheckRollupState.NoChecks)) with { State = PullRequestState.Open, ReviewDecision = "APPROVED" };
        var vm = new PullRequestCardViewModel(Item(details), false, UpdatedAt);

        Assert.StartsWith("Approved.", vm.StatusDescription, StringComparison.Ordinal);
        Assert.Contains(vm.StateDescription, vm.StatusDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void CardFooterExistsOnlyForComments()
    {
        // Review status alone does not need a comments footer.
        var quiet = Details(Checks(CheckRollupState.NoChecks)) with { ReviewDecision = "APPROVED", CommentCount = 0 };
        Assert.False(new PullRequestCardViewModel(Item(quiet), false, UpdatedAt).HasComments);

        var talkative = quiet with { CommentCount = 3 };
        Assert.True(new PullRequestCardViewModel(Item(talkative), false, UpdatedAt).HasComments);
    }

    [Fact]
    public void ClockNotifiesMetadataAndAccessibleNameWithoutFetching()    {
        var vm = Present(Checks(CheckRollupState.NoChecks));
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        vm.UpdateRelativeTimestamp(UpdatedAt.AddMinutes(5));

        Assert.Equal("#42 · @alex-dev · 5m ago", vm.Metadata);
        Assert.Contains(nameof(PullRequestCardViewModel.Metadata), notifications);
        Assert.Contains(nameof(PullRequestCardViewModel.AccessibleName), notifications);
        Assert.Contains("5m ago", vm.AccessibleName);
        Assert.Equal("AD", vm.AuthorInitials);
        Assert.Equal("feature/pr-cards → main", vm.Branches);
        Assert.Equal("owner/repository", vm.Repository);
        Assert.Equal("2 comments", vm.CommentSummary);
        Assert.True(vm.HasComments);
        Assert.Equal("2", vm.CommentCountText);
    }

    [Fact]
    public void FutureTimestampAndMissingHeadStayHonest()
    {
        var vm = Present(Checks(CheckRollupState.Unknown) with { CommitOid = null });
        vm.UpdateRelativeTimestamp(UpdatedAt.AddMinutes(-10));

        Assert.Equal("just now", vm.RelativeTimestamp);
        Assert.Equal("Head commit unavailable", vm.HeadCommitLabel);
        Assert.Equal("Checks unknown", vm.ChecksSummary);
    }

    [Theory]
    [InlineData("https://github.com/owner/repository/pull/42")]
    [InlineData("https://github.com/owner/repository/pull/42/")]
    [InlineData("https://github.com/owner/repository/pull/42?tab=files#discussion")]
    public void ChecksLinkIsCanonicalHttpsGitHubPath(string url)
    {
        var vm = new PullRequestCardViewModel(Item(Details(Checks(CheckRollupState.NoChecks))) with
        {
            Url = new Uri(url)
        }, false);

        Assert.True(vm.CanOpenChecks);
        Assert.Equal("https://github.com/owner/repository/pull/42/checks", vm.ChecksUri?.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://github.com/owner/repository/pull/42")]
    [InlineData("https://github.com.evil.test/owner/repository/pull/42")]
    [InlineData("https://evil.test@github.com/owner/repository/pull/42")]
    [InlineData("https://github.com:444/owner/repository/pull/42")]
    [InlineData("https://github.com/owner/repository/issues/42")]
    [InlineData("https://github.com/owner/repository/pull/43")]
    [InlineData("https://github.com/owner/repository/pull/42/files")]
    [InlineData("/owner/repository/pull/42")]
    public void ChecksNavigationRejectsUnsafeOrWrongPrLinks(string url)
    {
        var vm = new PullRequestCardViewModel(Item(Details(Checks(CheckRollupState.NoChecks))) with
        {
            Url = new Uri(url, UriKind.RelativeOrAbsolute)
        }, false);

        Assert.False(vm.CanOpenChecks);
        Assert.Null(vm.ChecksUri);
    }

    [Fact]
    public void MissingCheckNameStillHasAccessibleState()
    {
        var vm = Present(new CommitChecks(null, CheckRollupState.Unknown, [new(" ", CheckState.Unknown)], 1));

        Assert.Equal("Unnamed check: Unknown", Assert.Single(vm.Checks).AccessibleName);
        Assert.Equal(StatusTone.Caution, Assert.Single(vm.CheckGroups).Tone);
    }

    private static PullRequestCardViewModel Present(CommitChecks checks, bool isStale = false) =>
        new(Item(Details(checks)), isStale, UpdatedAt);

    private static CommitChecks Checks(CheckRollupState state, params CheckState[] outcomes) =>
        new("abcdef1234567890", state,
            outcomes.Select((outcome, index) => new PullRequestCheck($"Check {index + 1}", outcome)).ToImmutableArray(),
            outcomes.Length);

    private static PullRequestDetails Details(CommitChecks checks) =>
        new(42, "Add reusable pull request cards", "alex-dev", null, false,
            "feature/pr-cards", "main", null, 2, [], 0, checks);

    private static DashboardItem Item(PullRequestDetails details) =>
        new("pr-42", "#42 Add reusable pull request cards", "owner/repository", "Open pull request",
            UpdatedAt, new Uri("https://github.com/owner/repository/pull/42")) { PullRequest = details };
}
