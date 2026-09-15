using GitHubTray_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace GitHubTray_App.Controls;

public sealed partial class ContributionHeatmap
{
    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(
        nameof(IsLoading), typeof(bool), typeof(ContributionHeatmap),
        new PropertyMetadata(true, OnLoadingChanged));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(ContributionHeatmap),
        new PropertyMetadata(true, OnActiveChanged));

    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private readonly List<Border> _skeletonCells = [];
    private Storyboard? _shimmer;
    private int _shimmerFirstVisibleWeek;
    private bool _panelVisible = true;
    private bool _plotInvalidated = true;
    private bool _motionSettingsSubscribed;
    private bool _released;

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private static void OnLoadingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (ContributionHeatmap)sender;
        if (control.CalendarGrid is null)
        {
            return;
        }
        if (!control.ViewModel.HasDays)
        {
            control.TryRebuildPlot();
        }
        else
        {
            control.UpdateShimmer();
        }
    }

    private static void OnActiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (ContributionHeatmap)sender;
        if (control.CalendarGrid is not null)
        {
            // Preferences collapse the graph. Its scroll extent must be settled
            // again on activation even if it returns to the same dimensions.
            control._appliedViewport = null;
            if (control.IsActive)
            {
                // Let the parent's visibility binding finish before restoring the viewport.
                control.DispatcherQueue.TryEnqueue(control.EnsurePlotCurrent);
            }
            control.UpdateShimmer();
        }
    }

    public void SetPanelVisible(bool visible)
    {
        if (_released) return;
        _panelVisible = visible;
        if (!visible)
        {
            ReleasePlotVisuals();
        }
        else
        {
            EnsurePlotCurrent();
        }
    }

    internal void ReleaseForHide()
    {
        if (_released) return;
        _released = true;
        _panelVisible = false;
        DetachMotionSettings();
        CloseSelectionTooltip();
        if (_keyboardTooltip is not null)
        {
            _keyboardTooltip.XamlRoot = null;
            _keyboardTooltip = null;
        }
        ReleasePlotVisuals();
        Calendar = null;
        Bindings.StopTracking();
        Loaded -= Heatmap_Loaded;
        Unloaded -= Heatmap_Unloaded;
        LostFocus -= Heatmap_LostFocus;
        PlotOverlay.SizeChanged -= PlotOverlay_SizeChanged;
        PlotScroll.SizeChanged -= PlotScroll_SizeChanged;
        PlotScroll.ViewChanged -= PlotScroll_ViewChanged;
        CalendarGrid.Tapped -= CalendarGrid_Tapped;
        PlotOverlay.Clip = null;
        DataContext = null;
    }

    private void Heatmap_Loaded(object sender, RoutedEventArgs args)
    {
        if (_released) return;
        if (!_motionSettingsSubscribed)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                _uiSettings.AnimationsEnabledChanged += MotionSettingsChanged;
            }
            _uiSettings.ColorValuesChanged += MotionSettingsChanged;
            _motionSettingsSubscribed = true;
        }
        EnsurePlotCurrent();
    }

    private void Heatmap_Unloaded(object sender, RoutedEventArgs args)
    {
        CloseSelectionTooltip();
        DetachMotionSettings();
        StopShimmer();
        _appliedViewport = null;
    }

    private void DetachMotionSettings()
    {
        if (!_motionSettingsSubscribed) return;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _uiSettings.AnimationsEnabledChanged -= MotionSettingsChanged;
        }
        _uiSettings.ColorValuesChanged -= MotionSettingsChanged;
        _motionSettingsSubscribed = false;
    }

    private void MotionSettingsChanged(UISettings sender, object args)
    {
        if (!_released) DispatcherQueue.TryEnqueue(UpdateShimmer);
    }

    // DashboardViewModel publishes the latest hidden snapshot immediately before this
    // control is made visible. Setters mark the plot dirty until that reveal callback.
    private bool CanRealizePlot => !_released && IsLoaded && _panelVisible && IsActive;

    private bool TryRebuildPlot()
    {
        _plotInvalidated = true;
        if (!CanRealizePlot)
        {
            StopShimmer();
            return false;
        }

        RebuildPlot();
        return true;
    }

    private void EnsurePlotCurrent()
    {
        if (!CanRealizePlot)
        {
            StopShimmer();
            return;
        }

        if (_plotInvalidated)
        {
            RebuildPlot();
            SynchronizeSelection(ViewModel.SelectIndex(ViewModel.SelectedIndex), announce: false);
        }
        else
        {
            ApplyViewport();
            UpdateShimmer();
        }
    }

    private void ReleasePlotVisuals()
    {
        if (_plotInvalidated && CalendarGrid.Children.Count == 0)
        {
            return;
        }

        CloseSelectionTooltip();
        _pendingAnchor ??= CaptureViewport();
        StopShimmer();
        _skeletonCells.Clear();
        CalendarGrid.Children.Clear();
        CalendarGrid.ColumnDefinitions.Clear();
        CalendarGrid.Width = 0;
        SelectionOutline.Visibility = Visibility.Collapsed;
        GraphPlaceholder.Visibility = Visibility.Collapsed;
        _plotWeekCount = 0;
        _appliedViewport = null;
        _plotInvalidated = true;
    }

    private void RebuildPlot()
    {
        _pendingAnchor ??= CaptureViewport();
        // Release animation targets before detaching cells, even if the new plot
        // has exactly the same dimensions as the old one.
        StopShimmer();
        _skeletonCells.Clear();
        _appliedViewport = null;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var firstDay = ViewModel.HasDays ? ViewModel.Days[0].Day.Date : today.AddYears(-1);
        var lastDay = ViewModel.HasDays ? ViewModel.Days[^1].Day.Date : today;
        var firstWeek = firstDay.AddDays(-(int)firstDay.DayOfWeek);
        _plotWeekCount = (lastDay.DayNumber - firstWeek.DayNumber) / 7 + 1;
        CalendarGrid.Children.Clear();
        CalendarGrid.ColumnDefinitions.Clear();
        for (var week = 0; week < _plotWeekCount; week++)
        {
            CalendarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_cellLayout.WeekPitch) });
        }
        CalendarGrid.Width = _plotWeekCount * _cellLayout.WeekPitch;

        if (ViewModel.HasDays)
        {
            foreach (var day in ViewModel.Days)
            {
                var cell = new ContributionDayCell(day)
                {
                    Style = (Style)Resources[$"ContributionLevel{(int)day.Day.Level}Style"]
                };
                Grid.SetColumn(cell, day.WeekIndex);
                Grid.SetRow(cell, day.DayIndex);
                CalendarGrid.Children.Add(cell);
            }
        }
        else if (IsLoading)
        {
            for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
            {
                var cell = new Border { Style = (Style)Resources["ContributionSkeletonCellStyle"] };
                Grid.SetColumn(cell, (day.DayNumber - firstWeek.DayNumber) / 7);
                Grid.SetRow(cell, (int)day.DayOfWeek);
                CalendarGrid.Children.Add(cell);
                _skeletonCells.Add(cell);
            }
        }

        CalendarGrid.Children.Add(SelectionOutline);
        GraphPlaceholder.Visibility = ViewModel.HasDays || IsLoading ? Visibility.Collapsed : Visibility.Visible;
        ApplyViewport();
        UpdateShimmer();
        _plotInvalidated = false;
    }

    private void UpdateShimmer()
    {
        if (_released || !IsLoaded || !_panelVisible || !IsActive || !IsLoading || ViewModel.HasDays
            || _skeletonCells.Count == 0 || !_uiSettings.AnimationsEnabled || _accessibilitySettings.HighContrast)
        {
            StopShimmer();
            return;
        }

        var firstVisibleWeek = (int)Math.Floor(PlotScroll.HorizontalOffset / _cellLayout.WeekPitch);
        if (_shimmer is not null && _shimmerFirstVisibleWeek == firstVisibleWeek)
        {
            return;
        }

        StopShimmer();
        _shimmer = new Storyboard();
        _shimmerFirstVisibleWeek = firstVisibleWeek;
        foreach (var cell in _skeletonCells)
        {
            cell.Opacity = 0.6;
            var animation = new DoubleAnimation
            {
                From = 0.6,
                To = 1,
                Duration = new Duration(TimeSpan.FromSeconds(0.8)),
                BeginTime = TimeSpan.FromMilliseconds(Math.Max(0, Grid.GetColumn(cell) - firstVisibleWeek) * 25),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(animation, cell);
            Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
            _shimmer.Children.Add(animation);
        }
        _shimmer.Begin();
    }

    private void StopShimmer()
    {
        if (_shimmer is null)
        {
            return;
        }
        _shimmer.Stop();
        _shimmer.Children.Clear();
        _shimmer = null;
        foreach (var cell in _skeletonCells)
        {
            cell.Opacity = 1;
        }
    }
}
