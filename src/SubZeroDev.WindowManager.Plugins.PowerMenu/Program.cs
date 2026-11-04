using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Sdk;

[assembly: SupportedOSPlatform("windows")]

namespace SubZeroDev.WindowManager.Plugins.PowerMenu;

public static class Program
{
    public static Task<int> Main(string[] args) => PluginRuntime.RunAsync<PowerMenuPlugin>(args);
}

/// <summary>One menu entry: what the menu shows and what selecting it starts.</summary>
/// <param name="Title">The label.</param>
/// <param name="Target">The program or document the entry stands for.</param>
/// <param name="Args">Its command line, when it has one.</param>
/// <param name="Icon">The file the icon comes from, when known.</param>
/// <param name="Launch">What the shell is asked to open: the shortcut itself for a WinX entry (so its arguments, working folder and "run as administrator" flag all apply), else the target.</param>
public sealed record MenuEntry(string Title, string Target, string? Args, string? Icon, string Launch);

/// <summary>
/// The Win+X menu: a <c>menu</c> popup listing the entries of Windows' own WinX folders (so the user's
/// customisations show), or a built-in list when those folders cannot be read. It reads nothing from the window
/// model. A selected entry is opened through the shell, which is given foreground rights first so the program it
/// starts can come to the front. Under the test hook the folder is <c>winx</c> in the hook directory and a launch is
/// only recorded in <c>desktop-actions.log</c> (I38: a test never starts a program).
/// </summary>
public sealed class PowerMenuPlugin : Plugin
{
    public const string HookVariable = "SUBZERODEV_WM_TEST_HOOK";
    public const string PopupId = "menu";
    public const string ButtonId = "menu";

    private static readonly Regex OrderPrefix = new(@"^\s*\d+\s*-\s*", RegexOptions.Compiled);

    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly Lock _gate = new();
    private IReadOnlyList<MenuEntry> _entries = [];
    private PopupSurface _popup = null!;
    private volatile string? _buttonSurface;
    private bool _open;

    protected override Task OnStartAsync(CancellationToken ct)
    {
        _popup = Context.Popups.Get(PopupId);
        ShowMenu(string.Empty);
        Context.Commands.Register("powermenu.open", async (command, token) =>
        {
            var toggle = command.Trigger is CommandTrigger.Chord or CommandTrigger.Keyboard;
            await OpenMenuAsync(null, toggle, token).ConfigureAwait(false);
            return new CommandResult(null);
        });
        return Task.CompletedTask;
    }

    protected override ContributionInstance CreateInstance(InstanceRequest request) => new ButtonInstance(request, this);

    protected override Task OnPopupClosedAsync(PopupSurface popup, PopupCloseReason reason, CancellationToken ct)
    {
        if (popup.PopupId == PopupId)
        {
            _open = false;
        }

        return Task.CompletedTask;
    }

    protected override async Task OnPopupEventAsync(PopupSurface popup, ElementEvent e, CancellationToken ct)
    {
        if (e.Kind != ElementEventKind.Invoked || !e.ElementId.StartsWith("entry", StringComparison.Ordinal)
            || !int.TryParse(e.ElementId.AsSpan(5), out var index))
        {
            return;
        }

        MenuEntry entry;
        lock (_gate)
        {
            if (index < 0 || index >= _entries.Count)
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
            ShowMenu($"{entry.Title} did not start: {ex.Message}");
            return;
        }

        await _popup.CloseAsync(ct).ConfigureAwait(false);
    }

    internal void SetButton(string? surfaceId) => _buttonSurface = surfaceId;

    internal Task OpenFromButtonAsync(string surfaceId, CancellationToken ct) =>
        OpenMenuAsync(new PopupAnchor(new ElementAnchor(surfaceId, ButtonId), null), toggle: false, ct);

    /// <summary>Reads the folders afresh (a change the user made shows at once) and opens the menu; the chord closes an open one.</summary>
    internal async Task OpenMenuAsync(PopupAnchor? anchor, bool toggle, CancellationToken ct)
    {
        await _work.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (toggle && _open)
            {
                await _popup.CloseAsync(ct).ConfigureAwait(false);
                return;
            }

            var entries = await Task.Run(ReadEntries, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _entries = entries;
            }

            ShowMenu(string.Empty);
            _open = true;
            var result = await _popup.OpenAsync(anchor ?? DefaultAnchor(), ct).ConfigureAwait(false);
            if (result.Error is not null)
            {
                _open = false;
            }
        }
        finally
        {
            _work.Release();
        }
    }

    private PopupAnchor DefaultAnchor() => _buttonSurface is { } surface
        ? new PopupAnchor(new ElementAnchor(surface, ButtonId), null)
        : new PopupAnchor(null, MonitorAnchor.Active);

    private void ShowMenu(string message)
    {
        IReadOnlyList<MenuEntry> entries;
        lock (_gate)
        {
            entries = _entries;
        }

        var buttons = entries.Select((entry, index) =>
        {
            var id = "entry" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Prop<IconRef>? icon = null;
            if (entry.Icon is { } file)
            {
                icon = (IconRef)new IconRef.App(file, null);
            }

            return View.Button(id, entry.Title, View.Event(id), entry.Title, icon, null, "menu-item");
        });
        _popup.SetView(View.Stack("winx", StackOrientation.Vertical,
            [.. buttons, View.Text("message", Prop<string>.State("message"), "notice")]));
        _popup.SetState("message", message);
    }

    // ---- The entries ------------------------------------------------------------------------------------------

    private IReadOnlyList<MenuEntry> ReadEntries()
    {
        foreach (var folder in Folders())
        {
            try
            {
                var entries = ReadFolder(folder);
                if (entries.Count > 0)
                {
                    return entries;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
            {
                Context.Log.LogWarning("The WinX folder {Folder} could not be read: {Reason}", folder, e.Message);
            }
        }

        return BuiltIn();
    }

    /// <summary>The folders to try in order: the user's own, then the default profile's. Under the test hook only the hook's.</summary>
    private static IEnumerable<string> Folders()
    {
        if (Environment.GetEnvironmentVariable(HookVariable) is { Length: > 0 } hook)
        {
            yield return Path.Combine(hook, "winx");
            yield break;
        }

        const string relative = @"Microsoft\Windows\WinX";
        if (Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local)
        {
            yield return Path.Combine(local, relative);
        }

        if (Environment.GetEnvironmentVariable("SystemDrive") is { Length: > 0 } drive)
        {
            yield return Path.Combine(drive + Path.DirectorySeparatorChar, "Users", "Default", "AppData", "Local", relative);
        }
    }

    /// <summary>
    /// The shortcuts in <c>Group3</c>, then <c>Group2</c>, then <c>Group1</c> (the order Windows shows them in), each
    /// group by file name. An empty or missing folder gives an empty list.
    /// </summary>
    internal static List<MenuEntry> ReadFolder(string folder)
    {
        var entries = new List<MenuEntry>();
        if (!Directory.Exists(folder))
        {
            return entries;
        }

        var groups = Directory.EnumerateDirectories(folder).OrderByDescending(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            foreach (var file in Directory.EnumerateFiles(group, "*.lnk").OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            {
                if (ReadShortcut(file) is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }

        return entries;
    }

    private static MenuEntry? ReadShortcut(string file)
    {
        var title = OrderPrefix.Replace(Path.GetFileNameWithoutExtension(file), string.Empty).Trim();
        if (title.Length == 0)
        {
            return null;
        }

        string target = string.Empty, args = string.Empty, icon = string.Empty;
        try
        {
            if (Type.GetTypeFromProgID("WScript.Shell") is { } type && Activator.CreateInstance(type) is { } shell)
            {
                try
                {
                    dynamic link = ((dynamic)shell).CreateShortcut(file);
                    target = (string?)link.TargetPath ?? string.Empty;
                    args = (string?)link.Arguments ?? string.Empty;
                    icon = (string?)link.IconLocation ?? string.Empty;
                }
                finally
                {
                    Marshal.ReleaseComObject(shell);
                }
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // The shortcut still starts through the shell; only its details are unknown.
        }

        // IconLocation reads "path,index".
        var iconPath = icon.Split(',')[0].Trim();
        var shown = iconPath.Length > 0 ? Environment.ExpandEnvironmentVariables(iconPath) : target;
        return new MenuEntry(title, target.Length > 0 ? target : file, args.Length > 0 ? args : null, shown.Length > 0 ? shown : null, file);
    }

    /// <summary>The list used when the WinX folders give nothing: the same tools Windows offers on Win+X.</summary>
    internal static IReadOnlyList<MenuEntry> BuiltIn() =>
    [
        Tool("Programs and Features", "appwiz.cpl"),
        Tool("Power Options", "powercfg.cpl"),
        Tool("Event Viewer", "eventvwr.msc"),
        Tool("System", "ms-settings:about"),
        Tool("Device Manager", "devmgmt.msc"),
        Tool("Network Connections", "ncpa.cpl"),
        Tool("Disk Management", "diskmgmt.msc"),
        Tool("Computer Management", "compmgmt.msc"),
        Tool("Terminal", "wt.exe"),
        Tool("Task Manager", "taskmgr.exe"),
        Tool("Settings", "ms-settings:"),
        Tool("File Explorer", "explorer.exe"),
        Tool("Run", "rundll32.exe", "shell32.dll,#61"),
    ];

    private static MenuEntry Tool(string title, string target, string? args = null) => new(title, target, args, null, target);

    // ---- Launching --------------------------------------------------------------------------------------------

    private static void Launch(MenuEntry entry)
    {
        if (Environment.GetEnvironmentVariable(HookVariable) is { Length: > 0 } hook)
        {
            Directory.CreateDirectory(hook);
            var line = $"winx-launch {Path.GetFileName(entry.Launch)}{(entry.Args is null ? string.Empty : " " + entry.Args)}\n";
            File.AppendAllText(Path.Combine(hook, "desktop-actions.log"), line, new UTF8Encoding(false));
            return;
        }

        AllowSetForegroundWindow(AsfwAny);
        var shortcut = entry.Launch.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
        using var process = Process.Start(new ProcessStartInfo(entry.Launch)
        {
            UseShellExecute = true,
            Arguments = shortcut ? string.Empty : entry.Args ?? string.Empty,
        });
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}

/// <summary>The button in a panel that opens the menu.</summary>
public sealed class ButtonInstance(InstanceRequest request, PowerMenuPlugin plugin) : ContributionInstance(request)
{
    protected override Task OnCreatedAsync(CancellationToken ct)
    {
        Surface.SetView(View.Button(PowerMenuPlugin.ButtonId, "Power user menu", View.Event("open"),
            "Tools", new IconRef.Named("powermenu"), null, "indicator")
            .WithTooltip("Power user menu (Win+X)"));
        plugin.SetButton(Surface.SurfaceId);
        return Task.CompletedTask;
    }

    protected override Task OnEventAsync(ElementEvent e, CancellationToken ct) =>
        e.ElementId == PowerMenuPlugin.ButtonId && e.Kind == ElementEventKind.Invoked
            ? plugin.OpenFromButtonAsync(Surface.SurfaceId, ct)
            : Task.CompletedTask;

    public override ValueTask DisposeAsync()
    {
        plugin.SetButton(null);
        return base.DisposeAsync();
    }
}
