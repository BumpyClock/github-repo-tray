using GitHubTray_App.ViewModels;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace GitHubTray_App.Controls;

/// <summary>A noninteractive data point: accessible as text, never a button or tab stop.</summary>
public sealed partial class ContributionDayCell : Control
{
    public ContributionDayCell(ContributionPlotDay day)
    {
        Day = day;
        IsTabStop = false;
        AutomationProperties.SetAutomationId(this, day.AutomationId);
        AutomationProperties.SetName(this, day.Description);
        ToolTipService.SetToolTip(this, day.Description);
    }

    public ContributionPlotDay Day { get; }

    protected override AutomationPeer OnCreateAutomationPeer() => new DayCellAutomationPeer(this);

    private sealed partial class DayCellAutomationPeer(ContributionDayCell owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(ContributionDayCell);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override bool IsKeyboardFocusableCore() => false;
    }
}
