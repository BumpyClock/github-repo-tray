using GitHubTray.Core;
using GitHubTray_App.ViewModels;

namespace GitHubTray.App.Tests;

public sealed class ContributionViewportTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    [InlineData(2.5)]
    public void EveryPresetHasSevenUnclippedRowsAndPixelAlignedSquares(double scale)
    {
        foreach (var preset in Enum.GetValues<ContributionCellSizePreset>())
        {
            var layout = ContributionViewport.GetCellLayout(scale, 440, 53, preset);
            Assert.True(layout.RowPitch * 7 <= ContributionViewport.PlotHeight - ContributionViewport.TopInset - ContributionViewport.ScrollbarSpace);
            for (var week = 0; week < 53; week++)
            {
                var cell = ContributionViewport.GetWeekCell(layout, week, scale);
                Assert.InRange(cell.CellSize, 1 / scale, cell.RowPitch);
                Assert.True(cell.TopInset + cell.CellSize <= cell.RowPitch);
                Assert.Equal(Math.Round(cell.CellSize * scale), cell.CellSize * scale, 8);
                Assert.Equal(Math.Round(cell.LeftInset * scale), cell.LeftInset * scale, 8);
                Assert.Equal(Math.Round(cell.TopInset * scale), cell.TopInset * scale, 8);
            }
        }
    }

    [Theory]
    [InlineData(300, 1)]
    [InlineData(440, 1.25)]
    [InlineData(458, 1.5)]
    [InlineData(500, 2)]
    public void MediumFitsTheEntireYearExactly(double width, double scale)
    {
        var layout = ContributionViewport.GetCellLayout(scale, width, 53, ContributionCellSizePreset.Medium);
        var actualWidth = Enumerable.Range(0, 53).Sum(week => ContributionViewport.GetWeekCell(layout, week, scale).WeekPitch);
        Assert.Equal(Math.Round(width * scale) / scale, actualWidth, 8);
    }

    [Fact]
    public void LargeKeepsReadableCellsAndUsesHorizontalHistory()
    {
        var medium = ContributionViewport.GetCellLayout(1.5, 440, 53, ContributionCellSizePreset.Medium);
        var large = ContributionViewport.GetCellLayout(1.5, 440, 53, ContributionCellSizePreset.Large);
        Assert.True(large.CellSize > medium.CellSize);
        Assert.True(large.CellSize >= 16);
        Assert.True(large.WeekPitch * 53 > 440);
        Assert.InRange(large.RowPitch - large.WeekPitch, 0, 1 / 1.5);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void ContainerShrinksAlongWithTheSelectedPreset(double scale)
    {
        var small = ContributionViewport.GetCellLayout(scale, 440, 53, ContributionCellSizePreset.Small);
        var medium = ContributionViewport.GetCellLayout(scale, 440, 53, ContributionCellSizePreset.Medium);
        var large = ContributionViewport.GetCellLayout(scale, 440, 53, ContributionCellSizePreset.Large);
        Assert.True(small.PlotHeight < medium.PlotHeight);
        Assert.True(medium.PlotHeight < large.PlotHeight);
        Assert.True(large.PlotHeight <= ContributionViewport.PlotHeight);
    }

    [Fact]
    public void PresentAnchoringKeepsTheFinalWeekOnTheRight()
    {
        var anchor = ContributionViewport.Capture(53 * 18 - 440, 440, 18, 53);
        Assert.True(anchor.IsAtPresent);
        Assert.Equal(53 * 18 - 400, ContributionViewport.Restore(anchor, 400, 18, 53));
    }

    [Fact]
    public void ResizingInHistoryPreservesTheDateAtTheRightEdge()
    {
        var anchor = ContributionViewport.Capture(100, 300, 18, 53);
        var offset = ContributionViewport.Restore(anchor, 250, 18, 53);
        Assert.False(anchor.IsAtPresent);
        Assert.Equal(400, offset + 250);
    }

    [Fact]
    public void ShortCalendarsAndFirstLayoutStayWithinTheirScrollBounds()
    {
        Assert.Equal(0, ContributionViewport.Restore(new(true, 0), 500, 10, 4));
        Assert.True(ContributionViewport.Capture(0, 0, 10, 53).IsAtPresent);
        Assert.Equal(0, ContributionViewport.Restore(new(false, 2), 400, 10, 53));
    }

    [Fact]
    public void KeyboardSelectionRevealsOnlyOffscreenWeeks()
    {
        Assert.Equal(90, ContributionViewport.RevealWeek(20, 90, 300, 13.5, 53));
        Assert.Equal(0, ContributionViewport.RevealWeek(0, 90, 300, 13.5, 53));
        Assert.Equal(415.5, ContributionViewport.RevealWeek(52, 90, 300, 13.5, 53));
    }
}
