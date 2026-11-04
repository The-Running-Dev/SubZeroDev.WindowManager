using System.Text.Json;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>
/// <c>desktops.json</c>: <c>{ schemaVersion: 1, desktops: [ { id, name? } ], lastCurrent }</c>. There is always at least one
/// desktop. A missing file is one "Desktop 1"; an unreadable or invalid file is the same with a problem to report, and
/// the file is left as it was until something changes (then the old content is kept beside it as <c>.invalid</c>).
/// Window membership is never written: it does not outlive the run.
/// </summary>
public sealed class DesktopStore(string path)
{
    public const string DefaultName = "Desktop 1";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _invalidOnDisk;

    /// <summary>What the file said, or the default.</summary>
    /// <param name="Desktops">At least one.</param>
    /// <param name="LastCurrent">The desktop the file named as current; the first one when it named none that exists.</param>
    /// <param name="Problem">Why the file was not used, or null.</param>
    public sealed record Loaded(IReadOnlyList<DesktopInfo> Desktops, Guid LastCurrent, string? Problem);

    public string Path => path;

    public Loaded Load()
    {
        if (!File.Exists(path))
        {
            return Default(null);
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            {
                return Invalid("The file is not a JSON object.");
            }

            if (root["schemaVersion"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number != 1)
            {
                return Invalid("schemaVersion must be 1.");
            }

            if (root["desktops"] is not JsonArray list || list.Count == 0)
            {
                return Invalid("desktops must list at least one desktop.");
            }

            var desktops = new List<DesktopInfo>();
            foreach (var node in list)
            {
                if (node is not JsonObject item || item["id"] is not JsonValue id || !id.TryGetValue<string>(out var text)
                    || !Guid.TryParse(text, out var guid) || guid == Guid.Empty || desktops.Any(d => d.Id == guid))
                {
                    return Invalid("Each desktop needs a unique id (a GUID).");
                }

                var name = item["name"] is JsonValue n && n.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
                desktops.Add(new DesktopInfo(guid, name));
            }

            var last = root["lastCurrent"] is JsonValue current && current.TryGetValue<string>(out var c) && Guid.TryParse(c, out var g)
                && desktops.Any(d => d.Id == g)
                ? g
                : desktops[0].Id;
            return new Loaded(desktops, last, null);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return Invalid(e.Message);
        }
    }

    /// <summary>Writes the file atomically. Serialised; call it from any thread, it never runs on the caller's.</summary>
    public async Task<Outcome> SaveAsync(IReadOnlyList<DesktopInfo> desktops, Guid current, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Write(desktops, current), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Outcome Write(IReadOnlyList<DesktopInfo> desktops, Guid current)
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["desktops"] = new JsonArray([.. desktops.Select(d =>
            {
                var item = new JsonObject { ["id"] = d.Id.ToString("D") };
                if (d.Name is not null)
                {
                    item["name"] = d.Name;
                }

                return (JsonNode?)item;
            })]),
            ["lastCurrent"] = current.ToString("D"),
        };

        try
        {
            if (System.IO.Path.GetDirectoryName(path) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            if (_invalidOnDisk && File.Exists(path))
            {
                File.Copy(path, path + ".invalid", overwrite: true);
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new System.Text.UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            _invalidOnDisk = false;
            return Outcome.Success;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Outcome.Fail(CoreErrorCode.IoFailure, e.Message, retryable: true);
        }
    }

    private static Loaded Default(string? problem)
    {
        var first = new DesktopInfo(Guid.NewGuid(), DefaultName);
        return new Loaded([first], first.Id, problem);
    }

    private Loaded Invalid(string problem)
    {
        _invalidOnDisk = true;
        return Default("desktops.json is not valid (" + problem + "); the shell starts with one desktop and leaves the file as it is.");
    }
}
