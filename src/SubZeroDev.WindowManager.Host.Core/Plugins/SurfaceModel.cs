using System.Text.Json;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core;

public enum SurfaceUpdateResult { Applied, Rejected, Stale }

/// <summary>What one update did; <see cref="Issues"/> is set when a view was rejected (1005).</summary>
public sealed record SurfaceUpdate(SurfaceUpdateResult Result, string? Detail = null, IReadOnlyList<ValidationIssue>? Issues = null)
{
    public bool Applied => Result == SurfaceUpdateResult.Applied;
}

/// <summary>
/// The retained view and state of one surface. A view is checked with <see cref="ViewValidator"/>
/// before it replaces the current one, so a rejected view leaves the previous one shown (I11).
/// Patches apply in arrival order, and a view or patch below the current view revision is dropped
/// (I12). Not thread-safe: the owner applies notifications one at a time, in channel order.
/// </summary>
public sealed class SurfaceModel(string surfaceId)
{
    private readonly Dictionary<string, JsonNode?> _state = new(StringComparer.Ordinal);

    public string SurfaceId { get; } = surfaceId;

    /// <summary>0 until the first view arrives.</summary>
    public long ViewRevision { get; private set; }

    public JsonNode? View { get; private set; }

    public IReadOnlyDictionary<string, JsonNode?> State => _state;

    /// <summary>Applies <c>view.set</c> params: <c>{ surfaceId, revision, root }</c>.</summary>
    public SurfaceUpdate ApplyView(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("revision", out var revisionElement)
            || !revisionElement.TryGetInt64(out var revision)
            || !parameters.TryGetProperty("root", out var rootElement))
        {
            return new SurfaceUpdate(SurfaceUpdateResult.Rejected, "view.set needs a revision and a root.");
        }

        if (revision < ViewRevision)
        {
            return new SurfaceUpdate(SurfaceUpdateResult.Stale, $"Revision {revision} is below the current view revision {ViewRevision}.");
        }

        var root = JsonNode.Parse(rootElement.GetRawText());
        var validation = ViewValidator.Validate(root);
        if (!validation.IsValid)
        {
            return new SurfaceUpdate(SurfaceUpdateResult.Rejected, validation.ToString(), validation.Issues);
        }

        View = root;
        ViewRevision = revision;
        return new SurfaceUpdate(SurfaceUpdateResult.Applied);
    }

    public SurfaceUpdate ApplyPatch(StatePatchParams patch)
    {
        if (patch.Revision < ViewRevision)
        {
            return new SurfaceUpdate(SurfaceUpdateResult.Stale, $"Patch revision {patch.Revision} is below the view revision {ViewRevision}.");
        }

        foreach (var key in patch.Remove ?? [])
        {
            _state.Remove(key);
        }

        foreach (var (key, value) in patch.Set ?? new Dictionary<string, JsonNode?>())
        {
            _state[key] = value?.DeepClone();
        }

        return new SurfaceUpdate(SurfaceUpdateResult.Applied);
    }
}
