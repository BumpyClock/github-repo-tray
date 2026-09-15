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
/// A native calendar with preset cell sizes and one focus stop for day inspection.
/// Viewport and loading behavior are kept separate from selection synchronization.
/// </summary>
public sealed partial class ContributionHeatmap : UserControl
{
    public static readonly DependencyProperty CalendarProperty = DependencyProperty.Register(
        nameof(Calendar), typeof(ContributionCalendar), typeof(ContributionHeatmap),
        new PropertyMetadata(null, OnCalendarChanged));

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(string), typeof(ContributionHeatmap), new PropertyMetadata("Loading contributions…"));
    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.Register(
        nameof(HasError), typeof(bool), typeof(ContributionHeatmap), new PropertyMetadata(false));

    public ContributionHeatmap()
    {
        InitializeComponent();
        LostFocus += (_, _) => CloseSelectionTooltip();
        PlotOverlay.SizeChanged += (_, _) => UpdatePlotClip();
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
    public static Visibility FeedbackVisibility(bool loading, bool hasDays, bool hasError) =>
        loading || !hasDays || hasError ? Visibility.Visible : Visibility.Collapsed;

    public bool HasError
    {
        get => (bool)GetValue(HasErrorProperty);
        set => SetValue(HasErrorProperty, value);
    }

    private static void OnCalendarChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((ContributionHeatmap)sender).UpdateCalendar((ContributionCalendar?)args.NewValue);
    }

    private void UpdateCalendar(ContributionCalendar? calendar)
    {
        CloseSelectionTooltip();
        var change = ViewModel.UpdateCalendar(calendar);
        RebuildPlot();
        SynchronizeSelection(change, announce: false);
    }

    protected override void OnKeyDown(KeyRoutedEventArgs args)
    {
        base.OnKeyDown(args);
        if (XamlRoot is not null && !ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), this))
        {
            return;
        }
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
        RevealSelection();
    }

    private void CalendarGrid_Tapped(object sender, TappedRoutedEventArgs args)
    {
        var source = args.OriginalSource as DependencyObject;
        while (source is not null && source != CalendarGrid && source is not ContributionDayCell)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        if (source is ContributionDayCell cell)
        {
            CloseSelectionTooltip();
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
            if (XamlRoot is not null)
            {
                LayoutCell(SelectionOutline);
            }
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
        protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface switch
        {
            PatternInterface.Value => this,
            _ => base.GetPatternCore(patternInterface)
        };
    }
}
