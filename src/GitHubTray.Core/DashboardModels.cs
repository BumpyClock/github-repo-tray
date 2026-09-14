namespace GitHubTray.Core;

public sealed record GitHubUser(string Login, string DisplayName, Uri Url);

public sealed record DashboardItem(
    string Id,
    string Title,
    string Repository,
    string Detail,
    DateTimeOffset UpdatedAt,
    Uri Url);

public sealed record DashboardSection(
    IReadOnlyList<DashboardItem> Items,
    DateTimeOffset? UpdatedAt,
    string? Error)
{
    public bool IsStale => Error is not null && UpdatedAt.HasValue;
}

public sealed record DashboardSnapshot(
    GitHubUser User,
    DashboardSection Activity,
    DashboardSection PullRequests,
    DashboardSection ReviewRequests,
    DashboardSection Repositories)
{
    public ContributionSection Contributions { get; init; } = new(null, null, null);
}

public interface IGitHubApi
{
    Task<string> GetAsync(string endpoint, CancellationToken cancellationToken = default);
    Task<string> QueryAsync(string query, CancellationToken cancellationToken = default);
}

public class GitHubException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class GitHubAccountChangedException(string message) : GitHubException(message);

public sealed record AppSettings(int RefreshMinutes = 5)
{
    public void Validate()
    {
        if (RefreshMinutes is < 1 or > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(RefreshMinutes), "Refresh interval must be between 1 and 60 minutes.");
        }
    }
}
