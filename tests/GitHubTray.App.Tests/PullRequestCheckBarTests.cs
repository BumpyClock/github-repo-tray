using System.Collections.Immutable;
using GitHubTray.Core;
using GitHubTray_App.ViewModels;

namespace GitHubTray.App.Tests;

/// <summary>
/// Covers the check bar and its verdict: the card states an outcome in pixels as well as
/// words, so the rules that keep the words honest have to hold for the bar too.
/// </summary>
public sealed class PullRequestCheckBarTests
{
    private static readonly DateTimeOffset UpdatedAt = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EqualOutcomesSplitTheBarEvenlyAndFillIt()
    {
        var widths = CheckBarLayout.Distribute([1, 1, 1, 1], 100);

        // Runs share the width left after the gaps, and shared edges land on whole pixels.
        Assert.Equal([24, 23, 24, 23], widths);
        Assert.Equal(100 - 3 * CheckBarLayout.SegmentSpacing, widths.Sum());
    }

    [Fact]
    public void OneFailureAmongHundredsStaysWideEnoughToSee()
    {
        var widths = CheckBarLayout.Distribute([1, 499], 300);

        Assert.Equal(CheckBarLayout.MinSegmentWidth, widths[0]);
        Assert.Equal(300 - CheckBarLayout.SegmentSpacing, widths.Sum());
    }

    [Fact]
    public void ABarTooNarrowForTheFloorSharesWhatIsLeftInsteadOfCrowdingRunsOut()
    {
        var widths = CheckBarLayout.Distribute([90, 1, 1], 12);

        Assert.Equal(3, widths.Length);
        Assert.All(widths, width =>
        {
            Assert.InRange(width, 2, 3);
            Assert.Equal(Math.Round(width), width);
        });
        Assert.Equal(8, widths.Sum());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-40)]
    public void ABarWithNoRoomProducesNoWidthRatherThanNegativeOnes(double width)
    {
        Assert.Equal([0d, 0d], CheckBarLayout.Distribute([3, 1], width));
        Assert.Empty(CheckBarLayout.Distribute([], 200));
    }

    [Fact]
    public void ShortListsDrawOneRunPerCheckOrderedWorstFirst()
    {
        var vm = Present(Checks(CheckRollupState.Failed,
            CheckState.Passed, CheckState.Failed, CheckState.Passed, CheckState.Running));

        Assert.Equal(4, vm.CheckSegments.Count);
        Assert.All(vm.CheckSegments, segment => Assert.Equal(1, segment.Weight));
        Assert.All(vm.CheckSegments, segment => Assert.True(segment.IsLoaded));
        Assert.Equal(
            [StatusTone.Failure, StatusTone.Progress, StatusTone.Success, StatusTone.Success],
            vm.CheckSegments.Select(segment => segment.Tone));
    }

    [Fact]
    public void LongListsCollapseIntoOneWeightedRunPerOutcome()
    {
        var outcomes = Enumerable.Repeat(CheckState.Passed, 18)
            .Append(CheckState.Failed).Append(CheckState.Skipped).ToArray();
        var vm = Present(Checks(CheckRollupState.Failed, outcomes));

        // Skipped outranks passed in SeverityRank, so a completion never sorts last.
        Assert.Equal(
            [(StatusTone.Failure, 1), (StatusTone.Neutral, 1), (StatusTone.Success, 18)],
            vm.CheckSegments.Select(segment => (segment.Tone, segment.Weight)));
    }

    [Fact]
    public void ChecksGitHubNeverReturnedTrailTheBarAsAbsenceRatherThanAnOutcome()
    {
        var vm = Present(Truncated(CheckRollupState.Passed, loaded: 6, total: 142));
        var tail = vm.CheckSegments[^1];

        Assert.False(tail.IsLoaded);
        Assert.Equal(136, tail.Weight);
        Assert.Single(vm.CheckSegments, segment => !segment.IsLoaded);
        Assert.Equal("6 of 142 loaded", vm.ChecksDenominator);
        Assert.DoesNotContain("passed", vm.ChecksVerdict, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CheckRollupState.Passed, "All checks passed", "3 checks", CheckState.Passed, CheckState.Passed, CheckState.Passed)]
    [InlineData(CheckRollupState.Failed, "2 failing", "of 3 checks", CheckState.Failed, CheckState.Passed, CheckState.Failed)]
    [InlineData(CheckRollupState.Pending, "1 running", "of 2 checks", CheckState.Running, CheckState.Passed)]
    [InlineData(CheckRollupState.Unknown, "1 needs attention", "of 2 checks", CheckState.Cancelled, CheckState.Passed)]
    // A completion is not a success, so a skipped list stops short of the word "passed".
    [InlineData(CheckRollupState.Passed, "Completed", "2 checks", CheckState.Skipped, CheckState.Passed)]
    public void TheVerdictReportsOnlyWhatTheLoadedChecksShow(
        CheckRollupState aggregate, string verdict, string denominator, params CheckState[] outcomes)
    {
        var vm = Present(Checks(aggregate, outcomes));

        Assert.Equal(verdict, vm.ChecksVerdict);
        Assert.Equal(denominator, vm.ChecksDenominator);
    }

    [Fact]
    public void StaleResultsNeverPresentAsSettled()
    {
        var vm = Present(Checks(CheckRollupState.Passed, CheckState.Passed, CheckState.Passed), isStale: true);

        Assert.Equal("Stale results", vm.ChecksVerdict);
        Assert.Equal("2 checks · older commit", vm.ChecksDenominator);
        Assert.Contains("older commit", vm.ChecksCaveat);
    }

    [Fact]
    public void AnUneventfulResultCarriesNoCaveatAtAll()
    {
        var vm = Present(Checks(CheckRollupState.Passed, CheckState.Passed));

        Assert.False(vm.HasChecksCaveat);
        Assert.Empty(vm.ChecksCaveat);
    }

    [Fact]
    public void EveryQualificationIsStatedOnceInsteadOfPrefixingTheVerdict()
    {
        var vm = Present(Truncated(CheckRollupState.Failed, loaded: 6, total: 142), isStale: true);

        Assert.True(vm.HasChecksCaveat);
        Assert.Contains("refresh failed", vm.ChecksCaveat);
        Assert.Contains("6 of 142", vm.ChecksCaveat);
        Assert.Contains("do not mean those checks passed", vm.ChecksCaveat);
        Assert.Contains("No loaded check shows that outcome", vm.ChecksCaveat);
    }

    [Fact]
    public void DetailGroupsLeadWithTheOutcomeThatNeedsAttentionAndFoldTheRest()
    {
        var vm = Present(Checks(CheckRollupState.Failed,
            CheckState.Passed, CheckState.Failed, CheckState.Skipped, CheckState.Passed));

        Assert.Equal(["Failed", "Skipped", "Passed"], vm.CheckGroups.Select(group => group.Title));
        Assert.Equal([1, 1, 2], vm.CheckGroups.Select(group => group.Count));
        Assert.True(vm.CheckGroups[0].IsExpanded);
        Assert.All(vm.CheckGroups.Skip(1), group => Assert.False(group.IsExpanded));
        Assert.Equal("Failed, 1 check", vm.CheckGroups[0].AccessibleName);
    }

    [Fact]
    public void NothingIsFoldedAwayWhenThereIsNothingWorseToReadInstead()
    {
        var vm = Present(Checks(CheckRollupState.Passed, CheckState.Passed, CheckState.Skipped));

        Assert.Equal(["Skipped", "Passed"], vm.CheckGroups.Select(group => group.Title));
        Assert.All(vm.CheckGroups, group => Assert.True(group.IsExpanded));
    }

    [Fact]
    public void GroupingWaitsForTheDetailsRequestAndTheFlyoutClearsItOnRecycling()
    {
        var vm = Present(Checks(CheckRollupState.Failed, CheckState.Failed, CheckState.Passed));
        var flyout = new PullRequestChecksFlyoutState();

        Assert.NotEmpty(vm.CheckSegments);
        Assert.NotEmpty(vm.ChecksVerdict);
        Assert.False(vm.HasCreatedCheckDetails);
        Assert.Null(flyout.Groups);

        flyout.Open(vm);

        Assert.True(vm.HasCreatedCheckDetails);
        Assert.Same(vm.CheckGroups, flyout.Groups);
        flyout.Reset();
        Assert.Null(flyout.Groups);
    }

    private static PullRequestCardViewModel Present(CommitChecks checks, bool isStale = false) =>
        new(Item(Details(checks)), isStale, UpdatedAt);

    private static CommitChecks Checks(CheckRollupState state, params CheckState[] outcomes) =>
        new("abcdef1234567890", state,
            outcomes.Select((outcome, index) => new PullRequestCheck($"Check {index + 1}", outcome)).ToImmutableArray(),
            outcomes.Length);

    private static CommitChecks Truncated(CheckRollupState state, int loaded, int total) =>
        new("abcdef1234567890", state,
            Enumerable.Range(0, loaded).Select(index => new PullRequestCheck($"Check {index}", CheckState.Passed))
                .ToImmutableArray(),
            total);

    private static PullRequestDetails Details(CommitChecks checks) =>
        new(42, "Add reusable pull request cards", "alex-dev", null, false,
            "feature/pr-cards", "main", null, 2, [], 0, checks);

    private static DashboardItem Item(PullRequestDetails details) =>
        new("pr-42", "#42 Add reusable pull request cards", "owner/repository", "Open pull request",
            UpdatedAt, new Uri("https://github.com/owner/repository/pull/42")) { PullRequest = details };
}
