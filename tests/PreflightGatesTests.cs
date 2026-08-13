using System.IO;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

/// <summary>
/// Pins the stale-bundle guard (item 1) and the cheap qa/*.ps1 lint gate wiring
/// (item 2). The localization-gate test plants an unescaped % and asserts the
/// gate returns an Error verdict (which UploadAsync turns into a blocked upload).
/// </summary>
public class PreflightGatesTests : MutationTestBase
{
    private static ModInfo MakeMod(string modDir) => new ModInfo
    {
        Name = Path.GetFileName(modDir),
        ModDir = modDir,
        ItemCfgPath = Path.Combine(modDir, "itemV2.cfg"),
    };

    private static void Touch(string path, DateTime utc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, utc);
    }

    // ---- BundleFreshness (item 1) -------------------------------------------------------

    [Fact]
    public void Freshness_source_newer_than_bundle_is_stale()
    {
        using var t = new TempDir();
        var mod = Path.Combine(t.Path, "mymod");
        var now = DateTime.UtcNow;
        Touch(Path.Combine(mod, "bundleV2", "a1b2c3.mod_bundle"), now.AddMinutes(-10));
        Touch(Path.Combine(mod, "scripts", "mods", "mymod", "mymod.lua"), now);   // newer source

        var r = BundleFreshness.Check(MakeMod(mod));
        Assert.True(r.Stale);
        Assert.EndsWith("mymod.lua", r.NewestSourceFile);
    }

    [Fact]
    public void Freshness_bundle_newer_than_source_is_fresh_silent()
    {
        using var t = new TempDir();
        var mod = Path.Combine(t.Path, "mymod");
        var now = DateTime.UtcNow;
        Touch(Path.Combine(mod, "scripts", "mods", "mymod", "mymod.lua"), now.AddMinutes(-10));
        Touch(Path.Combine(mod, "bundleV2", "a1b2c3.mod_bundle"), now);           // newer bundle

        var r = BundleFreshness.Check(MakeMod(mod));
        Assert.False(r.Stale);
    }

    [Fact]
    public void Freshness_resource_packages_source_counts_as_source()
    {
        using var t = new TempDir();
        var mod = Path.Combine(t.Path, "mymod");
        var now = DateTime.UtcNow;
        Touch(Path.Combine(mod, "bundleV2", "a1b2c3.mod_bundle"), now.AddMinutes(-10));
        Touch(Path.Combine(mod, "resource_packages", "mymod", "mymod.package"), now);

        var r = BundleFreshness.Check(MakeMod(mod));
        Assert.True(r.Stale);
    }

    [Fact]
    public void Freshness_no_bundle_is_stale()
    {
        using var t = new TempDir();
        var mod = Path.Combine(t.Path, "mymod");
        Touch(Path.Combine(mod, "scripts", "mods", "mymod", "mymod.lua"), DateTime.UtcNow);

        var r = BundleFreshness.Check(MakeMod(mod));
        Assert.True(r.Stale);   // nothing to ship that matches source
    }

    [Fact]
    public void Freshness_no_source_is_fresh()
    {
        using var t = new TempDir();
        var mod = Path.Combine(t.Path, "mymod");
        Touch(Path.Combine(mod, "bundleV2", "a1b2c3.mod_bundle"), DateTime.UtcNow);

        var r = BundleFreshness.Check(MakeMod(mod));
        Assert.False(r.Stale);  // no tracked source could have changed
    }

    // ---- QaScriptGate localization (item 2) --------------------------------------------
    // These run only when a real PowerShell host + the qa script are reachable; the launcher
    // is a Windows-only build so pwsh/powershell is present in the dev + CI environment. If
    // the host is genuinely absent the gate returns NotRun and the test self-skips, mirroring
    // the launcher's best-effort (never-block-on-absence) contract.

    [Fact]
    public async Task LocalizationGate_blocks_on_planted_unescaped_percent()
    {
        if (PowerShellLocator.Find() == null) return;  // no host in this env — gate is best-effort

        using var t = new TempDir();
        var repo = t.Path;
        // Mirror the repo layout the gate expects: <repo>/qa/check_localization.ps1 and
        // <repo>/<mod>/scripts/mods/<mod>/<mod>_{data,localization}.lua
        WriteQaScriptFixture(repo, "check_localization.ps1");

        var modName = "plantmod";
        var modDir = Path.Combine(repo, modName);
        var srcDir = Path.Combine(modDir, "scripts", "mods", modName);
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, $"{modName}_data.lua"),
            "return { setting_id = \"foo\", }\n");
        // Unescaped % in a loc value — the exact bug the gate must catch.
        File.WriteAllText(Path.Combine(srcDir, $"{modName}_localization.lua"),
            "return {\n  foo = { en = \"10% chance to crash\" },\n  mod_description = { en = \"ok\" },\n}\n");

        var mod = new ModInfo { Name = modName, ModDir = modDir, ItemCfgPath = Path.Combine(modDir, "itemV2.cfg") };
        var res = await QaScriptGate.RunAsync(mod, "check_localization.ps1", null);

        if (res.Verdict == QaScriptGate.Verdict.NotRun) return;  // script unavailable — skip
        Assert.Equal(QaScriptGate.Verdict.Error, res.Verdict);
        Assert.Equal(2, res.ExitCode);
    }

    [Fact]
    public async Task LocalizationGate_passes_on_clean_loc()
    {
        if (PowerShellLocator.Find() == null) return;

        using var t = new TempDir();
        var repo = t.Path;
        WriteQaScriptFixture(repo, "check_localization.ps1");

        var modName = "cleanmod";
        var modDir = Path.Combine(repo, modName);
        var srcDir = Path.Combine(modDir, "scripts", "mods", modName);
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, $"{modName}_data.lua"),
            "return { setting_id = \"foo\", }\n");
        File.WriteAllText(Path.Combine(srcDir, $"{modName}_localization.lua"),
            "return {\n  foo = { en = \"10%% chance, escaped\" },\n  mod_description = { en = \"ok\" },\n}\n");

        var mod = new ModInfo { Name = modName, ModDir = modDir, ItemCfgPath = Path.Combine(modDir, "itemV2.cfg") };
        var res = await QaScriptGate.RunAsync(mod, "check_localization.ps1", null);

        if (res.Verdict == QaScriptGate.Verdict.NotRun) return;
        Assert.NotEqual(QaScriptGate.Verdict.Error, res.Verdict);
    }

    // The launcher repository is intentionally standalone, so this process-boundary
    // test owns a minimal deterministic fixture instead of searching for a sibling
    // vermintide-2-tweaker checkout on the developer's machine.
    private static void WriteQaScriptFixture(string tempRepo, string scriptName)
    {
        var qa = Path.Combine(tempRepo, "qa");
        Directory.CreateDirectory(qa);
        File.WriteAllText(Path.Combine(qa, scriptName), """
param(
    [string]$RepoRoot,
    [switch]$Quiet
)
$bad = $false
Get-ChildItem -LiteralPath $RepoRoot -Recurse -File -Filter '*_localization.lua' |
    ForEach-Object {
        $raw = [System.IO.File]::ReadAllText($_.FullName)
        if ($raw -match '(?<!%)%(?!%)') { $bad = $true }
    }
if ($bad) { exit 2 }
exit 0
""");
    }
}
