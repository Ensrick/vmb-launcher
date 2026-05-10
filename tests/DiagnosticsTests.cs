using System.IO;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public class DiagnosticsTests
{
    [Fact]
    public void RunAll_empty_settings_has_errors()
    {
        var s = new Settings();
        var checks = Diagnostics.RunAll(s);
        Assert.True(Diagnostics.HasErrors(checks));
        // VMB row should be Error
        Assert.Contains(checks, c => c.Title == "VMB" && c.Status == CheckStatus.Error);
    }

    [Fact]
    public void RunAll_with_vmbroot_but_no_other_paths_still_has_errors()
    {
        using var t = new TempDir();
        File.WriteAllBytes(Path.Combine(t.Path, "vmb.exe"), Array.Empty<byte>());
        var s = new Settings { VmbRoot = t.Path };
        var checks = Diagnostics.RunAll(s);
        var vmb = checks.Single(c => c.Title == "VMB");
        Assert.Equal(CheckStatus.Ok, vmb.Status);
        // SDK / ugc_tool / Steam still missing
        Assert.True(Diagnostics.HasErrors(checks));
    }

    [Fact]
    public void RunAll_with_all_paths_set_has_no_hard_errors()
    {
        using var vmb = new TempDir();
        using var sdk = new TempDir();
        using var steam = new TempDir();
        using var ws = new TempDir();
        File.WriteAllBytes(Path.Combine(vmb.Path, "vmb.exe"), Array.Empty<byte>());
        var ugc = Path.Combine(sdk.Path, "ugc_uploader", "ugc_tool.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(ugc)!);
        File.WriteAllBytes(ugc, Array.Empty<byte>());

        var s = new Settings
        {
            VmbRoot = vmb.Path,
            ProjectRoot = vmb.Path,
            SteamRoot = steam.Path,
            Vt2SdkRoot = sdk.Path,
            UgcToolPath = ugc,
            WorkshopContentRoot = ws.Path,
        };
        var checks = Diagnostics.RunAll(s);
        // Steam-running may legitimately be a Warn on the test machine.
        Assert.False(Diagnostics.HasErrors(checks));
    }

    [Fact]
    public void HasErrors_only_counts_error_status()
    {
        var rs = new[]
        {
            new CheckResult("a", CheckStatus.Ok, ""),
            new CheckResult("b", CheckStatus.Warn, ""),
        };
        Assert.False(Diagnostics.HasErrors(rs));
        Assert.True(Diagnostics.HasIssues(rs));
    }

    [Fact]
    public void HasIssues_counts_warn_and_error()
    {
        var rs = new[]
        {
            new CheckResult("a", CheckStatus.Ok, ""),
            new CheckResult("b", CheckStatus.Warn, ""),
        };
        Assert.True(Diagnostics.HasIssues(rs));
    }

    [Fact]
    public void HasIssues_false_when_all_ok()
    {
        var rs = new[]
        {
            new CheckResult("a", CheckStatus.Ok, ""),
            new CheckResult("b", CheckStatus.Ok, ""),
        };
        Assert.False(Diagnostics.HasIssues(rs));
    }
}
