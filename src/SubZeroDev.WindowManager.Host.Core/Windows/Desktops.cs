namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>One virtual desktop. Membership is never part of it: a window's desktop lives in the model for this run only.</summary>
public sealed record DesktopInfo(Guid Id, string? Name);

/// <summary>A window a plan names, with what must still be true of its handle before the effects thread touches it.</summary>
public sealed record PlannedWindow(WindowKey Key, WindowFingerprint Fingerprint);

/// <summary>What a switch will do, computed before anything is hidden or shown.</summary>
public sealed record SwitchPlan(Guid From, Guid To, IReadOnlyList<PlannedWindow> Hide, IReadOnlyList<PlannedWindow> Show);

/// <summary>The switch in flight, for the <c>switching</c> field of <c>host.desktops</c>.</summary>
public sealed record DesktopSwitching(Guid From, Guid To);

/// <summary>The desktop half of a snapshot, published in the same revision as the windows (I42).</summary>
public sealed record DesktopsView(Guid Current, bool Busy, DesktopSwitching? Switching, IReadOnlyList<DesktopInfo> Desktops)
{
    /// <summary>One unnamed desktop, for a model nobody configured.</summary>
    public static DesktopsView Single { get; } = new(WindowModel.DefaultDesktop, false, null, [new DesktopInfo(WindowModel.DefaultDesktop, null)]);
}

/// <summary>One window as the desktop set needs to see it.</summary>
public sealed record DesktopWindow(PlannedWindow Planned, Guid Desktop, bool Pinned, bool ShellHidden, bool Stuck, bool Visible, bool Minimised);

/// <summary>What the effects thread reports of a plan: windows it left visible because they refuse, and windows that were gone.</summary>
public sealed record EffectsResult(IReadOnlyList<WindowKey> Stuck, IReadOnlyList<WindowKey> Gone);

/// <summary>
/// Hides, shows and closes other applications' windows on a thread of its own (I27). Calls are non-blocking
/// (<c>SWP_ASYNCWINDOWPOS</c>) so one hung window never holds up the rest, and a window whose fingerprint no longer
/// matches is reported gone and left alone (I41).
/// </summary>
public interface IWindowEffects
{
    /// <summary>Hides <see cref="SwitchPlan.Hide"/> and shows <see cref="SwitchPlan.Show"/>; neither activates, moves or restores anything.</summary>
    Task<Outcome<EffectsResult>> ApplyAsync(SwitchPlan plan, CancellationToken ct);

    /// <summary>Shows every window this process hid (the shell is closing).</summary>
    Task<Outcome> ShowAllAsync(CancellationToken ct);

    /// <summary>Posts a close request. Never terminates the process.</summary>
    Task<Outcome> CloseAsync(PlannedWindow window, CancellationToken ct);
}

/// <summary>A live thumbnail of one window in one destination window; disposing it unregisters it (I36).</summary>
public interface IThumbnailRegistration : IDisposable
{
    /// <summary>Places the thumbnail in the destination's client area. <paramref name="visible"/> false hides it without unregistering.</summary>
    void SetBounds(RectInt bounds, bool visible);

    /// <summary>Whether the source is on screen to be drawn; false means the element shows its title card.</summary>
    bool SourceAvailable { get; }
}

/// <summary>Registers DWM thumbnails. A stale or gone reference registers nothing and the element draws a card.</summary>
public interface IThumbnailService
{
    /// <param name="source">The window to show, resolved at registration and again when it becomes available.</param>
    /// <param name="destinationWindow">The window the thumbnail is drawn in.</param>
    Outcome<IThumbnailRegistration> Register(WindowKey source, nint destinationWindow);
}

/// <summary>The shell's virtual desktops.</summary>
public interface IDesktopSet
{
    IReadOnlyList<DesktopInfo> Desktops { get; }

    Guid CurrentId { get; }

    /// <summary>Raised on any thread after the list or the current desktop changed.</summary>
    event EventHandler? Changed;

    /// <summary>A new desktop at the end; null or empty name leaves it unnamed.</summary>
    Outcome<Guid> Create(string? name);

    /// <summary>Removes a desktop; its windows move to the previous one. LastDesktop for the only desktop.</summary>
    Task<Outcome> RemoveAsync(Guid id, CancellationToken ct);

    Outcome Rename(Guid id, string name);

    /// <summary>What switching to <paramref name="id"/> would hide and show.</summary>
    Outcome<SwitchPlan> PlanSwitch(Guid id);

    /// <summary>Switches. A switch requested while one runs is held, and only the latest held one runs; the others report SwitchSuperseded.</summary>
    Task<Outcome> SwitchAsync(Guid id, CancellationToken ct);

    /// <summary>Files a window on a desktop and hides or shows it to match. The desktop must exist.</summary>
    Task<Outcome> MoveAsync(WindowKey key, Guid id, CancellationToken ct);

    /// <summary>
    /// Creates a desktop, files the window on it and hides it. A window that is gone before it is filed leaves no desktop
    /// behind; one that goes after it leaves the desktop, which holds it.
    /// </summary>
    Task<Outcome<Guid>> MoveToNewAsync(WindowKey key, CancellationToken ct);

    Task<Outcome> PinToAllAsync(WindowKey key, bool pinned, CancellationToken ct);
}
