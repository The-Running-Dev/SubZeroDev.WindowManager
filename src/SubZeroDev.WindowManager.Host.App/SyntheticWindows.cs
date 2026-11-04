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

    /// <summary>Destroys <paramref name="count"/> windows (oldest first) and tells the model.</summary>
    public void Destroy(int count)
    {
        foreach (var handle in _windows.Keys.OrderBy(h => h).Take(count))
        {
            _windows.TryRemove(handle, out _);
            _model!.Observe(new WindowObservation(handle, ObservationKind.Destroyed));
        }
    }

    private WindowFacts Facts(nint handle, string title, CloakState cloak = CloakState.None) => new(
        new WindowFingerprint(Environment.ProcessId, 1, (int)(handle & 0xFFFF), "szd.Synthetic"),
        title, ExePath: @"C:\szd\synthetic.exe", Aumid: null, Owner: 0, monitorId, Visible: true, cloak,
        ToolWindow: false, NoActivate: false, AppWindow: true, Minimised: false, OwnProcess: false, IsShellWindow: false);
}
