using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.Plugins.Notifications;

/// <summary>
/// What the plugin keeps on disk: when the centre was last opened and the do-not-disturb switch. Never a notification's
/// content, nor a count of it (invariant I31).
/// </summary>
public sealed class StateStore(string directory)
{
    private readonly string _path = Path.Combine(directory, "state.json");

    public (DateTimeOffset? LastSeen, bool DoNotDisturb) Load()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(_path, Encoding.UTF8)) is JsonObject root)
            {
                var lastSeen = root["lastSeen"] is JsonValue seen && seen.TryGetValue<string>(out var text)
                    && DateTimeOffset.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : (DateTimeOffset?)null;
                var dnd = root["doNotDisturb"] is JsonValue flag && flag.TryGetValue<bool>(out var on) && on;
                return (lastSeen, dnd);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Missing or damaged: start fresh. Nothing in the file is worth failing for.
        }

        return (null, false);
    }

    public void Save(DateTimeOffset lastSeen, bool doNotDisturb)
    {
        Directory.CreateDirectory(directory);
        var state = new JsonObject
        {
            ["lastSeen"] = lastSeen.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["doNotDisturb"] = doNotDisturb,
        };
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temporary, _path, overwrite: true);
    }
}
