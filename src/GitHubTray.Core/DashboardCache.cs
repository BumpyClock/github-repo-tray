using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace GitHubTray.Core;

public static class DashboardCacheVersions
{
    public const int Schema = 1;
    public const int Selector = 1;
    public const int Activity = 1;
    public const int PullRequests = 1;
    public const int ReviewRequests = 1;
    public const int Repositories = 1;
    public const int Contributions = 1;
    public const int Copilot = 1;
}

public sealed record DashboardCacheAccount(string Host, long UserId, string Login);

public sealed record DashboardListCacheSection(
    int Revision,
    DateTimeOffset SucceededAt,
    IReadOnlyList<DashboardItem> Items);

public sealed record DashboardContributionCacheSection(
    int Revision,
    DateTimeOffset SucceededAt,
    ContributionCalendar Calendar);

public sealed record DashboardCopilotCacheSection(
    int Revision,
    DateTimeOffset SucceededAt,
    CopilotUsage Usage);

public sealed record DashboardCacheRecord(
    int SchemaVersion,
    DashboardCacheAccount Account,
    DashboardListCacheSection? Activity,
    DashboardListCacheSection? PullRequests,
    DashboardListCacheSection? ReviewRequests,
    DashboardListCacheSection? Repositories,
    DashboardContributionCacheSection? Contributions,
    DashboardCopilotCacheSection? Copilot);

public enum DashboardCacheDiagnosticKind
{
    Missing,
    Loaded,
    Written,
    Migrated,
    Pruned,
    Malformed,
    Incompatible,
    ReadFailed,
    WriteFailed,
    Cleared,
    ClearFailed,
    Invalidated
}

public sealed record DashboardCacheDiagnostic(DashboardCacheDiagnosticKind Kind, string Message);

public sealed record DashboardCacheReadResult(
    DashboardCacheRecord? Record,
    DashboardCacheDiagnostic Diagnostic);

public sealed record DashboardCacheWriteResult(DashboardCacheDiagnostic Diagnostic)
{
    public bool Succeeded => Diagnostic.Kind is DashboardCacheDiagnosticKind.Written
        or DashboardCacheDiagnosticKind.Pruned;
}

public sealed record DashboardCacheClearResult(
    int DeletedFileCount,
    int FailedFileCount,
    DashboardCacheDiagnostic Diagnostic)
{
    public bool Succeeded => FailedFileCount == 0 &&
        Diagnostic.Kind is DashboardCacheDiagnosticKind.Cleared;
}

public interface IDashboardCacheStore
{
    Task<DashboardCacheReadResult> ReadLastUsedAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<DashboardCacheReadResult> ReadAsync(
        DashboardCacheAccount account,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<DashboardCacheWriteResult> SelectAccountAsync(
        DashboardCacheAccount account,
        CancellationToken cancellationToken = default);

    Task<DashboardCacheWriteResult> WriteAsync(
        DashboardCacheRecord record,
        CancellationToken cancellationToken = default);

    Task<DashboardCacheClearResult> ClearAsync(
        CancellationToken cancellationToken = default);
}

public sealed class JsonDashboardCacheStore : IDashboardCacheStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    internal const int InactiveRecordCleanupLimit = 8;
    private const long MaximumFileSize = 16 * 1024 * 1024;
    private const string SelectorFileName = "last-account.json";
    private readonly string _rootDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _inactivePruneSelectionGate = new();
    private readonly Action<string, string>? _beforeReplace;
    private readonly Action<string>? _beforeDelete;
    private readonly Action? _beforeLastUsedRecordRead;
    private long _generation;
    private int _inactivePruneCursor;

    public JsonDashboardCacheStore(string rootDirectory)
        : this(rootDirectory, null, null, null)
    {
    }

    internal JsonDashboardCacheStore(string rootDirectory, Action<string, string>? beforeReplace)
        : this(rootDirectory, beforeReplace, null, null)
    {
    }

    internal JsonDashboardCacheStore(
        string rootDirectory,
        Action<string, string>? beforeReplace,
        Action<string>? beforeDelete)
        : this(rootDirectory, beforeReplace, beforeDelete, null)
    {
    }

    internal JsonDashboardCacheStore(
        string rootDirectory,
        Action<string, string>? beforeReplace,
        Action<string>? beforeDelete,
        Action? beforeLastUsedRecordRead)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _beforeReplace = beforeReplace;
        _beforeDelete = beforeDelete;
        _beforeLastUsedRecordRead = beforeLastUsedRecordRead;
    }

    public async Task<DashboardCacheReadResult> ReadLastUsedAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var operationGeneration = Volatile.Read(ref _generation);
        DashboardCacheAccount? account;
        DashboardCacheReadResult? legacy = null;
        DashboardCacheWriteResult? migration = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (operationGeneration != _generation)
            {
                return InvalidatedRead();
            }

            DeleteInterruptedWrites(GetSelectorFilePath());
            var selector = await ReadSelectorAsync(cancellationToken).ConfigureAwait(false);
            if (selector.Diagnostic is { } selectorDiagnostic)
            {
                return new(null, selectorDiagnostic);
            }
            if (selector.Account is { } selected)
            {
                account = selected;
            }
            else
            {
                legacy = await FindLegacyLastUsedAccountAsync(now, cancellationToken)
                    .ConfigureAwait(false);
                account = legacy.Record?.Account;
                if (account is not null)
                {
                    migration = await ReplaceSelectorAsync(account, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return ReadFailed();
        }
        finally
        {
            _gate.Release();
        }

        if (account is null)
        {
            return legacy ?? new(null, new(DashboardCacheDiagnosticKind.Missing,
                "No last-used dashboard account could be restored from this device."));
        }

        if (legacy is not null)
        {
            if (operationGeneration != Volatile.Read(ref _generation))
            {
                return InvalidatedRead();
            }
            if (migration is { Succeeded: false })
            {
                return legacy with
                {
                    Diagnostic = migration.Diagnostic with
                    {
                        Message = "Reusable legacy dashboard cache was loaded, but its last-used account selector could not be saved."
                    }
                };
            }
            return legacy with
            {
                Diagnostic = new(DashboardCacheDiagnosticKind.Migrated,
                    "Reusable dashboard cache was loaded and a legacy account record was selected for future startups.")
            };
        }

        _beforeLastUsedRecordRead?.Invoke();
        return await ReadAsync(account, now, operationGeneration, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<DashboardCacheReadResult> ReadAsync(
        DashboardCacheAccount account,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ReadAsync(account, now, Volatile.Read(ref _generation), cancellationToken);

    private async Task<DashboardCacheReadResult> ReadAsync(
        DashboardCacheAccount account,
        DateTimeOffset now,
        long operationGeneration,
        CancellationToken cancellationToken)
    {
        var filePath = GetFilePath(account);
        var inactiveCandidates = GetInactivePruneCandidates(filePath, cancellationToken);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (operationGeneration != _generation)
            {
                return InvalidatedRead();
            }

            await PruneInactiveRecordsAsync(inactiveCandidates, now, cancellationToken)
                .ConfigureAwait(false);
            return await ReadOwnedRecordAsync(filePath, account, now, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return ReadFailed();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DashboardCacheWriteResult> WriteAsync(
        DashboardCacheRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var operationGeneration = Volatile.Read(ref _generation);
        ValidateRecord(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (operationGeneration != _generation)
            {
                return InvalidatedWrite();
            }

            var filePath = GetFilePath(record.Account);
            if (!HasAnySection(record))
            {
                try
                {
                    DeleteOwnedFile(filePath);
                    return new(new(DashboardCacheDiagnosticKind.Pruned,
                        "Dashboard cache contained no reusable sections and was removed."));
                }
                catch (Exception exception) when (IsIoFailure(exception))
                {
                    return new(new(DashboardCacheDiagnosticKind.WriteFailed,
                        "Dashboard cache could not be removed; live data remains available."));
                }
            }
            return await ReplaceAsync(filePath, record, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DashboardCacheWriteResult> SelectAccountAsync(
        DashboardCacheAccount account,
        CancellationToken cancellationToken = default)
    {
        ValidateAccount(account);
        var operationGeneration = Volatile.Read(ref _generation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (operationGeneration != _generation)
            {
                return InvalidatedWrite();
            }

            return await ReplaceSelectorAsync(account, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<DashboardCacheClearResult> ClearAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ClearWorkerAsync(cancellationToken), CancellationToken.None);

    internal string GetFilePath(DashboardCacheAccount account)
    {
        ValidateAccount(account);
        return Path.Combine(_rootDirectory, account.Host.ToLowerInvariant(),
            $"{account.UserId}.json");
    }

    private Task<DashboardCacheWriteResult> ReplaceAsync(
        string filePath,
        DashboardCacheRecord record,
        CancellationToken cancellationToken) =>
        ReplaceAsync(
            filePath,
            record,
            DashboardCacheJsonContext.Default.DashboardCacheRecord,
            "Dashboard cache was replaced atomically for the verified account.",
            "Dashboard cache could not be written; live data remains available.",
            cancellationToken);

    private Task<DashboardCacheWriteResult> ReplaceSelectorAsync(
        DashboardCacheAccount account,
        CancellationToken cancellationToken) =>
        ReplaceAsync(
            GetSelectorFilePath(),
            new DashboardCacheSelector(DashboardCacheVersions.Selector, account),
            DashboardCacheJsonContext.Default.DashboardCacheSelector,
            "The last-used dashboard account was saved without credentials.",
            "The dashboard was refreshed, but its last-used account selector could not be saved.",
            cancellationToken);

    private async Task<DashboardCacheWriteResult> ReplaceAsync<T>(
        string filePath,
        T value,
        JsonTypeInfo<T> typeInfo,
        string successMessage,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath)!;
        var temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream, value, typeInfo, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _beforeReplace?.Invoke(temporaryPath, filePath);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, filePath, overwrite: true);
            return new(new(DashboardCacheDiagnosticKind.Written, successMessage));
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return new(new(DashboardCacheDiagnosticKind.WriteFailed, failureMessage));
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (IsIoFailure(exception))
            {
                // The destination remains authoritative; a future read removes interrupted writes.
            }
        }
    }

    private async Task<DashboardCacheClearResult> ClearWorkerAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _generation++;
            cancellationToken.ThrowIfCancellationRequested();
            var accountDirectory = Path.Combine(_rootDirectory, "github.com");
            string[] ownedFiles;
            try
            {
                var accountFiles = Directory.Exists(accountDirectory)
                    ? Directory.EnumerateFiles(accountDirectory, "*", SearchOption.TopDirectoryOnly)
                        .Where(IsOwnedCacheFile)
                    : [];
                var selectorFiles = Directory.Exists(_rootDirectory)
                    ? Directory.EnumerateFiles(_rootDirectory, $"{SelectorFileName}*", SearchOption.TopDirectoryOnly)
                        .Where(IsOwnedSelectorFile)
                    : [];
                ownedFiles = accountFiles.Concat(selectorFiles).ToArray();
            }
            catch (Exception exception) when (IsIoFailure(exception))
            {
                return ClearFailed(0, 0);
            }

            var deleted = 0;
            var failed = 0;
            foreach (var filePath in ownedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    _beforeDelete?.Invoke(filePath);
                    File.Delete(filePath);
                    deleted++;
                }
                catch (Exception exception) when (IsIoFailure(exception))
                {
                    failed++;
                }
            }

            TryDeleteEmptyDirectory(accountDirectory);
            TryDeleteEmptyDirectory(_rootDirectory);
            return failed == 0 ? Cleared(deleted) : ClearFailed(deleted, failed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string[] GetInactivePruneCandidates(
        string activeFilePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accountDirectory = Path.GetDirectoryName(activeFilePath)!;
        if (!Directory.Exists(accountDirectory))
        {
            return [];
        }

        try
        {
            var candidates = EnumerateInactiveOwnedFiles(
                Directory.EnumerateFiles(
                    accountDirectory, "*", SearchOption.TopDirectoryOnly),
                activeFilePath,
                cancellationToken);
            lock (_inactivePruneSelectionGate)
            {
                var selected = SelectInactivePrunePage(
                    candidates,
                    _inactivePruneCursor,
                    cancellationToken,
                    out var nextCursor);
                _inactivePruneCursor = nextCursor;
                return selected;
            }
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return [];
        }
    }

    internal static IEnumerable<string> EnumerateInactiveOwnedFiles(
        IEnumerable<string> paths,
        string activeFilePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsOwnedCacheFile(path) &&
                !string.Equals(path, activeFilePath, StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    internal static string[] SelectInactivePrunePage(
        IEnumerable<string> candidates,
        int cursor,
        CancellationToken cancellationToken,
        out int nextCursor)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (cursor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cursor));
        }

        var selected = TakeInactivePrunePage(
            candidates, cursor, cancellationToken);
        var effectiveCursor = cursor;
        if (selected.Length == 0 && cursor != 0)
        {
            effectiveCursor = 0;
            selected = TakeInactivePrunePage(
                candidates, effectiveCursor, cancellationToken);
        }

        nextCursor = selected.Length == InactiveRecordCleanupLimit &&
                     effectiveCursor <= int.MaxValue - InactiveRecordCleanupLimit
            ? effectiveCursor + InactiveRecordCleanupLimit
            : 0;
        return selected;
    }

    private static string[] TakeInactivePrunePage(
        IEnumerable<string> candidates,
        int skip,
        CancellationToken cancellationToken)
    {
        var selected = new List<string>(InactiveRecordCleanupLimit);
        using var enumerator = candidates.GetEnumerator();
        for (var index = 0; index < skip; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext())
            {
                return [];
            }
        }

        while (selected.Count < InactiveRecordCleanupLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext())
            {
                break;
            }
            selected.Add(enumerator.Current);
        }
        return selected.ToArray();
    }

    private async Task PruneInactiveRecordsAsync(
        IReadOnlyList<string> ownedFiles,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var filePath in ownedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!filePath.EndsWith(".json", StringComparison.Ordinal))
            {
                try
                {
                    File.Delete(filePath);
                }
                catch (Exception exception) when (IsIoFailure(exception))
                {
                    // Another process may hold an interrupted write; retry on a future read.
                }
                continue;
            }

            await PruneInactiveRecordAsync(filePath, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PruneInactiveRecordAsync(
        string filePath,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DashboardCacheRecord record;
        FileStream stream;
        try
        {
            stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return;
        }

        try
        {
            if (stream.Length > MaximumFileSize)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                DeleteOwnedFile(filePath);
                return;
            }

            await using (stream)
            {
                record = await JsonSerializer.DeserializeAsync(
                    stream, DashboardCacheJsonContext.Default.DashboardCacheRecord, cancellationToken)
                    .ConfigureAwait(false) ?? throw new JsonException("Cache must contain an object.");
            }

            if (record.SchemaVersion != DashboardCacheVersions.Schema)
            {
                DeleteOwnedFile(filePath);
                return;
            }
            ValidateAccount(record.Account);
            if (!string.Equals(GetFilePath(record.Account), filePath, StringComparison.OrdinalIgnoreCase))
            {
                DeleteOwnedFile(filePath);
                return;
            }

            var pruned = false;
            var retained = record with
            {
                Activity = Retain(record.Activity, DashboardCacheVersions.Activity, now, ref pruned),
                PullRequests = Retain(
                    record.PullRequests, DashboardCacheVersions.PullRequests, now, ref pruned),
                ReviewRequests = Retain(
                    record.ReviewRequests, DashboardCacheVersions.ReviewRequests, now, ref pruned),
                Repositories = Retain(
                    record.Repositories, DashboardCacheVersions.Repositories, now, ref pruned),
                Contributions = Retain(
                    record.Contributions, DashboardCacheVersions.Contributions, now, ref pruned),
                Copilot = Retain(record.Copilot, DashboardCacheVersions.Copilot, now, ref pruned)
            };
            ValidateRecord(retained);

            if (!HasAnySection(retained))
            {
                DeleteOwnedFile(filePath);
            }
            else if (pruned)
            {
                _ = await ReplaceAsync(filePath, retained, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or InvalidOperationException)
        {
            DeleteOwnedFile(filePath);
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            // A locked inactive record is retried during a future cache read.
        }
    }

    private async Task<DashboardCacheReadResult> ReadOwnedRecordAsync(
        string filePath,
        DashboardCacheAccount? expectedAccount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DeleteInterruptedWrites(filePath);
        FileStream stream;
        try
        {
            stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Missing();
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return ReadFailed();
        }

        DashboardCacheRecord record;
        try
        {
            if (stream.Length > MaximumFileSize)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                DeleteOwnedFile(filePath);
                return Malformed();
            }

            await using (stream)
            {
                record = await JsonSerializer.DeserializeAsync(
                    stream, DashboardCacheJsonContext.Default.DashboardCacheRecord, cancellationToken)
                    .ConfigureAwait(false) ?? throw new JsonException("Cache must contain an object.");
            }
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            DeleteOwnedFile(filePath);
            return Malformed();
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return ReadFailed();
        }

        if (record.SchemaVersion != DashboardCacheVersions.Schema)
        {
            DeleteOwnedFile(filePath);
            return new(null, new(DashboardCacheDiagnosticKind.Incompatible,
                "Dashboard cache used an incompatible schema or account partition and was ignored."));
        }

        try
        {
            ValidateAccount(record.Account);
        }
        catch (ArgumentException)
        {
            DeleteOwnedFile(filePath);
            return Malformed();
        }

        var account = expectedAccount ?? record.Account;
        if ((expectedAccount is not null && !AccountsMatch(expectedAccount, record.Account)) ||
            !string.Equals(GetFilePath(record.Account), filePath, StringComparison.OrdinalIgnoreCase))
        {
            DeleteOwnedFile(filePath);
            return new(null, new(DashboardCacheDiagnosticKind.Incompatible,
                "Dashboard cache used an incompatible schema or account partition and was ignored."));
        }

        var pruned = false;
        var activity = Retain(record.Activity, DashboardCacheVersions.Activity, now, ref pruned);
        var pulls = Retain(record.PullRequests, DashboardCacheVersions.PullRequests, now, ref pruned);
        var reviews = Retain(record.ReviewRequests, DashboardCacheVersions.ReviewRequests, now, ref pruned);
        var repositories = Retain(record.Repositories, DashboardCacheVersions.Repositories, now, ref pruned);
        var contributions = Retain(record.Contributions, DashboardCacheVersions.Contributions, now, ref pruned);
        var copilot = Retain(record.Copilot, DashboardCacheVersions.Copilot, now, ref pruned);

        try
        {
            ValidateList(activity);
            ValidateList(pulls);
            ValidateList(reviews);
            ValidateList(repositories);
            ValidateContributions(contributions);
            ValidateCopilot(copilot);
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or InvalidOperationException)
        {
            DeleteOwnedFile(filePath);
            return Malformed();
        }

        var retained = record with
        {
            Account = account,
            Activity = activity,
            PullRequests = pulls,
            ReviewRequests = reviews,
            Repositories = repositories,
            Contributions = contributions,
            Copilot = copilot
        };

        if (!HasAnySection(retained))
        {
            DeleteOwnedFile(filePath);
            return new(null, new(DashboardCacheDiagnosticKind.Pruned,
                "Dashboard cache contained no reusable sections and was pruned."));
        }

        if (pruned || !string.Equals(record.Account.Login, account.Login, StringComparison.Ordinal))
        {
            var replacement = await ReplaceAsync(filePath, retained, cancellationToken)
                .ConfigureAwait(false);
            if (!replacement.Succeeded)
            {
                return new(retained, replacement.Diagnostic with
                {
                    Message = "Reusable dashboard cache was loaded, but obsolete sections could not be pruned."
                });
            }
            return new(retained, new(DashboardCacheDiagnosticKind.Pruned,
                "Reusable dashboard cache was loaded and obsolete sections were pruned."));
        }

        return new(retained, new(DashboardCacheDiagnosticKind.Loaded,
            "Reusable dashboard cache was loaded for the saved account."));
    }

    private async Task<SelectorReadResult> ReadSelectorAsync(
        CancellationToken cancellationToken)
    {
        var filePath = GetSelectorFilePath();
        FileStream stream;
        try
        {
            stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(null, null);
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return new(null, new(
                DashboardCacheDiagnosticKind.ReadFailed,
                "The last-used dashboard account selector could not be read; no other account was selected."));
        }

        try
        {
            if (stream.Length > MaximumFileSize)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                DeleteOwnedFile(filePath);
                return new(null, new(
                    DashboardCacheDiagnosticKind.Malformed,
                    "The malformed last-used dashboard account selector was ignored and removed."));
            }

            DashboardCacheSelector selector;
            await using (stream)
            {
                selector = await JsonSerializer.DeserializeAsync(
                    stream,
                    DashboardCacheJsonContext.Default.DashboardCacheSelector,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new JsonException("Cache selector must contain an object.");
            }

            if (selector.SchemaVersion != DashboardCacheVersions.Selector)
            {
                DeleteOwnedFile(filePath);
                return new(null, new(
                    DashboardCacheDiagnosticKind.Incompatible,
                    "The last-used dashboard account selector used an incompatible schema and was removed."));
            }
            ValidateAccount(selector.Account);
            return new(selector.Account, null);
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or NotSupportedException)
        {
            DeleteOwnedFile(filePath);
            return new(null, new(
                DashboardCacheDiagnosticKind.Malformed,
                "The malformed last-used dashboard account selector was ignored and removed."));
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return new(null, new(
                DashboardCacheDiagnosticKind.ReadFailed,
                "The last-used dashboard account selector could not be read; no other account was selected."));
        }
    }

    private async Task<DashboardCacheReadResult> FindLegacyLastUsedAccountAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var accountDirectory = Path.Combine(_rootDirectory, "github.com");
        if (!Directory.Exists(accountDirectory))
        {
            return new(null, new(
                DashboardCacheDiagnosticKind.Missing,
                "No last-used dashboard account could be restored from this device."));
        }

        string[] candidates;
        try
        {
            candidates = Directory.EnumerateFiles(accountDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .Where(IsOwnedCacheFile)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return ReadFailed();
        }

        if (candidates.Length == 0)
        {
            return new(null, new(
                DashboardCacheDiagnosticKind.Missing,
                "No last-used dashboard account could be restored from this device."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await ReadOwnedRecordAsync(
            candidates[0], expectedAccount: null, now, cancellationToken).ConfigureAwait(false);
    }

    private static DashboardListCacheSection? Retain(
        DashboardListCacheSection? section,
        int expectedRevision,
        DateTimeOffset now,
        ref bool pruned)
    {
        if (section is null)
        {
            return null;
        }
        if (section.Revision != expectedRevision || !IsReusable(section.SucceededAt, now))
        {
            pruned = true;
            return null;
        }
        return section;
    }

    private static DashboardContributionCacheSection? Retain(
        DashboardContributionCacheSection? section,
        int expectedRevision,
        DateTimeOffset now,
        ref bool pruned)
    {
        if (section is null)
        {
            return null;
        }
        if (section.Revision != expectedRevision || !IsReusable(section.SucceededAt, now))
        {
            pruned = true;
            return null;
        }
        return section;
    }

    private static DashboardCopilotCacheSection? Retain(
        DashboardCopilotCacheSection? section,
        int expectedRevision,
        DateTimeOffset now,
        ref bool pruned)
    {
        if (section is null)
        {
            return null;
        }
        if (section.Revision != expectedRevision || !IsReusable(section.SucceededAt, now))
        {
            pruned = true;
            return null;
        }
        return section;
    }

    internal static bool IsReusable(DateTimeOffset succeededAt, DateTimeOffset now) =>
        succeededAt <= now && now - succeededAt < Retention;

    private static void ValidateRecord(DashboardCacheRecord record)
    {
        if (record.SchemaVersion != DashboardCacheVersions.Schema)
        {
            throw new ArgumentException("Unsupported dashboard cache schema.", nameof(record));
        }
        ValidateAccount(record.Account);
        ValidateList(record.Activity);
        ValidateList(record.PullRequests);
        ValidateList(record.ReviewRequests);
        ValidateList(record.Repositories);
        ValidateContributions(record.Contributions);
        ValidateCopilot(record.Copilot);
    }

    private static void ValidateAccount(DashboardCacheAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!string.Equals(account.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            account.UserId <= 0 || !IsValidLogin(account.Login))
        {
            throw new ArgumentException("Invalid dashboard cache account.", nameof(account));
        }
    }

    private static bool AccountsMatch(DashboardCacheAccount expected, DashboardCacheAccount actual) =>
        actual is not null &&
        string.Equals(expected.Host, actual.Host, StringComparison.OrdinalIgnoreCase) &&
        expected.UserId == actual.UserId;

    private static bool IsValidLogin(string login) =>
        !string.IsNullOrWhiteSpace(login) && login.Length <= 100 &&
        login.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static void ValidateList(DashboardListCacheSection? section)
    {
        if (section is null)
        {
            return;
        }
        if (section.Items is null || section.Items.Count > DashboardService.ItemLimit)
        {
            throw new JsonException("Invalid cached dashboard list.");
        }
        foreach (var item in section.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Repository) ||
                item.Url is null)
            {
                throw new JsonException("Invalid cached dashboard item.");
            }
            _ = DashboardService.GitHubUrl(item.Url.AbsoluteUri);
            if (item.PullRequest is { } pull &&
                (pull.Number <= 0 || string.IsNullOrWhiteSpace(pull.AuthorLogin) ||
                 pull.Labels.IsDefault || pull.Labels.Any(label => label is null) ||
                 pull.Checks is null ||
                 pull.Checks.Items.IsDefault ||
                 pull.Checks.Items.Any(check => check is null) ||
                 pull.AuthorAvatarUrl is { } avatar &&
                 !PullRequestParser.IsGitHubAvatarUrl(avatar)))
            {
                throw new JsonException("Invalid cached pull request.");
            }
        }
    }

    private static void ValidateContributions(DashboardContributionCacheSection? section)
    {
        if (section is null)
        {
            return;
        }
        if (section.Calendar is null || section.Calendar.TotalContributions < 0 ||
            section.Calendar.Weeks is null || section.Calendar.Weeks.Count > 54)
        {
            throw new JsonException("Invalid cached contribution calendar.");
        }
        foreach (var week in section.Calendar.Weeks)
        {
            if (week is null || week.Days is null || week.Days.Count > 7 ||
                week.Days.Any(day => day is null || day.Count < 0 || !Enum.IsDefined(day.Level)))
            {
                throw new JsonException("Invalid cached contribution week.");
            }
        }
    }

    private static void ValidateCopilot(DashboardCopilotCacheSection? section)
    {
        if (section is null)
        {
            return;
        }
        if (section.Usage is null || string.IsNullOrWhiteSpace(section.Usage.Plan) ||
            section.Usage.Plan.Length > 80 || section.Usage.Plan.Any(char.IsControl) ||
            section.Usage.Quotas.IsDefaultOrEmpty ||
            section.Usage.Quotas.Any(quota => quota is null ||
                !Enum.IsDefined(quota.Kind) ||
                !Enum.IsDefined(quota.Availability) ||
                (quota.Availability == CopilotQuotaAvailability.Limited
                    ? quota.PercentRemaining is not { } value ||
                      !double.IsFinite(value) || value is < 0 or > 100
                    : quota.PercentRemaining is not null)))
        {
            throw new JsonException("Invalid cached Copilot usage.");
        }
    }

    private static bool HasAnySection(DashboardCacheRecord record) =>
        record.Activity is not null || record.PullRequests is not null ||
        record.ReviewRequests is not null || record.Repositories is not null ||
        record.Contributions is not null || record.Copilot is not null;

    private static bool IsIoFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or
            System.Security.SecurityException;

    private static DashboardCacheReadResult Missing() =>
        new(null, new(DashboardCacheDiagnosticKind.Missing,
            "No dashboard cache exists for the selected saved account."));

    private static DashboardCacheReadResult Malformed() =>
        new(null, new(DashboardCacheDiagnosticKind.Malformed,
            "Malformed dashboard cache was ignored and removed."));

    private static DashboardCacheReadResult ReadFailed() =>
        new(null, new(DashboardCacheDiagnosticKind.ReadFailed,
            "Dashboard cache could not be read; live refresh will continue."));

    private static DashboardCacheReadResult InvalidatedRead() =>
        new(null, new(DashboardCacheDiagnosticKind.Invalidated,
            "A dashboard cache read started before clearing and was discarded."));

    private static DashboardCacheWriteResult InvalidatedWrite() =>
        new(new(DashboardCacheDiagnosticKind.Invalidated,
            "A dashboard cache write started before clearing and was discarded."));

    private static DashboardCacheClearResult Cleared(int deletedFileCount) =>
        new(
            deletedFileCount,
            0,
            new(DashboardCacheDiagnosticKind.Cleared,
                deletedFileCount == 0
                    ? "No saved dashboard cache files were present. GitHub sign-in and settings were preserved."
                    : $"Cleared {deletedFileCount} saved dashboard cache {(deletedFileCount == 1 ? "file" : "files")} across all accounts. GitHub sign-in and settings were preserved."));

    private static DashboardCacheClearResult ClearFailed(int deletedFileCount, int failedFileCount) =>
        new(
            deletedFileCount,
            failedFileCount,
            new(DashboardCacheDiagnosticKind.ClearFailed,
                failedFileCount == 0
                    ? "Dashboard cache files could not be inspected or cleared. GitHub sign-in and settings were preserved; try again after checking access to the app's local data folder."
                    : $"Cleared {deletedFileCount} saved dashboard cache {(deletedFileCount == 1 ? "file" : "files")}, but {failedFileCount} {(failedFileCount == 1 ? "file" : "files")} could not be deleted. GitHub sign-in and settings were preserved; try again after checking access to the app's local data folder."));

    private static bool IsOwnedCacheFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var marker = fileName.IndexOf(".json", StringComparison.Ordinal);
        if (marker <= 0 ||
            !long.TryParse(fileName.AsSpan(0, marker), NumberStyles.None,
                CultureInfo.InvariantCulture, out var userId) ||
            userId <= 0)
        {
            return false;
        }

        var suffix = fileName.AsSpan(marker);
        return suffix.SequenceEqual(".json") ||
            suffix.StartsWith(".json.", StringComparison.Ordinal) &&
            suffix.EndsWith(".tmp", StringComparison.Ordinal);
    }

    private static bool IsOwnedSelectorFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return string.Equals(fileName, SelectorFileName, StringComparison.Ordinal) ||
            fileName.StartsWith(SelectorFileName + ".", StringComparison.Ordinal) &&
            fileName.EndsWith(".tmp", StringComparison.Ordinal);
    }

    private static void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) &&
                !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            // Cache files are already gone; directory cleanup is best effort.
        }
    }

    private static void DeleteInterruptedWrites(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath)!;
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var temporaryPath in Directory.EnumerateFiles(
            directory, Path.GetFileName(filePath) + ".*.tmp", SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (IsIoFailure(exception))
            {
                // The valid destination, if present, remains readable.
            }
        }
    }

    private string GetSelectorFilePath() => Path.Combine(_rootDirectory, SelectorFileName);

    private static void DeleteOwnedFile(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
}

internal sealed record DashboardCacheSelector(
    int SchemaVersion,
    DashboardCacheAccount Account);

internal sealed record SelectorReadResult(
    DashboardCacheAccount? Account,
    DashboardCacheDiagnostic? Diagnostic);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(DashboardCacheRecord))]
[JsonSerializable(typeof(DashboardCacheSelector))]
internal partial class DashboardCacheJsonContext : JsonSerializerContext;
