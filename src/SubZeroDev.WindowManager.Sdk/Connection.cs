using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Sdk;

/// <summary>The worker end of the pipe: one writer loop, request correlation, and a read loop that hands messages on.</summary>
internal sealed class Connection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<long, TaskCompletionSource<RpcMessage>> _pending = new();
    private readonly Task _writer;
    private long _nextId;

    public Connection(Stream stream)
    {
        _stream = stream;
        _writer = Task.Run(WriteLoopAsync);
    }

    public bool IsOpen { get; private set; } = true;

    public void Notify(string method, object? parameters) => Post(RpcMessage.Notification(method, parameters));

    public void Respond(long id, object? result) => Post(RpcMessage.Success(id, result));

    public void Fail(long? id, int code, string message, JsonNode? data = null) => Post(RpcMessage.Failure(id, code, message, data));

    public async Task<RpcMessage> RequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<RpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            Post(RpcMessage.Request(id, method, parameters));
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Reads the next message that is not a response; responses complete their pending request. Null at end of stream.</summary>
    public async Task<RpcMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await Framing.ReadAsync(_stream, cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                Close();
                return null;
            }

            var message = ProtocolJson.Deserialize(frame);
            if (message.Method is null && message.Id is { } id)
            {
                if (_pending.TryRemove(id, out var completion))
                {
                    completion.TrySetResult(message);
                }

                continue;
            }

            return message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _outgoing.Writer.TryComplete();
        try
        {
            await _writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or ObjectDisposedException)
        {
        }

        Close();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private void Post(RpcMessage message)
    {
        if (IsOpen)
        {
            _outgoing.Writer.TryWrite(ProtocolJson.Serialize(message));
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var payload in _outgoing.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await Framing.WriteAsync(_stream, payload).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidDataException)
        {
            Close();
        }
    }

    private void Close()
    {
        IsOpen = false;
        foreach (var (_, completion) in _pending)
        {
            completion.TrySetException(new IOException("The connection to the host closed."));
        }
    }
}
