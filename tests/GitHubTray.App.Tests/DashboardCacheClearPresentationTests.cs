using GitHubTray.Core;
using GitHubTray_App.ViewModels;

namespace GitHubTray.App.Tests;

public sealed class DashboardCacheClearPresentationTests
{
    [Fact]
    public void SuccessfulMultiAccountClearExplainsScopeAndPreservedState()
    {
        var presentation = DashboardCacheClearPresentation.Create(new(
            3,
            0,
            new(DashboardCacheDiagnosticKind.Cleared, "Cleared.")));

        Assert.Empty(presentation.Warning);
        Assert.Contains("3 saved dashboard cache files", presentation.Status);
        Assert.Contains("across all accounts", presentation.Status);
        Assert.Contains("sign-in and settings were preserved", presentation.Status);
        Assert.Contains("remains visible", presentation.Status);
    }

    [Fact]
    public void EmptyClearIsStillSuccessfulWithoutClaimingFilesWereDeleted()
    {
        var presentation = DashboardCacheClearPresentation.Create(new(
            0,
            0,
            new(DashboardCacheDiagnosticKind.Cleared, "Nothing to clear.")));

        Assert.Empty(presentation.Warning);
        Assert.Contains("No saved dashboard data was present", presentation.Status);
    }

    [Fact]
    public void PartialFailureNeverReportsCompleteSuccess()
    {
        var presentation = DashboardCacheClearPresentation.Create(new(
            2,
            1,
            new(DashboardCacheDiagnosticKind.ClearFailed, "Partial failure.")));

        Assert.Empty(presentation.Status);
        Assert.Contains("only partially cleared", presentation.Warning);
        Assert.Contains("1 file remains", presentation.Warning);
        Assert.Contains("sign-in and settings were preserved", presentation.Warning);
    }
}
