using System.IO;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public class TitleVersionSyncTests : MutationTestBase
{
    // ---- ApplyVersionSuffix (pure string helper) -----------------------------------------

    [Fact]
    public void RewritesTitleSuffix_WhenSuffixMatches()
    {
        var got = TitleVersionSync.ApplyVersionSuffix("X v0.1.0", "0.2.0");
        Assert.Equal("X v0.2.0", got);
    }

    [Fact]
    public void RewritesTitleSuffix_WhenSuffixHasTrack()
    {
        var got = TitleVersionSync.ApplyVersionSuffix("X v0.1.0-alpha", "0.2.0-dev");
        Assert.Equal("X v0.2.0-dev", got);
    }

    [Fact]
    public void AppendsSuffix_WhenAbsent()
    {
        var got = TitleVersionSync.ApplyVersionSuffix("X", "0.1.0");
        Assert.Equal("X v0.1.0", got);
    }

    [Fact]
    public void Idempotent_OnApplyVersionSuffix()
    {
        var once = TitleVersionSync.ApplyVersionSuffix("X v0.1.0", "0.1.0");
        var twice = TitleVersionSync.ApplyVersionSuffix(once, "0.1.0");
        Assert.Equal("X v0.1.0", once);
        Assert.Equal(once, twice);
    }

    [Theory]
    [InlineData("Tweaker: Cosmetics v0.9.8.8", "0.9.10-dev", "Tweaker: Cosmetics v0.9.10-dev")]
    [InlineData("Tweaker: CW v0.7.83-alpha", "0.7.84", "Tweaker: CW v0.7.84")]
    [InlineData("Foo Bar Baz v1.2.3.4-rc2", "1.2.4", "Foo Bar Baz v1.2.4")]
    [InlineData("No suffix here", "0.1.0", "No suffix here v0.1.0")]
    [InlineData("Has v_not_a_version_word", "0.1.0", "Has v_not_a_version_word v0.1.0")]
    public void ApplyVersionSuffix_TableDriven(string input, string version, string expected)
    {
        Assert.Equal(expected, TitleVersionSync.ApplyVersionSuffix(input, version));
    }

    // ---- ReadModVersion ------------------------------------------------------------------

    [Fact]
    public void ReadModVersion_ParsesConstant()
    {
        using var td = new TempDir();
        var lua = td.Write("mymod.lua",
            "local mod = get_mod(\"mymod\")\nlocal MOD_VERSION = \"0.4.2-dev\"\nreturn mod\n");
        Assert.Equal("0.4.2-dev", TitleVersionSync.ReadModVersion(lua));
    }

    [Fact]
    public void Throws_WhenModVersionUnparseable()
    {
        using var td = new TempDir();
        var lua = td.Write("mymod.lua", "local mod = get_mod(\"mymod\")\n-- no version here\nreturn mod\n");
        var ex = Assert.Throws<InvalidOperationException>(() => TitleVersionSync.ReadModVersion(lua));
        Assert.Contains("MOD_VERSION", ex.Message);
    }

    [Fact]
    public void Throws_WhenLuaFileMissing()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => TitleVersionSync.ReadModVersion(@"C:\definitely\does\not\exist\mymod.lua"));
        Assert.Contains("MOD_VERSION lua file not found", ex.Message);
    }

    // ---- RewriteCfgTitle -----------------------------------------------------------------

    [Fact]
    public void RewriteCfgTitle_PreservesOtherFields()
    {
        var cfg = "title = \"Tweaker: Events v0.4.1-dev\";\r\n" +
                  "description = \"Has v0.4.1-dev mentioned in body, untouched.\";\r\n" +
                  "preview = \"item_preview.png\";\r\n" +
                  "visibility = \"friends_only\";\r\n";
        var result = TitleVersionSync.RewriteCfgTitle(cfg, "0.4.2-dev");
        Assert.True(result.Changed);
        Assert.Contains("title = \"Tweaker: Events v0.4.2-dev\";", result.NewCfgText);
        // Description (which contains the OLD version in its body) is untouched.
        Assert.Contains("Has v0.4.1-dev mentioned in body, untouched.", result.NewCfgText);
        Assert.Contains("preview = \"item_preview.png\";", result.NewCfgText);
        Assert.Contains("visibility = \"friends_only\";", result.NewCfgText);
    }

    [Fact]
    public void RewriteCfgTitle_PreservesCrlfLineEndings()
    {
        var cfg = "title = \"X v0.1.0\";\r\nvisibility = \"private\";\r\n";
        var result = TitleVersionSync.RewriteCfgTitle(cfg, "0.2.0");
        Assert.True(result.Changed);
        Assert.Contains("\r\n", result.NewCfgText);
        // Should not have introduced LF-only lines.
        Assert.DoesNotContain("\";\nvisibility", result.NewCfgText);
    }

    [Fact]
    public void RewriteCfgTitle_Idempotent_WhenAlreadyCorrect()
    {
        var cfg = "title = \"X v0.1.0\";\r\nvisibility = \"private\";\r\n";
        var result = TitleVersionSync.RewriteCfgTitle(cfg, "0.1.0");
        Assert.False(result.Changed);
        Assert.Equal(cfg, result.NewCfgText);
        Assert.Equal("X v0.1.0", result.OldTitle);
        Assert.Equal("X v0.1.0", result.NewTitle);
    }

    [Fact]
    public void RewriteCfgTitle_NoOpWhenNoTitleLine()
    {
        var cfg = "visibility = \"private\";\r\ndescription = \"foo\";\r\n";
        var result = TitleVersionSync.RewriteCfgTitle(cfg, "0.1.0");
        Assert.False(result.Changed);
        Assert.Equal(cfg, result.NewCfgText);
    }

    [Fact]
    public void RewriteCfgTitle_HandlesEscapedQuotesInTitle()
    {
        var cfg = "title = \"Has \\\"quotes\\\" v0.1.0\";\r\nvisibility = \"private\";\r\n";
        var result = TitleVersionSync.RewriteCfgTitle(cfg, "0.2.0");
        Assert.True(result.Changed);
        Assert.Equal("Has \"quotes\" v0.1.0", result.OldTitle);
        Assert.Equal("Has \"quotes\" v0.2.0", result.NewTitle);
        Assert.Contains("title = \"Has \\\"quotes\\\" v0.2.0\";", result.NewCfgText);
    }

    // ---- SyncTitle (end-to-end with file IO) ---------------------------------------------

    private sealed class FakeMod : IDisposable
    {
        public TempDir Dir { get; }
        public ModInfo Mod { get; }
        public string LuaPath { get; }

        public FakeMod(string modName, string modVersion, string cfgTitle)
        {
            Dir = new TempDir();
            var modDir = Dir.CreateSubdir(modName);
            var luaSub = Path.Combine(modDir, "scripts", "mods", modName);
            Directory.CreateDirectory(luaSub);
            LuaPath = Path.Combine(luaSub, $"{modName}.lua");
            File.WriteAllText(LuaPath,
                $"local mod = get_mod(\"{modName}\")\nlocal MOD_VERSION = \"{modVersion}\"\nreturn mod\n");

            var cfgPath = Path.Combine(modDir, "itemV2.cfg");
            var cfgText = $"title = \"{cfgTitle}\";\r\ndescription = \"d\";\r\nvisibility = \"private\";\r\n";
            File.WriteAllText(cfgPath, cfgText);

            Mod = new ModInfo
            {
                Name = modName,
                ModDir = modDir,
                ItemCfgPath = cfgPath,
            };
            ModDiscovery.ParseItemCfg(Mod);
        }

        public void Dispose() => Dir.Dispose();
    }

    [Fact]
    public void SyncTitle_RewritesAndWritesBack()
    {
        using var fake = new FakeMod("mymod", "0.2.0", "X v0.1.0");
        var result = TitleVersionSync.SyncTitle(fake.Mod);
        Assert.True(result.Changed);
        var raw = File.ReadAllText(fake.Mod.ItemCfgPath);
        Assert.Contains("title = \"X v0.2.0\";", raw);
        Assert.Equal("X v0.2.0", fake.Mod.Title);
    }

    [Fact]
    public void SyncTitle_Idempotent_WhenAlreadyCorrect_FileMtimeUnchanged()
    {
        using var fake = new FakeMod("mymod", "0.1.0", "X v0.1.0");
        var originalMtime = File.GetLastWriteTimeUtc(fake.Mod.ItemCfgPath);
        // Wait a moment so a hypothetical write would bump the mtime detectably.
        System.Threading.Thread.Sleep(50);

        var result = TitleVersionSync.SyncTitle(fake.Mod);
        Assert.False(result.Changed);
        var newMtime = File.GetLastWriteTimeUtc(fake.Mod.ItemCfgPath);
        Assert.Equal(originalMtime, newMtime);
    }

    [Fact]
    public void SyncTitle_DryRun_DoesNotWriteCfg()
    {
        using var fake = new FakeMod("mymod", "0.2.0", "X v0.1.0");
        var originalRaw = File.ReadAllText(fake.Mod.ItemCfgPath);

        var result = TitleVersionSync.SyncTitle(fake.Mod, dryRun: true);
        Assert.True(result.Changed);
        Assert.Equal("X v0.2.0", result.NewTitle);

        // File on disk untouched.
        var afterRaw = File.ReadAllText(fake.Mod.ItemCfgPath);
        Assert.Equal(originalRaw, afterRaw);
        // ModInfo.Title also not mutated when dry-run.
        Assert.Equal("X v0.1.0", fake.Mod.Title);
    }

    [Fact]
    public void ValidateTitleForPublication_AcceptsExactCommittedTitleWithoutWriting()
    {
        using var fake = new FakeMod("mymod", "0.2.0-dev", "X v0.2.0-dev");
        var before = File.ReadAllBytes(fake.Mod.ItemCfgPath);

        var result = TitleVersionSync.ValidateTitleForPublication(fake.Mod);

        Assert.False(result.Changed);
        Assert.Equal(before, File.ReadAllBytes(fake.Mod.ItemCfgPath));
        Assert.Equal("X v0.2.0-dev", fake.Mod.Title);
    }

    [Fact]
    public void ValidateTitleForPublication_RejectsMismatchWithoutDirtyingReviewedCfg()
    {
        using var fake = new FakeMod("mymod", "0.2.0-dev", "X v0.1.0-dev");
        var before = File.ReadAllBytes(fake.Mod.ItemCfgPath);

        var error = Assert.Throws<InvalidOperationException>(() =>
            TitleVersionSync.ValidateTitleForPublication(fake.Mod));

        Assert.Contains("reviewed title", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllBytes(fake.Mod.ItemCfgPath));
        Assert.Equal("X v0.1.0-dev", fake.Mod.Title);
    }

    [Fact]
    public void SyncTitle_Throws_WhenModVersionMissingFromLua()
    {
        using var td = new TempDir();
        var modDir = td.CreateSubdir("mymod");
        var luaSub = Path.Combine(modDir, "scripts", "mods", "mymod");
        Directory.CreateDirectory(luaSub);
        File.WriteAllText(Path.Combine(luaSub, "mymod.lua"), "-- no MOD_VERSION here\n");
        var cfgPath = Path.Combine(modDir, "itemV2.cfg");
        File.WriteAllText(cfgPath, "title = \"X v0.1.0\";\r\nvisibility = \"private\";\r\n");

        var mod = new ModInfo { Name = "mymod", ModDir = modDir, ItemCfgPath = cfgPath };
        ModDiscovery.ParseItemCfg(mod);

        var ex = Assert.Throws<InvalidOperationException>(() => TitleVersionSync.SyncTitle(mod));
        Assert.Contains("MOD_VERSION", ex.Message);
        // Cfg must not have been touched.
        var raw = File.ReadAllText(cfgPath);
        Assert.Contains("title = \"X v0.1.0\";", raw);
    }

    [Fact]
    public void SyncTitle_TwoRunsLeaveFileByteIdentical()
    {
        using var fake = new FakeMod("mymod", "0.2.0", "X v0.1.0");
        TitleVersionSync.SyncTitle(fake.Mod);
        var afterFirst = File.ReadAllBytes(fake.Mod.ItemCfgPath);
        TitleVersionSync.SyncTitle(fake.Mod);
        var afterSecond = File.ReadAllBytes(fake.Mod.ItemCfgPath);
        Assert.Equal(afterFirst, afterSecond);
    }
}
