using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>Opt-in evidence only: never decodes, acknowledges or changes a native message.</summary>
internal sealed class TaskbarTrace : IDisposable
{
    internal const string Variable = "SUBZERODEV_WM_TASKBAR_TRACE";
    internal const int MaximumEvents = 2048;
    internal const int MaximumPayload = 256;
    private readonly Channel<object> _events = Channel.CreateBounded<object>(MaximumEvents + 4);
    private readonly Task _writer;
    private int _seen;
    private int _disposed;

    internal TaskbarTrace(TextWriter output, string windowClass, Action<string> log)
    {
        _writer = Task.Run(async () =>
        {
            try
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    type = "start", utc = DateTimeOffset.UtcNow, pid = Environment.ProcessId,
                    os = Environment.OSVersion.VersionString, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    windowClass, source = "wndproc-observation", hookScoped = windowClass != "Shell_TrayWnd",
                    maximumEvents = MaximumEvents, maximumPayload = MaximumPayload,
                    stopwatchFrequency = Stopwatch.Frequency,
                }));
                await foreach (var item in _events.Reader.ReadAllAsync())
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(item));
                    await output.FlushAsync();
                }

                await output.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    type = "end", utc = DateTimeOffset.UtcNow, eligible = _seen,
                    captured = Math.Min(_seen, MaximumEvents), dropped = Math.Max(0, _seen - MaximumEvents),
                }));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                log($"TaskbarTraceFailed: {e.Message}");
            }
            finally
            {
                try
                {
                    output.Dispose();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    log($"TaskbarTraceFailed: {e.Message}");
                }
            }
        });
    }

    internal static TaskbarTrace? Open(string windowClass, Action<string> log)
    {
        var path = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException("The trace path must be absolute.");
            }

            // Refuse overwrite. Open outside the window procedure; all subsequent IO is on the writer task.
            var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            log($"TaskbarTraceStarted: {path}; raw observations only, capped at {MaximumEvents} events.");
            return new TaskbarTrace(output, windowClass, log);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log($"TaskbarTraceFailed: {e.Message}");
            return null;
        }
    }

    internal static bool Eligible(uint message) => message == 0x004A || message >= 0x0400;

    internal void WindowCreated(nint window, string windowClass) => _events.Writer.TryWrite(new
    {
        type = "window", utc = DateTimeOffset.UtcNow, receiver = $"0x{window:X}", windowClass,
    });

    // Called on the tray thread. A repeated message is a separate sample, unlike the ordinary warning log.
    internal bool Reserve(uint message) => Volatile.Read(ref _disposed) == 0 && Eligible(message) &&
        Interlocked.Increment(ref _seen) <= MaximumEvents;

    internal void Record(nint window, uint message, nuint wParam, nint lParam,
        nuint? kind = null, uint? size = null, ReadOnlySpan<byte> payload = default, string? payloadError = null)
    {
        _events.Writer.TryWrite(new
        {
            type = "message", utc = DateTimeOffset.UtcNow, ticks = Stopwatch.GetTimestamp(),
            receiver = $"0x{window:X}", message = $"0x{message:X}", wParam = $"0x{wParam:X}", lParam = $"0x{lParam:X}",
            copyDataKind = kind is { } k ? $"0x{k:X}" : null, copyDataSize = size,
            payloadHex = Convert.ToHexString(payload[..Math.Min(payload.Length, MaximumPayload)]),
            payloadTruncated = size > MaximumPayload, payloadError,
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _events.Writer.TryComplete();
        // Never wait for disk IO from a window procedure or the tray thread.
        _writer.Wait(TimeSpan.FromSeconds(2));
    }
}
