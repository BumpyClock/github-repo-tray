using GitHubTray.Core;

namespace GitHubTray_App.ViewModels;

public sealed record DashboardCacheClearPresentation(string Status, string Warning)
{
    public static DashboardCacheClearPresentation Create(DashboardCacheClearResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Succeeded)
        {
            var status = result.DeletedFileCount == 0
                ? "No saved dashboard data was present. GitHub sign-in and settings were preserved."
                : $"Cleared {result.DeletedFileCount} saved dashboard cache {(result.DeletedFileCount == 1 ? "file" : "files")} across all accounts. GitHub sign-in and settings were preserved. Current dashboard data remains visible until the next refresh.";
            return new(status, "");
        }

        var warning = result.FailedFileCount > 0
            ? $"Cached data was only partially cleared: {result.DeletedFileCount} {(result.DeletedFileCount == 1 ? "file was" : "files were")} deleted, but {result.FailedFileCount} {(result.FailedFileCount == 1 ? "file remains" : "files remain")}. GitHub sign-in and settings were preserved. Close other processes using the app's local data folder, then try again."
            : "Cached data could not be cleared. GitHub sign-in and settings were preserved. Try again after checking access to the app's local data folder.";
        return new("", warning);
    }
}
