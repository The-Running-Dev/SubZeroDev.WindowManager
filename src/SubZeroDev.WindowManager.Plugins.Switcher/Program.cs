using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Sdk;

namespace SubZeroDev.WindowManager.Plugins.Switcher;

public static class Program
{
    public static Task<int> Main(string[] args) => PluginRuntime.RunAsync<SwitcherPlugin>(args);
}

/// <summary>
/// The themed Alt+Tab (S31, flow 14). The host's key hook turns the keyboard sequence into three commands:
/// <c>switcher.step</c> for each Tab while Alt is held, <c>switcher.commit</c> when Alt is released and
/// <c>switcher.cancel</c> for Esc. The plugin keeps only the list it showed and the selected place in it, never a
/// window's state (I39); the list is taken from <c>host.windows</c> and <c>host.desktops</c> when the sequence begins.
/// </summary>
public sealed class SwitcherPlugin : Plugin
{
    public const string PopupId = "switcher";

    private readonly SemaphoreSlim _order = new(1, 1);
    private readonly Lock _gate = new();
    private ShellViewSource? _view;
    private PopupSurface _popup = null!;
    private List<WindowFeedItem> _entries = [];
    private int _index;
    private bool _open;

    internal ShellView? Current => _view?.Current;

    protected override Task OnStartAsync(CancellationToken ct)
    {
        _popup = Context.Popups.Get(PopupId);
        _popup.SetView(SwitcherView());
        _view = new ShellViewSource(Context.Feeds);
        Context.Commands.Register("switcher.step", StepAsync);
        Context.Commands.Register("switcher.commit", CommitAsync);
        Context.Commands.Register("switcher.cancel", CancelAsync);
        Context.Commands.Register("switcher.pick", PickAsync);
        return Task.CompletedTask;
    }

    protected override Task OnStopAsync(CancellationToken ct)
    {
        _view?.Dispose();
        return Task.CompletedTask;
    }

    protected override ContributionInstance CreateInstance(InstanceRequest request) => new SwitchButtonInstance(request);

    protected override Task OnPopupClosedAsync(PopupSurface popup, PopupCloseReason reason, CancellationToken ct)
    {
        // Esc and a click outside close the popup from the host's side: the sequence is over with nothing chosen.
        lock (_gate)
        {
            _open = false;
            _entries = [];
            _index = 0;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The windows Alt+Tab lists: the strip rule on the current desktop or pinned to all, on every monitor, an owned
    /// window folded into its owner's entry, most recently in front first. Minimised windows are included.
    /// </summary>
    /// <param name="view">The shell view to list.</param>
    internal static List<WindowFeedItem> Listed(ShellView view)
    {
        var onDesktop = view.Windows.Windows
            .Where(window => window.IsStripStyle && (window.PinnedToAll
                || string.Equals(window.DesktopId, view.Desktops.Current, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var keys = onDesktop.Select(window => window.WindowKey).ToHashSet(StringComparer.Ordinal);
        var recency = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var window in view.Windows.Windows)
        {
            // An owned window in front puts its owner in front.
            if (window.OwnerKey is { } owner && keys.Contains(owner))
            {
                recency[owner] = Math.Max(recency.GetValueOrDefault(owner), window.ActivatedAt);
            }
        }

        return
        [
            .. onDesktop
                .Where(window => window.OwnerKey is null || !keys.Contains(window.OwnerKey))
                .OrderByDescending(window => Math.Max(window.ActivatedAt, recency.GetValueOrDefault(window.WindowKey)))
                .ThenBy(window => window.FirstSeen),
        ];
    }

    /// <summary>The place after a step: the first step lands on the second entry, backwards on the last, and both wrap.</summary>
    /// <param name="count">How many entries there are.</param>
    /// <param name="index">The selected place, or -1 before the first step.</param>
    /// <param name="backwards">Whether Shift is held.</param>
    internal static int Next(int count, int index, bool backwards)
    {
        if (count <= 1)
        {
            return 0;
        }

        if (index < 0)
        {
            return backwards ? count - 1 : 1;
        }

        return backwards ? (index + count - 1) % count : (index + 1) % count;
    }

    /// <summary>The popup's items: a picture and a title for each entry, the selected one marked.</summary>
    /// <param name="entries">The listed windows.</param>
    /// <param name="index">The selected place.</param>
    internal static JsonArray Items(IReadOnlyList<WindowFeedItem> entries, int index)
    {
        var items = new JsonArray();
        for (var i = 0; i < entries.Count; i++)
        {
            items.Add(new JsonObject
            {
                ["key"] = entries[i].WindowKey,
                ["windowKey"] = entries[i].WindowKey,
                ["title"] = entries[i].Title,
                ["selected"] = i == index,
                ["attention"] = entries[i].Attention,
            });
        }

        return items;
    }

    private static ViewNode SwitcherView()
    {
        var pick = new JsonObject { ["windowKey"] = new JsonObject { ["$item"] = "windowKey" } };
        var entry = View.Stack("entry", StackOrientation.Vertical,
            View.Thumbnail("picture", Prop<string>.Item("windowKey"), ThumbnailSize.Medium),
            View.Button("window", "Switch to window", View.Invoke("switcher.pick", pick),
                Prop<string>.Item("title"), null, Prop<bool>.Item("selected"), "switcher-entry")
                .WithAttention(Prop<bool>.Item("attention")));
        return View.Stack("switcher", StackOrientation.Vertical,
            View.List("windows", "Windows", Prop<JsonArray>.State("entries"), entry, StackOrientation.Horizontal));
    }

    private async Task<CommandResult> StepAsync(CommandContext command, CancellationToken ct)
    {
        var backwards = command.Args?["backwards"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
        await _order.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool opening;
            lock (_gate)
            {
                opening = !_open;
                if (opening)
                {
                    _entries = Current is { } view ? Listed(view) : [];
                    _index = -1;
                }

                if (_entries.Count == 0)
                {
                    return new CommandResult(null);
                }

                _index = Next(_entries.Count, _index, backwards);
                _popup.SetState("entries", Items(_entries, _index));
            }

            if (opening)
            {
                var opened = await _popup.OpenAsync(new PopupAnchor(null, MonitorAnchor.Pointer), ct).ConfigureAwait(false);
                if (opened.Error is { } error)
                {
                    throw new InvalidOperationException($"The switcher could not open: {error.Detail}");
                }

                lock (_gate)
                {
                    _open = true;
                }
            }

            return new CommandResult(null);
        }
        finally
        {
            _order.Release();
        }
    }

    private async Task<CommandResult> CommitAsync(CommandContext command, CancellationToken ct)
    {
        await _order.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            WindowFeedItem? chosen;
            lock (_gate)
            {
                chosen = _open && _index >= 0 && _index < _entries.Count ? _entries[_index] : null;
            }

            await CloseQuietlyAsync(ct).ConfigureAwait(false);
            if (chosen is not null)
            {
                await ActivateAsync(chosen.WindowKey, ct).ConfigureAwait(false);
            }

            return new CommandResult(null);
        }
        finally
        {
            _order.Release();
        }
    }

    private async Task<CommandResult> CancelAsync(CommandContext command, CancellationToken ct)
    {
        await _order.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            WindowFeedItem? before;
            lock (_gate)
            {
                // The first entry is the window that was in front when the sequence began.
                before = _open && _entries.Count > 0 && _entries[0].Active ? _entries[0] : null;
            }

            await CloseQuietlyAsync(ct).ConfigureAwait(false);
            if (before is not null)
            {
                await ActivateAsync(before.WindowKey, ct).ConfigureAwait(false);
            }

            return new CommandResult(null);
        }
        finally
        {
            _order.Release();
        }
    }

    private async Task<CommandResult> PickAsync(CommandContext command, CancellationToken ct)
    {
        var key = command.Args?["windowKey"] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0
            ? text
            : throw new ArgumentException("windowKey must name a window.");
        await _order.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await CloseQuietlyAsync(ct).ConfigureAwait(false);
            await ActivateAsync(key, ct).ConfigureAwait(false);
            return new CommandResult(null);
        }
        finally
        {
            _order.Release();
        }
    }

    private async Task ActivateAsync(string windowKey, CancellationToken ct)
    {
        var result = await Context.Commands.InvokeAsync("host.window.activate", new JsonObject { ["windowKey"] = windowKey }, ct)
            .ConfigureAwait(false);
        if (result.Error is { } error)
        {
            throw new CommandFailedException(error.Name ?? error.Code.ToString(), $"host.window.activate failed: {error.Detail}", error.Retryable);
        }
    }

    private async Task CloseQuietlyAsync(CancellationToken ct)
    {
        bool open;
        lock (_gate)
        {
            open = _open;
            _open = false;
            _entries = open ? _entries : [];
        }

        if (open)
        {
            await _popup.CloseAsync(ct).ConfigureAwait(false);
        }
    }
}

/// <summary>The panel item: a button that starts the switcher without the keyboard; Esc or a click outside closes it.</summary>
public sealed class SwitchButtonInstance(InstanceRequest request) : ContributionInstance(request)
{
    protected override Task OnCreatedAsync(CancellationToken ct)
    {
        Surface.SetView(View.Button("switch", "Switch windows",
            View.Invoke("switcher.step", new JsonObject { ["backwards"] = false }),
            Prop<string>.State("label"), null, null, "switcher"));
        Surface.SetState("label", "Windows");
        return Task.CompletedTask;
    }
}
