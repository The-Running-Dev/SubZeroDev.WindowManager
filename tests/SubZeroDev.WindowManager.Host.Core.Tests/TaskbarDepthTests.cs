using System.Buffers.Binary;
using System.Text;
using SubZeroDev.WindowManager.Host.Platform;
using SubZeroDev.WindowManager.Plugins.RunningWindows;
using SubZeroDev.WindowManager.Sdk;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>S29: grouping, the jump-list reader and the taskbar back channel, without a desktop.</summary>
public sealed class TaskbarDepthTests
{
    // --- Jump lists (S29.4) ---------------------------------------------------------------------------------------

    [Fact]
    public void S29_4_the_destination_file_name_is_the_crc64_of_the_upper_cased_id()
    {
        Assert.Equal("f01b4d95cf55d32a", JumpLists.FileStem("Microsoft.Windows.Explorer"));
        Assert.Equal(JumpLists.FileStem("Microsoft.Windows.Explorer"), JumpLists.FileStem("microsoft.windows.explorer"));
        Assert.NotEqual(JumpLists.Hash("A"), JumpLists.Hash("B"));
    }

    [Fact]
    public void S29_4_candidates_put_the_explicit_id_first_and_the_plain_path_last()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

        var ids = JumpLists.Candidates(exe, "My.App");

        Assert.Equal("My.App", ids[0]);
        Assert.Equal(exe, ids[^1]);
        Assert.Contains("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\notepad.exe", ids);
        Assert.Empty(JumpLists.Candidates(null, null));
        Assert.Equal(["C:\\tools\\a.exe"], JumpLists.Candidates("C:\\tools\\a.exe", null));
    }

    [Fact]
    public void S29_4_a_shell_link_gives_its_name_target_and_arguments()
    {
        var link = Link(0x4 | 0x8 | 0x20 | 0x80, ("Open project", null), (@"..\code.exe", null), (null, null), ("--folder C:\\src", null));

        var entry = ShellLink.Parse(link, "custom");

        Assert.NotNull(entry);
        Assert.Equal("custom", entry.Category);
        Assert.Equal("Open project", entry.Title);
        Assert.Equal(@"..\code.exe", entry.Target);
        Assert.Equal("--folder C:\\src", entry.Args);
    }

    [Fact]
    public void S29_4_a_link_without_a_name_is_titled_by_its_file_and_one_without_a_target_is_dropped()
    {
        var unnamed = ShellLink.Parse(Link(0x8 | 0x80, (@"C:\docs\notes.txt", null)), "recent");
        var empty = ShellLink.Parse(Link(0x80), "recent");

        Assert.Equal("notes.txt", unnamed?.Title);
        Assert.Null(unnamed?.Args);
        Assert.Null(empty);
        Assert.Null(ShellLink.Parse([1, 2, 3], "recent"));
    }

    [Fact]
    public void S29_4_a_link_info_path_is_the_target()
    {
        var path = Encoding.Latin1.GetBytes(@"C:\Apps\tool.exe");
        var info = new byte[0x1C + path.Length + 2];
        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(0), info.Length);
        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(4), 0x1C);
        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(12), 0x1C);
        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(16), 0x1C);
        path.CopyTo(info, 0x1C);

        var link = Header(0x2).Concat(info).ToArray();

        Assert.Equal(@"C:\Apps\tool.exe", ShellLink.Parse(link, "recent")?.Target);
    }

    [Fact]
    public void S29_4_a_custom_file_is_split_at_each_link_header()
    {
        var first = Link(0x4 | 0x8 | 0x80, ("One", null), (@"C:\a.exe", null));
        var second = Link(0x4 | 0x8 | 0x80, ("Two", null), (@"C:\b.exe", null));
        var file = first.Concat(second).ToArray();

        var links = ShellLink.Split(file);

        Assert.Equal(2, links.Count);
        Assert.Equal("Two", ShellLink.Parse(links[1], "custom")?.Title);
    }

    [Fact]
    public void S29_4_the_dest_list_orders_pinned_entries_first_and_skips_missing_streams()
    {
        var list = DestList(3, (2, -1), (3, -1), (1, 0), (9, -1));

        var order = JumpLists.DestListOrder(list, ["1", "2", "3", "DestList"]);

        Assert.Equal(["1", "2", "3"], order);
        Assert.Empty(JumpLists.DestListOrder(DestList(2, (1, -1)), ["1"]));
        Assert.Empty(JumpLists.DestListOrder([1, 2], ["1"]));
    }

    [Fact]
    public void S29_4_a_compound_file_gives_its_mini_and_regular_streams()
    {
        var small = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var big = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();

        var streams = CompoundFile.Read(Cfb(small, big));

        Assert.NotNull(streams);
        Assert.Equal(small, streams["1"]);
        Assert.Equal(big, streams["big"]);
        Assert.Null(CompoundFile.Read(new byte[600]));
        Assert.Null(CompoundFile.Read([1, 2, 3]));
    }

    [Fact]
    public void S29_4_an_automatic_file_lists_its_links_in_dest_list_order()
    {
        using var scratch = new Scratch();
        var link = Link(0x4 | 0x8 | 0x80, ("Doc", null), (@"C:\a.txt", null));
        var path = Path.Combine(scratch.DataRoot, "auto.automaticDestinations-ms");
        File.WriteAllBytes(path, Cfb(link, new byte[4096]));

        // The file holds the link as stream "1" and an unparseable "big"; with no DestList the streams are read newest first.
        var entries = JumpLists.ReadAutomatic(path);

        Assert.Single(entries);
        Assert.Equal("Doc", entries[0].Title);
        Assert.Equal("recent", entries[0].Category);
    }

    [Fact]
    public void S29_4_read_tries_each_candidate_and_reports_nothing_when_no_file_exists()
    {
        using var scratch = new Scratch();
        var root = scratch.DataRoot;
        Directory.CreateDirectory(Path.Combine(root, "CustomDestinations"));
        var exe = @"C:\NoSuchFolder\app.exe";
        var link = Link(0x4 | 0x8 | 0x80, ("Open", null), (exe, null));
        File.WriteAllBytes(Path.Combine(root, "CustomDestinations", JumpLists.FileStem(exe) + ".customDestinations-ms"), link);

        var found = JumpLists.Read(root, exe, "Not.An.Id");
        var missing = JumpLists.Read(root, @"C:\NoSuchFolder\other.exe", null);

        Assert.Single(found);
        Assert.Equal("custom", found[0].Category);
        Assert.Empty(missing);
        Assert.Empty(JumpLists.Read(Path.Combine(root, "absent"), exe, null));
    }

    // --- Grouping (S29.1) -----------------------------------------------------------------------------------------

    [Fact]
    public void S29_1_never_gives_one_button_per_window()
    {
        var windows = new[] { Window(1, "a"), Window(2, "a"), Window(3, "b") };

        var buttons = TaskGroups.Build(windows, "never");

        Assert.Equal(["1", "2", "3"], buttons.Select(b => b.Key));
    }

    [Fact]
    public void S29_1_always_groups_by_application_in_first_seen_order_and_a_lone_window_keeps_its_handle()
    {
        var windows = new[] { Window(1, "a"), Window(2, "b"), Window(3, "a") };

        var buttons = TaskGroups.Build(windows, "always");

        Assert.Equal(["g:a", "2"], buttons.Select(b => b.Key));
        Assert.Equal([1L, 3L], buttons[0].Windows.Select(w => w.Hwnd));
    }

    [Fact]
    public void S29_1_when_full_groups_only_above_the_limit()
    {
        var eight = Enumerable.Range(1, TaskGroups.FullAt).Select(i => Window(i, "a")).ToList();
        var nine = Enumerable.Range(1, TaskGroups.FullAt + 1).Select(i => Window(i, "a")).ToList();

        Assert.Equal(TaskGroups.FullAt, TaskGroups.Build(eight, "whenFull").Count);
        Assert.Single(TaskGroups.Build(nine, "whenFull"));
    }

    [Fact]
    public void S29_1_the_representative_is_the_most_recently_activated_window_and_members_resolve_by_key()
    {
        var windows = new[] { Window(1, "a", activatedAt: 5), Window(2, "a", activatedAt: 9), Window(3, "b") };
        var group = TaskGroups.Build(windows, "always")[0];

        Assert.Equal(2, group.Representative.Hwnd);
        Assert.Equal([1L, 2L], TaskGroups.Members(windows, "g:a").Select(w => w.Hwnd));
        Assert.Equal([3L], TaskGroups.Members(windows, "3").Select(w => w.Hwnd));
        Assert.Empty(TaskGroups.Members(windows, "g:gone"));
        Assert.Empty(TaskGroups.Members(windows, "99"));
    }

    [Fact]
    public void S29_1_the_item_of_a_group_counts_its_windows_and_hides_the_label_when_asked()
    {
        var windows = new[] { Window(1, "a", active: true), Window(2, "a") };
        var group = TaskGroups.Build(windows, "always")[0];

        var item = TaskGroups.Item(group, labels: false);
        var labelled = TaskGroups.Item(group, labels: true);

        Assert.Equal("g:a", (string?)item["key"]);
        Assert.Equal(2, (int?)item["count"]);
        Assert.Equal("2", (string?)item["badge"]);
        Assert.True((bool?)item["active"]);
        Assert.Equal(string.Empty, (string?)item["label"]);
        Assert.Equal("w1", (string?)labelled["label"]);
        Assert.Contains("2 windows", (string?)item["tooltip"]);
    }

    // --- Badges (S29.3) -------------------------------------------------------------------------------------------

    [Fact]
    public void S29_3_the_badge_prefers_progress_then_the_overlay_then_the_count()
    {
        var plain = new TaskGroup("g:a", [Window(1, "a"), Window(2, "a")]);
        var overlay = new TaskGroup("g:a", [Window(1, "a"), Window(2, "a", overlay: "Muted")]);
        var progress = new TaskGroup("g:a", [Window(1, "a", overlay: "Muted"), Window(2, "a", progress: new FeedProgress("normal", 0.456))]);
        var indeterminate = new TaskGroup("3", [Window(3, "b", progress: new FeedProgress("indeterminate", null))]);
        var emptyOverlay = new TaskGroup("4", [Window(4, "b", overlay: string.Empty)]);

        Assert.Equal("2", TaskGroups.Badge(plain));
        Assert.Equal("Muted", TaskGroups.Badge(overlay));
        Assert.Equal("46%", TaskGroups.Badge(progress));
        Assert.Equal("…", TaskGroups.Badge(indeterminate));
        Assert.Equal("•", TaskGroups.Badge(emptyOverlay));
        Assert.Equal(string.Empty, TaskGroups.Badge(new TaskGroup("5", [Window(5, "c")])));
    }

    [Fact]
    public void S29_3_an_undecoded_message_is_logged_once_per_number_and_shows_no_badge()
    {
        using var run = new BackchannelRun();
        var message = new TaskbarMessage(0xC123, 1, 2);

        Assert.False(run.Tray.Unhandled!(message));
        Assert.False(run.Tray.Unhandled!(message with { WParam = 7 }));
        Assert.False(run.Tray.Unhandled!(new TaskbarMessage(0xC124, 0, 0)));

        // Other applications broadcast registered messages to every top-level window, so the log may hold more lines.
        var first = Assert.Single(run.Log, line => line.Contains("0xC123", StringComparison.Ordinal));
        Assert.Single(run.Log, line => line.Contains("0xC124", StringComparison.Ordinal));
        Assert.StartsWith("TaskbarMessageUndecoded", first, StringComparison.Ordinal);
        Assert.Empty(run.Progress);
        Assert.Empty(run.Overlays);
    }

    [Fact]
    public void S29_3_a_decoded_update_reaches_the_sink_only_for_a_tracked_window()
    {
        using var run = new BackchannelRun(decode: m => m.Message == 0xC200
            ? new TaskbarUpdate.Progress((nint)m.LParam, new WindowProgress(ProgressState.Normal, 0.5))
            : null);

        Assert.True(run.Tray.Unhandled!(new TaskbarMessage(0xC200, 0, 42)));
        Assert.False(run.Tray.Unhandled!(new TaskbarMessage(0xC200, 0, 43)));

        var (key, progress) = Assert.Single(run.Progress);
        Assert.Equal(new WindowKey(42, 1), key);
        Assert.Equal(ProgressState.Normal, progress.State);

        // A decoded message is not logged. Other applications broadcast registered messages to every top-level window, so the log may hold lines about other numbers.
        Assert.DoesNotContain(run.Log, line => line.Contains("0xC200", StringComparison.Ordinal));
    }

    [Fact]
    public void S29_3_an_injected_overlay_and_a_stopped_channel()
    {
        using var run = new BackchannelRun();
        var overlay = new WindowOverlay(new IconRef.Named("overlay"), "Muted");

        Assert.True(run.Channel.Inject(new TaskbarUpdate.Overlay(42, overlay)));
        Assert.True(run.Channel.Inject(new TaskbarUpdate.Overlay(42, null)));
        Assert.False(run.Channel.Inject(new TaskbarUpdate.Overlay(7, overlay)));
        run.Channel.Stop();

        Assert.Equal(2, run.Overlays.Count);
        Assert.Equal("Muted", run.Overlays[0].Overlay?.Description);
        Assert.Null(run.Tray.Unhandled);
        Assert.Contains("TaskbarButtonCreated", run.Channel.ButtonCreatedName, StringComparison.Ordinal);
    }

    // --- Builders -------------------------------------------------------------------------------------------------

    private static WindowFeedItem Window(long hwnd, string group, long activatedAt = 0, bool active = false,
        FeedProgress? progress = null, string? overlay = null) =>
        new($"w{hwnd}:1", hwnd, 100, $"w{hwnd}", $@"C:\{group}.exe", null, group, null, "m1", "d1", false, true, "none", false,
            false, false, true, true, hwnd, activatedAt, active, false, false, progress, overlay);

    /// <summary>The 76-byte header of a shell link with the given flags, then the string data in the order a link stores it.</summary>
    private static byte[] Link(uint flags, params (string? Text, object? Unused)[] strings)
    {
        var bytes = new List<byte>(Header(flags));
        var unicode = (flags & 0x80) != 0;
        foreach (var (text, _) in strings)
        {
            if (text is null)
            {
                continue;
            }

            var data = unicode ? Encoding.Unicode.GetBytes(text) : Encoding.Latin1.GetBytes(text);
            var count = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(count, (ushort)(unicode ? data.Length / 2 : data.Length));
            bytes.AddRange(count);
            bytes.AddRange(data);
        }

        return [.. bytes];
    }

    private static byte[] Header(uint flags)
    {
        var header = new byte[76];
        new byte[] { 0x4C, 0, 0, 0, 0x01, 0x14, 0x02, 0, 0, 0, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46 }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), flags);
        return header;
    }

    /// <summary>A <c>DestList</c> stream of the given version with an entry per (stream number, pin) pair, each with an empty path.</summary>
    private static byte[] DestList(int version, params (int Stream, int Pin)[] entries)
    {
        var size = 32 + entries.Sum(_ => 130 + (version >= 4 ? 4 : 0));
        var list = new byte[size];
        BinaryPrimitives.WriteInt32LittleEndian(list.AsSpan(0), version);
        BinaryPrimitives.WriteInt32LittleEndian(list.AsSpan(4), entries.Length);
        var at = 32;
        foreach (var (stream, pin) in entries)
        {
            BinaryPrimitives.WriteInt32LittleEndian(list.AsSpan(at + 88), stream);
            BinaryPrimitives.WriteInt32LittleEndian(list.AsSpan(at + 108), pin);
            at += 130 + (version >= 4 ? 4 : 0);
        }

        return list;
    }

    /// <summary>A version-3 compound file with a mini-stream stream "1" and a regular stream "big" of 4096 bytes.</summary>
    private static byte[] Cfb(byte[] small, byte[] big)
    {
        Assert.True(small.Length <= 128 && big.Length == 4096, "The fixture holds a small stream of up to 128 bytes and one of 4096.");
        var file = new byte[512 * 13];
        Span<byte> s = file;
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(file, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x18..], 0x3E);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x1A..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x1C..], 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x1E..], 9);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x20..], 6);
        BinaryPrimitives.WriteInt32LittleEndian(s[0x2C..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(s[0x30..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(s[0x38..], 4096);
        BinaryPrimitives.WriteInt32LittleEndian(s[0x3C..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(s[0x40..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(s[0x44..], -2);
        BinaryPrimitives.WriteInt32LittleEndian(s[0x48..], 0);
        for (var i = 0; i < 109; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(s[(0x4C + (4 * i))..], i == 0 ? 0 : -1);
        }

        // Sector n starts at (n + 1) * 512. Sector 0 is the FAT, 1 the directory, 2 the mini FAT, 3 the mini stream, 4..11 "big".
        var fat = new int[128];
        Array.Fill(fat, -1);
        fat[0] = -3;
        fat[1] = -2;
        fat[2] = -2;
        fat[3] = -2;
        for (var i = 4; i < 11; i++)
        {
            fat[i] = i + 1;
        }

        fat[11] = -2;
        for (var i = 0; i < fat.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(s[(512 + (4 * i))..], fat[i]);
        }

        Entry(s[(2 * 512)..], 0, "Root Entry", 5, 3, 128);
        Entry(s[(2 * 512)..], 1, "1", 2, 0, small.Length);
        Entry(s[(2 * 512)..], 2, "big", 2, 4, big.Length);

        var miniFat = s[(3 * 512)..];
        for (var i = 0; i < 128; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(miniFat[(4 * i)..], -1);
        }

        BinaryPrimitives.WriteInt32LittleEndian(miniFat, 1);
        BinaryPrimitives.WriteInt32LittleEndian(miniFat[4..], -2);

        small.CopyTo(file, 4 * 512);
        big.CopyTo(file, 5 * 512);
        return file;
    }

    private static void Entry(Span<byte> directory, int index, string name, byte type, int start, int length)
    {
        var entry = directory[(index * 128)..];
        Encoding.Unicode.GetBytes(name).CopyTo(entry);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[64..], (ushort)((name.Length + 1) * 2));
        entry[66] = type;
        BinaryPrimitives.WriteInt32LittleEndian(entry[116..], start);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[120..], (uint)length);
    }

    /// <summary>A hook-scoped tray window with a back channel on it, resolving handle 42 only; never the real <c>Shell_TrayWnd</c>.</summary>
    private sealed class BackchannelRun : IDisposable
    {
        private readonly string _hook = Path.Combine(Path.GetTempPath(), "SubZeroDev.WindowManager.Tests", Guid.NewGuid().ToString("N"));

        public BackchannelRun(Func<TaskbarMessage, TaskbarUpdate?>? decode = null)
        {
            Directory.CreateDirectory(_hook);
            Tray = TrayHost.Start(_hook, _ => { }, null);
            Channel = new TaskbarBackchannel(Tray, handle => handle == 42 ? new WindowKey(42, 1) : null, Log.Add, decode);
            Channel.Start((key, progress) => Progress.Add((key, progress)), (key, overlay) => Overlays.Add((key, overlay)));
        }

        public TrayHost Tray { get; }

        public TaskbarBackchannel Channel { get; }

        public List<string> Log { get; } = [];

        public List<(WindowKey Key, WindowProgress Progress)> Progress { get; } = [];

        public List<(WindowKey Key, WindowOverlay? Overlay)> Overlays { get; } = [];

        public void Dispose()
        {
            Channel.Dispose();
            Tray.Dispose();
            try
            {
                Directory.Delete(_hook, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
