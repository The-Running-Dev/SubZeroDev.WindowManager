using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Sdk;

[assembly: SupportedOSPlatform("windows")]

namespace SubZeroDev.WindowManager.Plugins.RunningWindows;

public static class Program
{
    public static Task<int> Main(string[] args) => PluginRuntime.RunAsync<RunningWindowsPlugin>(args);
}

/// <summary>One button of the taskbar: a window, or the windows of one application drawn as a group.</summary>
/// <param name="Key">The button's key: the handle of a lone window, else <c>g:</c> and the group key.</param>
/// <param name="Windows">The windows it stands for, in first-seen order.</param>
public sealed record TaskGroup(string Key, IReadOnlyList<WindowFeedItem> Windows)
{
    /// <summary>The window a click acts on: the one activated most recently.</summary>
    public WindowFeedItem Representative => Windows.MaxBy(window => window.ActivatedAt) ?? Windows[0];
}

/// <summary>Grouping is presentation (S29.1): the host's model has one entry per window, and this draws them as buttons.</summary>
public static class TaskGroups
{
    /// <summary>How many windows an instance may show before <c>whenFull</c> starts to group them.</summary>
    public const int FullAt = 8;

    public const string GroupPrefix = "g:";

    /// <summary>The buttons for <paramref name="windows"/>: grouped by application, or one each.</summary>
    /// <param name="windows">The windows to show, in first-seen order.</param>
    /// <param name="grouping"><c>always</c>, <c>whenFull</c> (only while there are more than <see cref="FullAt"/> windows) or <c>never</c>.</param>
    public static IReadOnlyList<TaskGroup> Build(IReadOnlyList<WindowFeedItem> windows, string grouping)
    {
        var group = grouping == "always" || (grouping == "whenFull" && windows.Count > FullAt);
        if (!group)
        {
            return [.. windows.Select(window => new TaskGroup(Single(window), [window]))];
        }

        var buttons = new List<TaskGroup>();
        var seen = new Dictionary<string, List<WindowFeedItem>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var window in windows)
        {
            if (!seen.TryGetValue(window.GroupKey, out var members))
            {
                seen[window.GroupKey] = members = [];
                order.Add(window.GroupKey);
            }

            members.Add(window);
        }

        foreach (var key in order)
        {
            var members = seen[key];
            buttons.Add(members.Count == 1 ? new TaskGroup(Single(members[0]), members) : new TaskGroup(GroupPrefix + key, members));
        }

        return buttons;
    }

    /// <summary>The windows a button key stands for among <paramref name="windows"/>; empty when it is gone.</summary>
    /// <param name="windows">The windows to look in.</param>
    /// <param name="key">A key from <see cref="TaskGroup.Key"/>.</param>
    public static IReadOnlyList<WindowFeedItem> Members(IEnumerable<WindowFeedItem> windows, string key) =>
        key.StartsWith(GroupPrefix, StringComparison.Ordinal)
            ? [.. windows.Where(window => window.GroupKey == key[GroupPrefix.Length..])]
            : [.. windows.Where(window => Single(window) == key)];

    /// <summary>The text a button carries as its badge: the progress, else an overlay's description, else the number of windows in a group.</summary>
    /// <param name="group">The button.</param>
    public static string Badge(TaskGroup group)
    {
        if (group.Windows.FirstOrDefault(window => window.Progress is not null)?.Progress is { } progress)
        {
            return progress.State == "indeterminate" || progress.Value is null
                ? "…"
                : string.Create(CultureInfo.InvariantCulture, $"{Math.Round(Math.Clamp(progress.Value.Value, 0, 1) * 100)}%");
        }

        if (group.Windows.FirstOrDefault(window => window.Overlay is not null)?.Overlay is { } overlay)
        {
            return overlay.Length > 0 ? overlay : "•";
        }

        return group.Windows.Count > 1 ? group.Windows.Count.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    /// <summary>The list item for a button.</summary>
    /// <param name="group">The button.</param>
    /// <param name="labels">Whether the button shows its window's title; false leaves the icon alone.</param>
    public static JsonObject Item(TaskGroup group, bool labels)
    {
        var window = group.Representative;
        var item = RunningWindowsPlugin.Item(window);
        item["key"] = group.Key;
        item["active"] = group.Windows.Any(member => member.Active);
        item["minimized"] = group.Windows.All(member => member.Minimized);
        item["attention"] = group.Windows.Any(member => member.Attention);
        item["count"] = group.Windows.Count;
        item["label"] = labels ? window.Title : string.Empty;
        item["tooltip"] = group.Windows.Count > 1
            ? string.Create(CultureInfo.InvariantCulture, $"{window.Title} ({group.Windows.Count} windows)")
            : window.Title;
        item["badge"] = Badge(group);
        item["icon"] = new JsonObject { ["window"] = window.Hwnd };
        return item;
    }

    private static string Single(WindowFeedItem window) => window.Hwnd.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// An adapter over the shell's window model: reads <c>host.windows</c> and <c>host.desktops</c>, publishes the taskbar's
/// view of them as <c>windows.running</c>, serves <c>windows.activate</c> and <c>windows.minimize</c> through the host
/// commands, and shows the <c>windows</c> panel item: one button per window or per application, on every monitor or only
/// the panel's own. Hovering a button opens the <c>preview</c> popup with a live picture of each of its windows; a right
/// click or <c>windows.openJumpList</c> opens the <c>jumplist</c> popup. It tracks nothing itself (I39).
/// </summary>
public sealed class RunningWindowsPlugin : Plugin
{
    public const string Feed = "windows.running";
    public const string PreviewId = "preview";
    public const string JumpListId = "jumplist";
    public const string HookVariable = "SUBZERODEV_WM_TEST_HOOK";

    private readonly Lock _gate = new();
    private ShellViewSource? _view;
    private PopupSurface _preview = null!;
    private PopupSurface _jumpList = null!;
    private string? _previewFor;
    private IReadOnlyList<JumpListEntry> _entries = [];

    internal ShellView? Current => _view?.Current;

    internal event Action<ShellView>? Changed;

    protected override Task OnStartAsync(CancellationToken ct)
    {
        _preview = Context.Popups.Get(PreviewId);
        _jumpList = Context.Popups.Get(JumpListId);
        _preview.SetView(PreviewView());
        var publisher = Context.Feeds.Publisher(Feed);
        var view = _view = new ShellViewSource(Context.Feeds);
        view.Changed += shell =>
        {
            publisher.Publish(Snapshot(shell));
            RefreshPreview(shell);
            Changed?.Invoke(shell);
        };
        Context.Commands.Register("windows.activate", ActivateAsync);
        Context.Commands.Register("windows.minimize", (command, token) => ForwardAsync("host.window.minimize", command, token));
        Context.Commands.Register("windows.openJumpList", OpenJumpListAsync);
        return Task.CompletedTask;
    }

    protected override Task OnStopAsync(CancellationToken ct)
    {
        _view?.Dispose();
        return Task.CompletedTask;
    }

    protected override ContributionInstance CreateInstance(InstanceRequest request) => new WindowListInstance(request, this);

    protected override Task OnPopupOpenedAsync(PopupSurface popup, PopupTrigger trigger, CancellationToken ct)
    {
        if (popup.PopupId == PreviewId)
        {
            lock (_gate)
            {
                _previewFor = popup.OpenedFor;
            }

            if (Current is { } current)
            {
                RefreshPreview(current);
            }
        }

        return Task.CompletedTask;
    }

    protected override Task OnPopupClosedAsync(PopupSurface popup, PopupCloseReason reason, CancellationToken ct)
    {
        if (popup.PopupId == PreviewId)
        {
            lock (_gate)
            {
                _previewFor = null;
            }
        }

        return Task.CompletedTask;
    }

    protected override async Task OnPopupEventAsync(PopupSurface popup, ElementEvent e, CancellationToken ct)
    {
        if (popup.PopupId != JumpListId || e.Kind != ElementEventKind.Invoked
            || !e.ElementId.StartsWith("entry", StringComparison.Ordinal)
            || !int.TryParse(e.ElementId.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            return;
        }

        JumpListEntry entry;
        lock (_gate)
        {
            if (index >= _entries.Count)
            {
                return;
            }

            entry = _entries[index];
        }

        try
        {
            Launch(entry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Context.Log.LogWarning("Starting {Title} failed: {Reason}", entry.Title, ex.Message);
            return;
        }

        await _jumpList.CloseAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The <c>windows.running</c> snapshot: the taskbar's windows for the current desktop, in first-seen order.</summary>
    internal static JsonArray Snapshot(ShellView view) => [.. view.RunningWindows().Select(Item)];

    /// <summary>The first-release item, plus the additive <c>windowKey</c>, <c>groupKey</c> and <c>desktopId</c>.</summary>
    internal static JsonObject Item(WindowFeedItem window) => new()
    {
        ["key"] = window.Hwnd.ToString(CultureInfo.InvariantCulture),
        ["hwnd"] = window.Hwnd,
        ["title"] = window.Title,
        ["exe"] = window.Exe,
        ["aumid"] = window.Aumid,
        ["monitorId"] = window.MonitorId,
        ["active"] = window.Active,
        ["minimized"] = window.Minimized,
        ["attention"] = window.Attention,
        ["windowKey"] = window.WindowKey,
        ["groupKey"] = window.GroupKey,
        ["desktopId"] = window.DesktopId,
    };

    /// <summary>The preview's items: a picture and a title for each window of the button the pointer is on.</summary>
    /// <param name="windows">The windows of the button.</param>
    internal static JsonArray Previews(IEnumerable<WindowFeedItem> windows) =>
    [
        .. windows.Select(window => new JsonObject
        {
            ["key"] = window.WindowKey,
            ["windowKey"] = window.WindowKey,
            ["title"] = window.Title,
            ["attention"] = window.Attention,
        }),
    ];

    private static ViewNode PreviewView()
    {
        var goArgs = new JsonObject { ["windowKey"] = new JsonObject { ["$item"] = "windowKey" } };
        var card = View.Stack("card", StackOrientation.Vertical,
            View.Thumbnail("picture", Prop<string>.Item("windowKey"), ThumbnailSize.Medium),
            View.Button("go", "Go to window", View.Invoke("windows.activate", goArgs),
                Prop<string>.Item("title"), null, null, "preview-window")
                .WithAttention(Prop<bool>.Item("attention")));
        return View.List("previews", "Windows of the application", Prop<JsonArray>.State("previews"), card, StackOrientation.Horizontal);
    }

    private void RefreshPreview(ShellView view)
    {
        string? key;
        lock (_gate)
        {
            key = _previewFor;
        }

        if (key is not null)
        {
            _preview.SetState("previews", Previews(TaskGroups.Members(view.RunningWindows(), key)));
        }
    }

    /// <summary>Brings a window to the front, or minimises it when it already is in front, as a taskbar does.</summary>
    private Task<CommandResult> ActivateAsync(CommandContext command, CancellationToken ct)
    {
        var window = Find(command);
        var target = window is { Active: true, Minimized: false } ? "host.window.minimize" : "host.window.activate";
        return ForwardAsync(target, command, ct);
    }

    private WindowFeedItem? Find(CommandContext command)
    {
        var windows = _view?.Current?.Windows.Windows;
        if (windows is null)
        {
            return null;
        }

        return Key(command) is { } key
            ? windows.FirstOrDefault(candidate => candidate.WindowKey == key)
            : Hwnd(command) is { } hwnd ? windows.FirstOrDefault(candidate => candidate.Hwnd == hwnd) : null;
    }

    /// <summary>Forwards by the window key when the command carries one or the model knows the window, else by the handle.</summary>
    private async Task<CommandResult> ForwardAsync(string hostCommand, CommandContext command, CancellationToken ct)
    {
        var args = new JsonObject();
        if ((Key(command) ?? Find(command)?.WindowKey) is { } key)
        {
            args["windowKey"] = key;
        }
        else if (Hwnd(command) is { } hwnd)
        {
            args["hwnd"] = hwnd;
        }
        else
        {
            throw new ArgumentException("hwnd or windowKey must identify a window.");
        }

        var result = await Context.Commands.InvokeAsync(hostCommand, args, ct).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            throw new CommandFailedException(error.Name ?? error.Code.ToString(), $"{hostCommand} failed: {error.Detail}", error.Retryable);
        }

        return new CommandResult(result.Value?.Value);
    }

    // ---- The jump list ----------------------------------------------------------------------------------------

    /// <summary>
    /// Opens the jump list of the application a group key names. <c>x</c> and <c>y</c> are accepted for the contract but the
    /// popup opens at the pointer, which is where the click that asks for it happened.
    /// </summary>
    private async Task<CommandResult> OpenJumpListAsync(CommandContext command, CancellationToken ct)
    {
        var groupKey = command.Args?["groupKey"] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0
            ? text
            : throw new ArgumentException("groupKey must name an application.");
        var window = Current?.RunningWindows().FirstOrDefault(candidate => candidate.GroupKey == groupKey)
            ?? throw new ArgumentException("No running window belongs to that application.");
        var entries = await Task.Run(() => EntriesFor(window), ct).ConfigureAwait(false);
        lock (_gate)
        {
            _entries = entries;
        }

        ShowJumpList(entries);
        var opened = await _jumpList.OpenAsync(new PopupAnchor(null, MonitorAnchor.Pointer), ct).ConfigureAwait(false);
        if (opened.Error is { } error)
        {
            throw new InvalidOperationException($"The jump list could not open: {error.Detail}");
        }

        return new CommandResult(null);
    }

    /// <summary>
    /// What the list offers: a task to start the program, then whatever the destination files hold. Windows keeps those
    /// files under the user's profile; an application whose files are not found shows the task alone (design question 10).
    /// </summary>
    private IReadOnlyList<JumpListEntry> EntriesFor(WindowFeedItem window)
    {
        var entries = new List<JumpListEntry>();
        if (window.Exe is { Length: > 0 } exe)
        {
            entries.Add(new JumpListEntry("tasks", "Open " + Path.GetFileNameWithoutExtension(exe), exe, null));
        }

        try
        {
            entries.AddRange(Hook() is { } hook ? HookEntries(hook, window.Exe) : JumpLists.Read(UserRecentFolder(), window.Exe, window.Aumid));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Context.Log.LogWarning("The jump list of {Exe} could not be read: {Reason}", window.Exe, e.Message);
        }

        return entries;
    }

    /// <summary>Under the test hook the lists come from <c>jumplists.json</c> there, keyed by the lower-cased program file name (I38).</summary>
    private static List<JumpListEntry> HookEntries(string hook, string? exe)
    {
        var file = Path.Combine(hook, "jumplists.json");
        var entries = new List<JumpListEntry>();
        if (exe is null || !File.Exists(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonObject root
            || root[Path.GetFileName(exe).ToLowerInvariant()] is not JsonArray list)
        {
            return entries;
        }

        foreach (var node in list.OfType<JsonObject>())
        {
            if (node["path"]?.GetValue<string>() is { Length: > 0 } path)
            {
                entries.Add(new JumpListEntry(
                    node["category"]?.GetValue<string>() ?? "recent", node["title"]?.GetValue<string>() ?? path, path, node["args"]?.GetValue<string>()));
            }
        }

        return entries;
    }

    private static string UserRecentFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Recent");

    private void ShowJumpList(IReadOnlyList<JumpListEntry> entries)
    {
        var children = new List<ViewNode>();
        string? category = null;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.Category != category)
            {
                category = entry.Category;
                children.Add(View.Text("category" + index.ToString(CultureInfo.InvariantCulture), Heading(category), "jumplist-category"));
            }

            var id = "entry" + index.ToString(CultureInfo.InvariantCulture);
            Prop<IconRef>? icon = entry.Category == "tasks" ? new IconRef.App(entry.Target, null) : (Prop<IconRef>?)null;
            children.Add(View.Button(id, entry.Title, View.Event(id), entry.Title, icon, null, "menu-item"));
        }

        _jumpList.SetView(View.Stack("jumplist", StackOrientation.Vertical, [.. children]));
    }

    private static string Heading(string category) => category switch
    {
        "tasks" => "Tasks",
        "recent" => "Recent",
        "frequent" => "Frequent",
        "custom" => "More",
        _ => category,
    };

    /// <summary>Starts an entry through the shell. Under the test hook it is only recorded in <c>desktop-actions.log</c> (I38).</summary>
    private static void Launch(JumpListEntry entry)
    {
        if (Hook() is { } hook)
        {
            Directory.CreateDirectory(hook);
            File.AppendAllText(Path.Combine(hook, "desktop-actions.log"),
                $"jumplist-launch {entry.Title}|{entry.Target}|{entry.Args}\n", new UTF8Encoding(false));
            return;
        }

        _ = AllowSetForegroundWindow(-1);
        var start = new ProcessStartInfo(entry.Target) { UseShellExecute = true };
        if (entry.Args is { Length: > 0 } args)
        {
            start.Arguments = args;
        }

        Process.Start(start)?.Dispose();
    }

    private static string? Hook() =>
        Environment.GetEnvironmentVariable(HookVariable) is { Length: > 0 } hook ? hook : null;

    private static string? Key(CommandContext command) =>
        command.Args?["windowKey"] is JsonValue value && value.TryGetValue<string>(out var key) && key.Length > 0 ? key : null;

    private static long? Hwnd(CommandContext command) =>
        command.Args?["hwnd"] is JsonValue value && value.TryGetValue<long>(out var hwnd) && hwnd != 0 ? hwnd : null;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}

/// <summary>One button per running window or application, labelled with its title and icon, marked while it is active.</summary>
public sealed class WindowListInstance(InstanceRequest request, RunningWindowsPlugin plugin) : ContributionInstance(request)
{
    private readonly string _monitorId = request.Context.MonitorId;
    private volatile bool _thisMonitor = ThisMonitor(request.Settings);
    private volatile string _grouping = Grouping(request.Settings);
    private volatile bool _labels = Labels(request.Settings);

    protected override Task OnCreatedAsync(CancellationToken ct)
    {
        var title = new JsonObject { ["$item"] = "title" };
        var groupArgs = new JsonObject { ["groupKey"] = new JsonObject { ["$item"] = "groupKey" } };
        var button = View.Button("window", "Window",
            View.Invoke("windows.activate", new JsonObject { ["hwnd"] = new JsonObject { ["$item"] = "hwnd" } }),
            label: Prop<string>.Item("label"), icon: Prop<IconRef>.Item("icon"), active: Prop<bool>.Item("active"), "task")
            .WithAttention(Prop<bool>.Item("attention"))
            .WithBadge(Prop<string>.Item("badge"))
            .WithHoverPopup(RunningWindowsPlugin.PreviewId)
            .WithContextAction(View.Invoke("windows.openJumpList", groupArgs));
        var props = new Dictionary<string, JsonNode?>(button.Props!, StringComparer.Ordinal)
        {
            ["automationName"] = title,
            ["tooltip"] = new JsonObject { ["$item"] = "tooltip" },
        };

        Surface.SetView(View.List("windows", "Running windows", Prop<JsonArray>.State("items"), button with { Props = props }));
        plugin.Changed += Show;
        if (plugin.Current is { } current)
        {
            Show(current);
        }

        return Task.CompletedTask;
    }

    protected override Task OnSettingsChangedAsync(JsonObject settings, CancellationToken ct)
    {
        _thisMonitor = ThisMonitor(settings);
        _grouping = Grouping(settings);
        _labels = Labels(settings);
        if (plugin.Current is { } current)
        {
            Show(current);
        }

        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        plugin.Changed -= Show;
        return base.DisposeAsync();
    }

    private void Show(ShellView view)
    {
        var windows = view.RunningWindows()
            .Where(window => !_thisMonitor || string.Equals(window.MonitorId, _monitorId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var labels = _labels;
        Surface.SetState("items", new JsonArray([.. TaskGroups.Build(windows, _grouping).Select(group => (JsonNode)TaskGroups.Item(group, labels))]));
    }

    private static bool ThisMonitor(JsonObject settings) =>
        settings["scope"] is JsonValue value && value.TryGetValue<string>(out var scope) && scope == "thisMonitor";

    private static string Grouping(JsonObject settings) =>
        settings["grouping"] is JsonValue value && value.TryGetValue<string>(out var grouping) && grouping is "always" or "whenFull" or "never"
            ? grouping
            : "always";

    private static bool Labels(JsonObject settings) =>
        settings["labels"] is not JsonValue value || !value.TryGetValue<bool>(out var labels) || labels;
}
