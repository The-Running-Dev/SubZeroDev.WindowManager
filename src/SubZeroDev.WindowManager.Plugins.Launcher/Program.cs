using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Sdk;

[assembly: SupportedOSPlatform("windows")]

namespace SubZeroDev.WindowManager.Plugins.Launcher;

public static class Program
{
    public static Task<int> Main(string[] args) => PluginRuntime.RunAsync<LauncherPlugin>(args);
}

/// <summary>
/// Publishes <c>applications.index</c>, serves <c>launcher.open</c>, <c>launcher.close</c> and
/// <c>applications.launch</c>, and owns the <c>search</c> popup: a query box over the index, or a command
/// line in <c>run</c> mode. The index is built at start and rebuilt in the background on every open.
/// </summary>
public sealed class LauncherPlugin : Plugin
{
    public const string PopupId = "search";
    public const string IndexFeed = "applications.index";

    /// <summary>How long a started program gets to show a window before the popup closes behind it.</summary>
    private static readonly TimeSpan ShowWindowWait = TimeSpan.FromSeconds(3);

    private readonly Lock _gate = new();
    private IReadOnlyList<AppEntry> _index = [];
    private IFeedPublisher? _publisher;
    private PopupSurface _popup = null!;
    private string _mode = "search";
    private string _query = string.Empty;
    private int _refreshing;

    protected override async Task OnStartAsync(CancellationToken ct)
    {
        _publisher = Context.Feeds.Publisher(IndexFeed);
        _popup = Context.Popups.Get(PopupId);
        Context.Commands.Register("launcher.open", OpenAsync);
        Context.Commands.Register("launcher.close", CloseAsync);
        Context.Commands.Register("applications.launch", LaunchAsync);
        ShowMode("search");
        await RefreshAsync().ConfigureAwait(false);
    }

    protected override ContributionInstance CreateInstance(InstanceRequest request) => new LauncherInstance(request);

    protected override Task OnPopupOpenedAsync(PopupSurface popup, PopupTrigger trigger, CancellationToken ct)
    {
        _ = RefreshAsync();
        return Task.CompletedTask;
    }

    /// <summary>A closed popup starts afresh in search mode next time.</summary>
    protected override Task OnPopupClosedAsync(PopupSurface popup, PopupCloseReason reason, CancellationToken ct)
    {
        ShowMode("search");
        return Task.CompletedTask;
    }

    protected override Task OnPopupEventAsync(PopupSurface popup, ElementEvent e, CancellationToken ct)
    {
        switch (e)
        {
            case { ElementId: "query", Kind: ElementEventKind.TextChanged }:
                ShowResults(Text(e.Value));
                break;
            case { ElementId: "results", Kind: ElementEventKind.SelectionChanged, ItemKey: { } key }:
                popup.SetState("selected", key);
                break;
            case { ElementId: "command", Kind: ElementEventKind.TextSubmitted }:
                return RunAsync(Text(e.Value), ct);
        }

        return Task.CompletedTask;
    }

    private async Task<CommandResult> OpenAsync(CommandContext command, CancellationToken ct)
    {
        var mode = command.Args?["mode"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "search";
        if (mode is not ("search" or "run"))
        {
            throw new ArgumentException("mode must be 'search' or 'run'.");
        }

        ShowMode(mode);
        var opened = await _popup.OpenAsync(new PopupAnchor(null, MonitorAnchor.Pointer), ct).ConfigureAwait(false);
        if (opened.Error is { } error)
        {
            throw new InvalidOperationException($"The launcher could not open: {error.Detail}");
        }

        return new CommandResult(null);
    }

    private async Task<CommandResult> CloseAsync(CommandContext command, CancellationToken ct)
    {
        var closed = await _popup.CloseAsync(ct).ConfigureAwait(false);
        if (closed.Error is { } error)
        {
            throw new InvalidOperationException($"The launcher could not close: {error.Detail}");
        }

        return new CommandResult(null);
    }

    /// <summary>
    /// Starts an executable (with optional arguments) or a packaged app by AUMID, lets it take the
    /// foreground, then closes the popup. A missing executable fails without retry.
    /// </summary>
    private async Task<CommandResult> LaunchAsync(CommandContext command, CancellationToken ct)
    {
        var exe = Text(command.Args?["exe"]);
        var aumid = Text(command.Args?["aumid"]);
        var args = Text(command.Args?["args"]);
        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        if (aumid.Length > 0)
        {
            var manager = (Native.IApplicationActivationManager)new Native.ApplicationActivationManager();
            manager.ActivateApplication(aumid, args.Length > 0 ? args : null, 0, out _);
        }
        else if (exe.Length > 0)
        {
            if (!File.Exists(exe))
            {
                throw new FileNotFoundException($"'{exe}' no longer exists.", exe);
            }

            using var process = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
            });
            await WaitForWindowAsync(process, ct).ConfigureAwait(false);
        }
        else
        {
            throw new ArgumentException("Give exe or aumid.");
        }

        await _popup.CloseAsync(ct).ConfigureAwait(false);
        return new CommandResult(null);
    }

    /// <summary>Runs a command line as Win+R did: the first word (or quoted part) is the program, the rest its arguments.</summary>
    private async Task RunAsync(string line, CancellationToken ct)
    {
        line = line.Trim();
        if (line.Length == 0)
        {
            return;
        }

        var (file, args) = Split(line);
        try
        {
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            using var process = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
            _popup.SetState("message", string.Empty);
            await WaitForWindowAsync(process, ct).ConfigureAwait(false);
            await _popup.CloseAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            _popup.SetState("message", $"Could not run '{file}': {e.Message}");
        }
    }

    internal static (string File, string Args) Split(string line)
    {
        if (line.StartsWith('"'))
        {
            var end = line.IndexOf('"', 1);
            return end < 0 ? (line[1..], string.Empty) : (line[1..end], line[(end + 1)..].Trim());
        }

        var space = line.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (line, string.Empty) : (line[..space], line[(space + 1)..].Trim());
    }

    private static async Task WaitForWindowAsync(Process? process, CancellationToken ct)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => process.WaitForInputIdle((int)ShowWindowWait.TotalMilliseconds), ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // A console program or one that already exited has no input queue to wait for.
        }
    }

    private void ShowMode(string mode)
    {
        lock (_gate)
        {
            _mode = mode;
            _query = string.Empty;
        }

        _popup.SetView(mode == "run"
            ? View.Stack("launcher", StackOrientation.Vertical,
                View.TextInput("command", "Command line", Prop<string>.State("command"), "Type a program, folder or document to open"),
                View.Text("message", Prop<string>.State("message"), "notice"))
            : View.Stack("launcher", StackOrientation.Vertical,
                View.TextInput("query", "Search applications", Prop<string>.State("query"), "Type to search applications", navigates: "results"),
                View.List("results", "Applications", Prop<JsonArray>.State("items"), ItemTemplate(),
                    StackOrientation.Vertical, ListSelection.Single, Prop<string>.State("selected"))));
        _popup.SetState("query", string.Empty);
        _popup.SetState("command", string.Empty);
        _popup.SetState("message", string.Empty);
        _popup.SetState("items", new JsonArray());
        _popup.SetState("selected", null);
    }

    private static ViewNode ItemTemplate()
    {
        var launch = View.Invoke("applications.launch", new JsonObject
        {
            ["exe"] = new JsonObject { ["$item"] = "exe" },
            ["aumid"] = new JsonObject { ["$item"] = "aumid" },
            ["args"] = new JsonObject { ["$item"] = "args" },
        });
        return View.Button("app", "Application", launch, Prop<string>.Item("name"), Prop<IconRef>.Item("icon"), null, "result");
    }

    private void ShowResults(string query)
    {
        IReadOnlyList<AppEntry> index;
        lock (_gate)
        {
            if (_mode != "search")
            {
                return;
            }

            _query = query;
            index = _index;
        }

        var matches = AppIndex.Search(index, query);
        var items = new JsonArray();
        foreach (var entry in matches)
        {
            var item = entry.ToJson();
            item["icon"] = new JsonObject { ["app"] = new JsonObject { ["exe"] = entry.Exe, ["aumid"] = entry.Aumid } };
            items.Add(item);
        }

        _popup.SetState("query", query);
        _popup.SetState("items", items);
        _popup.SetState("selected", matches.Count > 0 ? matches[0].Key : null);
    }

    /// <summary>Rebuilds the index unless a rebuild is already running, then publishes it and refreshes the results shown.</summary>
    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var index = await AppIndex.BuildAsync(AppIndex.StartMenuFolders()).ConfigureAwait(false);
            string query;
            lock (_gate)
            {
                _index = index;
                query = _query;
            }

            _publisher?.Publish(new JsonArray([.. index.Select(entry => (JsonNode)entry.ToJson())]));
            if (query.Length > 0)
            {
                ShowResults(query);
            }
        }
        catch (Exception e)
        {
            Context.Log.LogWarning("Indexing applications failed: {Message}", e.Message);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;
}

/// <summary>A panel button that opens the <c>search</c> popup above or below itself.</summary>
public sealed class LauncherInstance(InstanceRequest request) : ContributionInstance(request)
{
    protected override Task OnCreatedAsync(CancellationToken ct)
    {
        Surface.SetView(View.Button("open", "Open the launcher", View.Popup(LauncherPlugin.PopupId),
            "Apps", new IconRef.Named("launcher"), null, "launcher"));
        return Task.CompletedTask;
    }
}
