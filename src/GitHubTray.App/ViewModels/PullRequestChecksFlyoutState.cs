namespace GitHubTray_App.ViewModels;

/// <summary>
/// Owns deferred detail content and the identity authorized by the current flyout
/// opening. The control assigns ItemsSource only from Open, and resets on recycling.
/// </summary>
internal sealed class PullRequestChecksFlyoutState
{
    private PullRequestCardViewModel? _openData;

    public IReadOnlyList<PullRequestCheckViewModel>? Items { get; private set; }

    /// <summary>The grouped projection the flyout binds, materialized alongside <see cref="Items"/>.</summary>
    public IReadOnlyList<PullRequestCheckGroup>? Groups { get; private set; }

    public IReadOnlyList<PullRequestCheckViewModel>? Open(PullRequestCardViewModel? data)
    {
        _openData = data;
        Groups = data?.CheckGroups;
        return Items = data?.Checks;
    }

    public void Close() => _openData = null;

    public void Reset()
    {
        _openData = null;
        Items = null;
        Groups = null;
    }

    public PullRequestCardViewModel? GetOpenData(PullRequestCardViewModel? currentData) =>
        _openData is not null && ReferenceEquals(_openData, currentData) ? _openData : null;
}
