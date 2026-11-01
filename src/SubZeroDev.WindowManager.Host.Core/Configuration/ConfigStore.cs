using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>
/// JSON documents under the data root, with built-in layouts and presets under the content root as a
/// read-only fallback. Writes go to the data root only, through a temp file in the same directory (I14).
/// After <see cref="StartWatching"/>, a hand edit under the data root raises <see cref="Changed"/> with
/// <see cref="ChangeOrigin.External"/> once the file has been quiet for 300 ms; the host's own writes do not.
/// </summary>
public sealed class ConfigStore : IConfigStore, IDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Locations _locations;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly Lock _watchGate = new();
    private readonly Dictionary<DocumentId, string?> _knownHashes = [];
    private readonly HashSet<DocumentId> _dirty = [];
    private FileSystemWatcher? _watcher;
    private Timer? _settle;

    public ConfigStore(Locations locations) => _locations = locations;

    public event EventHandler<DocumentChangedEventArgs>? Changed;

    public DocumentState Get(DocumentId id)
    {
        var path = EffectivePath(id);
        return path is null ? new DocumentState(id, null, null, DocumentHealth.Missing, null) : Load(id, path);
    }

    public IReadOnlyList<DocumentId> List(DocumentKind kind)
    {
        if (kind == DocumentKind.HostSettings)
        {
            return File.Exists(UserPath(DocumentId.HostSettings)) ? [DocumentId.HostSettings] : [];
        }

        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var root in new[] { UserDirectory(kind), BuiltInDirectory(kind) })
        {
            if (root is null || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.json"))
            {
                ids.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        return [.. ids.Select(i => new DocumentId(kind, i))];
    }

    public async Task<Outcome<DocumentState>> WriteAsync(DocumentId id, string? basedOnHash, JsonObject content, CancellationToken ct)
    {
        var result = await WriteLockedAsync(id, basedOnHash, content, ct).ConfigureAwait(false);
        if (result.Ok)
        {
            Remember(id, result.Value!.ContentHash);
            Changed?.Invoke(this, new DocumentChangedEventArgs(id, result.Value!, ChangeOrigin.Host));
        }

        return result;
    }

    public async Task<Outcome> DeleteAsync(DocumentId id, string? basedOnHash, CancellationToken ct)
    {
        var result = await DeleteLockedAsync(id, basedOnHash, ct).ConfigureAwait(false);
        if (result.Ok)
        {
            var state = Get(id);
            Remember(id, state.ContentHash);
            Changed?.Invoke(this, new DocumentChangedEventArgs(id, state, ChangeOrigin.Host));
        }

        return result;
    }

    /// <summary>Starts reporting edits made outside the host. Idempotent.</summary>
    public void StartWatching()
    {
        lock (_watchGate)
        {
            if (_watcher is not null)
            {
                return;
            }

            Directory.CreateDirectory(_locations.DataRoot);
            foreach (var kind in Enum.GetValues<DocumentKind>())
            {
                foreach (var id in List(kind))
                {
                    _knownHashes[id] = Get(id).ContentHash;
                }
            }

            _settle = new Timer(_ => RaiseExternal(), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(_locations.DataRoot, "*.json")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += (_, e) => Touch(e.FullPath);
            _watcher.Created += (_, e) => Touch(e.FullPath);
            _watcher.Deleted += (_, e) => Touch(e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                Touch(e.OldFullPath);
                Touch(e.FullPath);
            };
            _watcher.EnableRaisingEvents = true;
        }
    }

    public void Dispose()
    {
        lock (_watchGate)
        {
            _watcher?.Dispose();
            _settle?.Dispose();
            _watcher = null;
        }

        _writer.Dispose();
    }

    // Changed is raised by the two methods above once the writer is released, so handlers may write.
    private async Task<Outcome<DocumentState>> WriteLockedAsync(DocumentId id, string? basedOnHash, JsonObject content, CancellationToken ct)
    {
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = Get(id);
            if (current.Health is DocumentHealth.Invalid or DocumentHealth.TooNew)
            {
                return Outcome<DocumentState>.Fail(CoreErrorCode.DocumentReadOnly,
                    $"{Describe(id)} is {current.Health}: {current.LastError?.Detail}");
            }

            if (!string.Equals(current.ContentHash, basedOnHash, StringComparison.Ordinal))
            {
                return Outcome<DocumentState>.Fail(CoreErrorCode.Conflict, $"{Describe(id)} changed on disk.", retryable: true);
            }

            var problems = DocumentValidator.Validate(id, content);
            if (problems.Count > 0)
            {
                return Outcome<DocumentState>.Fail(CoreErrorCode.DocumentInvalid, string.Join("; ", problems));
            }

            var path = UserPath(id);
            var bytes = Utf8NoBom.GetBytes(content.ToJsonString(WriteOptions) + "\n");
            try
            {
                await WriteAtomicallyAsync(path, bytes, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Outcome<DocumentState>.Fail(CoreErrorCode.IoFailure, $"{path}: {e.Message}", retryable: true);
            }

            var state = new DocumentState(id, (JsonObject)content.DeepClone(), Hash(bytes), DocumentHealth.Valid, null);
            return Outcome<DocumentState>.Success(state);
        }
        finally
        {
            _writer.Release();
        }
    }

    private async Task<Outcome> DeleteLockedAsync(DocumentId id, string? basedOnHash, CancellationToken ct)
    {
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = UserPath(id);
            if (!File.Exists(path))
            {
                return Outcome.Fail(CoreErrorCode.NotFound, $"{Describe(id)} has no user copy.");
            }

            var current = Load(id, path);
            if (!string.Equals(current.ContentHash, basedOnHash, StringComparison.Ordinal))
            {
                return Outcome.Fail(CoreErrorCode.Conflict, $"{Describe(id)} changed on disk.", retryable: true);
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Outcome.Fail(CoreErrorCode.IoFailure, $"{path}: {e.Message}", retryable: true);
            }

            return Outcome.Success;
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Maps a file under the data root back to the document it holds, or null.</summary>
    internal DocumentId? IdForPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (string.Equals(full, UserPath(DocumentId.HostSettings), StringComparison.OrdinalIgnoreCase))
        {
            return DocumentId.HostSettings;
        }

        var directory = Path.GetDirectoryName(full);
        foreach (var kind in new[] { DocumentKind.Layout, DocumentKind.Preset, DocumentKind.PluginSettings })
        {
            if (string.Equals(directory, UserDirectory(kind), StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(full);
                return name.StartsWith('.') ? null : new DocumentId(kind, name);
            }
        }

        return null;
    }

    private void Remember(DocumentId id, string? hash)
    {
        lock (_watchGate)
        {
            _knownHashes[id] = hash;
        }
    }

    private void Touch(string path)
    {
        if (IdForPath(path) is not { } id)
        {
            return;
        }

        lock (_watchGate)
        {
            _dirty.Add(id);
            _settle?.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void RaiseExternal()
    {
        List<DocumentChangedEventArgs> changes = [];
        lock (_watchGate)
        {
            foreach (var id in _dirty)
            {
                var state = Get(id);
                if (_knownHashes.TryGetValue(id, out var known) && known == state.ContentHash)
                {
                    continue;
                }

                _knownHashes[id] = state.ContentHash;
                changes.Add(new DocumentChangedEventArgs(id, state, ChangeOrigin.External));
            }

            _dirty.Clear();
        }

        foreach (var change in changes)
        {
            Changed?.Invoke(this, change);
        }
    }

    internal string UserPath(DocumentId id) => id.Kind switch
    {
        DocumentKind.HostSettings => Path.Combine(_locations.DataRoot, "settings.json"),
        _ => Path.Combine(UserDirectory(id.Kind)!, id.Id + ".json"),
    };

    private string? EffectivePath(DocumentId id)
    {
        var user = UserPath(id);
        if (File.Exists(user))
        {
            return user;
        }

        var builtInRoot = BuiltInDirectory(id.Kind);
        var builtIn = builtInRoot is null ? null : Path.Combine(builtInRoot, id.Id + ".json");
        return builtIn is not null && File.Exists(builtIn) ? builtIn : null;
    }

    private string? UserDirectory(DocumentKind kind) => kind switch
    {
        DocumentKind.Layout => Path.Combine(_locations.DataRoot, "layouts"),
        DocumentKind.Preset => Path.Combine(_locations.DataRoot, "presets"),
        DocumentKind.PluginSettings => Path.Combine(_locations.DataRoot, "plugin-settings"),
        _ => null,
    };

    private string? BuiltInDirectory(DocumentKind kind) => kind switch
    {
        DocumentKind.Layout => Path.Combine(_locations.ContentRoot, "layouts"),
        DocumentKind.Preset => Path.Combine(_locations.ContentRoot, "presets"),
        _ => null,
    };

    private static DocumentState Load(DocumentId id, string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DocumentState(id, null, null, DocumentHealth.Invalid,
                new CoreError(CoreErrorCode.IoFailure, $"{path}: {e.Message}", true));
        }

        var hash = Hash(bytes);
        JsonObject? content;
        try
        {
            content = JsonNode.Parse(bytes, documentOptions: ReadOptions) as JsonObject;
        }
        catch (JsonException e)
        {
            return Invalid(id, hash, $"{path}: {e.Message}");
        }

        if (content is null)
        {
            return Invalid(id, hash, $"{path}: the document is not a JSON object.");
        }

        if (content["schemaVersion"] is JsonValue v && v.TryGetValue<int>(out var version) && version > SupportedSchemaVersion)
        {
            return new DocumentState(id, content, hash, DocumentHealth.TooNew,
                new CoreError(CoreErrorCode.DocumentReadOnly, $"{path}: schemaVersion {version} is newer than {SupportedSchemaVersion}.", false));
        }

        var problems = DocumentValidator.Validate(id, content);
        return problems.Count > 0
            ? Invalid(id, hash, $"{path}: {string.Join("; ", problems)}")
            : new DocumentState(id, content, hash, DocumentHealth.Valid, null);
    }

    private static DocumentState Invalid(DocumentId id, string hash, string detail) =>
        new(id, null, hash, DocumentHealth.Invalid, new CoreError(CoreErrorCode.DocumentInvalid, detail, false));

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Describe(DocumentId id) => id.Kind == DocumentKind.HostSettings ? "settings.json" : $"{id.Kind} '{id.Id}'";
}
