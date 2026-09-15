using System.Runtime.InteropServices;
using GitHubTray_App.Controls;
using GitHubTray_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace GitHubTray_App;

public sealed partial class MainPage : Page
{
    private readonly MainWindow _window;

    public MainPage(DashboardViewModel viewModel, MainWindow window)
    {
        ViewModel = viewModel;
        _window = window;
        InitializeComponent();
        AddHandler(KeyDownEvent, new KeyEventHandler(OnPageKeyDown), true);
    }

    public DashboardViewModel ViewModel { get; }

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static bool Not(bool value) => !value;
    public static bool HasError(string? error) => error is not null;

    public void SetPanelVisible(bool visible)
    {
        ViewModel.SetPanelVisible(visible);
        ContributionGraph.SetPanelVisible(visible);
        if (visible)
        {
            ContributionGraph.ReturnToPresent();
        }
    }

    public void OpenSettings()
    {
        ViewModel.IsSettingsOpen = true;
        BackButton.Focus(FocusState.Programmatic);
    }

    public void FocusPanel()
    {
        if (ViewModel.IsSettingsOpen)
        {
            BackButton.Focus(FocusState.Programmatic);
        }
        else
        {
            ModeSelector.SelectedItem?.Focus(FocusState.Programmatic);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void RetryTrayButton_Click(object sender, RoutedEventArgs e) => _window.RetryTray();
    private async void QuitButton_Click(object sender, RoutedEventArgs e) => await _window.QuitAsync();

    private async void ClearCachedDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanClearCachedData)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear cached dashboard data?",
            Content = "Saved dashboard data for every cached GitHub account on this device will be removed. GitHub CLI authentication and settings are preserved. The current dashboard may remain visible, but the next refresh fetches every section.",
            PrimaryButtonText = "Clear cached data",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ClearCachedDataAsync();
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsSettingsOpen = false;
        SettingsButton.Focus(FocusState.Programmatic);
    }

    private void ModeSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag && int.TryParse(tag, out var index)
            && index >= 0 && index < ViewModel.Sections.Count)
        {
            ViewModel.SelectedSection = ViewModel.Sections[index];
        }
    }

    private async void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ViewModel.RefreshAsync();
    }

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape && !args.Handled)
        {
            args.Handled = true;
            _window.HidePanel();
        }
    }

    private async void DashboardList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DashboardRow row)
        {
            await OpenRowAsync(row);
        }
    }

    private async void OpenOnGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: DashboardRow row })
        {
            await OpenRowAsync(row);
        }
    }

    private async void PullRequestCard_OpenChecksRequested(object? sender, PullRequestActionEventArgs args)
    {
        if (sender is not PullRequestCard card)
            return;
        var row = ViewModel.SelectedSection.Items.FirstOrDefault(candidate => candidate.Item.Id == args.PullRequestId);
        // Resolve the identifier against the current view, not data retained by a
        // popup after refresh/account changes. Authorization stays with the page.
        if (row?.PullRequest is not { } presentation || !ReferenceEquals(card.Data, presentation))
            return;
        if (presentation.ChecksUri is { } uri)
            await OpenRowAsync(row, uri);
    }

    private async Task OpenRowAsync(DashboardRow row)
        => await OpenRowAsync(row, row.Item.Url);

    private async Task OpenRowAsync(DashboardRow row, Uri uri)
    {
        if (!ViewModel.IsAccountVerified || !ViewModel.CanOpenRow(row))
        {
            return;
        }

        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
        {
            ViewModel.ActionError = "Only HTTPS links on github.com can be opened.";
            return;
        }

        try
        {
            if (!await Launcher.LaunchUriAsync(uri))
            {
                ViewModel.ActionError = "Windows could not open the GitHub link. Check your default browser and try again.";
            }
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException)
        {
            ViewModel.ActionError = "Windows could not open the GitHub link. Check your default browser and try again.";
        }
    }

    private void DashboardList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            args.ItemContainer.ContextFlyout = null;
            args.ItemContainer.ClearValue(AutomationProperties.NameProperty);
            args.ItemContainer.ClearValue(AutomationProperties.AutomationIdProperty);
        }
        else if (args.Item is DashboardRow row)
        {
            AutomationProperties.SetAutomationId(args.ItemContainer, row.AutomationId);
            args.ItemContainer.SetBinding(AutomationProperties.NameProperty, new Binding
            {
                Source = row,
                Path = new PropertyPath(nameof(DashboardRow.AccessibleName)),
                Mode = BindingMode.OneWay
            });
            var openItem = new MenuFlyoutItem
            {
                Text = "Open on GitHub",
                Icon = new SymbolIcon(Symbol.OpenFile),
                Tag = row
            };
            AutomationProperties.SetAutomationId(openItem, $"OpenOnGitHub_{row.Item.Id}");
            AutomationProperties.SetName(openItem, "Open on GitHub");
            openItem.Click += OpenOnGitHub_Click;
            var menu = new MenuFlyout();
            menu.Items.Add(openItem);
            args.ItemContainer.ContextFlyout = menu;
        }
    }
}
