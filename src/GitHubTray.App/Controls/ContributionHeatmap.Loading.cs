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
    private Storyboard? _shimmer;
    private bool _panelVisible = true;

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
            control.RebuildPlot();
        }
        control.UpdateShimmer();
    }

    private static void OnActiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (ContributionHeatmap)sender;
        if (control.CalendarGrid is not null)
        {
            if (control.IsActive)
            {
                // Let the parent's visibility binding finish before restoring the viewport.
                control.DispatcherQueue.TryEnqueue(control.ApplyViewport);
            }
            control.UpdateShimmer();
        }
    }

    public void SetPanelVisible(bool visible)
    {
        _panelVisible = visible;
        if (!visible)
        {
            CloseSelectionTooltip();
        }
        UpdateShimmer();
    }

    private void Heatmap_Loaded(object sender, RoutedEventArgs args)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _uiSettings.AnimationsEnabledChanged += MotionSettingsChanged;
        }
        _uiSettings.ColorValuesChanged += MotionSettingsChanged;
        RebuildPlot();
        ApplyViewport();
        UpdateShimmer();
    }

    private void Heatmap_Unloaded(object sender, RoutedEventArgs args)
    {
        CloseSelectionTooltip();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _uiSettings.AnimationsEnabledChanged -= MotionSettingsChanged;
        }
        _uiSettings.ColorValuesChanged -= MotionSettingsChanged;
        _shimmer?.Stop();
        _shimmer = null;
    }

    private void MotionSettingsChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(UpdateShimmer);

    private void RebuildPlot()
    {
        _pendingAnchor ??= CaptureViewport();
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
            }
        }

        CalendarGrid.Children.Add(SelectionOutline);
        GraphPlaceholder.Visibility = ViewModel.HasDays || IsLoading ? Visibility.Collapsed : Visibility.Visible;
        ApplyViewport();
        UpdateShimmer();
    }

    private void UpdateShimmer()
    {
        _shimmer?.Stop();
        _shimmer = null;
        var cells = CalendarGrid.Children.OfType<Border>()
            .Where(cell => !ReferenceEquals(cell, SelectionOutline)).ToArray();
        foreach (var cell in cells)
        {
            cell.Opacity = 1;
        }
        if (!IsLoaded || !_panelVisible || !IsActive || !IsLoading || ViewModel.HasDays
            || !_uiSettings.AnimationsEnabled || _accessibilitySettings.HighContrast)
        {
            return;
        }

        _shimmer = new Storyboard();
        var firstVisibleWeek = (int)Math.Floor(PlotScroll.HorizontalOffset / _cellLayout.WeekPitch);
        foreach (var cell in cells)
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
}
