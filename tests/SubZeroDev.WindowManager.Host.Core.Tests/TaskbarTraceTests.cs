using System.Text.Json;
using System.Runtime.InteropServices;
using SubZeroDev.WindowManager.Host.Platform;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

[Collection("Taskbar trace environment")]
public sealed class TaskbarTraceTests
{
    [Theory]
    [InlineData(0x004A, true)]
    [InlineData(0x0401, true)]
    [InlineData(0x8001, true)]
    [InlineData(0xC001, true)]
    [InlineData(0x0113, false)]
    public void Capture_includes_private_and_copydata_messages_without_timer_noise(uint message, bool eligible) =>
        Assert.Equal(eligible, TaskbarTrace.Eligible(message));

    [Fact]
    public void Capture_keeps_repeats_bounds_payload_and_reports_drops()
    {
        var output = new StringWriter();
        using (var trace = new TaskbarTrace(output, "test-scoped", _ => { }))
        {
            var payload = Enumerable.Repeat((byte)0xAB, 300).ToArray();
            for (var i = 0; i < TaskbarTrace.MaximumEvents + 3; i++)
            {
                if (trace.Reserve(0x004A))
                {
                    trace.Record(42, 0x004A, (nuint)i, 123, 9, 300, payload);
                }
            }
        }

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(TaskbarTrace.MaximumEvents + 2, lines.Length);
        using var first = JsonDocument.Parse(lines[1]);
        using var second = JsonDocument.Parse(lines[2]);
        Assert.Equal("0x0", first.RootElement.GetProperty("wParam").GetString());
        Assert.Equal("0x1", second.RootElement.GetProperty("wParam").GetString());
        Assert.Equal("0x7B", first.RootElement.GetProperty("lParam").GetString());
        Assert.Equal("0x9", first.RootElement.GetProperty("copyDataKind").GetString());
        Assert.Equal(512, first.RootElement.GetProperty("payloadHex").GetString()!.Length);
        Assert.True(first.RootElement.GetProperty("payloadTruncated").GetBoolean());
        using var end = JsonDocument.Parse(lines[^1]);
        Assert.Equal(3, end.RootElement.GetProperty("dropped").GetInt32());
    }

    [Fact]
    public void Recording_copies_payload_and_never_decodes_any_native_format()
    {
        var output = new StringWriter();
        using (var trace = new TaskbarTrace(output, "Shell_TrayWnd", _ => { }))
        {
            byte[] payload = [1, 2, 3];
            Assert.True(trace.Reserve(0x004A));
            trace.Record(42, 0x004A, 1, 2, 5, 3, payload);
            payload[0] = 255;
            Assert.Null(TaskbarDecoder.Unverified(new TaskbarMessage(0x004A, 1, 2)));
            Assert.Null(TaskbarDecoder.Unverified(new TaskbarMessage(0x401, 1, 2)));
            Assert.Null(TaskbarDecoder.Unverified(new TaskbarMessage(0x8001, 1, 2)));
            Assert.Null(TaskbarDecoder.Unverified(new TaskbarMessage(0xC001, 1, 2)));
        }

        Assert.Contains("010203", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("FF0203", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Simulated_user32_stimuli_are_captured_before_routing_without_becoming_badges()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.DataRoot, "trace.jsonl");
        var previous = Environment.GetEnvironmentVariable(TaskbarTrace.Variable);
        var progress = new List<WindowProgress>();
        var overlays = new List<WindowOverlay?>();
        try
        {
            Environment.SetEnvironmentVariable(TaskbarTrace.Variable, path);
            using (var tray = TrayHost.Start(scratch.DataRoot, _ => { }, null))
            using (var channel = new TaskbarBackchannel(tray, _ => new WindowKey(42, 1), _ => { }))
            {
                channel.Start((_, value) => progress.Add(value), (_, value) => overlays.Add(value));
                // Synthetic window messages, NOT real ITaskbarList3 calls.
                Assert.NotEqual(0, SendMessageTimeoutW(tray.Window, 0x0401, 17, 100, 2, 5000, out _));
                Assert.NotEqual(0, SendMessageTimeoutW(tray.Window, 0x0401, 61, 100, 2, 5000, out _));
                var memory = Marshal.AllocHGlobal(3);
                var envelope = Marshal.AllocHGlobal(Marshal.SizeOf<CopyDataStruct>());
                try
                {
                    Marshal.Copy(new byte[] { 1, 2, 3 }, 0, memory, 3);
                    Marshal.StructureToPtr(new CopyDataStruct { Kind = 9, Size = 3, Data = memory }, envelope, false);
                    Assert.NotEqual(0, SendMessageTimeoutW(tray.Window, 0x004A, 0, envelope, 2, 5000, out _));
                }
                finally
                {
                    Marshal.FreeHGlobal(envelope);
                    Marshal.FreeHGlobal(memory);
                }
            }

            var lines = File.ReadAllLines(path).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.True(lines[0].RootElement.GetProperty("hookScoped").GetBoolean());
                var events = lines.Where(line => line.RootElement.GetProperty("type").GetString() == "message").ToArray();
                Assert.Contains(events, line => line.RootElement.GetProperty("wParam").GetString() == "0x11");
                Assert.Contains(events, line => line.RootElement.GetProperty("wParam").GetString() == "0x3D");
                Assert.Contains(events, line => line.RootElement.GetProperty("payloadHex").GetString() == "010203");
                Assert.Equal("end", lines[^1].RootElement.GetProperty("type").GetString());
                Assert.Empty(progress);
                Assert.Empty(overlays);
            }
            finally
            {
                foreach (var line in lines) line.Dispose();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(TaskbarTrace.Variable, previous);
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageTimeoutW(nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
}

[CollectionDefinition("Taskbar trace environment", DisableParallelization = true)]
public sealed class TaskbarTraceEnvironment { }
