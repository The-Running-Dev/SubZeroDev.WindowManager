using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace SubZeroDev.WindowManager.Host.Core.Diagnostics;

public interface IPluginLogs
{
    /// <summary>Appends one line to the plugin's own log; <paramref name="level"/> is free text (Information, stderr, host).</summary>
    void Write(string pluginId, string level, string message);
}

/// <summary>
/// Appends lines to <c>logs\plugins\&lt;pluginId&gt;-&lt;yyyyMMdd&gt;.log</c>: what the worker logs, its
/// stderr, and what the host rejected from it. Like <see cref="HostLog"/>, callers only enqueue.
/// </summary>
public sealed class PluginLogs : IPluginLogs, IAsyncDisposable
{
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Channel<(string PluginId, string Line)> _lines =
        Channel.CreateUnbounded<(string, string)>(new UnboundedChannelOptions { SingleReader = true });
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly Task _writer;

    public PluginLogs(string logsDirectory, Func<DateTimeOffset>? clock = null)
    {
        _directory = Path.Combine(logsDirectory, "plugins");
        _clock = clock ?? (() => DateTimeOffset.Now);
        _writer = Task.Run(WriteLoopAsync);
    }

    public string PathFor(string pluginId, DateTimeOffset day) =>
        Path.Combine(_directory, $"{pluginId}-{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

    public void Write(string pluginId, string level, string message)
    {
        var now = _clock();
        var flat = message.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        _lines.Writer.TryWrite((pluginId, $"{now.ToString("O", CultureInfo.InvariantCulture)} {level} {flat}"));
    }

    public async ValueTask DisposeAsync()
    {
        _lines.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    private async Task WriteLoopAsync()
    {
        while (await _lines.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var batches = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
            while (_lines.Reader.TryRead(out var entry))
            {
                if (!batches.TryGetValue(entry.PluginId, out var batch))
                {
                    batches[entry.PluginId] = batch = new StringBuilder();
                }

                batch.Append(entry.Line).Append('\n');
            }

            foreach (var (pluginId, batch) in batches)
            {
                try
                {
                    Directory.CreateDirectory(_directory);
                    await File.AppendAllTextAsync(PathFor(pluginId, _clock()), batch.ToString(), Utf8NoBom).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // Best effort, as for the host log.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
