using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace GitHubTray.Core;

public sealed class GitHubCliApi : IGitHubApi
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly Func<ProcessStartInfo> _createStartInfo;
    private readonly TimeSpan _requestTimeout;

    public GitHubCliApi() : this(() => new ProcessStartInfo("gh"), RequestTimeout)
    {
    }

    internal GitHubCliApi(Func<ProcessStartInfo> createStartInfo, TimeSpan requestTimeout)
    {
        _createStartInfo = createStartInfo;
        _requestTimeout = requestTimeout;
    }

    public async Task<string> GetAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.StartsWith('-') ||
            endpoint.StartsWith('/') || Uri.TryCreate(endpoint, UriKind.Absolute, out _))
        {
            throw new ArgumentException("A relative GitHub API endpoint is required.", nameof(endpoint));
        }

        var startInfo = _createStartInfo();
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        foreach (var argument in new[]
        {
            "api", "--hostname", "github.com", "--method", "GET",
            "--header", "Accept: application/vnd.github+json",
            "--header", "X-GitHub-Api-Version: 2022-11-28", endpoint
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GH_PROMPT_DISABLED"] = "1";
        startInfo.Environment["GH_PAGER"] = "";
        startInfo.Environment["NO_COLOR"] = "1";
        // Do not let inherited HTTP tracing expose authentication headers.
        startInfo.Environment.Remove("GH_DEBUG");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new GitHubException("GitHub CLI could not be started. Install gh and try again.");
            }
        }
        catch (Win32Exception exception)
        {
            throw new GitHubException("GitHub CLI is unavailable. Install gh, run 'gh auth login --hostname github.com' in a terminal, then restart GitHub Tray.", exception);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
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
                // The child can exit between HasExited and Kill.
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new GitHubException("GitHub did not respond within 30 seconds. Check your connection and refresh again.");
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            // CLI stderr may contain sensitive diagnostics. Only expose known, fixed messages.
            throw new GitHubException(DescribeFailure(error));
        }
        return output;
    }

    private static string DescribeFailure(string error)
    {
        if (error.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase))
        {
            return "GitHub's API rate limit was reached. Wait before refreshing; previously loaded data is retained.";
        }
        if (error.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("auth login", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Bad credentials", StringComparison.OrdinalIgnoreCase))
        {
            return "GitHub sign-in is unavailable or expired. Run 'gh auth login --hostname github.com' in a terminal, then refresh.";
        }
        if (error.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase))
        {
            return "GitHub denied access. Check your account, repository permissions, and organization SSO authorization in gh.";
        }
        return "The GitHub request failed. Check your connection and 'gh auth status --hostname github.com', then refresh.";
    }
}
