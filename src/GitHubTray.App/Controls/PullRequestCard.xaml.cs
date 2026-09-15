using System.Globalization;
using GitHubTray_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace GitHubTray_App.Controls;

public sealed partial class PullRequestCard : UserControl
{
    private static readonly AccessibilitySettings Accessibility = new();

    private readonly PullRequestChecksFlyoutState _checksFlyoutState = new();

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(PullRequestCardViewModel), typeof(PullRequestCard), new PropertyMetadata(null, OnDataChanged));

    public static readonly DependencyProperty ShowRepositoryProperty = DependencyProperty.Register(
        nameof(ShowRepository), typeof(bool), typeof(PullRequestCard), new PropertyMetadata(true));

    public PullRequestCard()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateStateAppearance();
        Unloaded += (_, _) => ChecksFlyout.Hide();
    }

    public PullRequestCardViewModel? Data
    {
        get => (PullRequestCardViewModel?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public bool ShowRepository
    {
        get => (bool)GetValue(ShowRepositoryProperty);
        set => SetValue(ShowRepositoryProperty, value);
    }

    public event EventHandler<PullRequestActionEventArgs>? OpenChecksRequested;

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Brush LabelDotBrush(string? color) => new SolidColorBrush(LabelColor(color));

    /// <summary>Tints a chip with its own label color, which high contrast replaces outright.</summary>
    public static Brush LabelFillBrush(string? color) =>
        Accessibility.HighContrast || color is null
            ? ThemeBrush("ControlAltFillColorSecondaryBrush")
            : new SolidColorBrush(LabelColor(color)) { Opacity = 0.16 };

    public static Brush LabelStrokeBrush(string? color)
    {
        if (Accessibility.HighContrast) return ThemeBrush("SystemColorWindowTextColorBrush");
        return color is null
            ? ThemeBrush("ControlStrokeColorDefaultBrush")
            : new SolidColorBrush(LabelColor(color)) { Opacity = 0.55 };
    }

    /// <summary>Status color for a single check, using the same palette as the rollup chip.</summary>
    public static Brush ToneBrush(StatusTone tone) => ThemeBrush(tone switch
    {
        StatusTone.Success => "SystemFillColorSuccessBrush",
        StatusTone.Failure => "SystemFillColorCriticalBrush",
        StatusTone.Caution => "SystemFillColorCautionBrush",
        StatusTone.Progress => "SystemFillColorAttentionBrush",
        _ => "TextFillColorSecondaryBrush"
    });

    private static Color LabelColor(string? color)
    {
        // Core validates label colors; retaining a fallback also makes this control safe
        // to use with independently constructed presentation data.
        return color is { Length: 6 } && uint.TryParse(color, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out var rgb)
            ? Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : Color.FromArgb(255, 128, 128, 128);
    }

    // Chips are rebuilt whenever their card's data changes, so resolving the current
    // theme's brush once per chip is enough; a missing key must never crash a card.
    private static Brush ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private static void OnDataChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var card = (PullRequestCard)sender;
        // A recycled row must never leave a flyout acting on the old PR.
        card._checksFlyoutState.Reset();
        card.ChecksItems.ItemsSource = null;
        card.ChecksFlyout.Hide();
        card.UpdateStateAppearance();
        card.AuthorPicture.ProfilePicture = null;
        if (card.Data?.AuthorAvatarUrl is { } uri)
        {
            var image = new BitmapImage(uri);
            image.ImageFailed += (_, _) =>
            {
                if (ReferenceEquals(card.AuthorPicture.ProfilePicture, image))
                    card.AuthorPicture.ProfilePicture = null; // PersonPicture keeps its initials.
            };
            card.AuthorPicture.ProfilePicture = image;
        }
    }

    private void UpdateStateAppearance()
    {
        VisualStateManager.GoToState(this, Data?.StateLabel ?? "Draft", useTransitions: false);
        VisualStateManager.GoToState(this, Data?.ChecksVisualState ?? "ChecksNeutral", useTransitions: false);
        VisualStateManager.GoToState(this, Data?.ReviewVisualState ?? "ReviewNeutral", useTransitions: false);
    }

    private void ChecksButton_Tapped(object sender, TappedRoutedEventArgs args) => args.Handled = true;

    private void ChecksButton_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        // Button owns Enter/Space activation; do not let ListView invoke the PR too.
        if (args.Key is VirtualKey.Enter or VirtualKey.Space)
            args.Handled = true;
    }

    private void ChecksFlyout_Opening(object? sender, object args) =>
        ChecksItems.ItemsSource = _checksFlyoutState.Open(Data);

    private void ChecksFlyout_Closed(object? sender, object args) => _checksFlyoutState.Close();

    private void OpenChecks_Click(object sender, RoutedEventArgs args)
    {
        if (_checksFlyoutState.GetOpenData(Data) is { } data)
        {
            ChecksFlyout.Hide();
            OpenChecksRequested?.Invoke(this, new PullRequestActionEventArgs(data.Id));
        }
    }
}

public sealed class PullRequestActionEventArgs(string pullRequestId) : EventArgs
{
    public string PullRequestId { get; } = pullRequestId;
}
