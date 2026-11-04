using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Sdk;

[assembly: SupportedOSPlatform("windows")]

namespace SubZeroDev.WindowManager.Plugins.DesktopIcons;

public static class Program
{
    public static Task<int> Main(string[] args) => PluginRuntime.RunAsync<DesktopIconsPlugin>(args);
}

/// <summary>
/// The <c>icons</c> gadget: the user's and the public Desktop folders, merged and sorted as Explorer sorts by
/// name (folders first). The folders are watched, so an added or deleted file shows within a second.
/// <c>desktop.open { path }</c> opens an item through ShellExecute, after letting it take the foreground.
/// <c>desktop.contextMenu { path?, x, y }</c> opens a menu of file operations for an item, or for the Desktop itself when
/// no path is given: open, rename, delete (to the Recycle Bin, never permanently), copy and paste, and a new folder.
/// </summary>
public sealed class DesktopIconsPlugin : Plugin
{
    /// <summary>The test hook directory; with it set, opening is recorded instead of done.</summary>
    public const string HookVariable = "SUBZERODEV_WM_TEST_HOOK";

    /// <summary>The folders shown instead of the real Desktop, separated by <c>;</c>. Honoured only with the test hook.</summary>
    public const string FoldersVariable = "SUBZERODEV_WM_DESKTOP_FOLDERS";

    public const string MenuId = "menu";

    public const string RenameId = "rename";

    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);
    private static readonly string[] HiddenExtensions = [".lnk", ".url", ".appref-ms", ".pif"];

    private readonly Lock _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly string? _hook = Environment.GetEnvironmentVariable(HookVariable) is { Length: > 0 } hook ? hook : null;
    private JsonArray _items = [];
    private Timer? _settle;
    private string[] _folders = [];
    private PopupSurface _menu = null!;
    private PopupSurface _rename = null!;
    private string? _target;
    private List<string> _copied = [];

    internal event EventHandler? Changed;

    internal JsonArray Items
    {
        get
        {
            lock (_gate)
            {
                return (JsonArray)_items.DeepClone();
            }
        }
    }

    protected override Task OnStartAsync(CancellationToken ct)
    {
        Context.Commands.Register("desktop.open", OpenAsync);
        Context.Commands.Register("desktop.contextMenu", ContextMenuAsync);
        _menu = Context.Popups.Get(MenuId);
        _rename = Context.Popups.Get(RenameId);
        _rename.SetView(RenameView());
        var folders = _folders = Folders();
        _settle = new Timer(_ => Rescan(folders));
        foreach (var folder in folders.Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes,
            };
            FileSystemEventHandler touched = (_, _) => _settle.Change(Settle, Timeout.InfiniteTimeSpan);
            watcher.Created += touched;
            watcher.Deleted += touched;
            watcher.Changed += touched;
            watcher.Renamed += (_, e) => touched(watcher, e);
            watcher.Error += (_, _) => _settle.Change(Settle, Timeout.InfiniteTimeSpan);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }

        Rescan(folders);
        return Task.CompletedTask;
    }

    protected override Task OnStopAsync(CancellationToken ct)
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _settle?.Dispose();
        return Task.CompletedTask;
    }

    protected override ContributionInstance CreateInstance(InstanceRequest request) => new DesktopIconsInstance(request, this);

    /// <summary>The user's Desktop, then the public one; or the test folders.</summary>
    private string[] Folders()
    {
        if (_hook is not null)
        {
            return Environment.GetEnvironmentVariable(FoldersVariable) is { Length: > 0 } list
                ? list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
        }

        return [.. new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            }.Where(folder => folder.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private void Rescan(string[] folders)
    {
        var items = new JsonArray([.. Scan(folders).Select(entry => (JsonNode)new JsonObject
        {
            ["key"] = entry.Path,
            ["path"] = entry.Path,
            ["name"] = entry.Name,
            ["folder"] = entry.Folder,
            ["icon"] = new JsonObject { ["app"] = new JsonObject { ["exe"] = entry.Path } },
        })]);
        lock (_gate)
        {
            if (JsonNode.DeepEquals(items, _items))
            {
                return;
            }

            _items = items;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The visible entries of every folder, folders first, then by name as the user's culture sorts it.</summary>
    internal static List<DesktopEntry> Scan(IEnumerable<string> folders)
    {
        var entries = new List<DesktopEntry>();
        foreach (var folder in folders)
        {
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(folder).EnumerateFileSystemInfos();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var child in children)
            {
                try
                {
                    if ((child.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        continue;
                    }

                    var isFolder = (child.Attributes & FileAttributes.Directory) != 0;
                    entries.Add(new DesktopEntry(child.FullName, NameOf(child.Name, isFolder), isFolder));
                }
                catch (IOException)
                {
                    // Deleted while listing; the watcher brings the next scan.
                }
            }
        }

        entries.Sort((a, b) => a.Folder != b.Folder
            ? (a.Folder ? -1 : 1)
            : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase) is var byName and not 0
                ? byName
                : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    /// <summary>The name shown: a shortcut's without its extension, as Explorer always shows it.</summary>
    internal static string NameOf(string fileName, bool isFolder)
    {
        var extension = Path.GetExtension(fileName);
        return !isFolder && HiddenExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) && extension.Length < fileName.Length
            ? fileName[..^extension.Length]
            : fileName;
    }

    private Task<CommandResult> OpenAsync(CommandContext command, CancellationToken ct)
    {
        var path = command.Args?["path"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;
        if (path.Length == 0)
        {
            throw new ArgumentException("desktop.open needs { path: string }.");
        }

        Open(path);
        return Task.FromResult(new CommandResult(null));
    }

    private void Open(string path)
    {
        if (_hook is not null)
        {
            Directory.CreateDirectory(_hook);
            File.AppendAllText(Path.Combine(_hook, "desktop-actions.log"), $"desktop-open {path}\n", new UTF8Encoding(false));
            return;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException($"'{path}' is no longer on the Desktop.", path);
        }

        AllowSetForegroundWindow(AsfwAny);
        using var process = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Verb = "open",
            WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
        });
    }

    // ---- The context menu -------------------------------------------------------------------------------------

    /// <summary>
    /// Opens the menu for an item (<c>path</c>), or for the Desktop when there is none. <c>x</c> and <c>y</c> are accepted for
    /// the contract, but the menu opens at the pointer, which is where the click that asks for it happened.
    /// </summary>
    private async Task<CommandResult> ContextMenuAsync(CommandContext command, CancellationToken ct)
    {
        var path = command.Args?["path"] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
        if (path is not null && !IsOnDesktop(path))
        {
            throw new ArgumentException("That item is not on the Desktop.");
        }

        lock (_gate)
        {
            _target = path;
        }

        _menu.SetView(MenuView(path));
        var opened = await _menu.OpenAsync(new PopupAnchor(null, MonitorAnchor.Pointer), ct).ConfigureAwait(false);
        if (opened.Error is { } error)
        {
            throw new InvalidOperationException($"The menu could not open: {error.Detail}");
        }

        return new CommandResult(null);
    }

    private bool IsOnDesktop(string path)
    {
        var parent = Path.GetDirectoryName(path);
        return parent is not null
            && _folders.Any(folder => string.Equals(Path.TrimEndingDirectorySeparator(folder), parent, StringComparison.OrdinalIgnoreCase))
            && (File.Exists(path) || Directory.Exists(path));
    }

    /// <summary>The entries: an item's own operations, or the Desktop\x27s; paste only once something was copied.</summary>
    private ViewNode MenuView(string? path)
    {
        var entries = new List<(string Id, string Label)>();
        if (path is not null)
        {
            entries.Add(("open", "Open"));
            entries.Add(("rename", "Rename"));
            entries.Add(("copy", "Copy"));
        }
        else
        {
            entries.Add(("newFolder", "New folder"));
        }

        lock (_gate)
        {
            if (_copied.Count > 0)
            {
                entries.Add(("paste", "Paste"));
            }
        }

        if (path is not null)
        {
            entries.Add(("delete", "Delete"));
        }

        return View.Stack("menu", StackOrientation.Vertical,
            [.. entries.Select(entry => View.Button(entry.Id, entry.Label, View.Event(entry.Id), entry.Label, null, null, "menu-item"))]);
    }

    private static ViewNode RenameView() =>
        View.Stack("rename", StackOrientation.Vertical,
            View.Text("heading", "Rename", "menu-heading"),
            View.TextInput("name", "New name", Prop<string>.State("name"), "New name"),
            View.Text("problem", Prop<string>.State("problem"), "error"));

    protected override async Task OnPopupEventAsync(PopupSurface popup, ElementEvent e, CancellationToken ct)
    {
        string? target;
        lock (_gate)
        {
            target = _target;
        }

        try
        {
            if (popup.PopupId == MenuId && e.Kind == ElementEventKind.Invoked)
            {
                await ChooseAsync(popup, e.ElementId, target, ct).ConfigureAwait(false);
            }
            else if (popup.PopupId == RenameId && e is { ElementId: "name", Kind: ElementEventKind.TextSubmitted } && target is not null)
            {
                var typed = e.Value is JsonValue typedValue && typedValue.TryGetValue<string>(out var typedText) ? typedText : string.Empty;
                if (RenameTo(target, typed) is { } problem)
                {
                    popup.SetState("problem", problem);
                    return;
                }

                await popup.CloseAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Context.Log.LogWarning("The desktop menu entry {Entry} failed: {Reason}", e.ElementId, ex.Message);
        }
    }

    private async Task ChooseAsync(PopupSurface menu, string entry, string? target, CancellationToken ct)
    {
        if (entry == "rename" && target is not null)
        {
            _rename.SetState("name", NameOf(Path.GetFileName(target), Directory.Exists(target)));
            _rename.SetState("problem", string.Empty);
            await menu.CloseAsync(ct).ConfigureAwait(false);
            var opened = await _rename.OpenAsync(new PopupAnchor(null, MonitorAnchor.Pointer), ct).ConfigureAwait(false);
            if (opened.Error is { } error)
            {
                Context.Log.LogWarning("The rename box could not open: {Reason}", error.Detail);
            }

            return;
        }

        await menu.CloseAsync(ct).ConfigureAwait(false);
        switch (entry)
        {
            case "open" when target is not null:
                Open(target);
                break;
            case "copy" when target is not null:
                lock (_gate)
                {
                    _copied = [target];
                }

                break;
            case "paste" when _folders.FirstOrDefault(Directory.Exists) is { } folder:
                string[] sources;
                lock (_gate)
                {
                    sources = [.. _copied];
                }

                foreach (var source in sources)
                {
                    CopyInto(source, folder);
                }

                break;
            case "newFolder" when _folders.FirstOrDefault(Directory.Exists) is { } parent:
                _ = Directory.CreateDirectory(Path.Combine(parent, UniqueName(parent, "New folder", string.Empty, copy: false)));
                break;
            case "delete" when target is not null:
                Recycle(target);
                break;
        }
    }

    /// <summary>
    /// Renames an item to what was typed (a shortcut\x27s hidden extension is kept). Returns what is wrong with the name, or
    /// null once it is renamed.
    /// </summary>
    internal static string? RenameTo(string path, string typed)
    {
        var name = typed.Trim();
        var isFolder = Directory.Exists(path);
        var current = Path.GetFileName(path);
        var hidden = isFolder ? string.Empty : Path.GetExtension(current);
        if (hidden.Length > 0 && !HiddenExtensions.Contains(hidden, StringComparer.OrdinalIgnoreCase))
        {
            hidden = string.Empty;
        }

        if (name.Length == 0 || name is "." or ".." || name.EndsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "A name cannot be empty, end in a dot or contain \\ / : * ? \" < > |.";
        }

        var fileName = hidden.Length > 0 && !name.EndsWith(hidden, StringComparison.OrdinalIgnoreCase) ? name + hidden : name;
        var destination = Path.Combine(Path.GetDirectoryName(path)!, fileName);
        if (string.Equals(destination, path, StringComparison.Ordinal))
        {
            return null;
        }

        if ((File.Exists(destination) || Directory.Exists(destination)) && !string.Equals(destination, path, StringComparison.OrdinalIgnoreCase))
        {
            return "That name is already used on the Desktop.";
        }

        if (isFolder)
        {
            Directory.Move(path, destination);
        }
        else
        {
            File.Move(path, destination);
        }

        return null;
    }

    /// <summary>A name not yet used in <paramref name="folder"/>: <c>New folder</c>, <c>New folder (2)</c>; a copy adds <c> - Copy</c> first.</summary>
    internal static string UniqueName(string folder, string stem, string extension, bool copy)
    {
        var first = copy ? stem + " - Copy" : stem;
        var name = first + extension;
        for (var number = 2; File.Exists(Path.Combine(folder, name)) || Directory.Exists(Path.Combine(folder, name)); number++)
        {
            name = first + " (" + number.ToString(CultureInfo.InvariantCulture) + ")" + extension;
        }

        return name;
    }

    /// <summary>Copies a file or folder into <paramref name="folder"/> under a name that is free; a copy beside its source is named <c> - Copy</c>.</summary>
    internal static string CopyInto(string source, string folder)
    {
        var isFolder = Directory.Exists(source);
        if (!isFolder && !File.Exists(source))
        {
            throw new FileNotFoundException("The copied item is gone.", source);
        }

        var stem = isFolder ? Path.GetFileName(source) : Path.GetFileNameWithoutExtension(source);
        var extension = isFolder ? string.Empty : Path.GetExtension(source);
        var taken = File.Exists(Path.Combine(folder, stem + extension)) || Directory.Exists(Path.Combine(folder, stem + extension));
        var destination = Path.Combine(folder, UniqueName(folder, stem, extension, copy: taken));
        if (isFolder)
        {
            CopyFolder(source, destination);
        }
        else
        {
            File.Copy(source, destination);
        }

        return destination;
    }

    private static void CopyFolder(string source, string destination)
    {
        if (string.Equals(Path.TrimEndingDirectorySeparator(destination), Path.TrimEndingDirectorySeparator(source), StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A folder cannot be copied into itself.");
        }

        _ = Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyFolder(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    /// <summary>
    /// Sends an item to the Recycle Bin, which keeps it restorable; it is never removed permanently. If the shell cannot recycle it
    /// the shell asks first, and a refusal leaves it where it is. Under the test hook the item moves to <c>recycle-bin</c> there instead.
    /// </summary>
    private void Recycle(string path)
    {
        if (_hook is not null)
        {
            var bin = Path.Combine(_hook, "recycle-bin");
            _ = Directory.CreateDirectory(bin);
            var isFolder = Directory.Exists(path);
            var name = UniqueName(bin, isFolder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path),
                isFolder ? string.Empty : Path.GetExtension(path), copy: false);
            if (isFolder)
            {
                Directory.Move(path, Path.Combine(bin, name));
            }
            else
            {
                File.Move(path, Path.Combine(bin, name));
            }

            File.AppendAllText(Path.Combine(_hook, "desktop-actions.log"), $"desktop-recycle {path}\n", new UTF8Encoding(false));
            return;
        }

        var operation = new ShFileOperation
        {
            Function = FoDelete,
            From = path + "\0",
            Flags = FofAllowUndo | FofWantNukeWarning,
        };
        var code = SHFileOperation(ref operation);
        if (code != 0 || operation.AnyOperationsAborted)
        {
            throw new IOException($"The Recycle Bin did not take '{path}' (code {code.ToString(CultureInfo.InvariantCulture)}); it was left where it is.");
        }
    }

    private const uint FoDelete = 0x0003;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofWantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOperation
    {
        public nint Window;
        public uint Function;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string From;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;
        public nint NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Title;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref ShFileOperation operation);


    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}

internal sealed record DesktopEntry(string Path, string Name, bool Folder);

/// <summary>The icons in a wrapping grid; a click focuses one, a double click or Enter opens it.</summary>
public sealed class DesktopIconsInstance(InstanceRequest request, DesktopIconsPlugin plugin) : ContributionInstance(request)
{
    protected override Task OnCreatedAsync(CancellationToken ct)
    {
        var icon = View.Button("icon", "Desktop item",
            View.Invoke("desktop.open", new JsonObject { ["path"] = new JsonObject { ["$item"] = "path" } }),
            Prop<string>.Item("name"), Prop<IconRef>.Item("icon"), classes: ["desktop"])
            .WithDoubleClick()
            .WithContextAction(View.Invoke("desktop.contextMenu", new JsonObject { ["path"] = new JsonObject { ["$item"] = "path" } }))
            .WithTooltip(Prop<string>.Item("path"));
        var props = new Dictionary<string, JsonNode?>(icon.Props!, StringComparer.Ordinal)
        {
            ["automationName"] = new JsonObject { ["$item"] = "name" },
        };

        Surface.SetView(View.List("icons", "Desktop", Prop<JsonArray>.State("items"), icon with { Props = props },
            StackOrientation.Vertical)
            .WithContextAction(View.Invoke("desktop.contextMenu", new JsonObject())));
        plugin.Changed += OnChanged;
        Surface.SetState("items", plugin.Items);
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        plugin.Changed -= OnChanged;
        return base.DisposeAsync();
    }

    private void OnChanged(object? sender, EventArgs e) => Surface.SetState("items", plugin.Items);
}
