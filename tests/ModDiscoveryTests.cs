using System.IO;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public class ModDiscoveryTests
{
    private const string SampleCfg = """
title = "My Test Mod";
description = "Line one.\nLine two with \"quotes\".";
preview = "preview.jpg";
content = "bundleV2";
language = "english";
visibility = "public";
published_id = 3712929235L;
apply_for_sanctioned_status = false;
tags = [ ];
""";

    [Fact]
    public void ExtractString_finds_simple_field()
    {
        Assert.Equal("english", ModDiscovery.ExtractString(SampleCfg, "language"));
    }

    [Fact]
    public void ExtractString_unescapes_quotes_and_newlines()
    {
        var got = ModDiscovery.ExtractString(SampleCfg, "description");
        Assert.NotNull(got);
        Assert.Contains("Line one.\n", got);
        Assert.Contains("\"quotes\"", got);
    }

    [Fact]
    public void ExtractString_returns_null_for_missing_key()
    {
        Assert.Null(ModDiscovery.ExtractString(SampleCfg, "nonexistent_field"));
    }

    [Fact]
    public void ExtractPublishedId_returns_unsigned_id()
    {
        Assert.Equal("3712929235", ModDiscovery.ExtractPublishedId(SampleCfg));
    }

    [Fact]
    public void ExtractPublishedId_handles_no_L_suffix()
    {
        var raw = "published_id = 1234567890;";
        Assert.Equal("1234567890", ModDiscovery.ExtractPublishedId(raw));
    }

    [Fact]
    public void ExtractPublishedId_returns_null_when_absent()
    {
        Assert.Null(ModDiscovery.ExtractPublishedId("title = \"foo\";"));
    }

    [Fact]
    public void ParseItemCfg_populates_all_fields()
    {
        using var t = new TempDir();
        var cfgPath = t.Write("itemV2.cfg", SampleCfg);
        var info = new ModInfo { Name = "test", ModDir = t.Path, ItemCfgPath = cfgPath };
        ModDiscovery.ParseItemCfg(info);
        Assert.Equal("My Test Mod", info.Title);
        Assert.Equal("public", info.Visibility);
        Assert.True(info.IsPublic);
        Assert.Equal("english", info.Language);
        Assert.Equal("bundleV2", info.ContentDir);
        Assert.Equal("3712929235", info.PublishedId);
    }

    [Fact]
    public void ParseItemCfg_missing_visibility_defaults_to_private()
    {
        using var t = new TempDir();
        var cfgPath = t.Write("itemV2.cfg", "title = \"foo\";");
        var info = new ModInfo { Name = "test", ModDir = t.Path, ItemCfgPath = cfgPath };
        ModDiscovery.ParseItemCfg(info);
        Assert.Equal("private", info.Visibility);
        Assert.False(info.IsPublic);
    }

    [Fact]
    public void ScanModsAt_skips_dirs_without_itemcfg()
    {
        using var t = new TempDir();
        t.CreateSubdir("not_a_mod");
        var modPath = t.CreateSubdir("real_mod");
        File.WriteAllText(Path.Combine(modPath, "itemV2.cfg"), SampleCfg);
        var mods = ModDiscovery.ScanModsAt(t.Path);
        Assert.Single(mods);
        Assert.Equal("real_mod", mods[0].Name);
    }

    [Fact]
    public void ScanModsAt_alphabetical_order()
    {
        using var t = new TempDir();
        foreach (var name in new[] { "zebra_mod", "alpha_mod", "beta_mod" })
        {
            var d = t.CreateSubdir(name);
            File.WriteAllText(Path.Combine(d, "itemV2.cfg"), $"title = \"{name}\";");
        }
        var mods = ModDiscovery.ScanModsAt(t.Path);
        Assert.Equal(new[] { "alpha_mod", "beta_mod", "zebra_mod" }, mods.Select(m => m.Name).ToArray());
    }

    [Fact]
    public void ScanModsAt_detects_bundle_output()
    {
        using var t = new TempDir();
        var mod = t.CreateSubdir("with_bundles");
        File.WriteAllText(Path.Combine(mod, "itemV2.cfg"), "title = \"x\";");
        var bundles = Path.Combine(mod, "bundleV2");
        Directory.CreateDirectory(bundles);
        File.WriteAllText(Path.Combine(bundles, "abc123.mod_bundle"), "");
        File.WriteAllText(Path.Combine(bundles, "def456.mod_bundle"), "");
        var mods = ModDiscovery.ScanModsAt(t.Path);
        Assert.Single(mods);
        Assert.True(mods[0].HasBuildOutput);
        Assert.Equal(2, mods[0].BundleCount);
    }

    [Fact]
    public void ScanModsAt_no_bundles_means_not_built()
    {
        using var t = new TempDir();
        var mod = t.CreateSubdir("not_built");
        File.WriteAllText(Path.Combine(mod, "itemV2.cfg"), "title = \"x\";");
        var mods = ModDiscovery.ScanModsAt(t.Path);
        Assert.Single(mods);
        Assert.False(mods[0].HasBuildOutput);
        Assert.Equal(0, mods[0].BundleCount);
    }

    [Fact]
    public void WriteItemCfg_round_trips_simple_edit()
    {
        using var t = new TempDir();
        var cfgPath = t.Write("itemV2.cfg", SampleCfg);
        var info = new ModInfo { Name = "test", ModDir = t.Path, ItemCfgPath = cfgPath };
        ModDiscovery.ParseItemCfg(info);

        info.Title = "New Title";
        info.Visibility = "private";
        ModDiscovery.WriteItemCfg(info);

        var info2 = new ModInfo { Name = "test", ModDir = t.Path, ItemCfgPath = cfgPath };
        ModDiscovery.ParseItemCfg(info2);
        Assert.Equal("New Title", info2.Title);
        Assert.Equal("private", info2.Visibility);
        // Other fields preserved.
        Assert.Equal("3712929235", info2.PublishedId);
        Assert.Equal("english", info2.Language);
    }

    [Fact]
    public void WriteItemCfg_preserves_published_id_and_unknown_fields()
    {
        using var t = new TempDir();
        var raw = SampleCfg + "\nweird_extra_field = \"do not lose me\";\n";
        var cfgPath = t.Write("itemV2.cfg", raw);
        var info = new ModInfo { Name = "test", ModDir = t.Path, ItemCfgPath = cfgPath };
        ModDiscovery.ParseItemCfg(info);
        info.Title = "Updated";
        ModDiscovery.WriteItemCfg(info);
        var rewritten = File.ReadAllText(cfgPath);
        Assert.Contains("published_id = 3712929235L;", rewritten);
        Assert.Contains("weird_extra_field = \"do not lose me\";", rewritten);
        Assert.Contains("title = \"Updated\";", rewritten);
    }
}
