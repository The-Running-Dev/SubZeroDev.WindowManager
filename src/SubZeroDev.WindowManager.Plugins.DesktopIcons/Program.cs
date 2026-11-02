using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
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
/// </summary>
public sealed class DesktopIconsPlugin : Plugin
{
    /// <summary>The test hook directory; with it set, opening is recorded instead of done.</summary>
    public const string HookVariable = "SUBZERODEV_WM_TEST_HOOK";

    /// <summary>The folders shown instead of the real Desktop, separated by <c>;</c>. Honoured only with the test hook.</summary>
    public const string FoldersVariable = "SUBZERODEV_WM_DESKTOP_FOLDERS";

    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);
    private static readonly string[] HiddenExtensions = [".lnk", ".url", ".appref-ms", ".pif"];

    private readonly Lock _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly string? _hook = Environment.GetEnvironmentVariable(HookVariable) is { Length: > 0 } hook ? hook : null;
    private JsonArray _items = [];
    private Timer? _settle;

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
        var folders = Folders();
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

        if (_hook is not null)
        {
            Directory.CreateDirectory(_hook);
            File.AppendAllText(Path.Combine(_hook, "desktop-actions.log"), $"desktop-open {path}\n", new UTF8Encoding(false));
            return Task.FromResult(new CommandResult(null));
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
        return Task.FromResult(new CommandResult(null));
    }

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
            .WithTooltip(Prop<string>.Item("path"));
        var props = new Dictionary<string, JsonNode?>(icon.Props!, StringComparer.Ordinal)
        {
            ["automationName"] = new JsonObject { ["$item"] = "name" },
        };

        Surface.SetView(View.List("icons", "Desktop", Prop<JsonArray>.State("items"), icon with { Props = props },
            StackOrientation.Vertical));
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
