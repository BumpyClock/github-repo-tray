using CommunityToolkit.Mvvm.ComponentModel;

namespace GitHubTray_App.ViewModels;

public sealed partial class DashboardViewModel
{
    [ObservableProperty]
    public partial CopilotUsageViewModel CopilotDisplay { get; private set; } =
        CopilotUsageViewModel.Create(null, displayable: false, refreshing: true);
}
