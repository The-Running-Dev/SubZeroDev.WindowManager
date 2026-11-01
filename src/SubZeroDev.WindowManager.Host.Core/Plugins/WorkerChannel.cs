using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>Why a handshake did not produce a channel. <see cref="Incompatible"/> marks a protocol version mismatch.</summary>
public sealed record HandshakeFailure(FaultReason Reason, bool Incompatible, string Detail);

/// <summary>Why a channel stopped: the worker closed the pipe, or broke the protocol.</summary>
public sealed record ChannelClose(FaultReason Reason, string Detail);

/// <summary>
/// The host end of one worker's pipe. <see cref="AcceptAsync"/> runs the handshake (I5): the first
/// frame must be a <c>hello</c> request carrying the launch token, this plugin's id and protocol 1;
/// anything else is answered with 1010 (1004 for a version mismatch) and the pipe is closed, so no
/// later message is read. After <see cref="Start"/>, notifications are raised one at a time in the
/// order they arrived (I12). Incoming messages are metered: above the rate, events other than views
/// and patches are dropped, and a worker that stays above it is closed with <see cref="FaultReason.Flood"/>.
/// </summary>
public sealed class WorkerChannel : IWorkerChannel, IAsyncDisposable
{
    private static long s_messagesSent;

    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>What a worker may send. Requests other than <c>hello</c> are not served until their slices.</summary>
    private static readonly HashSet<string> WorkerMethods = new(StringComparer.Ordinal)
    {
        Methods.ViewSet, Methods.StatePatch, Methods.PopupOpen, Methods.PopupClose, Methods.CommandInvoke,
        Methods.FeedPublish, Methods.SettingsSave, Methods.Log,
    };

    private readonly Stream _stream;
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<long, TaskCompletionSource<RpcMessage>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<ChannelClose> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FloodMeter _meter;
    private readonly int _messagesPerSecond;
    private readonly TimeSpan _floodSustain;
    private long _nextId;
    private int _started;
    private int _dropped;
    private long _dropReportedAt = long.MinValue;

    private WorkerChannel(Stream stream, string pluginId, int processId, SupervisorTimings timings)
    {
        _stream = stream;
        PluginId = pluginId;
        ProcessId = processId;
        _messagesPerSecond = timings.MessagesPerSecond;
        _floodSustain = timings.FloodSustain;
        _meter = new FloodMeter(timings.MessagesPerSecond, timings.FloodSustain);
    }

    public string PluginId { get; }

    public int ProcessId { get; }

    public event EventHandler<WorkerNotification>? NotificationReceived;

    /// <summary>Lines for the plugin's log about what the channel dropped.</summary>
    public event EventHandler<string>? Diagnostic;

    /// <summary>Completes once, when the channel stops for any reason.</summary>
    public Task<ChannelClose> Closed => _closed.Task;

    public bool IsOpen => !_closed.Task.IsCompleted;

    /// <summary>Reads the first frame and answers it. On failure the stream is disposed.</summary>
    public static async Task<(WorkerChannel? Channel, HandshakeFailure? Failure)> AcceptAsync(
        Stream stream, string pluginId, int processId, string token, TimeSpan timeout, CancellationToken ct,
        SupervisorTimings? timings = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        RpcMessage? first;
        try
        {
            var frame = await Framing.ReadAsync(stream, deadline.Token).ConfigureAwait(false);
            first = frame is null ? null : ProtocolJson.Deserialize(frame);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return (null, new HandshakeFailure(FaultReason.HandshakeTimeout, false, $"No hello within {timeout.TotalSeconds:0} s."));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or EndOfStreamException)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return (null, new HandshakeFailure(FaultReason.ProtocolViolation, false, $"Unreadable first frame: {e.Message}"));
        }

        if (first is null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return (null, new HandshakeFailure(FaultReason.PipeBroken, false, "The worker closed the pipe before hello."));
        }

        HelloParams? hello = null;
        if (first is { IsRequest: true, Method: Methods.Hello })
        {
            try
            {
                hello = first.ParamsAs<HelloParams>();
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        HandshakeFailure? failure = null;
        int code = WireErrorCodes.Unauthorised;
        if (hello is null || !TokenMatches(hello.Token, token))
        {
            failure = new HandshakeFailure(FaultReason.ProtocolViolation, false,
                hello is null ? $"First message was '{first.Method ?? "a response"}', not hello." : "The hello token did not match.");
        }
        else if (hello.PluginId != pluginId)
        {
            failure = new HandshakeFailure(FaultReason.ProtocolViolation, false, $"hello named '{hello.PluginId}', expected '{pluginId}'.");
        }
        else if (hello.ProtocolVersion != ProtocolInfo.ProtocolVersion)
        {
            code = WireErrorCodes.IncompatibleVersion;
            failure = new HandshakeFailure(FaultReason.ProtocolViolation, true,
                $"Protocol {hello.ProtocolVersion} is not supported; the host speaks {ProtocolInfo.ProtocolVersion}.");
        }

        try
        {
            var answer = failure is null
                ? RpcMessage.Success(first.Id!.Value, new HelloResult(true))
                : RpcMessage.Failure(first.Id, code, failure.Detail);
            await Framing.WriteAsync(stream, ProtocolJson.Serialize(answer), ct).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            failure ??= new HandshakeFailure(FaultReason.PipeBroken, false, e.Message);
        }

        if (failure is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return (null, failure);
        }

        return (new WorkerChannel(stream, pluginId, processId, timings ?? SupervisorTimings.Default), null);
    }

    /// <summary>
    /// Requests and notifications queued to any worker since the process started, liveness pings aside.
    /// Diagnostics read it to show that a restyle reached no worker.
    /// </summary>
    public static long MessagesSent => Interlocked.Read(ref s_messagesSent);

    /// <summary>Starts reading and writing. Subscribe to <see cref="NotificationReceived"/> first.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _ = Task.Run(ReadLoopAsync);
            _ = Task.Run(WriteLoopAsync);
        }
    }

    public Outcome Notify<TParams>(string method, TParams args) =>
        Post(RpcMessage.Notification(method, args))
            ? Outcome.Success
            : Outcome.Fail(CoreErrorCode.InvalidState, $"The channel to {PluginId} is closed.");

    public async Task<Outcome<TResult>> RequestAsync<TParams, TResult>(string method, TParams args, TimeSpan timeout, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<RpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            if (!Post(RpcMessage.Request(id, method, args)))
            {
                return Outcome<TResult>.Fail(CoreErrorCode.InvalidState, $"The channel to {PluginId} is closed.");
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            RpcMessage response;
            try
            {
                response = await reply.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Outcome<TResult>.Fail(CoreErrorCode.InvalidState, $"{WireErrorCodes.Timeout} {method} timed out after {timeout.TotalMilliseconds:0} ms.");
            }

            if (response.Error is { } error)
            {
                return Outcome<TResult>.Fail(CoreErrorCode.InvalidState, $"{error.Code} {method}: {error.Message}");
            }

            try
            {
                return Outcome<TResult>.Success(response.ResultAs<TResult>()!);
            }
            catch (System.Text.Json.JsonException e)
            {
                return Outcome<TResult>.Fail(CoreErrorCode.InvalidState, $"{WireErrorCodes.InvalidParams} {method} result: {e.Message}");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Close(FaultReason.PipeBroken, "Closed by the host.");
        await _stream.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private static bool TokenMatches(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented ?? string.Empty), Encoding.UTF8.GetBytes(expected));

    private bool Post(RpcMessage message)
    {
        if (!IsOpen || !_outgoing.Writer.TryWrite(ProtocolJson.Serialize(message)))
        {
            return false;
        }

        if (message.Method is { } method && method != Methods.Ping)
        {
            Interlocked.Increment(ref s_messagesSent);
        }

        return true;
    }

    private void Close(FaultReason reason, string detail)
    {
        if (!_closed.TrySetResult(new ChannelClose(reason, detail)))
        {
            return;
        }

        _outgoing.Writer.TryComplete();
        foreach (var pending in _pending.Values)
        {
            pending.TrySetResult(RpcMessage.Failure(null, WireErrorCodes.PluginUnavailable, detail));
        }

        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Counts an event dropped over the rate and reports the count at most once a second.</summary>
    private void Drop(string method)
    {
        _dropped++;
        var now = Environment.TickCount64;
        if (now - _dropReportedAt >= 1000)
        {
            Diagnostic?.Invoke(this, $"Over {_messagesPerSecond} messages a second: dropped {_dropped} events (latest '{method}').");
            _dropped = 0;
            _dropReportedAt = now;
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var payload in _outgoing.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await Framing.WriteAsync(_stream, payload, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Close(FaultReason.PipeBroken, $"Write failed: {e.Message}");
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var frame = await Framing.ReadAsync(_stream, _stop.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    Close(FaultReason.PipeBroken, "The worker closed the pipe.");
                    return;
                }

                RpcMessage message;
                try
                {
                    message = ProtocolJson.Deserialize(frame);
                }
                catch (System.Text.Json.JsonException e)
                {
                    Close(FaultReason.ProtocolViolation, $"Malformed message: {e.Message}");
                    return;
                }

                if (message.IsResponse)
                {
                    if (_pending.TryGetValue(message.Id!.Value, out var reply))
                    {
                        reply.TrySetResult(message);
                    }

                    continue;
                }

                if (message.Method is not { } method || !WorkerMethods.Contains(method))
                {
                    Post(RpcMessage.Failure(message.Id, WireErrorCodes.MethodNotFound, $"Unknown method '{message.Method}'."));
                    Close(FaultReason.ProtocolViolation, $"Unknown method '{message.Method}'.");
                    return;
                }

                if (message.IsRequest)
                {
                    Post(RpcMessage.Failure(message.Id, WireErrorCodes.MethodNotFound, $"'{method}' is not served by this host yet."));
                    continue;
                }

                switch (_meter.Record())
                {
                    case FloodVerdict.Flood:
                        Close(FaultReason.Flood,
                            $"More than {_messagesPerSecond} messages a second for {_floodSustain.TotalSeconds:0} s.");
                        return;
                    case FloodVerdict.Over when method is not (Methods.StatePatch or Methods.ViewSet):
                        Drop(method);
                        continue;
                }

                var parameters = message.Params ?? ProtocolJson.ToElement(new { })!.Value;
                NotificationReceived?.Invoke(this, new WorkerNotification(method, parameters));
            }
        }
        catch (OperationCanceledException)
        {
            Close(FaultReason.PipeBroken, "Closed by the host.");
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or EndOfStreamException)
        {
            Close(FaultReason.PipeBroken, e.Message);
        }
        catch (InvalidDataException e)
        {
            Close(FaultReason.ProtocolViolation, e.Message);
        }
        catch (Exception e)
        {
            // A failing subscriber must not leave the channel open with nobody reading it.
            Close(FaultReason.PipeBroken, $"Reading stopped: {e.Message}");
        }
    }
}
