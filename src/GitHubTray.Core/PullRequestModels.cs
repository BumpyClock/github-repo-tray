using System.Collections.Immutable;

namespace GitHubTray.Core;

public enum CheckState
{
    Unknown,
    Pending,
    Running,
    Passed,
    Failed,
    Neutral,
    Skipped,
    Cancelled,
    ActionRequired
}

public enum CheckRollupState
{
    Unknown,
    NoChecks,
    Pending,
    Passed,
    Failed
}

public sealed record PullRequestLabel(string Name, string Color);

public sealed record PullRequestCheck(string Name, CheckState State);

public enum PullRequestState
{
    Open,
    Closed,
    Merged
}

public sealed record CommitChecks(
    string? CommitOid,
    CheckRollupState State,
    ImmutableArray<PullRequestCheck> Items,
    int TotalCount)
{
    public bool IsTruncated => Items.Length < TotalCount;
}

public sealed record PullRequestDetails(
    int Number,
    string Title,
    string AuthorLogin,
    Uri? AuthorAvatarUrl,
    bool IsDraft,
    string HeadRefName,
    string BaseRefName,
    string? ReviewDecision,
    int CommentCount,
    ImmutableArray<PullRequestLabel> Labels,
    int LabelCount,
    CommitChecks Checks)
{
    public PullRequestState State { get; init; } = PullRequestState.Open;
}
