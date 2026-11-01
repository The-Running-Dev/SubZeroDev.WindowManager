using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.Protocol;

public static class ProtocolInfo
{
    public const int ProtocolVersion = 1;
    public static readonly SemVersion HostApiVersion = new(1, 0, 0);
    public const int MaxMessageBytes = 4 * 1024 * 1024;
}

public static class Methods
{
    // worker -> host
    public const string Hello = "hello";
    public const string ViewSet = "view.set";
    public const string StatePatch = "state.patch";
    public const string PopupOpen = "popup.open";
    public const string PopupClose = "popup.close";
    public const string CommandInvoke = "command.invoke";
    public const string FeedPublish = "feed.publish";
    public const string SettingsSave = "settings.save";
    public const string Log = "log";

    // host -> worker
    public const string Initialize = "initialize";
    public const string SettingsMigrate = "settings.migrate";
    public const string InstanceCreate = "instance.create";
    public const string InstanceSettingsChanged = "instance.settingsChanged";
    public const string InstanceDestroy = "instance.destroy";
    public const string PluginSettingsChanged = "plugin.settingsChanged";
    public const string ElementEvent = "element.event";
    public const string PopupOpened = "popup.opened";
    public const string PopupClosed = "popup.closed";
    public const string FeedUpdate = "feed.update";
    public const string Ping = "ping";
    public const string Shutdown = "shutdown";
}

/// <summary>JSON-RPC and protocol error codes on the wire.</summary>
public static class WireErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int PluginUnavailable = 1001;
    public const int CommandNotFound = 1002;
    public const int Timeout = 1003;
    public const int IncompatibleVersion = 1004;
    public const int InvalidView = 1005;
    public const int Cancelled = 1006;
    public const int Undeclared = 1007;
    public const int SettingsInvalid = 1008;
    public const int CommandFailed = 1009;
    public const int Unauthorised = 1010;
}

public sealed record HelloParams(string Token, string PluginId, int ProtocolVersion, string SdkVersion);

public sealed record HelloResult(bool Accepted);

public sealed record InitializeParams(
    int ProtocolVersion, string HostVersion, HostMode Mode,
    JsonObject PluginSettings, int PluginSettingsVersion,
    string DataDirectory, string Culture);

public sealed record InitializeResult;

public enum HostMode { Coexistence, Replacement }

public sealed record SettingsMigrateParams(Scope Scope, string? InstanceId, int FromVersion, JsonObject Settings);

public sealed record SettingsMigrateResult(int Version, JsonObject Settings);

public enum Scope { Plugin, Instance }

public sealed record InstanceCreateParams(
    string InstanceId, string ContributionId, JsonObject Settings, int SettingsVersion,
    SurfaceContext Context);

public sealed record InstanceSettingsChangedParams(string InstanceId, JsonObject Settings);

public sealed record InstanceDestroyParams(string InstanceId);

public sealed record PluginSettingsChangedParams(JsonObject Settings);

/// <summary>A surface is an instance's main view (id = instance id) or a popup (id = <c>popup:</c> + popup id).</summary>
public sealed record ViewSetParams(string SurfaceId, long Revision, ViewNode Root);

public sealed record StatePatchParams(string SurfaceId, long Revision,
    IReadOnlyDictionary<string, JsonNode?>? Set, IReadOnlyList<string>? Remove);

public sealed record PopupOpenParams(string PopupId, PopupAnchor Anchor);

public sealed record PopupAnchor(ElementAnchor? Element, MonitorAnchor? Monitor);

public sealed record ElementAnchor(string SurfaceId, string ElementId);

public enum MonitorAnchor { Active, Pointer, Primary }

public sealed record PopupCloseParams(string PopupId);

public sealed record PopupOpenedParams(string PopupId, PopupTrigger Trigger);

public sealed record PopupClosedParams(string PopupId, PopupCloseReason Reason);

public enum PopupTrigger { Element, Command }

public enum PopupCloseReason { LightDismiss, Escape, Requested, OwnerFaulted }

public sealed record CommandInvokeParams(string CommandId, JsonObject? Args, InvocationSource Source);

public enum InvocationSource { Pointer, Keyboard, Hotkey, Plugin, Host }

public sealed record CommandResult(JsonNode? Value);

public sealed record ElementEventParams(string SurfaceId, string ElementId, ElementEventKind Kind,
    string? ItemKey, JsonNode? Value, InvocationSource Source);

public enum ElementEventKind { Invoked, Toggled, TextChanged, TextSubmitted, SelectionChanged, ItemInvoked }

public sealed record FeedPublishParams(string FeedId, JsonNode Snapshot);

public sealed record FeedUpdateParams(string FeedId, bool Available, JsonNode? Snapshot);

public sealed record SettingsSaveParams(Scope Scope, string? InstanceId, JsonObject Settings);

public sealed record LogParams(LogLevel Level, string Message, string? Exception);

public enum LogLevel { Trace, Debug, Information, Warning, Error }

public sealed record ShutdownParams(int DeadlineMilliseconds);
