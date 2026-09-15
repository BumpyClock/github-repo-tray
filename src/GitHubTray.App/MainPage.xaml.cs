using System.Runtime.InteropServices;
using GitHubTray_App.Controls;
using GitHubTray_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace GitHubTray_App;

public sealed partial class MainPage : Page
{
    private readonly MainWindow _window;
    private readonly KeyEventHandler _keyDownHandler;
    private readonly Dictionary<SelectorItem, MenuFlyout> _contextMenus = [];
    private readonly HashSet<PullRequestCard> _loadedCards = [];
    private ContentDialog? _clearCacheDialog;
    private bool _initializing = true;
    private bool _released;

    public MainPage(DashboardViewModel viewModel, MainWindow window)
    {
        ViewModel = viewModel;
        _window = window;
        _keyDownHandler = OnPageKeyDown;
        InitializeComponent();
        for (var index = 0; index < ViewModel.Sections.Count && index < ModeSelector.Items.Count; index++)
        {
            if (ReferenceEquals(ViewModel.Sections[index], ViewModel.SelectedSection))
            {
                ModeSelector.SelectedItem = ModeSelector.Items[index];
                break;
            }
        }
        _initializing = false;
        AddHandler(KeyDownEvent, _keyDownHandler, true);
    }

    public DashboardViewModel ViewModel { get; }

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static bool Not(bool value) => !value;
    public static bool HasError(string? error) => error is not null;

    public void SetPanelVisible(bool visible)
    {
        if (_released) return;
        ViewModel.SetPanelVisible(visible);
        ContributionGraph.SetPanelVisible(visible);
        if (visible)
        {
            ContributionGraph.ReturnToPresent();
        }
    }

    public void OpenSettings()
    {
        if (_released) return;
        ViewModel.IsSettingsOpen = true;
        BackButton.Focus(FocusState.Programmatic);
    }

    public void FocusPanel()
    {
        if (_released) return;
        if (ViewModel.IsSettingsOpen)
        {
            BackButton.Focus(FocusState.Programmatic);
        }
        else
        {
            ModeSelector.SelectedItem?.Focus(FocusState.Programmatic);
        }
    }

    public void ReleaseForHide()
    {
        if (_released) return;
        _released = true;
        Bindings.StopTracking();
        RemoveHandler(KeyDownEvent, _keyDownHandler);
        DashboardList.ContainerContentChanging -= DashboardList_ContainerContentChanging;
        DashboardList.ItemClick -= DashboardList_ItemClick;
        ModeSelector.SelectionChanged -= ModeSelector_SelectionChanged;

        _clearCacheDialog?.Hide();
        _clearCacheDialog = null;
        foreach (var container in _contextMenus.Keys.ToArray())
        {
            ReleaseContextMenu(container);
        }
        foreach (var card in _loadedCards.ToArray())
        {
            card.OpenChecksRequested -= PullRequestCard_OpenChecksRequested;
            card.Loaded -= PullRequestCard_Loaded;
            card.Unloaded -= PullRequestCard_Unloaded;
            card.ReleaseForHide();
        }
        _loadedCards.Clear();
        ContributionGraph.ReleaseForHide();

        // Snapshot before disconnecting sources: recycling can detach children immediately.
        var elements = EnumerateVisualTree(this).ToArray();
        foreach (var element in elements)
        {
            XamlBindingHelper.GetDataTemplateComponent(element)?.Recycle();
            if (element is PullRequestCard card)
            {
                card.OpenChecksRequested -= PullRequestCard_OpenChecksRequested;
                card.Loaded -= PullRequestCard_Loaded;
                card.Unloaded -= PullRequestCard_Unloaded;
                card.ReleaseForHide();
            }
            else if (element is CopilotUsageCard usage)
            {
                usage.ReleaseForHide();
            }
            if (element is ButtonBase button)
            {
                button.Command = null;
            }
            if (element is Button flyoutButton)
            {
                flyoutButton.Flyout?.Hide();
                flyoutButton.Flyout = null;
            }
            if (element is ComboBox comboBox)
            {
                comboBox.IsDropDownOpen = false;
            }
            if (element is ProgressRing progress)
            {
                progress.IsActive = false;
            }
            // Keep two-way preference inputs intact while their control callbacks retire.
            if (element is ItemsControl items && items is not Selector)
            {
                items.ItemsSource = null;
            }
            if (ToolTipService.GetToolTip(element) is ToolTip tooltip)
            {
                tooltip.IsOpen = false;
                tooltip.PlacementTarget = null;
                tooltip.Content = null;
                tooltip.XamlRoot = null;
            }
            ToolTipService.SetToolTip(element, null);
            if (element is FrameworkElement frameworkElement)
            {
                frameworkElement.ContextFlyout?.Hide();
                frameworkElement.ContextFlyout = null;
                frameworkElement.DataContext = null;
            }
        }
        DashboardList.ItemsSource = null;
        DataContext = null;
        Content = null;
    }

    private static IEnumerable<DependencyObject> EnumerateVisualTree(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in EnumerateVisualTree(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void RetryTrayButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_released) _window.RetryTray();
    }
    private async void QuitButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_released) await _window.QuitAsync();
    }

    private async void ClearCachedDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (_released || _clearCacheDialog is not null || !ViewModel.CanClearCachedData)
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
        _clearCacheDialog = dialog;
        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            if (!_released)
            {
                ViewModel.ActionError =
                    "The confirmation dialog could not be shown. Close any open dialog and try again.";
            }
            return;
        }
        finally
        {
            if (ReferenceEquals(_clearCacheDialog, dialog))
            {
                _clearCacheDialog = null;
            }
            dialog.XamlRoot = null;
        }
        if (!_released && result == ContentDialogResult.Primary)
        {
            await ViewModel.ClearCachedDataAsync();
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_released) return;
        ViewModel.IsSettingsOpen = false;
        SettingsButton.Focus(FocusState.Programmatic);
    }

    private void ModeSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_initializing && !_released
            && sender.SelectedItem?.Tag is string tag && int.TryParse(tag, out var index)
            && index >= 0 && index < ViewModel.Sections.Count)
        {
            ViewModel.SelectedSection = ViewModel.Sections[index];
        }
    }

    private async void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_released) return;
        args.Handled = true;
        await ViewModel.RefreshAsync();
    }

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!_released && args.Key == VirtualKey.Escape && !args.Handled)
        {
            args.Handled = true;
            _window.HidePanel();
        }
    }

    private async void DashboardList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (!_released && e.ClickedItem is DashboardRow row)
        {
            await OpenRowAsync(row);
        }
    }

    private async void OpenOnGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (!_released && sender is MenuFlyoutItem { Tag: DashboardRow row })
        {
            await OpenRowAsync(row);
        }
    }

    private void PullRequestCard_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not PullRequestCard card) return;
        if (_released)
        {
            card.ReleaseForHide();
            return;
        }
        if (_loadedCards.Add(card))
        {
            card.OpenChecksRequested += PullRequestCard_OpenChecksRequested;
        }
    }

    private void PullRequestCard_Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is PullRequestCard card)
        {
            _loadedCards.Remove(card);
            card.OpenChecksRequested -= PullRequestCard_OpenChecksRequested;
        }
    }

    private async void PullRequestCard_OpenChecksRequested(object? sender, PullRequestActionEventArgs args)
    {
        if (_released || sender is not PullRequestCard card)
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
        if (_released || !ViewModel.HasDisplayableData || !ViewModel.CanOpenRow(row))
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
            if (!await Launcher.LaunchUriAsync(uri) && !_released)
            {
                ViewModel.ActionError = "Windows could not open the GitHub link. Check your default browser and try again.";
            }
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException)
        {
            if (!_released)
            {
                ViewModel.ActionError = "Windows could not open the GitHub link. Check your default browser and try again.";
            }
        }
    }

    private void DashboardList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        ReleaseContextMenu(args.ItemContainer);
        if (_released || args.InRecycleQueue)
        {
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
            _contextMenus.Add(args.ItemContainer, menu);
            args.ItemContainer.ContextFlyout = menu;
        }
    }

    private void ReleaseContextMenu(SelectorItem container)
    {
        if (_contextMenus.Remove(container, out var menu))
        {
            menu.Hide();
            foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
            {
                item.Click -= OpenOnGitHub_Click;
                item.Tag = null;
            }
            menu.Items.Clear();
        }
        container.ContextFlyout = null;
        container.ClearValue(AutomationProperties.NameProperty);
    }
}
