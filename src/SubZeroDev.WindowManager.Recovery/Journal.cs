using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SubZeroDev.WindowManager.Recovery;

/// <summary>Reads and writes <c>recovery\session-&lt;sessionId&gt;.json</c>. A write replaces the file in one move after a flush to disk (I6).</summary>
public static class Journal
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private const int Attempts = 5;
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(50);

    public static string PathForSession(string dataRoot, int sessionId) =>
        Path.Combine(dataRoot, "recovery", $"session-{sessionId.ToString(CultureInfo.InvariantCulture)}.json");

    /// <summary>
    /// The journal, or null when there is none; anything that is not a valid document is <see cref="RecoveryErrorCode.JournalUnreadable"/>.
    /// The file is opened so that the other process can replace it meanwhile, and a sharing conflict is retried before it counts.
    /// </summary>
    public static Outcome<JournalDocument?> Read(string path)
    {
        try
        {
            if (ReadBytes(path) is not { } bytes)
            {
                return Outcome<JournalDocument?>.Success(null);
            }

            var doc = JsonSerializer.Deserialize<JournalDocument>(bytes, Options);
            if (doc is null || doc.SchemaVersion != JournalDocument.CurrentSchemaVersion || doc.Entries is null
                || doc.Entries.Any(entry => entry?.Payload is null || entry.MonitorId is null))
            {
                return Outcome<JournalDocument?>.Fail(RecoveryErrorCode.JournalUnreadable,
                    $"{path} is not a schema {JournalDocument.CurrentSchemaVersion} journal.");
            }

            return Outcome<JournalDocument?>.Success(doc);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return Outcome<JournalDocument?>.Fail(RecoveryErrorCode.JournalUnreadable, $"{path}: {e.Message}");
        }
    }

    private static byte[]? ReadBytes(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < Attempts)
            {
                Thread.Sleep(Retry);
            }
        }
    }

    /// <summary>Writes beside the journal, flushes to disk, then replaces the journal in one move (retried while a reader has it open).</summary>
    public static Outcome Write(string path, JournalDocument doc)
    {
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, doc, Options);
                stream.Flush(flushToDisk: true);
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return Outcome.Success;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < Attempts)
                {
                    Thread.Sleep(Retry);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            return Outcome.Fail(RecoveryErrorCode.JournalWriteFailed, $"{path}: {e.Message}");
        }
    }
}
