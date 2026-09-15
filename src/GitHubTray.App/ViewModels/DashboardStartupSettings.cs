using GitHubTray.Core;

namespace GitHubTray_App.ViewModels;

internal static class DashboardStartupSettings
{
    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(5);

    public static async Task<TimeSpan> ResolveRefreshIntervalAsync(Task<AppSettings> settingsTask)
    {
        ArgumentNullException.ThrowIfNull(settingsTask);
        try
        {
            var settings = await settingsTask.ConfigureAwait(false);
            settings.Validate();
            return TimeSpan.FromMinutes(settings.RefreshMinutes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         System.Text.Json.JsonException or ArgumentException or
                                         System.Security.SecurityException)
        {
            return DefaultRefreshInterval;
        }
    }
}
