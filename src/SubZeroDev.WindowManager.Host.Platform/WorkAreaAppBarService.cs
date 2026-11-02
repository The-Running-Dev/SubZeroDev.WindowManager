using System.Globalization;
using System.Text;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>
/// The app-bar service for replacement mode, where no shell answers <c>SHAppBarMessage</c>: the host owns the
/// work area. Each monitor's reservations take strips from its bounds in registration order, and what is left
/// becomes the work area (<c>SPI_SETWORKAREA</c>). The monitor's original work area is journalled as a
/// <c>workArea</c> change before the first reservation is applied, and the entry ends once the last reservation
/// is gone and the original is back (I6). Full-screen detection is the coexistence service's.
/// </summary>
public sealed class WorkAreaAppBarService : IAppBarService, IDisposable
{
    private readonly IMonitorService _monitors;
    private readonly IRecoveryJournal _journal;
    private readonly Func<string, RectInt, bool> _apply;
    private readonly Func<string, Task> _checkpoint;
    private readonly AppBarService _fullscreen;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, MonitorState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<nint, string> _windows = [];

    /// <param name="apply">Sets a monitor's work area; <see cref="Applier"/> gives the real one, or the recording one under the test hook.</param>
    public WorkAreaAppBarService(IMonitorService monitors, IRecoveryJournal journal, Func<string, RectInt, bool> apply,
        Func<string, Task>? checkpoint = null, ShellEvents? events = null)
    {
        _monitors = monitors;
        _journal = journal;
        _apply = apply;
        _checkpoint = checkpoint ?? (_ => Task.CompletedTask);
        _fullscreen = new AppBarService(monitors, journal, checkpoint, events);
        _fullscreen.ShellRestarted += (_, _) => ShellRestarted?.Invoke(this, EventArgs.Empty);

        // Last resort: a host that exits without closing its panels still gives the work area back.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveAll();
    }

    public event EventHandler<FullscreenChangedEventArgs> FullscreenChanged
    {
        add => _fullscreen.FullscreenChanged += value;
        remove => _fullscreen.FullscreenChanged -= value;
    }

    public event EventHandler? ShellRestarted;

    /// <summary>
    /// Sets the work area with <c>SPI_SETWORKAREA</c>; under the test hook records
    /// <c>work-area &lt;monitor&gt; l,t,r,b</c> in <c>desktop-actions.log</c> instead.
    /// </summary>
    public static Func<string, RectInt, bool> Applier(string? hookDirectory) => (monitorId, area) =>
    {
        if (hookDirectory is not null)
        {
            Directory.CreateDirectory(hookDirectory);
            File.AppendAllText(Path.Combine(hookDirectory, Recovery.Reverter.DesktopActionsLog),
                string.Create(CultureInfo.InvariantCulture, $"work-area {monitorId} {area.Left},{area.Top},{area.Right},{area.Bottom}\n"),
                new UTF8Encoding(false));
            return true;
        }

        unsafe
        {
            var rect = new Rect { Left = area.Left, Top = area.Top, Right = area.Right, Bottom = area.Bottom };
            return Native.SystemParametersInfo(Native.SpiSetWorkArea, 0, &rect, Native.SpifSendChange);
        }
    };

    public void Dispose()
    {
        _fullscreen.Dispose();
        _gate.Dispose();
    }

    public Task<Outcome<RectInt>> RegisterAsync(nint hwnd, string monitorId, PanelEdge edge, int thicknessPx, CancellationToken ct) =>
        Task.Run(async () =>
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await ReserveAsync(hwnd, monitorId, edge, thicknessPx, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }, ct);

    public Task<Outcome<RectInt>> UpdateAsync(nint hwnd, string monitorId, PanelEdge edge, int thicknessPx, CancellationToken ct) =>
        RegisterAsync(hwnd, monitorId, edge, thicknessPx, ct);

    /// <summary>No shell forgot anything; the work area is set again in case something else changed it.</summary>
    public Task<Outcome<RectInt>> RenewAsync(nint hwnd, string monitorId, PanelEdge edge, int thicknessPx, CancellationToken ct) =>
        RegisterAsync(hwnd, monitorId, edge, thicknessPx, ct);

    public Task<Outcome> RemoveAsync(nint hwnd, CancellationToken ct) =>
        Task.Run(async () =>
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await ReleaseAsync(hwnd).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }, ct);

    public void RemoveNow(nint hwnd)
    {
        // The journal does not capture a synchronisation context, so waiting here cannot deadlock the UI thread.
        _gate.Wait();
        try
        {
            ReleaseAsync(hwnd).GetAwaiter().GetResult();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RemoveAll()
    {
        nint[] windows;
        try
        {
            _gate.Wait();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            windows = [.. _windows.Keys];
            foreach (var hwnd in windows)
            {
                ReleaseAsync(hwnd).GetAwaiter().GetResult();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Outcome<RectInt>> ReserveAsync(nint hwnd, string monitorId, PanelEdge edge, int thickness, CancellationToken ct)
    {
        if (_monitors.Monitors.FirstOrDefault(m => m.StableId == monitorId) is not { } monitor)
        {
            return Outcome<RectInt>.Fail(CoreErrorCode.NotFound, $"Monitor '{monitorId}' is not connected.");
        }

        if (_windows.TryGetValue(hwnd, out var previous) && previous != monitorId)
        {
            await ReleaseAsync(hwnd).ConfigureAwait(false);
        }

        if (!_states.TryGetValue(monitorId, out var state))
        {
            var original = monitor.WorkArea;
            var change = Recovery.DesktopChange.WorkArea(monitorId, original.Left, original.Top, original.Right, original.Bottom, DateTimeOffset.UtcNow);
            var begun = await _journal.BeginAsync(change, ct).ConfigureAwait(false);
            if (begun.Error is { } journalError)
            {
                return Outcome<RectInt>.Fail(journalError);
            }

            await _checkpoint("before-appbar").ConfigureAwait(false);
            state = new MonitorState(change.Id, original, monitor.Bounds);
            _states[monitorId] = state;
        }

        var index = state.Bars.FindIndex(bar => bar.Hwnd == hwnd);
        var reservation = new Reservation(hwnd, edge, Math.Max(0, thickness));
        if (index >= 0)
        {
            state.Bars[index] = reservation;
        }
        else
        {
            state.Bars.Add(reservation);
        }

        _windows[hwnd] = monitorId;
        var (strips, workArea) = Layout(state);
        if (!_apply(monitorId, workArea))
        {
            await ReleaseAsync(hwnd).ConfigureAwait(false);
            return Outcome<RectInt>.Fail(CoreErrorCode.AppBarRefused, "SPI_SETWORKAREA failed.", retryable: true);
        }

        return Outcome<RectInt>.Success(strips[hwnd]);
    }

    private async Task<Outcome> ReleaseAsync(nint hwnd)
    {
        if (!_windows.Remove(hwnd, out var monitorId) || !_states.TryGetValue(monitorId, out var state))
        {
            return Outcome.Fail(CoreErrorCode.NotFound, "The window holds no app bar.");
        }

        state.Bars.RemoveAll(bar => bar.Hwnd == hwnd);
        if (state.Bars.Count > 0)
        {
            _apply(monitorId, Layout(state).WorkArea);
            return Outcome.Success;
        }

        _states.Remove(monitorId);
        _apply(monitorId, state.Original);
        await _checkpoint("after-remove").ConfigureAwait(false);
        return await _journal.EndAsync(state.ChangeId, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Each reservation's strip, taken from what the earlier ones left, and the remainder.</summary>
    internal static (Dictionary<nint, RectInt> Strips, RectInt WorkArea) Layout(MonitorState state)
    {
        var strips = new Dictionary<nint, RectInt>();
        var (left, top, right, bottom) = (state.Bounds.Left, state.Bounds.Top, state.Bounds.Right, state.Bounds.Bottom);
        foreach (var bar in state.Bars)
        {
            switch (bar.Edge)
            {
                case PanelEdge.Top:
                    var topEdge = Math.Min(bottom, top + bar.Thickness);
                    strips[bar.Hwnd] = new RectInt(left, top, right, topEdge);
                    top = topEdge;
                    break;
                case PanelEdge.Bottom:
                    var bottomEdge = Math.Max(top, bottom - bar.Thickness);
                    strips[bar.Hwnd] = new RectInt(left, bottomEdge, right, bottom);
                    bottom = bottomEdge;
                    break;
                case PanelEdge.Left:
                    var leftEdge = Math.Min(right, left + bar.Thickness);
                    strips[bar.Hwnd] = new RectInt(left, top, leftEdge, bottom);
                    left = leftEdge;
                    break;
                default:
                    var rightEdge = Math.Max(left, right - bar.Thickness);
                    strips[bar.Hwnd] = new RectInt(rightEdge, top, right, bottom);
                    right = rightEdge;
                    break;
            }
        }

        return (strips, new RectInt(left, top, right, bottom));
    }

    internal sealed record Reservation(nint Hwnd, PanelEdge Edge, int Thickness);

    internal sealed record MonitorState(Guid ChangeId, RectInt Original, RectInt Bounds)
    {
        public List<Reservation> Bars { get; } = [];
    }
}
