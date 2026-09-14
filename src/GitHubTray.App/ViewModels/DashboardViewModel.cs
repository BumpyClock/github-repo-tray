using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubTray.AppState;
using GitHubTray.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace GitHubTray_App.ViewModels;

public sealed partial class DashboardRow : ObservableObject
{
    private readonly bool _isRepositoryFirst;

    public DashboardRow(DashboardItem item, Symbol icon, bool isRepositoryFirst)
    {
        Item = item;
        Icon = icon;
        _isRepositoryFirst = isRepositoryFirst;
        UpdateRelativeTimestamp();
    }

    public DashboardItem Item { get; }
    public Symbol Icon { get; }
    public string Heading => _isRepositoryFirst && !string.IsNullOrWhiteSpace(Item.Repository) ? Item.Repository : Item.Title;
    public string SecondaryText => _isRepositoryFirst ? Item.Title : Item.Repository;
    public string Detail => Item.Detail;
    public bool HasSecondaryText => !string.IsNullOrWhiteSpace(SecondaryText) && !string.Equals(Heading, SecondaryText, StringComparison.Ordinal);
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail) && !string.Equals(Detail, SecondaryText, StringComparison.Ordinal);
    public string Timestamp => Item.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string AccessibleName => $"{Item.Title}. {Item.Repository}. {Detail}. {Timestamp}. Open on GitHub.";
    public string AutomationId => $"DashboardItem_{Item.Id}";

    [ObservableProperty]
    public partial string RelativeTimestamp { get; set; } = "";

    public void UpdateRelativeTimestamp()
    {
        var elapsed = DateTimeOffset.UtcNow - Item.UpdatedAt;
        RelativeTimestamp = elapsed.TotalMinutes < 1 ? "just now"
            : elapsed.TotalHours < 1 ? $"{(int)elapsed.TotalMinutes} min ago"
            : elapsed.TotalDays < 1 ? $"{(int)elapsed.TotalHours} hr ago"
            : elapsed.TotalDays < 30 ? $"{(int)elapsed.TotalDays} d ago"
            : elapsed.TotalDays < 365 ? $"{(int)(elapsed.TotalDays / 30)} mo ago"
            : $"{(int)(elapsed.TotalDays / 365)} yr ago";
    }
}

public sealed partial class SectionViewModel : ObservableObject
{
    private readonly Symbol _icon;
    private readonly bool _isRepositoryFirst;

    public SectionViewModel(string title, string emptyMessage, Symbol icon, bool isRepositoryFirst = false)
    {
        Title = title;
        EmptyMessage = emptyMessage;
        _icon = icon;
        _isRepositoryFirst = isRepositoryFirst;
    }

    public string Title { get; }
    public string EmptyMessage { get; }
    public ObservableCollection<DashboardRow> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; set; } = "";

    [ObservableProperty]
    public partial string UpdatedLabel { get; set; } = "Not refreshed yet";

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public void Clear()
    {
        if (Items.Count != 0)
        {
            Items.Clear();
        }

        Error = "";
        UpdatedLabel = "Not refreshed yet";
    }

    public void Update(DashboardSection section)
    {
        Items.Clear();
        foreach (var item in section.Items)
        {
            Items.Add(new DashboardRow(item, _icon, _isRepositoryFirst));
        }

        Error = section.Error ?? "";
        UpdatedLabel = section.UpdatedAt is { } updated
            ? $"{(section.IsStale ? "Last success" : "Updated")} {updated.ToLocalTime():g} · {Items.Count} items"
            : "No successful refresh yet";
    }
}

/// <summary>All methods are called on the UI dispatcher; the session owns refresh coalescing.</summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly DashboardRefreshSession _refreshSession;
    private readonly SettingsStore _settingsStore;
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly DispatcherQueueTimer _clockTimer;
    private readonly CancellationTokenSource _lifetime = new();
    // Redraw marker only; the session owns published-data eligibility and recovery.
    private DashboardSnapshot? _lastProjectedSnapshot;
    private Task? _initializeTask;
    private Task? _saveTask;
    private int _refreshMinutes = 5;
    private bool _isShuttingDown;

    public DashboardViewModel(DashboardRefreshSession refreshSession, SettingsStore settingsStore, DispatcherQueue dispatcher)
    {
        _refreshSession = refreshSession;
        _settingsStore = settingsStore;
        Sections =
        [
            new("Activity", "No recent activity returned for this account.", Symbol.Clock, isRepositoryFirst: true),
            new("My PRs", "No open authored pull requests.", Symbol.Document),
            new("Reviews", "No open pull requests awaiting your review.", Symbol.Comment),
            new("Repos", "No repositories returned for this account.", Symbol.Library)
        ];
        SelectedSection = Sections[0];
        _refreshTimer = dispatcher.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMinutes(_refreshMinutes);
        _refreshTimer.Tick += OnRefreshTimerTick;
        _clockTimer = dispatcher.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromMinutes(1);
        _clockTimer.Tick += OnClockTimerTick;
    }

    public IReadOnlyList<SectionViewModel> Sections { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleContributionCalendar))]
    [NotifyPropertyChangedFor(nameof(ContributionStatus))]
    public partial ContributionSection Contributions { get; private set; } = new(null, null, null);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyVisible))]
    [NotifyPropertyChangedFor(nameof(EmptyTitle))]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
    public partial SectionViewModel SelectedSection { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyVisible))]
    [NotifyPropertyChangedFor(nameof(EmptyTitle))]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
    [NotifyPropertyChangedFor(nameof(ContributionStatus))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsRefreshing { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyVisible))]
    [NotifyPropertyChangedFor(nameof(EmptyTitle))]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
    [NotifyPropertyChangedFor(nameof(VisibleContributionCalendar))]
    [NotifyPropertyChangedFor(nameof(ContributionStatus))]
    public partial bool IsAccountVerified { get; private set; }

    [ObservableProperty]
    public partial string AccountLabel { get; set; } = "Checking GitHub CLI account…";

    [ObservableProperty]
    public partial string AccountDescription { get; set; } = "Uses the current github.com account resolved by GitHub CLI.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRefreshError))]
    public partial string RefreshError { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettingsWarning))]
    public partial string SettingsWarning { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettingsStatus))]
    public partial string SettingsStatus { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionError))]
    public partial string ActionError { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrayError))]
    public partial string TrayError { get; set; } = "";

    [ObservableProperty]
    public partial string RefreshMinutesText { get; set; } = "5";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    public partial bool IsSettingsLoaded { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    public partial bool IsSavingSettings { get; set; }

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    [ObservableProperty]
    public partial string RefreshScheduleLabel { get; set; } = "Refresh every 5 minutes";

    public bool HasRefreshError => RefreshError.Length != 0;
    public bool HasSettingsWarning => SettingsWarning.Length != 0;
    public bool HasSettingsStatus => SettingsStatus.Length != 0;
    public bool HasActionError => ActionError.Length != 0;
    public bool HasTrayError => TrayError.Length != 0;
    public bool CanRefresh => !_isShuttingDown && !IsRefreshing;
    public bool CanSaveSettings => !_isShuttingDown && IsSettingsLoaded && !IsSavingSettings;
    public ContributionCalendar? VisibleContributionCalendar => IsAccountVerified ? Contributions.Calendar : null;
    public string ContributionStatus
    {
        get
        {
            if (!IsAccountVerified)
            {
                return IsRefreshing ? "Loading contributions · verifying GitHub CLI account…" : "Contributions hidden until the GitHub CLI account is verified.";
            }

            if (Contributions.Error is { } error)
            {
                var lastSuccess = Contributions.UpdatedAt is { } updated ? $" Last success {updated.ToLocalTime():g}." : "";
                return $"{(Contributions.IsStale ? "Stale contributions" : "Contributions unavailable")}: {error}{lastSuccess}";
            }

            if (Contributions.Calendar is null)
            {
                return IsRefreshing ? "Loading contributions…" : "No contribution calendar was returned.";
            }

            return IsRefreshing
                ? "Refreshing contributions · showing the last successful calendar."
                : Contributions.UpdatedAt is { } timestamp ? $"Updated {timestamp.ToLocalTime():g}" : "Contribution calendar loaded.";
        }
    }

    public bool IsEmptyVisible => !IsAccountVerified || SelectedSection.Items.Count == 0;
    public string EmptyTitle => IsRefreshing && !IsAccountVerified
        ? "Checking your GitHub account"
        : !IsAccountVerified ? "GitHub account unavailable"
        : SelectedSection.HasError && SelectedSection.Items.Count == 0 ? "This section could not be loaded"
        : SelectedSection.Items.Count == 0 && IsRefreshing ? "Refreshing…" : "You're up to date";
    public string EmptyMessage => IsRefreshing && !IsAccountVerified
        ? "Reading your existing GitHub CLI session. No sign-in window will open."
        : !IsAccountVerified
            ? "Check your GitHub CLI session in a terminal, then choose Refresh. Previous account data stays hidden until the account is verified."
            : SelectedSection.HasError
                ? "Choose Refresh to try again. Other sections may still be available."
                : SelectedSection.EmptyMessage;

    public Task InitializeAsync() => _initializeTask ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        try
        {
            var settings = await _settingsStore.LoadAsync(_lifetime.Token);
            settings.Validate();
            _refreshMinutes = settings.RefreshMinutes;
            RefreshMinutesText = _refreshMinutes.ToString(CultureInfo.InvariantCulture);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            SettingsWarning = "Settings could not be read. Using 5 minutes for this session; the file has not been changed. Open Settings and save a valid interval to repair it.";
        }
        finally
        {
            IsSettingsLoaded = true;
        }

        UpdateRefreshSchedule();
        if (!_isShuttingDown)
        {
            _refreshTimer.Start();
            _clockTimer.Start();
            await RefreshAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        if (_isShuttingDown)
        {
            return;
        }

        if (!_refreshSession.State.IsRefreshing)
        {
            ActionError = "";
        }

        try
        {
            var refreshTask = _refreshSession.RefreshAsync();
            ApplyRefreshState(_refreshSession.State);
            await refreshTask;
        }
        finally
        {
            if (!_isShuttingDown)
            {
                ApplyRefreshState(_refreshSession.State);
            }
        }
    }

    private void ApplyRefreshState(DashboardSessionState state)
    {
        if (state.Snapshot is { } snapshot)
        {
            if (!ReferenceEquals(_lastProjectedSnapshot, snapshot))
            {
                Sections[0].Update(snapshot.Activity);
                Sections[1].Update(snapshot.PullRequests);
                Sections[2].Update(snapshot.ReviewRequests);
                Sections[3].Update(snapshot.Repositories);
                Contributions = snapshot.Contributions;
                _lastProjectedSnapshot = snapshot;
            }

            AccountLabel = string.IsNullOrWhiteSpace(snapshot.User.DisplayName)
                ? $"@{snapshot.User.Login}"
                : $"{snapshot.User.DisplayName} · @{snapshot.User.Login}";
            AccountDescription = $"Last verified github.com account: @{snapshot.User.Login}. Resolved by gh for this process; environment credentials can take precedence over the stored CLI account.";
            IsAccountVerified = true;
        }
        else
        {
            IsAccountVerified = false;
            _lastProjectedSnapshot = null;
            foreach (var section in Sections)
            {
                section.Clear();
            }

            Contributions = new(null, null, null);
            if (state.IsRefreshing && state.LastKnownLogin is null && state.Error is null)
            {
                AccountLabel = "Checking GitHub CLI account…";
                AccountDescription = "Uses the current github.com account resolved by GitHub CLI.";
            }
            else
            {
                AccountLabel = state.LastKnownLogin is null
                    ? "GitHub CLI account unavailable"
                    : $"Account unverified · last seen @{state.LastKnownLogin}";
                AccountDescription = "The current CLI account could not be verified. No previous-account rows are shown.";
            }
        }

        RefreshError = state.Error is { } error
            ? $"{error} Previous data is not current and remains hidden until the account is verified."
            : "";
        IsRefreshing = state.IsRefreshing;
        NotifyEmptyState();
    }

    public bool CanOpenRow(DashboardRow row)
    {
        var state = _refreshSession.State;
        return !_isShuttingDown && !state.IsStopping && state.Snapshot is { } snapshot
            && (snapshot.Activity.Items.Contains(row.Item)
                || snapshot.PullRequests.Items.Contains(row.Item)
                || snapshot.ReviewRequests.Items.Contains(row.Item)
                || snapshot.Repositories.Items.Contains(row.Item));
    }

    private void NotifyEmptyState()
    {
        OnPropertyChanged(nameof(IsEmptyVisible));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    [RelayCommand(CanExecute = nameof(CanSaveSettings))]
    public Task SaveSettingsAsync()
    {
        if (!CanSaveSettings)
        {
            return _saveTask ?? Task.CompletedTask;
        }

        _saveTask = SaveSettingsCoreAsync();
        return _saveTask;
    }

    private async Task SaveSettingsCoreAsync()
    {
        SettingsStatus = "";
        if (!int.TryParse(RefreshMinutesText, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || minutes is < 1 or > 60)
        {
            SettingsWarning = "Enter a whole number from 1 to 60 minutes. Settings have not been saved.";
            return;
        }

        IsSavingSettings = true;
        try
        {
            var settings = new AppSettings(minutes);
            settings.Validate();
            await _settingsStore.SaveAsync(settings, _lifetime.Token);
            if (_isShuttingDown)
            {
                return;
            }

            _refreshMinutes = minutes;
            _refreshTimer.Stop();
            UpdateRefreshSchedule();
            _refreshTimer.Start();
            SettingsWarning = "";
            SettingsStatus = $"Saved. Automatic refresh runs every {minutes} {(minutes == 1 ? "minute" : "minutes")}, including while the panel is hidden.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SettingsWarning = "Settings could not be saved. The previous refresh interval remains active. Check access to the app's local data folder, then try Save again.";
        }
        finally
        {
            IsSavingSettings = false;
        }
    }

    private void UpdateRefreshSchedule()
    {
        _refreshTimer.Interval = TimeSpan.FromMinutes(_refreshMinutes);
        RefreshScheduleLabel = $"Refresh every {_refreshMinutes} {(_refreshMinutes == 1 ? "minute" : "minutes")}";
    }

    private async void OnRefreshTimerTick(DispatcherQueueTimer sender, object args) => await RefreshAsync();

    private void OnClockTimerTick(DispatcherQueueTimer sender, object args)
    {
        foreach (var section in Sections)
        {
            foreach (var row in section.Items)
            {
                row.UpdateRelativeTimestamp();
            }
        }
    }

    public async Task ShutdownAsync()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;
        _clockTimer.Stop();
        _clockTimer.Tick -= OnClockTimerTick;
        RefreshCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
        try
        {
            var refreshShutdownTask = _refreshSession.ShutdownAsync();
            ApplyRefreshState(_refreshSession.State);
            await Task.WhenAll(
                refreshShutdownTask,
                _lifetime.CancelAsync(),
                _initializeTask ?? Task.CompletedTask,
                _saveTask ?? Task.CompletedTask);
        }
        finally
        {
            _lifetime.Dispose();
        }
    }
}
