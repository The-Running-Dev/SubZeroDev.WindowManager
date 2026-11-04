using System.Globalization;
using System.Text;

namespace SubZeroDev.WindowManager.Plugins.RunningWindows;

/// <summary>One destination of a jump list: something to launch.</summary>
/// <param name="Category">Where it came from: <c>recent</c> (an automatic destination) or <c>custom</c> (a program's own list).</param>
/// <param name="Title">The text the entry shows.</param>
/// <param name="Target">The file, folder or program it opens.</param>
/// <param name="Args">The arguments to start <paramref name="Target"/> with; null for none.</param>
public sealed record JumpListEntry(string Category, string Title, string Target, string? Args);

/// <summary>
/// Reads a program's jump list from the destination files Windows keeps under the user's profile, without Explorer
/// (design question 10). Windows names a file by the CRC-64 of the upper-cased application id; an application without an
/// explicit id has its executable path as the id, with a known folder written as its GUID. Only what such a file holds is
/// readable: an application whose id is not one of the candidates tried, or whose list Windows has not written, shows none.
/// </summary>
public static class JumpLists
{
    private const ulong Polynomial = 0x92C64265D32139A4UL;
    private const int MostEntries = 10;

    private static readonly (Guid Id, Func<string?> Path)[] KnownFolders =
    [
        (new Guid("1AC14E77-02E7-4E5D-B744-2EB1AE5198B7"), () => Environment.GetFolderPath(Environment.SpecialFolder.System)),
        (new Guid("F38BF404-1D43-42F2-9305-67DE0B28FC23"), () => Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
        (new Guid("6D809377-6AF0-444B-8957-A3773F02200E"), () => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
        (new Guid("7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E"), () => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
        (new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"), () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
    ];

    /// <summary>The CRC-64 Windows names a destination file after: reflected, init all ones, no final xor, over the UTF-16 of the upper-cased id.</summary>
    /// <param name="appId">The application id.</param>
    public static ulong Hash(string appId)
    {
        ArgumentNullException.ThrowIfNull(appId);
        var crc = ulong.MaxValue;
        foreach (var b in Encoding.Unicode.GetBytes(appId.ToUpperInvariant()))
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
            }
        }

        return crc;
    }

    /// <summary>The 16 lower-case hex digits that name the destination files of an application id.</summary>
    /// <param name="appId">The application id.</param>
    public static string FileStem(string appId) => Hash(appId).ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>The application ids a program may have, most likely first: its explicit id, then its path with a known folder as a GUID, then the plain path.</summary>
    /// <param name="exe">The full path of the program; null when unknown.</param>
    /// <param name="aumid">The explicit application user model id; null when the window has none.</param>
    public static IReadOnlyList<string> Candidates(string? exe, string? aumid)
    {
        var ids = new List<string>();
        if (!string.IsNullOrEmpty(aumid))
        {
            ids.Add(aumid);
        }

        if (!string.IsNullOrEmpty(exe))
        {
            foreach (var (id, folder) in KnownFolders)
            {
                if (folder() is { Length: > 0 } root
                    && exe.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    ids.Add("{" + id.ToString("D").ToUpperInvariant() + "}\\" + exe[(root.TrimEnd('\\').Length + 1)..]);
                }
            }

            ids.Add(exe);
        }

        return ids;
    }

    /// <summary>The entries of the jump list the destination files under <paramref name="root"/> hold for the first matching id; empty when none do.</summary>
    /// <param name="root">The folder holding <c>AutomaticDestinations</c> and <c>CustomDestinations</c>.</param>
    /// <param name="exe">The full path of the program; null when unknown.</param>
    /// <param name="aumid">The explicit application user model id; null when the window has none.</param>
    public static IReadOnlyList<JumpListEntry> Read(string root, string? exe, string? aumid)
    {
        foreach (var id in Candidates(exe, aumid))
        {
            var stem = FileStem(id);
            var automatic = Path.Combine(root, "AutomaticDestinations", stem + ".automaticDestinations-ms");
            var custom = Path.Combine(root, "CustomDestinations", stem + ".customDestinations-ms");
            if (!File.Exists(automatic) && !File.Exists(custom))
            {
                continue;
            }

            var entries = new List<JumpListEntry>();
            entries.AddRange(ReadCustom(custom));
            entries.AddRange(ReadAutomatic(automatic));
            return entries;
        }

        return [];
    }

    /// <summary>The destinations of an automatic destination file, most recently used first, pinned ones before the rest.</summary>
    /// <param name="path">The <c>.automaticDestinations-ms</c> file.</param>
    public static IReadOnlyList<JumpListEntry> ReadAutomatic(string path)
    {
        if (Bytes(path) is not { } file || CompoundFile.Read(file) is not { } streams)
        {
            return [];
        }

        var order = streams.TryGetValue("DestList", out var destList) ? DestListOrder(destList, streams.Keys) : [];
        var names = order.Count > 0
            ? order
            : [.. streams.Keys.Where(name => name != "DestList").OrderByDescending(Number)];
        var entries = new List<JumpListEntry>();
        foreach (var name in names)
        {
            if (streams.TryGetValue(name, out var link) && ShellLink.Parse(link, "recent") is { } entry)
            {
                entries.Add(entry);
                if (entries.Count == MostEntries)
                {
                    break;
                }
            }
        }

        return entries;
    }

    /// <summary>The links of a custom destination file: every shell link it holds, in file order.</summary>
    /// <param name="path">The <c>.customDestinations-ms</c> file.</param>
    public static IReadOnlyList<JumpListEntry> ReadCustom(string path)
    {
        if (Bytes(path) is not { } file)
        {
            return [];
        }

        var entries = new List<JumpListEntry>();
        foreach (var link in ShellLink.Split(file))
        {
            if (ShellLink.Parse(link, "custom") is { } entry)
            {
                entries.Add(entry);
                if (entries.Count == MostEntries)
                {
                    break;
                }
            }
        }

        return entries;
    }

    /// <summary>The stream names of a <c>DestList</c> stream in the order it lists them: pinned first, then as written (newest first).</summary>
    /// <param name="destList">The <c>DestList</c> stream.</param>
    /// <param name="present">The names of the streams the file holds; an entry naming another is skipped.</param>
    internal static IReadOnlyList<string> DestListOrder(byte[] destList, IEnumerable<string> present)
    {
        var have = new HashSet<string>(present, StringComparer.Ordinal);
        var names = new List<(string Name, bool Pinned)>();
        try
        {
            var version = BitConverter.ToInt32(destList, 0);
            var count = BitConverter.ToInt32(destList, 4);
            var at = 32;
            if (version < 3)
            {
                return [];
            }

            for (var i = 0; i < count && at + 130 <= destList.Length; i++)
            {
                var name = BitConverter.ToInt32(destList, at + 88).ToString("x", CultureInfo.InvariantCulture);
                var pinned = BitConverter.ToInt32(destList, at + 108) != -1;
                var length = BitConverter.ToUInt16(destList, at + 128);
                if (have.Contains(name))
                {
                    names.Add((name, pinned));
                }

                at += 130 + (length * 2) + (version >= 4 ? 4 : 0);
            }
        }
        catch (ArgumentException)
        {
            return [];
        }

        return [.. names.Where(n => n.Pinned).Select(n => n.Name), .. names.Where(n => !n.Pinned).Select(n => n.Name)];
    }

    private static int Number(string name) => int.TryParse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number) ? number : -1;

    private static byte[]? Bytes(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is 0 or > 16 * 1024 * 1024)
            {
                return null;
            }

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>A reader for the Microsoft compound file (OLE structured storage) an automatic destination file is.</summary>
public static class CompoundFile
{
    private static readonly byte[] Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Every stream of the file by name; null when the bytes are not a compound file or are damaged.</summary>
    /// <param name="file">The whole file.</param>
    public static IReadOnlyDictionary<string, byte[]>? Read(byte[] file)
    {
        try
        {
            return ReadCore(file);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    private static Dictionary<string, byte[]>? ReadCore(byte[] f)
    {
        if (f.Length < 512 || !f.AsSpan(0, 8).SequenceEqual(Signature))
        {
            return null;
        }

        var shift = BitConverter.ToUInt16(f, 0x1E);
        var miniShift = BitConverter.ToUInt16(f, 0x20);
        if (shift is not (9 or 12) || miniShift != 6)
        {
            return null;
        }

        var size = 1 << shift;
        var perSector = size / 4;
        var fatCount = BitConverter.ToInt32(f, 0x2C);
        var directoryStart = BitConverter.ToInt32(f, 0x30);
        var cutoff = BitConverter.ToInt32(f, 0x38);
        var miniFatStart = BitConverter.ToInt32(f, 0x3C);
        var difatStart = BitConverter.ToInt32(f, 0x44);
        var difatCount = BitConverter.ToInt32(f, 0x48);

        int Int(long at) => at < 0 || at + 4 > f.Length ? throw new InvalidDataException("Past the end of the file.") : BitConverter.ToInt32(f, (int)at);
        long Offset(int sector) => ((long)sector + 1) << shift;

        var fatSectors = new List<int>();
        for (var i = 0; i < 109 && fatSectors.Count < fatCount; i++)
        {
            if (Int(0x4C + (4 * i)) is var id and >= 0)
            {
                fatSectors.Add(id);
            }
        }

        var next = difatStart;
        for (var d = 0; d < difatCount && next >= 0 && fatSectors.Count < fatCount; d++)
        {
            var at = Offset(next);
            for (var i = 0; i < perSector - 1 && fatSectors.Count < fatCount; i++)
            {
                if (Int(at + (4 * i)) is var id and >= 0)
                {
                    fatSectors.Add(id);
                }
            }

            next = Int(at + size - 4);
        }

        var fat = new int[fatSectors.Count * perSector];
        for (var s = 0; s < fatSectors.Count; s++)
        {
            for (var j = 0; j < perSector; j++)
            {
                fat[(s * perSector) + j] = Int(Offset(fatSectors[s]) + (4 * j));
            }
        }

        byte[] Chain(int start, long length)
        {
            using var output = new MemoryStream();
            var sector = start;
            for (var seen = 0; sector >= 0 && sector < fat.Length && seen <= fat.Length; seen++)
            {
                var at = Offset(sector);
                if (at < 0 || at + size > f.Length)
                {
                    throw new InvalidDataException("A sector lies past the end of the file.");
                }

                output.Write(f, (int)at, size);
                sector = fat[sector];
            }

            var bytes = output.ToArray();
            return length >= 0 && length < bytes.Length ? bytes[..(int)length] : bytes;
        }

        var directory = Chain(directoryStart, -1);
        if (directory.Length < 128)
        {
            return null;
        }

        var rootStart = BitConverter.ToInt32(directory, 116);
        var rootSize = BitConverter.ToUInt32(directory, 120);
        var miniStream = rootStart >= 0 ? Chain(rootStart, rootSize) : [];
        var miniFat = miniFatStart >= 0 ? Chain(miniFatStart, -1) : [];

        byte[] MiniChain(int start, long length)
        {
            using var output = new MemoryStream();
            var sector = start;
            var limit = miniFat.Length / 4;
            for (var seen = 0; sector >= 0 && sector < limit && seen <= limit; seen++)
            {
                var at = (long)sector << 6;
                if (at + 64 > miniStream.Length)
                {
                    throw new InvalidDataException("A mini sector lies past the end of the mini stream.");
                }

                output.Write(miniStream, (int)at, 64);
                sector = BitConverter.ToInt32(miniFat, sector * 4);
            }

            var bytes = output.ToArray();
            return length < bytes.Length ? bytes[..(int)length] : bytes;
        }

        var streams = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var at = 128; at + 128 <= directory.Length; at += 128)
        {
            if (directory[at + 66] != 2)
            {
                continue;
            }

            var nameBytes = Math.Max(0, BitConverter.ToUInt16(directory, at + 64) - 2);
            var name = Encoding.Unicode.GetString(directory, at, Math.Min(nameBytes, 64));
            var start = BitConverter.ToInt32(directory, at + 116);
            var length = BitConverter.ToUInt32(directory, at + 120);
            streams[name] = length < cutoff ? MiniChain(start, length) : Chain(start, length);
        }

        return streams;
    }
}

/// <summary>A reader for the parts of a shell link (<c>.lnk</c>) a jump list entry needs.</summary>
public static class ShellLink
{
    private static readonly byte[] Header = [0x4C, 0, 0, 0, 0x01, 0x14, 0x02, 0, 0, 0, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46];

    /// <summary>The links in a custom destination file, found by their header and cut where the next begins.</summary>
    /// <param name="file">The whole file.</param>
    public static IReadOnlyList<byte[]> Split(byte[] file)
    {
        var starts = new List<int>();
        for (var i = 0; i + Header.Length <= file.Length; i++)
        {
            if (file.AsSpan(i, Header.Length).SequenceEqual(Header))
            {
                starts.Add(i);
            }
        }

        var links = new List<byte[]>();
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : file.Length;
            links.Add(file[starts[i]..end]);
        }

        return links;
    }

    /// <summary>The entry a link stands for; null when the link names nothing to launch.</summary>
    /// <param name="link">The bytes of the link.</param>
    /// <param name="category">The category to give the entry.</param>
    public static JumpListEntry? Parse(byte[] link, string category)
    {
        try
        {
            return ParseCore(link, category);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException)
        {
            return null;
        }
    }

    private static JumpListEntry? ParseCore(byte[] b, string category)
    {
        if (b.Length < 76 || BitConverter.ToUInt32(b, 0) != 0x4C)
        {
            return null;
        }

        var flags = BitConverter.ToUInt32(b, 20);
        var at = 76;
        if ((flags & 0x1) != 0)
        {
            at += 2 + BitConverter.ToUInt16(b, at);
        }

        string? target = null;
        if ((flags & 0x2) != 0)
        {
            var infoSize = BitConverter.ToInt32(b, at);
            var headerSize = BitConverter.ToInt32(b, at + 4);
            var infoFlags = BitConverter.ToUInt32(b, at + 8);
            if ((infoFlags & 1) != 0)
            {
                var unicodeOffset = headerSize >= 0x24 ? BitConverter.ToInt32(b, at + 28) : 0;
                target = unicodeOffset > 0
                    ? Utf16(b, at + unicodeOffset)
                    : Ansi(b, at + BitConverter.ToInt32(b, at + 16));
            }

            at += infoSize;
        }

        var unicode = (flags & 0x80) != 0;

        string? Text(uint flag)
        {
            if ((flags & flag) == 0)
            {
                return null;
            }

            var count = BitConverter.ToUInt16(b, at);
            at += 2;
            var bytes = unicode ? count * 2 : count;
            var text = unicode ? Encoding.Unicode.GetString(b, at, bytes) : Encoding.Latin1.GetString(b, at, bytes);
            at += bytes;
            return text;
        }

        var name = Text(0x4);
        var relative = Text(0x8);
        _ = Text(0x10);
        var args = Text(0x20);
        target ??= relative;
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        var title = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(target.TrimEnd('\\')) : name;
        return new JumpListEntry(category, title.Length > 0 ? title : target, target, string.IsNullOrWhiteSpace(args) ? null : args);
    }

    private static string Ansi(byte[] b, int at)
    {
        var end = Array.IndexOf(b, (byte)0, at);
        return Encoding.Latin1.GetString(b, at, (end < 0 ? b.Length : end) - at);
    }

    private static string Utf16(byte[] b, int at)
    {
        var end = at;
        while (end + 1 < b.Length && (b[end] != 0 || b[end + 1] != 0))
        {
            end += 2;
        }

        return Encoding.Unicode.GetString(b, at, end - at);
    }
}
