using VmbLauncher.Services;

namespace VmbLauncher.Tests;

/// <summary>
/// Tests for <see cref="RemoteDeploy"/>. Targeted at the v0.4.1 scp-arglist regression: every
/// `deploy` and `all` failed with `scp: error: unexpected filename: C:\Users\...` because
/// OpenSSH 9's new guard rejects Windows absolute paths whose embedded `:` looks like a
/// `host:path` remote spec. The fix is the `--` end-of-options sentinel before the source
/// arg; these tests pin that sentinel in place.
/// </summary>
public class RemoteDeployTests
{
    [Fact]
    public void BuildScpArgs_includes_end_of_options_sentinel_before_src()
    {
        // The "--" MUST appear after "-O" but BEFORE the src path. This is the load-bearing
        // bit: without it, scp's option parser interprets `C:\path\foo.mod_bundle` as a
        // `host:path` remote spec and fails OpenSSH 9's "unexpected filename" guard.
        // v0.5.2+ also normalizes the src path to forward slashes (see test below).
        var src = @"C:\Users\danjo\source\repos\vermintide-2-tweaker\general_tweaker\bundleV2\abc123.mod_bundle";
        var destSpec = "pc-b:\"C:/(025) Steam/steamapps/workshop/content/552500/3713619122/\"";
        var srcFwd = src.Replace('\\', '/');

        var args = RemoteDeploy.BuildScpArgs(src, destSpec);

        Assert.Equal(new[] { "-O", "--", srcFwd, destSpec }, args);
    }

    [Fact]
    public void BuildScpArgs_sentinel_is_positioned_correctly_relative_to_src()
    {
        // Defensive: explicitly assert the index of "--" is exactly one position before src.
        // Catches "moved the sentinel after src" regressions where scp would still accept the
        // arg but the sentinel would be parsed as the destination (or vice versa).
        var src = @"C:\some\file.mod_bundle";
        var destSpec = "pc-b:\"C:/dest/\"";

        var args = RemoteDeploy.BuildScpArgs(src, destSpec);

        var sentinelIdx = Array.IndexOf(args, "--");
        var srcIdx = Array.IndexOf(args, src.Replace('\\', '/'));
        Assert.True(sentinelIdx >= 0, "expected -- sentinel in arg list");
        Assert.True(srcIdx > sentinelIdx, "src must come AFTER -- sentinel");
        Assert.Equal(srcIdx, sentinelIdx + 1);
    }

    [Fact]
    public void BuildScpArgs_normalizes_src_backslashes_to_forward_slashes()
    {
        // Burned 2026-05-24 (v0.5.1 → v0.5.2): even with `-O --`, the launcher's scp
        // invocation failed every remote deploy with `scp: error: unexpected filename:
        // C:\...` while the same argv from a PowerShell .NET repro succeeded. Forward-
        // slashing the local src made the call repeatable from any host process. The
        // theory: OpenSSH 9's receiver-side "unexpected filename" guard inspects the
        // basename frame serialized into the SCP protocol, and backslashes in the
        // basename can mis-parse depending on the local scp.exe + .NET process env
        // tokenization. Forward slashes always parse correctly.
        var src = @"C:\Users\danjo\source\repos\vermintide-2-tweaker\general_tweaker\bundleV2\abc123.mod_bundle";
        var args = RemoteDeploy.BuildScpArgs(src, "pc-b:\"C:/dest/\"");

        Assert.Contains(@"C:/Users/danjo/source/repos/vermintide-2-tweaker/general_tweaker/bundleV2/abc123.mod_bundle", args);
        Assert.DoesNotContain(args, a => a.Contains('\\'));
    }

    [Fact]
    public void BuildScpArgs_preserves_windows_drive_letter_after_normalization()
    {
        // The whole point of -- is to let us pass the raw Windows path through without quoting
        // or escaping. After backslash normalization we should still see the `C:` drive prefix
        // intact (only the separators flip, not the drive letter or path components).
        var src = @"C:\Users\danjo\path with spaces\file.mod_bundle";
        var args = RemoteDeploy.BuildScpArgs(src, "pc-b:\"C:/dest/\"");

        Assert.Contains(@"C:/Users/danjo/path with spaces/file.mod_bundle", args);
    }

    [Fact]
    public void BuildScpArgs_legacy_scp_protocol_flag_comes_first()
    {
        // -O must precede --. scp processes -O as an option BEFORE the end-of-options sentinel
        // closes parsing; swapping them would silently drop -O and fall back to SFTP, which
        // mangles Windows paths with spaces (the original reason -O was added).
        var args = RemoteDeploy.BuildScpArgs(@"C:\x.bin", "pc-b:dest/");

        Assert.Equal("-O", args[0]);
        Assert.Equal("--", args[1]);
    }
}
