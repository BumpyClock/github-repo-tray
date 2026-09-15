using System.Collections.Immutable;
using GitHubTray.Core;

namespace GitHubTray.AppState.Tests;

public sealed class DashboardSnapshotFreezeTests
{
    private static readonly DateTimeOffset UpdatedAt =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ImmutableOuterContributionWeeksStillFreezeMutableNestedDays()
    {
        var immutableDays = ImmutableArray.Create(
            new ContributionDay(new DateOnly(2026, 9, 6), 1, ContributionLevel.First));
        var mutableDays = new[]
        {
            new ContributionDay(new DateOnly(2026, 9, 7), 2, ContributionLevel.Second)
        };
        var immutableWeek = new ContributionWeek(new DateOnly(2026, 9, 6), immutableDays);
        var mutableWeek = new ContributionWeek(new DateOnly(2026, 9, 7), mutableDays);
        var snapshot = Snapshot(ImmutableArray.Create(immutableWeek, mutableWeek));

        var frozen = DashboardRefreshSession.FreezeSnapshot(snapshot);

        Assert.NotSame(snapshot, frozen);
        Assert.NotSame(snapshot.Contributions, frozen.Contributions);
        Assert.NotSame(snapshot.Contributions.Calendar, frozen.Contributions.Calendar);
        var weeks = Assert.IsType<ImmutableArray<ContributionWeek>>(
            frozen.Contributions.Calendar!.Weeks);
        Assert.Same(immutableWeek, weeks[0]);
        Assert.NotSame(mutableWeek, weeks[1]);
        Assert.IsType<ImmutableArray<ContributionDay>>(weeks[1].Days);

        var retainedDay = weeks[1].Days[0];
        mutableDays[0] = new(new DateOnly(2026, 9, 7), 99, ContributionLevel.Fourth);
        Assert.Equal(retainedDay, weeks[1].Days[0]);
    }

    [Fact]
    public void RepeatedFreezeOfMaxShapeFrozenSnapshotAllocatesNoManagedBytes()
    {
        var frozen = DashboardRefreshSession.FreezeSnapshot(MaxShapeSnapshot());
        Assert.Same(frozen, DashboardRefreshSession.FreezeSnapshot(frozen));
        for (var index = 0; index < 100; index++)
        {
            _ = DashboardRefreshSession.FreezeSnapshot(frozen);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        DashboardSnapshot? result = null;
        for (var index = 0; index < 10_000; index++)
        {
            result = DashboardRefreshSession.FreezeSnapshot(frozen);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        GC.KeepAlive(result);
        Assert.Same(frozen, result);
        Assert.Equal(0, allocated);
    }

    private static DashboardSnapshot MaxShapeSnapshot()
    {
        var sections = Enumerable.Range(0, 4)
            .Select(section => new DashboardSection(
                Enumerable.Range(0, DashboardService.ItemLimit)
                    .Select(index => Item($"{section}-{index}"))
                    .ToArray(),
                UpdatedAt,
                null))
            .ToArray();
        var firstDay = new DateOnly(2025, 8, 31);
        var weeks = Enumerable.Range(0, 54)
            .Select(weekIndex =>
            {
                var weekStart = firstDay.AddDays(weekIndex * 7);
                return new ContributionWeek(
                    weekStart,
                    Enumerable.Range(0, 7)
                        .Select(dayIndex => new ContributionDay(
                            weekStart.AddDays(dayIndex),
                            dayIndex,
                            dayIndex == 0 ? ContributionLevel.None : ContributionLevel.First))
                        .ToArray());
            })
            .ToArray();
        return new(
            User(),
            sections[0],
            sections[1],
            sections[2],
            sections[3])
        {
            Contributions = new(new ContributionCalendar(1134, weeks), UpdatedAt, null)
        };
    }

    private static DashboardSnapshot Snapshot(IReadOnlyList<ContributionWeek> weeks)
    {
        var empty = new DashboardSection(
            ImmutableArray<DashboardItem>.Empty,
            UpdatedAt,
            null);
        return new(User(), empty, empty, empty, empty)
        {
            Contributions = new(new ContributionCalendar(3, weeks), UpdatedAt, null)
        };
    }

    private static DashboardItem Item(string id) =>
        new(
            id,
            $"Item {id}",
            "owner/repository",
            "Detail",
            UpdatedAt,
            new Uri($"https://github.com/owner/repository/issues/{id.Replace('-', '/')}"));

    private static GitHubUser User() =>
        new("github.com", 1, "octocat", "The Octocat", new("https://github.com/octocat"));
}
