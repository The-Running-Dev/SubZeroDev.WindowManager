using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.Host.Core;

public enum DocumentKind { HostSettings, Layout, Preset, PluginSettings }

/// <summary><see cref="Id"/> is "" for <see cref="DocumentKind.HostSettings"/>.</summary>
public sealed record DocumentId(DocumentKind Kind, string Id)
{
    public static DocumentId HostSettings { get; } = new(DocumentKind.HostSettings, "");
}

public sealed record DocumentState(DocumentId Id, JsonObject? Content, string? ContentHash, DocumentHealth Health, CoreError? LastError);

public enum DocumentHealth { Valid, Missing, Invalid, TooNew }

public interface IConfigStore
{
    DocumentState Get(DocumentId id);

    IReadOnlyList<DocumentId> List(DocumentKind kind);

    // Fails with Conflict when the disk hash differs from basedOnHash,
    // and with DocumentReadOnly when Health is Invalid or TooNew.
    Task<Outcome<DocumentState>> WriteAsync(DocumentId id, string? basedOnHash, JsonObject content, CancellationToken ct);

    Task<Outcome> DeleteAsync(DocumentId id, string? basedOnHash, CancellationToken ct);

    event EventHandler<DocumentChangedEventArgs> Changed;   // raised off the UI thread
}

public sealed record DocumentChangedEventArgs(DocumentId Id, DocumentState State, ChangeOrigin Origin);

public enum ChangeOrigin { Host, External }
