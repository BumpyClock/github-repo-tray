using GitHubTray.Core;
using GitHubTray_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace GitHubTray_App.Controls;

public sealed partial class ContributionHeatmap
{
    public static readonly DependencyProperty CellSizePresetProperty = DependencyProperty.Register(
        nameof(CellSizePreset), typeof(ContributionCellSizePreset), typeof(ContributionHeatmap),
        new PropertyMetadata(ContributionCellSizePreset.Medium, OnCellSizePresetChanged));

    private ContributionViewportAnchor? _pendingAnchor;
    private ContributionCellLayout _cellLayout = new(8, 10, 10, 1, 1, 1);
    private (double Scale, double Width, int Weeks, ContributionCellSizePreset Preset)? _appliedViewport;
    private bool _returnToPresent = true;
    private bool _layingOut;
    private int _plotWeekCount;
    private ToolTip? _keyboardTooltip;
    private bool _showTooltipAfterScroll;

    public ContributionCellSizePreset CellSizePreset
    {
        get => (ContributionCellSizePreset)GetValue(CellSizePresetProperty);
        set => SetValue(CellSizePresetProperty, value);
    }

    private static void OnCellSizePresetChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (ContributionHeatmap)sender;
        if (control.PlotScroll is not null)
        {
            control.ReturnToPresent();
        }
    }

    private ContributionViewportAnchor CaptureViewport() => ContributionViewport.Capture(
        PlotScroll.HorizontalOffset, PlotScroll.ViewportWidth, _cellLayout.WeekPitch, _plotWeekCount);

    private void ApplyViewport()
    {
        if (_layingOut || !CanRealizePlot || PlotScroll.ViewportWidth <= 0 || _plotWeekCount == 0)
        {
            return;
        }

        _layingOut = true;
        try
        {
            var anchor = _returnToPresent ? new ContributionViewportAnchor(true, _plotWeekCount)
                : _pendingAnchor ?? CaptureViewport();
            var scale = XamlRoot.RasterizationScale;
            var viewport = (scale, PlotScroll.ViewportWidth, _plotWeekCount, CellSizePreset);
            // Returning to the present still restores selection and scroll position, but
            // unchanged inputs do not require rewriting every cell or forcing layout.
            // RebuildPlot invalidates this snapshot whenever the children are replaced.
            if (_appliedViewport != viewport)
            {
                _cellLayout = ContributionViewport.GetCellLayout(scale, PlotScroll.ViewportWidth, _plotWeekCount, CellSizePreset);
                for (var week = 0; week < CalendarGrid.ColumnDefinitions.Count; week++)
                {
                    CalendarGrid.ColumnDefinitions[week].Width = new GridLength(
                        ContributionViewport.GetWeekCell(_cellLayout, week, scale).WeekPitch);
                }
                foreach (var row in CalendarGrid.RowDefinitions)
                {
                    row.Height = new GridLength(_cellLayout.RowPitch);
                }
                CalendarGrid.Width = Math.Round(_plotWeekCount * _cellLayout.WeekPitch * scale) / scale;
                CalendarGrid.Height = 7 * _cellLayout.RowPitch;
                PlotScroll.Padding = new Thickness(0, 0, 0,
                    ContributionViewport.GetScrollbarSpace(CalendarGrid.Width, PlotScroll.ViewportWidth));
                GraphContainer.Height = _cellLayout.GetPlotHeight(CalendarGrid.Width, PlotScroll.ViewportWidth);
                foreach (var child in CalendarGrid.Children.OfType<FrameworkElement>())
                {
                    LayoutCell(child);
                }
                SelectionOutline.BorderThickness = new Thickness(_cellLayout.StrokeThickness);
                CalendarGrid.UpdateLayout();
                _appliedViewport = viewport;
            }
            UpdatePlotClip();
            var offset = ContributionViewport.Restore(anchor, PlotScroll.ViewportWidth, _cellLayout.WeekPitch, _plotWeekCount);
            _returnToPresent = false;
            _pendingAnchor = null;
            PlotScroll.ChangeView(offset, 0, null, disableAnimation: true);
            UpdateViewportStatus();
        }
        finally
        {
            _layingOut = false;
        }
    }

    private void UpdatePlotClip()
    {
        if (_released) return;
        // Overlay bounds can change without changing the cell geometry.
        if (PlotOverlay.Clip is not RectangleGeometry clip)
        {
            clip = new RectangleGeometry();
            PlotOverlay.Clip = clip;
        }
        var bounds = new Rect(0, 0, PlotOverlay.ActualWidth, PlotOverlay.ActualHeight);
        if (!clip.Rect.Equals(bounds))
        {
            clip.Rect = bounds;
        }
    }

    private void LayoutCell(FrameworkElement child)
    {
        var layout = ContributionViewport.GetWeekCell(_cellLayout, Grid.GetColumn(child), XamlRoot.RasterizationScale);
        child.Width = layout.CellSize;
        child.Height = layout.CellSize;
        child.Margin = new Thickness(layout.LeftInset, layout.TopInset, 0, 0);
        child.HorizontalAlignment = HorizontalAlignment.Left;
        child.VerticalAlignment = VerticalAlignment.Top;
        if (child is Control cell)
        {
            cell.BorderThickness = new Thickness(layout.StrokeThickness);
        }
    }

    private void ReturnToPresent()
    {
        if (_released) return;
        CloseSelectionTooltip();
        _returnToPresent = true;
        _pendingAnchor = null;
        SynchronizeSelection(ViewModel.SelectIndex(ViewModel.Days.Count - 1), announce: false);
        ApplyViewport();
    }

    private void PlotScroll_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        ApplyViewport();
        UpdateShimmer();
    }

    private void PlotScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (_released) return;
        if (!args.IsIntermediate && _showTooltipAfterScroll)
        {
            _showTooltipAfterScroll = false;
            ShowSelectionTooltip();
        }
        else if (args.IsIntermediate && _keyboardTooltip is not null)
        {
            _keyboardTooltip.IsOpen = false;
        }
        UpdateViewportStatus();
    }

    private void UpdateViewportStatus()
    {
        AutomationProperties.SetItemStatus(this, $"{CellSizePreset} contribution cells.");
    }

    private void RevealSelection()
    {
        if (ViewModel.SelectedDay is not { } selected)
        {
            return;
        }
        var offset = ContributionViewport.RevealWeek(
            selected.WeekIndex, PlotScroll.HorizontalOffset, PlotScroll.ViewportWidth, _cellLayout.WeekPitch, _plotWeekCount);
        _showTooltipAfterScroll = Math.Abs(offset - PlotScroll.HorizontalOffset) > 0.5;
        if (_showTooltipAfterScroll)
        {
            PlotScroll.ChangeView(offset, 0, null, disableAnimation: true);
        }
        else
        {
            ShowSelectionTooltip();
        }
    }

    private void ShowSelectionTooltip()
    {
        if (_released || XamlRoot is null) return;
        var cell = CalendarGrid.Children.OfType<ContributionDayCell>()
            .FirstOrDefault(candidate => candidate.Day == ViewModel.SelectedDay);
        if (cell is null)
        {
            return;
        }
        if (_keyboardTooltip is null)
        {
            _keyboardTooltip = new ToolTip();
            // IsOpen requires an attached owner, not just a PlacementTarget.
            ToolTipService.SetToolTip(SelectionOutline, _keyboardTooltip);
        }
        _keyboardTooltip.IsOpen = false;
        _keyboardTooltip.XamlRoot = XamlRoot;
        _keyboardTooltip.PlacementTarget = cell;
        _keyboardTooltip.Content = cell.Day.Description;
        _keyboardTooltip.IsOpen = true;
    }

    private void CloseSelectionTooltip()
    {
        _showTooltipAfterScroll = false;
        if (_keyboardTooltip is not null)
        {
            _keyboardTooltip.IsOpen = false;
            _keyboardTooltip.PlacementTarget = null;
            _keyboardTooltip.Content = null;
        }
    }
}
