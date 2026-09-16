using System.Text.Json;
using System.Text.Json.Nodes;
using GitHubTray.Core;

namespace GitHubTray.Core.Tests;

public sealed class DashboardCacheStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DashboardCacheAccount Octocat =
        new("github.com", 1, "octocat");
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "GitHubTray.Cache.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RoundTripUsesVersionedAccountPartitionAndPreservesSuccessfulEmptySections()
    {
        var store = new JsonDashboardCacheStore(_directory);
        var record = Record(
            activity: ListSection("retained"),
            pullRequests: new(DashboardCacheVersions.PullRequests, Now.AddHours(-2), []));

        var written = await store.WriteAsync(record);
        var loaded = await store.ReadAsync(Octocat, Now);

        Assert.True(written.Succeeded);
        Assert.Equal(DashboardCacheDiagnosticKind.Loaded, loaded.Diagnostic.Kind);
        Assert.Equal(record.SchemaVersion, loaded.Record!.SchemaVersion);
        Assert.Equal(record.Account, loaded.Record.Account);
        Assert.Equal(record.Activity!.SucceededAt, loaded.Record.Activity!.SucceededAt);
        Assert.Equal(record.Activity.Items, loaded.Record.Activity.Items);
        Assert.Empty(Assert.IsType<DashboardListCacheSection>(loaded.Record!.PullRequests).Items);
        var path = store.GetFilePath(Octocat);
        Assert.True(File.Exists(path));
        Assert.StartsWith(Path.GetFullPath(_directory), path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("settings.json", path, StringComparison.OrdinalIgnoreCase);
        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"schemaVersion\":1", json);
        Assert.Contains("\"userId\":1", json);
        Assert.DoesNotContain("error", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplicitLastUsedSelectionIsIndependentOfSectionSuccessTimes()
    {
        var store = new JsonDashboardCacheStore(_directory);
        var other = new DashboardCacheAccount("github.com", 2, "other");
        await store.WriteAsync(Record(
            activity: ListSection("newer-section") with { SucceededAt = Now.AddMinutes(-1) }));
        await store.WriteAsync(Record(
            other,
            activity: ListSection("older-section") with { SucceededAt = Now.AddDays(-1) }));
        await store.SelectAccountAsync(other);

        var selected = await store.ReadLastUsedAsync(Now);

        Assert.Equal(other, selected.Record!.Account);
        Assert.Equal("older-section", Assert.Single(selected.Record.Activity!.Items).Id);
    }

    [Fact]
    public async Task LegacyRecordsMigrateUsingLastWriteTimeThenKeepTheDurableSelection()
    {
        var store = new JsonDashboardCacheStore(_directory);
        var other = new DashboardCacheAccount("github.com", 2, "other");
        await store.WriteAsync(Record(
            activity: ListSection("newer-section") with { SucceededAt = Now.AddMinutes(-1) }));
        await store.WriteAsync(Record(
            other,
            activity: ListSection("older-section") with { SucceededAt = Now.AddDays(-1) }));
        File.SetLastWriteTimeUtc(store.GetFilePath(Octocat), Now.AddMinutes(-2).UtcDateTime);
        File.SetLastWriteTimeUtc(store.GetFilePath(other), Now.AddMinutes(-1).UtcDateTime);

        var migrated = await store.ReadLastUsedAsync(Now);
        File.SetLastWriteTimeUtc(store.GetFilePath(Octocat), Now.UtcDateTime);
        var selectedAgain = await store.ReadLastUsedAsync(Now);

        Assert.Equal(DashboardCacheDiagnosticKind.Migrated, migrated.Diagnostic.Kind);
        Assert.Equal(other, migrated.Record!.Account);
        Assert.Equal(other, selectedAgain.Record!.Account);
        Assert.True(File.Exists(Path.Combine(_directory, "last-account.json")));
    }

    [Theory]
    [InlineData("malformed", DashboardCacheDiagnosticKind.Malformed)]
    [InlineData("incompatible", DashboardCacheDiagnosticKind.Incompatible)]
    public async Task InvalidSelectorDoesNotSilentlyFallBackToAnotherAccount(
        string selectorKind,
        DashboardCacheDiagnosticKind expectedDiagnostic)
    {
        var store = new JsonDashboardCacheStore(_directory);
        var other = new DashboardCacheAccount("github.com", 2, "other");
        await store.WriteAsync(Record(activity: ListSection("octocat")));
        await store.WriteAsync(Record(other, activity: ListSection("other")));
        var selectorPath = Path.Combine(_directory, "last-account.json");
        await File.WriteAllTextAsync(
            selectorPath,
            selectorKind == "malformed"
                ? "not-json"
                : """{"schemaVersion":999,"account":{"host":"github.com","userId":2,"login":"other"}}""");

        var result = await store.ReadLastUsedAsync(Now);

        Assert.Null(result.Record);
        Assert.Equal(expectedDiagnostic, result.Diagnostic.Kind);
        Assert.False(File.Exists(selectorPath));
    }

    [Fact]
    public async Task UnreadableSelectorDoesNotSilentlyFallBackToAnotherAccount()
    {
        var store = new JsonDashboardCacheStore(_directory);
        await store.WriteAsync(Record(activity: ListSection("octocat")));
        await store.SelectAccountAsync(Octocat);
        var selectorPath = Path.Combine(_directory, "last-account.json");
        await using var locked = new FileStream(
            selectorPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await store.ReadLastUsedAsync(Now);

        Assert.Null(result.Record);
        Assert.Equal(DashboardCacheDiagnosticKind.ReadFailed, result.Diagnostic.Kind);
        Assert.True(File.Exists(selectorPath));
    }

    [Fact]
    public async Task LegacyMigrationPrunesExpiredInvalidSectionsBeforeValidatingReusableData()
    {
        var store = new JsonDashboardCacheStore(_directory);
        await store.WriteAsync(Record(
            activity: ListSection("expired") with
            {
                SucceededAt = Now - JsonDashboardCacheStore.Retention
            },
            pullRequests: ListSection(
                "eligible", DashboardCacheVersions.PullRequests)));
        var path = store.GetFilePath(Octocat);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        root["activity"]!["items"]![0]!["title"] = null;
        await File.WriteAllTextAsync(path, root.ToJsonString());

        var result = await store.ReadLastUsedAsync(Now);

        Assert.Equal(DashboardCacheDiagnosticKind.Migrated, result.Diagnostic.Kind);
        Assert.Null(result.Record!.Activity);
        Assert.Equal("eligible", Assert.Single(result.Record.PullRequests!.Items).Id);
    }

    [Theory]
    [InlineData("expired", DashboardCacheDiagnosticKind.Pruned)]
    [InlineData("invalid", DashboardCacheDiagnosticKind.Malformed)]
    public async Task UnusableLegacyRecordReturnsItsExplicitDiagnostic(
        string kind,
        DashboardCacheDiagnosticKind expectedDiagnostic)
    {
        var store = new JsonDashboardCacheStore(_directory);
        var older = new DashboardCacheAccount("github.com", 2, "older");
        await store.WriteAsync(Record(
            older,
            activity: ListSection("older-valid")));
        await store.WriteAsync(Record(activity: ListSection("legacy") with
        {
            SucceededAt = kind == "expired"
                ? Now - JsonDashboardCacheStore.Retention
                : Now.AddHours(-1)
        }));
        var path = store.GetFilePath(Octocat);
        if (kind == "invalid")
        {
            await File.WriteAllTextAsync(path, "not-json");
        }
        File.SetLastWriteTimeUtc(store.GetFilePath(older), Now.AddMinutes(-2).UtcDateTime);
        File.SetLastWriteTimeUtc(path, Now.AddMinutes(-1).UtcDateTime);

        var result = await store.ReadLastUsedAsync(Now);

        Assert.Null(result.Record);
        Assert.Equal(expectedDiagnostic, result.Diagnostic.Kind);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(store.GetFilePath(older)));
        Assert.False(File.Exists(Path.Combine(_directory, "last-account.json")));
    }

    [Fact]
    public async Task FailedLegacySelectorMigrationReturnsReusableDataWithWriteDiagnostic()
    {
        var initialStore = new JsonDashboardCacheStore(_directory);
        await initialStore.WriteAsync(Record(activity: ListSection("legacy")));
        var failingStore = new JsonDashboardCacheStore(
            _directory,
            (_, destination) =>
            {
                if (destination.EndsWith("last-account.json", StringComparison.Ordinal))
                {
                    throw new IOException("Injected selector failure");
                }
            });

        var result = await failingStore.ReadLastUsedAsync(Now);

        Assert.NotNull(result.Record);
        Assert.Equal("legacy", Assert.Single(result.Record.Activity!.Items).Id);
        Assert.Equal(DashboardCacheDiagnosticKind.WriteFailed, result.Diagnostic.Kind);
        Assert.False(File.Exists(Path.Combine(_directory, "last-account.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledSelectorReplacementPreservesThePreviousSelection(bool cancel)
    {
        var store = new JsonDashboardCacheStore(_directory);
        var other = new DashboardCacheAccount("github.com", 2, "other");
        await store.WriteAsync(Record(activity: ListSection("selected")));
        await store.WriteAsync(Record(other, activity: ListSection("other")));
        await store.SelectAccountAsync(Octocat);
        using var cancellation = new CancellationTokenSource();
        var failingStore = new JsonDashboardCacheStore(_directory, (_, _) =>
        {
            if (cancel)
            {
                cancellation.Cancel();
            }
            else
            {
                throw new IOException("Injected selector replacement failure");
            }
        });

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                failingStore.SelectAccountAsync(other, cancellation.Token));
        }
        else
        {
            var result = await failingStore.SelectAccountAsync(other);
            Assert.False(result.Succeeded);
            Assert.Equal(DashboardCacheDiagnosticKind.WriteFailed, result.Diagnostic.Kind);
        }

        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp", SearchOption.AllDirectories));
        var selected = await store.ReadLastUsedAsync(Now);
        Assert.Equal(DashboardCacheDiagnosticKind.Loaded, selected.Diagnostic.Kind);
        Assert.Equal(Octocat, selected.Record!.Account);
        Assert.Equal("selected", Assert.Single(selected.Record.Activity!.Items).Id);
    }

    [Fact]
    public async Task InterruptedSelectorWriteIsRemovedWithoutChangingTheSelectedAccount()
    {
        var store = new JsonDashboardCacheStore(_directory);
        await store.WriteAsync(Record(activity: ListSection("selected")));
        await store.SelectAccountAsync(Octocat);
        var interrupted = Path.Combine(_directory, "last-account.json.interrupted.tmp");
        var unrelated = Path.Combine(_directory, "settings.json.interrupted.tmp");
        await File.WriteAllTextAsync(interrupted, "partial");
        await File.WriteAllTextAsync(unrelated, "keep");

        var selected = await store.ReadLastUsedAsync(Now);

        Assert.Equal(DashboardCacheDiagnosticKind.Loaded, selected.Diagnostic.Kind);
        Assert.Equal(Octocat, selected.Record!.Account);
        Assert.False(File.Exists(interrupted));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
    }

    [Fact]
    public async Task ClearBetweenSelectorAndRecordReadInvalidatesTheWholeLastUsedOperation()
    {
        using var readReady = new ManualResetEventSlim();
        using var releaseRead = new ManualResetEventSlim();
        var initialStore = new JsonDashboardCacheStore(_directory);
        await initialStore.WriteAsync(Record(activity: ListSection("selected")));
        await initialStore.SelectAccountAsync(Octocat);
        var store = new JsonDashboardCacheStore(
            _directory,
            null,
            null,
            () =>
            {
                readReady.Set();
                if (!releaseRead.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The test did not release the record read.");
                }
            });

        var read = Task.Run(() => store.ReadLastUsedAsync(Now));
        Assert.True(readReady.Wait(TimeSpan.FromSeconds(10)));
        var clear = await store.ClearAsync();
        releaseRead.Set();
        var result = await read;

        Assert.True(clear.Succeeded);
        Assert.Null(result.Record);
        Assert.Equal(DashboardCacheDiagnosticKind.Invalidated, result.Diagnostic.Kind);
    }

    [Fact]
    public async Task ExactSevenDayBoundaryIsPrunedPerSectionWithoutRenewingOtherTimestamps()
    {
        var store = new JsonDashboardCacheStore(_directory);
        var expired = ListSection("expired") with { SucceededAt = Now - TimeSpan.FromDays(7) };
        var eligibleAt = Now - TimeSpan.FromDays(7) + TimeSpan.FromTicks(1);
        var eligible = ListSection("eligible", DashboardCacheVersions.PullRequests) with
        {
            SucceededAt = eligibleAt
        };
        await store.WriteAsync(Record(activity: expired, pullRequests: eligible));

        var loaded = await store.ReadAsync(Octocat, Now);

        Assert.Equal(DashboardCacheDiagnosticKind.Pruned, loaded.Diagnostic.Kind);
        Assert.Null(loaded.Record!.Activity);
        Assert.Equal(eligibleAt, loaded.Record.PullRequests!.SucceededAt);
        var secondRead = await store.ReadAsync(Octocat, Now);
        Assert.Equal(DashboardCacheDiagnosticKind.Loaded, secondRead.Diagnostic.Kind);
        Assert.Equal(eligibleAt, secondRead.Record!.PullRequests!.SucceededAt);
    }

    [Fact]
    public async Task IncompatibleSectionRevisionIsPrunedWithoutDiscardingCompatibleSections()
    {
        var store = new JsonDashboardCacheStore(_directory);
        var incompatible = ListSection("old-query") with { Revision = 999 };
        var compatible = ListSection("current-query", DashboardCacheVersions.PullRequests);
        await store.WriteAsync(Record(activity: incompatible, pullRequests: compatible));

        var loaded = await store.ReadAsync(Octocat, Now);

        Assert.Equal(DashboardCacheDiagnosticKind.Pruned, loaded.Diagnostic.Kind);
        Assert.Null(loaded.Record!.Activity);
        Assert.Equal("current-query", Assert.Single(loaded.Record.PullRequests!.Items).Id);
    }

    [Fact]
    public async Task AccountRecordsAreIndependentAndLoginCaseDoesNotChangeTheStablePartition()
    {
        var store = new JsonDashboardCacheStore(_directory);
        var other = new DashboardCacheAccount("github.com", 2, "other");
        await store.WriteAsync(Record(activity: ListSection("octocat")));
        await store.WriteAsync(Record(other, activity: ListSection("other")));
        await store.SelectAccountAsync(other);

        var octocat = await store.ReadAsync(Octocat with { Login = "OCTOCAT" }, Now);
        var second = await store.ReadAsync(other, Now);

        Assert.Equal("octocat", Assert.Single(octocat.Record!.Activity!.Items).Id);
        Assert.Equal("other", Assert.Single(second.Record!.Activity!.Items).Id);
        Assert.NotEqual(store.GetFilePath(Octocat), store.GetFilePath(other));
        Assert.Equal("OCTOCAT", octocat.Record.Account.Login);
    }

    [Fact]
    public async Task ReadingActiveAccountDeletesFullyExpiredInactiveAccountRecord()
    {
        var store = new JsonDashboardCacheStore(_directory);
        var other = new DashboardCacheAccount("github.com", 2, "other");
        await store.WriteAsync(Record(activity: ListSection("active")));
        await store.WriteAsync(Record(
            other,
            activity: ListSection("expired") with
            {
                SucceededAt = Now - JsonDashboardCacheStore.Retention
            }));
        var otherPath = store.GetFilePath(other);

        var active = await store.ReadAsync(Octocat, Now);

        Assert.Equal(DashboardCacheDiagnosticKind.Loaded, active.Diagnostic.Kind);
        Assert.False(File.Exists(otherPath));
    }

    [Fact]
    public async Task ReadingActiveAccountPrunesExpiredSectionsFromInactiveAccountRecord()
    {
        var store = new JsonDashboardCacheStore(_directory);
        var other = new DashboardCacheAccount("github.com", 2, "other");
        await store.WriteAsync(Record(activity: ListSection("active")));
        await store.WriteAsync(Record(
            other,
            activity: ListSection("expired") with
            {
                SucceededAt = Now - JsonDashboardCacheStore.Retention
            },
            pullRequests: ListSection(
                "eligible", DashboardCacheVersions.PullRequests) with
            {
                SucceededAt = Now - JsonDashboardCacheStore.Retention + TimeSpan.FromTicks(1)
            }));
        var otherPath = store.GetFilePath(other);

        _ = await store.ReadAsync(Octocat, Now);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(otherPath));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("activity").ValueKind);
        Assert.Equal(
            "eligible",
            document.RootElement.GetProperty("pullRequests").GetProperty("items")[0]
                .GetProperty("id").GetString());
    }

    [Fact]
    public async Task InactiveCleanupUsesRotatingBoundedPages()
    {
        var store = new JsonDashboardCacheStore(_directory);
        await store.WriteAsync(Record(activity: ListSection("active")));
        for (var userId = 2; userId <= JsonDashboardCacheStore.InactiveRecordCleanupLimit + 3; userId++)
        {
            await store.WriteAsync(Record(
                new("github.com", userId, $"user-{userId}"),
                activity: ListSection($"expired-{userId}") with
                {
                    SucceededAt = Now - JsonDashboardCacheStore.Retention
                }));
        }
        var accountDirectory = Path.GetDirectoryName(store.GetFilePath(Octocat))!;

        _ = await store.ReadAsync(Octocat, Now);

        Assert.Equal(
            3,
            Directory.EnumerateFiles(accountDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .Count());

        _ = await store.ReadAsync(Octocat, Now);

        Assert.Single(Directory.EnumerateFiles(
            accountDirectory, "*.json", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void InactivePrunePageIsBoundedAndEventuallyCoversAStableDirectory()
    {
        var candidates = Enumerable.Range(2, 19)
            .Select(userId => Path.Combine(_directory, $"{userId}.json"))
            .ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cursor = 0;

        for (var pageIndex = 0; pageIndex < 3; pageIndex++)
        {
            var page = JsonDashboardCacheStore.SelectInactivePrunePage(
                candidates,
                cursor,
                CancellationToken.None,
                out cursor);
            Assert.InRange(page.Length, 1, JsonDashboardCacheStore.InactiveRecordCleanupLimit);
            seen.UnionWith(page);
        }

        Assert.Equal(0, cursor);
        Assert.Equal(candidates.Length, seen.Count);
        Assert.All(candidates, candidate => Assert.Contains(candidate, seen));
    }

    [Fact]
    public void InactivePrunePageStopsAtTheBoundAndObservesCancellationWhileEnumerating()
    {
        var enumerated = 0;
        IEnumerable<string> CountedCandidates()
        {
            for (var index = 0; index < 10_000; index++)
            {
                enumerated++;
                yield return Path.Combine(_directory, $"{index + 2}.json");
            }
        }

        var page = JsonDashboardCacheStore.SelectInactivePrunePage(
            CountedCandidates(),
            0,
            CancellationToken.None,
            out var cursor);

        Assert.Equal(JsonDashboardCacheStore.InactiveRecordCleanupLimit, page.Length);
        Assert.Equal(JsonDashboardCacheStore.InactiveRecordCleanupLimit, enumerated);
        Assert.Equal(JsonDashboardCacheStore.InactiveRecordCleanupLimit, cursor);

        enumerated = 0;
        using var cancellation = new CancellationTokenSource();
        IEnumerable<string> CancelingCandidates()
        {
            for (var index = 0; index < 10_000; index++)
            {
                enumerated++;
                if (enumerated == 4)
                {
                    cancellation.Cancel();
                }
                yield return Path.Combine(_directory, $"{index + 2}.json");
            }
        }

        Assert.Throws<OperationCanceledException>(() =>
        {
            _ = JsonDashboardCacheStore.SelectInactivePrunePage(
                CancelingCandidates(),
                0,
                cancellation.Token,
                out _);
        });
        Assert.Equal(4, enumerated);
    }

    [Fact]
    public void InactivePruneCancellationIsObservedAcrossALongNonOwnedPrefix()
    {
        var activeFilePath = Path.Combine(_directory, "1.json");
        var enumerated = 0;
        using var cancellation = new CancellationTokenSource();
        IEnumerable<string> RawPaths()
        {
            for (var index = 0; index < 10_000; index++)
            {
                enumerated++;
                if (enumerated == 100)
                {
                    cancellation.Cancel();
                }
                yield return Path.Combine(_directory, $"unrelated-{index}.txt");
            }
            yield return Path.Combine(_directory, "2.json");
        }

        var candidates = JsonDashboardCacheStore.EnumerateInactiveOwnedFiles(
            RawPaths(),
            activeFilePath,
            cancellation.Token);

        Assert.Throws<OperationCanceledException>(() =>
        {
            _ = JsonDashboardCacheStore.SelectInactivePrunePage(
                candidates,
                0,
                cancellation.Token,
                out _);
        });
        Assert.Equal(100, enumerated);
    }

    [Fact]
    public async Task MalformedIncompatibleAndInterruptedFilesAreExplicitMisses()
    {
        var store = new JsonDashboardCacheStore(_directory);
        var path = store.GetFilePath(Octocat);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "not-json");
        await File.WriteAllTextAsync(path + ".interrupted.tmp", "partial");

        var malformed = await store.ReadAsync(Octocat, Now);

        Assert.Null(malformed.Record);
        Assert.Equal(DashboardCacheDiagnosticKind.Malformed, malformed.Diagnostic.Kind);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!));

        await store.WriteAsync(Record(activity: ListSection("old-schema")));
        var json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace(
            "\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal));

        var incompatible = await store.ReadAsync(Octocat, Now);

        Assert.Null(incompatible.Record);
        Assert.Equal(DashboardCacheDiagnosticKind.Incompatible, incompatible.Diagnostic.Kind);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task OversizedCacheIsDisposedBeforeItIsRemovedAsMalformed()
    {
        var store = new JsonDashboardCacheStore(_directory);
        var path = store.GetFilePath(Octocat);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(16 * 1024 * 1024 + 1);
        }

        var result = await store.ReadAsync(Octocat, Now);

        Assert.Null(result.Record);
        Assert.Equal(DashboardCacheDiagnosticKind.Malformed, result.Diagnostic.Kind);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ModifiedCacheWithUntrustedPullRequestAvatarIsRemovedAsMalformed()
    {
        var store = new JsonDashboardCacheStore(_directory);
        var section = ListSection("pull") with
        {
            Items =
            [
                ListSection("pull").Items[0] with
                {
                    PullRequest = PullRequest(new Uri("https://avatars.githubusercontent.com/u/1?v=4"))
                }
            ]
        };
        await store.WriteAsync(Record(pullRequests: section));
        var path = store.GetFilePath(Octocat);
        var json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace(
            "avatars.githubusercontent.com", "untrusted.example", StringComparison.Ordinal));

        var result = await store.ReadAsync(Octocat, Now);

        Assert.Null(result.Record);
        Assert.Equal(DashboardCacheDiagnosticKind.Malformed, result.Diagnostic.Kind);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("author")]
    [InlineData("label")]
    [InlineData("check")]
    public async Task ModifiedCacheWithInvalidNestedPullRequestDataIsRemovedAsMalformed(
        string invalid)
    {
        var store = new JsonDashboardCacheStore(_directory);
        var section = ListSection("pull") with
        {
            Items =
            [
                ListSection("pull").Items[0] with
                {
                    PullRequest = PullRequest(new Uri("https://avatars.githubusercontent.com/u/1?v=4"))
                }
            ]
        };
        await store.WriteAsync(Record(pullRequests: section));
        var path = store.GetFilePath(Octocat);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var pull = root["pullRequests"]!["items"]![0]!["pullRequest"]!;
        switch (invalid)
        {
            case "author":
                pull["authorLogin"] = null;
                break;
            case "label":
                pull["labels"] = new JsonArray((JsonNode?)null);
                break;
            case "check":
                pull["checks"]!["items"] = new JsonArray((JsonNode?)null);
                break;
        }
        await File.WriteAllTextAsync(path, root.ToJsonString());

        var result = await store.ReadAsync(Octocat, Now);

        Assert.Null(result.Record);
        Assert.Equal(DashboardCacheDiagnosticKind.Malformed, result.Diagnostic.Kind);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("remaining")]
    [InlineData("kind")]
    [InlineData("availability")]
    public async Task ModifiedCacheWithInvalidCopilotQuotaIsRemovedAsMalformed(string invalid)
    {
        var store = new JsonDashboardCacheStore(_directory);
        await store.WriteAsync(Record(copilot: new(
            DashboardCacheVersions.Copilot,
            Now.AddHours(-1),
            new("enterprise",
                [new(CopilotQuotaKind.PremiumInteractions, CopilotQuotaAvailability.Limited, 50, false, null)]))));
        var path = store.GetFilePath(Octocat);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var quota = root["copilot"]!["usage"]!["quotas"]![0]!;
        switch (invalid)
        {
            case "remaining":
                quota["percentRemaining"] = null;
                break;
            case "kind":
                quota["kind"] = 999;
                break;
            case "availability":
                quota["availability"] = 999;
                break;
        }
        await File.WriteAllTextAsync(path, root.ToJsonString());

        var result = await store.ReadAsync(Octocat, Now);

        Assert.Null(result.Record);
        Assert.Equal(DashboardCacheDiagnosticKind.Malformed, result.Diagnostic.Kind);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task FailedAtomicReplacementPreservesThePreviousValidRecord()
    {
        var initialStore = new JsonDashboardCacheStore(_directory);
        await initialStore.WriteAsync(Record(activity: ListSection("previous")));
        var failingStore = new JsonDashboardCacheStore(_directory,
            (_, _) => throw new IOException("Injected replacement failure"));

        var result = await failingStore.WriteAsync(Record(activity: ListSection("replacement")));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(initialStore.GetFilePath(Octocat))!));
        var retained = await initialStore.ReadAsync(Octocat, Now);

        Assert.False(result.Succeeded);
        Assert.Equal(DashboardCacheDiagnosticKind.WriteFailed, result.Diagnostic.Kind);
        Assert.Equal("previous", Assert.Single(retained.Record!.Activity!.Items).Id);
    }

    [Fact]
    public async Task CancelledAtomicReplacementPreservesThePreviousValidRecord()
    {
        var initialStore = new JsonDashboardCacheStore(_directory);
        await initialStore.WriteAsync(Record(activity: ListSection("previous")));
        using var cancellation = new CancellationTokenSource();
        var cancelingStore = new JsonDashboardCacheStore(_directory, (_, _) => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancelingStore.WriteAsync(Record(activity: ListSection("replacement")), cancellation.Token));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(initialStore.GetFilePath(Octocat))!));
        var retained = await initialStore.ReadAsync(Octocat, Now);

        Assert.Equal("previous", Assert.Single(retained.Record!.Activity!.Items).Id);
    }

    [Fact]
    public async Task LockedRecordReportsReadFailureWithoutDeletingIt()
    {
        var store = new JsonDashboardCacheStore(_directory);
        await store.WriteAsync(Record(activity: ListSection("locked")));
        var path = store.GetFilePath(Octocat);
        await using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await store.ReadAsync(Octocat, Now);

        Assert.Null(result.Record);
        Assert.Equal(DashboardCacheDiagnosticKind.ReadFailed, result.Diagnostic.Kind);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ClearRemovesEveryAccountRecordAndOwnedTemporaryFileButPreservesUnrelatedFiles()
    {
        var store = new JsonDashboardCacheStore(_directory);
        var other = new DashboardCacheAccount("github.com", 2, "other");
        await store.WriteAsync(Record(activity: ListSection("octocat")));
        await store.WriteAsync(Record(other, activity: ListSection("other")));
        await store.SelectAccountAsync(other);
        var accountDirectory = Path.GetDirectoryName(store.GetFilePath(Octocat))!;
        var interrupted = Path.Combine(accountDirectory, "3.json.interrupted.tmp");
        var unrelated = Path.Combine(accountDirectory, "readme.txt");
        var settings = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(interrupted, "partial");
        await File.WriteAllTextAsync(unrelated, "keep");
        await File.WriteAllTextAsync(settings, """{"RefreshMinutes":10}""");

        var result = await store.ClearAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(4, result.DeletedFileCount);
        Assert.Equal(0, result.FailedFileCount);
        Assert.False(File.Exists(store.GetFilePath(Octocat)));
        Assert.False(File.Exists(store.GetFilePath(other)));
        Assert.False(File.Exists(interrupted));
        Assert.False(File.Exists(Path.Combine(_directory, "last-account.json")));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
        Assert.Equal("""{"RefreshMinutes":10}""", await File.ReadAllTextAsync(settings));
    }

    [Fact]
    public async Task PartialClearReportsRemainingFilesAndRepeatedClearCanFinish()
    {
        var initialStore = new JsonDashboardCacheStore(_directory);
        var other = new DashboardCacheAccount("github.com", 2, "other");
        await initialStore.WriteAsync(Record(activity: ListSection("octocat")));
        await initialStore.WriteAsync(Record(other, activity: ListSection("other")));
        var partialStore = new JsonDashboardCacheStore(
            _directory,
            null,
            path =>
            {
                if (path.EndsWith("2.json", StringComparison.Ordinal))
                {
                    throw new IOException("Injected deletion failure");
                }
            });

        var partial = await partialStore.ClearAsync();

        Assert.False(partial.Succeeded);
        Assert.Equal(DashboardCacheDiagnosticKind.ClearFailed, partial.Diagnostic.Kind);
        Assert.Equal(1, partial.DeletedFileCount);
        Assert.Equal(1, partial.FailedFileCount);
        Assert.True(File.Exists(initialStore.GetFilePath(other)));

        var retry = await initialStore.ClearAsync();
        Assert.True(retry.Succeeded);
        Assert.Equal(1, retry.DeletedFileCount);
        Assert.False(File.Exists(initialStore.GetFilePath(other)));
    }

    [Fact]
    public async Task ClearWaitsForAnOlderAtomicWriteThenDeletesItsResult()
    {
        var timeout = TimeSpan.FromSeconds(10);
        using var writeEntered = new ManualResetEventSlim();
        using var releaseWrite = new ManualResetEventSlim();
        var store = new JsonDashboardCacheStore(
            _directory,
            (_, _) =>
            {
                writeEntered.Set();
                if (!releaseWrite.Wait(timeout))
                {
                    throw new TimeoutException("The test did not release the write.");
                }
            });
        var write = Task.Run(() => store.WriteAsync(Record(activity: ListSection("pre-clear"))));
        Task<DashboardCacheClearResult>? clear = null;
        Exception? testFailure = null;
        try
        {
            Assert.True(writeEntered.Wait(timeout));
            clear = store.ClearAsync();
            Assert.False(clear.IsCompleted);
            releaseWrite.Set();
            var written = await write.WaitAsync(timeout);
            Assert.True(written.Succeeded);
            Assert.Equal(DashboardCacheDiagnosticKind.Written, written.Diagnostic.Kind);
            var result = await clear.WaitAsync(timeout);

            Assert.True(result.Succeeded);
            Assert.False(File.Exists(store.GetFilePath(Octocat)));
            Assert.Equal(DashboardCacheDiagnosticKind.Missing,
                (await store.ReadAsync(Octocat, Now).WaitAsync(timeout)).Diagnostic.Kind);
        }
        catch (Exception exception)
        {
            testFailure = exception;
            throw;
        }
        finally
        {
            releaseWrite.Set();
            try
            {
                Task[] operations = clear is null ? [write] : [write, clear];
                await Task.WhenAll(operations).WaitAsync(timeout);
            }
            catch (Exception cleanupFailure) when (testFailure is not null)
            {
                throw new AggregateException(
                    "The test and its cleanup both failed.", testFailure, cleanupFailure);
            }
        }
    }

    [Fact]
    public async Task ClearInvalidatesWriteThatStartedBeforeValidationCompleted()
    {
        using var validationEntered = new ManualResetEventSlim();
        using var releaseValidation = new ManualResetEventSlim();
        var store = new JsonDashboardCacheStore(_directory);
        var section = ListSection("pre-clear") with
        {
            Items = new BlockingList<DashboardItem>(
                ListSection("pre-clear").Items,
                validationEntered,
                releaseValidation)
        };

        var write = Task.Run(() => store.WriteAsync(Record(activity: section)));
        Assert.True(validationEntered.Wait(TimeSpan.FromSeconds(10)));

        var clear = await store.ClearAsync();
        releaseValidation.Set();
        var result = await write;

        Assert.True(clear.Succeeded);
        Assert.False(result.Succeeded);
        Assert.Equal(DashboardCacheDiagnosticKind.Invalidated, result.Diagnostic.Kind);
        Assert.False(File.Exists(store.GetFilePath(Octocat)));
    }

    private static DashboardCacheRecord Record(
        DashboardCacheAccount? account = null,
        DashboardListCacheSection? activity = null,
        DashboardListCacheSection? pullRequests = null,
        DashboardCopilotCacheSection? copilot = null) =>
        new(
            DashboardCacheVersions.Schema,
            account ?? Octocat,
            activity,
            pullRequests,
            null,
            null,
            null,
            copilot);

    private static DashboardListCacheSection ListSection(
        string id,
        int revision = DashboardCacheVersions.Activity) =>
        new(
            revision,
            Now.AddHours(-1),
            [
                new DashboardItem(
                    id,
                    id,
                    "octocat/tray",
                    "cached",
                    Now.AddHours(-2),
                    new Uri("https://github.com/octocat/tray"))
            ]);

    private static PullRequestDetails PullRequest(Uri avatar) =>
        new(
            42,
            "Improve tray",
            "octocat",
            avatar,
            false,
            "fix/tray",
            "main",
            "REVIEW_REQUIRED",
            2,
            [],
            0,
            new(null, CheckRollupState.NoChecks, [], 0));

    private sealed class BlockingList<T>(
        IReadOnlyList<T> items,
        ManualResetEventSlim entered,
        ManualResetEventSlim release) : IReadOnlyList<T>
    {
        public int Count
        {
            get
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The test did not release validation.");
                }
                return items.Count;
            }
        }

        public T this[int index] => items[index];
        public IEnumerator<T> GetEnumerator() => items.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
