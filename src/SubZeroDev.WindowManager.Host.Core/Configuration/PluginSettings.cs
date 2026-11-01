using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>Asks a plugin's worker to move stored settings from an older version: <c>settings.migrate</c>.</summary>
public delegate Task<Outcome<SettingsMigrateResult>> SettingsMigrator(int fromVersion, JsonObject settings, CancellationToken ct);

/// <summary>
/// Plugin-level settings, kept in <c>plugin-settings\&lt;pluginId&gt;.json</c> as
/// <c>{ pluginId, settingsVersion, settings }</c> through the config store. What a worker is given at
/// <c>initialize</c> is always valid against the manifest's <c>settingsSchema</c>, with its defaults filled
/// in: stored settings that are invalid, newer than the manifest, or fail to migrate are replaced by the
/// defaults in memory, a diagnostic goes to the plugin's log, and the file is left as it is. Settings
/// older than the manifest are migrated by the worker; the prior file is kept once as
/// <c>&lt;pluginId&gt;.v&lt;N&gt;.bak</c> before the migrated settings are written.
/// </summary>
public sealed class PluginSettingsStore(IConfigStore store, Locations locations, IPluginLogs logs)
{
    public static DocumentId DocumentFor(string pluginId) => new(DocumentKind.PluginSettings, pluginId);

    /// <summary>The settings to start <paramref name="manifest"/>'s worker with.</summary>
    public async Task<JsonObject> LoadAsync(PluginManifest manifest, SettingsMigrator migrate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(migrate);
        var schema = manifest.SettingsSchema;
        var defaults = SettingsSchemaSubset.Defaults(schema);
        var id = DocumentFor(manifest.Id);
        var state = store.Get(id);
        if (state.Health == DocumentHealth.Missing)
        {
            return defaults;
        }

        if (state is not { Health: DocumentHealth.Valid, Content: { } content } || content["settings"] is not JsonObject stored)
        {
            Diagnose(manifest.Id, $"The stored settings could not be read, so defaults are used: {state.LastError?.Detail}");
            return defaults;
        }

        var version = StoredVersion(content);
        if (version > manifest.SettingsVersion)
        {
            Diagnose(manifest.Id, $"The stored settings are version {version}, newer than the plugin's {manifest.SettingsVersion}, so defaults are used.");
            return defaults;
        }

        if (version == manifest.SettingsVersion)
        {
            var problems = SettingsSchemaSubset.Validate(schema, stored);
            if (problems.Count > 0)
            {
                Diagnose(manifest.Id, $"The stored settings fail the schema, so defaults are used: {string.Join("; ", problems)}");
                return defaults;
            }

            return SettingsSchemaSubset.WithDefaults(schema, stored);
        }

        Outcome<SettingsMigrateResult> migrated;
        try
        {
            migrated = await migrate(version, (JsonObject)stored.DeepClone(), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            migrated = Outcome<SettingsMigrateResult>.Fail(CoreErrorCode.CommandFailed, e.Message);
        }

        if (!migrated.Ok)
        {
            Diagnose(manifest.Id, $"Migrating the settings from version {version} failed, so defaults are used and the file is kept: {migrated.Error!.Detail}");
            return defaults;
        }

        var result = migrated.Value!;
        var invalid = result.Version != manifest.SettingsVersion
            ? [$"the migration returned version {result.Version}, not {manifest.SettingsVersion}"]
            : SettingsSchemaSubset.Validate(schema, result.Settings);
        if (invalid.Count > 0)
        {
            Diagnose(manifest.Id, $"The migrated settings are invalid, so defaults are used and the file is kept: {string.Join("; ", invalid)}");
            return defaults;
        }

        var path = Path.Combine(locations.DataRoot, "plugin-settings", manifest.Id + ".json");
        var backup = Path.Combine(locations.DataRoot, "plugin-settings", $"{manifest.Id}.v{version}.bak");
        try
        {
            if (!File.Exists(backup))
            {
                File.Copy(path, backup);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnose(manifest.Id, $"The settings were migrated in memory but not written, because the backup failed: {e.Message}");
            return SettingsSchemaSubset.WithDefaults(schema, result.Settings);
        }

        var written = await store.WriteAsync(id, state.ContentHash, Document(manifest.Id, result.Version, result.Settings), ct)
            .ConfigureAwait(false);
        if (!written.Ok)
        {
            Diagnose(manifest.Id, $"The migrated settings could not be written: {written.Error!.Detail}");
        }

        return SettingsSchemaSubset.WithDefaults(schema, result.Settings);
    }

    /// <summary>
    /// Writes plugin-level settings at the manifest's version. Settings that fail the schema are refused with
    /// <see cref="CoreErrorCode.DocumentInvalid"/> and nothing is written.
    /// </summary>
    public async Task<Outcome> SaveAsync(PluginManifest manifest, JsonObject settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(settings);
        var problems = SettingsSchemaSubset.Validate(manifest.SettingsSchema, settings);
        if (problems.Count > 0)
        {
            return Outcome.Fail(CoreErrorCode.DocumentInvalid, string.Join("; ", problems));
        }

        var id = DocumentFor(manifest.Id);
        var written = await store.WriteAsync(id, store.Get(id).ContentHash, Document(manifest.Id, manifest.SettingsVersion, settings), ct)
            .ConfigureAwait(false);
        return written.Ok ? Outcome.Success : new Outcome(written.Error);
    }

    /// <summary>The settings in a valid stored document, with the schema's defaults filled in; null when it is not usable.</summary>
    public static JsonObject? Current(PluginManifest manifest, DocumentState state)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(state);
        return state is { Health: DocumentHealth.Valid, Content: { } content } && content["settings"] is JsonObject stored
            && StoredVersion(content) == manifest.SettingsVersion && SettingsSchemaSubset.Validate(manifest.SettingsSchema, stored).Count == 0
            ? SettingsSchemaSubset.WithDefaults(manifest.SettingsSchema, stored)
            : null;
    }

    private static JsonObject Document(string pluginId, int version, JsonObject settings) => new()
    {
        ["pluginId"] = pluginId,
        ["settingsVersion"] = version,
        ["settings"] = settings.DeepClone(),
    };

    private static int StoredVersion(JsonObject content) =>
        content["settingsVersion"] is JsonValue value && value.TryGetValue<int>(out var version) ? version : 1;

    private void Diagnose(string pluginId, string message) => logs.Write(pluginId, "host", message);
}
