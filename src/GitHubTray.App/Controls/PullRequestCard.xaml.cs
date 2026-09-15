using System.Globalization;
using GitHubTray_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.UI;

namespace GitHubTray_App.Controls;

public sealed partial class PullRequestCard : UserControl
{
    private PullRequestCardViewModel? _flyoutData;

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

    public static Brush LabelDotBrush(string? color)
    {
        // Core validates label colors; retaining a fallback also makes this control safe
        // to use with independently constructed presentation data.
        return new SolidColorBrush(color is { Length: 6 } && uint.TryParse(color, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out var rgb)
            ? Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : Color.FromArgb(255, 128, 128, 128));
    }

    private static void OnDataChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var card = (PullRequestCard)sender;
        // A recycled row must never leave a flyout acting on the old PR.
        card._flyoutData = null;
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

    private void UpdateStateAppearance() =>
        VisualStateManager.GoToState(this, Data?.StateLabel ?? "Draft", useTransitions: false);

    private void ChecksButton_Tapped(object sender, TappedRoutedEventArgs args) => args.Handled = true;

    private void ChecksButton_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        // Button owns Enter/Space activation; do not let ListView invoke the PR too.
        if (args.Key is VirtualKey.Enter or VirtualKey.Space)
            args.Handled = true;
    }

    private void ChecksFlyout_Opening(object? sender, object args) => _flyoutData = Data;
    private void ChecksFlyout_Closed(object? sender, object args) => _flyoutData = null;

    private void OpenChecks_Click(object sender, RoutedEventArgs args)
    {
        if (_flyoutData is { } data && ReferenceEquals(Data, data))
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
