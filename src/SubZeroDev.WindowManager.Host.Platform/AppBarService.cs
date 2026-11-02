using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>
/// Shell app-bar registration. Each call runs <c>SHAppBarMessage</c> on a background task (I9) and
/// returns the rectangle the shell granted; the caller moves its window there. Every registration is
/// journalled before <c>ABM_NEW</c> and the entry is ended only after <c>ABM_REMOVE</c> (I6). A monitor with
/// no handle (a simulated one) is refused, as the shell would refuse a bar on a monitor it does not know.
/// <see cref="ShellRestarted"/> follows <see cref="ShellEvents.TaskbarCreated"/>; <see cref="FullscreenChanged"/>
/// comes from polling the foreground window every 250 ms once anyone listens.
/// </summary>
public sealed class AppBarService : IAppBarService, IDisposable
{
    private static readonly TimeSpan FullscreenPoll = TimeSpan.FromMilliseconds(250);

    private static readonly uint CallbackMessage = Native.RegisterWindowMessage("SubZeroDev.WindowManager.AppBar");

    private readonly IMonitorService _monitors;
    private readonly IRecoveryJournal _journal;
    private readonly Func<string, Task> _checkpoint;
    private readonly Dictionary<nint, Guid> _registered = [];
    private readonly Lock _gate = new();
    private readonly HashSet<string> _fullscreen = new(StringComparer.Ordinal);
    private readonly int _processId = Environment.ProcessId;
    private Timer? _poll;
    private EventHandler<FullscreenChangedEventArgs>? _fullscreenChanged;

    /// <param name="checkpoint">Awaited at named points ("before-appbar", "after-remove") so tests can observe the journal from outside; a no-op normally.</param>
    public AppBarService(IMonitorService monitors, IRecoveryJournal journal, Func<string, Task>? checkpoint = null, ShellEvents? events = null)
    {
        _monitors = monitors;
        _journal = journal;
        _checkpoint = checkpoint ?? (_ => Task.CompletedTask);
        if (events is not null)
        {
            events.TaskbarCreated += (_, _) => ShellRestarted?.Invoke(this, EventArgs.Empty);
        }

        // Last resort: a host that exits without closing its panels still gives the space back.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveAll();
    }

    public event EventHandler<FullscreenChangedEventArgs> FullscreenChanged
    {
        add
        {
            lock (_gate)
            {
                _fullscreenChanged += value;
                _poll ??= new Timer(_ => PollFullscreen(), null, FullscreenPoll, FullscreenPoll);
            }
        }
        remove
        {
            lock (_gate)
            {
                _fullscreenChanged -= value;
            }
        }
    }

    public event EventHandler? ShellRestarted;

    public void Dispose() => _poll?.Dispose();

    public Task<Outcome<RectInt>> RenewAsync(nint hwnd, string monitorId, PanelEdge edge, int thicknessPx, CancellationToken ct) =>
        Task.Run(() =>
        {
            var monitor = Find(monitorId);
            if (monitor is null)
            {
                return Outcome<RectInt>.Fail(CoreErrorCode.NotFound, $"Monitor '{monitorId}' is not connected.");
            }

            // The restarted shell forgot every bar: ABM_NEW again (a shell that still knows the bar just says no)
            // and claim the space anew. The journal entry from the first registration still covers the window.
            var data = NewData(hwnd);
            data.CallbackMessage = CallbackMessage;
            Native.SHAppBarMessage(Native.AbmNew, ref data);
            return Outcome<RectInt>.Success(SetPosition(hwnd, monitor, edge, thicknessPx));
        }, ct);

    public Task<Outcome<RectInt>> RegisterAsync(nint hwnd, string monitorId, PanelEdge edge, int thicknessPx, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var monitor = Find(monitorId);
            if (monitor is null)
            {
                return Outcome<RectInt>.Fail(CoreErrorCode.NotFound, $"Monitor '{monitorId}' is not connected.");
            }

            if (monitor.Handle == 0)
            {
                return Outcome<RectInt>.Fail(CoreErrorCode.AppBarRefused, $"Monitor '{monitorId}' is not known to the shell.");
            }

            var change = Recovery.DesktopChange.AppBar(monitorId, hwnd, edge.ToString().ToLowerInvariant(), thicknessPx, DateTimeOffset.UtcNow);
            var begun = await _journal.BeginAsync(change, ct).ConfigureAwait(false);
            if (begun.Error is { } journalError)
            {
                return Outcome<RectInt>.Fail(journalError);
            }

            await _checkpoint("before-appbar").ConfigureAwait(false);
            var data = NewData(hwnd);
            data.CallbackMessage = CallbackMessage;
            if (Native.SHAppBarMessage(Native.AbmNew, ref data) == 0)
            {
                await _journal.EndAsync(change.Id, CancellationToken.None).ConfigureAwait(false);
                return Outcome<RectInt>.Fail(CoreErrorCode.AppBarRefused, "The shell refused ABM_NEW.", retryable: true);
            }

            lock (_gate)
            {
                _registered[hwnd] = change.Id;
            }

            return Outcome<RectInt>.Success(SetPosition(hwnd, monitor, edge, thicknessPx));
        }, ct);

    public Task<Outcome<RectInt>> UpdateAsync(nint hwnd, string monitorId, PanelEdge edge, int thicknessPx, CancellationToken ct) =>
        Task.Run(() =>
        {
            var monitor = Find(monitorId);
            return monitor is null
                ? Outcome<RectInt>.Fail(CoreErrorCode.NotFound, $"Monitor '{monitorId}' is not connected.")
                : Outcome<RectInt>.Success(SetPosition(hwnd, monitor, edge, thicknessPx));
        }, ct);

    public Task<Outcome> RemoveAsync(nint hwnd, CancellationToken ct) =>
        Task.Run(async () =>
        {
            Guid changeId;
            lock (_gate)
            {
                if (!_registered.Remove(hwnd, out changeId))
                {
                    return Outcome.Fail(CoreErrorCode.NotFound, "The window holds no app bar.");
                }
            }

            var data = NewData(hwnd);
            Native.SHAppBarMessage(Native.AbmRemove, ref data);
            await _checkpoint("after-remove").ConfigureAwait(false);
            return await _journal.EndAsync(changeId, CancellationToken.None).ConfigureAwait(false);
        }, ct);

    public void RemoveNow(nint hwnd)
    {
        Guid changeId;
        lock (_gate)
        {
            if (!_registered.Remove(hwnd, out changeId))
            {
                return;
            }
        }

        var data = NewData(hwnd);
        Native.SHAppBarMessage(Native.AbmRemove, ref data);
        // The journal does not capture a synchronisation context, so waiting here cannot deadlock the UI thread.
        _journal.EndAsync(changeId, CancellationToken.None).GetAwaiter().GetResult();
    }

    private void RemoveAll()
    {
        nint[] windows;
        lock (_gate)
        {
            windows = [.. _registered.Keys];
        }

        foreach (var hwnd in windows)
        {
            RemoveNow(hwnd);
        }
    }

    /// <summary>Asks the shell for space on <paramref name="edge"/>, trims the answer to the thickness, and claims it.</summary>
    private static RectInt SetPosition(nint hwnd, MonitorInfo monitor, PanelEdge edge, int thickness)
    {
        var data = NewData(hwnd);
        data.Edge = edge switch { PanelEdge.Left => 0u, PanelEdge.Top => 1u, PanelEdge.Right => 2u, _ => 3u };
        data.Bounds = new Rect { Left = monitor.Bounds.Left, Top = monitor.Bounds.Top, Right = monitor.Bounds.Right, Bottom = monitor.Bounds.Bottom };
        Native.SHAppBarMessage(Native.AbmQueryPos, ref data);

        // QUERYPOS moves the inner edge clear of other bars; set the thickness from that edge.
        switch (edge)
        {
            case PanelEdge.Top:
                data.Bounds.Bottom = data.Bounds.Top + thickness;
                break;
            case PanelEdge.Bottom:
                data.Bounds.Top = data.Bounds.Bottom - thickness;
                break;
            case PanelEdge.Left:
                data.Bounds.Right = data.Bounds.Left + thickness;
                break;
            case PanelEdge.Right:
                data.Bounds.Left = data.Bounds.Right - thickness;
                break;
        }

        Native.SHAppBarMessage(Native.AbmSetPos, ref data);
        return MonitorService.ToRectInt(data.Bounds);
    }

    /// <summary>
    /// A monitor is full-screen while the foreground window belongs to another process, has no caption, is
    /// not the desktop or a taskbar, and covers the whole monitor.
    /// </summary>
    private void PollFullscreen()
    {
        var window = Native.GetForegroundWindow();
        var covered = window == 0 ? null : Covered(window);
        var changes = new List<FullscreenChangedEventArgs>();
        lock (_gate)
        {
            foreach (var monitor in _fullscreen.Where(id => id != covered).ToList())
            {
                _fullscreen.Remove(monitor);
                changes.Add(new FullscreenChangedEventArgs(monitor, false));
            }

            if (covered is not null && _fullscreen.Add(covered))
            {
                changes.Add(new FullscreenChangedEventArgs(covered, true));
            }
        }

        foreach (var change in changes)
        {
            _fullscreenChanged?.Invoke(this, change);
        }
    }

    private unsafe string? Covered(nint window)
    {
        Native.GetWindowThreadProcessId(window, out var processId);
        if (processId == _processId || !Native.IsWindowVisible(window) || Native.IsIconic(window)
            || ((uint)Native.GetWindowLongPtr(window, Native.GwlStyle) & Native.WsCaption) == Native.WsCaption)
        {
            return null;
        }

        var name = stackalloc char[64];
        var length = Native.GetClassName(window, name, 64);
        var className = new string(name, 0, Math.Max(length, 0));
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" || !Native.GetWindowRect(window, out var rect))
        {
            return null;
        }

        return _monitors.Monitors.FirstOrDefault(m => rect.Left <= m.Bounds.Left && rect.Top <= m.Bounds.Top
            && rect.Right >= m.Bounds.Right && rect.Bottom >= m.Bounds.Bottom)?.StableId;
    }

    private MonitorInfo? Find(string monitorId) => _monitors.Monitors.FirstOrDefault(m => m.StableId == monitorId);

    private static unsafe AppBarData NewData(nint hwnd) => new() { Size = (uint)sizeof(AppBarData), Window = hwnd };
}
