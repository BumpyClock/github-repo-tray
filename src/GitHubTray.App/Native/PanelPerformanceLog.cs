using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;

namespace GitHubTray_App.Native;

internal sealed class PanelPerformanceLog : IDisposable
{
    private readonly string _path;
    private readonly DispatcherQueueTimer _renderTimeout;
    private long _showStarted;
    private int _showNumber;
    private bool _waitingForRender;
    private bool _isDisposed;
    private double? _preparationMilliseconds;
    private double? _synchronousShowMilliseconds;

    private PanelPerformanceLog(string path, DispatcherQueue dispatcher)
    {
        _path = path;
        _renderTimeout = dispatcher.CreateTimer();
        _renderTimeout.Interval = TimeSpan.FromSeconds(5);
        _renderTimeout.IsRepeating = false;
        _renderTimeout.Tick += RenderTimeout_Tick;
    }

    public static PanelPerformanceLog? TryCreate(DispatcherQueue dispatcher)
    {
#if PANEL_PERFORMANCE
        try
        {
            var path = Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                $"GitHubTray-performance-{Environment.ProcessId}.jsonl");
            using (File.Open(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
            }

            return new PanelPerformanceLog(path, dispatcher);
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
            return null;
        }
#else
        return null;
#endif
    }

    public bool BeginShow(long started, bool wasVisible)
    {
        if (_isDisposed || wasVisible)
        {
            return false;
        }

        CancelPendingRender();
        _showStarted = started;
        _showNumber++;
        _preparationMilliseconds = null;
        _synchronousShowMilliseconds = null;
        return true;
    }

    public void PreparationComplete()
    {
        if (!_isDisposed)
        {
            _preparationMilliseconds = Stopwatch.GetElapsedTime(_showStarted).TotalMilliseconds;
        }
    }

    public void Shown()
    {
        if (_isDisposed)
        {
            return;
        }

        _waitingForRender = true;
        CompositionTarget.Rendering += CompositionTarget_Rendering;
        _renderTimeout.Start();
    }

    public void SynchronousShowComplete()
    {
        if (!_isDisposed)
        {
            _synchronousShowMilliseconds = Stopwatch.GetElapsedTime(_showStarted).TotalMilliseconds;
        }
    }

    public void Hidden()
    {
        if (_isDisposed)
        {
            return;
        }

        var cancelledRender = _waitingForRender;
        CancelPendingRender();
        WriteSample("hidden", Stopwatch.GetTimestamp(), cancelledRender);
    }

    private void CompositionTarget_Rendering(object? sender, object args)
    {
        if (!_waitingForRender || _isDisposed)
        {
            return;
        }

        var timestamp = Stopwatch.GetTimestamp();
        CancelPendingRender();
        WriteSample("first-render-callback", timestamp);
    }

    private void RenderTimeout_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!_waitingForRender || _isDisposed)
        {
            return;
        }

        var timestamp = Stopwatch.GetTimestamp();
        CancelPendingRender();
        WriteSample("render-timeout", timestamp);
    }

    private void CancelPendingRender()
    {
        if (_waitingForRender)
        {
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            _waitingForRender = false;
        }

        _renderTimeout.Stop();
    }

    private void WriteSample(string eventName, long timestamp, bool cancelledRender = false)
    {
        try
        {
            // Capture before serialization/file I/O; never collect or trim the measured process.
            var managedBytes = GC.GetTotalMemory(forceFullCollection: false);
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
            using var process = Process.GetCurrentProcess();
            var privateBytes = process.PrivateMemorySize64;
            var workingSetBytes = process.WorkingSet64;
            using var stream = File.Open(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            writer.WriteString("event", eventName);
            writer.WriteString("utc", DateTimeOffset.UtcNow);
            writer.WriteNumber("process_id", Environment.ProcessId);
            writer.WriteNumber("show_number", _showNumber);
            writer.WriteString("show_kind", _showNumber == 1 ? "initial" : "reopen");
            writer.WriteString("latency_kind", "rendering-callback-proxy-not-compositor-present-or-input-latency");
            writer.WriteNumber("monotonic_ticks", timestamp);
            writer.WriteNumber("monotonic_frequency", Stopwatch.Frequency);
            WriteOptionalNumber(writer, "preparation_ms", _preparationMilliseconds);
            WriteOptionalNumber(writer, "synchronous_show_ms", _synchronousShowMilliseconds);
            WriteOptionalNumber(writer, "first_render_callback_ms",
                eventName == "first-render-callback"
                    ? Stopwatch.GetElapsedTime(_showStarted, timestamp).TotalMilliseconds
                    : null);
            writer.WriteBoolean("cancelled_pending_render", cancelledRender);
            writer.WriteNumber("managed_bytes", managedBytes);
            writer.WriteNumber("total_allocated_bytes", allocatedBytes);
            writer.WriteNumber("private_bytes", privateBytes);
            writer.WriteNumber("working_set_bytes", workingSetBytes);
            writer.WriteNumber("gen0_collections", GC.CollectionCount(0));
            writer.WriteNumber("gen1_collections", GC.CollectionCount(1));
            writer.WriteNumber("gen2_collections", GC.CollectionCount(2));
            writer.WriteEndObject();
            writer.Flush();
            stream.WriteByte((byte)'\n');
        }
        catch (Exception exception)
        {
            Dispose();
            ReportFailure(exception);
        }
    }

    private static void WriteOptionalNumber(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void ReportFailure(Exception exception)
    {
        var message = $"GitHub Tray performance logging disabled: {exception}";
        Trace.TraceError(message);
        try
        {
            Console.Error.WriteLine(message);
        }
        catch (IOException)
        {
            // A packaged GUI launch may not have a writable stderr; the debugger trace remains.
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CancelPendingRender();
        _renderTimeout.Tick -= RenderTimeout_Tick;
    }
}
