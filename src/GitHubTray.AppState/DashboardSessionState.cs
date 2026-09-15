using GitHubTray.Core;

namespace GitHubTray.AppState;

public enum DashboardAccountVerificationStatus
{
    Verifying,
    Verified,
    Failed
}

/// <summary>An immutable view of the session's currently publishable data and refresh status.</summary>
public sealed class DashboardSessionState
{
    internal DashboardSessionState(
        DashboardSnapshot? snapshot,
        string? lastKnownLogin,
        string? error,
        bool isRefreshing,
        bool isStopping,
        GitHubUser? account = null,
        DashboardAccountVerificationStatus verificationStatus =
            DashboardAccountVerificationStatus.Verifying)
    {
        Snapshot = snapshot;
        LastKnownLogin = lastKnownLogin;
        Error = error;
        IsRefreshing = isRefreshing;
        IsStopping = isStopping;
        Account = account ?? snapshot?.User;
        VerificationStatus = verificationStatus;
    }

    /// <summary>Data eligible for display, including saved data whose account has not yet been verified.</summary>
    public DashboardSnapshot? Snapshot { get; }

    /// <summary>The last account associated with displayable or verified state.</summary>
    public string? LastKnownLogin { get; }

    /// <summary>The saved or network-verified account currently represented by the state.</summary>
    public GitHubUser? Account { get; }

    /// <summary>The refresh error without any presentation-specific suffix.</summary>
    public string? Error { get; }

    public bool IsRefreshing { get; }
    public bool IsStopping { get; }
    public DashboardAccountVerificationStatus VerificationStatus { get; }
    public bool HasDisplayableData => Snapshot is not null;
    public bool IsAccountVerified =>
        VerificationStatus is DashboardAccountVerificationStatus.Verified;
}
