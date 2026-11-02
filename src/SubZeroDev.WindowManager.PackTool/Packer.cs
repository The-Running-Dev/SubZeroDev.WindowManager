using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.PackTool;

/// <summary>
/// <c>wm-pack &lt;output-folder&gt; --manifest plugin.json --out package.zip</c>: checks the manifest and that the
/// entry it names was built, then writes a zip with <c>plugin.json</c> at its root beside every file of the
/// output folder. Entries are sorted and carry a fixed time, so the same input gives the same bytes.
/// </summary>
public static class Packer
{
    public const int Ok = 0;
    public const int InvalidManifest = 1;
    public const int EntryMissing = 2;
    public const int IoFailure = 3;

    private const string ManifestName = "plugin.json";

    /// <summary>The time every entry carries; the earliest a zip can hold, so it says nothing about the build.</summary>
    private static readonly DateTimeOffset EntryTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        string? folder = null, manifestPath = null, outPath = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--manifest" when i + 1 < args.Count: manifestPath = args[++i]; break;
                case "--out" when i + 1 < args.Count: outPath = args[++i]; break;
                case var value when !value.StartsWith("--", StringComparison.Ordinal) && folder is null: folder = value; break;
                default:
                    error.WriteLine($"Unexpected argument '{args[i]}'.");
                    Usage(error);
                    return IoFailure;
            }
        }

        if (folder is null || outPath is null)
        {
            Usage(error);
            return IoFailure;
        }

        manifestPath ??= Path.Combine(folder, ManifestName);
        try
        {
            var manifestText = File.ReadAllText(manifestPath);
            var problems = new List<string>();
            PluginManifest? manifest = null;
            try
            {
                if (JsonNode.Parse(manifestText, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) is JsonObject document)
                {
                    manifest = PluginManifestReader.Parse(document, problems);
                }
                else
                {
                    problems.Add("plugin.json is not a JSON object.");
                }
            }
            catch (JsonException e)
            {
                problems.Add($"plugin.json is not valid JSON: {e.Message}");
            }

            if (manifest is null)
            {
                foreach (var problem in problems)
                {
                    error.WriteLine($"{manifestPath}: {problem}");
                }

                return InvalidManifest;
            }

            var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            var entry = Path.GetFullPath(Path.Combine(root, manifest.Entry.Path));
            if (!entry.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                error.WriteLine($"{manifestPath}: entry.path '{manifest.Entry.Path}' leaves the package folder.");
                return InvalidManifest;
            }

            if (!File.Exists(entry))
            {
                error.WriteLine($"entry.path '{manifest.Entry.Path}' was not found in {root}.");
                return EntryMissing;
            }

            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(file => (Full: file, Name: Path.GetRelativePath(root, file).Replace('\\', '/')))
                .Where(file => !string.Equals(file.Name, ManifestName, StringComparison.OrdinalIgnoreCase))
                .Where(file => !string.Equals(Path.GetFullPath(file.Full), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.Name, StringComparer.Ordinal)
                .ToList();
            var directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = outPath + ".partial";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                Add(zip, ManifestName, File.ReadAllBytes(manifestPath));
                foreach (var file in files)
                {
                    Add(zip, file.Name, File.ReadAllBytes(file.Full));
                }
            }

            File.Move(temporary, outPath, overwrite: true);
            output.WriteLine($"Packed {manifest.Id} {manifest.Version} ({files.Count + 1} files) into {outPath}.");
            return Ok;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error.WriteLine(e.Message);
            return IoFailure;
        }
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        var item = zip.CreateEntry(name, CompressionLevel.Optimal);
        item.LastWriteTime = EntryTime;
        using var stream = item.Open();
        stream.Write(bytes);
    }

    private static void Usage(TextWriter error) =>
        error.WriteLine("Usage: wm-pack <output-folder> [--manifest plugin.json] --out <package.zip>");
}
