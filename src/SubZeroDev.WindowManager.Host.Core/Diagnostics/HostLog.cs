using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace SubZeroDev.WindowManager.Host.Core.Diagnostics;

public enum LogLevel { Info, Warning, Error }

public interface IHostLog
{
    void Write(LogLevel level, string area, string message);
}

/// <summary>
/// Appends lines to <c>logs\host-&lt;yyyyMMdd&gt;.log</c>. Callers only enqueue; one background task
/// does the file IO, so logging is safe from the UI thread (I9).
/// </summary>
public sealed class HostLog : IHostLog, IAsyncDisposable
{
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly Task _writer;

    public HostLog(string directory, Func<DateTimeOffset>? clock = null)
    {
        _directory = directory;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _writer = Task.Run(WriteLoopAsync);
    }

    public string PathFor(DateTimeOffset day) =>
        Path.Combine(_directory, $"host-{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

    public void Write(LogLevel level, string area, string message) => _lines.Writer.TryWrite(Line(level, area, message));

    /// <summary>Appends on the calling thread, for a process that is about to end and cannot wait for the writer.</summary>
    public void WriteNow(LogLevel level, string area, string message)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.AppendAllText(PathFor(_clock()), Line(level, area, message) + "\n", Utf8NoBom);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort, as for the writer.
        }
    }

    private string Line(LogLevel level, string area, string message)
    {
        var flat = message.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return $"{_clock().ToString("O", CultureInfo.InvariantCulture)} {level} [{area}] {flat}";
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
            var batch = new StringBuilder();
            while (_lines.Reader.TryRead(out var line))
            {
                batch.Append(line).Append('\n');
            }

            try
            {
                Directory.CreateDirectory(_directory);
                await File.AppendAllTextAsync(PathFor(_clock()), batch.ToString(), Utf8NoBom).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The log is best effort; a locked or full disk must not take the host down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
