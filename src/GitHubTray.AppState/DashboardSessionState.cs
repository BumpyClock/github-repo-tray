using GitHubTray.Core;

namespace GitHubTray.AppState;

/// <summary>An immutable view of the session's currently publishable data and refresh status.</summary>
public sealed class DashboardSessionState
{
    internal DashboardSessionState(
        DashboardSnapshot? snapshot,
        string? lastKnownLogin,
        string? error,
        bool isRefreshing,
        bool isStopping)
    {
        Snapshot = snapshot;
        LastKnownLogin = lastKnownLogin;
        Error = error;
        IsRefreshing = isRefreshing;
        IsStopping = isStopping;
    }

    /// <summary>Data eligible for display, or null when the account is unverified or the session is stopping.</summary>
    public DashboardSnapshot? Snapshot { get; }

    /// <summary>The last successfully published login, for an unverified account's historical header only.</summary>
    public string? LastKnownLogin { get; }

    /// <summary>The refresh error without any presentation-specific suffix.</summary>
    public string? Error { get; }

    public bool IsRefreshing { get; }
    public bool IsStopping { get; }
    public bool IsAccountVerified => Snapshot is not null;
}
