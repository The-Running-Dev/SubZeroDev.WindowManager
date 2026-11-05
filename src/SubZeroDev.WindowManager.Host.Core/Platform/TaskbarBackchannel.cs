namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>
/// What the shell hears from <c>ITaskbarList3</c> callers: progress and overlay badges for a window's button
/// (replacement mode only). Native arrival at <c>Shell_TrayWnd</c> is unverified; the sinks take the window's
/// key, so an application whose window is not tracked is ignored.
/// </summary>
public interface ITaskbarBackchannel : IDisposable
{
    /// <summary>Starts listening; the sinks are called on the tray's thread.</summary>
    void Start(Action<WindowKey, WindowProgress> progress, Action<WindowKey, WindowOverlay?> overlay);

    /// <summary>Broadcasts <c>TaskbarButtonCreated</c>, so running applications set their badges again.</summary>
    void BroadcastButtonCreated();

    /// <summary>Stops listening; messages that arrive afterwards are dropped.</summary>
    void Stop();
}
