using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Sdk;

namespace SubZeroDev.WindowManager.TestPlugin;

/// <summary>
/// Scenarios, chosen by the first argument:
/// <c>invalid-view</c> shows "ok", then sends a stale patch and four invalid views the host must reject;
/// <c>ignore-shutdown</c> shows "ok" and never stops, so the host has to kill it;
/// <c>styled</c> shows "ok" with the classes <c>primary</c> and <c>compact</c>, for theme restyling;
/// <c>faulty</c> shows "ok", appends a line to <c>created.log</c> in its data folder for each instance
/// created, and every 50 ms acts on the switch files there (see <see cref="Switches"/>);
/// <c>buttons</c> shows the Buttons <c>echo</c> (invokes <c>samples.echo</c>), <c>remote</c> (invokes
/// <c>samples.remote</c>, which nothing running provides) and <c>ev</c> (event <c>ping</c>), and the Toggle
/// <c>tog</c>; it appends each <c>samples.echo</c> invocation to <c>invocations.log</c> and each element
/// event to <c>events.log</c> in its data folder;
/// <c>icons</c> shows one Button per named icon, each with the icon's id as its element id:
/// <c>wm.test.first</c>, <c>wm.test.second</c>, <c>wm.test.theme</c>, <c>launcher</c> and <c>wm.test.missing</c>;
/// <c>subscriber</c> shows "ok" and appends "available &lt;true|false&gt; &lt;items&gt;" to <c>feed.log</c> in its
/// data folder for each <c>windows.running</c> update (its manifest must subscribe to that feed).
/// <c>save-settings</c> appends "start &lt;settings&gt;" to <c>settings.log</c>, then saves its plugin settings
/// with <c>count</c> one higher and appends "saved &lt;ok|code&gt;"; <c>save-invalid</c> does the same but saves
/// <c>count</c> as a string; <c>migrate-throws</c> appends "start &lt;settings&gt;" and throws from
/// <c>MigrateSettingsAsync</c>. All three show "ok".
/// <c>fixture-windows</c> and <c>fixture-fullscreen</c> are not plugins at all: see <see cref="Fixture"/>.
/// Every scenario appends "created &lt;instance&gt;" and "destroyed &lt;instance&gt;" to <c>instances.log</c> in its
/// data folder as instances come and go.
/// </summary>
public static class Program
{
    public static string Scenario { get; private set; } = "invalid-view";

    public static string DataDirectory { get; set; } = string.Empty;

    public static void Append(string file, string line) =>
        File.AppendAllText(Path.Combine(DataDirectory, file), line + Environment.NewLine);

    public static Task<int> Main(string[] args)
    {
        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Scenario = args[0];
        }

        if (Scenario == "fixture-windows")
        {
            return Task.FromResult(Fixture.Run(args.Length > 1 ? args[1] : "fixture"));
        }

        if (Scenario == "fixture-fullscreen")
        {
            return Task.FromResult(Fixture.RunFullscreen(args[1..5]));
        }

        return PluginRuntime.RunAsync<ProbePlugin>(args);
    }
}

public sealed class ProbePlugin : Plugin
{
    private IDisposable? _subscription;

    protected internal override async Task OnStartAsync(CancellationToken ct)
    {
        Program.DataDirectory = Context.DataDirectory;
        Directory.CreateDirectory(Context.DataDirectory);
        if (Program.Scenario == "subscriber")
        {
            _subscription = Context.Feeds.Subscribe("windows.running", (update, _) =>
            {
                var count = (update.Snapshot as JsonArray)?.Count ?? -1;
                Program.Append("feed.log", $"available {(update.Available ? "true" : "false")} {count}");
                return Task.CompletedTask;
            });
        }

        if (Program.Scenario is "save-settings" or "save-invalid" or "migrate-throws")
        {
            Program.Append("settings.log", $"start {Context.Settings.ToJsonString()}");
        }

        if (Program.Scenario is "save-settings" or "save-invalid")
        {
            var count = Context.Settings["count"] is JsonValue value && value.TryGetValue<int>(out var n) ? n : 0;
            JsonNode next = Program.Scenario == "save-settings" ? JsonValue.Create(count + 1) : JsonValue.Create("many");
            var saved = await Context.SaveSettingsAsync(new JsonObject { ["count"] = next }, ct).ConfigureAwait(false);
            Program.Append("settings.log", $"saved {(saved.Ok ? "ok" : $"{saved.Error!.Code} {(int)saved.Error.Code}")}");
        }

        if (Program.Scenario == "buttons")
        {
            Context.Commands.Register("samples.echo", (context, _) =>
            {
                Program.Append("invocations.log", $"command samples.echo {Lower(context.Source)} {Environment.ProcessId}");
                return Task.FromResult(new CommandResult(JsonValue.Create("echo")));
            });
        }
    }

    protected internal override Task<MigratedSettings> MigrateSettingsAsync(SettingsScope scope, int fromVersion, JsonObject settings,
        CancellationToken ct) => Program.Scenario == "migrate-throws"
            ? throw new InvalidOperationException("This migration always fails.")
            : base.MigrateSettingsAsync(scope, fromVersion, settings, ct);

    private static string Lower(InvocationSource source) => source.ToString().ToLowerInvariant();

    protected internal override ContributionInstance CreateInstance(InstanceRequest request) => new ProbeInstance(request);

    protected internal override async Task OnStopAsync(CancellationToken ct)
    {
        _subscription?.Dispose();
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
        Program.Append("instances.log", $"created {InstanceId}");
        if (Program.Scenario == "buttons")
        {
            Surface.SetView(View.Stack("root", StackOrientation.Horizontal,
                View.Button("echo", "Echo", View.Invoke("samples.echo", new JsonObject { ["n"] = 1 }), label: "Echo"),
                View.Button("remote", "Remote", View.Invoke("samples.remote"), label: "Remote"),
                View.Button("ev", "Event", View.Event("ping"), label: "Event"),
                View.Toggle("tog", "Toggle", Prop<bool>.State("checked"), View.Event("toggled"), label: "Toggle")));
            Surface.SetState("checked", false);
            return Task.CompletedTask;
        }

        if (Program.Scenario == "icons")
        {
            string[] names = ["wm.test.first", "wm.test.second", "wm.test.theme", "launcher", "wm.test.missing"];
            Surface.SetView(View.Stack("root", StackOrientation.Horizontal,
                names.Select(name => View.Button(name, name, View.Event("ping"), icon: new IconRef.Named(name))).ToArray()));
            return Task.CompletedTask;
        }

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

    protected internal override Task OnEventAsync(ElementEvent e, CancellationToken ct)
    {
        Program.Append("events.log", $"event {e.ElementId} {e.Kind} {e.Value?.ToJsonString()} {e.Source}");
        if (e.Kind == ElementEventKind.Toggled && e.Value is JsonValue value && value.TryGetValue<bool>(out var isChecked))
        {
            Surface.SetState("checked", isChecked);
        }

        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        Program.Append("instances.log", $"destroyed {InstanceId}");
        return base.DisposeAsync();
    }

    private static JsonObject Text(string id, string text) =>
        new() { ["id"] = id, ["kind"] = "text", ["props"] = new JsonObject { ["text"] = text } };

    private static JsonObject TextWith(string prop, JsonNode value) =>
        new() { ["id"] = "t", ["kind"] = "text", ["props"] = new JsonObject { ["text"] = "bad", [prop] = value } };

    private static JsonObject Stack(params JsonNode[] children) =>
        new() { ["id"] = "root", ["kind"] = "stack", ["children"] = new JsonArray(children) };
}
