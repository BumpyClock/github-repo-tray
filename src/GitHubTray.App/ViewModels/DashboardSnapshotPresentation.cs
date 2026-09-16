using GitHubTray.Core;

namespace GitHubTray_App.ViewModels;

public static class DashboardSnapshotPresentation
{
    public static bool ShouldShowContributionFeedback(bool loading, bool hasDays, bool hasError) =>
        hasError || !loading && !hasDays;

    public static string AccountDescription(
        DashboardSnapshot snapshot,
        bool isRefreshing,
        bool isVerified = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!isVerified)
        {
            return isRefreshing
                ? $"Saved github.com account: @{snapshot.User.Login}. Showing data saved on this device with its original timestamps while account verification continues."
                : $"Saved github.com account: @{snapshot.User.Login}. Showing data saved on this device with its original timestamps; this account is not verified for the current process.";
        }

        var showingCache =
            snapshot.Activity.Source == DashboardSectionSource.Cached ||
            snapshot.PullRequests.Source == DashboardSectionSource.Cached ||
            snapshot.ReviewRequests.Source == DashboardSectionSource.Cached ||
            snapshot.Repositories.Source == DashboardSectionSource.Cached ||
            snapshot.Contributions.Source == DashboardSectionSource.Cached ||
            snapshot.Copilot.Source == DashboardSectionSource.Cached;
        if (showingCache)
        {
            return isRefreshing
                ? $"Verified github.com account: @{snapshot.User.Login}. Showing retained data from this device while live requests continue."
                : $"Verified github.com account: @{snapshot.User.Login}. Showing retained data from this device.";
        }

        return $"Last verified github.com account: @{snapshot.User.Login}. Resolved by gh for this process; environment credentials can take precedence over the stored CLI account.";
    }

    public static string CachedContributionStatus(
        ContributionSection contributions,
        bool isRefreshing) =>
        contributions.UpdatedAt is { } cachedAt
            ? isRefreshing
                ? $"Cached from {cachedAt.ToLocalTime():g} · refreshing contributions…"
                : $"Cached from {cachedAt.ToLocalTime():g}"
            : isRefreshing
                ? "Cached contributions · refreshing…"
                : "Cached contributions";
}
