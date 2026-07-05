using System.IO;

namespace VmbLauncher.Services;

/// <summary>
/// A remote machine the launcher pushes deploys to in addition to the local Workshop folder.
/// Configured in settings.json; auto-detected from ~/.ssh/config when possible.
/// </summary>
public sealed class RemoteDeployTarget
{
    /// <summary>Display name for log lines (e.g. "pc-b").</summary>
    public string Name { get; set; } = "";
    /// <summary>SSH host alias resolvable via ~/.ssh/config. Authentication is key-only — interactive prompts are not supported in headless mode.</summary>
    public string SshHost { get; set; } = "";
    /// <summary>Remote Workshop content root (Windows-style path), e.g. "C:/(025) Steam/steamapps/workshop/content/552500".</summary>
    public string WorkshopContentRoot { get; set; } = "";
    /// <summary>Disabled targets are skipped without warning. Default true.</summary>
    public bool Enabled { get; set; } = true;
}

public static class RemoteDeploy
{
    /// <summary>
    /// Push the contents of <paramref name="bundleDir"/> to every enabled remote target's
    /// `&lt;WorkshopContentRoot&gt;/&lt;workshopId&gt;/` directory. Each file is sent via
    /// `scp -O` (legacy SCP protocol — required for Windows OpenSSH targets whose paths
    /// contain spaces; the modern SFTP protocol mangles the destination quoting). After
    /// the transfer, the remote file size is compared against the local size as a cheap
    /// integrity check.
    ///
    /// Returns an aggregate outcome — fails fast on first remote that errors. Local deploy
    /// is already complete when this runs, so the failure mode is "PC-A current, PC-B stale";
    /// the loud failure surfaces that immediately rather than letting it rot.
    /// </summary>
    public static async Task<RunOutcome> PushAsync(
        IReadOnlyList<RemoteDeployTarget> targets,
        string bundleDir,
        string workshopId,
        Action<string> log,
        CancellationToken ct = default)
    {
        var enabled = targets.Where(t => t.Enabled
                                         && !string.IsNullOrWhiteSpace(t.SshHost)
                                         && !string.IsNullOrWhiteSpace(t.WorkshopContentRoot)).ToList();
        if (enabled.Count == 0) return new RunOutcome(true, "no remote targets configured");

        var files = Directory.EnumerateFiles(bundleDir).ToArray();
        if (files.Length == 0) return new RunOutcome(false, $"no files to push from {bundleDir}");

        foreach (var t in enabled)
        {
            // Forward-slash + trim trailing slashes so the join below produces a single clean path.
            var root = t.WorkshopContentRoot.Replace('\\', '/').TrimEnd('/');
            var remoteDir = $"{root}/{workshopId}";

            log($"[deploy:remote] {t.Name} ({t.SshHost}) -> {remoteDir}/");

            // Verify the target directory exists. If the remote Steam isn't subscribed to this
            // mod, the folder won't exist and scp would create a stray directory in the wrong
            // place — better to fail loudly with a clear message.
            var testCmd = $"Test-Path '{remoteDir.Replace("'", "''")}'";
            var checkResult = await ProcessRunner.RunAsync("ssh", new[] { t.SshHost, testCmd }, null, null, null, ct);
            if (checkResult.ExitCode != 0)
                return new RunOutcome(false, $"ssh probe to {t.Name} failed (exit {checkResult.ExitCode}). Check ~/.ssh/config and that {t.SshHost} is reachable. stderr: {checkResult.Stderr.Trim()}");
            if (!checkResult.Stdout.Contains("True", StringComparison.OrdinalIgnoreCase))
                return new RunOutcome(false, $"{t.Name} workshop dir does not exist: {remoteDir}. Subscribe to the mod on {t.Name} (or fix WorkshopContentRoot in settings.json).");

            // scp -O = force legacy SCP protocol. Windows OpenSSH's default SFTP protocol does
            // NOT cleanly handle paths with spaces in the remote shell tokenization layer —
            // documented in reference_pc_b_dispatch.md.
            //
            // `--` end-of-options sentinel is REQUIRED before the source path. OpenSSH 9 added
            // an "unexpected filename" guard that rejects Windows absolute paths like
            // `C:\Users\...\foo.mod_bundle` because the embedded `:` makes scp parse the arg
            // as `host:path` (a remote spec) rather than a local file. The `--` sentinel stops
            // option/spec parsing and forces both subsequent args to be treated as positional
            // (src, dest). Without it, every remote deploy fails with
            // `scp: error: unexpected filename: C:\...`. Burned VMBLauncher v0.4.0 across all 4 mods.
            var destSpec = $"{t.SshHost}:\"{remoteDir}/\"";
            var pushed = 0;
            foreach (var src in files)
            {
                ct.ThrowIfCancellationRequested();
                var scp = await ProcessRunner.RunAsync("scp", BuildScpArgs(src, destSpec), null, null, null, ct);
                if (scp.ExitCode != 0)
                    return new RunOutcome(false, $"scp failed for {Path.GetFileName(src)} -> {t.Name} (exit {scp.ExitCode}). stderr: {scp.Stderr.Trim()}");
                pushed++;
            }

            // Size-verify each transferred file. Cheaper than hashing across the wire; catches
            // truncated writes and "scp said OK but nothing arrived" silently-zero-byte cases.
            var localManifest = string.Join("|", files.Select(f => $"{Path.GetFileName(f)}={new FileInfo(f).Length}"));
            var remoteProbe = $"Get-ChildItem '{remoteDir.Replace("'", "''")}' -File | ForEach-Object {{ \"$($_.Name)=$($_.Length)\" }} | Sort-Object";
            var sizeCheck = await ProcessRunner.RunAsync("ssh", new[] { t.SshHost, remoteProbe }, null, null, null, ct);
            if (sizeCheck.ExitCode != 0)
                return new RunOutcome(false, $"ssh size-probe to {t.Name} failed (exit {sizeCheck.ExitCode})");

            foreach (var f in files)
            {
                var expected = $"{Path.GetFileName(f)}={new FileInfo(f).Length}";
                if (!sizeCheck.Stdout.Contains(expected, StringComparison.Ordinal))
                {
                    return new RunOutcome(false, $"size mismatch on {t.Name}: expected '{expected}' not found in remote listing. Remote may be stale or write was truncated.");
                }
            }

            log($"[deploy:remote] {t.Name} OK -- {pushed} file(s) verified ({files.Length} sizes match)");
        }

        return new RunOutcome(true, $"pushed to {enabled.Count} remote target(s)");
    }

    /// <summary>
    /// Build the argument list for a single-file scp transfer. Extracted from <see cref="PushAsync"/>
    /// so the v0.4.1 fix (the `--` end-of-options sentinel) is unit-testable without booting
    /// a real ssh transport. Order is load-bearing:
    /// <list type="number">
    ///   <item><c>-O</c> — force legacy SCP protocol (option, parsed before sentinel).</item>
    ///   <item><c>--</c> — end-of-options sentinel. Stops scp from parsing later args as flags
    ///                     or `host:path` remote specs. Without it, OpenSSH 9's "unexpected
    ///                     filename" guard rejects Windows absolute paths whose embedded `:`
    ///                     looks like a remote spec.</item>
    ///   <item>src — local source file (Windows absolute path acceptable, thanks to <c>--</c>).</item>
    ///   <item>destSpec — remote `host:path` spec, kept AFTER the sentinel deliberately so
    ///                    scp still recognises the colon as a remote separator.</item>
    /// </list>
    /// </summary>
    public static string[] BuildScpArgs(string src, string destSpec)
    {
        // Normalize Windows-style backslashes to forward slashes in the local source path.
        // Both forms parse as valid paths on Windows scp.exe; forward slashes also avoid the
        // OpenSSH 9 "unexpected filename" receiver-side guard which can mis-parse a basename
        // when backslashes leak into the SCP-protocol filename frame. Burned 2026-05-24: even
        // with `-O --`, the launcher's scp invocation failed with that error on every remote
        // deploy while the same argv from a PowerShell .NET repro succeeded — the difference
        // was the local path tokenization in the single-file runtime's process environment.
        // Forward-slashing the local src makes the call repeatable from any host process.
        var normalizedSrc = src.Replace('\\', '/');
        return new[] { "-O", "--", normalizedSrc, destSpec };
    }

    /// <summary>
    /// Best-effort: parse ~/.ssh/config for `Host pc-b` (or any future preset host names) and
    /// return a target if found. Called once during Settings.AutoFillMissing on first run so
    /// users with the standard PC-B tailnet alias already configured get remote deploy turned
    /// on without editing settings.json by hand.
    /// </summary>
    /// <remarks>
    /// We deliberately don't claim a generic match — the auto-detect is limited to a known
    /// allowlist (currently just "pc-b") with the workshop path pinned from project memory.
    /// New remotes should be added explicitly to settings.json so users opt into where their
    /// bundles get pushed.
    /// </remarks>
    public static List<RemoteDeployTarget> AutoDetectFromSshConfig()
    {
        var sshConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
        if (!File.Exists(sshConfig)) return new List<RemoteDeployTarget>();

        string text;
        try { text = File.ReadAllText(sshConfig); }
        catch { return new List<RemoteDeployTarget>(); }

        var detected = new List<RemoteDeployTarget>();
        // Look for `Host pc-b` (case-insensitive, whole-token). The path is pinned to PC-B's
        // known Steam install location ((025) Steam, not the default Program Files path) —
        // this is the value documented in reference_pc_b_dispatch.md.
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(?im)^\s*Host\s+pc-b\b"))
        {
            detected.Add(new RemoteDeployTarget
            {
                Name = "pc-b",
                SshHost = "pc-b",
                WorkshopContentRoot = "C:/(025) Steam/steamapps/workshop/content/552500",
                Enabled = true,
            });
        }

        return detected;
    }
}
