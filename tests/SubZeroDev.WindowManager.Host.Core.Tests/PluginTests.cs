using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Host.Platform;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>Slice S2: architecture rules, the handshake, view validation and revision ordering.</summary>
public sealed partial class PluginTests
{
    [Fact]
    public void S2_1_plugin_projects_reference_no_host_or_guardian_project()
    {
        var root = RepositoryRoot();
        var plugins = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories))
            .Where(path => Path.GetFileName(path).Contains(".Plugins.", StringComparison.Ordinal)
                || Path.GetFileName(path) == "SubZeroDev.WindowManager.TestPlugin.csproj")
            .ToList();
        Assert.NotEmpty(plugins);

        foreach (var project in plugins)
        {
            foreach (var reference in ReferencesOf(project))
            {
                var name = Path.GetFileNameWithoutExtension(reference);
                Assert.False(name.Contains(".Host.", StringComparison.Ordinal) || name.Contains("Guardian", StringComparison.Ordinal),
                    $"{Path.GetFileName(project)} reaches {name}.");
            }
        }

        var clock = plugins.Single(path => path.EndsWith("SubZeroDev.WindowManager.Plugins.Clock.csproj", StringComparison.Ordinal));
        Assert.Equal(["SubZeroDev.WindowManager.Sdk"], DirectReferences(clock).Select(Path.GetFileNameWithoutExtension));
    }

    [Fact]
    public void S2_1_no_host_project_loads_an_assembly()
    {
        var root = RepositoryRoot();
        var hostSources = Directory.EnumerateDirectories(Path.Combine(root, "src"), "SubZeroDev.WindowManager.Host.*")
            .Append(Path.Combine(root, "src", "SubZeroDev.WindowManager.Guardian"))
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(hostSources);

        foreach (var file in hostSources)
        {
            var match = AssemblyLoading().Match(File.ReadAllText(file));
            Assert.False(match.Success, $"{Path.GetRelativePath(root, file)} uses {match.Value}.");
        }
    }

    [Theory]
    [InlineData("log")]
    [InlineData("hello-bad-token")]
    [InlineData("hello-notification")]
    public async Task S2_4_a_first_message_that_is_not_a_valid_hello_gets_1010_and_the_pipe_closes(string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        var (server, client) = await PipePairAsync(ct);
        await using var _ = client;
        var first = scenario switch
        {
            "log" => RpcMessage.Notification(Methods.Log, new { level = "info", message = "early" }),
            "hello-bad-token" => RpcMessage.Request(1, Methods.Hello, new HelloParams("wrong", "example.plugin", 1, "1.0.0")),
            _ => RpcMessage.Notification(Methods.Hello, new HelloParams("secret", "example.plugin", 1, "1.0.0")),
        };
        var accepting = WorkerChannel.AcceptAsync(server, "example.plugin", 42, "secret", TimeSpan.FromSeconds(5), ct);
        await Framing.WriteAsync(client, ProtocolJson.Serialize(first), ct);
        var answer = ProtocolJson.Deserialize(await Framing.ReadAsync(client, ct));
        var (channel, failure) = await accepting;

        Assert.Null(channel);
        Assert.NotNull(failure);
        Assert.Equal(WireErrorCodes.Unauthorised, answer.Error!.Code);
        Assert.Null(await Framing.ReadAsync(client, ct));
        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await Framing.WriteAsync(client, ProtocolJson.Serialize(RpcMessage.Notification(Methods.Log, new { message = "after" })), ct));
    }

    [Fact]
    public async Task S2_4_a_hello_with_the_launch_token_is_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (server, client) = await PipePairAsync(ct);
        await using var _ = client;
        var accepting = WorkerChannel.AcceptAsync(server, "example.plugin", 42, "secret", TimeSpan.FromSeconds(5), ct);
        await Framing.WriteAsync(client, ProtocolJson.Serialize(
            RpcMessage.Request(1, Methods.Hello, new HelloParams("secret", "example.plugin", ProtocolInfo.ProtocolVersion, "1.0.0"))), ct);

        var answer = ProtocolJson.Deserialize(await Framing.ReadAsync(client, ct));
        var (channel, failure) = await accepting;

        Assert.Null(failure);
        Assert.NotNull(channel);
        Assert.Null(answer.Error);
        await channel.DisposeAsync();
    }

    public static TheoryData<string, string> InvalidViews() => new()
    {
        { """{"id":"a","kind":"stack","children":[{"id":"b","kind":"text","props":{"text":"x"}},{"id":"b","kind":"text","props":{"text":"y"}}]}""", ViewValidator.Codes.DuplicateId },
        { """{"id":"a","kind":"hologram"}""", ViewValidator.Codes.UnknownKind },
        { """{"id":"a","kind":"text","props":{"text":"x","background":"#f00"}}""", ViewValidator.Codes.ForbiddenProp },
        { """{"id":"a","kind":"text","props":{"text":"x","foreground":"#f00"}}""", ViewValidator.Codes.ForbiddenProp },
        { """{"id":"a","kind":"text","props":{"text":"x","fontSize":20}}""", ViewValidator.Codes.ForbiddenProp },
        { """{"id":"a","kind":"text","props":{"text":"x","fontFamily":"Comic Sans MS"}}""", ViewValidator.Codes.ForbiddenProp },
    };

    [Theory]
    [MemberData(nameof(InvalidViews))]
    public void S2_5_an_invalid_view_is_rejected_and_the_shown_view_stays(string root, string code)
    {
        var model = new SurfaceModel("s");
        Assert.True(model.ApplyView(ViewSet(1, """{"id":"t","kind":"text","props":{"text":"ok"}}""")).Applied);

        var update = model.ApplyView(ViewSet(2, root));

        Assert.Equal(SurfaceUpdateResult.Rejected, update.Result);
        Assert.Contains(update.Issues!, issue => issue.Code == code);
        Assert.Equal(1, model.ViewRevision);
        Assert.Equal("ok", (string?)model.View!["props"]!["text"]);
    }

    [Fact]
    public void S2_6_a_patch_below_the_view_revision_has_no_effect()
    {
        var model = new SurfaceModel("s");
        model.ApplyView(ViewSet(3, """{"id":"t","kind":"text","props":{"text":{"$state":"time"}}}"""));
        model.ApplyPatch(new StatePatchParams("s", 3, new Dictionary<string, JsonNode?> { ["time"] = "12:00" }, null));

        var stale = model.ApplyPatch(new StatePatchParams("s", 2, new Dictionary<string, JsonNode?> { ["time"] = "99:99" }, ["time"]));

        Assert.Equal(SurfaceUpdateResult.Stale, stale.Result);
        Assert.Equal("12:00", (string?)model.State["time"]);
        Assert.Equal(SurfaceUpdateResult.Stale, model.ApplyView(ViewSet(2, """{"id":"t","kind":"text"}""")).Result);
    }

    [Fact]
    public async Task S2_7_a_hand_edited_layout_raises_an_external_change()
    {
        using var scratch = new Scratch();
        using var store = new ConfigStore(scratch.Locations);
        Directory.CreateDirectory(Path.Combine(scratch.DataRoot, "layouts"));
        var changed = new TaskCompletionSource<DocumentChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += (_, e) => changed.TrySetResult(e);
        store.StartWatching();

        var layout = JsonNode.Parse(File.ReadAllText(Path.Combine(scratch.Locations.ContentRoot, "layouts", "subzerodev.default.json")))!;
        layout["displayName"] = "Edited by hand";
        await File.WriteAllTextAsync(Path.Combine(scratch.DataRoot, "layouts", "subzerodev.default.json"), layout.ToJsonString(), TestContext.Current.CancellationToken);

        var e = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(new DocumentId(DocumentKind.Layout, "subzerodev.default"), e.Id);
        Assert.Equal(ChangeOrigin.External, e.Origin);
        Assert.Equal(DocumentHealth.Valid, e.State.Health);
    }

    [Theory]
    [InlineData("\"args\": [1]", "\"modes\": [\"coexistence\"]", "\"multiInstance\": true")]
    [InlineData("\"args\": []", "\"modes\": [5]", "\"multiInstance\": true")]
    [InlineData("\"args\": []", "\"modes\": [\"coexistence\"]", "\"multiInstance\": \"x\"")]
    public void A_wrong_typed_manifest_field_marks_the_package_invalid(string args, string modes, string multiInstance)
    {
        using var scratch = new Scratch();
        var folder = Path.Combine(scratch.DataRoot, "plugins", "example.bad");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "worker.exe"), string.Empty);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), $$"""
            {
              "manifestVersion": 1, "id": "example.bad", "version": "0.1.0", "displayName": "Bad", "publisher": "Test",
              "hostApi": "^1.0", "entry": { "path": "worker.exe", {{args}} }, {{modes}},
              "contributions": [ { "id": "c", "kind": "panelItem", "displayName": "C", {{multiInstance}} } ]
            }
            """);

        using var catalog = new PackageCatalog(scratch.Locations, new NullLog(), watch: false);

        var record = Assert.Single(catalog.All(PackageKind.Plugin), record => record.Id == "example.bad");
        Assert.Equal(PackageHealth.Invalid, record.Health);
        Assert.Null(catalog.GetManifest("example.bad"));
    }

    [Fact]
    public void Paths_map_back_to_documents()
    {
        using var scratch = new Scratch();
        using var store = new ConfigStore(scratch.Locations);

        Assert.Equal(DocumentId.HostSettings, store.IdForPath(Path.Combine(scratch.DataRoot, "settings.json")));
        Assert.Equal(new DocumentId(DocumentKind.Layout, "mine"), store.IdForPath(Path.Combine(scratch.DataRoot, "layouts", "mine.json")));
        Assert.Null(store.IdForPath(Path.Combine(scratch.DataRoot, "layouts", ".mine.json.tmp")));
        Assert.Null(store.IdForPath(Path.Combine(scratch.DataRoot, "themes", "x", "theme.json")));
    }

    [Theory]
    [InlineData(new[] { "plain" }, @"C:\w\worker.exe plain")]
    [InlineData(new[] { "two words", "" }, @"C:\w\worker.exe ""two words"" """"")]
    [InlineData(new[] { @"a\b""c", @"end\" }, @"C:\w\worker.exe ""a\b\""c"" end\")]
    [InlineData(new[] { @"trailing\ space\" }, @"C:\w\worker.exe ""trailing\ space\\""")]
    public void Worker_arguments_survive_the_command_line(string[] arguments, string expected) =>
        Assert.Equal(expected, WorkerLauncher.BuildCommandLine(@"C:\w\worker.exe", arguments));

    [GeneratedRegex(@"\bAssembly\.(Load|LoadFrom|LoadFile|UnsafeLoadFrom)\s*\(|\bAssemblyLoadContext\b")]
    private static partial Regex AssemblyLoading();

    private static JsonElement ViewSet(long revision, string root) =>
        JsonDocument.Parse($$"""{"surfaceId":"s","revision":{{revision}},"root":{{root}}}""").RootElement.Clone();

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

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SubZeroDev.WindowManager.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("No SubZeroDev.WindowManager.slnx above the test assembly.");
    }

    private static IEnumerable<string> DirectReferences(string project) =>
        XDocument.Load(project).Descendants("ProjectReference")
            .Select(reference => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!,
                ((string)reference.Attribute("Include")!).Replace('\\', Path.DirectorySeparatorChar))));

    /// <summary>Every project reachable from <paramref name="project"/> through project references.</summary>
    private static IEnumerable<string> ReferencesOf(string project)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(DirectReferences(project));
        while (pending.TryPop(out var next))
        {
            if (seen.Add(next))
            {
                foreach (var further in DirectReferences(next))
                {
                    pending.Push(further);
                }
            }
        }

        return seen;
    }

    private sealed class NullLog : IHostLog
    {
        public void Write(Diagnostics.LogLevel level, string area, string message)
        {
        }
    }
}
