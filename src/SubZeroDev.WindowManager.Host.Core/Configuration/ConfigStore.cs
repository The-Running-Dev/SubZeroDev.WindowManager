using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>
/// JSON documents under the data root, with built-in layouts and presets under the content root as a
/// read-only fallback. Writes go to the data root only, through a temp file in the same directory (I14).
/// </summary>
public sealed class ConfigStore : IConfigStore
{
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Locations _locations;
    private readonly SemaphoreSlim _writer = new(1, 1);

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
            Changed?.Invoke(this, new DocumentChangedEventArgs(id, state, ChangeOrigin.Host));
            return Outcome<DocumentState>.Success(state);
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<Outcome> DeleteAsync(DocumentId id, string? basedOnHash, CancellationToken ct)
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

            Changed?.Invoke(this, new DocumentChangedEventArgs(id, Get(id), ChangeOrigin.Host));
            return Outcome.Success;
        }
        finally
        {
            _writer.Release();
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
