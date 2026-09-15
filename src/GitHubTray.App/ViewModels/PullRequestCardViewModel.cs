using System.Collections.Immutable;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GitHubTray.Core;

namespace GitHubTray_App.ViewModels;

/// <summary>
/// Presentation of one PR at one section refresh. Only the clock changes in place;
/// a new section result replaces this model, including its explicit freshness.
/// No WinUI types, timers, fetching, or navigation live here.
/// </summary>
public sealed class PullRequestCardViewModel : ObservableObject
{
    private string _relativeTimestamp = "";

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
        ReviewDecisionText = details.ReviewDecision switch
        {
            "APPROVED" => "Approved",
            "CHANGES_REQUESTED" => "Changes requested",
            "REVIEW_REQUIRED" => "Review required",
            null or "" => "",
            _ => "Review status unknown"
        };
        ReviewDecisionGlyph = details.ReviewDecision switch
        {
            "APPROVED" => "\uE73E",
            "CHANGES_REQUESTED" => "\uE7BA",
            _ => "\uE8F2"
        };
        CommentSummary = $"{details.CommentCount} {(details.CommentCount == 1 ? "comment" : "comments")}";

        var labels = details.Labels.IsDefault ? ImmutableArray<PullRequestLabel>.Empty : details.Labels;
        Labels = labels.Take(3).ToArray();
        LabelCount = details.LabelCount;
        AdditionalLabelCount = Math.Max(0, LabelCount - Labels.Count);
        LabelsToolTip = LabelCount == 0 ? "No labels"
            : $"{LabelCount} {(LabelCount == 1 ? "label" : "labels")}: {string.Join(", ", labels.Select(label => label.Name))}"
                + (labels.Length < LabelCount ? $" · Showing names for {labels.Length} of {LabelCount} labels." : "");

        var checks = details.Checks;
        var items = checks.Items.IsDefault ? ImmutableArray<PullRequestCheck>.Empty : checks.Items;
        Checks = items.Select(check => new PullRequestCheckViewModel(check)).ToArray();
        IsChecksTruncated = items.Length < checks.TotalCount;
        CheckCountLabel = IsChecksTruncated
            ? $"Showing {items.Length} of {checks.TotalCount} checks"
            : $"{items.Length} {(items.Length == 1 ? "check" : "checks")}";
        var (summary, glyph) = SummarizeChecks(checks.State, items, IsChecksTruncated);
        ChecksSummary = IsStale ? $"Stale · {summary}" : summary;
        ChecksGlyph = IsStale ? "\uE7BA" : glyph;
        CheckStateCounts = FormatStateCounts(items);
        ChecksExplanation = IsChecksTruncated
            ? $"{AggregateDescription(checks.State)} Individual outcomes below cover only the loaded checks, not exact progress."
            : CheckStateCounts;
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
    public string StateGlyph => State switch
    {
        PullRequestState.Merged => "\uE73E", // CheckMark
        PullRequestState.Closed => "\uE711", // Cancel
        _ => IsDraft ? "\uE70F" : "\uEA3A" // Edit / CircleRing
    };
    public string StateDescription => $"{(IsStale ? "Last known state" : "Current state")}: {StateLabel}."
        + (IsStale ? " Section refresh failed." : "");
    public string Branches { get; }
    public string ReviewDecisionText { get; }
    public string ReviewDecisionGlyph { get; }
    public bool HasReviewDecision => ReviewDecisionText.Length != 0;
    public string CommentSummary { get; }
    public IReadOnlyList<PullRequestLabel> Labels { get; }
    public int LabelCount { get; }
    public bool HasLabels => LabelCount > 0;
    public int AdditionalLabelCount { get; }
    public bool HasAdditionalLabels => AdditionalLabelCount > 0;
    public string AdditionalLabelsText => $"+{AdditionalLabelCount}";
    public string LabelsToolTip { get; }
    public IReadOnlyList<PullRequestCheckViewModel> Checks { get; }
    public bool HasChecks => Checks.Count != 0;
    public bool IsChecksTruncated { get; }
    public string CheckCountLabel { get; }
    public string ChecksSummary { get; }
    public string ChecksGlyph { get; }
    public string CheckStateCounts { get; }
    public string ChecksExplanation { get; }
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
    public int ActivityEventCount => Item.PullRequestActivity?.EventCount ?? 0;
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

    private static (string Text, string Glyph) SummarizeChecks(
        CheckRollupState aggregate, ImmutableArray<PullRequestCheck> checks, bool isTruncated)
    {
        // GraphQL's aggregate covers checks we have not loaded. Never infer progress
        // or all-passed from the first page, even when every loaded check passed.
        if (isTruncated)
        {
            return aggregate switch
            {
                CheckRollupState.Passed => ("Checks successful · partial list", "\uE9D9"),
                CheckRollupState.Failed => ("Checks failed · partial list", "\uEA39"),
                CheckRollupState.Pending => ("Checks pending · partial list", "\uE823"),
                _ => ("Checks unknown · partial list", "\uE9CE")
            };
        }
        if (checks.Length == 0)
        {
            return aggregate == CheckRollupState.NoChecks
                ? ("No checks", "\uE90A") : ("Checks unknown", "\uE9CE");
        }
        // Preserve failure/pending aggregate evidence, including unexpected mismatches.
        if (checks.Any(check => check.State == CheckState.ActionRequired))
            return ("Checks need action", "\uE7BA");
        if (aggregate == CheckRollupState.Failed || checks.Any(check => check.State == CheckState.Failed))
            return ("Checks failed", "\uEA39");
        if (checks.Any(check => check.State == CheckState.Running))
            return ("Checks running", "\uE768");
        if (aggregate == CheckRollupState.Pending || checks.Any(check => check.State == CheckState.Pending))
            return ("Checks pending", "\uE823");
        if (aggregate == CheckRollupState.Unknown || checks.Any(check => check.State == CheckState.Unknown))
            return ("Checks unknown", "\uE9CE");

        var distinctStates = checks.Select(check => check.State).Distinct().ToArray();
        if (distinctStates.Length == 1)
        {
            var state = distinctStates[0];
            // A success aggregate alone cannot turn neutral/skipped/cancelled into passed.
            if (state != CheckState.Passed || aggregate == CheckRollupState.Passed)
                return ($"Checks {PullRequestCheckViewModel.StateText(state).ToLowerInvariant()} · {checks.Length}",
                    PullRequestCheckViewModel.StateGlyph(state));
            return ("Checks unknown", "\uE9CE");
        }
        return checks.Any(check => check.State == CheckState.Cancelled)
            ? ("Checks include cancelled", "\uE711")
            : ("Checks completed · mixed outcomes", "\uE9D9");
    }

    private static string FormatStateCounts(ImmutableArray<PullRequestCheck> checks) =>
        string.Join(" · ", checks.GroupBy(check => check.State).OrderBy(group => group.Key)
            .Select(group => $"{group.Count()} {PullRequestCheckViewModel.StateText(group.Key).ToLowerInvariant()}"));

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

public sealed class PullRequestCheckViewModel(PullRequestCheck check)
{
    public string Name { get; } = string.IsNullOrWhiteSpace(check.Name) ? "Unnamed check" : check.Name;
    public string State { get; } = StateText(check.State);
    public string Glyph { get; } = StateGlyph(check.State);
    public string AccessibleName => $"{Name}: {State}";

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
