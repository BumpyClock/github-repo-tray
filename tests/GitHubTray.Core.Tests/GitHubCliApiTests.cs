using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using GitHubTray.Core;

namespace GitHubTray.Core.Tests;

public sealed class GitHubCliApiTests(GitHubProcessFixture fixture) : IClassFixture<GitHubProcessFixture>
{
    private const string SensitiveDiagnostic = "Authorization: Bearer fixture-only-secret; private/repository";
    private const string AuthenticationError = "GitHub sign-in is unavailable or expired. Run 'gh auth login --hostname github.com' in a terminal, then refresh.";
    private const string RateLimitError = "GitHub's API rate limit was reached. Wait before refreshing; previously loaded data is retained.";

    [Theory]
    [InlineData("HTTP 403")]
    [InlineData("HTTP 404")]
    public async Task CopilotPermissionFailuresDoNotStartAnotherAuthenticationFlow(string diagnostic)
    {
        var api = CreateOutputApi(SensitiveDiagnostic, $"{diagnostic}\n{SensitiveDiagnostic}", 1);
        var error = await Assert.ThrowsAsync<GitHubException>(() => api.GetAsync(CopilotUsageParser.Endpoint));
        Assert.Contains("Copilot usage is not accessible to the current gh account", error.Message);
        Assert.DoesNotContain(SensitiveDiagnostic, error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulRequestReturnsStdoutUnchangedAndIgnoresStderr(bool graphQl)
    {
        const string response = "{\"login\":\"octocat\",\"name\":\"Octocat 🐙\"}\n";
        var api = CreateOutputApi(response, SensitiveDiagnostic);

        Assert.Equal(response, await RequestAsync(api, graphQl));
    }

    [Fact]
    public async Task RequestUsesFixedReadOnlyArgumentsAndRemovesInheritedHttpTracing()
    {
        const string endpoint = "search/issues?q=label:\"help wanted\"&per_page=30";
        var inheritedDebug = Environment.GetEnvironmentVariable("GH_DEBUG");
        var api = new GitHubCliApi(() =>
        {
            var info = fixture.CreateStartInfo("inspect");
            info.Environment["GH_DEBUG"] = "api";
            info.Environment["GH_PROMPT_DISABLED"] = "0";
            info.Environment["GH_PAGER"] = "should-not-run";
            info.Environment["NO_COLOR"] = "0";
            return info;
        }, TimeSpan.FromSeconds(15));

        using var response = JsonDocument.Parse(await api.GetAsync(endpoint));
        var root = response.RootElement;
        Assert.Equal(
            ["api", "--hostname", "github.com", "--method", "GET",
                "--header", "Accept: application/vnd.github+json",
                "--header", "X-GitHub-Api-Version: 2022-11-28", endpoint],
            root.GetProperty("Arguments").EnumerateArray().Select(argument => argument.GetString()));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("Debug").ValueKind);
        Assert.Equal("1", root.GetProperty("PromptDisabled").GetString());
        Assert.True(string.IsNullOrEmpty(root.GetProperty("Pager").GetString()));
        Assert.Equal("1", root.GetProperty("NoColor").GetString());
        Assert.Equal(inheritedDebug, Environment.GetEnvironmentVariable("GH_DEBUG"));
    }

    [Fact]
    public async Task GraphQlUsesFixedPostArgumentsAndASingleRawQueryFieldWithoutTracing()
    {
        var api = new GitHubCliApi(() =>
        {
            var info = fixture.CreateStartInfo("inspect");
            info.Environment["GH_DEBUG"] = "api";
            return info;
        }, TimeSpan.FromSeconds(15));

        using var response = JsonDocument.Parse(await api.QueryAsync(ContributionCalendarParser.Query));
        var root = response.RootElement;
        Assert.Equal(
            ["api", "--hostname", "github.com", "--method", "POST",
                "--header", "Accept: application/vnd.github+json",
                "--header", "X-GitHub-Api-Version: 2022-11-28", "graphql",
                "--raw-field", $"query={ContributionCalendarParser.Query}"],
            root.GetProperty("Arguments").EnumerateArray().Select(argument => argument.GetString()));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("Debug").ValueKind);
        Assert.Equal("1", root.GetProperty("PromptDisabled").GetString());
        Assert.True(string.IsNullOrEmpty(root.GetProperty("Pager").GetString()));
        Assert.Equal("1", root.GetProperty("NoColor").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedPullRequestQueryIsPassedAsOneReadOnlyArgument(bool reviewRequested)
    {
        var query = PullRequestParser.Query("octocat", reviewRequested);
        var api = new GitHubCliApi(() => fixture.CreateStartInfo("inspect"), TimeSpan.FromSeconds(15));
        using var response = JsonDocument.Parse(await api.QueryAsync(query));
        Assert.Contains($"query={query}",
            response.RootElement.GetProperty("Arguments").EnumerateArray().Select(argument => argument.GetString()));
    }

    [Fact]
    public async Task PullRequestAllowanceDoesNotPermitModifiedOrAppendedOperations()
    {
        var query = PullRequestParser.Query("octocat", false);
        var api = new GitHubCliApi(() => throw new InvalidOperationException("Must not launch"), TimeSpan.FromSeconds(15));
        foreach (var invalid in new[]
        {
            query + "\nmutation { deleteIssue }",
            query.Replace("query PullRequests", "mutation PullRequests", StringComparison.Ordinal),
            query.Replace("first: 30", "first: 100", StringComparison.Ordinal),
            query.Replace("viewer { login }", "viewer { login } deleteIssue(input: {})", StringComparison.Ordinal)
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => api.QueryAsync(invalid));
        }
        Assert.Throws<ArgumentException>(() => PullRequestParser.Query("octocat\" } mutation {", false));
    }

    [Fact]
    public async Task ActivityBatchAllowanceIsBoundedAndRejectsModifiedOperations()
    {
        var query = ActivityPullRequestQuery.Query([new("octocat/tray", 42), new("org/other.repo", 9)]);
        var api = new GitHubCliApi(() => fixture.CreateStartInfo("inspect"), TimeSpan.FromSeconds(15));
        using var response = JsonDocument.Parse(await api.QueryAsync(query));
        Assert.Contains($"query={query}",
            response.RootElement.GetProperty("Arguments").EnumerateArray().Select(argument => argument.GetString()));
        var blockedApi = new GitHubCliApi(() => throw new InvalidOperationException("Must not launch"), TimeSpan.FromSeconds(15));
        foreach (var invalid in new[]
        {
            query + "\nmutation { deleteIssue }",
            query.Replace("query ActivityPullRequests", "mutation ActivityPullRequests", StringComparison.Ordinal),
            query.Replace("first: 100", "first: 200", StringComparison.Ordinal),
            query.Replace("pr1:", "pr0:", StringComparison.Ordinal)
        })
            await Assert.ThrowsAsync<ArgumentException>(() => blockedApi.QueryAsync(invalid));
        Assert.Throws<ArgumentException>(() => ActivityPullRequestQuery.Query([]));
        Assert.Throws<ArgumentException>(() => ActivityPullRequestQuery.Query(
            Enumerable.Range(1, 31).Select(number => new ActivityPullRequestReference("org/repo", number)).ToArray()));
        Assert.Throws<ArgumentException>(() => ActivityPullRequestQuery.Query([new("org/repo\") { deleteIssue }", 42)]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("mutation { deleteIssue }")]
    [InlineData("subscription { viewer { login } }")]
    [InlineData("query { viewer { login } } mutation { deleteIssue }")]
    [InlineData("query { viewer { login } } query { viewer { login } }")]
    [InlineData("query($id: ID!) { node(id: $id) { id } }")]
    [InlineData("query { viewer { contributionsCollection(from: \"2024-01-01\") { startedAt } } }")]
    [InlineData("# query\nmutation { deleteIssue }")]
    [InlineData("query { viewer { login }")]
    [InlineData("query { viewer { login } }}")]
    public async Task QueryBoundaryRejectsOtherOperationsBeforeLaunchingAProcess(string query)
    {
        var api = new GitHubCliApi(() => throw new InvalidOperationException("Must not launch"), TimeSpan.FromSeconds(15));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => api.QueryAsync(query));

        Assert.Equal("query", error.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingExecutableProducesActionableInstallationError(bool graphQl)
    {
        var missingExecutable = Path.Combine(fixture.DirectoryPath, "does-not-exist.exe");
        var api = new GitHubCliApi(() => new ProcessStartInfo(missingExecutable), TimeSpan.FromSeconds(15));

        var error = await Assert.ThrowsAsync<GitHubException>(() => RequestAsync(api, graphQl));

        Assert.Equal("GitHub CLI is unavailable. Install gh, run 'gh auth login --hostname github.com' in a terminal, then restart GitHub Tray.", error.Message);
        Assert.IsType<Win32Exception>(error.InnerException);
    }

    [Theory]
    [InlineData("HTTP 401", AuthenticationError)]
    [InlineData("run gh auth login", AuthenticationError)]
    [InlineData("AUTHENTICATION required", AuthenticationError)]
    [InlineData("Bad credentials", AuthenticationError)]
    [InlineData("API RATE LIMIT exceeded", RateLimitError)]
    [InlineData("HTTP 429", RateLimitError)]
    [InlineData("HTTP 403: rate limit exceeded", RateLimitError)]
    [InlineData("HTTP 403", "GitHub denied access. Check your account, repository permissions, and organization SSO authorization in gh.")]
    [InlineData("unexpected failure", "The GitHub request failed. Check your connection and 'gh auth status --hostname github.com', then refresh.")]
    public async Task FailedRequestExposesOnlyFixedErrorText(string diagnostic, string expected)
    {
        var api = CreateOutputApi(SensitiveDiagnostic, $"{diagnostic}\n{SensitiveDiagnostic}", 1);

        var error = await Assert.ThrowsAsync<GitHubException>(() => api.GetAsync("user"));

        Assert.Equal(expected, error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(SensitiveDiagnostic, error.ToString());
    }

    [Theory]
    [InlineData("HTTP 401", AuthenticationError)]
    [InlineData("HTTP 429", RateLimitError)]
    [InlineData("private graphql error", "The GitHub request failed. Check your connection and 'gh auth status --hostname github.com', then refresh.")]
    public async Task GraphQlProcessFailuresRetainTheSameRedactionBoundary(string diagnostic, string expected)
    {
        var api = CreateOutputApi(SensitiveDiagnostic, $"{diagnostic}\n{SensitiveDiagnostic}", 1);

        var error = await Assert.ThrowsAsync<GitHubException>(() => api.QueryAsync(ContributionCalendarParser.Query));

        Assert.Equal(expected, error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(SensitiveDiagnostic, error.ToString());
    }

    [Theory]
    [InlineData("large-stderr-first")]
    [InlineData("large-stdout-first")]
    public async Task BothOutputPipesAreDrainedWithoutDeadlock(string mode)
    {
        var api = new GitHubCliApi(() => fixture.CreateStartInfo(mode), TimeSpan.FromSeconds(15));

        Assert.Equal(new string('o', 1024 * 1024), await api.GetAsync("user"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task CancellationAndTimeoutKillOnlyTheLaunchedProcessTree(bool cancelExternally, bool graphQl)
    {
        var directory = Path.Combine(fixture.DirectoryPath, Guid.NewGuid().ToString("N"));
        var unrelatedDirectory = Path.Combine(directory, "unrelated");
        Directory.CreateDirectory(unrelatedDirectory);
        using var cancellation = new CancellationTokenSource();
        using var unrelated = Process.Start(fixture.CreateStartInfo("hold", unrelatedDirectory))!;
        Process? root = null;
        Process? child = null;
        Task<string>? request = null;
        try
        {
            await fixture.WaitUntilReadyAsync(unrelatedDirectory);
            var api = new GitHubCliApi(() => fixture.CreateStartInfo("tree", directory),
                TimeSpan.FromSeconds(cancelExternally ? 30 : 10));
            request = RequestAsync(api, graphQl, cancellation.Token);
            await fixture.WaitUntilReadyAsync(directory);
            root = fixture.OpenProcess(directory, "root");
            child = fixture.OpenProcess(directory, "child");
            Assert.False(root.HasExited);
            Assert.False(child.HasExited);
            Assert.False(unrelated.HasExited);

            if (cancelExternally)
            {
                cancellation.Cancel();
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => request.WaitAsync(TimeSpan.FromSeconds(15)));
                Assert.Equal(cancellation.Token, error.CancellationToken);
            }
            else
            {
                var error = await Assert.ThrowsAsync<GitHubException>(
                    () => request.WaitAsync(TimeSpan.FromSeconds(15)));
                Assert.Equal("GitHub did not respond within 30 seconds. Check your connection and refresh again.", error.Message);
                Assert.False(cancellation.IsCancellationRequested);
            }

            Assert.True(root.HasExited);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(child.HasExited);
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            cancellation.Cancel();
            await fixture.StopProcessesAsync(directory);
            await GitHubProcessFixture.StopAsync(unrelated);
            if (request is not null)
            {
                try
                {
                    await request.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception error) when (error is OperationCanceledException or GitHubException)
                {
                }
            }
            root?.Dispose();
            child?.Dispose();
        }
    }

    private GitHubCliApi CreateOutputApi(string stdout, string stderr, int exitCode = 0) => new(() =>
    {
        var info = fixture.CreateStartInfo("output");
        info.Environment["GITHUB_TRAY_FIXTURE_STDOUT"] = stdout;
        info.Environment["GITHUB_TRAY_FIXTURE_STDERR"] = stderr;
        info.Environment["GITHUB_TRAY_FIXTURE_EXIT_CODE"] = exitCode.ToString();
        return info;
    }, TimeSpan.FromSeconds(15));

    private static Task<string> RequestAsync(GitHubCliApi api, bool graphQl, CancellationToken cancellationToken = default) =>
        graphQl ? api.QueryAsync(ContributionCalendarParser.Query, cancellationToken) : api.GetAsync("user", cancellationToken);
}

public sealed class GitHubProcessFixture : IAsyncLifetime
{
    public string DirectoryPath { get; private set; } = "";
    private string ExecutablePath => Path.Combine(DirectoryPath, "bin",
        OperatingSystem.IsWindows() ? "GitHubTray.ProcessFixture.exe" : "GitHubTray.ProcessFixture");

    public async Task InitializeAsync()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null &&
               !File.Exists(Path.Combine(repository.FullName, "tests", "GitHubTray.ProcessFixture", "GitHubTray.ProcessFixture.csproj")))
        {
            repository = repository.Parent;
        }
        Assert.NotNull(repository);
        DirectoryPath = Path.Combine(repository.FullName, "artifacts", "cli-process-tests", Guid.NewGuid().ToString("N"));
        var offlinePackages = Path.Combine(DirectoryPath, "packages");
        Directory.CreateDirectory(offlinePackages);
        var buildInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Keep fixture setup self-contained without a test-project reference or any package feed.
        foreach (var argument in new[]
        {
            "build", Path.Combine(repository.FullName, "tests", "GitHubTray.ProcessFixture", "GitHubTray.ProcessFixture.csproj"),
            "--nologo", "--verbosity", "quiet", "--disable-build-servers",
            "--output", Path.Combine(DirectoryPath, "bin"), $"-p:RestoreSources={offlinePackages}"
        })
        {
            buildInfo.ArgumentList.Add(argument);
        }
        using var build = Process.Start(buildInfo)!;
        var stdout = build.StandardOutput.ReadToEndAsync();
        var stderr = build.StandardError.ReadToEndAsync();
        try
        {
            await build.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            Assert.True(build.ExitCode == 0, $"Fixture build failed:\n{await stdout}\n{await stderr}");
            Assert.True(File.Exists(ExecutablePath));
        }
        finally
        {
            await StopAsync(build);
        }
    }

    public ProcessStartInfo CreateStartInfo(string mode, string? directory = null)
    {
        var info = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.Environment["GITHUB_TRAY_FIXTURE_MODE"] = mode;
        info.Environment["GITHUB_TRAY_FIXTURE_ROLE"] = "root";
        if (directory is not null)
        {
            info.Environment["GITHUB_TRAY_FIXTURE_DIRECTORY"] = directory;
        }
        return info;
    }

    public async Task WaitUntilReadyAsync(string directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(Path.Combine(directory, "root.ready")))
        {
            await Task.Delay(20, deadline.Token);
        }
    }

    public Process OpenProcess(string directory, string role)
    {
        var process = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(directory, $"{role}.pid"))));
        _ = process.Handle;
        Assert.Equal(ExecutablePath, process.MainModule!.FileName);
        return process;
    }

    public async Task StopProcessesAsync(string directory)
    {
        foreach (var pidFile in Directory.EnumerateFiles(directory, "*.pid"))
        {
            if (!int.TryParse(await File.ReadAllTextAsync(pidFile), out var pid))
            {
                continue;
            }
            try
            {
                using var process = Process.GetProcessById(pid);
                _ = process.Handle;
                if (!process.HasExited &&
                    string.Equals(process.MainModule?.FileName, ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    await StopAsync(process);
                }
            }
            catch (ArgumentException)
            {
                // The fixture has already exited and its PID is no longer registered.
            }
            catch (InvalidOperationException)
            {
                // The fixture exited while its identity was being inspected.
            }
        }
    }

    public static async Task StopAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
        return Task.CompletedTask;
    }
}
