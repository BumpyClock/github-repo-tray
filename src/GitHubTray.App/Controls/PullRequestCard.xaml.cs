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
    private const int AvatarDecodePixelWidth = 96;

    private static readonly AccessibilitySettings Accessibility = new();

    private readonly PullRequestChecksFlyoutState _checksFlyoutState = new();
    private BitmapImage? _avatarImage;
    private bool _released;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(PullRequestCardViewModel), typeof(PullRequestCard), new PropertyMetadata(null, OnDataChanged));

    public static readonly DependencyProperty ShowRepositoryProperty = DependencyProperty.Register(
        nameof(ShowRepository), typeof(bool), typeof(PullRequestCard), new PropertyMetadata(true));

    public static readonly DependencyProperty IsChecksFlyoutContentLoadedProperty = DependencyProperty.Register(
        nameof(IsChecksFlyoutContentLoaded), typeof(bool), typeof(PullRequestCard), new PropertyMetadata(false));

    public PullRequestCard()
    {
        InitializeComponent();
        Loaded += PullRequestCard_Loaded;
        Unloaded += PullRequestCard_Unloaded;
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

    public bool IsChecksFlyoutContentLoaded
    {
        get => (bool)GetValue(IsChecksFlyoutContentLoadedProperty);
        private set => SetValue(IsChecksFlyoutContentLoadedProperty, value);
    }

    public event EventHandler<PullRequestActionEventArgs>? OpenChecksRequested;

    internal void ReleaseForHide()
    {
        if (_released) return;
        _released = true;
        ResetChecksFlyout();
        ClearAvatar();
        Data = null;
        Bindings.StopTracking();
        OpenChecksRequested = null;
        Loaded -= PullRequestCard_Loaded;
        Unloaded -= PullRequestCard_Unloaded;
        ChecksFlyout.Opening -= ChecksFlyout_Opening;
        ChecksFlyout.Closed -= ChecksFlyout_Closed;
        ChecksFlyout.Content = null;
        ChecksButton.Flyout = null;
        DataContext = null;
    }

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Brush LabelDotBrush(string? color) => new SolidColorBrush(LabelColor(color));

    /// <summary>Tints a chip with its own label color, which high contrast replaces outright.</summary>
    public static Brush LabelFillBrush(string? color) =>
        Accessibility.HighContrast || color is null
            ? ThemeBrushes.Get("ControlAltFillColorSecondaryBrush")
            : new SolidColorBrush(LabelColor(color)) { Opacity = 0.16 };

    public static Brush LabelStrokeBrush(string? color)
    {
        if (Accessibility.HighContrast) return ThemeBrushes.Get("SystemColorWindowTextColorBrush");
        return color is null
            ? ThemeBrushes.Get("ControlStrokeColorDefaultBrush")
            : new SolidColorBrush(LabelColor(color)) { Opacity = 0.55 };
    }

    /// <summary>Status color for a single check, using the same palette as the rollup chip.</summary>
    public static Brush ToneBrush(StatusTone tone) => ThemeBrushes.Tone(tone);

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
    // theme's brush once per chip is enough.
    private static void OnDataChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var card = (PullRequestCard)sender;
        if (card._released) return;
        // A recycled row must never leave a flyout acting on the old PR.
        card.ResetChecksFlyout();
        card.UpdateStateAppearance();
        card.UpdateAvatar();
    }

    private void PullRequestCard_Loaded(object sender, RoutedEventArgs args)
    {
        if (_released) return;
        UpdateStateAppearance();
        UpdateAvatar();
    }

    private void PullRequestCard_Unloaded(object sender, RoutedEventArgs args)
    {
        ResetChecksFlyout();
        ClearAvatar();
    }

    private void UpdateAvatar()
    {
        ClearAvatar();
        if (_released || !IsLoaded || Data?.AuthorAvatarUrl is not { } uri)
        {
            return;
        }

        var image = new BitmapImage
        {
            DecodePixelWidth = AvatarDecodePixelWidth
        };
        image.ImageFailed += AvatarImageFailed;
        _avatarImage = image;
        AuthorPicture.ProfilePicture = image;
        image.UriSource = uri;
    }

    private void ClearAvatar()
    {
        if (_avatarImage is not null)
        {
            _avatarImage.ImageFailed -= AvatarImageFailed;
            _avatarImage = null;
        }
        AuthorPicture.ProfilePicture = null;
    }

    private void AvatarImageFailed(object sender, ExceptionRoutedEventArgs args)
    {
        if (!ReferenceEquals(sender, _avatarImage))
        {
            return;
        }

        ClearAvatar(); // PersonPicture keeps its initials.
    }

    private void UpdateStateAppearance() =>
        VisualStateManager.GoToState(this, Data?.StatusVisualState ?? "StatusDraft", useTransitions: false);

    private void ChecksButton_Tapped(object sender, TappedRoutedEventArgs args) => args.Handled = true;

    private void ChecksButton_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        // Button owns Enter/Space activation; do not let ListView invoke the PR too.
        if (args.Key is VirtualKey.Enter or VirtualKey.Space)
            args.Handled = true;
    }

    private void ChecksFlyout_Opening(object? sender, object args)
    {
        if (_released) return;
        _checksFlyoutState.Open(Data);
        IsChecksFlyoutContentLoaded = true;
        if (ChecksGroups is not null)
        {
            ChecksGroups.ItemsSource = _checksFlyoutState.Groups;
        }
    }

    private void ChecksFlyoutContent_Loaded(object sender, RoutedEventArgs args)
    {
        if (!_released) ChecksGroups.ItemsSource = _checksFlyoutState.Groups;
    }

    private void ChecksFlyout_Closed(object? sender, object args) => _checksFlyoutState.Close();

    private void ResetChecksFlyout()
    {
        _checksFlyoutState.Reset();
        if (ChecksGroups is not null)
        {
            ChecksGroups.ItemsSource = null;
        }
        ChecksFlyout.Hide();
        IsChecksFlyoutContentLoaded = false;
    }

    private void OpenChecks_Click(object sender, RoutedEventArgs args)
    {
        if (!_released && _checksFlyoutState.GetOpenData(Data) is { } data)
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
