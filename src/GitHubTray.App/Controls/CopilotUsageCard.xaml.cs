using GitHubTray_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GitHubTray_App.Controls;

public sealed partial class CopilotUsageCard : UserControl
{
    private bool _released;

    public CopilotUsageCard() => InitializeComponent();

    internal void ReleaseForHide()
    {
        if (_released) return;
        _released = true;
        Bindings.StopTracking();
        ClearValue(ViewModelProperty);
        DataContext = null;
    }

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(CopilotUsageViewModel), typeof(CopilotUsageCard),
        new PropertyMetadata(CopilotUsageViewModel.Create(null, displayable: false, refreshing: true)));

    public CopilotUsageViewModel ViewModel
    {
        get => (CopilotUsageViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
