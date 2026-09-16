using System.Collections;
using GitHubTray.Core;
using GitHubTray_App.ViewModels;

namespace GitHubTray.App.Tests;

public sealed class CopilotUsageViewModelTests
{
    private static CopilotUsageSection Section(string? error = null) => new(
        new("enterprise",
        [
            new(CopilotQuotaKind.PremiumInteractions, CopilotQuotaAvailability.Limited, 75.7, true, null),
            new(CopilotQuotaKind.Chat, CopilotQuotaAvailability.Unlimited, null, true, null),
            new(CopilotQuotaKind.Completions, CopilotQuotaAvailability.Unlimited, null, true, null)
        ]), DateTimeOffset.UtcNow, error);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuotaItemsExposeAReferenceTypeReadOnlyListForNativeBinding(bool displayable)
    {
        var display = CopilotUsageViewModel.Create(Section(), displayable, false);
        object source = display.GetQuotaItems();

        Assert.False(source.GetType().IsValueType);
        var items = Assert.IsAssignableFrom<IList>(source);
        Assert.Equal(displayable ? 1 : 0, items.Count);
        Assert.True(items.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => items.Clear());
        if (displayable)
        {
            Assert.Same(display.Quotas[0], items[0]);
        }
    }

    [Fact]
    public void CreditUsageHasOneMeterWithoutUnlimitedChatOrCompletionRows()
    {
        var display = CopilotUsageViewModel.Create(Section(), true, false);
        Assert.Equal("Enterprise", display.Plan);
        var row = Assert.Single(display.Quotas);
        Assert.Equal("AI credits", row.Label);
        Assert.Equal(24.3, row.UsedPercent, precision: 8);
        Assert.True(row.HasMeter);
        Assert.Contains("used", row.ValueLabel);
        Assert.Equal("Enterprise", row.Plan);
        Assert.Equal("Reset time unavailable", row.ResetLabel);
        Assert.Contains("Account-wide", row.AccessibleName);
        Assert.False(display.HasStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingDashboardDoesNotProjectRetainedUsage(bool refreshing)
    {
        var display = CopilotUsageViewModel.Create(Section(), false, refreshing);
        Assert.Empty(display.Quotas);
        Assert.Empty(display.Plan);
        Assert.Equal(!refreshing, display.HasStatus);
    }

    [Fact]
    public void SavedUsageRemainsReadableWithAnUnverifiedTrustLabel()
    {
        var cached = Section() with { Source = DashboardSectionSource.Cached };

        var display = CopilotUsageViewModel.Create(
            cached,
            displayable: true,
            refreshing: true,
            accountVerified: false);

        Assert.Single(display.Quotas);
        Assert.Equal("Enterprise", display.Plan);
        Assert.Contains("Cached from", display.Status);
        Assert.Contains("Account unverified", display.Status);
    }

    [Fact]
    public void StaleUsageRemainsVisibleWithExplicitErrorAndLastSuccess()
    {
        var display = CopilotUsageViewModel.Create(Section("Offline"), true, false);
        Assert.Single(display.Quotas);
        Assert.True(display.HasError);
        Assert.Contains("Stale usage: Offline", display.Status);
        Assert.Contains("Last success", display.Status);
        var unavailable = CopilotUsageViewModel.Create(new(null, null, "Denied"), true, false);
        Assert.Empty(unavailable.Quotas);
        Assert.Contains("Usage unavailable: Denied", unavailable.Status);
    }

    [Fact]
    public void CachedUsageKeepsProvenanceWithoutRefreshStatusNoise()
    {
        var cached = Section() with { Source = DashboardSectionSource.Cached };
        var display = CopilotUsageViewModel.Create(cached, true, true);
        Assert.Single(display.Quotas);
        Assert.StartsWith("Cached from", display.Status);
        Assert.DoesNotContain("refresh", display.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(display.HasError);
    }

    [Fact]
    public void LegacyAndUnlimitedPremiumDoNotClaimCreditUsageOrInventAMeter()
    {
        var row = new CopilotQuotaViewModel(new(CopilotQuotaKind.PremiumInteractions,
            CopilotQuotaAvailability.Unlimited, null, false, null));
        Assert.Equal("Premium requests", row.Label);
        Assert.Equal("Unlimited", row.ValueLabel);
        Assert.False(row.HasMeter);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public void EmptyAndFullQuotasAreNotConfused(double remaining, double used)
    {
        var row = new CopilotQuotaViewModel(new(CopilotQuotaKind.PremiumInteractions,
            CopilotQuotaAvailability.Limited, remaining, true, null));
        Assert.Equal(used, row.UsedPercent);
        Assert.True(row.HasMeter);
    }

    [Theory]
    [InlineData(4620, "Resets in 3d 5h")]
    [InlineData(320, "Resets in 5h 20m")]
    [InlineData(12, "Resets in 12m")]
    [InlineData(0.5, "Resets in <1m")]
    [InlineData(0, "Reset pending")]
    [InlineData(-1, "Reset pending")]
    public void ResetCountdownHandlesShortDurationsAndExpiredSnapshots(double minutes, string expected)
    {
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var row = new CopilotQuotaViewModel(new(CopilotQuotaKind.PremiumInteractions,
            CopilotQuotaAvailability.Limited, 50, true, now.AddMinutes(minutes))) { ObservedAt = now };
        Assert.Equal(expected, row.ResetLabel);
    }

    [Fact]
    public void ReprojectingAtTheNextClockTickUpdatesCountdownWithoutFetching()
    {
        var now = DateTimeOffset.UtcNow;
        var section = new CopilotUsageSection(new("enterprise",
            [new(CopilotQuotaKind.PremiumInteractions, CopilotQuotaAvailability.Limited, 50, true, now.AddMinutes(10))]),
            now, null);
        var first = CopilotUsageViewModel.Create(section, true, false, now);
        var next = CopilotUsageViewModel.Create(section, true, false, now.AddMinutes(1));
        Assert.Equal("Resets in 10m", first.Quotas[0].ResetLabel);
        Assert.Equal("Resets in 9m", next.Quotas[0].ResetLabel);
    }
}
