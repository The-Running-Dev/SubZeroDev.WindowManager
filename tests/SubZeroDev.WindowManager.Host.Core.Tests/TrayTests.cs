using System.Text;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Host.Platform;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Recovery;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>
/// Slice S19: the notify-icon payload as shell32 sends it, icon keys, balloon times, the tray feed and its
/// invoke arguments, and app-bar position queries against a simulated monitor. No tray window is created.
/// </summary>
public sealed class TrayTests : IDisposable
{
    private readonly Scratch _scratch = new();

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void S19_1_a_full_notify_payload_reads_every_field()
    {
        var item = Guid.NewGuid();
        var request = TrayHost.ParseNotify(Notify(1, nid =>
        {
            BitConverter.TryWriteBytes(nid[4..], 0x1234);
            BitConverter.TryWriteBytes(nid[8..], 7u);
            BitConverter.TryWriteBytes(nid[12..], 0x3Fu);
            BitConverter.TryWriteBytes(nid[16..], 0x8001u);
            BitConverter.TryWriteBytes(nid[20..], 0x5678);
            Write(nid.Slice(24, 256), "Tip");
            BitConverter.TryWriteBytes(nid[280..], 1u);
            BitConverter.TryWriteBytes(nid[284..], 1u);
            Write(nid.Slice(288, 512), "Text");
            BitConverter.TryWriteBytes(nid[800..], 4000u);
            Write(nid.Slice(804, 128), "Title");
            item.TryWriteBytes(nid.Slice(936, 16));
        }), out var error);

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(1u, request.Message);
        Assert.Equal((nint)0x1234, request.Window);
        Assert.Equal(7u, request.Uid);
        Assert.Equal(0x3Fu, request.Flags);
        Assert.Equal(0x8001u, request.CallbackMessage);
        Assert.Equal((nint)0x5678, request.Icon);
        Assert.Equal("Tip", request.Tip);
        Assert.Equal(1u, request.State);
        Assert.Equal("Text", request.Info);
        Assert.Equal(4000u, request.TimeoutOrVersion);
        Assert.Equal("Title", request.InfoTitle);
        Assert.Equal(item, request.Item);
    }

    [Fact]
    public void S19_1_an_older_shorter_structure_reads_what_it_has()
    {
        var full = Notify(0, nid =>
        {
            BitConverter.TryWriteBytes(nid[8..], 3u);
            Write(nid.Slice(24, 128), "Old");
        });

        var request = TrayHost.ParseNotify(full.AsSpan(0, TrayHost.MinimumNotifySize), out var error);

        Assert.Null(error);
        Assert.Equal(3u, request!.Uid);
        Assert.Equal("Old", request.Tip);
        Assert.Equal("", request.Info);
        Assert.Equal(Guid.Empty, request.Item);
    }

    [Fact]
    public void S19_4_malformed_notify_payloads_are_refused_with_a_reason()
    {
        var wrongSignature = Notify(0, _ => { });
        BitConverter.TryWriteBytes(wrongSignature, 0u);

        Assert.Null(TrayHost.ParseNotify(new byte[20], out var shortError));
        Assert.Contains("shorter", shortError);
        Assert.Null(TrayHost.ParseNotify(wrongSignature, out var signatureError));
        Assert.Contains("signature", signatureError);
        Assert.Null(TrayHost.ParseNotify(Notify(9, _ => { }), out var messageError));
        Assert.Contains("Unknown notify-icon message 9", messageError);
    }

    [Fact]
    public void S19_1_an_icon_is_keyed_by_its_guid_only_when_it_names_one()
    {
        var item = Guid.NewGuid();

        Assert.Equal(item.ToString("N"), TrayHost.KeyOf(Request(0x20, item)));
        Assert.Equal("4660:7", TrayHost.KeyOf(Request(0x20, Guid.Empty)));
        Assert.Equal("4660:7", TrayHost.KeyOf(Request(0, item)));
    }

    [Theory]
    [InlineData(0u, 10_000)]
    [InlineData(500u, 1_000)]
    [InlineData(4_000u, 4_000)]
    [InlineData(60_000u, 30_000)]
    public void S19_3_a_balloon_stays_its_asked_time_within_limits(uint asked, int expectedMs) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), TrayHost.BalloonTimeout(asked));

    [Fact]
    public void S19_1_the_feed_carries_each_icon_with_an_image_reference_and_its_balloon()
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        var icons = new[]
        {
            new TrayIcon("10:1", 10, 1, 42, "Tip", false, new TrayBalloon("Title", "Text", at, at.AddSeconds(5)), 3, 4),
            new TrayIcon("10:2", 10, 2, 42, "", true, null, 0, 0),
        };

        var feed = TrayFeed.Snapshot(icons);

        Assert.Equal(2, feed.Count);
        var first = feed[0]!.AsObject();
        Assert.Equal("10:1", (string?)first["key"]);
        Assert.Equal(1u, (uint)first["uid"]!);
        Assert.Equal(42, (int)first["ownerPid"]!);
        Assert.Equal("Tip", (string?)first["tooltip"]);
        Assert.Equal("10:1", (string?)first["icon"]!["tray"]);
        Assert.Equal(3, (int)first["icon"]!["v"]!);
        Assert.False((bool)first["hidden"]!);
        Assert.Equal("Title", (string?)first["balloon"]!["title"]);
        Assert.Equal(1_700_000_000_000, (long)first["balloon"]!["at"]!);
        Assert.True((bool)feed[1]!["hidden"]!);
        Assert.Null(feed[1]!["balloon"]);
    }

    [Fact]
    public void S19_2_invoke_arguments_name_a_key_an_action_and_an_optional_point()
    {
        var plain = TrayFeed.ParseInvoke(new JsonObject { ["key"] = "10:1", ["action"] = "select" }, out var plainError);
        var pointed = TrayFeed.ParseInvoke(new JsonObject { ["key"] = "10:1", ["action"] = "contextMenu", ["x"] = 100, ["y"] = 200 }, out _);
        var doubled = TrayFeed.ParseInvoke(new JsonObject { ["key"] = "10:1", ["action"] = "doubleClick", ["x"] = 100 }, out _);

        Assert.Null(plainError);
        Assert.Equal(("10:1", TrayAction.Select, ((int, int)?)null), plain);
        Assert.Equal(("10:1", TrayAction.ContextMenu, ((int, int)?)(100, 200)), pointed);
        Assert.Null(doubled!.Value.Point);

        Assert.Null(TrayFeed.ParseInvoke(null, out var noArgs));
        Assert.Contains("needs { key", noArgs);
        Assert.Null(TrayFeed.ParseInvoke(new JsonObject { ["key"] = "", ["action"] = "select" }, out var emptyKey));
        Assert.Contains("needs { key", emptyKey);
        Assert.Null(TrayFeed.ParseInvoke(new JsonObject { ["key"] = "10:1", ["action"] = "hover" }, out var badAction));
        Assert.Contains("Unknown tray action 'hover'", badAction);
    }

    [Fact]
    public async Task S19_6_a_position_query_lays_out_the_strip_without_taking_it()
    {
        var journal = new RecoveryJournal(Path.Combine(_scratch.DataRoot, "journal.json"), Environment.ProcessId, DateTimeOffset.UtcNow);
        await journal.StartAsync(TestContext.Current.CancellationToken);
        var monitor = new MonitorInfo("sim-a", 0, new RectInt(0, 0, 1000, 800), new RectInt(0, 0, 1000, 760), 96, true);
        var applied = new List<RectInt>();
        using var service = new WorkAreaAppBarService(new FakeMonitors([monitor]), journal, (_, area) =>
        {
            applied.Add(area);
            return true;
        });
        await service.RegisterAsync(1, "sim-a", PanelEdge.Top, 30, TestContext.Current.CancellationToken);

        var below = service.Query(2, "sim-a", PanelEdge.Top, 20);
        var again = service.Query(1, "sim-a", PanelEdge.Top, 40);

        Assert.Equal(new RectInt(0, 30, 1000, 50), below);
        Assert.Equal(new RectInt(0, 0, 1000, 40), again);
        Assert.Null(service.Query(2, "sim-b", PanelEdge.Top, 20));
        Assert.Single(applied);
        Assert.Equal(new RectInt(0, 0, 1000, 30), service.Query(1, "sim-a", PanelEdge.Top, 30));
    }

    /// <summary>A <c>WM_COPYDATA</c> notify payload: signature, message and a full <c>NOTIFYICONDATAW</c>.</summary>
    private static byte[] Notify(uint message, SpanAction fill)
    {
        var payload = new byte[8 + TrayHost.NotifyDataSize];
        BitConverter.TryWriteBytes(payload, TrayHost.Signature);
        BitConverter.TryWriteBytes(payload.AsSpan(4), message);
        var nid = payload.AsSpan(8);
        BitConverter.TryWriteBytes(nid, TrayHost.NotifyDataSize);
        fill(nid);
        return payload;
    }

    private static void Write(Span<byte> field, string text) => Encoding.Unicode.GetBytes(text, field);

    private static TrayHost.NotifyRequest Request(uint flags, Guid item) =>
        new(0, 0x1234, 7, flags, 0, 0, "", 0, 0, "", 0, "", item);

    private delegate void SpanAction(Span<byte> nid);

    private sealed class FakeMonitors(IReadOnlyList<MonitorInfo> monitors) : IMonitorService
    {
        public IReadOnlyList<MonitorInfo> Monitors => monitors;

        public event EventHandler TopologyChanged { add { } remove { } }
    }
}
