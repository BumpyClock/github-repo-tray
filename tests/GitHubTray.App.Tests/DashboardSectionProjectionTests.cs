using GitHubTray.Core;
using GitHubTray_App.ViewModels;

namespace GitHubTray.App.Tests;

public sealed class DashboardSectionProjectionTests
{
    private static readonly DateTimeOffset UpdatedAt =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EquivalentWrapperWithSameItemInstancesCanReuseRows()
    {
        var item = Item("same");
        var projected = Section([item]);
        var next = Section([item]);

        Assert.True(DashboardSectionProjection.IsEquivalent(projected, next));
    }

    [Fact]
    public void ReplacingAnItemInvalidatesProjectionEvenWhenValuesMatch()
    {
        var projected = Section([Item("same")]);
        var next = Section([Item("same")]);

        Assert.False(DashboardSectionProjection.IsEquivalent(projected, next));
    }

    [Theory]
    [InlineData(DashboardSectionSource.Retained, "failed")]
    [InlineData(DashboardSectionSource.Live, "failed")]
    [InlineData(DashboardSectionSource.Retained, null)]
    public void MetadataChangesInvalidateProjection(DashboardSectionSource source, string? error)
    {
        var item = Item("same");
        var projected = Section([item]);
        var next = Section([item], source, error);

        Assert.False(DashboardSectionProjection.IsEquivalent(projected, next));
    }

    [Fact]
    public void TimestampAndCountChangesInvalidateProjection()
    {
        var item = Item("same");
        var projected = Section([item]);
        var later = Section([item]) with { UpdatedAt = UpdatedAt.AddMinutes(1) };
        var extra = Section([item, Item("extra")]);

        Assert.False(DashboardSectionProjection.IsEquivalent(projected, later));
        Assert.False(DashboardSectionProjection.IsEquivalent(projected, extra));
    }

    private static DashboardSection Section(
        IReadOnlyList<DashboardItem> items,
        DashboardSectionSource source = DashboardSectionSource.Live,
        string? error = null) =>
        new(items, UpdatedAt, error) { Source = source };

    private static DashboardItem Item(string id) =>
        new(id, "Title", "octocat/tray", "Detail", UpdatedAt,
            new Uri($"https://github.com/octocat/tray/{id}"));
}
