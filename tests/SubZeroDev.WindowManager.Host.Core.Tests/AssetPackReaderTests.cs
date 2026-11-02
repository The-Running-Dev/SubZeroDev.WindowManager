using SubZeroDev.WindowManager.Host.Theming;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

public sealed class AssetPackReaderTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static string WritePack(string folder, string id, string json, params string[] images)
    {
        var root = Path.Combine(folder, "assetpacks", id);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, AssetPackReader.FileName), json);
        foreach (var image in images)
        {
            var path = Path.Combine(root, image);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Png);
        }

        return root;
    }

    [Fact]
    public void The_host_default_pack_ships_and_is_valid()
    {
        using var scratch = new Scratch();

        var chain = AssetPackReader.BuildChain(scratch.Locations, [], null, null);

        var host = Assert.Single(chain.Packs);
        Assert.Equal(PackageHealth.Valid, host.Health);
        Assert.Empty(host.Problems);
        Assert.Equal("host:" + AssetPackReader.HostDefaultId, chain.FindNamed("launcher")!.Pack.Label);
    }

    [Fact]
    public void S13_1_the_first_pack_naming_an_icon_wins_then_the_theme_bundle_then_the_host_default()
    {
        using var scratch = new Scratch();
        WritePack(scratch.DataRoot, "test.a", """{ "id": "test.a", "icons": { "wm.test.first": "first.png" } }""", "first.png");
        WritePack(scratch.DataRoot, "test.b", """{ "id": "test.b", "icons": { "wm.test.first": "b1.png", "wm.test.second": "b2.png" } }""", "b1.png", "b2.png");
        var theme = Path.Combine(scratch.DataRoot, "themes", "test.theme");
        WritePack(theme, "test.bundled", """{ "id": "test.bundled", "icons": { "wm.test.theme": "t.png", "launcher": "l.png" } }""", "t.png", "l.png");

        var chain = AssetPackReader.BuildChain(scratch.Locations, ["test.a", "test.b"], theme, "test.bundled");

        Assert.Equal("pack:test.a", chain.FindNamed("wm.test.first")!.Pack.Label);
        Assert.Equal("pack:test.b", chain.FindNamed("wm.test.second")!.Pack.Label);
        Assert.Equal("theme:test.bundled", chain.FindNamed("wm.test.theme")!.Pack.Label);
        Assert.Equal("theme:test.bundled", chain.FindNamed("launcher")!.Pack.Label);
        Assert.Equal("host:" + AssetPackReader.HostDefaultId, chain.FindNamed("action.search")!.Pack.Label);
        Assert.Null(chain.FindNamed("wm.test.missing"));
    }

    [Fact]
    public void S13_3_an_image_path_leaving_the_folder_makes_the_pack_invalid_and_the_chain_skips_it()
    {
        using var scratch = new Scratch();
        File.WriteAllBytes(Path.Combine(scratch.DataRoot, "outside.png"), Png);
        WritePack(scratch.DataRoot, "test.escape", """{ "id": "test.escape", "icons": { "launcher": "../../outside.png", "wm.test.first": "ok.png" } }""", "ok.png");

        var chain = AssetPackReader.BuildChain(scratch.Locations, ["test.escape"], null, null);

        var pack = chain.Packs[0];
        Assert.Equal(PackageHealth.Invalid, pack.Health);
        Assert.Contains(pack.Problems, p => p.Code == ThemeErrorCode.PathEscape);
        Assert.Null(chain.FindNamed("wm.test.first"));
        Assert.Equal("host:" + AssetPackReader.HostDefaultId, chain.FindNamed("launcher")!.Pack.Label);
    }

    [Fact]
    public void A_missing_image_drops_only_that_entry_and_a_missing_pack_is_invalid()
    {
        using var scratch = new Scratch();
        WritePack(scratch.DataRoot, "test.partial", """{ "id": "test.partial", "icons": { "a": "a.png", "b": "gone.png", "c": "c.bmp" } }""", "a.png", "c.bmp");

        var chain = AssetPackReader.BuildChain(scratch.Locations, ["test.partial", "test.absent"], null, null);

        var partial = chain.Packs[0];
        Assert.Equal(PackageHealth.Valid, partial.Health);
        Assert.Equal(["a"], partial.Icons.Keys);
        Assert.Equal(2, partial.Problems.Count(p => p.Code == ThemeErrorCode.AssetMissing));
        Assert.Equal(PackageHealth.Invalid, chain.Packs[1].Health);
    }

    [Fact]
    public void S13_2_app_icons_match_a_bare_file_name_a_full_path_or_an_aumid()
    {
        using var scratch = new Scratch();
        WritePack(scratch.DataRoot, "test.apps", """
            {
              "id": "test.apps",
              "appIcons": [
                { "match": { "exe": "notepad.exe" }, "image": "n.png" },
                { "match": { "exe": "C:\\Tools\\thing.exe" }, "image": "t.png" },
                { "match": { "aumid": "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" }, "image": "c.png" },
                { "match": { "exe": "both.exe", "aumid": "x" }, "image": "n.png" }
              ]
            }
            """, "n.png", "t.png", "c.png");

        var chain = AssetPackReader.BuildChain(scratch.Locations, ["test.apps"], null, null);

        Assert.Equal(3, chain.Packs[0].AppIcons.Count);
        Assert.EndsWith("n.png", chain.FindApp(@"C:\Windows\System32\NOTEPAD.EXE", null)!.Path, StringComparison.Ordinal);
        Assert.EndsWith("t.png", chain.FindApp(@"c:\tools\thing.exe", null)!.Path, StringComparison.Ordinal);
        Assert.Null(chain.FindApp(@"D:\Other\thing.exe", null));
        Assert.EndsWith("c.png", chain.FindApp(null, "microsoft.windowscalculator_8wekyb3d8bbwe!app")!.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pack_inside_the_theme_wins_over_one_of_the_same_id_in_the_data_root()
    {
        using var scratch = new Scratch();
        WritePack(scratch.DataRoot, "test.same", """{ "id": "test.same", "icons": { "x": "data.png" } }""", "data.png");
        var theme = Path.Combine(scratch.DataRoot, "themes", "test.theme");
        WritePack(theme, "test.same", """{ "id": "test.same", "icons": { "x": "bundled.png" } }""", "bundled.png");

        var chain = AssetPackReader.BuildChain(scratch.Locations, [], theme, "test.same");

        Assert.EndsWith("bundled.png", chain.FindNamed("x")!.Path, StringComparison.Ordinal);
    }
}
