using System.Text.Json;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>A contribution instance in the active layout, with its schema and its settings (defaults filled in).</summary>
public sealed record InstanceSettings(string InstanceId, string Contribution, string ContributionName, JsonObject? Schema, JsonObject Settings);

/// <summary>
/// Serves <c>settings.save</c> from workers and tells a running worker when its plugin-level settings change
/// on disk. Plugin scope goes to <see cref="PluginSettingsStore"/>; instance scope rewrites the instance's
/// <c>settings</c> in the active layout. Either is checked against the manifest's schema first and refused
/// with <c>1008 SettingsInvalid</c>, writing nothing, when it fails.
/// </summary>
public sealed class SettingsService(IPackageCatalog catalog, IPluginSupervisor supervisor, IConfigStore config, PluginSettingsStore store,
    Func<string> activeLayoutId)
{
    public async Task<RpcMessage> ServeWorkerAsync(string pluginId, RpcMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SettingsSaveParams? parameters;
        try
        {
            parameters = request.ParamsAs<SettingsSaveParams>();
        }
        catch (JsonException e)
        {
            return RpcMessage.Failure(request.Id, WireErrorCodes.InvalidParams, e.Message);
        }

        if (parameters?.Settings is null || (parameters.Scope == Scope.Instance && string.IsNullOrEmpty(parameters.InstanceId)))
        {
            return RpcMessage.Failure(request.Id, WireErrorCodes.InvalidParams, "settings.save needs settings and, for an instance, its id.");
        }

        if (catalog.GetManifest(pluginId) is not { } manifest)
        {
            return RpcMessage.Failure(request.Id, WireErrorCodes.PluginUnavailable, $"{pluginId} has no valid manifest.");
        }

        var outcome = parameters.Scope == Scope.Plugin
            ? await store.SaveAsync(manifest, parameters.Settings, CancellationToken.None).ConfigureAwait(false)
            : await SaveInstanceAsync(manifest, parameters.InstanceId!, parameters.Settings, CancellationToken.None).ConfigureAwait(false);
        return outcome.Ok
            ? RpcMessage.Success(request.Id!.Value, null)
            : RpcMessage.Failure(request.Id, WorkerChannel.WireCodeOf(outcome.Error!.Code), outcome.Error.Detail,
                new JsonObject { ["retryable"] = outcome.Error.Retryable });
    }

    /// <summary>The plugin's instances in the active layout, in layout order; empty when the layout is not usable.</summary>
    public IReadOnlyList<InstanceSettings> InstancesOf(string pluginId)
    {
        if (catalog.GetManifest(pluginId) is not { } manifest
            || config.Get(new DocumentId(DocumentKind.Layout, activeLayoutId())).Content is not { } layout)
        {
            return [];
        }

        return [.. Instances(layout).Where(instance => Text(instance, "plugin") == pluginId).Select(instance =>
        {
            var contribution = Text(instance, "contribution") ?? string.Empty;
            var schema = manifest.ContributionSettingsSchema(contribution);
            var settings = instance["settings"] as JsonObject ?? [];
            var text = Text(instance, "id") ?? string.Empty;
            var id = Guid.TryParse(text, out var guid) ? guid.ToString("D") : text;
            return new InstanceSettings(id, contribution,
                manifest.Contributions.FirstOrDefault(declared => declared.Id == contribution)?.DisplayName ?? contribution,
                schema?.DeepClone() as JsonObject,
                SettingsSchemaSubset.Validate(schema, settings).Count == 0
                    ? SettingsSchemaSubset.WithDefaults(schema, settings)
                    : SettingsSchemaSubset.Defaults(schema));
        })];
    }

    /// <summary>Saves an instance's settings into the active layout, as <c>settings.save</c> with instance scope does.</summary>
    public Task<Outcome> SaveInstanceAsync(string pluginId, string instanceId, JsonObject settings, CancellationToken ct) =>
        catalog.GetManifest(pluginId) is { } manifest
            ? SaveInstanceAsync(manifest, instanceId, settings, ct)
            : Task.FromResult(Outcome.Fail(CoreErrorCode.NotFound, $"{pluginId} has no valid manifest."));

    /// <summary>Sends <c>plugin.settingsChanged</c> to the running worker when its settings file changed outside the host.</summary>
    public void OnDocumentChanged(DocumentChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Id.Kind != DocumentKind.PluginSettings || e.Origin != ChangeOrigin.External
            || catalog.GetManifest(e.Id.Id) is not { } manifest || supervisor.GetChannel(e.Id.Id) is not { } channel)
        {
            return;
        }

        var settings = e.State.Health == DocumentHealth.Missing
            ? SettingsSchemaSubset.Defaults(manifest.SettingsSchema)
            : PluginSettingsStore.Current(manifest, e.State);
        if (settings is not null)
        {
            channel.Notify(Methods.PluginSettingsChanged, new PluginSettingsChangedParams(settings));
        }
    }

    private async Task<Outcome> SaveInstanceAsync(PluginManifest manifest, string instanceId, JsonObject settings, CancellationToken ct)
    {
        var id = new DocumentId(DocumentKind.Layout, activeLayoutId());
        var state = config.Get(id);
        if (state is not { Health: DocumentHealth.Valid, Content: { } content })
        {
            return Outcome.Fail(CoreErrorCode.DocumentReadOnly, $"Layout '{id.Id}' is {state.Health}.");
        }

        var layout = (JsonObject)content.DeepClone();
        var instance = Instances(layout).FirstOrDefault(item => Text(item, "id") is { } text
            && Guid.TryParse(text, out var guid) && guid.ToString("D") == instanceId);
        if (instance is null || Text(instance, "plugin") != manifest.Id)
        {
            return Outcome.Fail(CoreErrorCode.NotFound, $"{manifest.Id} has no instance {instanceId} in layout '{id.Id}'.");
        }

        var schema = manifest.ContributionSettingsSchema(Text(instance, "contribution") ?? "");
        var problems = SettingsSchemaSubset.Validate(schema, settings);
        if (problems.Count > 0)
        {
            return Outcome.Fail(CoreErrorCode.DocumentInvalid, string.Join("; ", problems));
        }

        instance["settings"] = settings.DeepClone();
        var written = await config.WriteAsync(id, state.ContentHash, layout, ct).ConfigureAwait(false);
        return written.Ok ? Outcome.Success : new Outcome(written.Error);
    }

    private static IEnumerable<JsonObject> Instances(JsonObject layout) =>
        (layout["panels"] as JsonArray ?? []).OfType<JsonObject>()
            .SelectMany(panel => (panel["zones"] as JsonObject)?.Select(zone => zone.Value).OfType<JsonArray>() ?? [])
            .SelectMany(items => items.OfType<JsonObject>());

    private static string? Text(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
