using System.Globalization;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.Sdk;

/// <summary>The feed a plugin subscribes to for every window the shell tracks (host API 1.1).</summary>
public static class ShellFeedIds
{
    public const string Windows = "host.windows";
    public const string Desktops = "host.desktops";
}

/// <summary>
/// One window as <c>host.windows</c> carries it: facts only. Which view lists it is the view's rule, not a field here (I40).
/// </summary>
/// <param name="WindowKey">The opaque reference to pass to <c>host.window.activate</c> and <c>host.window.minimize</c>.</param>
/// <param name="Hwnd">The handle now; it may be reused later, so address a window by <paramref name="WindowKey"/>.</param>
/// <param name="ProcessId">The owning process.</param>
/// <param name="Title">The window title.</param>
/// <param name="Exe">The full path of the owning executable, when readable.</param>
/// <param name="Aumid">The application user model id, when the window has one.</param>
/// <param name="GroupKey">The AUMID if there is one, else the normalised executable path.</param>
/// <param name="OwnerKey">The reference of the owner window, when it is tracked.</param>
/// <param name="MonitorId">The display device the window is on.</param>
/// <param name="DesktopId">The virtual desktop the window belongs to.</param>
/// <param name="PinnedToAll">Whether the window shows on every desktop.</param>
/// <param name="Visible">Whether the window is visible, minimised or not.</param>
/// <param name="Cloak">One of <c>none</c>, <c>app</c>, <c>shell</c> and <c>inherited</c>.</param>
/// <param name="ShellHidden">Whether the shell hid it for another desktop.</param>
/// <param name="ToolWindow">The tool-window style.</param>
/// <param name="NoActivate">The no-activate style.</param>
/// <param name="AppWindow">The app-window style.</param>
/// <param name="Titled">Whether the title is not empty.</param>
/// <param name="FirstSeen">Orders windows by when the shell first saw them.</param>
/// <param name="ActivatedAt">Orders windows by when they were last in front.</param>
/// <param name="Active">Whether the window is the foreground one, or the last one while a panel has the foreground.</param>
/// <param name="Minimized">Whether the window is minimised.</param>
/// <param name="Attention">Whether the window asks for attention.</param>
/// <param name="Progress">The taskbar progress the program reports for it; null when it reports none.</param>
/// <param name="Overlay">The description of the overlay badge the program sets; null when it sets none.</param>
public sealed record WindowFeedItem(string WindowKey, long Hwnd, int ProcessId, string Title, string? Exe, string? Aumid,
    string GroupKey, string? OwnerKey, string MonitorId, string DesktopId, bool PinnedToAll, bool Visible, string Cloak,
    bool ShellHidden, bool ToolWindow, bool NoActivate, bool AppWindow, bool Titled, long FirstSeen, long ActivatedAt,
    bool Active, bool Minimized, bool Attention, FeedProgress? Progress = null, string? Overlay = null)
{
    /// <summary>
    /// The rule of a taskbar strip: visible, not cloaked, titled, and either marked as an app window or unowned and
    /// neither a tool window nor a no-activate window. Minimised windows are included.
    /// </summary>
    public bool IsStripStyle => Visible && Cloak == "none" && Titled && (AppWindow || (OwnerKey is null && !ToolWindow && !NoActivate));
}

/// <summary>The progress a program reports on its taskbar button.</summary>
/// <param name="State">One of <c>indeterminate</c>, <c>normal</c>, <c>error</c> and <c>paused</c>.</param>
/// <param name="Value">0..1 while the state is not indeterminate; null when unknown.</param>
public sealed record FeedProgress(string State, double? Value);

/// <param name="Id">The desktop's id.</param>
/// <param name="WindowCount">The windows that belong to it, pinned ones aside.</param>
/// <param name="HasAttention">Whether a window on it asks for attention.</param>
/// <param name="Name">The desktop's name; null when it is unnamed.</param>
public sealed record DesktopFeedItem(string Id, int WindowCount, bool HasAttention, string? Name = null);

/// <summary>The desktop switch in flight, from the <c>switching</c> field of <c>host.desktops</c>.</summary>
/// <param name="From">The desktop being left.</param>
/// <param name="To">The desktop being switched to.</param>
public sealed record DesktopSwitch(string From, string To);

/// <param name="Revision">The snapshot the feed was built from; <c>host.desktops</c> carries the same one.</param>
/// <param name="Windows">Every tracked window, in the order they were first seen.</param>
public sealed record WindowsFeedState(long Revision, IReadOnlyList<WindowFeedItem> Windows)
{
    /// <summary>Reads a <c>host.windows</c> snapshot; null when it does not have the contract's shape.</summary>
    public static WindowsFeedState? Parse(JsonNode? snapshot)
    {
        if (snapshot is not JsonObject root || Long(root, "revision") is not { } revision || root["windows"] is not JsonArray array)
        {
            return null;
        }

        var windows = new List<WindowFeedItem>(array.Count);
        foreach (var node in array)
        {
            if (node is JsonObject item && Item(item) is { } window)
            {
                windows.Add(window);
            }
        }

        return new WindowsFeedState(revision, windows);
    }

    private static WindowFeedItem? Item(JsonObject o)
    {
        if (Text(o, "windowKey") is not { } key || Long(o, "hwnd") is not { } hwnd)
        {
            return null;
        }

        return new WindowFeedItem(
            key, hwnd, (int)(Long(o, "processId") ?? 0), Text(o, "title") ?? string.Empty, Text(o, "exe"), Text(o, "aumid"),
            Text(o, "groupKey") ?? string.Empty, Text(o, "ownerKey"), Text(o, "monitorId") ?? string.Empty,
            Text(o, "desktopId") ?? string.Empty, Flag(o, "pinnedToAll"), Flag(o, "visible"), Text(o, "cloak") ?? "none",
            Flag(o, "shellHidden"), Flag(o, "toolWindow"), Flag(o, "noActivate"), Flag(o, "appWindow"), Flag(o, "titled"),
            Long(o, "firstSeen") ?? 0, Long(o, "activatedAt") ?? 0, Flag(o, "active"), Flag(o, "minimized"), Flag(o, "attention"),
            ProgressOf(o["progress"] as JsonObject), o["overlay"] is JsonObject overlay ? Text(overlay, "description") ?? string.Empty : null);
    }

    private static FeedProgress? ProgressOf(JsonObject? o) =>
        o is not null && Text(o, "state") is { } state && state != "none"
            ? new FeedProgress(state, o["value"] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null)
            : null;

    internal static long? Long(JsonObject o, string name) =>
        o[name] is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    internal static string? Text(JsonObject o, string name) =>
        o[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static bool Flag(JsonObject o, string name) =>
        o[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}

/// <param name="Revision">The snapshot the feed was built from; <c>host.windows</c> carries the same one.</param>
/// <param name="Current">The desktop the user is on.</param>
/// <param name="Busy">Whether a desktop switch is planned or running.</param>
/// <param name="Desktops">The desktops, the current one first.</param>
/// <param name="Switching">The switch in flight; null when none runs.</param>
public sealed record DesktopsFeedState(long Revision, string Current, bool Busy, IReadOnlyList<DesktopFeedItem> Desktops,
    DesktopSwitch? Switching = null)
{
    /// <summary>Reads a <c>host.desktops</c> snapshot; null when it does not have the contract's shape.</summary>
    public static DesktopsFeedState? Parse(JsonNode? snapshot)
    {
        if (snapshot is not JsonObject root || WindowsFeedState.Long(root, "revision") is not { } revision
            || WindowsFeedState.Text(root, "current") is not { } current || root["desktops"] is not JsonArray array)
        {
            return null;
        }

        var desktops = new List<DesktopFeedItem>(array.Count);
        foreach (var node in array)
        {
            if (node is JsonObject item && WindowsFeedState.Text(item, "id") is { } id)
            {
                desktops.Add(new DesktopFeedItem(id, (int)(WindowsFeedState.Long(item, "windowCount") ?? 0),
                    WindowsFeedState.Flag(item, "hasAttention"), WindowsFeedState.Text(item, "name")));
            }
        }

        var switching = root["switching"] is JsonObject s && WindowsFeedState.Text(s, "from") is { } from
            && WindowsFeedState.Text(s, "to") is { } to
            ? new DesktopSwitch(from, to)
            : null;
        return new DesktopsFeedState(revision, current, WindowsFeedState.Flag(root, "busy"), desktops, switching);
    }
}

/// <summary>The two shell feeds read together.</summary>
/// <param name="Revision">The revision both feeds carry; the lower of the two while <paramref name="Stale"/>.</param>
/// <param name="Stale">Whether the feeds disagreed for longer than the hold, so this view mixes two revisions.</param>
/// <param name="Windows">The windows feed.</param>
/// <param name="Desktops">The desktops feed.</param>
public sealed record ShellView(long Revision, bool Stale, WindowsFeedState Windows, DesktopsFeedState Desktops)
{
    /// <summary>The windows a taskbar on the current desktop lists: the strip rule, on this desktop or pinned, in first-seen order.</summary>
    public IReadOnlyList<WindowFeedItem> RunningWindows() =>
    [
        .. Windows.Windows
            .Where(window => window.IsStripStyle && (window.PinnedToAll
                || string.Equals(window.DesktopId, Desktops.Current, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(window => window.FirstSeen),
    ];

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"ShellView r{Revision}{(Stale ? " stale" : string.Empty)}");
}

/// <summary>
/// Reads <c>host.windows</c> and <c>host.desktops</c> as one view (I42). Both carry the revision of one snapshot, so a
/// view forms only when the two agree. When one lags, the newer waits for the hold; the lagging feed is then read
/// again, and if it still lags the view is formed from the lower revision with <see cref="ShellView.Stale"/> set.
/// </summary>
public sealed class ShellViewSource : IDisposable
{
    /// <summary>How long a disagreement is tolerated before the view is marked stale.</summary>
    public static readonly TimeSpan DefaultHold = TimeSpan.FromMilliseconds(250);

    private readonly IFeeds _feeds;
    private readonly TimeSpan _hold;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<IDisposable> _subscriptions = [];
    private WindowsFeedState? _windows;
    private DesktopsFeedState? _desktops;
    private bool _available = true;
    private ShellView? _current;
    private CancellationTokenSource? _holding;

    /// <param name="feeds">The plugin's feeds; the manifest must list both shell feeds under <c>subscribes</c>.</param>
    /// <param name="hold">How long to wait for a lagging feed; <see cref="DefaultHold"/> when null.</param>
    /// <param name="delay">Waits for the hold; tests pass their own clock.</param>
    public ShellViewSource(IFeeds feeds, TimeSpan? hold = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(feeds);
        _feeds = feeds;
        _hold = hold ?? DefaultHold;
        _delay = delay ?? Task.Delay;
        _subscriptions.Add(feeds.Subscribe(ShellFeedIds.Windows, (update, _) => OnUpdate(update, windows: true)));
        _subscriptions.Add(feeds.Subscribe(ShellFeedIds.Desktops, (update, _) => OnUpdate(update, windows: false)));
    }

    /// <summary>Raised with each new view, on the feed callback queue or, after a hold, on a thread-pool thread. Not raised while the feeds are unavailable.</summary>
    public event Action<ShellView>? Changed;

    /// <summary>Raised when a feed becomes unavailable or returns.</summary>
    public event Action<bool>? AvailabilityChanged;

    /// <summary>The latest view, or null before the first one forms.</summary>
    public ShellView? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public bool Available
    {
        get
        {
            lock (_gate)
            {
                return _available;
            }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        lock (_gate)
        {
            _holding?.Cancel();
        }
    }

    private Task OnUpdate(FeedUpdate update, bool windows)
    {
        ShellView? formed = null;
        bool? availability = null;
        lock (_gate)
        {
            if (!update.Available)
            {
                if (_available)
                {
                    _available = false;
                    availability = false;
                }
            }
            else
            {
                if (windows)
                {
                    _windows = WindowsFeedState.Parse(update.Snapshot) ?? _windows;
                }
                else
                {
                    _desktops = DesktopsFeedState.Parse(update.Snapshot) ?? _desktops;
                }

                if (!_available)
                {
                    _available = true;
                    availability = true;
                }

                formed = Evaluate();
            }
        }

        if (availability is { } value)
        {
            AvailabilityChanged?.Invoke(value);
        }

        if (formed is not null)
        {
            Changed?.Invoke(formed);
        }

        return Task.CompletedTask;
    }

    /// <summary>Called under the gate. Forms a view when the feeds agree, else starts the hold.</summary>
    private ShellView? Evaluate()
    {
        if (_windows is not { } windows || _desktops is not { } desktops)
        {
            return null;
        }

        if (windows.Revision == desktops.Revision)
        {
            _holding?.Cancel();
            _holding = null;
            return _current = new ShellView(windows.Revision, false, windows, desktops);
        }

        if (_holding is null)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _holding = source;
            _ = HoldAsync(source);
        }

        return null;
    }

    private async Task HoldAsync(CancellationTokenSource source)
    {
        // Whoever replaces or cancels the hold only cancels it; this method is the one place that disposes it.
        using (source)
        {
            try
            {
                await _delay(_hold, source.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            ShellView? formed;
            lock (_gate)
            {
                if (!ReferenceEquals(_holding, source) || source.IsCancellationRequested)
                {
                    return;
                }

                _holding = null;
                ReadAgain();
                if (_windows is not { } windows || _desktops is not { } desktops)
                {
                    return;
                }

                formed = _current = new ShellView(Math.Min(windows.Revision, desktops.Revision), windows.Revision != desktops.Revision, windows, desktops);
            }

            Changed?.Invoke(formed);
        }
    }

    /// <summary>Takes the latest snapshot of each feed again; an update may have landed while the hold ran.</summary>
    private void ReadAgain()
    {
        if (_feeds.Latest(ShellFeedIds.Windows) is { Available: true } windows)
        {
            _windows = WindowsFeedState.Parse(windows.Snapshot) ?? _windows;
        }

        if (_feeds.Latest(ShellFeedIds.Desktops) is { Available: true } desktops)
        {
            _desktops = DesktopsFeedState.Parse(desktops.Snapshot) ?? _desktops;
        }
    }
}
