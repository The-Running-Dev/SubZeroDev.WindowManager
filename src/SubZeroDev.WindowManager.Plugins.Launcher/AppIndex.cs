using System.Text;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.Plugins.Launcher;

/// <summary>An application the launcher can start: a shortcut's target, or a packaged app by its AUMID.</summary>
public sealed record AppEntry(string Key, string Name, string? Exe = null, string? Aumid = null, string? Args = null)
{
    public JsonObject ToJson()
    {
        var item = new JsonObject { ["key"] = Key, ["name"] = Name };
        if (Exe is not null)
        {
            item["exe"] = Exe;
        }

        if (Aumid is not null)
        {
            item["aumid"] = Aumid;
        }

        if (!string.IsNullOrEmpty(Args))
        {
            item["args"] = Args;
        }

        return item;
    }
}

/// <summary>
/// Builds the application index and searches it. Shortcuts come from the per-user and all-users Start
/// menu Programs folders, kept only while their target exists; packaged apps come from
/// <c>shell:AppsFolder</c>, whose parsing names are AUMIDs for packaged apps (they contain <c>!</c>).
/// Names that appear twice keep the first, so a shortcut wins over a packaged entry of the same name.
/// </summary>
public static class AppIndex
{
    public const int MaxResults = 12;

    /// <summary>Builds the index on an STA thread, as the shell's folder objects expect.</summary>
    public static Task<IReadOnlyList<AppEntry>> BuildAsync(IEnumerable<string> shortcutFolders)
    {
        var folders = shortcutFolders.ToList();
        var done = new TaskCompletionSource<IReadOnlyList<AppEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(Build(folders));
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        })
        {
            IsBackground = true,
            Name = "Launcher index",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    public static IReadOnlyList<string> StartMenuFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
    ];

    /// <summary>
    /// Entries whose name contains <paramref name="query"/>, ignoring case: those starting with it first,
    /// each group by name, at most <see cref="MaxResults"/>. An empty query matches nothing.
    /// </summary>
    public static IReadOnlyList<AppEntry> Search(IEnumerable<AppEntry> index, string query)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return [];
        }

        return index
            .Select(entry => (Entry: entry, At: entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase)))
            .Where(match => match.At >= 0)
            .OrderBy(match => match.At == 0 ? 0 : 1)
            .ThenBy(match => match.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxResults)
            .Select(match => match.Entry)
            .ToList();
    }

    private static List<AppEntry> Build(IReadOnlyList<string> folders)
    {
        var entries = new List<AppEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in folders.SelectMany(Shortcuts).Concat(PackagedApps()))
        {
            if (names.Add(entry.Name))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static IEnumerable<AppEntry> Shortcuts(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .ToList();
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            if (ReadShortcut(file) is { } entry)
            {
                yield return entry;
            }
        }
    }

    private static AppEntry? ReadShortcut(string path)
    {
        try
        {
            var link = Native.LoadShortcut(path);
            var target = new StringBuilder(Native.MaxPath);
            link.GetPath(target, target.Capacity, 0, 0);
            var args = new StringBuilder(Native.MaxPath);
            link.GetArguments(args, args.Capacity);
            var exe = target.ToString();
            return exe.Length > 0 && File.Exists(exe)
                ? new AppEntry(path, Path.GetFileNameWithoutExtension(path), Exe: exe, Args: args.ToString())
                : null;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or IOException
            or InvalidCastException)
        {
            return null;
        }
    }

    private static IEnumerable<AppEntry> PackagedApps()
    {
        var found = new List<AppEntry>();
        try
        {
            Native.SHCreateItemFromParsingName("shell:AppsFolder", 0, Native.IID_IShellItem, out var folder);
            folder.BindToHandler(0, Native.BHID_EnumItems, Native.IID_IEnumShellItems, out var handler);
            var items = (Native.IEnumShellItems)handler;
            while (items.Next(1, out var item, out var fetched) == Native.S_OK && fetched == 1 && item is not null)
            {
                var aumid = Native.DisplayName(item, Native.SIGDN_PARENTRELATIVEPARSING);
                if (aumid.Contains('!', StringComparison.Ordinal))
                {
                    var name = Native.DisplayName(item, Native.SIGDN_NORMALDISPLAY);
                    found.Add(new AppEntry("aumid:" + aumid, name.Length > 0 ? name : aumid, Aumid: aumid));
                }
            }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidCastException or UnauthorizedAccessException)
        {
            // Without the apps folder the index still has every shortcut.
        }

        return found;
    }
}
