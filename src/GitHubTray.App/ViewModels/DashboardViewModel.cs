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

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class DashboardRow : ObservableObject
{
    private readonly bool _isRepositoryFirst;

    public DashboardRow(DashboardItem item, Symbol icon, bool isRepositoryFirst, bool isStale = false)
    {
        Item = item;
        Icon = icon;
        _isRepositoryFirst = isRepositoryFirst;
        PullRequest = item.PullRequest is not null
            ? new PullRequestCardViewModel(item, isStale) : null;
        UpdateRelativeTimestamp();
    }

    public DashboardItem Item { get; }
    public Symbol Icon { get; }
    public PullRequestCardViewModel? PullRequest { get; }
    public string Heading => _isRepositoryFirst && !string.IsNullOrWhiteSpace(Item.Repository) ? Item.Repository : Item.Title;
    public string SecondaryText => _isRepositoryFirst ? Item.Title : Item.Repository;
    public string Detail => Item.Detail;
    public bool HasSecondaryText => !string.IsNullOrWhiteSpace(SecondaryText) && !string.Equals(Heading, SecondaryText, StringComparison.Ordinal);
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail) && !string.Equals(Detail, SecondaryText, StringComparison.Ordinal);
    public string Timestamp => Item.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string AccessibleName => PullRequest?.AccessibleName
        ?? $"{Item.Title}. {Item.Repository}. {Detail}. {Timestamp}. Open on GitHub.";
    public string AutomationId => $"DashboardItem_{Item.Id}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string RelativeTimestamp { get; set; } = "";

    public void UpdateRelativeTimestamp()
    {
        var now = DateTimeOffset.UtcNow;
        PullRequest?.UpdateRelativeTimestamp(now);
        var elapsed = now - Item.UpdatedAt;
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
    private DashboardSection? _projectedSection;

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
        ReleaseRows();
        Error = "";
        UpdatedLabel = "Not refreshed yet";
    }

    internal void ReleaseRows()
    {
        _projectedSection = null;
        if (Items.Count != 0)
        {
            Items.Clear();
        }
    }

    public void Update(DashboardSection section)
    {
        if (DashboardSectionProjection.IsEquivalent(_projectedSection, section))
        {
            _projectedSection = section;
            return;
        }

        Items.Clear();
        foreach (var item in section.Items)
        {
            Items.Add(new DashboardRow(item, _icon, _isRepositoryFirst, isStale: section.Error is not null));
        }

        _projectedSection = section;
        Error = section.Error ?? "";
        UpdatedLabel = section.UpdatedAt is not { } updated
            ? section.Source == DashboardSectionSource.Missing
                ? "Not retained on this device"
                : "No successful refresh yet"
            : section.Source switch
            {
                DashboardSectionSource.Cached =>
                    $"Cached from {updated.ToLocalTime():g} · {Items.Count} items",
                DashboardSectionSource.Retained =>
                    $"Last success {updated.ToLocalTime():g} · {Items.Count} items",
                _ => $"Updated {updated.ToLocalTime():g} · {Items.Count} items"
            };
    }
}

/// <summary>All methods are called on the UI dispatcher; the session owns refresh coalescing.</summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly DashboardStartup _startup;
    private readonly DashboardRefreshSession _refreshSession;
    private readonly SettingsStore _settingsStore;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly DispatcherQueueTimer _clockTimer;
    private readonly DashboardPresentationClock _presentationClock;
    private readonly DashboardStateProjection<DashboardSessionState> _stateProjection;
    private readonly DispatcherQueueTimer _preferenceSaveTimer;
    private readonly CancellationTokenSource _lifetime = new();
    // Last UI projection, never recovery data; the session owns publication eligibility.
    private DashboardSnapshot? _lastProjectedSnapshot;
    private Task? _initializeTask;
    private Task? _saveTask;
    private Task? _clearCacheTask;
    private Task? _shutdownTask;
    private AppSettings _committedSettings = new();
    // Cache clearing may finish while hidden; its marker must not retain an obsolete snapshot.
    private WeakReference<DashboardSnapshot>? _cacheInvalidatedSnapshot;
    private ContributionCellSizePreset _contributionCellSize = ContributionCellSizePreset.Medium;
    private int? _pendingRefreshMinutes;
    private bool _isPreferenceSaveReady;
    private bool _canAutoSaveSettings;
    private string _settingsSaveWarning = "";
    private bool _isShuttingDown;

    internal DashboardViewModel(DashboardStartup startup, DispatcherQueue dispatcher)
    {
        _startup = startup;
        _refreshSession = startup.Session;
        _settingsStore = startup.SettingsStore;
        _dispatcher = dispatcher;
        _stateProjection = new(ApplyRefreshStateCore, ReleasePresentation);
        _refreshSession.StateChanged += OnSessionStateChanged;
        Sections =
        [
            new("Activity", "No recent activity returned for this account.", Symbol.Clock, isRepositoryFirst: true),
            new("My PRs", "No authored pull requests returned for this account.", Symbol.Document),
            new("Reviews", "No open pull requests awaiting your review.", Symbol.Comment),
            new("Repos", "No repositories returned for this account.", Symbol.Library)
        ];
        SelectedSection = Sections[0];
        _refreshTimer = dispatcher.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMinutes(_committedSettings.RefreshMinutes);
        _refreshTimer.Tick += OnRefreshTimerTick;
        _clockTimer = dispatcher.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromMinutes(1);
        _clockTimer.Tick += OnClockTimerTick;
        _presentationClock = new(_clockTimer.Start, _clockTimer.Stop, UpdateClock);
        _preferenceSaveTimer = dispatcher.CreateTimer();
        _preferenceSaveTimer.Interval = TimeSpan.FromMilliseconds(300);
        _preferenceSaveTimer.IsRepeating = false;
        _preferenceSaveTimer.Tick += OnPreferenceSaveTimerTick;
    }

    public IReadOnlyList<SectionViewModel> Sections { get; }
    public IReadOnlyList<ContributionCellSizePreset> ContributionCellSizes { get; } =
        Array.AsReadOnly(Enum.GetValues<ContributionCellSizePreset>());

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleContributionCalendar))]
    [NotifyPropertyChangedFor(nameof(ContributionStatus))]
    public partial ContributionSection Contributions { get; private set; } = new(null, null, null);

    [ObservableProperty]
    public partial CopilotUsageViewModel CopilotDisplay { get; private set; } =
        CopilotUsageViewModel.Create(null, displayable: false, refreshing: true);

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
    [NotifyPropertyChangedFor(nameof(AccountDisplayName))]
    public partial bool IsAccountVerified { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyVisible))]
    [NotifyPropertyChangedFor(nameof(EmptyTitle))]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
    [NotifyPropertyChangedFor(nameof(VisibleContributionCalendar))]
    [NotifyPropertyChangedFor(nameof(ContributionStatus))]
    public partial bool HasDisplayableData { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountHandle))]
    [NotifyPropertyChangedFor(nameof(AccountDisplayName))]
    public partial string AccountLabel { get; set; } = "Checking GitHub CLI account…";

    public string AccountHandle => _lastProjectedSnapshot is { } snapshot ? $"@{snapshot.User.Login}" : AccountLabel;
    public string AccountDisplayName => _lastProjectedSnapshot is not { } snapshot
        ? ""
        : IsAccountVerified
            ? snapshot.User.DisplayName
            : HasRefreshError
                ? "Saved account · verification failed"
                : "Saved account · verification pending";

    [ObservableProperty]
    public partial string AccountDescription { get; set; } = "Uses the current github.com account resolved by GitHub CLI.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRefreshError))]
    [NotifyPropertyChangedFor(nameof(AccountDisplayName))]
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
    [NotifyPropertyChangedFor(nameof(HasCacheClearStatus))]
    public partial string CacheClearStatus { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCacheClearWarning))]
    public partial string CacheClearWarning { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClearCachedData))]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    [NotifyCanExecuteChangedFor(nameof(ClearCachedDataCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsClearingCachedData { get; private set; }

    [ObservableProperty]
    public partial string RefreshMinutesText { get; set; } = "5";

    public ContributionCellSizePreset ContributionCellSize
    {
        get => _contributionCellSize;
        set
        {
            if (value != _contributionCellSize)
            {
                SetContributionCellSize(value);
            }
        }
    }

    public void SetContributionCellSize(ContributionCellSizePreset preset)
    {
        if (!Enum.IsDefined(preset))
        {
            SettingsWarning = "Choose Small, Medium, or Large. The graph preferences have not been changed.";
            return;
        }

        if (!IsSettingsLoaded || _isShuttingDown)
        {
            return;
        }

        if (!SetProperty(ref _contributionCellSize, preset, nameof(ContributionCellSize)))
        {
            return;
        }
        ScheduleContributionPreferenceSave();
    }

    private void ScheduleContributionPreferenceSave()
    {
        SettingsStatus = "";
        if (_canAutoSaveSettings)
        {
            _isPreferenceSaveReady = false;
            _preferenceSaveTimer.Stop();
            _preferenceSaveTimer.Start();
        }
    }

    private bool HasPendingContributionPreferences =>
        ContributionCellSize != _committedSettings.ContributionCellSize;

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
    public bool HasCacheClearStatus => CacheClearStatus.Length != 0;
    public bool HasCacheClearWarning => CacheClearWarning.Length != 0;
    public bool CanRefresh => !_isShuttingDown && !IsRefreshing && !IsClearingCachedData;
    public bool CanSaveSettings => !_isShuttingDown && IsSettingsLoaded && !IsSavingSettings;
    public bool CanClearCachedData => !_isShuttingDown && !IsClearingCachedData;
    public ContributionCalendar? VisibleContributionCalendar =>
        HasDisplayableData ? Contributions.Calendar : null;
    public string ContributionStatus
    {
        get
        {
            if (!HasDisplayableData)
            {
                return IsRefreshing
                    ? "Loading contributions · verifying GitHub CLI account…"
                    : "Contributions have not been loaded.";
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

            if (Contributions.Source == DashboardSectionSource.Cached)
            {
                return DashboardSnapshotPresentation.CachedContributionStatus(
                    Contributions,
                    IsRefreshing);
            }

            return IsRefreshing
                ? "Refreshing contributions · showing the last successful calendar."
                : Contributions.UpdatedAt is { } timestamp ? $"Updated {timestamp.ToLocalTime():g}" : "Contribution calendar loaded.";
        }
    }

    public bool IsEmptyVisible => !HasDisplayableData || SelectedSection.Items.Count == 0;
    public string EmptyTitle => IsRefreshing && !HasDisplayableData
        ? "Checking your GitHub account"
        : !HasDisplayableData ? "GitHub account unavailable"
        : SelectedSection.HasError && SelectedSection.Items.Count == 0 ? "This section could not be loaded"
        : SelectedSection.Items.Count == 0 && IsRefreshing ? "Refreshing…" : "You're up to date";
    public string EmptyMessage => IsRefreshing && !HasDisplayableData
        ? "Reading your existing GitHub CLI session. No sign-in window will open."
        : !HasDisplayableData
            ? "Check your GitHub CLI session in a terminal, then choose Refresh."
            : SelectedSection.HasError
                ? "Choose Refresh to try again. Other sections may still be available."
                : SelectedSection.EmptyMessage;

    public Task InitializeAsync() => _initializeTask ??=
        _startup.InitializeAsync(InitializeSettingsAsync, ApplyStartupState);

    private void ApplyStartupState(DashboardSessionState state)
    {
        if (!_isShuttingDown)
            PublishRefreshState(state);
    }

    private async Task InitializeSettingsAsync()
    {
        try
        {
            var settings = await _startup.SettingsTask.WaitAsync(_lifetime.Token);
            settings.Validate();
            _committedSettings = settings;
            _refreshSession.SetRefreshInterval(TimeSpan.FromMinutes(settings.RefreshMinutes));
            SetProperty(ref _contributionCellSize, settings.ContributionCellSize, nameof(ContributionCellSize));
            RefreshMinutesText = settings.RefreshMinutes.ToString(CultureInfo.InvariantCulture);
            _canAutoSaveSettings = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || _isShuttingDown)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            SettingsWarning = "Settings could not be read. Using default preferences for this session; the file has not been changed. Graph preferences will not be saved automatically. Open Settings and save a valid interval to repair the file.";
        }
        finally
        {
            IsSettingsLoaded = !_isShuttingDown;
        }

        UpdateRefreshSchedule();
        if (!_isShuttingDown)
        {
            _refreshTimer.Start();
            _presentationClock.Initialize();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public Task RefreshAsync() => RefreshWithReasonAsync(DashboardRefreshReason.Manual);

    private async Task RefreshWithReasonAsync(DashboardRefreshReason reason)
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
            var refreshTask = _refreshSession.RefreshAsync(reason);
            PublishRefreshState(_refreshSession.State);
            await refreshTask;
        }
        finally
        {
            if (!_isShuttingDown)
            {
                PublishRefreshState(_refreshSession.State);
            }
        }
    }

    private void PublishRefreshState(DashboardSessionState state)
    {
        _stateProjection.Publish(PrepareRefreshState(state));
    }

    private void OnSessionStateChanged()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _ = _dispatcher.TryEnqueue(() =>
        {
            if (!_isShuttingDown)
            {
                PublishRefreshState(_refreshSession.State);
            }
        });
    }

    private DashboardSessionState PrepareRefreshState(DashboardSessionState state)
    {
        if (_cacheInvalidatedSnapshot is not null &&
            (state.IsRefreshing ||
             !IsCacheInvalidatedSnapshot(state.Snapshot)))
        {
            _cacheInvalidatedSnapshot = null;
        }

        return state;
    }

    private bool IsCacheInvalidatedSnapshot(DashboardSnapshot? snapshot) =>
        _cacheInvalidatedSnapshot is not null &&
        _cacheInvalidatedSnapshot.TryGetTarget(out var invalidated) &&
        ReferenceEquals(invalidated, snapshot);

    private void ApplyRefreshStateCore(DashboardSessionState state)
    {
        if (state.Snapshot is { } snapshot)
        {
            var showCacheInvalidated =
                !state.IsRefreshing &&
                IsCacheInvalidatedSnapshot(snapshot);

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
            AccountDescription = DashboardSnapshotPresentation.AccountDescription(
                snapshot,
                state.IsRefreshing,
                state.IsAccountVerified);
            if (showCacheInvalidated)
            {
                AccountDescription = "The displayed dashboard remains available, but its reusable cached data was invalidated. The next refresh fetches every section.";
            }
            HasDisplayableData = true;
            IsAccountVerified = state.IsAccountVerified;
        }
        else
        {
            HasDisplayableData = false;
            IsAccountVerified = false;
            _cacheInvalidatedSnapshot = null;
            _lastProjectedSnapshot = null;
            foreach (var section in Sections)
            {
                section.Clear();
            }

            Contributions = new(null, null, null);
            if (state.Account is { } account)
            {
                AccountLabel = state.IsAccountVerified
                    ? $"Verified account @{account.Login}"
                    : $"Account unverified · last seen @{account.Login}";
                AccountDescription = state.IsAccountVerified
                    ? $"Verified github.com account: @{account.Login}. Dashboard sections are loading."
                    : "The current CLI account could not be verified and no saved dashboard was available.";
            }
            else if (state.IsRefreshing && state.LastKnownLogin is null && state.Error is null)
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
            ? state.Snapshot is { } saved
                ? state.IsAccountVerified
                    ? $"{error} Showing previously loaded data for @{saved.User.Login}; no new data was published."
                    : $"{error} Showing saved data for @{saved.User.Login}; it remains unverified."
                : error
            : "";
        IsRefreshing = state.IsRefreshing;
        UpdateCopilotDisplay(state);
        NotifyEmptyState();
    }

    private void UpdateCopilotDisplay(DashboardSessionState state)
    {
        CopilotDisplay = CopilotUsageViewModel.Create(
            state.Snapshot?.Copilot,
            state.HasDisplayableData,
            state.IsRefreshing,
            accountVerified: state.IsAccountVerified);
    }

    private void ReleasePresentation()
    {
        _lastProjectedSnapshot = null;
        foreach (var section in Sections)
        {
            section.ReleaseRows();
        }

        Contributions = new(null, null, null);
        CopilotDisplay = CopilotUsageViewModel.Create(null, displayable: false, refreshing: IsRefreshing);
        HasDisplayableData = false;
        IsAccountVerified = false;
        OnPropertyChanged(nameof(AccountHandle));
        OnPropertyChanged(nameof(AccountDisplayName));
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

    [RelayCommand(CanExecute = nameof(CanClearCachedData))]
    public Task ClearCachedDataAsync()
    {
        if (!CanClearCachedData)
        {
            return _clearCacheTask ?? Task.CompletedTask;
        }

        if (_clearCacheTask is null || _clearCacheTask.IsCompleted)
        {
            _clearCacheTask = ClearCachedDataCoreAsync();
        }
        return _clearCacheTask;
    }

    private async Task ClearCachedDataCoreAsync()
    {
        IsClearingCachedData = true;
        CacheClearStatus = "";
        CacheClearWarning = "";
        ActionError = "";
        try
        {
            var result = await _refreshSession.ClearCacheAsync();
            if (!_isShuttingDown)
            {
                var presentation = DashboardCacheClearPresentation.Create(result);
                CacheClearStatus = presentation.Status;
                CacheClearWarning = presentation.Warning;
                _cacheInvalidatedSnapshot =
                    _refreshSession.State is { Snapshot: { } snapshot, IsRefreshing: false }
                        ? new(snapshot)
                        : null;
                PublishRefreshState(_refreshSession.State);
            }
        }
        finally
        {
            IsClearingCachedData = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveSettings))]
    public Task SaveSettingsAsync()
    {
        if (!CanSaveSettings)
        {
            return _saveTask ?? Task.CompletedTask;
        }

        SettingsStatus = "";
        if (!int.TryParse(RefreshMinutesText, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || minutes is < 1 or > 60)
        {
            SettingsWarning = "Enter a whole number from 1 to 60 minutes. Settings have not been saved.";
            return Task.CompletedTask;
        }

        _pendingRefreshMinutes = minutes;
        IsSavingSettings = true;
        return StartSettingsSave();
    }

    private async void OnPreferenceSaveTimerTick(DispatcherQueueTimer sender, object args)
    {
        _preferenceSaveTimer.Stop();
        _isPreferenceSaveReady = true;
        await StartSettingsSave();
    }

    private Task StartSettingsSave()
    {
        if (_saveTask is null || _saveTask.IsCompleted)
        {
            _saveTask = SavePendingSettingsAsync();
        }

        return _saveTask;
    }

    private async Task SavePendingSettingsAsync()
    {
        while (_pendingRefreshMinutes.HasValue
            || (_canAutoSaveSettings && _isPreferenceSaveReady && HasPendingContributionPreferences))
        {
            var requestedMinutes = _pendingRefreshMinutes;
            _pendingRefreshMinutes = null;
            _isPreferenceSaveReady = false;
            _preferenceSaveTimer.Stop();
            // Take the snapshot when the writer is ready so preference changes cannot overwrite a newer interval.
            var settings = _committedSettings with
            {
                RefreshMinutes = requestedMinutes ?? _committedSettings.RefreshMinutes,
                ContributionCellSize = ContributionCellSize
            };

            try
            {
                await _settingsStore.SaveAsync(settings, _lifetime.Token);
                _committedSettings = settings;
                _canAutoSaveSettings = true;

                if (requestedMinutes.HasValue)
                {
                    if (!_isShuttingDown)
                    {
                        _refreshSession.SetRefreshInterval(TimeSpan.FromMinutes(settings.RefreshMinutes));
                        _refreshTimer.Stop();
                        UpdateRefreshSchedule();
                        _refreshTimer.Start();
                    }

                    SettingsWarning = "";
                    var minutes = settings.RefreshMinutes;
                    SettingsStatus = $"Saved. Automatic refresh runs every {minutes} {(minutes == 1 ? "minute" : "minutes")}, including while the panel is hidden.";
                }
                else if (SettingsWarning == _settingsSaveWarning)
                {
                    SettingsWarning = "";
                }

                _settingsSaveWarning = "";
                if (HasPendingContributionPreferences
                    && !_isPreferenceSaveReady && !_preferenceSaveTimer.IsRunning)
                {
                    if (_isShuttingDown)
                    {
                        _isPreferenceSaveReady = true;
                    }
                    else
                    {
                        // Preference changes during an explicit repair had automatic saving disabled.
                        _preferenceSaveTimer.Start();
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                var warning = $"Settings could not be saved: {exception.Message} The previous refresh interval remains active. Graph preference changes are only in memory. Check access to the app's local data folder, then try Save again.";
                _settingsSaveWarning = requestedMinutes.HasValue ? "" : warning;
                SettingsWarning = warning;
                SettingsStatus = "";
            }
            finally
            {
                if (requestedMinutes.HasValue)
                {
                    IsSavingSettings = false;
                }
            }
        }
    }

    private void UpdateRefreshSchedule()
    {
        var minutes = _committedSettings.RefreshMinutes;
        _refreshTimer.Interval = TimeSpan.FromMinutes(minutes);
        RefreshScheduleLabel = $"Refresh every {minutes} {(minutes == 1 ? "minute" : "minutes")}";
    }

    private async void OnRefreshTimerTick(DispatcherQueueTimer sender, object args) =>
        await RefreshWithReasonAsync(DashboardRefreshReason.Periodic);

    public void SetPanelVisible(bool isVisible)
    {
        if (isVisible)
        {
            _stateProjection.Reveal(() => PrepareRefreshState(_refreshSession.State));
            _presentationClock.SetVisible(true);
        }
        else
        {
            _presentationClock.SetVisible(false);
            _stateProjection.Hide();
        }
    }

    private void OnClockTimerTick(DispatcherQueueTimer sender, object args) => _presentationClock.Tick();

    private void UpdateClock()
    {
        foreach (var section in Sections)
        {
            foreach (var row in section.Items)
            {
                row.UpdateRelativeTimestamp();
            }
        }
        UpdateCopilotDisplay(_refreshSession.State);
    }

    public Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        _isShuttingDown = true;
        _refreshSession.StateChanged -= OnSessionStateChanged;
        IsSettingsLoaded = false;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;
        _presentationClock.Stop();
        _clockTimer.Tick -= OnClockTimerTick;
        _preferenceSaveTimer.Stop();
        _preferenceSaveTimer.Tick -= OnPreferenceSaveTimerTick;
        RefreshCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
        ClearCachedDataCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanClearCachedData));
        try
        {
            var refreshShutdownTask = _startup.DisposeAsync().AsTask();
            _cacheInvalidatedSnapshot = null;
            _stateProjection.Stop(_refreshSession.State);
            try
            {
                _isPreferenceSaveReady = true;
                await StartSettingsSave();
            }
            finally
            {
                await Task.WhenAll(
                    refreshShutdownTask,
                    _lifetime.CancelAsync(),
                    _initializeTask ?? Task.CompletedTask,
                    _clearCacheTask ?? Task.CompletedTask);
            }
        }
        finally
        {
            _lifetime.Dispose();
        }
    }
}
