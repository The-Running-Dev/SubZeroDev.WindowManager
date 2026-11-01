using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Protocol;
using LogLevel = SubZeroDev.WindowManager.Host.Core.Diagnostics.LogLevel;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary><see cref="Provider"/> is <c>host</c> for a built-in command, else a plugin id.</summary>
public sealed record CommandDescriptor(string CommandId, string Title, string Provider, bool Available, string? DefaultHotkey);

public interface ICommandRouter
{
    IReadOnlyList<CommandDescriptor> Commands { get; }

    Task<Outcome<CommandResult>> InvokeAsync(string commandId, JsonObject? args, InvocationSource source, string? callerPluginId, CancellationToken ct);

    /// <summary>Records the user's choice of provider in <c>commandProviders</c>.</summary>
    Task<Outcome> SetProviderAsync(string commandId, string pluginId, CancellationToken ct);

    event EventHandler CommandsChanged;
}

public delegate Task<Outcome<CommandResult>> BuiltInCommand(JsonObject? args, CancellationToken ct);

/// <summary>
/// Routes a command to the plugin that provides it, or to a built-in <c>host.*</c> handler. A provider
/// that is not running gets an error, never an on-demand start or a resend (I19). For input from the
/// user, foreground rights go to the provider before the request is written (I18).
/// </summary>
public sealed class CommandRouter : ICommandRouter
{
    public const string HostProvider = "host";

    public static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(5);

    private readonly IPackageCatalog _catalog;
    private readonly IPluginSupervisor _supervisor;
    private readonly IForegroundService _foreground;
    private readonly IConfigStore _config;
    private readonly IHostLog _log;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, (string Title, BuiltInCommand Handler)> _builtIns = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _dispatches = new();
    private readonly SemaphoreSlim _settingsGate = new(1, 1);

    public CommandRouter(IPackageCatalog catalog, IPluginSupervisor supervisor, IForegroundService foreground,
        IConfigStore config, IHostLog log, TimeSpan? timeout = null)
    {
        _catalog = catalog;
        _supervisor = supervisor;
        _foreground = foreground;
        _config = config;
        _log = log;
        _timeout = timeout ?? InvokeTimeout;
        _catalog.Changed += (_, e) =>
        {
            if (e.Kind == PackageKind.Plugin)
            {
                CommandsChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        _supervisor.StatusChanged += (_, _) => CommandsChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? CommandsChanged;

    /// <summary>The last 100 dispatch steps, oldest first: <c>allow &lt;pid&gt;</c> and <c>send &lt;command&gt; &lt;pid&gt; &lt;source&gt;</c>. For tests.</summary>
    public IReadOnlyList<string> Dispatches => [.. _dispatches];

    public IReadOnlyList<CommandDescriptor> Commands
    {
        get
        {
            var providers = ReadProviders();
            var list = _builtIns
                .Select(b => new CommandDescriptor(b.Key, b.Value.Title, HostProvider, true, null))
                .ToList();
            foreach (var (commandId, declarers) in Declarations())
            {
                var provider = Choose(commandId, declarers, providers);
                var declaration = declarers.First(d => d.PluginId == provider).Command;
                list.Add(new CommandDescriptor(commandId, declaration.Title, provider,
                    _supervisor.GetStatus(provider).State == PluginState.Running, declaration.DefaultHotkey));
            }

            return [.. list.OrderBy(c => c.CommandId, StringComparer.Ordinal)];
        }
    }

    /// <summary>Adds a <c>host.*</c> command.</summary>
    public void AddBuiltIn(string commandId, string title, BuiltInCommand handler)
    {
        if (!commandId.StartsWith("host.", StringComparison.Ordinal))
        {
            throw new ArgumentException("Built-in command ids start with 'host.'.", nameof(commandId));
        }

        _builtIns[commandId] = (title, handler);
        CommandsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<Outcome<CommandResult>> InvokeAsync(string commandId, JsonObject? args, InvocationSource source,
        string? callerPluginId, CancellationToken ct)
    {
        try
        {
            return await InvokeCoreAsync(commandId, args, source, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Outcome<CommandResult>.Fail(CoreErrorCode.Cancelled, $"{commandId} was cancelled.");
        }
    }

    public async Task<Outcome> SetProviderAsync(string commandId, string pluginId, CancellationToken ct)
    {
        if (!Declarations().TryGetValue(commandId, out var declarers) || declarers.All(d => d.PluginId != pluginId))
        {
            return Outcome.Fail(CoreErrorCode.NotFound, $"'{pluginId}' does not declare '{commandId}'.");
        }

        var outcome = await WriteProviderAsync(commandId, pluginId, ct).ConfigureAwait(false);
        CommandsChanged?.Invoke(this, EventArgs.Empty);
        return outcome;
    }

    /// <summary>Answers a worker's request. Serves <c>command.invoke</c>; other requests are not served yet.</summary>
    public async Task<RpcMessage> ServeWorkerAsync(string pluginId, RpcMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Method != Methods.CommandInvoke)
        {
            return RpcMessage.Failure(request.Id, WireErrorCodes.MethodNotFound, $"'{request.Method}' is not served by this host yet.");
        }

        CommandInvokeParams? parameters;
        try
        {
            parameters = request.ParamsAs<CommandInvokeParams>();
        }
        catch (JsonException e)
        {
            return RpcMessage.Failure(request.Id, WireErrorCodes.InvalidParams, e.Message);
        }

        if (parameters is null || string.IsNullOrEmpty(parameters.CommandId))
        {
            return RpcMessage.Failure(request.Id, WireErrorCodes.InvalidParams, "commandId is required.");
        }

        var result = await InvokeAsync(parameters.CommandId, parameters.Args, InvocationSource.Plugin, pluginId, CancellationToken.None)
            .ConfigureAwait(false);
        return result.Ok
            ? RpcMessage.Success(request.Id!.Value, result.Value)
            : RpcMessage.Failure(request.Id, WorkerChannel.WireCodeOf(result.Error!.Code), result.Error.Detail,
                new JsonObject { ["retryable"] = result.Error.Retryable });
    }

    private async Task<Outcome<CommandResult>> InvokeCoreAsync(string commandId, JsonObject? args, InvocationSource source, CancellationToken ct)
    {
        if (_builtIns.TryGetValue(commandId, out var builtIn))
        {
            Record($"host {commandId} {source}");
            try
            {
                return await builtIn.Handler(args, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not (OutOfMemoryException or OperationCanceledException))
            {
                _log.Write(LogLevel.Error, "commands", $"{commandId} failed: {e}");
                return Outcome<CommandResult>.Fail(CoreErrorCode.CommandFailed, $"{commandId}: {e.Message}");
            }
        }

        if (!Declarations().TryGetValue(commandId, out var declarers))
        {
            return Fail(commandId, CoreErrorCode.CommandNotFound, $"No plugin provides '{commandId}'.");
        }

        var providers = ReadProviders();
        var provider = Choose(commandId, declarers, providers);
        if (declarers.Count > 1 && (!providers.TryGetValue(commandId, out var stored) || stored != provider))
        {
            await WriteProviderAsync(commandId, provider, ct).ConfigureAwait(false);
        }

        if (_supervisor.GetChannel(provider) is not { } channel)
        {
            var state = _supervisor.GetStatus(provider).State;
            return Fail(commandId, CoreErrorCode.PluginUnavailable, $"{WireErrorCodes.PluginUnavailable} {provider} is {state}.");
        }

        if (source is InvocationSource.Pointer or InvocationSource.Keyboard or InvocationSource.Hotkey)
        {
            _foreground.AllowForeground(channel.ProcessId);
            Record($"allow {channel.ProcessId}");
        }

        Record($"send {commandId} {channel.ProcessId} {source}");
        var result = await channel.RequestAsync<CommandInvokeParams, CommandResult?>(Methods.CommandInvoke,
            new CommandInvokeParams(commandId, args, source), _timeout, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            _log.Write(LogLevel.Warning, "commands", $"{commandId} via {provider}: {result.Error!.Code} {result.Error.Detail}");
            return Outcome<CommandResult>.Fail(result.Error!);
        }

        return Outcome<CommandResult>.Success(result.Value ?? new CommandResult(null));
    }

    private Outcome<CommandResult> Fail(string commandId, CoreErrorCode code, string detail)
    {
        _log.Write(LogLevel.Warning, "commands", $"{commandId}: {code} {detail}");
        return Outcome<CommandResult>.Fail(code, detail);
    }

    /// <summary>The provider named in <c>commandProviders</c> if it declares the command, else the lowest plugin id in ordinal order (I15).</summary>
    private static string Choose(string commandId, IReadOnlyList<(string PluginId, CommandDeclaration Command)> declarers,
        IReadOnlyDictionary<string, string> providers) =>
        providers.TryGetValue(commandId, out var chosen) && declarers.Any(d => d.PluginId == chosen)
            ? chosen
            : declarers.Select(d => d.PluginId).Min(StringComparer.Ordinal)!;

    private Dictionary<string, List<(string PluginId, CommandDeclaration Command)>> Declarations()
    {
        var table = new Dictionary<string, List<(string, CommandDeclaration)>>(StringComparer.Ordinal);
        foreach (var record in _catalog.All(PackageKind.Plugin))
        {
            if (record.Health != PackageHealth.Valid || _catalog.GetManifest(record.Id) is not { } manifest)
            {
                continue;
            }

            foreach (var command in manifest.Commands)
            {
                if (!table.TryGetValue(command.Id, out var list))
                {
                    table[command.Id] = list = [];
                }

                list.Add((record.Id, command));
            }
        }

        return table;
    }

    private Dictionary<string, string> ReadProviders()
    {
        var providers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_config.Get(DocumentId.HostSettings) is { Health: DocumentHealth.Valid, Content: { } content }
            && content["commandProviders"] is JsonObject map)
        {
            foreach (var (command, value) in map)
            {
                if (value is JsonValue v && v.TryGetValue<string>(out var plugin))
                {
                    providers[command] = plugin;
                }
            }
        }

        return providers;
    }

    private async Task<Outcome> WriteProviderAsync(string commandId, string pluginId, CancellationToken ct)
    {
        await _settingsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _config.Get(DocumentId.HostSettings);
            var content = current.Content is { } existing && current.Health == DocumentHealth.Valid
                ? (JsonObject)existing.DeepClone()
                : HostSettingsDocument.Default.ToJson();
            if (content["commandProviders"] is not JsonObject map)
            {
                content["commandProviders"] = map = [];
            }

            map[commandId] = pluginId;
            var written = await _config.WriteAsync(DocumentId.HostSettings,
                current.Health == DocumentHealth.Valid ? current.ContentHash : null, content, ct).ConfigureAwait(false);
            if (!written.Ok)
            {
                _log.Write(LogLevel.Warning, "commands", $"Recording {pluginId} as the provider of {commandId} failed: {written.Error!.Detail}");
                return new Outcome(written.Error);
            }

            _log.Write(LogLevel.Info, "commands", $"{pluginId} provides {commandId}.");
            return Outcome.Success;
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    private void Record(string step)
    {
        _dispatches.Enqueue(step);
        while (_dispatches.Count > 100 && _dispatches.TryDequeue(out _))
        {
        }
    }
}
