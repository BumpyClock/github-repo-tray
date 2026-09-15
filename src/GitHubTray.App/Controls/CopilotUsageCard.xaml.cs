using GitHubTray_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GitHubTray_App.Controls;

public sealed partial class CopilotUsageCard : UserControl
{
    public CopilotUsageCard() => InitializeComponent();

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(CopilotUsageViewModel), typeof(CopilotUsageCard),
        new PropertyMetadata(CopilotUsageViewModel.Create(null, verified: false, refreshing: true)));

    public CopilotUsageViewModel ViewModel
    {
        get => (CopilotUsageViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
