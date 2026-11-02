using System.IO.Compression;
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
/// after 300 ms of quiet so a copy in progress is read once, whole. Installs extract into
/// <c>&lt;data&gt;\staging</c> and are checked there before the folder is moved in, so the user folder only
/// ever holds whole packages; <c>staging</c> is emptied when the catalog starts.
/// </summary>
public sealed class PackageCatalog : IPackageCatalog, IDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly string _builtInRoot;
    private readonly string _userRoot;
    private readonly string _stagingRoot;
    private readonly IHostLog _log;
    private readonly Lock _gate = new();
    private readonly Lock _rescanGate = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _rescan;
    private Dictionary<string, (PackageRecord Record, PluginManifest? Manifest)> _plugins = new(StringComparer.Ordinal);

    public PackageCatalog(Locations locations, IHostLog log, bool watch = true)
    {
        _builtInRoot = Path.Combine(locations.ContentRoot, "plugins");
        _userRoot = Path.Combine(locations.DataRoot, "plugins");
        _stagingRoot = Path.Combine(locations.DataRoot, "staging");
        _log = log;
        EmptyStaging();
        _rescan = new Timer(_ => RescanSafely(), null, Timeout.Infinite, Timeout.Infinite);
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

    /// <summary>
    /// Stops a plugin's worker before its folder is replaced or removed, so no file in it is held open.
    /// The app sets it to the supervisor's stop; without it the move is tried as it is.
    /// </summary>
    public Func<string, CancellationToken, Task>? StopWorkerAsync { get; set; }

    /// <summary>Where installs are extracted and checked; exposed for tests.</summary>
    public string StagingRoot => _stagingRoot;

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

    /// <summary>
    /// Installs or upgrades a plugin from a zip or a folder holding <c>plugin.json</c> at its root (or in its
    /// only folder). An upgrade stops the worker, sets the old folder aside, moves the new one in and
    /// reports the plugin Updated so it restarts; a failed move puts the old folder back and restarts it.
    /// </summary>
    public async Task<Outcome<PackageRecord>> InstallAsync(PackageKind kind, string zipOrFolderPath, CancellationToken ct)
    {
        if (kind != PackageKind.Plugin)
        {
            return Outcome<PackageRecord>.Fail(CoreErrorCode.InvalidState, $"Installing {kind} packages is not supported yet.");
        }

        await _operations.WaitAsync(ct).ConfigureAwait(false);
        var work = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            var extracted = Path.Combine(work, "package");
            var unpacked = Unpack(zipOrFolderPath, extracted);
            if (!unpacked.Ok)
            {
                return Outcome<PackageRecord>.Fail(unpacked.Error!);
            }

            var root = File.Exists(Path.Combine(extracted, "plugin.json"))
                ? extracted
                : Directory.GetDirectories(extracted) is [var only] && Directory.GetFiles(extracted).Length == 0
                    && File.Exists(Path.Combine(only, "plugin.json")) ? only : null;
            if (root is null)
            {
                return Outcome<PackageRecord>.Fail(CoreErrorCode.PackageInvalid, "The package has no plugin.json at its root.");
            }

            var problems = new List<string>();
            var manifest = ReadManifest(Path.Combine(root, "plugin.json"), problems);
            if (manifest is null)
            {
                return Outcome<PackageRecord>.Fail(CoreErrorCode.PackageInvalid, string.Join(" ", problems));
            }

            var staged = Path.Combine(work, manifest.Id);
            Directory.Move(root, staged);
            if (ReadPackage(staged, PackageSource.User) is { Record.Health: PackageHealth.Invalid } invalid)
            {
                return Outcome<PackageRecord>.Fail(CoreErrorCode.PackageInvalid,
                    string.Join(" ", invalid.Record.Problems.Select(problem => problem.Detail)));
            }

            var target = Path.Combine(_userRoot, manifest.Id);
            Directory.CreateDirectory(_userRoot);
            var replacing = Directory.Exists(target);
            if (replacing)
            {
                await StopAsync(manifest.Id, ct).ConfigureAwait(false);
                var aside = Path.Combine(work, "previous");
                try
                {
                    Directory.Move(target, aside);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Restart(manifest.Id, $"Upgrading {manifest.Id} failed; the installed version is kept: {e.Message}");
                    return Outcome<PackageRecord>.Fail(CoreErrorCode.IoFailure, $"The installed {manifest.Id} could not be replaced: {e.Message}", true);
                }

                try
                {
                    Directory.Move(staged, target);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Directory.Move(aside, target);
                    Restart(manifest.Id, $"Upgrading {manifest.Id} failed; the installed version is back: {e.Message}");
                    return Outcome<PackageRecord>.Fail(CoreErrorCode.IoFailure, $"The new {manifest.Id} could not be moved in: {e.Message}", true);
                }
            }
            else
            {
                Directory.Move(staged, target);
            }

            _log.Write(LogLevel.Info, "catalog", $"Installed {manifest.Id} {manifest.Version} from {zipOrFolderPath}.");
            var changes = Rescan();
            if (replacing && !changes.Any(change => change.Id == manifest.Id))
            {
                Raise(new CatalogChangedEventArgs(PackageKind.Plugin, manifest.Id, CatalogChange.Updated));
            }

            lock (_gate)
            {
                return _plugins.TryGetValue(manifest.Id, out var entry)
                    ? Outcome<PackageRecord>.Success(entry.Record)
                    : Outcome<PackageRecord>.Fail(CoreErrorCode.IoFailure, $"{manifest.Id} was moved in but is not listed.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Outcome<PackageRecord>.Fail(CoreErrorCode.IoFailure, $"Installing {zipOrFolderPath} failed: {e.Message}", true);
        }
        finally
        {
            DeleteQuietly(work);
            _operations.Release();
        }
    }

    /// <summary>
    /// Stops a user plugin and deletes its folder; a built-in copy underneath, if any, takes its place.
    /// Its <c>plugin-data</c> is left alone. A built-in plugin cannot be removed (<c>PackageInUse</c>).
    /// </summary>
    public async Task<Outcome> RemoveAsync(PackageKind kind, string id, CancellationToken ct)
    {
        if (kind != PackageKind.Plugin)
        {
            return Outcome.Fail(CoreErrorCode.InvalidState, $"Removing {kind} packages is not supported yet.");
        }

        await _operations.WaitAsync(ct).ConfigureAwait(false);
        var work = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            var folder = Path.Combine(_userRoot, id);
            PackageRecord? record;
            lock (_gate)
            {
                record = _plugins.TryGetValue(id, out var entry) ? entry.Record : null;
            }

            if (!Directory.Exists(folder))
            {
                return record is { Source: PackageSource.BuiltIn }
                    ? Outcome.Fail(CoreErrorCode.PackageInUse, $"{id} is built in and cannot be removed; disable it instead.")
                    : Outcome.Fail(CoreErrorCode.NotFound, $"No user plugin {id} is installed.");
            }

            await StopAsync(id, ct).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(work);
                Directory.Move(folder, Path.Combine(work, id));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Restart(id, $"Removing {id} failed; it stays installed: {e.Message}");
                return Outcome.Fail(CoreErrorCode.IoFailure, $"{id} could not be removed: {e.Message}", true);
            }

            _log.Write(LogLevel.Info, "catalog", $"Removed {id}.");
            Rescan();
            return Outcome.Success;
        }
        finally
        {
            DeleteQuietly(work);
            _operations.Release();
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _rescan.Dispose();
        _operations.Dispose();
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            problems.Add($"plugin.json could not be read: {e.Message}");
        }

        if (manifest is not null)
        {
            try
            {
                CheckEntry(folder, folderName, manifest, problems);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                problems.Add($"entry.path '{manifest.Entry.Path}' is not a valid path.");
            }
        }

        var record = new PackageRecord(
            PackageKind.Plugin, manifest?.Id ?? folderName, manifest?.Version ?? new SemVersion(0, 0, 0), source,
            folder, false, problems.Count == 0 ? PackageHealth.Valid : PackageHealth.Invalid,
            problems.Select(problem => new CoreError(CoreErrorCode.PackageInvalid, problem, false)).ToList());
        return (record, problems.Count == 0 ? manifest : null);
    }

    private static PluginManifest? ReadManifest(string path, List<string> problems)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) is JsonObject document)
            {
                return PluginManifestReader.Parse(document, problems);
            }

            problems.Add("plugin.json is not a JSON object.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            problems.Add($"plugin.json could not be read: {e.Message}");
        }

        return null;
    }

    /// <summary>Extracts a zip, refusing it whole if any entry would land outside the target, or copies a folder.</summary>
    private static Outcome Unpack(string source, string target)
    {
        Directory.CreateDirectory(target);
        if (Directory.Exists(source))
        {
            Copy(source, target);
            return Outcome.Success;
        }

        if (!File.Exists(source))
        {
            return Outcome.Fail(CoreErrorCode.NotFound, $"{source} does not exist.");
        }

        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        try
        {
            using var zip = ZipFile.OpenRead(source);
            var destinations = zip.Entries.Select(entry => (Entry: entry, Path: Path.GetFullPath(Path.Combine(root, entry.FullName)))).ToList();
            if (destinations.FirstOrDefault(item => !item.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) is { Entry: { } escaping })
            {
                return Outcome.Fail(CoreErrorCode.PackageInvalid, $"The package entry '{escaping.FullName}' leaves the package folder.");
            }

            foreach (var (entry, destination) in destinations)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: false);
            }
        }
        catch (InvalidDataException e)
        {
            return Outcome.Fail(CoreErrorCode.PackageInvalid, $"{source} is not a readable zip: {e.Message}");
        }

        return Outcome.Success;
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private async Task StopAsync(string pluginId, CancellationToken ct)
    {
        if (StopWorkerAsync is { } stop)
        {
            await stop(pluginId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Reports the plugin changed so the instances that use it start it again.</summary>
    private void Restart(string pluginId, string reason)
    {
        _log.Write(LogLevel.Warning, "catalog", reason);
        Raise(new CatalogChangedEventArgs(PackageKind.Plugin, pluginId, CatalogChange.Updated));
    }

    private void Raise(CatalogChangedEventArgs change) => Changed?.Invoke(this, change);

    private void EmptyStaging()
    {
        if (!Directory.Exists(_stagingRoot))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(_stagingRoot))
        {
            DeleteQuietly(entry);
        }
    }

    /// <summary>A leftover only wastes space, and the next start empties <c>staging</c> again.</summary>
    private void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Write(LogLevel.Warning, "catalog", $"Could not delete {path}: {e.Message}");
        }
    }

    private static void CheckEntry(string folder, string folderName, PluginManifest manifest, List<string> problems)
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

    private void Schedule() => _rescan.Change(Debounce, Timeout.InfiniteTimeSpan);

    /// <summary>The watcher's rescan runs on a timer thread, where an escaped exception would end the host.</summary>
    private void RescanSafely()
    {
        try
        {
            Rescan();
        }
        catch (Exception e)
        {
            _log.Write(LogLevel.Error, "catalog", $"Rescanning the plugins failed: {e}");
        }
    }

    private List<CatalogChangedEventArgs> Rescan(bool raise = true)
    {
        // A watcher rescan and an install's rescan must not both report the same change.
        lock (_rescanGate)
        {
            return RescanLocked(raise);
        }
    }

    private List<CatalogChangedEventArgs> RescanLocked(bool raise)
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
            return changes;
        }

        foreach (var change in changes)
        {
            _log.Write(LogLevel.Info, "catalog", $"{change.Id} {change.Change.ToString().ToLowerInvariant()}.");
            Raise(change);
        }

        return changes;
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
