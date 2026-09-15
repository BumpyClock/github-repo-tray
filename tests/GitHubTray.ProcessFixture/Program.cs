using System.Diagnostics;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
var mode = Environment.GetEnvironmentVariable("GITHUB_TRAY_FIXTURE_MODE");
switch (mode)
{
    case "output":
        Console.Out.Write(Environment.GetEnvironmentVariable("GITHUB_TRAY_FIXTURE_STDOUT"));
        Console.Error.Write(Environment.GetEnvironmentVariable("GITHUB_TRAY_FIXTURE_STDERR"));
        return int.Parse(Environment.GetEnvironmentVariable("GITHUB_TRAY_FIXTURE_EXIT_CODE") ?? "0");

    case "inspect":
        Console.Write(JsonSerializer.Serialize(new
        {
            Arguments = args,
            Debug = Environment.GetEnvironmentVariable("GH_DEBUG"),
            PromptDisabled = Environment.GetEnvironmentVariable("GH_PROMPT_DISABLED"),
            Pager = Environment.GetEnvironmentVariable("GH_PAGER"),
            NoColor = Environment.GetEnvironmentVariable("NO_COLOR")
        }));
        return 0;

    case "large-stderr-first":
        Console.Error.Write(new string('e', 1024 * 1024));
        Console.Out.Write(new string('o', 1024 * 1024));
        return 0;

    case "large-stdout-first":
        Console.Out.Write(new string('o', 1024 * 1024));
        Console.Error.Write(new string('e', 1024 * 1024));
        return 0;

    case "chunked-stderr":
        Console.Error.Write(Environment.GetEnvironmentVariable("GITHUB_TRAY_FIXTURE_STDERR_FIRST"));
        Console.Error.Flush();
        await Task.Delay(100);
        Console.Error.Write(Environment.GetEnvironmentVariable("GITHUB_TRAY_FIXTURE_STDERR_SECOND"));
        Console.Error.Flush();
        Console.Out.Write(Environment.GetEnvironmentVariable("GITHUB_TRAY_FIXTURE_STDOUT"));
        return int.Parse(Environment.GetEnvironmentVariable("GITHUB_TRAY_FIXTURE_EXIT_CODE") ?? "0");

    case "tree":
    case "hold":
        var directory = Environment.GetEnvironmentVariable("GITHUB_TRAY_FIXTURE_DIRECTORY")!;
        var role = Environment.GetEnvironmentVariable("GITHUB_TRAY_FIXTURE_ROLE") ?? "root";
        File.WriteAllText(Path.Combine(directory, $"{role}.pid"), Environment.ProcessId.ToString());

        if (mode == "tree")
        {
            var childInfo = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            childInfo.Environment["GITHUB_TRAY_FIXTURE_MODE"] = "hold";
            childInfo.Environment["GITHUB_TRAY_FIXTURE_ROLE"] = "child";
            using var child = Process.Start(childInfo)!;
            var ready = Path.Combine(directory, "child.ready");
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(ready))
            {
                await Task.Delay(20, startup.Token);
            }
        }

        File.WriteAllText(Path.Combine(directory, $"{role}.ready"), "ready");
        // Bound fixture lifetime even if a failed test cannot perform its cleanup.
        await Task.Delay(TimeSpan.FromSeconds(45));
        return 0;

    default:
        throw new InvalidOperationException($"Unknown fixture mode: {mode}");
}
