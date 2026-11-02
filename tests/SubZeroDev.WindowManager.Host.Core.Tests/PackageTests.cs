using System.IO.Compression;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.PackTool;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>Slice S11: plugins are packed, installed, upgraded and removed.</summary>
public sealed class PackageTests
{
    private const string Id = "example.packed";

    [Fact]
    public void S11_1_wm_pack_puts_the_manifest_and_entry_at_the_zip_root()
    {
        using var scratch = new Scratch();
        var output = BuildOutput(scratch, "0.1.0");
        var zip = Path.Combine(scratch.DataRoot, "x.zip");

        var code = Packer.Run([output, "--manifest", Path.Combine(output, "plugin.json"), "--out", zip], TextWriter.Null, TextWriter.Null);

        Assert.Equal(Packer.Ok, code);
        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(["lib/helper.dll", "plugin.json", "worker.exe"], archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void S11_1_wm_pack_gives_the_same_bytes_for_the_same_input()
    {
        using var scratch = new Scratch();
        var output = BuildOutput(scratch, "0.1.0");
        var first = Path.Combine(scratch.DataRoot, "first.zip");
        var second = Path.Combine(scratch.DataRoot, "second.zip");

        Packer.Run([output, "--out", first], TextWriter.Null, TextWriter.Null);
        File.SetLastWriteTimeUtc(Path.Combine(output, "worker.exe"), DateTime.UtcNow.AddDays(-3));
        Packer.Run([output, "--out", second], TextWriter.Null, TextWriter.Null);

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Fact]
    public void S11_1_wm_pack_exits_2_when_the_entry_was_not_built()
    {
        using var scratch = new Scratch();
        var output = BuildOutput(scratch, "0.1.0");
        File.Delete(Path.Combine(output, "worker.exe"));
        var error = new StringWriter();

        var code = Packer.Run([output, "--out", Path.Combine(scratch.DataRoot, "x.zip")], TextWriter.Null, error);

        Assert.Equal(Packer.EntryMissing, code);
        Assert.Contains("worker.exe", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void S11_1_wm_pack_exits_1_and_names_the_field_when_the_manifest_is_invalid()
    {
        using var scratch = new Scratch();
        var output = BuildOutput(scratch, "not-a-version");
        var error = new StringWriter();

        var code = Packer.Run([output, "--out", Path.Combine(scratch.DataRoot, "x.zip")], TextWriter.Null, error);

        Assert.Equal(Packer.InvalidManifest, code);
        Assert.Contains("version 'not-a-version'", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(scratch.DataRoot, "x.zip")));
    }

    [Fact]
    public async Task S11_2_installing_a_zip_adds_a_user_plugin_and_reports_it_added()
    {
        using var scratch = new Scratch();
        var zip = Pack(scratch, "0.1.0");
        using var catalog = new PackageCatalog(scratch.Locations, new NullLog(), watch: false);
        var changes = Record(catalog);

        var installed = await catalog.InstallAsync(PackageKind.Plugin, zip, CancellationToken.None);

        Assert.True(installed.Ok, installed.Error?.Detail);
        Assert.Equal(PackageSource.User, installed.Value!.Source);
        Assert.True(File.Exists(Path.Combine(scratch.DataRoot, "plugins", Id, "lib", "helper.dll")));
        Assert.NotNull(catalog.GetManifest(Id));
        Assert.Contains(new CatalogChangedEventArgs(PackageKind.Plugin, Id, CatalogChange.Added), changes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(catalog.StagingRoot));
    }

    [Fact]
    public async Task A_folder_whose_only_child_holds_the_manifest_installs_too()
    {
        using var scratch = new Scratch();
        var output = BuildOutput(scratch, "0.1.0");
        var wrapper = Path.Combine(scratch.DataRoot, "wrapper");
        Directory.CreateDirectory(wrapper);
        Directory.Move(output, Path.Combine(wrapper, "anything"));
        using var catalog = new PackageCatalog(scratch.Locations, new NullLog(), watch: false);

        var installed = await catalog.InstallAsync(PackageKind.Plugin, wrapper, CancellationToken.None);

        Assert.True(installed.Ok, installed.Error?.Detail);
        Assert.True(File.Exists(Path.Combine(scratch.DataRoot, "plugins", Id, "worker.exe")));
    }

    [Fact]
    public async Task S11_3_a_zip_with_an_entry_outside_its_root_is_refused_and_staging_is_left_empty()
    {
        using var scratch = new Scratch();
        var zip = Pack(scratch, "0.1.0");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(archive.CreateEntry("..\\escaped.txt").Open());
            writer.Write("out");
        }

        using var catalog = new PackageCatalog(scratch.Locations, new NullLog(), watch: false);

        var installed = await catalog.InstallAsync(PackageKind.Plugin, zip, CancellationToken.None);

        Assert.Equal(CoreErrorCode.PackageInvalid, installed.Error?.Code);
        Assert.Contains("escaped.txt", installed.Error!.Detail, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(scratch.DataRoot, "plugins", Id)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(catalog.StagingRoot));
        Assert.False(File.Exists(Path.Combine(catalog.StagingRoot, "escaped.txt")));
    }

    [Fact]
    public async Task A_package_with_an_invalid_manifest_is_refused_with_the_problem()
    {
        using var scratch = new Scratch();
        var output = BuildOutput(scratch, "nope");
        using var catalog = new PackageCatalog(scratch.Locations, new NullLog(), watch: false);

        var installed = await catalog.InstallAsync(PackageKind.Plugin, output, CancellationToken.None);

        Assert.Equal(CoreErrorCode.PackageInvalid, installed.Error?.Code);
        Assert.Contains("version 'nope'", installed.Error!.Detail, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(catalog.StagingRoot));
    }

    [Fact]
    public async Task S11_4_upgrading_stops_the_worker_swaps_the_folder_and_reports_it_updated()
    {
        using var scratch = new Scratch();
        using var catalog = new PackageCatalog(scratch.Locations, new NullLog(), watch: false);
        Assert.True((await catalog.InstallAsync(PackageKind.Plugin, Pack(scratch, "0.1.0"), CancellationToken.None)).Ok);
        var stopped = new List<string>();
        catalog.StopWorkerAsync = (id, _) =>
        {
            stopped.Add(id);
            return Task.CompletedTask;
        };
        var changes = Record(catalog);

        var upgraded = await catalog.InstallAsync(PackageKind.Plugin, Pack(scratch, "0.2.0"), CancellationToken.None);

        Assert.True(upgraded.Ok, upgraded.Error?.Detail);
        Assert.Equal([Id], stopped);
        Assert.Equal("0.2.0", catalog.GetManifest(Id)!.Version.ToString());
        Assert.Equal([new CatalogChangedEventArgs(PackageKind.Plugin, Id, CatalogChange.Updated)], changes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(catalog.StagingRoot));
    }

    [Fact]
    public async Task S11_4_reinstalling_the_same_version_still_restarts_the_plugin()
    {
        using var scratch = new Scratch();
        using var catalog = new PackageCatalog(scratch.Locations, new NullLog(), watch: false);
        var zip = Pack(scratch, "0.1.0");
        Assert.True((await catalog.InstallAsync(PackageKind.Plugin, zip, CancellationToken.None)).Ok);
        var changes = Record(catalog);

        Assert.True((await catalog.InstallAsync(PackageKind.Plugin, zip, CancellationToken.None)).Ok);

        Assert.Equal([new CatalogChangedEventArgs(PackageKind.Plugin, Id, CatalogChange.Updated)], changes);
    }

    [Fact]
    public async Task S11_4_a_swap_that_fails_on_a_locked_file_keeps_the_installed_version_and_restarts_it()
    {
        using var scratch = new Scratch();
        using var catalog = new PackageCatalog(scratch.Locations, new NullLog(), watch: false);
        Assert.True((await catalog.InstallAsync(PackageKind.Plugin, Pack(scratch, "0.1.0"), CancellationToken.None)).Ok);
        var changes = Record(catalog);

        Outcome<PackageRecord> upgraded;
        using (new FileStream(Path.Combine(scratch.DataRoot, "plugins", Id, "worker.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            upgraded = await catalog.InstallAsync(PackageKind.Plugin, Pack(scratch, "0.2.0"), CancellationToken.None);
        }

        Assert.Equal(CoreErrorCode.IoFailure, upgraded.Error?.Code);
        Assert.Equal("0.1.0", catalog.GetManifest(Id)!.Version.ToString());
        Assert.Contains("\"0.1.0\"", File.ReadAllText(Path.Combine(scratch.DataRoot, "plugins", Id, "plugin.json")), StringComparison.Ordinal);
        Assert.Equal([new CatalogChangedEventArgs(PackageKind.Plugin, Id, CatalogChange.Updated)], changes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(catalog.StagingRoot));
    }

    [Fact]
    public async Task S11_7_removing_a_user_plugin_deletes_its_folder_and_keeps_its_data()
    {
        using var scratch = new Scratch();
        using var catalog = new PackageCatalog(scratch.Locations, new NullLog(), watch: false);
        Assert.True((await catalog.InstallAsync(PackageKind.Plugin, Pack(scratch, "0.1.0"), CancellationToken.None)).Ok);
        var data = Path.Combine(scratch.DataRoot, "plugin-data", Id);
        Directory.CreateDirectory(data);
        var stopped = new List<string>();
        catalog.StopWorkerAsync = (id, _) =>
        {
            stopped.Add(id);
            return Task.CompletedTask;
        };
        var changes = Record(catalog);

        var removed = await catalog.RemoveAsync(PackageKind.Plugin, Id, CancellationToken.None);

        Assert.True(removed.Ok, removed.Error?.Detail);
        Assert.Equal([Id], stopped);
        Assert.False(Directory.Exists(Path.Combine(scratch.DataRoot, "plugins", Id)));
        Assert.True(Directory.Exists(data));
        Assert.Null(catalog.GetManifest(Id));
        Assert.Equal([new CatalogChangedEventArgs(PackageKind.Plugin, Id, CatalogChange.Removed)], changes);
    }

    [Fact]
    public async Task S11_7_removing_a_built_in_plugin_is_refused_as_in_use()
    {
        using var scratch = new Scratch();
        var content = Path.Combine(scratch.DataRoot, "content");
        var builtIn = BuildOutput(scratch, "0.1.0");
        Directory.CreateDirectory(Path.Combine(content, "plugins"));
        Directory.Move(builtIn, Path.Combine(content, "plugins", Id));
        using var catalog = new PackageCatalog(new Locations(scratch.DataRoot, content), new NullLog(), watch: false);

        var removed = await catalog.RemoveAsync(PackageKind.Plugin, Id, CancellationToken.None);

        Assert.Equal(CoreErrorCode.PackageInUse, removed.Error?.Code);
        Assert.NotNull(catalog.GetManifest(Id));
    }

    [Fact]
    public void Staging_is_emptied_when_the_catalog_starts()
    {
        using var scratch = new Scratch();
        var leftover = Path.Combine(scratch.DataRoot, "staging", "abandoned", Id);
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "plugin.json"), "{}");

        using var catalog = new PackageCatalog(scratch.Locations, new NullLog(), watch: false);

        Assert.Empty(Directory.EnumerateFileSystemEntries(catalog.StagingRoot));
    }

    private static List<CatalogChangedEventArgs> Record(PackageCatalog catalog)
    {
        var changes = new List<CatalogChangedEventArgs>();
        catalog.Changed += (_, change) => changes.Add(change);
        return changes;
    }

    private static string Pack(Scratch scratch, string version)
    {
        var output = BuildOutput(scratch, version);
        var zip = Path.Combine(scratch.DataRoot, $"{Guid.NewGuid():N}.zip");
        Assert.Equal(Packer.Ok, Packer.Run([output, "--out", zip], TextWriter.Null, TextWriter.Null));
        return zip;
    }

    /// <summary>A plugin's build output: its manifest, entry and one library in a subfolder.</summary>
    private static string BuildOutput(Scratch scratch, string version)
    {
        var folder = Path.Combine(scratch.DataRoot, "build", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "lib"));
        File.WriteAllText(Path.Combine(folder, "worker.exe"), "worker " + version);
        File.WriteAllText(Path.Combine(folder, "lib", "helper.dll"), "helper");
        File.WriteAllText(Path.Combine(folder, "plugin.json"), new JsonObject
        {
            ["manifestVersion"] = 1,
            ["id"] = Id,
            ["version"] = version,
            ["displayName"] = "Packed",
            ["publisher"] = "Test",
            ["hostApi"] = "^1.0",
            ["entry"] = new JsonObject { ["path"] = "worker.exe", ["args"] = new JsonArray() },
            ["modes"] = new JsonArray("coexistence"),
            ["contributions"] = new JsonArray(new JsonObject { ["id"] = "item", ["kind"] = "panelItem", ["displayName"] = "Item" }),
        }.ToJsonString());
        return folder;
    }

    private sealed class NullLog : IHostLog
    {
        public void Write(Diagnostics.LogLevel level, string area, string message)
        {
        }
    }
}
