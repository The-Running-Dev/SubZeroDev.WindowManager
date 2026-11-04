using System.Security.Cryptography;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>
/// The shell's one window registry (I39). A top-level window of another process is admitted once it has been visible
/// with a title, and then stays until it is destroyed, its handle stops being a window, or its fingerprint no longer
/// matches (a reused handle). Hiding, minimising and cloaking are changes to its facts, never removal (I40).
/// State changes arrive as observations on the UI thread; commands may resolve references from any thread.
/// </summary>
public sealed class WindowModel : IWindowModel
{
    /// <summary>The only desktop until virtual desktops are modelled; every window is filed on it.</summary>
    public static readonly Guid DefaultDesktop = new("00000000-0000-0000-0000-000000000001");

    private sealed class Entry(WindowKey key, WindowFacts facts, long firstSeen)
    {
        public WindowKey Key { get; } = key;

        public WindowFacts Facts { get; set; } = facts;

        public long FirstSeen { get; } = firstSeen;

        public long ActivatedAt { get; set; }

        public bool Attention { get; set; }

        public WindowProgress Progress { get; set; } = WindowProgress.None;

        public WindowOverlay? Overlay { get; set; }
    }

    private readonly IWindowFacts _reader;
    private readonly IUiPoster _poster;
    private readonly Func<WindowFacts, bool>? _scope;
    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];
    private long _generation;
    private long _sequence;
    private long _revision;
    private nint _foreground;
    private bool _dirty;
    private bool _publishQueued;
    private WindowSnapshot _current = new(0, []);

    /// <param name="reader">Reads the facts of a handle.</param>
    /// <param name="poster">Runs work on the UI thread.</param>
    /// <param name="epoch">The reference epoch; random when omitted.</param>
    /// <param name="scope">Narrows what is admitted, for a throwaway host in a test; null admits everything the boundary allows.</param>
    public WindowModel(IWindowFacts reader, IUiPoster poster, long? epoch = null, Func<WindowFacts, bool>? scope = null)
    {
        _reader = reader;
        _poster = poster;
        _scope = scope;
        Epoch = epoch ?? (RandomNumberGenerator.GetInt32(1, int.MaxValue) * 65536L) + RandomNumberGenerator.GetInt32(1, 65536);
    }

    public long Epoch { get; }

    public WindowSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<WindowSnapshot>? Changed;

    /// <summary>Admits the windows found by the first enumeration and publishes revision 1, even when there are none.</summary>
    /// <param name="handles">Topmost first, as <c>EnumWindows</c> returns them; the bottom-most is admitted first, so the order matches the first release.</param>
    public Task SeedAsync(IReadOnlyList<nint> handles)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _poster.Post(() =>
        {
            try
            {
                lock (_gate)
                {
                    for (var i = handles.Count - 1; i >= 0; i--)
                    {
                        Admit(handles[i]);
                    }

                    _dirty = true;
                }

                PublishNow();
                done.SetResult();
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        });
        return done.Task;
    }

    public void Observe(WindowObservation observation) => _poster.Post(() =>
    {
        lock (_gate)
        {
            Apply(observation);
        }

        QueuePublish();
    });

    public Outcome<WindowTarget> Resolve(string windowKey)
    {
        if (!WindowReference.TryParse(windowKey, out var reference))
        {
            return Outcome<WindowTarget>.Fail(CoreErrorCode.CommandFailed, "windowKey is not a window reference.");
        }

        if (reference.Epoch != Epoch)
        {
            return Outcome<WindowTarget>.Fail(CoreErrorCode.StaleWindowReference, "The window reference is from another run of the shell.");
        }

        var removed = false;
        try
        {
            lock (_gate)
            {
                var entry = _entries.Find(e => e.Key.Generation == reference.Generation);
                if (entry is null)
                {
                    return Outcome<WindowTarget>.Fail(CoreErrorCode.WindowGone, "The window is gone.");
                }

                if (!StillTheSame(entry))
                {
                    Remove(entry);
                    removed = true;
                    return Outcome<WindowTarget>.Fail(CoreErrorCode.WindowGone, "The window is gone.");
                }

                return Outcome<WindowTarget>.Success(new WindowTarget(entry.Key.Handle, Project(entry, _foreground)));
            }
        }
        finally
        {
            if (removed)
            {
                QueuePublish();
            }
        }
    }

    public Outcome<WindowTarget> ResolveHandle(nint handle)
    {
        var removed = false;
        try
        {
            lock (_gate)
            {
                var entry = _entries.Find(e => e.Key.Handle == handle);
                if (entry is not null)
                {
                    if (StillTheSame(entry))
                    {
                        return Outcome<WindowTarget>.Success(new WindowTarget(handle, Project(entry, _foreground)));
                    }

                    Remove(entry);
                    removed = true;
                }

                return _reader.Read(handle) is not null
                    ? Outcome<WindowTarget>.Success(new WindowTarget(handle, null))
                    : Outcome<WindowTarget>.Fail(CoreErrorCode.WindowGone, "The window is gone.");
            }
        }
        finally
        {
            if (removed)
            {
                QueuePublish();
            }
        }
    }

    public void SetProgress(WindowKey key, WindowProgress progress) => Edit(key, entry => entry.Progress = progress);

    public void SetOverlay(WindowKey key, WindowOverlay? overlay) => Edit(key, entry => entry.Overlay = overlay);

    private void Edit(WindowKey key, Action<Entry> change)
    {
        lock (_gate)
        {
            if (_entries.Find(e => e.Key == key) is not { } entry)
            {
                return;
            }

            change(entry);
            _dirty = true;
        }

        QueuePublish();
    }

    private void Apply(WindowObservation observation)
    {
        var (handle, kind) = (observation.Handle, observation.Kind);
        if (kind == ObservationKind.Rescan)
        {
            foreach (var entry in _entries.ToList())
            {
                Refresh(entry);
            }

            return;
        }

        var existing = _entries.Find(e => e.Key.Handle == handle);
        switch (kind)
        {
            case ObservationKind.Destroyed:
                if (existing is not null)
                {
                    Remove(existing);
                }

                if (_foreground == handle)
                {
                    _foreground = 0;
                }

                return;
            case ObservationKind.RescanMissing:
                if (existing is not null)
                {
                    Refresh(existing);
                }

                return;
            case ObservationKind.Foreground:
                OnForeground(handle, existing);
                return;
            case ObservationKind.Flashed:
                if (existing is not null && !existing.Attention)
                {
                    existing.Attention = true;
                    _dirty = true;
                }

                return;
            case ObservationKind.Redrawn:
                if (existing is not null && handle != _foreground && !existing.Attention)
                {
                    var title = _reader.Read(handle)?.Title;
                    if (title is not null && title == existing.Facts.Title)
                    {
                        existing.Attention = true;
                        _dirty = true;
                    }
                }

                return;
            default:
                if (existing is not null)
                {
                    Refresh(existing);
                }
                else
                {
                    Admit(handle);
                }

                return;
        }
    }

    private void OnForeground(nint handle, Entry? existing)
    {
        var facts = _reader.Read(handle);
        if (existing is not null && StillTheSame(existing, facts))
        {
            existing.Facts = facts!;
            existing.Attention = false;
            existing.ActivatedAt = ++_sequence;
            _foreground = ListsAsWindow(facts!) ? handle : _foreground;
            _dirty = true;
            return;
        }

        if (existing is null && Admit(handle) is { } admitted)
        {
            admitted.ActivatedAt = ++_sequence;
            _foreground = handle;
            _dirty = true;
            return;
        }

        // A foreground window the model does not track: a window that is not a tool window takes "active" away
        // (the user is elsewhere); a tool window, such as a panel, leaves the previous window active.
        if (facts is null || !(facts.ToolWindow && !facts.AppWindow))
        {
            if (_foreground != 0)
            {
                _foreground = 0;
                _dirty = true;
            }
        }
    }

    /// <summary>The first release's "can be the window the user works in" shape.</summary>
    private static bool ListsAsWindow(WindowFacts facts) => !(facts.ToolWindow && !facts.AppWindow);

    private Entry? Admit(nint handle)
    {
        var facts = _reader.Read(handle);
        if (facts is null || !Admissible(facts))
        {
            return null;
        }

        var entry = new Entry(new WindowKey(handle, ++_generation), facts, ++_sequence);
        _entries.Add(entry);
        _dirty = true;
        return entry;
    }

    private bool Admissible(WindowFacts facts)
    {
        if (facts.IsShellWindow || (_scope is not null && !_scope(facts)))
        {
            return false;
        }

        // A window of the host's own is admitted only if it would sit on the strip, so panels and popups never do.
        if (facts.OwnProcess && (facts.ToolWindow || facts.NoActivate) && !facts.AppWindow)
        {
            return false;
        }

        return facts.Visible && facts.Title.Length > 0;
    }

    private void Refresh(Entry entry)
    {
        var facts = _reader.Read(entry.Key.Handle);
        if (facts is null)
        {
            Remove(entry);
            return;
        }

        if (!facts.Fingerprint.Equals(entry.Facts.Fingerprint))
        {
            // The handle now belongs to another window: the old generation is destroyed, the handle is admitted afresh.
            Remove(entry);
            Admit(entry.Key.Handle);
            return;
        }

        if (facts != entry.Facts)
        {
            entry.Facts = facts;
            _dirty = true;
        }
    }

    private bool StillTheSame(Entry entry) => StillTheSame(entry, _reader.Read(entry.Key.Handle));

    private static bool StillTheSame(Entry entry, WindowFacts? facts) =>
        facts is not null && facts.Fingerprint.Equals(entry.Facts.Fingerprint);

    private void Remove(Entry entry)
    {
        _entries.Remove(entry);
        _dirty = true;
        if (_foreground == entry.Key.Handle)
        {
            _foreground = 0;
        }
    }

    private void QueuePublish()
    {
        lock (_gate)
        {
            if (!_dirty || _publishQueued)
            {
                return;
            }

            _publishQueued = true;
        }

        _poster.Post(PublishNow);
    }

    private void PublishNow()
    {
        WindowSnapshot snapshot;
        lock (_gate)
        {
            _publishQueued = false;
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            var keys = new Dictionary<nint, WindowKey>(_entries.Count);
            foreach (var entry in _entries)
            {
                keys.TryAdd(entry.Key.Handle, entry.Key);
            }

            snapshot = _current = new WindowSnapshot(++_revision, [.. _entries.Select(e => Project(e, _foreground, keys))]);
        }

        Changed?.Invoke(this, snapshot);
    }

    /// <param name="entry">The window to project.</param>
    /// <param name="foreground">The foreground handle.</param>
    /// <param name="keys">The keys of every tracked handle, built once per publish so a snapshot is linear; null looks the owner up directly.</param>
    private ManagedWindow Project(Entry entry, nint foreground, Dictionary<nint, WindowKey>? keys = null)
    {
        var f = entry.Facts;
        WindowKey? tracked = null;
        if (f.Owner != 0)
        {
            tracked = keys is null
                ? _entries.Find(e => e.Key.Handle == f.Owner)?.Key
                : keys.TryGetValue(f.Owner, out var key) ? key : null;
        }

        var owner = f.Owner == 0 ? (WindowKey?)null : tracked ?? new WindowKey(f.Owner, 0);
        return new ManagedWindow(
            entry.Key,
            f.Fingerprint,
            f.Title,
            f.ExePath,
            f.Aumid,
            GroupKeyOf(f),
            owner,
            f.MonitorId,
            DefaultDesktop,
            PinnedToAll: false,
            f.Visible,
            f.Cloak,
            ShellHidden: false,
            f.ToolWindow,
            f.NoActivate,
            f.AppWindow,
            Titled: f.Title.Length > 0,
            entry.FirstSeen,
            entry.ActivatedAt,
            Active: entry.Key.Handle == foreground,
            f.Minimised,
            entry.Attention,
            entry.Progress,
            entry.Overlay);
    }

    /// <summary>The application identity: the AUMID, else the normalised exe path, else the process id (a window whose process cannot be read).</summary>
    public static string GroupKeyOf(WindowFacts facts) =>
        facts.Aumid is { Length: > 0 } aumid
            ? aumid
            : facts.ExePath is { Length: > 0 } path
                ? path.Replace('/', '\\').ToLowerInvariant()
                : "pid:" + facts.Fingerprint.ProcessId;
}
