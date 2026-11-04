using System.Globalization;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>A window's lifetime identity: the handle plus the generation the model assigned when it admitted it (I26).</summary>
public readonly record struct WindowKey(nint Handle, long Generation);

/// <summary>What must still hold for a handle to be the same window: process, process start, thread and class.</summary>
public readonly record struct WindowFingerprint(int ProcessId, long ProcessStartTicks, int ThreadId, string ClassName);

/// <summary>
/// The public form of a lifetime key, <c>wk1:&lt;epoch&gt;:&lt;generation&gt;</c>. The epoch is random per host start, so a
/// reference kept across a restart is recognisably stale rather than silently pointing at another window.
/// </summary>
public readonly record struct WindowReference(long Epoch, long Generation)
{
    private const string Prefix = "wk1:";

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Prefix}{Epoch}:{Generation}");

    public static bool TryParse(string? text, out WindowReference reference)
    {
        reference = default;
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = text[Prefix.Length..].Split(':');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var generation))
        {
            return false;
        }

        reference = new WindowReference(epoch, generation);
        return true;
    }
}

public enum CloakState { None, App, Shell, Inherited }

public enum ProgressState { None, Indeterminate, Normal, Error, Paused }

public sealed record WindowProgress(ProgressState State, double? Value)
{
    public static WindowProgress None { get; } = new(ProgressState.None, null);
}

public sealed record WindowOverlay(IconRef Icon, string? Description);

/// <summary>
/// One tracked window as the shell knows it. These are facts only: whether a surface lists the window is that
/// surface's rule over these fields (I40), so hidden, cloaked and minimised windows stay in the model.
/// </summary>
/// <param name="Key">The lifetime key.</param>
/// <param name="Fingerprint">What was true of the handle at admission and at the last check.</param>
/// <param name="Title">The window title.</param>
/// <param name="ExePath">The owning executable, when readable.</param>
/// <param name="Aumid">The application user model id, when the window or its process declares one.</param>
/// <param name="GroupKey">The AUMID if there is one, else the normalised exe path.</param>
/// <param name="Owner">The owner window, when it is tracked.</param>
/// <param name="MonitorId">The monitor the window is mostly on.</param>
/// <param name="DesktopId">The virtual desktop the window is filed on.</param>
/// <param name="PinnedToAll">Whether the window shows on every desktop.</param>
/// <param name="Visible">Whether the window has <c>WS_VISIBLE</c>.</param>
/// <param name="Cloak">Why DWM hides the window, if it does.</param>
/// <param name="ShellHidden">Whether the shell itself hid the window.</param>
/// <param name="ToolWindow">Whether it has <c>WS_EX_TOOLWINDOW</c>.</param>
/// <param name="NoActivate">Whether it has <c>WS_EX_NOACTIVATE</c>.</param>
/// <param name="AppWindow">Whether it has <c>WS_EX_APPWINDOW</c>.</param>
/// <param name="Titled">Whether the title is non-empty.</param>
/// <param name="FirstSeen">The order of admission; for ordering only.</param>
/// <param name="ActivatedAt">The order of last activation; for ordering only.</param>
/// <param name="Active">Whether this is the window the user is working in.</param>
/// <param name="Minimised">Whether it is minimised.</param>
/// <param name="Attention">Whether it is flashing for attention.</param>
/// <param name="Progress">Taskbar progress.</param>
/// <param name="Overlay">Taskbar overlay badge.</param>
public sealed record ManagedWindow(
    WindowKey Key,
    WindowFingerprint Fingerprint,
    string Title,
    string? ExePath,
    string? Aumid,
    string GroupKey,
    WindowKey? Owner,
    string MonitorId,
    Guid DesktopId,
    bool PinnedToAll,
    bool Visible,
    CloakState Cloak,
    bool ShellHidden,
    bool ToolWindow,
    bool NoActivate,
    bool AppWindow,
    bool Titled,
    long FirstSeen,
    long ActivatedAt,
    bool Active,
    bool Minimised,
    bool Attention,
    WindowProgress Progress,
    WindowOverlay? Overlay);

/// <summary>Every tracked window and the desktops at one revision; null desktops means the single default one.</summary>
public sealed record WindowSnapshot(long Revision, IReadOnlyList<ManagedWindow> Windows, DesktopsView? Desktops = null)
{
    public DesktopsView DesktopsOrDefault => Desktops ?? DesktopsView.Single;
}

/// <summary>What a command resolved a window reference to: the handle to act on, and the model's entry when it has one.</summary>
public sealed record WindowTarget(nint Handle, ManagedWindow? Managed);

public enum ObservationKind
{
    Created,
    Shown,
    Hidden,
    Cloaked,
    Destroyed,
    TitleChanged,
    Foreground,
    Minimised,
    Restored,

    /// <summary>The periodic scan did not find a window the tracker knew; the model checks it still exists.</summary>
    RescanMissing,

    /// <summary>The periodic scan finished; the model re-verifies every tracked window (handle <c>0</c>).</summary>
    Rescan,

    /// <summary>The shell hook reported a flash.</summary>
    Flashed,

    /// <summary>The shell hook reported a redraw, which counts as a flash only for a background window whose title is unchanged.</summary>
    Redrawn,
}

public readonly record struct WindowObservation(nint Handle, ObservationKind Kind);

/// <summary>Everything the model reads of one window in a single pass; null from <see cref="IWindowFacts.Read"/> when it is not a window.</summary>
public sealed record WindowFacts(
    WindowFingerprint Fingerprint,
    string Title,
    string? ExePath,
    string? Aumid,
    nint Owner,
    string MonitorId,
    bool Visible,
    CloakState Cloak,
    bool ToolWindow,
    bool NoActivate,
    bool AppWindow,
    bool Minimised,
    bool OwnProcess,
    bool IsShellWindow);

/// <summary>Reads a window's current facts (read-only; never changes a window).</summary>
public interface IWindowFacts
{
    WindowFacts? Read(nint handle);
}

/// <summary>Finds candidate windows and reports what changes, for the model to judge (I39: the model is the one registry).</summary>
public interface IWindowTracker
{
    /// <summary>The handles of every top-level window now, topmost first.</summary>
    Task<IReadOnlyList<nint>> EnumerateAsync(CancellationToken ct);

    /// <summary>The fingerprint of a handle now; null when it is not a window.</summary>
    WindowFingerprint? Capture(nint handle);

    /// <summary>Starts hearing changes; <paramref name="sink"/> is called from the tracker's own threads.</summary>
    void Start(Action<WindowObservation> sink);

    void Stop();
}

/// <summary>Runs work on the shell's UI thread, in order.</summary>
public interface IUiPoster
{
    void Post(Action action);
}

public interface IWindowModel
{
    WindowSnapshot Current { get; }

    /// <summary>Random per host start; part of every public reference.</summary>
    long Epoch { get; }

    /// <summary>Raised on the UI thread after the model changed, at most once per queue drain.</summary>
    event EventHandler<WindowSnapshot> Changed;

    /// <summary>Records something the tracker saw. Any thread; the work happens on the UI thread.</summary>
    void Observe(WindowObservation observation);

    /// <summary>Resolves a public reference. StaleWindowReference for another epoch, WindowGone for a window that is not there.</summary>
    Outcome<WindowTarget> Resolve(string windowKey);

    /// <summary>Resolves a handle: the registry's current holder, else an unmanaged target when it is still a window, else WindowGone.</summary>
    Outcome<WindowTarget> ResolveHandle(nint handle);

    void SetProgress(WindowKey key, WindowProgress progress);

    void SetOverlay(WindowKey key, WindowOverlay? overlay);
}
