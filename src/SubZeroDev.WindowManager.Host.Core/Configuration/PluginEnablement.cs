using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>
/// Reads and writes the <c>plugins</c> map of <c>settings.json</c>. A plugin absent from the map is enabled
/// when built in and disabled when user-installed. The map is read from the document on every call, so a
/// hand edit counts from the next start.
/// </summary>
public sealed class PluginEnablement(IConfigStore config, IPackageCatalog catalog)
{
    private const int WriteAttempts = 3;

    public bool IsEnabled(string pluginId)
    {
        if (config.Get(DocumentId.HostSettings).Content?["plugins"] is JsonObject plugins
            && plugins[pluginId] is JsonObject entry
            && entry["enabled"] is JsonValue value
            && value.TryGetValue<bool>(out var enabled))
        {
            return enabled;
        }

        return catalog.All(PackageKind.Plugin).FirstOrDefault(package => package.Id == pluginId)?.Source != PackageSource.User;
    }

    /// <summary>Writes <c>plugins.&lt;id&gt;.enabled</c>, keeping everything else in the document; retries when another write got there first.</summary>
    public async Task<Outcome> SetAsync(string pluginId, bool enabled, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var current = config.Get(DocumentId.HostSettings);
            var content = current.Content is { } existing ? (JsonObject)existing.DeepClone() : HostSettingsDocument.Default.ToJson();
            if (content["plugins"] is not JsonObject plugins)
            {
                content["plugins"] = plugins = new JsonObject();
            }

            plugins[pluginId] = new JsonObject { ["enabled"] = enabled };
            var written = await config.WriteAsync(DocumentId.HostSettings, current.ContentHash, content, ct).ConfigureAwait(false);
            if (written.Ok || written.Error!.Code != CoreErrorCode.Conflict || attempt == WriteAttempts)
            {
                return written.Ok ? Outcome.Success : new Outcome(written.Error);
            }
        }
    }
}
