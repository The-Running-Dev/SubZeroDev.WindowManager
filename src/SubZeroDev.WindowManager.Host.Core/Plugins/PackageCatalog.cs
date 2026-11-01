using System.Text.Json;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Protocol;
using LogLevel = SubZeroDev.WindowManager.Host.Core.Diagnostics.LogLevel;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>
/// Plugin packages found in <c>&lt;content&gt;\plugins</c> (built-in) and <c>&lt;data&gt;\plugins</c> (user).
/// A package is a folder named after its manifest id holding <c>plugin.json</c> and the entry it names.
/// A valid user copy overrides the built-in one. The user folder is watched; a change is reported
/// after 300 ms of quiet so a copy in progress is read once, whole.
/// </summary>
public sealed class PackageCatalog : IPackageCatalog, IDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly string _builtInRoot;
    private readonly string _userRoot;
    private readonly IHostLog _log;
    private readonly Lock _gate = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _rescan;
    private Dictionary<string, (PackageRecord Record, PluginManifest? Manifest)> _plugins = new(StringComparer.Ordinal);

    public PackageCatalog(Locations locations, IHostLog log, bool watch = true)
    {
        _builtInRoot = Path.Combine(locations.ContentRoot, "plugins");
        _userRoot = Path.Combine(locations.DataRoot, "plugins");
        _log = log;
        _rescan = new Timer(_ => Rescan(), null, Timeout.Infinite, Timeout.Infinite);
        Rescan(raise: false);

        if (watch)
        {
            Directory.CreateDirectory(_userRoot);
            _watcher = new FileSystemWatcher(_userRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += (_, _) => Schedule();
            _watcher.Created += (_, _) => Schedule();
            _watcher.Deleted += (_, _) => Schedule();
            _watcher.Renamed += (_, _) => Schedule();
            _watcher.Error += (_, _) => Schedule();
            _watcher.EnableRaisingEvents = true;
        }
    }

    public event EventHandler<CatalogChangedEventArgs>? Changed;

    public IReadOnlyList<PackageRecord> All(PackageKind kind)
    {
        if (kind != PackageKind.Plugin)
        {
            return [];
        }

        lock (_gate)
        {
            return _plugins.Values.Select(entry => entry.Record).OrderBy(record => record.Id, StringComparer.Ordinal).ToList();
        }
    }

    public PluginManifest? GetManifest(string pluginId)
    {
        lock (_gate)
        {
            return _plugins.TryGetValue(pluginId, out var entry) ? entry.Manifest : null;
        }
    }

    public Task<Outcome<PackageRecord>> InstallAsync(PackageKind kind, string zipOrFolderPath, CancellationToken ct) =>
        Task.FromResult(Outcome<PackageRecord>.Fail(CoreErrorCode.InvalidState, "Installing packages arrives with the plugin manager."));

    public Task<Outcome> RemoveAsync(PackageKind kind, string id, CancellationToken ct) =>
        Task.FromResult(Outcome.Fail(CoreErrorCode.InvalidState, "Removing packages arrives with the plugin manager."));

    public void Dispose()
    {
        _watcher?.Dispose();
        _rescan.Dispose();
    }

    /// <summary>Reads one package folder. Exposed for tests.</summary>
    internal static (PackageRecord Record, PluginManifest? Manifest)? ReadPackage(string folder, PackageSource source)
    {
        var manifestPath = Path.Combine(folder, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var folderName = Path.GetFileName(folder);
        var problems = new List<string>();
        PluginManifest? manifest = null;
        try
        {
            using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) is JsonObject document)
            {
                manifest = PluginManifestReader.Parse(document, problems);
            }
            else
            {
                problems.Add("plugin.json is not a JSON object.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            problems.Add($"plugin.json could not be read: {e.Message}");
        }

        if (manifest is not null)
        {
            if (manifest.Id != folderName)
            {
                problems.Add($"The folder '{folderName}' does not match the manifest id '{manifest.Id}'.");
            }

            var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            var entry = Path.GetFullPath(Path.Combine(folder, manifest.Entry.Path));
            if (!entry.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"entry.path '{manifest.Entry.Path}' leaves the package folder.");
            }
            else if (!File.Exists(entry))
            {
                problems.Add($"entry.path '{manifest.Entry.Path}' does not exist.");
            }
        }

        var record = new PackageRecord(
            PackageKind.Plugin, manifest?.Id ?? folderName, manifest?.Version ?? new SemVersion(0, 0, 0), source,
            folder, false, problems.Count == 0 ? PackageHealth.Valid : PackageHealth.Invalid,
            problems.Select(problem => new CoreError(CoreErrorCode.PackageInvalid, problem, false)).ToList());
        return (record, problems.Count == 0 ? manifest : null);
    }

    private void Schedule() => _rescan.Change(Debounce, Timeout.InfiniteTimeSpan);

    private void Rescan(bool raise = true)
    {
        var builtIn = Scan(_builtInRoot, PackageSource.BuiltIn);
        var user = Scan(_userRoot, PackageSource.User);
        var merged = new Dictionary<string, (PackageRecord Record, PluginManifest? Manifest)>(builtIn, StringComparer.Ordinal);
        foreach (var (id, entry) in user)
        {
            // An invalid user copy does not hide a working built-in one.
            if (merged.TryGetValue(id, out var shipped))
            {
                if (entry.Manifest is null && shipped.Manifest is not null)
                {
                    _log.Write(LogLevel.Warning, "catalog", $"The user copy of {id} is invalid; using the built-in one.");
                    continue;
                }

                merged[id] = (entry.Record with { OverridesBuiltIn = true }, entry.Manifest);
            }
            else
            {
                merged[id] = entry;
            }
        }

        List<CatalogChangedEventArgs> changes = [];
        lock (_gate)
        {
            foreach (var (id, entry) in merged)
            {
                if (!_plugins.TryGetValue(id, out var previous))
                {
                    changes.Add(new CatalogChangedEventArgs(PackageKind.Plugin, id, CatalogChange.Added));
                }
                else if (!Same(previous, entry))
                {
                    changes.Add(new CatalogChangedEventArgs(PackageKind.Plugin, id, CatalogChange.Updated));
                }
            }

            changes.AddRange(_plugins.Keys.Where(id => !merged.ContainsKey(id))
                .Select(id => new CatalogChangedEventArgs(PackageKind.Plugin, id, CatalogChange.Removed)));
            _plugins = merged;
        }

        foreach (var (id, entry) in merged)
        {
            foreach (var problem in entry.Record.Problems)
            {
                if (changes.Any(change => change.Id == id) || !raise)
                {
                    _log.Write(LogLevel.Warning, "catalog", $"{id}: {problem.Detail}");
                }
            }
        }

        if (!raise)
        {
            return;
        }

        foreach (var change in changes)
        {
            _log.Write(LogLevel.Info, "catalog", $"{change.Id} {change.Change.ToString().ToLowerInvariant()}.");
            Changed?.Invoke(this, change);
        }
    }

    private static bool Same((PackageRecord Record, PluginManifest? Manifest) a, (PackageRecord Record, PluginManifest? Manifest) b) =>
        a.Record.RootPath == b.Record.RootPath
        && a.Record.Version == b.Record.Version
        && a.Record.Health == b.Record.Health
        && a.Record.Source == b.Record.Source
        && (a.Manifest?.Raw.ToJsonString() ?? string.Empty) == (b.Manifest?.Raw.ToJsonString() ?? string.Empty);

    private Dictionary<string, (PackageRecord Record, PluginManifest? Manifest)> Scan(string root, PackageSource source)
    {
        var found = new Dictionary<string, (PackageRecord, PluginManifest?)>(StringComparer.Ordinal);
        if (!Directory.Exists(root))
        {
            return found;
        }

        IEnumerable<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Write(LogLevel.Warning, "catalog", $"Could not list {root}: {e.Message}");
            return found;
        }

        foreach (var folder in folders)
        {
            if (ReadPackage(folder, source) is { } package)
            {
                found[package.Record.Id] = package;
            }
        }

        return found;
    }
}
