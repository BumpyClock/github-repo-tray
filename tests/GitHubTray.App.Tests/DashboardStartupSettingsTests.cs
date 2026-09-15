using GitHubTray.Core;
using GitHubTray_App.ViewModels;

namespace GitHubTray.App.Tests;

public sealed class DashboardStartupSettingsTests
{
    [Fact]
    public async Task ValidSettingsSupplyStartupRefreshInterval()
    {
        var interval = await DashboardStartupSettings.ResolveRefreshIntervalAsync(
            Task.FromResult(new AppSettings(17)));

        Assert.Equal(TimeSpan.FromMinutes(17), interval);
    }

    [Theory]
    [MemberData(nameof(ExpectedFailures))]
    public async Task ExpectedSettingsFailuresUseDefaultInterval(Exception exception)
    {
        var interval = await DashboardStartupSettings.ResolveRefreshIntervalAsync(
            Task.FromException<AppSettings>(exception));

        Assert.Equal(TimeSpan.FromMinutes(5), interval);
    }

    [Fact]
    public async Task UnexpectedSettingsFailureStillPropagates()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DashboardStartupSettings.ResolveRefreshIntervalAsync(
                Task.FromException<AppSettings>(new InvalidOperationException("unexpected"))));
    }

    public static TheoryData<Exception> ExpectedFailures() =>
    [
        new IOException("io"),
        new UnauthorizedAccessException("denied"),
        new System.Text.Json.JsonException("json"),
        new ArgumentException("invalid"),
        new System.Security.SecurityException("security")
    ];
}
