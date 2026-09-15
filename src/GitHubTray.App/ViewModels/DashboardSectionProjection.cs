using GitHubTray.Core;

namespace GitHubTray_App.ViewModels;

internal static class DashboardSectionProjection
{
    public static bool IsEquivalent(DashboardSection? projected, DashboardSection next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (projected is null ||
            projected.Source != next.Source ||
            projected.UpdatedAt != next.UpdatedAt ||
            !string.Equals(projected.Error, next.Error, StringComparison.Ordinal) ||
            projected.Items.Count != next.Items.Count)
        {
            return false;
        }

        for (var index = 0; index < projected.Items.Count; index++)
        {
            if (!ReferenceEquals(projected.Items[index], next.Items[index]))
            {
                return false;
            }
        }

        return true;
    }
}
