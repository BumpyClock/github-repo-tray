namespace GitHubTray.Core;

public enum ContributionLevel
{
    None,
    First,
    Second,
    Third,
    Fourth
}

public sealed record ContributionDay(DateOnly Date, int Count, ContributionLevel Level);

public sealed record ContributionWeek(DateOnly FirstDay, IReadOnlyList<ContributionDay> Days);

public sealed record ContributionCalendar(int TotalContributions, IReadOnlyList<ContributionWeek> Weeks);

public sealed record ContributionSection(
    ContributionCalendar? Calendar,
    DateTimeOffset? UpdatedAt,
    string? Error)
{
    public bool IsStale => Calendar is not null && Error is not null;
}
