using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Sdk;

public static class PluginRuntime
{
    public const string SdkVersion = "0.1.0";

    /// <summary>False makes the worker ignore pings, as a hung worker would. Used by the test plugin.</summary>
    internal static volatile bool AnswerPings = true;

    private static long _lastPingAnswered;

    /// <summary>When the last ping was answered, as UTC Unix milliseconds; 0 before the first. Used by the test plugin.</summary>
    internal static long LastPingAnswered => Interlocked.Read(ref _lastPingAnswered);

    /// <summary>Exit codes a worker returns.</summary>
    public static class ExitCodes
    {
        public const int Normal = 0;
        public const int HostGone = 1;
        public const int BadArguments = 2;
        public const int Rejected = 3;
    }

    /// <summary>Call from Main. Parses the <c>--wm-*</c> arguments, connects, runs until shutdown. Returns the process exit code.</summary>
    public static async Task<int> RunAsync<TPlugin>(string[] args) where TPlugin : Plugin, new()
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = WorkerOptions.Parse(args);
        var pluginId = ReadPluginId();
        if (options is null || pluginId is null)
        {
            await Console.Error.WriteLineAsync("Expected --wm-pipe, --wm-token, --wm-data and --wm-mode, and a plugin.json beside the executable.").ConfigureAwait(false);
            return ExitCodes.BadArguments;
        }

        var pipe = new NamedPipeClientStream(".", options.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            await Console.Error.WriteLineAsync("Timed out connecting to the host.").ConfigureAwait(false);
            return ExitCodes.HostGone;
        }

        await using var connection = new Connection(pipe);
        var worker = new Worker(connection, new TPlugin(), pluginId, options);
        return await worker.RunAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The ids in a list of plugin.json: <c>{ id }</c> objects for <c>commands</c>, <c>feeds</c> and <c>popups</c>, plain
    /// strings for <c>subscribes</c>. Empty when it cannot be read.
    /// </summary>
    private static HashSet<string> ReadDeclared(string list)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "plugin.json")))?[list] is JsonArray items)
            {
                foreach (var item in items)
                {
                    if ((item is JsonObject ? item["id"] : item) is JsonValue id && id.TryGetValue<string>(out var text))
                    {
                        declared.Add(text);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
        }

        return declared;
    }

    private static string? ReadPluginId()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "plugin.json");
        try
        {
            return JsonNode.Parse(File.ReadAllText(path))?["id"]?.GetValue<string>();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    internal sealed record WorkerOptions(string Pipe, string Token, string DataDirectory, HostMode Mode)
    {
        public static WorkerOptions? Parse(string[] args)
        {
            string? Value(string name)
            {
                var index = Array.IndexOf(args, name);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            }

            var pipe = Value("--wm-pipe");
            var token = Value("--wm-token");
            var data = Value("--wm-data");
            HostMode? mode = Value("--wm-mode") switch
            {
                "coexistence" => HostMode.Coexistence,
                "replacement" => HostMode.Replacement,
                _ => null,
            };
            return pipe is null || token is null || data is null || mode is null
                ? null
                : new WorkerOptions(pipe, token, data, mode.Value);
        }
    }

    /// <summary>Runs one worker: handshake, then callbacks serialised in arrival order (I20).</summary>
    private sealed class Worker(Connection connection, Plugin plugin, string pluginId, WorkerOptions options)
    {
        private readonly Channel<Func<Task>> _callbacks = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Dictionary<string, ContributionInstance> _instances = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<int> RunAsync()
        {
            // The pipe has no buffer, so the host's hello answer waits until something reads it.
            var reader = Task.Run(ReadAsync);
            try
            {
                var hello = await connection.RequestAsync(Methods.Hello,
                    new HelloParams(options.Token, pluginId, ProtocolInfo.ProtocolVersion, SdkVersion),
                    TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
                if (hello.Error is not null || hello.ResultAs<HelloResult>() is not { Accepted: true })
                {
                    await Console.Error.WriteLineAsync($"The host refused the handshake: {hello.Error?.Message}").ConfigureAwait(false);
                    return ExitCodes.Rejected;
                }
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                await Console.Error.WriteLineAsync($"The handshake failed: {exception.Message}").ConfigureAwait(false);
                return ExitCodes.Rejected;
            }

            var dispatcher = Task.Run(DispatchAsync);
            var code = await _exit.Task.ConfigureAwait(false);
            _callbacks.Writer.TryComplete();
            await dispatcher.ConfigureAwait(false);
            _ = reader;
            return code;
        }

        private async Task ReadAsync()
        {
            try
            {
                while (await connection.ReceiveAsync(CancellationToken.None).ConfigureAwait(false) is { } message)
                {
                    Route(message);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or ObjectDisposedException)
            {
                await Console.Error.WriteLineAsync($"The connection failed: {exception.Message}").ConfigureAwait(false);
            }

            _exit.TrySetResult(ExitCodes.HostGone);
        }

        private void Route(RpcMessage message)
        {
            switch (message.Method)
            {
                case Methods.Ping when message.Id is { } id:
                    if (AnswerPings)
                    {
                        connection.Respond(id, null);
                        Interlocked.Exchange(ref _lastPingAnswered, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    }

                    break;
                case Methods.Initialize:
                    Enqueue(message, () => InitializeAsync(message.ParamsAs<InitializeParams>()!));
                    break;
                case Methods.InstanceCreate:
                    Enqueue(message, () => CreateAsync(message.ParamsAs<InstanceCreateParams>()!));
                    break;
                case Methods.InstanceDestroy:
                    Enqueue(message, () => DestroyAsync(message.ParamsAs<InstanceDestroyParams>()!.InstanceId));
                    break;
                case Methods.InstanceSettingsChanged:
                    Enqueue(message, () => SettingsChangedAsync(message.ParamsAs<InstanceSettingsChangedParams>()!));
                    break;
                case Methods.ElementEvent:
                    Enqueue(message, () => EventAsync(message.ParamsAs<ElementEventParams>()!));
                    break;
                case Methods.PopupOpened:
                    var opened = message.ParamsAs<PopupOpenedParams>()!;
                    Enqueue(message, () => plugin.Context?.PopupTable.Find(opened.PopupId) is { } shown
                        ? plugin.OnPopupOpenedAsync(shown, opened.Trigger, CancellationToken.None)
                        : Task.CompletedTask);
                    break;
                case Methods.PopupClosed:
                    var closed = message.ParamsAs<PopupClosedParams>()!;
                    Enqueue(message, () => plugin.Context?.PopupTable.Find(closed.PopupId) is { } hidden
                        ? plugin.OnPopupClosedAsync(hidden, closed.Reason, CancellationToken.None)
                        : Task.CompletedTask);
                    break;
                case Methods.Shutdown:
                    Enqueue(message, () => ShutdownAsync(message.ParamsAs<ShutdownParams>()!));
                    break;
                case Methods.FeedUpdate:
                    var update = message.ParamsAs<FeedUpdateParams>()!;
                    _callbacks.Writer.TryWrite(() => plugin.Context is { } feeds ? feeds.FeedTable.DeliverAsync(update) : Task.CompletedTask);
                    break;
                case Methods.CommandInvoke when message.Id is { } id:
                    var invoke = message.ParamsAs<CommandInvokeParams>()!;
                    _callbacks.Writer.TryWrite(() => plugin.Context is { } context
                        ? context.CommandTable.ServeAsync(id, invoke, e => LogError(e, $"{invoke.CommandId} failed."))
                        : Task.Run(() => connection.Fail(id, WireErrorCodes.CommandNotFound, "The plugin is not initialised.")));
                    break;
                default:
                    if (message.Id is { } unknown)
                    {
                        connection.Fail(unknown, WireErrorCodes.MethodNotFound, $"Method '{message.Method}' is not supported.");
                    }

                    break;
            }
        }

        private void Enqueue(RpcMessage message, Func<Task> callback) =>
            _callbacks.Writer.TryWrite(async () =>
            {
                try
                {
                    await callback().ConfigureAwait(false);
                    if (message.Id is { } id)
                    {
                        connection.Respond(id, null);
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    LogError(exception, $"{message.Method} failed.");
                    if (message.Id is { } id)
                    {
                        connection.Fail(id, WireErrorCodes.CommandFailed, exception.Message);
                    }
                }
            });

        private async Task DispatchAsync()
        {
            await foreach (var callback in _callbacks.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await callback().ConfigureAwait(false);
            }
        }

        private async Task InitializeAsync(InitializeParams parameters)
        {
            plugin.Context = new PluginContext(connection, pluginId, parameters.Mode, parameters.DataDirectory, parameters.PluginSettings,
                ReadDeclared("commands"), ReadDeclared("feeds"), ReadDeclared("subscribes"),
                ReadDeclared("popups"), callback => _callbacks.Writer.TryWrite(callback));
            await plugin.OnStartAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private async Task CreateAsync(InstanceCreateParams parameters)
        {
            var surface = new Surface(connection, parameters.InstanceId);
            var instance = plugin.CreateInstance(new InstanceRequest(
                parameters.InstanceId, parameters.ContributionId, parameters.Settings, parameters.Context, surface));
            _instances[parameters.InstanceId] = instance;
            await instance.OnCreatedAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private async Task DestroyAsync(string instanceId)
        {
            if (_instances.Remove(instanceId, out var instance))
            {
                await instance.DisposeAsync().ConfigureAwait(false);
            }
        }

        private Task SettingsChangedAsync(InstanceSettingsChangedParams parameters) =>
            _instances.TryGetValue(parameters.InstanceId, out var instance)
                ? instance.OnSettingsChangedAsync(parameters.Settings, CancellationToken.None)
                : Task.CompletedTask;

        private Task EventAsync(ElementEventParams parameters)
        {
            var e = new ElementEvent(parameters.ElementId, parameters.Kind, parameters.ItemKey, parameters.Value, parameters.Source);
            if (parameters.SurfaceId.StartsWith("popup:", StringComparison.Ordinal))
            {
                return plugin.Context?.PopupTable.Find(parameters.SurfaceId["popup:".Length..]) is { } popup
                    ? plugin.OnPopupEventAsync(popup, e, CancellationToken.None)
                    : Task.CompletedTask;
            }

            return _instances.TryGetValue(parameters.SurfaceId, out var instance)
                ? instance.OnEventAsync(e, CancellationToken.None)
                : Task.CompletedTask;
        }

        private async Task ShutdownAsync(ShutdownParams parameters)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(0, parameters.DeadlineMilliseconds)));
            try
            {
                await plugin.OnStopAsync(deadline.Token).ConfigureAwait(false);
                foreach (var instance in _instances.Values)
                {
                    await instance.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogError(exception, "Stopping failed.");
            }

            _instances.Clear();
            _exit.TrySetResult(ExitCodes.Normal);
        }

        private void LogError(Exception exception, string message)
        {
            if (plugin.Context is { } context)
            {
                context.Log.LogError(exception, "{Message}", message);
            }
            else
            {
                Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{message} {exception}"));
            }
        }
    }
}
