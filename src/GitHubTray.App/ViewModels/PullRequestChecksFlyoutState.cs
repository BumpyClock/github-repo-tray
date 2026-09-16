namespace GitHubTray_App.ViewModels;

/// <summary>
/// Owns deferred detail content and the identity authorized by the current flyout
/// opening. The control assigns ItemsSource only from Open, and resets on recycling.
/// </summary>
internal sealed class PullRequestChecksFlyoutState
{
    private PullRequestCardViewModel? _openData;

    /// <summary>The grouped projection the flyout binds, materialized on opening.</summary>
    public IReadOnlyList<PullRequestCheckGroup>? Groups { get; private set; }

    public void Open(PullRequestCardViewModel? data)
    {
        _openData = data;
        Groups = data?.CheckGroups;
    }

    public void Close() => _openData = null;

    public void Reset()
    {
        _openData = null;
        Groups = null;
    }

    public PullRequestCardViewModel? GetOpenData(PullRequestCardViewModel? currentData) =>
        _openData is not null && ReferenceEquals(_openData, currentData) ? _openData : null;
}
