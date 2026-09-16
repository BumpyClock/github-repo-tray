using System.Globalization;
using System.Text.Json;

namespace GitHubTray.Core;

public sealed class DashboardService
{
    public const int ItemLimit = 30;
    public static readonly TimeSpan RepositoryAndContributionFreshness = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan CopilotFreshness = TimeSpan.FromMinutes(5);

    private readonly IGitHubApi _api;
    private readonly IDashboardCacheStore? _cacheStore;
    private readonly TimeProvider _timeProvider;
    private readonly Action<DashboardCacheDiagnostic>? _reportCacheDiagnostic;

    public DashboardService(
        IGitHubApi api,
        IDashboardCacheStore? cacheStore = null,
        TimeProvider? timeProvider = null,
        Action<DashboardCacheDiagnostic>? reportCacheDiagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
        _cacheStore = cacheStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _reportCacheDiagnostic = reportCacheDiagnostic;
    }

    public async Task<DashboardSnapshot> RefreshAsync(
        DashboardSnapshot? previous = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RefreshCoreAsync(
            new DashboardRefreshRequest(DashboardRefreshReason.Manual, TimeSpan.Zero),
            previous,
            null,
            null,
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        return result.Snapshot;
    }

    public async Task<DashboardCacheClearResult> ClearCacheAsync(
        CancellationToken cancellationToken = default)
    {
        if (_cacheStore is null)
        {
            return new(
                0,
                0,
                new(DashboardCacheDiagnosticKind.Cleared,
                    "No dashboard cache store is configured. GitHub sign-in and settings were preserved."));
        }

        var result = await _cacheStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        Report(result.Diagnostic);
        return result;
    }

    public Task<DashboardRefreshResult> RefreshAsync(
        DashboardRefreshRequest request,
        DashboardSnapshot? previous = null,
        CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(request, previous, null, null, null, null, cancellationToken);

    public Task<DashboardRefreshResult> RefreshAsync(
        DashboardRefreshRequest request,
        DashboardSnapshot? previous,
        Action<GitHubUser, DashboardSnapshot?> publishVerifiedAccount,
        Action<GitHubUser?, string> publishAccountMismatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(publishVerifiedAccount);
        ArgumentNullException.ThrowIfNull(publishAccountMismatch);
        return RefreshCoreAsync(
            request,
            previous,
            null,
            publishVerifiedAccount,
            publishAccountMismatch,
            null,
            cancellationToken);
    }

    public async Task<DashboardSnapshot> RefreshWithHydrationAsync(
        DashboardSnapshot? previous,
        Action<DashboardSnapshot?> publishHydrated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publishHydrated);
        var result = await RefreshCoreAsync(
            new DashboardRefreshRequest(DashboardRefreshReason.Manual, TimeSpan.Zero),
            previous,
            publishHydrated,
            null,
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        return result.Snapshot;
    }

    public Task<DashboardRefreshResult> RefreshWithHydrationAsync(
        DashboardRefreshRequest request,
        DashboardSnapshot? previous,
        Action<DashboardSnapshot?> publishHydrated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(publishHydrated);
        return RefreshCoreAsync(
            request, previous, publishHydrated, null, null, null, cancellationToken);
    }

    public Task<DashboardRefreshResult> RefreshWithHydrationAsync(
        DashboardRefreshRequest request,
        DashboardSnapshot? previous,
        Action<DashboardSnapshot?> publishHydrated,
        Action<GitHubUser, DashboardSnapshot?> publishVerifiedAccount,
        Action<GitHubUser?, string> publishAccountMismatch,
        Func<CancellationToken, Task<TimeSpan>>? resolveStartupRefreshInterval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(publishHydrated);
        ArgumentNullException.ThrowIfNull(publishVerifiedAccount);
        ArgumentNullException.ThrowIfNull(publishAccountMismatch);
        return RefreshCoreAsync(
            request,
            previous,
            publishHydrated,
            publishVerifiedAccount,
            publishAccountMismatch,
            resolveStartupRefreshInterval,
            cancellationToken);
    }

    private async Task<DashboardRefreshResult> RefreshCoreAsync(
        DashboardRefreshRequest request,
        DashboardSnapshot? previous,
        Action<DashboardSnapshot?>? publishHydrated,
        Action<GitHubUser, DashboardSnapshot?>? publishVerifiedAccount,
        Action<GitHubUser?, string>? publishAccountMismatch,
        Func<CancellationToken, Task<TimeSpan>>? resolveStartupRefreshInterval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var now = _timeProvider.GetUtcNow();

        if (publishHydrated is not null)
        {
            DashboardSnapshot? hydrated = null;
            if (_cacheStore is not null)
            {
                var cached = await _cacheStore.ReadLastUsedAsync(now, cancellationToken)
                    .ConfigureAwait(false);
                Report(cached.Diagnostic);
                if (cached.Record is { } record)
                {
                    hydrated = FromCache(record);
                }
            }

            previous = MergeHydration(previous, hydrated);
            if (previous is not null)
            {
                previous = RetainEligible(previous, _timeProvider.GetUtcNow());
            }
            publishHydrated(HasSuccessfulSection(previous) ? previous : null);
        }

        var user = await ReadAsync("user", ParseUser, cancellationToken).ConfigureAwait(false);
        publishVerifiedAccount?.Invoke(user, null);

        // Never reuse private data from another gh account after an account switch.
        if (previous is not null && !AccountsMatch(previous.User, user))
        {
            previous = null;
        }

        if (publishHydrated is not null && previous is null && _cacheStore is not null)
        {
            var cached = await _cacheStore.ReadAsync(
                Account(user), _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            Report(cached.Diagnostic);
            if (cached.Record is { } record)
            {
                previous = RetainEligible(
                    FromCache(record) with { User = user },
                    _timeProvider.GetUtcNow());
                if (HasSuccessfulSection(previous))
                {
                    publishVerifiedAccount?.Invoke(user, previous);
                }
            }
        }

        if (_cacheStore is not null)
        {
            var selected = await _cacheStore.SelectAccountAsync(Account(user), cancellationToken)
                .ConfigureAwait(false);
            Report(selected.Diagnostic);
        }

        if (publishHydrated is not null &&
            request.Reason is DashboardRefreshReason.Startup &&
            resolveStartupRefreshInterval is not null)
        {
            request = request with
            {
                RefreshInterval = await resolveStartupRefreshInterval(cancellationToken)
                    .ConfigureAwait(false)
            };
            request.Validate();
        }

        now = _timeProvider.GetUtcNow();
        if (previous is not null)
        {
            previous = RetainEligible(previous with { User = user }, now);
        }
        var reusedSections = DashboardSectionKind.None;

        Task<DashboardSection> activity = CanReuse(
            request, DashboardSectionKind.Activity, previous?.Activity.UpdatedAt, now)
            ? Reuse(previous!.Activity, DashboardSectionKind.Activity, ref reusedSections)
            : LoadActivityAsync(user.Login, previous?.Activity, cancellationToken);
        Task<DashboardSection> authored = CanReuse(
            request, DashboardSectionKind.PullRequests, previous?.PullRequests.UpdatedAt, now)
            ? Reuse(previous!.PullRequests, DashboardSectionKind.PullRequests, ref reusedSections)
            : LoadPullRequestsAsync(user.Login, false, previous?.PullRequests, cancellationToken);
        Task<DashboardSection> reviews = CanReuse(
            request, DashboardSectionKind.ReviewRequests, previous?.ReviewRequests.UpdatedAt, now)
            ? Reuse(previous!.ReviewRequests, DashboardSectionKind.ReviewRequests, ref reusedSections)
            : LoadPullRequestsAsync(user.Login, true, previous?.ReviewRequests, cancellationToken);
        Task<DashboardSection> repositories = CanReuse(
            request, DashboardSectionKind.Repositories, previous?.Repositories.UpdatedAt, now)
            ? Reuse(previous!.Repositories, DashboardSectionKind.Repositories, ref reusedSections)
            : LoadRepositoriesAsync(previous?.Repositories, cancellationToken);
        Task<ContributionSection> contributions =
            previous?.Contributions.Calendar is not null &&
            CanReuse(request, DashboardSectionKind.Contributions,
                previous.Contributions.UpdatedAt, now)
            ? Reuse(previous.Contributions, DashboardSectionKind.Contributions, ref reusedSections)
            : LoadContributionsAsync(user.Login, previous?.Contributions, cancellationToken);
        Task<CopilotUsageSection> copilot =
            previous?.Copilot.Usage is not null &&
            CanReuse(request, DashboardSectionKind.Copilot, previous.Copilot.UpdatedAt, now)
            ? Reuse(previous.Copilot, DashboardSectionKind.Copilot, ref reusedSections)
            : LoadCopilotUsageAsync(user.Login, previous?.Copilot, cancellationToken);

        if (publishAccountMismatch is not null)
        {
            var mismatchPublished = 0;
            void PublishMismatch(GitHubAccountChangedException exception)
            {
                if (Interlocked.Exchange(ref mismatchPublished, 1) == 0)
                {
                    publishAccountMismatch(null, exception.Message);
                }
            }

            activity = ObserveAccountMismatchAsync(activity, PublishMismatch);
            authored = ObserveAccountMismatchAsync(authored, PublishMismatch);
            reviews = ObserveAccountMismatchAsync(reviews, PublishMismatch);
            repositories = ObserveAccountMismatchAsync(repositories, PublishMismatch);
            contributions = ObserveAccountMismatchAsync(contributions, PublishMismatch);
            copilot = ObserveAccountMismatchAsync(copilot, PublishMismatch);
        }

        await Task.WhenAll(activity, authored, reviews, repositories, contributions, copilot)
            .ConfigureAwait(false);

        var verifiedUser = await ReadAsync("user", ParseUser, cancellationToken).ConfigureAwait(false);
        if (!AccountsMatch(user, verifiedUser))
        {
            const string message = "The GitHub account changed during refresh. No new data was displayed. Refresh again to load the current account.";
            publishAccountMismatch?.Invoke(verifiedUser, message);
            if (_cacheStore is not null)
            {
                var selected = await _cacheStore.SelectAccountAsync(
                    Account(verifiedUser), cancellationToken).ConfigureAwait(false);
                Report(selected.Diagnostic);
            }
            throw new GitHubAccountChangedException(message);
        }

        var snapshot = new DashboardSnapshot(user, await activity, await authored, await reviews, await repositories)
        {
            Contributions = await contributions,
            Copilot = await copilot
        };
        if (_cacheStore is not null)
        {
            try
            {
                var persisted = await _cacheStore.WriteAsync(
                    CreateCacheRecord(snapshot, _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                Report(persisted.Diagnostic);
            }
            catch (Exception exception) when (
                exception is ArgumentException or JsonException or NotSupportedException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report(new(
                    DashboardCacheDiagnosticKind.WriteFailed,
                    "Dashboard cache rejected the refreshed data; live data remains available."));
            }
        }
        return new DashboardRefreshResult(snapshot, reusedSections);
    }

    private static async Task<T> ObserveAccountMismatchAsync<T>(
        Task<T> task,
        Action<GitHubAccountChangedException> publishMismatch)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (GitHubAccountChangedException exception)
        {
            publishMismatch(exception);
            throw;
        }
    }

    private static Task<T> Reuse<T>(
        T section,
        DashboardSectionKind kind,
        ref DashboardSectionKind reusedSections)
    {
        reusedSections |= kind;
        return Task.FromResult(section);
    }

    private static bool CanReuse(
        DashboardRefreshRequest request,
        DashboardSectionKind section,
        DateTimeOffset? successfulAt,
        DateTimeOffset now)
    {
        if (request.Reason is DashboardRefreshReason.Manual || successfulAt is null)
        {
            return false;
        }

        var freshness = section switch
        {
            DashboardSectionKind.Activity or
            DashboardSectionKind.PullRequests or
            DashboardSectionKind.ReviewRequests when request.Reason is DashboardRefreshReason.Startup =>
                request.RefreshInterval,
            DashboardSectionKind.Repositories or DashboardSectionKind.Contributions =>
                RepositoryAndContributionFreshness,
            DashboardSectionKind.Copilot => CopilotFreshness,
            _ => TimeSpan.Zero
        };
        var age = now - successfulAt.Value;
        return freshness > TimeSpan.Zero && age >= TimeSpan.Zero && age < freshness;
    }

    private async Task<CopilotUsageSection> LoadCopilotUsageAsync(
        string login, CopilotUsageSection? previous, CancellationToken cancellationToken)
    {
        try
        {
            var json = await _api.GetAsync(CopilotUsageParser.Endpoint, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var usage = CopilotUsageParser.Parse(document.RootElement, login);
            return new(usage, _timeProvider.GetUtcNow(), null)
            {
                Source = DashboardSectionSource.Live
            };
        }
        catch (GitHubException exception) when (exception is not GitHubAccountChangedException)
        {
            return Failed(previous, exception.Message);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or
                                         InvalidOperationException or KeyNotFoundException or
                                         OverflowException or ArgumentOutOfRangeException)
        {
            return Failed(previous,
                "GitHub returned an unexpected Copilot usage response. Refresh again or update GitHub Tray if this persists.");
        }
    }

    private async Task<DashboardSection> LoadPullRequestsAsync(
        string login, bool reviewRequested, DashboardSection? previous, CancellationToken cancellationToken)
    {
        try
        {
            var json = await _api.QueryAsync(PullRequestParser.Query(login, reviewRequested), cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var items = PullRequestParser.Parse(document.RootElement, login, reviewRequested);
            return new DashboardSection(items, _timeProvider.GetUtcNow(), null)
            {
                Source = DashboardSectionSource.Live
            };
        }
        catch (GitHubException exception) when (exception is not GitHubAccountChangedException)
        {
            return Failed(previous, exception.Message);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or
                                         InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            return Failed(previous,
                "GitHub returned an unexpected response for pull requests and checks. Refresh again or update GitHub Tray if this persists.");
        }
    }

    private async Task<DashboardSection> LoadActivityAsync(
        string login, DashboardSection? previous, CancellationToken cancellationToken)
    {
        try
        {
            var items = await ReadAsync($"users/{Uri.EscapeDataString(login)}/events?per_page={ItemLimit}",
                ParseActivity, cancellationToken).ConfigureAwait(false);
            var references = items.Where(item => item.PullRequestActivity is not null)
                .Select(item => new ActivityPullRequestReference(item.Repository, item.PullRequestActivity!.Number)).ToArray();
            if (references.Length > 0)
            {
                var json = await _api.QueryAsync(ActivityPullRequestQuery.Query(references), cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                items = ActivityPullRequestQuery.Hydrate(document.RootElement, login, items);
            }
            return new DashboardSection(items, _timeProvider.GetUtcNow(), null)
            {
                Source = DashboardSectionSource.Live
            };
        }
        catch (GitHubException exception) when (exception is not GitHubAccountChangedException)
        {
            return Failed(previous, exception.Message);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or
                                         InvalidOperationException or KeyNotFoundException or OverflowException or ArgumentException)
        {
            return Failed(previous,
                "GitHub returned an unexpected response for recent activity. Refresh again or update GitHub Tray if this persists.");
        }
    }

    private async Task<ContributionSection> LoadContributionsAsync(
        string login, ContributionSection? previous, CancellationToken cancellationToken)
    {
        try
        {
            var json = await _api.QueryAsync(ContributionCalendarParser.Query, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var calendar = ContributionCalendarParser.Parse(
                document.RootElement, login, DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime));
            return new ContributionSection(calendar, _timeProvider.GetUtcNow(), null)
            {
                Source = DashboardSectionSource.Live
            };
        }
        catch (GitHubException exception) when (exception is not GitHubAccountChangedException)
        {
            return Failed(previous, exception.Message);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or
                                         InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            return Failed(previous,
                "GitHub returned an unexpected contribution calendar. Refresh again or update GitHub Tray if this persists.");
        }
    }

    private async Task<DashboardSection> LoadRepositoriesAsync(
        DashboardSection? previous,
        CancellationToken cancellationToken)
    {
        try
        {
            var items = await ReadAsync(
                $"user/repos?sort=pushed&direction=desc&per_page={ItemLimit}&affiliation=owner,collaborator,organization_member",
                ParseRepositories, cancellationToken).ConfigureAwait(false);
            return new DashboardSection(items, _timeProvider.GetUtcNow(), null)
            {
                Source = DashboardSectionSource.Live
            };
        }
        catch (GitHubException exception)
        {
            return Failed(previous, exception.Message);
        }
    }

    private async Task<T> ReadAsync<T>(
        string endpoint,
        Func<JsonElement, T> parse,
        CancellationToken cancellationToken)
    {
        var json = await _api.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(json);
            return parse(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            throw new GitHubException("GitHub returned an unexpected response. Refresh again or update GitHub Tray if this persists.", exception);
        }
    }

    private void Report(DashboardCacheDiagnostic diagnostic)
    {
        if (diagnostic.Kind is not DashboardCacheDiagnosticKind.Missing
            and not DashboardCacheDiagnosticKind.Loaded
            and not DashboardCacheDiagnosticKind.Migrated
            and not DashboardCacheDiagnosticKind.Written)
        {
            _reportCacheDiagnostic?.Invoke(diagnostic);
        }
    }

    private static DashboardCacheAccount Account(GitHubUser user) =>
        new(user.Host, user.Id, user.Login);

    private static bool AccountsMatch(GitHubUser expected, GitHubUser actual) =>
        string.Equals(expected.Host, actual.Host, StringComparison.OrdinalIgnoreCase) &&
        expected.Id == actual.Id &&
        string.Equals(expected.Login, actual.Login, StringComparison.OrdinalIgnoreCase);

    private static DashboardSection Failed(DashboardSection? previous, string error) =>
        new(previous?.Items ?? [], previous?.UpdatedAt, error)
        {
            Source = previous?.UpdatedAt is not null
                ? DashboardSectionSource.Retained : DashboardSectionSource.Failed
        };

    private static ContributionSection Failed(ContributionSection? previous, string error) =>
        new(previous?.Calendar, previous?.UpdatedAt, error)
        {
            Source = previous?.UpdatedAt is not null
                ? DashboardSectionSource.Retained : DashboardSectionSource.Failed
        };

    private static CopilotUsageSection Failed(CopilotUsageSection? previous, string error) =>
        new(previous?.Usage, previous?.UpdatedAt, error)
        {
            Source = previous?.UpdatedAt is not null
                ? DashboardSectionSource.Retained : DashboardSectionSource.Failed
        };

    private static DashboardSnapshot RetainEligible(DashboardSnapshot snapshot, DateTimeOffset now) =>
        snapshot with
        {
            Activity = RetainEligible(snapshot.Activity, now),
            PullRequests = RetainEligible(snapshot.PullRequests, now),
            ReviewRequests = RetainEligible(snapshot.ReviewRequests, now),
            Repositories = RetainEligible(snapshot.Repositories, now),
            Contributions = RetainEligible(snapshot.Contributions, now),
            Copilot = RetainEligible(snapshot.Copilot, now)
        };

    private static DashboardSection RetainEligible(DashboardSection section, DateTimeOffset now) =>
        section.UpdatedAt is { } updated && JsonDashboardCacheStore.IsReusable(updated, now)
            ? section
            : new([], null, null) { Source = DashboardSectionSource.Missing };

    private static ContributionSection RetainEligible(ContributionSection section, DateTimeOffset now) =>
        section.UpdatedAt is { } updated && section.Calendar is not null &&
        JsonDashboardCacheStore.IsReusable(updated, now)
            ? section
            : new(null, null, null) { Source = DashboardSectionSource.Missing };

    private static CopilotUsageSection RetainEligible(CopilotUsageSection section, DateTimeOffset now) =>
        section.UpdatedAt is { } updated && section.Usage is not null &&
        JsonDashboardCacheStore.IsReusable(updated, now)
            ? section
            : new(null, null, null) { Source = DashboardSectionSource.Missing };

    private static DashboardSnapshot? MergeHydration(
        DashboardSnapshot? inMemory,
        DashboardSnapshot? hydrated)
    {
        if (inMemory is null)
        {
            return hydrated;
        }
        if (hydrated is null || !AccountsMatch(inMemory.User, hydrated.User))
        {
            return inMemory;
        }

        return new DashboardSnapshot(
            inMemory.User,
            Newer(inMemory.Activity, hydrated.Activity),
            Newer(inMemory.PullRequests, hydrated.PullRequests),
            Newer(inMemory.ReviewRequests, hydrated.ReviewRequests),
            Newer(inMemory.Repositories, hydrated.Repositories))
        {
            Contributions = Newer(inMemory.Contributions, hydrated.Contributions),
            Copilot = Newer(inMemory.Copilot, hydrated.Copilot)
        };
    }

    private static DashboardSection Newer(DashboardSection preferred, DashboardSection alternative) =>
        alternative.UpdatedAt is { } alternativeTime &&
        (preferred.UpdatedAt is null || alternativeTime > preferred.UpdatedAt)
            ? alternative
            : preferred;

    private static ContributionSection Newer(
        ContributionSection preferred,
        ContributionSection alternative) =>
        alternative.UpdatedAt is { } alternativeTime &&
        (preferred.UpdatedAt is null || alternativeTime > preferred.UpdatedAt)
            ? alternative
            : preferred;

    private static CopilotUsageSection Newer(
        CopilotUsageSection preferred,
        CopilotUsageSection alternative) =>
        alternative.UpdatedAt is { } alternativeTime &&
        (preferred.UpdatedAt is null || alternativeTime > preferred.UpdatedAt)
            ? alternative
            : preferred;

    private static bool HasSuccessfulSection(DashboardSnapshot? snapshot) =>
        snapshot is not null &&
        (snapshot.Activity.UpdatedAt is not null ||
         snapshot.PullRequests.UpdatedAt is not null ||
         snapshot.ReviewRequests.UpdatedAt is not null ||
         snapshot.Repositories.UpdatedAt is not null ||
         snapshot.Contributions is { Calendar: not null, UpdatedAt: not null } ||
         snapshot.Copilot is { Usage: not null, UpdatedAt: not null });

    private static DashboardSnapshot FromCache(DashboardCacheRecord record)
    {
        var cachedUser = new GitHubUser(
            record.Account.Host,
            record.Account.UserId,
            record.Account.Login,
            record.Account.Login,
            GitHubUrl($"https://github.com/{record.Account.Login}"));
        return
        new(
            cachedUser,
            FromCache(record.Activity),
            FromCache(record.PullRequests),
            FromCache(record.ReviewRequests),
            FromCache(record.Repositories))
        {
            Contributions = record.Contributions is { } contributions
                ? new(contributions.Calendar, contributions.SucceededAt, null)
                {
                    Source = DashboardSectionSource.Cached
                }
                : new(null, null, null) { Source = DashboardSectionSource.Missing },
            Copilot = record.Copilot is { } copilot
                ? new(copilot.Usage, copilot.SucceededAt, null)
                {
                    Source = DashboardSectionSource.Cached
                }
                : new(null, null, null) { Source = DashboardSectionSource.Missing }
        };
    }

    private static DashboardSection FromCache(DashboardListCacheSection? section) =>
        section is { } cached
            ? new(cached.Items, cached.SucceededAt, null)
            {
                Source = DashboardSectionSource.Cached
            }
            : new([], null, null) { Source = DashboardSectionSource.Missing };

    private static DashboardCacheRecord CreateCacheRecord(
        DashboardSnapshot snapshot,
        DateTimeOffset now) =>
        new(
            DashboardCacheVersions.Schema,
            Account(snapshot.User),
            Cache(snapshot.Activity, DashboardCacheVersions.Activity, now),
            Cache(snapshot.PullRequests, DashboardCacheVersions.PullRequests, now),
            Cache(snapshot.ReviewRequests, DashboardCacheVersions.ReviewRequests, now),
            Cache(snapshot.Repositories, DashboardCacheVersions.Repositories, now),
            Cache(snapshot.Contributions, now),
            Cache(snapshot.Copilot, now));

    private static DashboardListCacheSection? Cache(
        DashboardSection section,
        int revision,
        DateTimeOffset now) =>
        section.UpdatedAt is { } updated && JsonDashboardCacheStore.IsReusable(updated, now)
            ? new(revision, updated, section.Items)
            : null;

    private static DashboardContributionCacheSection? Cache(
        ContributionSection section,
        DateTimeOffset now) =>
        section.UpdatedAt is { } updated && section.Calendar is { } calendar &&
        JsonDashboardCacheStore.IsReusable(updated, now)
            ? new(DashboardCacheVersions.Contributions, updated, calendar)
            : null;

    private static DashboardCopilotCacheSection? Cache(
        CopilotUsageSection section,
        DateTimeOffset now) =>
        section.UpdatedAt is { } updated && section.Usage is { } usage &&
        JsonDashboardCacheStore.IsReusable(updated, now)
            ? new(DashboardCacheVersions.Copilot, updated, usage)
            : null;

    private static GitHubUser ParseUser(JsonElement root)
    {
        var login = Text(root, "login");
        if (login.Length > 100 || login.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new JsonException("Invalid GitHub login.");
        }
        var id = root.GetProperty("id").GetInt64();
        if (id <= 0)
        {
            throw new JsonException("Invalid GitHub user ID.");
        }
        return new GitHubUser("github.com", id, login,
            OptionalText(root, "name") ?? login, GitHubUrl(Text(root, "html_url")));
    }

    private static IReadOnlyList<DashboardItem> ParseRepositories(JsonElement root) =>
        root.EnumerateArray().Take(ItemLimit).Select(repo =>
        {
            var fullName = Text(repo, "full_name");
            var description = OptionalText(repo, "description");
            var language = OptionalText(repo, "language");
            var visibility = OptionalBool(repo, "private") ? "Private" : "Public";
            var archived = OptionalBool(repo, "archived") ? " / Archived" : "";
            return new DashboardItem(
                fullName, fullName, string.IsNullOrWhiteSpace(language) ? "Repository" : language,
                $"{visibility}{archived}" + (string.IsNullOrWhiteSpace(description) ? "" : $" / {description}"),
                OptionalDate(repo, "pushed_at") ?? Date(repo, "updated_at"),
                GitHubUrl(Text(repo, "html_url")));
        }).ToArray();

    private static IReadOnlyList<DashboardItem> ParseActivity(JsonElement root) =>
        root.EnumerateArray().Select(ParseEvent)
            .OrderByDescending(item => item.UpdatedAt).DistinctBy(item => item.Id).Take(ItemLimit)
            .GroupBy(item => item.PullRequestActivity is { } pull
                ? $"pr:{item.Repository}/{pull.Number}" : $"event:{item.Id}")
            .Select(group =>
            {
                var latest = group.First();
                return latest.PullRequestActivity is { } activity
                    ? latest with { PullRequestActivity = activity with { EventCount = group.Count() } }
                    : latest;
            })
            .ToArray();

    private static DashboardItem ParseEvent(JsonElement item)
    {
        var repository = Text(item.GetProperty("repo"), "name");
        var segments = repository.Split('/');
        if (segments.Length != 2 || segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new JsonException("Invalid repository name.");
        }
        var repositoryUrl = new Uri($"https://github.com/{Uri.EscapeDataString(segments[0])}/{Uri.EscapeDataString(segments[1])}");
        var type = Text(item, "type");
        var payload = item.GetProperty("payload");
        var action = OptionalText(payload, "action");
        var title = type switch
        {
            "PushEvent" => "Pushed commits",
            "PullRequestEvent" => $"{Capitalize(action ?? "updated")} a pull request",
            "PullRequestReviewEvent" => "Reviewed a pull request",
            "PullRequestReviewCommentEvent" => "Commented on a pull request",
            "IssuesEvent" => $"{Capitalize(action ?? "updated")} an issue",
            "IssueCommentEvent" => "Commented on an issue or pull request",
            "CreateEvent" => $"Created a {OptionalText(payload, "ref_type") ?? "reference"}",
            "DeleteEvent" => $"Deleted a {OptionalText(payload, "ref_type") ?? "reference"}",
            "ForkEvent" => "Forked a repository",
            "WatchEvent" => "Starred a repository",
            "ReleaseEvent" => $"{Capitalize(action ?? "published")} a release",
            "PublicEvent" => "Made a repository public",
            "MemberEvent" => "Updated a collaborator",
            "GollumEvent" => "Updated the wiki",
            _ => "Repository activity"
        };
        var detail = type == "PushEvent"
            ? (OptionalText(payload, "ref") ?? "Commits").Replace("refs/heads/", "", StringComparison.Ordinal)
            : OptionalText(payload, "ref") ?? "";
        var url = repositoryUrl;
        foreach (var key in new[] { "comment", "review", "pull_request", "issue", "release", "forkee" })
        {
            if (payload.TryGetProperty(key, out var subject) && subject.ValueKind == JsonValueKind.Object)
            {
                detail = OptionalText(subject, "title") ?? OptionalText(subject, "name") ?? detail;
                if (OptionalText(subject, "html_url") is { } target)
                {
                    url = GitHubUrl(target);
                    break;
                }
            }
        }
        PullRequestActivity? activity = null;
        var isPull = payload.TryGetProperty("pull_request", out var pull) && pull.ValueKind == JsonValueKind.Object;
        if (!isPull && type == "IssueCommentEvent" && payload.TryGetProperty("issue", out var issue) &&
            issue.TryGetProperty("pull_request", out _))
        {
            pull = issue;
            isPull = true;
            title = "Commented on a pull request";
        }
        if (isPull)
        {
            var number = pull.GetProperty("number").GetInt32();
            if (number < 1) throw new JsonException("Invalid activity pull request number.");
            activity = new PullRequestActivity(number, title, 1);
            url = new Uri($"{repositoryUrl.AbsoluteUri}/pull/{number}");
        }
        return new DashboardItem(Text(item, "id"), title, repository, detail, Date(item, "created_at"), url)
        {
            PullRequestActivity = activity
        };
    }

    internal static Uri GitHubUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
        {
            throw new JsonException("Expected an HTTPS github.com URL.");
        }
        return uri;
    }

    private static string Text(JsonElement element, string name) =>
        OptionalText(element, name) is { Length: > 0 } value ? value : throw new JsonException($"Missing {name}.");

    private static string? OptionalText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static bool OptionalBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null && value.GetBoolean();

    private static DateTimeOffset Date(JsonElement element, string name) =>
        OptionalDate(element, name) ?? throw new JsonException($"Missing {name}.");

    private static DateTimeOffset? OptionalDate(JsonElement element, string name) =>
        OptionalText(element, name) is { } value
            ? DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
            : null;

    private static string Capitalize(string value) =>
        value.Length > 0 ? char.ToUpperInvariant(value[0]) + value[1..] : value;
}
