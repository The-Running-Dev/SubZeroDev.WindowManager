using System.Collections.Concurrent;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>A message the host's <c>Shell_TrayWnd</c> received that neither the tray nor an app bar owns.</summary>
public readonly record struct TaskbarMessage(uint Message, nuint WParam, nint LParam);

/// <summary>A decoded badge update for the window with the given handle.</summary>
public abstract record TaskbarUpdate(nint Window)
{
    public sealed record Progress(nint Window, WindowProgress Value) : TaskbarUpdate(Window);

    public sealed record Overlay(nint Window, WindowOverlay? Value) : TaskbarUpdate(Window);
}

/// <summary>
/// The decoders for what <c>ITaskbarList3</c> sends. The wire format of those calls is undocumented and has not
/// been captured from a real Explorer-less session (S29.3 is a manual check), so the default decoder claims no
/// message: nothing is guessed, and every message it declines is logged once.
/// </summary>
public static class TaskbarDecoder
{
    /// <summary>The decoder used until a format has been verified: it decodes nothing.</summary>
    public static TaskbarUpdate? Unverified(TaskbarMessage message) => null;
}

/// <summary>
/// Receives the badge messages sent to the host's tray window, decodes them and calls the sinks.
/// An undecodable message is ignored and logged once per message number, and shows no badge.
/// Under the test hook the tray window is the hook-scoped one, never the real <c>Shell_TrayWnd</c>, and
/// <see cref="Inject"/> stands in for the applications.
/// </summary>
public sealed class TaskbarBackchannel : ITaskbarBackchannel
{
    /// <summary>How many distinct undecoded message numbers are logged: other programs broadcast registered messages to every top-level window.</summary>
    private const int MostReported = 64;

    private readonly TrayHost _tray;
    private readonly Func<nint, WindowKey?> _resolve;
    private readonly Action<string> _log;
    private readonly Func<TaskbarMessage, TaskbarUpdate?> _decode;
    private readonly ConcurrentDictionary<uint, byte> _reported = new();
    private Action<WindowKey, WindowProgress>? _progress;
    private Action<WindowKey, WindowOverlay?>? _overlay;

    public TaskbarBackchannel(TrayHost tray, Func<nint, WindowKey?> resolve, Action<string> log,
        Func<TaskbarMessage, TaskbarUpdate?>? decode = null)
    {
        _tray = tray;
        _resolve = resolve;
        _log = log;
        _decode = decode ?? TaskbarDecoder.Unverified;
        ButtonCreatedName = tray.CreatedMessageName.Replace("TaskbarCreated", "TaskbarButtonCreated", StringComparison.Ordinal);
    }

    /// <summary>The registered message broadcast to ask applications to set their badges again.</summary>
    public string ButtonCreatedName { get; }

    /// <summary>Whether <see cref="BroadcastButtonCreated"/> has run.</summary>
    public bool Broadcast { get; private set; }

    public void Start(Action<WindowKey, WindowProgress> progress, Action<WindowKey, WindowOverlay?> overlay)
    {
        _progress = progress;
        _overlay = overlay;
        _tray.Unhandled = Receive;
    }

    public void BroadcastButtonCreated()
    {
        var message = Native.RegisterWindowMessage(ButtonCreatedName);
        TrayHost.Broadcast(message);
        Broadcast = true;
    }

    public void Stop()
    {
        _tray.Unhandled = null;
        _progress = null;
        _overlay = null;
    }

    public void Dispose() => Stop();

    /// <summary>Applies an update as if an application had sent it; the test hook's way in.</summary>
    public bool Inject(TaskbarUpdate update) => Apply(update);

    private bool Receive(TaskbarMessage message)
    {
        if (_progress is null)
        {
            return false;
        }

        TaskbarUpdate? update;
        try
        {
            update = _decode(message);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            update = null;
        }

        if (update is null)
        {
            if (_reported.Count < MostReported && _reported.TryAdd(message.Message, 0))
            {
                _log($"TaskbarMessageUndecoded: message 0x{message.Message:X} (wParam 0x{message.WParam:X}, lParam 0x{message.LParam:X}) is not a known badge format; it is ignored and shows no badge.");
            }

            return false;
        }

        return Apply(update);
    }

    private bool Apply(TaskbarUpdate update)
    {
        if (_resolve(update.Window) is not { } key)
        {
            return false;
        }

        switch (update)
        {
            case TaskbarUpdate.Progress progress:
                _progress?.Invoke(key, progress.Value);
                return true;
            case TaskbarUpdate.Overlay overlay:
                _overlay?.Invoke(key, overlay.Value);
                return true;
            default:
                return false;
        }
    }
}
