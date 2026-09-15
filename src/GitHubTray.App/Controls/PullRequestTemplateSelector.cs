using GitHubTray_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GitHubTray_App.Controls;

public sealed class PullRequestTemplateSelector : DataTemplateSelector
{
    public DataTemplate RowTemplate { get; set; } = null!;
    public DataTemplate PullRequestTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item) =>
        item is DashboardRow { PullRequest: not null } ? PullRequestTemplate : RowTemplate;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
