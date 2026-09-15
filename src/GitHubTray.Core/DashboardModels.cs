using System.Text.Json.Serialization;

namespace GitHubTray.Core;

public sealed record GitHubUser(string Host, long Id, string Login, string DisplayName, Uri Url);

public enum DashboardSectionSource
{
    Missing,
    Live,
    Cached,
    Retained,
    Failed
}

public sealed record DashboardItem(
    string Id,
    string Title,
    string Repository,
    string Detail,
    DateTimeOffset UpdatedAt,
    Uri Url)
{
    public PullRequestDetails? PullRequest { get; init; }
    public PullRequestActivity? PullRequestActivity { get; init; }
}

public sealed record PullRequestActivity(int Number, string LatestAction, int EventCount);

public sealed record DashboardSection(
    IReadOnlyList<DashboardItem> Items,
    DateTimeOffset? UpdatedAt,
    string? Error)
{
    public DashboardSectionSource Source { get; init; } = DashboardSectionSource.Live;
    public bool IsStale => Error is not null && UpdatedAt.HasValue;
}

public sealed record DashboardSnapshot(
    GitHubUser User,
    DashboardSection Activity,
    DashboardSection PullRequests,
    DashboardSection ReviewRequests,
    DashboardSection Repositories)
{
    public ContributionSection Contributions { get; init; } = new(null, null, null)
    {
        Source = DashboardSectionSource.Missing
    };
    public CopilotUsageSection Copilot { get; init; } = new(null, null, null)
    {
        Source = DashboardSectionSource.Missing
    };
}

[Flags]
public enum DashboardSectionKind
{
    None = 0,
    Activity = 1 << 0,
    PullRequests = 1 << 1,
    ReviewRequests = 1 << 2,
    Repositories = 1 << 3,
    Contributions = 1 << 4,
    Copilot = 1 << 5,
    All = Activity | PullRequests | ReviewRequests | Repositories | Contributions | Copilot
}

public enum DashboardRefreshReason
{
    Startup,
    Periodic,
    Manual
}

public sealed record DashboardRefreshRequest(
    DashboardRefreshReason Reason,
    TimeSpan RefreshInterval)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Reason))
        {
            throw new ArgumentOutOfRangeException(nameof(Reason));
        }

        if (Reason is not DashboardRefreshReason.Manual && RefreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RefreshInterval),
                "Automatic refresh requires a positive configured refresh interval.");
        }
    }
}

public sealed record DashboardRefreshResult(
    DashboardSnapshot Snapshot,
    DashboardSectionKind ReusedSections)
{
    public bool ReusedAnySection => ReusedSections is not DashboardSectionKind.None;
}

public interface IGitHubApi
{
    Task<string> GetAsync(string endpoint, CancellationToken cancellationToken = default);
    Task<string> QueryAsync(string query, CancellationToken cancellationToken = default);
}

public class GitHubException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class GitHubAccountChangedException(string message) : GitHubException(message);

[JsonConverter(typeof(JsonStringEnumConverter<ContributionCellSizePreset>))]
public enum ContributionCellSizePreset
{
    Small,
    Medium,
    Large
}

public sealed record AppSettings(
    int RefreshMinutes = 5,
    ContributionCellSizePreset ContributionCellSize = ContributionCellSizePreset.Medium)
{
    public void Validate()
    {
        if (RefreshMinutes is < 1 or > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(RefreshMinutes), "Refresh interval must be between 1 and 60 minutes.");
        }

        if (!Enum.IsDefined(ContributionCellSize))
        {
            throw new ArgumentOutOfRangeException(nameof(ContributionCellSize), "Contribution cell size must be Small, Medium, or Large.");
        }
    }
}
