using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Host.Platform;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>Slice S6: command routing, key chords, hotkeys and window activation.</summary>
public sealed class CommandTests : IDisposable
{
    private readonly Scratch _scratch = new();
    private readonly ConcurrentQueue<string> _steps = new();
    private readonly FakeCatalog _catalog = new();
    private readonly FakeSupervisor _supervisor = new();
    private readonly ConfigStore _config;

    public CommandTests() => _config = new ConfigStore(_scratch.Locations);

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public async Task S6_1_user_input_grants_foreground_rights_before_the_one_request_is_written()
    {
        _catalog.Add("samples.plugin", """[{"id":"samples.echo","title":"Echo"}]""");
        var channel = _supervisor.Run("samples.plugin", 4242, _steps);
        var router = Router();

        var result = await router.InvokeAsync("samples.echo", new JsonObject { ["text"] = "hi" }, InvocationSource.Pointer, null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Error?.Detail);
        Assert.Equal(["allow 4242", "send samples.echo Pointer"], _steps);
        var sent = Assert.Single(channel.Requests);
        Assert.Equal(InvocationSource.Pointer, sent.Source);
        Assert.Equal("hi", sent.Args!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task S6_1_a_plugin_invoking_a_command_grants_no_foreground_rights()
    {
        _catalog.Add("samples.plugin", """[{"id":"samples.echo"}]""");
        var channel = _supervisor.Run("samples.plugin", 4242, _steps);
        var router = Router();

        var answer = await router.ServeWorkerAsync("other.plugin", RpcMessage.Request(7, Methods.CommandInvoke,
            new CommandInvokeParams("samples.echo", null, InvocationSource.Pointer)));

        Assert.Null(answer.Error);
        Assert.Equal(["send samples.echo Plugin"], _steps);
        Assert.Equal(InvocationSource.Plugin, Assert.Single(channel.Requests).Source);
    }

    [Theory]
    [InlineData(PluginState.Faulted)]
    [InlineData(PluginState.Unavailable)]
    [InlineData(PluginState.Starting)]
    public async Task S6_2_a_provider_that_is_not_running_gets_plugin_unavailable_and_no_start(PluginState state)
    {
        _catalog.Add("samples.plugin", """[{"id":"samples.echo"}]""");
        _supervisor.States["samples.plugin"] = state;
        var router = Router();

        var result = await router.InvokeAsync("samples.echo", null, InvocationSource.Pointer, null, TestContext.Current.CancellationToken);

        Assert.Equal(CoreErrorCode.PluginUnavailable, result.Error!.Code);
        Assert.StartsWith("1001", result.Error.Detail, StringComparison.Ordinal);
        Assert.Empty(_steps);
        Assert.Equal(0, _supervisor.StartsAndRetries);
    }

    [Fact]
    public async Task S6_2_an_unknown_command_is_command_not_found()
    {
        var result = await Router().InvokeAsync("nobody.has.this", null, InvocationSource.Pointer, null,
            TestContext.Current.CancellationToken);

        Assert.Equal(CoreErrorCode.CommandNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task S6_3_a_provider_that_does_not_answer_times_out()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), CommandRouter.InvokeTimeout);
        var ct = TestContext.Current.CancellationToken;
        var (server, client) = await PipePairAsync(ct);
        await using var _ = client;
        var accepting = WorkerChannel.AcceptAsync(server, "slow.plugin", 42, "secret", TimeSpan.FromSeconds(5), ct);
        await Framing.WriteAsync(client, ProtocolJson.Serialize(
            RpcMessage.Request(1, Methods.Hello, new HelloParams("secret", "slow.plugin", ProtocolInfo.ProtocolVersion, "1.0.0"))), ct);
        await Framing.ReadAsync(client, ct);
        var (channel, failure) = await accepting;
        Assert.Null(failure);
        await using var __ = channel!;
        channel!.Start();
        _catalog.Add("slow.plugin", """[{"id":"slow.wait"}]""");
        _supervisor.Channels["slow.plugin"] = channel;
        _supervisor.States["slow.plugin"] = PluginState.Running;
        var router = new CommandRouter(_catalog, _supervisor, new RecordingForeground(_steps), _config, new NullLog(),
            TimeSpan.FromMilliseconds(300));

        var result = await router.InvokeAsync("slow.wait", null, InvocationSource.Plugin, null, ct);

        Assert.Equal(CoreErrorCode.Timeout, result.Error!.Code);
        var request = ProtocolJson.Deserialize(await Framing.ReadAsync(client, ct));
        Assert.Equal(Methods.CommandInvoke, request.Method);
    }

    [Fact]
    public async Task S6_4_the_lower_plugin_id_provides_a_contested_command_and_is_recorded()
    {
        _catalog.Add("zeta.plugin", """[{"id":"shared.go"}]""");
        _catalog.Add("alpha.plugin", """[{"id":"shared.go"}]""");
        var alpha = _supervisor.Run("alpha.plugin", 1, _steps);
        var zeta = _supervisor.Run("zeta.plugin", 2, _steps);

        var result = await Router().InvokeAsync("shared.go", null, InvocationSource.Plugin, null, TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Error?.Detail);
        Assert.Single(alpha.Requests);
        Assert.Empty(zeta.Requests);
        Assert.Equal("alpha.plugin", _config.Get(DocumentId.HostSettings).Content!["commandProviders"]!["shared.go"]!.GetValue<string>());
    }

    [Fact]
    public async Task S6_4_commandProviders_picks_the_provider()
    {
        _catalog.Add("zeta.plugin", """[{"id":"shared.go"}]""");
        _catalog.Add("alpha.plugin", """[{"id":"shared.go"}]""");
        var alpha = _supervisor.Run("alpha.plugin", 1, _steps);
        var zeta = _supervisor.Run("zeta.plugin", 2, _steps);
        var router = Router();
        Assert.True((await router.SetProviderAsync("shared.go", "zeta.plugin", TestContext.Current.CancellationToken)).Ok);

        var result = await router.InvokeAsync("shared.go", null, InvocationSource.Plugin, null, TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Error?.Detail);
        Assert.Empty(alpha.Requests);
        Assert.Single(zeta.Requests);
        Assert.Equal("zeta.plugin", Assert.Single(router.Commands, c => c.CommandId == "shared.go").Provider);
    }

    [Fact]
    public void S6_5_key_chords_parse_and_print()
    {
        var chord = KeyChord.Parse("Ctrl+Alt+Space");
        Assert.True(chord.Ok);
        Assert.Equal(KeyModifiers.Ctrl | KeyModifiers.Alt, chord.Value.Modifiers);
        Assert.Equal(0x20u, chord.Value.VirtualKey);
        Assert.Equal("Ctrl+Alt+Space", chord.Value.ToString());
        Assert.Equal("Ctrl+Shift+Win+A", KeyChord.Parse("win+shift+control+a").Value.ToString());
        Assert.Equal(0x7Au, KeyChord.Parse("F11").Value.VirtualKey);

        Assert.False(KeyChord.Parse("Ctrl+Foo").Ok);
        Assert.False(KeyChord.Parse("Space").Ok);
        Assert.False(KeyChord.Parse("Ctrl+Ctrl+A").Ok);
        Assert.False(KeyChord.Parse("A+Ctrl").Ok);
        Assert.False(KeyChord.Parse("").Ok);
    }

    [Fact]
    public void S6_5_a_chord_held_elsewhere_is_hotkey_in_use()
    {
        using var first = new HotkeyService();
        using var second = new HotkeyService();
        var chord = KeyChord.Parse("Ctrl+Alt+Shift+Win+F23").Value;

        var held = first.Register(chord, () => { });
        Assert.True(held.Ok, held.Error?.Detail);
        using var _ = held.Value;
        var refused = second.Register(chord, () => { });

        Assert.Equal(CoreErrorCode.HotkeyInUse, refused.Error!.Code);
    }

    [Fact]
    public void S6_5_a_command_whose_hotkey_is_in_use_stays_unbound_for_the_session()
    {
        _catalog.Add("samples.plugin", """[{"id":"samples.echo","defaultHotkey":"Ctrl+Alt+E"}]""");
        var hotkeys = new FakeHotkeys { Refuse = true };
        using var binder = new HotkeyBinder(Router(), hotkeys, _config, new NullLog());

        binder.Refresh();
        binder.Refresh();

        Assert.Equal(1, hotkeys.Attempts);
        Assert.Equal(new HotkeyBinding("samples.echo", "Ctrl+Alt+E", "in-use"), Assert.Single(binder.Bindings));
    }

    [Fact]
    public async Task S6_6_a_hotkey_invokes_its_command_with_source_hotkey()
    {
        _catalog.Add("samples.plugin", """[{"id":"samples.echo","defaultHotkey":"Ctrl+Alt+E"},{"id":"samples.other","defaultHotkey":"Ctrl+Alt+O"}]""");
        var channel = _supervisor.Run("samples.plugin", 9, _steps);
        var hotkeys = new FakeHotkeys();
        await _config.WriteAsync(DocumentId.HostSettings, null, HostSettingsDocument.Default.ToJson().Also(json =>
            json["hotkeys"] = new JsonObject { ["samples.echo"] = "Ctrl+Shift+F9", ["samples.other"] = null }),
            TestContext.Current.CancellationToken);
        using var binder = new HotkeyBinder(Router(), hotkeys, _config, new NullLog());
        binder.Refresh();

        var (chord, press) = Assert.Single(hotkeys.Registered);
        Assert.Equal("Ctrl+Shift+F9", chord.ToString());
        press();

        await WaitUntil(() => !channel.Requests.IsEmpty);
        var sent = Assert.Single(channel.Requests);
        Assert.Equal(("samples.echo", InvocationSource.Hotkey), (sent.CommandId, sent.Source));
        Assert.Equal(["allow 9", "send samples.echo Hotkey"], _steps);
    }

    [Fact]
    public async Task S6_7_activating_a_destroyed_window_is_window_gone()
    {
        var foreground = new ForegroundService();

        var activate = await foreground.ActivateWindowAsync(unchecked((nint)0x7FFF_FFF0), TestContext.Current.CancellationToken);
        var minimize = await foreground.MinimizeWindowAsync(unchecked((nint)0x7FFF_FFF0), TestContext.Current.CancellationToken);

        Assert.Equal(CoreErrorCode.WindowGone, activate.Error!.Code);
        Assert.Equal(CoreErrorCode.WindowGone, minimize.Error!.Code);
    }

    [Fact]
    public async Task Built_in_commands_run_in_the_host()
    {
        var router = Router();
        router.AddBuiltIn("host.test", "Test", (args, _) => Task.FromResult(Outcome<CommandResult>.Success(new CommandResult(args?["n"]?.DeepClone()))));
        router.AddBuiltIn("host.broken", "Broken", (_, _) => throw new InvalidOperationException("boom"));

        var ok = await router.InvokeAsync("host.test", new JsonObject { ["n"] = 3 }, InvocationSource.Keyboard, null, TestContext.Current.CancellationToken);
        var broken = await router.InvokeAsync("host.broken", null, InvocationSource.Keyboard, null, TestContext.Current.CancellationToken);

        Assert.Equal(3, ok.Value!.Value!.GetValue<int>());
        Assert.Equal(CoreErrorCode.CommandFailed, broken.Error!.Code);
        Assert.Throws<ArgumentException>(() => router.AddBuiltIn("samples.x", "X", (_, _) => throw new InvalidOperationException()));
    }

    [Fact]
    public void Manifest_commands_are_read_and_wire_errors_map_to_core_codes()
    {
        var manifest = FakeCatalog.Manifest("samples.plugin", """[{"id":"samples.echo","title":"Echo","defaultHotkey":"Ctrl+Alt+E"},{"id":"samples.plain"}]""");
        Assert.Equal([new CommandDeclaration("samples.echo", "Echo", "Ctrl+Alt+E"), new CommandDeclaration("samples.plain", "samples.plain", null)],
            manifest.Commands);

        var error = WorkerChannel.ErrorOf(new RpcError(1003, "slow", new JsonObject { ["retryable"] = true }), Methods.CommandInvoke);
        Assert.Equal(CoreErrorCode.Timeout, error.Code);
        Assert.True(error.Retryable);
        foreach (var code in new[] { CoreErrorCode.PluginUnavailable, CoreErrorCode.CommandNotFound, CoreErrorCode.Timeout,
            CoreErrorCode.Cancelled, CoreErrorCode.Undeclared, CoreErrorCode.CommandFailed })
        {
            Assert.Equal(code, WorkerChannel.ErrorOf(new RpcError(WorkerChannel.WireCodeOf(code), "x"), "m").Code);
        }
    }

    private CommandRouter Router() => new(_catalog, _supervisor, new RecordingForeground(_steps), _config, new NullLog());

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<(Stream Server, Stream Client)> PipePairAsync(CancellationToken ct)
    {
        var name = "SubZeroDev.WindowManager.Tests." + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var waiting = server.WaitForConnectionAsync(ct);
        await client.ConnectAsync(ct);
        await waiting;
        return (server, client);
    }

    private sealed class NullLog : IHostLog
    {
        public void Write(Diagnostics.LogLevel level, string area, string message)
        {
        }
    }

    private sealed class RecordingForeground(ConcurrentQueue<string> steps) : IForegroundService
    {
        public void AllowForeground(int processId) => steps.Enqueue($"allow {processId}");

        public Task<Outcome> ActivateWindowAsync(nint hwnd, CancellationToken ct) => Task.FromResult(Outcome.Success);

        public Task<Outcome> MinimizeWindowAsync(nint hwnd, CancellationToken ct) => Task.FromResult(Outcome.Success);
    }

    private sealed class FakeHotkeys : IHotkeyService
    {
        public bool Refuse { get; init; }

        public int Attempts { get; private set; }

        public List<(KeyChord Chord, Action Press)> Registered { get; } = [];

        public Outcome<HotkeyRegistration> Register(KeyChord chord, Action onPressed)
        {
            Attempts++;
            if (Refuse)
            {
                return Outcome<HotkeyRegistration>.Fail(CoreErrorCode.HotkeyInUse, "held");
            }

            Registered.Add((chord, onPressed));
            return Outcome<HotkeyRegistration>.Success(new HotkeyRegistration(chord, () => Registered.RemoveAll(r => r.Chord == chord)));
        }
    }

    private sealed class FakeCatalog : IPackageCatalog
    {
        private readonly Dictionary<string, PluginManifest> _manifests = new(StringComparer.Ordinal);

        public event EventHandler<CatalogChangedEventArgs>? Changed;

        public static PluginManifest Manifest(string id, string commands)
        {
            var problems = new List<string>();
            var manifest = PluginManifestReader.Parse(JsonNode.Parse($$"""
                {"manifestVersion":1,"id":"{{id}}","version":"1.0.0","displayName":"P","publisher":"T","description":"D",
                 "hostApi":"^1.0","entry":{"path":"p.exe"},"modes":["coexistence"],"contributions":[],"commands":{{commands}}}
                """)!.AsObject(), problems);
            Assert.True(manifest is not null, string.Join("; ", problems));
            return manifest;
        }

        public void Add(string id, string commands)
        {
            _manifests[id] = Manifest(id, commands);
            Changed?.Invoke(this, new CatalogChangedEventArgs(PackageKind.Plugin, id, CatalogChange.Added));
        }

        public IReadOnlyList<PackageRecord> All(PackageKind kind) =>
            kind != PackageKind.Plugin
                ? []
                : [.. _manifests.Values.Select(m => new PackageRecord(PackageKind.Plugin, m.Id, m.Version, PackageSource.User,
                    "C:\\nowhere", false, PackageHealth.Valid, []))];

        public PluginManifest? GetManifest(string pluginId) => _manifests.GetValueOrDefault(pluginId);

        public Task<Outcome<PackageRecord>> InstallAsync(PackageKind kind, string zipOrFolderPath, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Outcome> RemoveAsync(PackageKind kind, string id, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeSupervisor : IPluginSupervisor
    {
        public Dictionary<string, PluginState> States { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, IWorkerChannel> Channels { get; } = new(StringComparer.Ordinal);

        public int StartsAndRetries { get; private set; }

        public IReadOnlyList<PluginStatus> All => [.. States.Keys.Select(GetStatus)];

#pragma warning disable CS0067
        public event EventHandler<PluginStatus>? StatusChanged;
#pragma warning restore CS0067

        public FakeChannel Run(string pluginId, int processId, ConcurrentQueue<string> steps)
        {
            var channel = new FakeChannel(pluginId, processId, steps);
            Channels[pluginId] = channel;
            States[pluginId] = PluginState.Running;
            return channel;
        }

        public PluginStatus GetStatus(string pluginId) =>
            new(pluginId, States.GetValueOrDefault(pluginId, PluginState.Discovered), null, null, [], []);

        public Task<Outcome> EnsureRunningAsync(string pluginId, CancellationToken ct)
        {
            StartsAndRetries++;
            return Task.FromResult(Outcome.Success);
        }

        public Task StopAsync(string pluginId, CancellationToken ct) => Task.CompletedTask;

        public Task<Outcome> RetryAsync(string pluginId, CancellationToken ct)
        {
            StartsAndRetries++;
            return Task.FromResult(Outcome.Success);
        }

        public Task StopAllAsync(CancellationToken ct) => Task.CompletedTask;

        public IWorkerChannel? GetChannel(string pluginId) =>
            States.GetValueOrDefault(pluginId) == PluginState.Running ? Channels.GetValueOrDefault(pluginId) : null;
    }

    private sealed class FakeChannel(string pluginId, int processId, ConcurrentQueue<string> steps) : IWorkerChannel
    {
        public string PluginId => pluginId;

        public int ProcessId => processId;

        public ConcurrentQueue<CommandInvokeParams> Requests { get; } = new();

#pragma warning disable CS0067
        public event EventHandler<WorkerNotification>? NotificationReceived;
#pragma warning restore CS0067

        public Task<Outcome<TResult>> RequestAsync<TParams, TResult>(string method, TParams args, TimeSpan timeout, CancellationToken ct)
        {
            Assert.Equal(Methods.CommandInvoke, method);
            var invoke = (args as CommandInvokeParams)!;
            steps.Enqueue($"send {invoke.CommandId} {invoke.Source}");
            Requests.Enqueue(invoke);
            return Task.FromResult(Outcome<TResult>.Success(default!));
        }

        public Outcome Notify<TParams>(string method, TParams args) => Outcome.Success;
    }
}

internal static class JsonTestExtensions
{
    public static JsonObject Also(this JsonObject json, Action<JsonObject> change)
    {
        change(json);
        return json;
    }
}
