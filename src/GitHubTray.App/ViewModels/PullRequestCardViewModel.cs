using System.Collections.Immutable;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GitHubTray.Core;

namespace GitHubTray_App.ViewModels;

/// <summary>
/// Severity a status chip communicates. Presentation maps this to platform status
/// colors; it never upgrades an outcome the data did not report.
/// </summary>
public enum StatusTone
{
    Neutral,
    Success,
    Progress,
    Caution,
    Failure
}

/// <summary>
/// The single badge a pull request card shows. Terminal states outrank review, and review
/// outranks a bare "Open", so the card never stacks two status words that mean the same moment.
/// </summary>
public enum PullRequestStatusKind
{
    Open,
    Draft,
    ReviewRequired,
    Approved,
    ChangesRequested,
    Closed,
    Merged
}

/// <summary>
/// Presentation of one PR at one section refresh. Only the clock changes in place;
/// a new section result replaces this model, including its explicit freshness.
/// No WinUI types, timers, fetching, or navigation live here.
/// </summary>
public sealed class PullRequestCardViewModel : ObservableObject
{
    // Chips wrap onto several lines, so more of them stay readable than in a single row.
    private const int MaxLabelChips = 6;

    private string _relativeTimestamp = "";
    private readonly ImmutableArray<PullRequestCheck> _checkItems;
    private IReadOnlyList<PullRequestCheckViewModel>? _checks;
    private IReadOnlyList<PullRequestCheckGroup>? _checkGroups;

    public PullRequestCardViewModel(DashboardItem item, bool isStale, DateTimeOffset? now = null)
    {
        var details = item.PullRequest ?? throw new ArgumentException("A PR card requires PR details.", nameof(item));
        Item = item;
        IsStale = isStale;
        Title = details.Title;
        Number = details.Number;
        AuthorLogin = details.AuthorLogin;
        AuthorAvatarUrl = details.AuthorAvatarUrl;
        AuthorInitials = GetInitials(AuthorLogin);
        State = details.State;
        // A closed or merged PR is no longer a draft, even if retained API metadata
        // still has the draft bit. Current state is independent of the latest event.
        IsDraft = State == PullRequestState.Open && details.IsDraft;
        Branches = $"{details.HeadRefName} → {details.BaseRefName}";
        ReviewDecision = details.ReviewDecision;
        ReviewDecisionText = details.ReviewDecision switch
        {
            "APPROVED" => "Approved",
            "CHANGES_REQUESTED" => "Changes requested",
            "REVIEW_REQUIRED" => "Review required",
            null or "" => "",
            _ => "Review status unknown"
        };
        CommentCount = details.CommentCount;
        CommentSummary = $"{details.CommentCount} {(details.CommentCount == 1 ? "comment" : "comments")}";

        var labels = details.Labels.IsDefault ? ImmutableArray<PullRequestLabel>.Empty : details.Labels;
        LabelCount = details.LabelCount;
        var chips = labels.Take(MaxLabelChips)
            .Select(label => new PullRequestLabelChip(label.Name, label.Color, label.Name))
            .ToList();
        AdditionalLabelCount = Math.Max(0, LabelCount - chips.Count);
        if (AdditionalLabelCount > 0)
            chips.Add(new PullRequestLabelChip($"+{AdditionalLabelCount}", null,
                $"{AdditionalLabelCount} more {(AdditionalLabelCount == 1 ? "label" : "labels")}"));
        LabelChips = chips;
        LabelsToolTip = LabelCount == 0 ? "No labels"
            : $"{LabelCount} {(LabelCount == 1 ? "label" : "labels")}: {string.Join(", ", labels.Select(label => label.Name))}"
                + (labels.Length < LabelCount ? $" · Showing names for {labels.Length} of {LabelCount} labels." : "");

        var checks = details.Checks;
        var items = checks.Items.IsDefault ? ImmutableArray<PullRequestCheck>.Empty : checks.Items;
        _checkItems = items;
        IsChecksTruncated = items.Length < checks.TotalCount;
        CheckCountLabel = IsChecksTruncated
            ? $"Showing {items.Length} of {checks.TotalCount} checks"
            : $"{items.Length} {(items.Length == 1 ? "check" : "checks")}";
        var (summary, glyph, tone) = SummarizeChecks(checks.State, items, IsChecksTruncated);
        ChecksSummary = IsStale ? $"Stale · {summary}" : summary;
        ChecksGlyph = IsStale ? "\uE7BA" : glyph;
        // Stale results describe an older commit, so they never present as settled.
        ChecksTone = IsStale ? StatusTone.Caution : tone;
        CheckSegments = BuildSegments(items, checks.TotalCount);
        (ChecksVerdict, ChecksDenominator) =
            SummarizeVerdict(checks.State, items, checks.TotalCount, IsChecksTruncated, IsStale);
        ChecksCaveat = BuildCaveat(checks.State, items, checks.TotalCount, IsChecksTruncated, IsStale);
        EmptyChecksMessage = checks.State == CheckRollupState.NoChecks && checks.TotalCount == 0
            ? "No checks were reported for this commit."
            : "Individual check results are unavailable. Missing results do not mean checks passed.";
        HeadCommitLabel = string.IsNullOrWhiteSpace(checks.CommitOid)
            ? "Head commit unavailable"
            : $"{(IsStale ? "Last known head" : "Latest head")} {checks.CommitOid[..Math.Min(7, checks.CommitOid.Length)]}";
        HeadCommitToolTip = checks.CommitOid ?? "GitHub did not return a head commit.";
        ChecksUri = CreateChecksUri(item.Url, Number);
        UpdateRelativeTimestamp(now ?? DateTimeOffset.UtcNow);
    }

    public DashboardItem Item { get; }
    public string Id => Item.Id;
    public string Title { get; }
    public int Number { get; }
    public string Repository => Item.Repository;
    public string AuthorLogin { get; }
    public Uri? AuthorAvatarUrl { get; }
    public string AuthorInitials { get; }
    public PullRequestState State { get; }
    public bool IsDraft { get; }
    public string StateLabel => State switch
    {
        PullRequestState.Merged => "Merged",
        PullRequestState.Closed => "Closed",
        _ => IsDraft ? "Draft" : "Open"
    };
    public string StateDescription => $"{(IsStale ? "Last known state" : "Current state")}: {StateLabel}."
        + (IsStale ? " Section refresh failed." : "");

    /// <summary>
    /// The card shows one status, not two. A merged or closed pull request is finished, so the
    /// review decision that got it there is history; only an open pull request has a review
    /// decision worth acting on, and when it does that decision outranks the bare "Open".
    /// </summary>
    public PullRequestStatusKind StatusKind => State switch
    {
        PullRequestState.Merged => PullRequestStatusKind.Merged,
        PullRequestState.Closed => PullRequestStatusKind.Closed,
        _ when IsDraft => PullRequestStatusKind.Draft,
        _ => ReviewDecision switch
        {
            "CHANGES_REQUESTED" => PullRequestStatusKind.ChangesRequested,
            "REVIEW_REQUIRED" => PullRequestStatusKind.ReviewRequired,
            "APPROVED" => PullRequestStatusKind.Approved,
            _ => PullRequestStatusKind.Open
        }
    };
    public string StatusLabel => StatusKind switch
    {
        PullRequestStatusKind.Merged => "Merged",
        PullRequestStatusKind.Closed => "Closed",
        PullRequestStatusKind.Draft => "Draft",
        PullRequestStatusKind.ChangesRequested => "Changes requested",
        PullRequestStatusKind.ReviewRequired => "Review required",
        PullRequestStatusKind.Approved => "Approved",
        _ => "Open"
    };
    public string StatusGlyph => StatusKind switch
    {
        PullRequestStatusKind.Merged => "\uE73E", // CheckMark
        PullRequestStatusKind.Closed => "\uE711", // Cancel
        PullRequestStatusKind.Draft => "\uE70F", // Edit
        PullRequestStatusKind.ChangesRequested => "\uE7BA", // Warning
        PullRequestStatusKind.ReviewRequired => "\uE8F2", // People
        PullRequestStatusKind.Approved => "\uE73E", // CheckMark
        _ => "\uEA3A" // CircleRing
    };
    public string StatusVisualState => $"Status{StatusKind}";
    /// <summary>
    /// The pill shows the review decision on an open pull request, so the tooltip still names the
    /// underlying state. Nothing the badge replaced disappears from the card.
    /// </summary>
    public string StatusDescription => StatusKind is PullRequestStatusKind.ChangesRequested
        or PullRequestStatusKind.ReviewRequired or PullRequestStatusKind.Approved
        ? $"{StatusLabel}. {StateDescription}"
        : StateDescription;

    public string Branches { get; }
    public string? ReviewDecision { get; }
    public string ReviewDecisionText { get; }
    public bool HasReviewDecision => ReviewDecisionText.Length != 0;
    public int CommentCount { get; }
    public bool HasComments => CommentCount > 0;
    public string CommentCountText => CommentCount.ToString(CultureInfo.CurrentCulture);
    public string CommentSummary { get; }
    public IReadOnlyList<PullRequestLabelChip> LabelChips { get; }
    public int LabelCount { get; }
    public bool HasLabels => LabelCount > 0;
    public int AdditionalLabelCount { get; }
    public string LabelsToolTip { get; }
    /// <summary>Individual rows are materialized once, when the check details are requested.</summary>
    public IReadOnlyList<PullRequestCheckViewModel> Checks =>
        _checks ??= _checkItems.Select(check => new PullRequestCheckViewModel(check)).ToArray();
    /// <summary>Grouped rows for the details flyout, materialized with <see cref="Checks"/>.</summary>
    public IReadOnlyList<PullRequestCheckGroup> CheckGroups => _checkGroups ??= BuildGroups(Checks);
    internal bool HasCreatedCheckDetails => _checks is not null;
    public bool HasChecks => !_checkItems.IsEmpty;
    public bool IsChecksTruncated { get; }
    public string CheckCountLabel { get; }
    public string ChecksSummary { get; }
    /// <summary>The headline outcome, short enough to read without parsing a sentence.</summary>
    public string ChecksVerdict { get; }
    /// <summary>What the verdict is measured against; carries the truncation count when there is one.</summary>
    public string ChecksDenominator { get; }
    /// <summary>Everything the bar cannot show honestly, stated once instead of woven into the summary.</summary>
    public string ChecksCaveat { get; }
    public bool HasChecksCaveat => ChecksCaveat.Length != 0;
    public IReadOnlyList<CheckBarSegment> CheckSegments { get; }
    public bool HasCheckSegments => CheckSegments.Count > 0;
    public string ChecksGlyph { get; }
    public StatusTone ChecksTone { get; }
    public string EmptyChecksMessage { get; }
    public string HeadCommitLabel { get; }
    public string HeadCommitToolTip { get; }
    public bool IsStale { get; }
    public string FreshnessDescription => IsStale
        ? "Stale checks · section refresh failed. Showing the last successful results."
        : "Check results for the latest head at the last successful section refresh.";
    public Uri? ChecksUri { get; }
    public bool CanOpenChecks => ChecksUri is not null;
    public string ChecksButtonAutomationId => $"PullRequestChecks_{Id}";
    public string OpenChecksAutomationId => $"OpenPullRequestChecks_{Id}";
    public string ChecksAccessibleName => $"{ChecksSummary}. {Repository} pull request {Number}. Show individual checks.";
    public string OpenChecksAccessibleName => $"Open checks on GitHub for {Repository} pull request {Number}";
    public string Timestamp => Item.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string Metadata => $"#{Number} · @{AuthorLogin} · {RelativeTimestamp}";
    public bool HasActivity => Item.PullRequestActivity is not null;
    public string ActivitySummary
    {
        get
        {
            if (Item.PullRequestActivity is not { } activity)
                return "";
            var latestAction = string.IsNullOrWhiteSpace(activity.LatestAction)
                ? "Activity recorded" : activity.LatestAction;
            var count = activity.EventCount > 0
                ? $"{activity.EventCount} {(activity.EventCount == 1 ? "event" : "events")}"
                : "Event count unavailable";
            return $"Latest: {latestAction} · {count}";
        }
    }
    public string ActivityToolTip => HasActivity
        ? $"{ActivitySummary}. Latest activity {Timestamp}. Grouped events are from the loaded activity feed, not the full PR history."
        : "";
    public string MetadataToolTip => $"#{Number} · @{AuthorLogin} · {(HasActivity ? "Latest activity" : "Updated")} {Timestamp} · {CommentSummary}";
    public string AccessibleName => $"{Title}. {Repository}, pull request {Number}, by {AuthorLogin}. "
        + $"{(HasActivity ? "Latest activity" : "Updated")} {RelativeTimestamp}. {StateDescription} "
        + (HasActivity ? $"{ActivitySummary}. " : "")
        + $"{(HasReviewDecision ? ReviewDecisionText + ". " : "")}{Branches}. "
        + $"{LabelsToolTip}. {CommentSummary}. {ChecksSummary}. Open on GitHub.";

    public string RelativeTimestamp
    {
        get => _relativeTimestamp;
        private set
        {
            if (SetProperty(ref _relativeTimestamp, value))
            {
                OnPropertyChanged(nameof(Metadata));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public void UpdateRelativeTimestamp(DateTimeOffset now)
    {
        var elapsed = now - Item.UpdatedAt;
        RelativeTimestamp = elapsed.TotalMinutes < 1 ? "just now"
            : elapsed.TotalHours < 1 ? $"{(int)elapsed.TotalMinutes}m ago"
            : elapsed.TotalDays < 1 ? $"{(int)elapsed.TotalHours}h ago"
            : elapsed.TotalDays < 30 ? $"{(int)elapsed.TotalDays}d ago"
            : elapsed.TotalDays < 365 ? $"{(int)(elapsed.TotalDays / 30)}mo ago"
            : $"{(int)(elapsed.TotalDays / 365)}y ago";
    }

    private static (string Text, string Glyph, StatusTone Tone) SummarizeChecks(
        CheckRollupState aggregate, ImmutableArray<PullRequestCheck> checks, bool isTruncated)
    {
        // GraphQL's aggregate covers checks we have not loaded. Never infer progress
        // or all-passed from the first page, even when every loaded check passed.
        if (isTruncated)
        {
            return aggregate switch
            {
                CheckRollupState.Passed => ("Checks successful · partial list", "\uE9D9", StatusTone.Neutral),
                CheckRollupState.Failed => ("Checks failed · partial list", "\uEA39", StatusTone.Failure),
                CheckRollupState.Pending => ("Checks pending · partial list", "\uE823", StatusTone.Progress),
                _ => ("Checks unknown · partial list", "\uE9CE", StatusTone.Caution)
            };
        }
        if (checks.Length == 0)
        {
            return aggregate == CheckRollupState.NoChecks
                ? ("No checks", "\uE90A", StatusTone.Neutral)
                : ("Checks unknown", "\uE9CE", StatusTone.Caution);
        }

        // The loaded outcomes are reported as counts rather than one vague word. Evidence
        // that only the aggregate carries is named in front of them instead of replacing them.
        var unshown = UnshownAggregate(aggregate, checks);
        var tone = ToneForChecks(aggregate, checks);
        var counts = FormatStateCounts(checks);
        return (unshown is null ? counts : $"{unshown} · {counts}",
            unshown is null ? ToneGlyph(tone) : AggregateGlyph(aggregate),
            tone);
    }

    /// <summary>
    /// Restates the loaded outcomes as a short verdict plus what it is measured against.
    /// It reports only what was loaded; anything the aggregate alone claims is left to
    /// <see cref="BuildCaveat"/> so a verdict can never overstate the evidence.
    /// </summary>
    private static (string Verdict, string Denominator) SummarizeVerdict(
        CheckRollupState aggregate, ImmutableArray<PullRequestCheck> checks,
        int totalCount, bool isTruncated, bool isStale)
    {
        var loaded = checks.Length;
        var noun = $"{loaded} {(loaded == 1 ? "check" : "checks")}";
        // Truncation replaces the denominator outright: "of 6 checks" would be a lie
        // when only 6 of 142 were returned.
        string Support(bool countLed) => isTruncated ? $"{loaded} of {totalCount} loaded"
            : countLed ? $"of {noun}" : noun;

        // Stale results describe an older commit, so nothing below them can read as settled.
        if (isStale)
            return ("Stale results", loaded == 0 ? "older commit" : $"{Support(false)} · older commit");
        if (loaded == 0)
            return aggregate == CheckRollupState.NoChecks && totalCount == 0
                ? ("No checks", "none reported for this commit")
                : ("Checks unknown", "state not reported");

        var failing = checks.Count(check => check.State == CheckState.Failed);
        if (failing > 0) return ($"{failing} failing", Support(true));
        if (aggregate == CheckRollupState.Failed) return ("Checks failed", Support(false));

        var attention = checks.Count(check =>
            check.State is CheckState.ActionRequired or CheckState.Cancelled or CheckState.Unknown);
        if (attention > 0)
            return ($"{attention} {(attention == 1 ? "needs" : "need")} attention", Support(true));
        if (aggregate is CheckRollupState.Unknown or CheckRollupState.NoChecks)
            return ("Checks unknown", Support(false));

        var running = checks.Count(check => check.State is CheckState.Running or CheckState.Pending);
        if (running > 0) return ($"{running} running", Support(true));
        if (aggregate == CheckRollupState.Pending) return ("Checks pending", Support(false));

        // Neutral and skipped results are completions, not successes, so only an
        // untruncated, wholly passing list earns the word "passed".
        return !isTruncated && aggregate == CheckRollupState.Passed
            && checks.All(check => check.State == CheckState.Passed)
            ? ("All checks passed", noun)
            : ("Completed", Support(false));
    }

    /// <summary>
    /// Collects the qualifications the bar and verdict cannot carry, so the details view
    /// states them once instead of prefixing every summary line with them.
    /// </summary>
    private static string BuildCaveat(CheckRollupState aggregate, ImmutableArray<PullRequestCheck> checks,
        int totalCount, bool isTruncated, bool isStale)
    {
        var caveats = new List<string>(3);
        if (isStale)
            caveats.Add("Section refresh failed, so these results describe an older commit.");
        if (isTruncated)
            caveats.Add($"GitHub returned {checks.Length} of {totalCount} checks. "
                + "Results that are missing do not mean those checks passed.");
        if (UnshownAggregate(aggregate, checks) is not null)
            caveats.Add($"{AggregateDescription(aggregate)} No loaded check shows that outcome.");
        return string.Join(" ", caveats);
    }

    private const int MaxDetailedSegments = 12;

    /// <summary>
    /// Builds the bar runs, worst first. Small lists get one run per check so the
    /// reader can count them; longer lists fall back to proportional runs per outcome.
    /// </summary>
    private static IReadOnlyList<CheckBarSegment> BuildSegments(
        ImmutableArray<PullRequestCheck> checks, int totalCount)
    {
        var segments = new List<CheckBarSegment>();
        var detailed = totalCount <= MaxDetailedSegments;
        foreach (var group in checks.GroupBy(check => check.State).OrderBy(group => SeverityRank(group.Key)))
        {
            var tone = PullRequestCheckViewModel.ToneFor(group.Key);
            var count = group.Count();
            if (detailed) segments.AddRange(Enumerable.Repeat(new CheckBarSegment(tone, 1, true), count));
            else segments.Add(new CheckBarSegment(tone, count, true));
        }

        // Checks GitHub never returned occupy the bar as absence. They carry no tone,
        // so a partial list can never be mistaken for a passing one.
        var unloaded = totalCount - checks.Length;
        if (unloaded > 0) segments.Add(new CheckBarSegment(StatusTone.Neutral, unloaded, false));
        return segments;
    }

    /// <summary>
    /// Groups the detail rows worst first. Quiet outcomes start collapsed only when
    /// something worse exists to read instead.
    /// </summary>
    private static IReadOnlyList<PullRequestCheckGroup> BuildGroups(
        IReadOnlyList<PullRequestCheckViewModel> checks)
    {
        var groups = checks.GroupBy(check => check.RawState)
            .OrderBy(group => SeverityRank(group.Key)).ToArray();
        var hasWorse = groups.Any(group => !IsQuiet(group.Key));
        return groups.Select(group => new PullRequestCheckGroup(
            PullRequestCheckViewModel.StateText(group.Key),
            PullRequestCheckViewModel.ToneFor(group.Key),
            PullRequestCheckViewModel.StateGlyph(group.Key),
            isExpanded: !(IsQuiet(group.Key) && hasWorse),
            group.ToArray())).ToArray();
    }

    private static bool IsQuiet(CheckState state) =>
        state is CheckState.Passed or CheckState.Skipped or CheckState.Neutral;

    /// <summary>The chip's icon tracks its severity, not whichever check sorted first.</summary>
    private static string ToneGlyph(StatusTone tone) => tone switch    {
        StatusTone.Success => "\uE73E",
        StatusTone.Failure => "\uEA39",
        StatusTone.Caution => "\uE7BA",
        StatusTone.Progress => "\uE823",
        _ => "\uE738"
    };

    /// <summary>
    /// Names an aggregate outcome that none of the loaded checks show, so a success
    /// aggregate can never hide a failure and a failure aggregate can never be hidden.
    /// </summary>
    private static string? UnshownAggregate(CheckRollupState aggregate, ImmutableArray<PullRequestCheck> checks) =>
        aggregate switch
        {
            CheckRollupState.Failed when !checks.Any(check =>
                check.State is CheckState.Failed or CheckState.ActionRequired) => "Checks failed",
            CheckRollupState.Pending when !checks.Any(check =>
                check.State is CheckState.Pending or CheckState.Running) => "Checks pending",
            CheckRollupState.Unknown when !checks.Any(check => check.State == CheckState.Unknown) => "Checks unknown",
            // GitHub reported no checks yet returned some; the real state is unknown.
            CheckRollupState.NoChecks => "Checks unknown",
            _ => null
        };

    private static StatusTone ToneForChecks(CheckRollupState aggregate, ImmutableArray<PullRequestCheck> checks)
    {
        if (aggregate == CheckRollupState.Failed || checks.Any(check => check.State == CheckState.Failed))
            return StatusTone.Failure;
        if (aggregate is CheckRollupState.Unknown or CheckRollupState.NoChecks
            || checks.Any(check => check.State is CheckState.ActionRequired or CheckState.Cancelled or CheckState.Unknown))
            return StatusTone.Caution;
        if (aggregate == CheckRollupState.Pending
            || checks.Any(check => check.State is CheckState.Pending or CheckState.Running))
            return StatusTone.Progress;
        // Neutral, skipped and cancelled results are completions, not successes.
        return aggregate == CheckRollupState.Passed && checks.All(check => check.State == CheckState.Passed)
            ? StatusTone.Success
            : StatusTone.Neutral;
    }

    private static string AggregateGlyph(CheckRollupState state) => state switch
    {
        CheckRollupState.Failed => "\uEA39",
        CheckRollupState.Pending => "\uE823",
        _ => "\uE9CE"
    };

    /// <summary>Worst first, so the chip leads with the outcome that needs attention.</summary>
    private static int SeverityRank(CheckState state) => state switch
    {
        CheckState.Failed => 0,
        CheckState.ActionRequired => 1,
        CheckState.Cancelled => 2,
        CheckState.Unknown => 3,
        CheckState.Running => 4,
        CheckState.Pending => 5,
        CheckState.Neutral => 6,
        CheckState.Skipped => 7,
        _ => 8
    };

    private static string FormatStateCounts(ImmutableArray<PullRequestCheck> checks) =>
        string.Join(" · ", checks.GroupBy(check => check.State).OrderBy(group => SeverityRank(group.Key))
            .Select(group => $"{group.Count()} {CountWord(group.Key)}"));

    private static string CountWord(CheckState state) => state switch
    {
        CheckState.ActionRequired => "awaiting action",
        _ => PullRequestCheckViewModel.StateText(state).ToLowerInvariant()
    };

    private static string AggregateDescription(CheckRollupState state) => state switch
    {
        CheckRollupState.Passed => "GitHub reports a successful aggregate (which can include neutral or skipped checks).",
        CheckRollupState.Failed => "GitHub reports a failed aggregate.",
        CheckRollupState.Pending => "GitHub reports a pending aggregate.",
        _ => "The aggregate check state is unknown."
    };

    private static string GetInitials(string login)
    {
        var parts = login.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        return (StringInfo.GetNextTextElement(parts[0])
            + (parts.Length > 1 ? StringInfo.GetNextTextElement(parts[^1]) : "")).ToUpperInvariant();
    }

    private static Uri? CreateChecksUri(Uri uri, int number)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            return null;
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 4 || segments[0].Length == 0 || segments[1].Length == 0
            || segments[2] != "pull"
            || segments[3] != number.ToString(CultureInfo.InvariantCulture))
            return null;
        return new UriBuilder(uri)
        {
            Path = uri.AbsolutePath.TrimEnd('/') + "/checks",
            Query = "",
            Fragment = ""
        }.Uri;
    }
}

/// <summary>
/// One outcome's worth of detail rows. Groups let the flyout lead with the checks that
/// need attention and fold away the ones that do not, without hiding anything.
/// </summary>
public sealed class PullRequestCheckGroup(
    string title, StatusTone tone, string glyph, bool isExpanded,
    IReadOnlyList<PullRequestCheckViewModel> items)
{
    public string Title { get; } = title;
    public StatusTone Tone { get; } = tone;
    public string Glyph { get; } = glyph;
    public bool IsExpanded { get; } = isExpanded;
    public IReadOnlyList<PullRequestCheckViewModel> Items { get; } = items;
    public int Count => Items.Count;
    public string CountText => Count.ToString(CultureInfo.CurrentCulture);
    public string AccessibleName => $"{Title}, {Count} {(Count == 1 ? "check" : "checks")}";
}

/// <summary>One label chip, or the trailing overflow chip when labels did not fit.</summary>
public sealed class PullRequestLabelChip(string text, string? color, string toolTip)
{
    public string Text { get; } = text;

    /// <summary>GitHub's six-digit label color, or null for the overflow chip.</summary>
    public string? Color { get; } = color;
    public bool HasColor => Color is not null;
    public string ToolTip { get; } = toolTip;
}

public sealed class PullRequestCheckViewModel(PullRequestCheck check)
{
    public string Name { get; } = string.IsNullOrWhiteSpace(check.Name) ? "Unnamed check" : check.Name;
    public string State { get; } = StateText(check.State);
    internal CheckState RawState { get; } = check.State;
    public string AccessibleName => $"{Name}: {State}";

    internal static StatusTone ToneFor(CheckState state) => state switch
    {
        CheckState.Passed => StatusTone.Success,
        CheckState.Failed => StatusTone.Failure,
        CheckState.Pending or CheckState.Running => StatusTone.Progress,
        CheckState.Cancelled or CheckState.ActionRequired or CheckState.Unknown => StatusTone.Caution,
        _ => StatusTone.Neutral
    };

    internal static string StateText(CheckState state) => state switch
    {
        CheckState.Pending => "Pending",
        CheckState.Running => "Running",
        CheckState.Passed => "Passed",
        CheckState.Failed => "Failed",
        CheckState.Neutral => "Neutral",
        CheckState.Skipped => "Skipped",
        CheckState.Cancelled => "Cancelled",
        CheckState.ActionRequired => "Action required",
        _ => "Unknown"
    };

    internal static string StateGlyph(CheckState state) => state switch
    {
        CheckState.Pending => "\uE823", // clock
        CheckState.Running => "\uE768", // nonanimated play
        CheckState.Passed => "\uE73E",
        CheckState.Failed => "\uEA39",
        CheckState.Neutral => "\uE738",
        CheckState.Skipped => "\uE893",
        CheckState.Cancelled => "\uE711",
        CheckState.ActionRequired => "\uE7BA",
        _ => "\uE9CE"
    };
}
