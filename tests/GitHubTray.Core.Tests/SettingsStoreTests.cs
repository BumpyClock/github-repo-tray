using System.Text.Json;
using GitHubTray.Core;

namespace GitHubTray.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GitHubTray.Tests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public async Task MissingSettingsUseTheDocumentedDefaultWithoutWritingAFile()
    {
        Assert.Equal(5, (await new SettingsStore(SettingsPath).LoadAsync()).RefreshMinutes);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public async Task SavesRoundTripAndReplacePreviousSettings()
    {
        var store = new SettingsStore(SettingsPath);
        await store.SaveAsync(new AppSettings(10));
        await store.SaveAsync(new AppSettings(2));
        Assert.Equal(2, (await store.LoadAsync()).RefreshMinutes);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    public async Task CorruptSettingsAreReportedAndNotOverwritten(string contents)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SettingsPath, contents);
        await Assert.ThrowsAsync<JsonException>(() => new SettingsStore(SettingsPath).LoadAsync());
        Assert.Equal(contents, await File.ReadAllTextAsync(SettingsPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public async Task InvalidRefreshIntervalsCannotReplaceValidSettings(int interval)
    {
        var store = new SettingsStore(SettingsPath);
        await store.SaveAsync(new AppSettings(5));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync(new AppSettings(interval)));
        Assert.Equal(5, (await store.LoadAsync()).RefreshMinutes);
    }

    [Fact]
    public async Task InvalidSavedIntervalIsReported()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SettingsPath, """{"RefreshMinutes":0}""");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new SettingsStore(SettingsPath).LoadAsync());
    }

    [Fact]
    public async Task CancellationDoesNotOverwriteExistingSettingsOrLeaveTemporaryFiles()
    {
        var store = new SettingsStore(SettingsPath);
        await store.SaveAsync(new AppSettings(5));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new AppSettings(10), cancellation.Token));
        Assert.Equal(5, (await store.LoadAsync()).RefreshMinutes);
        Assert.Single(Directory.GetFiles(_directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            foreach (var file in Directory.GetFiles(_directory))
            {
                File.Delete(file);
            }
            Directory.Delete(_directory);
        }
    }
}
