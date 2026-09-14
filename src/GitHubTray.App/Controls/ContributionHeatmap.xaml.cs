using GitHubTray.Core;
using GitHubTray_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace GitHubTray_App.Controls;

/// <summary>
/// WinUI has no built-in contribution chart. One keyboard-focusable native control
/// owns a Grid of noninteractive date cells; no per-day buttons or custom animation.
/// </summary>
public sealed partial class ContributionHeatmap : UserControl
{
    public static readonly DependencyProperty CalendarProperty = DependencyProperty.Register(
        nameof(Calendar), typeof(ContributionCalendar), typeof(ContributionHeatmap),
        new PropertyMetadata(null, OnCalendarChanged));

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(string), typeof(ContributionHeatmap), new PropertyMetadata("Loading contributions…"));

    public ContributionHeatmap()
    {
        InitializeComponent();
    }

    public ContributionHeatmapViewModel ViewModel { get; } = new();

    public ContributionCalendar? Calendar
    {
        get => (ContributionCalendar?)GetValue(CalendarProperty);
        set => SetValue(CalendarProperty, value);
    }

    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private static void OnCalendarChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((ContributionHeatmap)sender).UpdateCalendar((ContributionCalendar?)args.NewValue);
    }

    private void UpdateCalendar(ContributionCalendar? calendar)
    {
        var change = ViewModel.UpdateCalendar(calendar);
        CalendarGrid.Children.Clear();
        CalendarGrid.ColumnDefinitions.Clear();
        for (var week = 0; week < ViewModel.WeekCount; week++)
        {
            CalendarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        foreach (var day in ViewModel.Days)
        {
            var cell = new ContributionDayCell(day)
            {
                // ThemeResource setters in these styles stay live across theme changes.
                // Use the supplied enum verbatim; there are no locally invented thresholds.
                Style = (Style)Resources[$"ContributionLevel{(int)day.Day.Level}Style"]
            };
            Grid.SetColumn(cell, day.WeekIndex);
            Grid.SetRow(cell, day.DayIndex);
            CalendarGrid.Children.Add(cell);
        }

        CalendarGrid.Children.Add(SelectionOutline);
        SynchronizeSelection(change, announce: false);
        UpdateCellSize();
    }

    private void CalendarGrid_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateCellSize();

    private void UpdateCellSize()
    {
        if (ViewModel.WeekCount > 0 && CalendarGrid.ActualWidth > 0)
        {
            var height = Math.Clamp(CalendarGrid.ActualWidth / ViewModel.WeekCount * 7, 42, 63);
            if (Math.Abs(CalendarGrid.Height - height) > 0.25)
            {
                CalendarGrid.Height = height;
            }
        }
    }

    protected override void OnKeyDown(KeyRoutedEventArgs args)
    {
        base.OnKeyDown(args);
        if (!ViewModel.HasDays)
        {
            return;
        }

        ContributionSelectionChange change;
        switch (args.Key)
        {
            case VirtualKey.Up:
                change = ViewModel.MoveSelection(-1);
                break;
            case VirtualKey.Down:
                change = ViewModel.MoveSelection(1);
                break;
            case VirtualKey.Left:
                change = ViewModel.MoveSelection(-7);
                break;
            case VirtualKey.Right:
                change = ViewModel.MoveSelection(7);
                break;
            case VirtualKey.Home:
                change = ViewModel.SelectIndex(0);
                break;
            case VirtualKey.End:
                change = ViewModel.SelectIndex(ViewModel.Days.Count - 1);
                break;
            default:
                return;
        }

        args.Handled = true;
        SynchronizeSelection(change, announce: true);
    }

    private void CalendarGrid_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var source = args.OriginalSource as DependencyObject;
        while (source is not null && source != CalendarGrid && source is not ContributionDayCell)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        if (source is ContributionDayCell cell)
        {
            var index = ViewModel.Days.ToList().IndexOf(cell.Day);
            var change = ViewModel.SelectIndex(index);
            Focus(FocusState.Pointer);
            SynchronizeSelection(change, announce: true);
            args.Handled = true;
        }
    }

    private void SynchronizeSelection(ContributionSelectionChange change, bool announce)
    {
        // A rebuilt calendar can move the outline without changing the selected value.
        if (change.Current?.PlotDay is { } selected)
        {
            Grid.SetColumn(SelectionOutline, selected.WeekIndex);
            Grid.SetRow(SelectionOutline, selected.DayIndex);
            SelectionOutline.Visibility = Visibility.Visible;
        }
        else
        {
            SelectionOutline.Visibility = Visibility.Collapsed;
        }
        if (!change.ValueChanged)
        {
            return;
        }

        var peer = FrameworkElementAutomationPeer.FromElement(this);
        peer?.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, change.PreviousDescription, change.CurrentDescription);
        if (!announce || change.Current is null)
        {
            return;
        }

        var dayPeer = FrameworkElementAutomationPeer.FromElement(SelectedDayText)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(SelectedDayText);
        dayPeer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new HeatmapAutomationPeer(this);

    private sealed class HeatmapAutomationPeer(ContributionHeatmap owner) : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        public bool IsReadOnly => true;
        public string Value => owner.ViewModel.SelectedDayDescription;

        public void SetValue(string value) => throw new InvalidOperationException("Use the arrow keys to inspect contribution days.");

        protected override string GetClassNameCore() => nameof(ContributionHeatmap);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override object GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Value ? this : base.GetPatternCore(patternInterface);
    }
}
