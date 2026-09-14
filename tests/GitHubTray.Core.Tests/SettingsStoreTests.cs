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
        var settings = await new SettingsStore(SettingsPath).LoadAsync();
        Assert.Equal(5, settings.RefreshMinutes);
        Assert.Equal(ContributionCellSizePreset.Medium, settings.ContributionCellSize);
        Assert.False(File.Exists(SettingsPath));
    }

    [Theory]
    [InlineData("{}", 5)]
    [InlineData("""{"RefreshMinutes":10}""", 10)]
    [InlineData("""{"RefreshMinutes":11,"ContributionZoomFactor":1.75}""", 11)]
    public async Task LegacySettingsUseMediumWithoutChangingTheFile(string contents, int interval)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SettingsPath, contents);
        Assert.Equal(new AppSettings(interval), await new SettingsStore(SettingsPath).LoadAsync());
        Assert.Equal(contents, await File.ReadAllTextAsync(SettingsPath));
    }

    [Theory]
    [InlineData(ContributionCellSizePreset.Small)]
    [InlineData(ContributionCellSizePreset.Medium)]
    [InlineData(ContributionCellSizePreset.Large)]
    public async Task PresetAndIntervalRoundTripAtomically(ContributionCellSizePreset preset)
    {
        var store = new SettingsStore(SettingsPath);
        await store.SaveAsync(new AppSettings(10, ContributionCellSizePreset.Large));
        var replacement = new AppSettings(2, preset);
        await store.SaveAsync(replacement);
        Assert.Equal(replacement, await store.LoadAsync());
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(SettingsPath));
        Assert.Equal(preset.ToString(), json.RootElement.GetProperty("ContributionCellSize").GetString());
        Assert.False(json.RootElement.TryGetProperty("ContributionZoomFactor", out _));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("""{"ContributionCellSize":"Small"}""", ContributionCellSizePreset.Small)]
    [InlineData("""{"ContributionCellSize":"Medium"}""", ContributionCellSizePreset.Medium)]
    [InlineData("""{"ContributionCellSize":"Large"}""", ContributionCellSizePreset.Large)]
    public async Task PresetOnlySettingsUseDefaultInterval(string contents, ContributionCellSizePreset preset)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SettingsPath, contents);
        Assert.Equal(new AppSettings(5, preset), await new SettingsStore(SettingsPath).LoadAsync());
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("""{"ContributionCellSize":"ExtraLarge"}""")]
    [InlineData("""{"ContributionCellSize":null}""")]
    [InlineData("""{"ContributionCellSize":true}""")]
    [InlineData("""{"ContributionCellSize":1.5}""")]
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

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public async Task InvalidPresetCannotReplaceValidSettings(int value)
    {
        var store = new SettingsStore(SettingsPath);
        var previous = new AppSettings(5, ContributionCellSizePreset.Large);
        await store.SaveAsync(previous);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync(new AppSettings(10, (ContributionCellSizePreset)value)));
        Assert.Equal(previous, await store.LoadAsync());
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("""{"RefreshMinutes":0}""")]
    [InlineData("""{"ContributionCellSize":-1}""")]
    [InlineData("""{"ContributionCellSize":3}""")]
    public async Task InvalidSavedSettingsAreReportedWithoutChangingTheFile(string contents)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SettingsPath, contents);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new SettingsStore(SettingsPath).LoadAsync());
        Assert.Equal(contents, await File.ReadAllTextAsync(SettingsPath));
    }

    [Fact]
    public async Task CancellationDoesNotOverwriteExistingSettingsOrLeaveTemporaryFiles()
    {
        var store = new SettingsStore(SettingsPath);
        var previous = new AppSettings(5, ContributionCellSizePreset.Large);
        await store.SaveAsync(previous);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(
            new AppSettings(10, ContributionCellSizePreset.Small), cancellation.Token));
        Assert.Equal(previous, await store.LoadAsync());
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
