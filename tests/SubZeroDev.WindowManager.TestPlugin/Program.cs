using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Sdk;

namespace SubZeroDev.WindowManager.TestPlugin;

/// <summary>
/// Scenarios, chosen by the first argument:
/// <c>invalid-view</c> shows "ok", then sends a stale patch and four invalid views the host must reject;
/// <c>ignore-shutdown</c> shows "ok" and never stops, so the host has to kill it;
/// <c>styled</c> shows "ok" with the classes <c>primary</c> and <c>compact</c>, for theme restyling;
/// <c>faulty</c> shows "ok", appends a line to <c>created.log</c> in its data folder for each instance
/// created, and every 50 ms acts on the switch files there (see <see cref="Switches"/>).
/// </summary>
public static class Program
{
    public static string Scenario { get; private set; } = "invalid-view";

    public static string DataDirectory { get; set; } = string.Empty;

    public static Task<int> Main(string[] args)
    {
        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Scenario = args[0];
        }

        return PluginRuntime.RunAsync<ProbePlugin>(args);
    }
}

public sealed class ProbePlugin : Plugin
{
    protected internal override Task OnStartAsync(CancellationToken ct)
    {
        Program.DataDirectory = Context.DataDirectory;
        Directory.CreateDirectory(Context.DataDirectory);
        return Task.CompletedTask;
    }

    protected internal override ContributionInstance CreateInstance(InstanceRequest request) => new ProbeInstance(request);

    protected internal override async Task OnStopAsync(CancellationToken ct)
    {
        if (Program.Scenario == "ignore-shutdown")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
        }
    }
}

public sealed class ProbeInstance(InstanceRequest request) : ContributionInstance(request)
{
    protected internal override Task OnCreatedAsync(CancellationToken ct)
    {
        Surface.SetView(Program.Scenario == "styled"
            ? View.Text("t", Prop<string>.State("t"), "primary", "compact")
            : View.Text("t", Prop<string>.State("t")));
        Surface.SetState("t", "ok");
        if (Program.Scenario == "faulty")
        {
            File.AppendAllText(Path.Combine(Program.DataDirectory, "created.log"),
                $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} {Environment.ProcessId}{Environment.NewLine}");
            _ = Task.Run(() => Switches.RunAsync(Surface), CancellationToken.None);
            return Task.CompletedTask;
        }

        if (Program.Scenario != "invalid-view")
        {
            return Task.CompletedTask;
        }

        // A patch below the current view revision (1) must be dropped.
        Surface.SendPatchAt(0, new Dictionary<string, JsonNode?> { ["t"] = "stale" }, null);
        Surface.SendView(Stack(Text("dup", "a"), Text("dup", "b")));
        Surface.SendView(new JsonObject { ["id"] = "x", ["kind"] = "hologram" });
        Surface.SendView(TextWith("background", "#FFFF0000"));
        Surface.SendView(TextWith("fontSize", 40));
        return Task.CompletedTask;
    }

    private static JsonObject Text(string id, string text) =>
        new() { ["id"] = id, ["kind"] = "text", ["props"] = new JsonObject { ["text"] = text } };

    private static JsonObject TextWith(string prop, JsonNode value) =>
        new() { ["id"] = "t", ["kind"] = "text", ["props"] = new JsonObject { ["text"] = "bad", [prop] = value } };

    private static JsonObject Stack(params JsonNode[] children) =>
        new() { ["id"] = "root", ["kind"] = "stack", ["children"] = new JsonArray(children) };
}
