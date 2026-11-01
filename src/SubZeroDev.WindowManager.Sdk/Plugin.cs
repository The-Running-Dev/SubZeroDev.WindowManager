using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Sdk;

public abstract class Plugin
{
    protected internal PluginContext Context { get; internal set; } = null!;

    /// <summary>Register commands and feeds here.</summary>
    protected internal virtual Task OnStartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>The token fires at the shutdown deadline.</summary>
    protected internal virtual Task OnStopAsync(CancellationToken ct) => Task.CompletedTask;

    protected internal abstract ContributionInstance CreateInstance(InstanceRequest request);

    /// <summary>The host showed one of the plugin's popups; it has foreground rights while this runs (I18).</summary>
    protected internal virtual Task OnPopupOpenedAsync(PopupSurface popup, PopupTrigger trigger, CancellationToken ct) => Task.CompletedTask;

    protected internal virtual Task OnPopupClosedAsync(PopupSurface popup, PopupCloseReason reason, CancellationToken ct) => Task.CompletedTask;

    /// <summary>An element in a popup's view did something. The foreground grant of I18 covers it too.</summary>
    protected internal virtual Task OnPopupEventAsync(PopupSurface popup, ElementEvent e, CancellationToken ct) => Task.CompletedTask;
}

public sealed class PluginContext
{
    private readonly Connection _connection;

    internal PluginContext(Connection connection, string pluginId, HostMode mode, string dataDirectory, JsonObject settings,
        IReadOnlySet<string> declaredCommands, IReadOnlySet<string> declaredFeeds, IReadOnlySet<string> declaredSubscriptions,
        IReadOnlySet<string> declaredPopups, Action<Func<Task>> enqueue)
    {
        _connection = connection;
        PluginId = pluginId;
        Mode = mode;
        DataDirectory = dataDirectory;
        Settings = settings;
        Log = new HostLogger(connection);
        CommandTable = new Commands(connection, declaredCommands);
        PopupTable = new Popups(connection, declaredPopups);
        FeedTable = new Feeds(connection, declaredFeeds, declaredSubscriptions, enqueue,
            (exception, message) => Log.LogError(exception, "{Message}", message));
    }

    public string PluginId { get; }

    public HostMode Mode { get; }

    public string DataDirectory { get; }

    public JsonObject Settings { get; }

    /// <summary>Forwarded to the host as <c>log</c> notifications, into the plugin's log file.</summary>
    public ILogger Log { get; }

    /// <summary>Register handlers for the commands in plugin.json, and invoke any command through the host.</summary>
    public ICommands Commands => CommandTable;

    internal Commands CommandTable { get; }

    /// <summary>Publish the feeds in plugin.json, and subscribe to the feeds it lists under <c>subscribes</c>.</summary>
    public IFeeds Feeds => FeedTable;

    internal Feeds FeedTable { get; }

    /// <summary>The popups in plugin.json.</summary>
    public IPopups Popups => PopupTable;

    internal Popups PopupTable { get; }

    internal Connection Connection => _connection;
}

public sealed record InstanceRequest(string InstanceId, string ContributionId, JsonObject Settings,
    SurfaceContext Context, Surface Surface);

public sealed record ElementEvent(string ElementId, ElementEventKind Kind, string? ItemKey, JsonNode? Value,
    InvocationSource Source);

public abstract class ContributionInstance : IAsyncDisposable
{
    protected ContributionInstance(InstanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        InstanceId = request.InstanceId;
        ContributionId = request.ContributionId;
        Surface = request.Surface;
    }

    public string InstanceId { get; }

    public string ContributionId { get; }

    protected Surface Surface { get; }

    /// <summary>Set the first view here.</summary>
    protected internal virtual Task OnCreatedAsync(CancellationToken ct) => Task.CompletedTask;

    protected internal virtual Task OnEventAsync(ElementEvent e, CancellationToken ct) => Task.CompletedTask;

    protected internal virtual Task OnSettingsChangedAsync(JsonObject settings, CancellationToken ct) => Task.CompletedTask;

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A view the plugin owns: an instance's main view, or a popup.</summary>
public class Surface
{
    private readonly Connection _connection;
    private readonly Lock _gate = new();
    private long _revision;

    internal Surface(Connection connection, string surfaceId)
    {
        _connection = connection;
        SurfaceId = surfaceId;
    }

    public string SurfaceId { get; }

    /// <summary>Validates the tree (I11), bumps the revision and sends it. Safe from any thread.</summary>
    public void SetView(ViewNode root)
    {
        var result = ViewValidator.Validate(root);
        if (!result.IsValid)
        {
            throw new InvalidViewException(result);
        }

        SendView(ProtocolJson.ToNode(root)!);
    }

    public void SetState(string key, JsonNode? value) =>
        SendPatch(new Dictionary<string, JsonNode?> { [key] = value?.DeepClone() }, null);

    public void RemoveState(string key) => SendPatch(null, [key]);

    /// <summary>Sends a view without validating it. Used by the test plugin to exercise the host's checks.</summary>
    internal void SendView(JsonNode root)
    {
        lock (_gate)
        {
            _revision++;
            _connection.Notify(Methods.ViewSet, new JsonObject
            {
                ["surfaceId"] = SurfaceId,
                ["revision"] = _revision,
                ["root"] = root.DeepClone(),
            });
        }
    }

    /// <summary>Sends a state patch at an explicit revision. Used by the test plugin.</summary>
    internal void SendPatchAt(long revision, IReadOnlyDictionary<string, JsonNode?>? set, IReadOnlyList<string>? remove) =>
        _connection.Notify(Methods.StatePatch, new StatePatchParams(SurfaceId, revision, set, remove));

    private void SendPatch(IReadOnlyDictionary<string, JsonNode?>? set, IReadOnlyList<string>? remove)
    {
        lock (_gate)
        {
            SendPatchAt(_revision, set, remove);
        }
    }
}

internal sealed class HostLogger(Connection connection) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel != Microsoft.Extensions.Logging.LogLevel.None;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var level = logLevel switch
        {
            Microsoft.Extensions.Logging.LogLevel.Trace => Protocol.LogLevel.Trace,
            Microsoft.Extensions.Logging.LogLevel.Debug => Protocol.LogLevel.Debug,
            Microsoft.Extensions.Logging.LogLevel.Information => Protocol.LogLevel.Information,
            Microsoft.Extensions.Logging.LogLevel.Warning => Protocol.LogLevel.Warning,
            _ => Protocol.LogLevel.Error,
        };
        connection.Notify(Methods.Log, new LogParams(level, formatter(state, exception), exception?.ToString()));
    }
}
