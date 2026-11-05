using System.Collections.Concurrent;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.App;

/// <summary>
/// The test hook's source of windows (S25.11): windows that exist only in the model's eyes, so a test can track hundreds of
/// them, create, retitle and destroy them, without opening or touching a real window (I38). It answers for its own
/// handles and passes every other handle to the real reader.
/// </summary>
internal sealed class SyntheticWindows(IWindowFacts inner, string monitorId) : IWindowFacts
{
    // Well above any real handle (a window handle holds 32 significant bits), so the two never collide.
    private const long FirstHandle = 0x7A00_0000_0000;

    private readonly ConcurrentDictionary<nint, WindowFacts> _windows = new();
    private readonly ConcurrentDictionary<nint, byte> _refusing = new();
    private long _next = FirstHandle;
    private int _titleVersion;
    private IWindowModel? _model;

    public int Count => _windows.Count;

    /// <summary>The model that reads these windows; the model needs this reader first, so it is bound afterwards.</summary>
    public void Bind(IWindowModel model) => _model = model;

    public WindowFacts? Read(nint handle) => _windows.TryGetValue(handle, out var facts) ? facts : inner.Read(handle);

    /// <summary>
    /// Creates <paramref name="count"/> shown, titled application windows and tells the model. With
    /// <paramref name="strip"/> false they are cloaked, as a window on another desktop is: the model tracks and
    /// publishes them, but no strip lists them, so a test can measure tracking apart from drawing hundreds of buttons.
    /// </summary>
    /// <param name="count">How many windows to create.</param>
    /// <param name="strip">Whether the windows are of the kind a strip lists.</param>
    public IReadOnlyList<nint> Add(int count, bool strip = true)
    {
        var handles = new List<nint>(count);
        for (var i = 0; i < count; i++)
        {
            var handle = (nint)Interlocked.Increment(ref _next);
            _windows[handle] = Facts(handle, $"szd-synthetic-{handle:X}", strip ? CloakState.None : CloakState.Shell);
            handles.Add(handle);
            _model!.Observe(new WindowObservation(handle, ObservationKind.Created));
        }

        return handles;
    }

    /// <summary>Gives <paramref name="count"/> windows (oldest first) a new title and tells the model.</summary>
    public void Retitle(int count)
    {
        var version = Interlocked.Increment(ref _titleVersion);
        foreach (var handle in _windows.Keys.OrderBy(h => h).Take(count))
        {
            _windows[handle] = _windows[handle] with { Title = $"szd-synthetic-{handle:X}-{version}" };
            _model!.Observe(new WindowObservation(handle, ObservationKind.TitleChanged));
        }
    }

    /// <summary>Whether <paramref name="handle"/> is one of these model-only windows.</summary>
    public bool Owns(nint handle) => _windows.ContainsKey(handle);

    /// <summary>Brings one of these windows to the front, as the user would: it is shown again if minimised and the model is told.</summary>
    /// <param name="handle">The window handle.</param>
    /// <returns>False when the handle is not one of these windows.</returns>
    public bool Activate(nint handle)
    {
        if (!_windows.TryGetValue(handle, out var facts))
        {
            return false;
        }

        if (facts.Minimised)
        {
            _windows[handle] = facts with { Minimised = false };
        }

        _model!.Observe(new WindowObservation(handle, ObservationKind.Foreground));
        return true;
    }

    /// <summary>Synthetic windows that are not shown.</summary>
    public int HiddenCount => _windows.Values.Count(f => !f.Visible);

    /// <summary>Whether the window refuses to be hidden, as an elevated or hung one does.</summary>
    public bool Refuses(nint handle) => _refusing.ContainsKey(handle);

    /// <summary>Makes the oldest <paramref name="count"/> windows refuse to be hidden.</summary>
    public void Refuse(int count)
    {
        foreach (var handle in _windows.Keys.OrderBy(h => h).Take(count))
        {
            _refusing[handle] = 0;
        }
    }

    /// <summary>Shows or hides one window, as the effects thread does, and tells the model.</summary>
    public void SetVisible(nint handle, bool visible)
    {
        if (_windows.TryGetValue(handle, out var facts) && facts.Visible != visible)
        {
            _windows[handle] = facts with { Visible = visible };
            _model!.Observe(new WindowObservation(handle, visible ? ObservationKind.Shown : ObservationKind.Hidden));
        }
    }

    /// <summary>The application closes the window: it goes, and the model is told.</summary>
    public void Close(nint handle)
    {
        if (_windows.TryRemove(handle, out _))
        {
            _refusing.TryRemove(handle, out _);
            _model!.Observe(new WindowObservation(handle, ObservationKind.Destroyed));
        }
    }

    /// <summary>The application closes <paramref name="count"/> windows (oldest first), as <see cref="Close"/> does for one.</summary>
    /// <param name="count">How many windows to close.</param>
    public void CloseOldest(int count)
    {
        foreach (var handle in _windows.Keys.OrderBy(h => h).Take(count))
        {
            Close(handle);
        }
    }

    /// <summary>Destroys <paramref name="count"/> windows (oldest first) and tells the model.</summary>
    public void Destroy(int count)
    {
        foreach (var handle in _windows.Keys.OrderBy(h => h).Take(count))
        {
            _windows.TryRemove(handle, out _);
            _refusing.TryRemove(handle, out _);
            _model!.Observe(new WindowObservation(handle, ObservationKind.Destroyed));
        }
    }

    private WindowFacts Facts(nint handle, string title, CloakState cloak = CloakState.None) => new(
        new WindowFingerprint(Environment.ProcessId, 1, (int)(handle & 0xFFFF), "szd.Synthetic"),
        title, ExePath: @"C:\szd\synthetic.exe", Aumid: null, Owner: 0, monitorId, Visible: true, cloak,
        ToolWindow: false, NoActivate: false, AppWindow: true, Minimised: false, OwnProcess: false, IsShellWindow: false);
}
