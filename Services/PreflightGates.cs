using System.IO;

namespace VmbLauncher.Services;

/// <summary>
/// Cheap pre-stage gates the launcher runs on the single target mod before an
/// upload (and, where cheaper, a build). These promote the out-of-band static
/// lints that the pre-commit hook + CI run into the build→deploy iteration loop
/// that previously bypassed them (commit deferred; CI is continue-on-error).
///
/// Two gates live here:
///   1. Bundle-freshness — newest source mtime vs newest bundle mtime. Catches
///      the "uploaded v0.2, game ran v0.1" stale-bundle ship (the #1 silent
///      ship-wrong-thing path). See <see cref="BundleFreshness"/>.
///   2. qa/*.ps1 static lints — check_localization.ps1 (unescaped %, etc.) and
///      check_vmf_widget_types.ps1, scoped to the single mod directory so they
///      stay ripgrep-fast. See <see cref="QaScriptGate"/>.
/// </summary>
public static class BundleFreshness
{
    // Source file globs that, when newer than the newest bundle, indicate the
    // bundle is stale. scripts/** is the primary surface; resource_packages/**
    // carries .package manifests + any non-Lua source the bundle compiles in.
    private static readonly string[] SourceSubdirs = { "scripts", "resource_packages" };

    public sealed record Result(bool Stale, DateTime NewestSource, DateTime NewestBundle, string? NewestSourceFile);

    /// <summary>
    /// True when any tracked source file under the mod is newer than the newest
    /// .mod_bundle. If the mod has no bundles at all, treats it as stale (there
    /// is nothing to ship). If the mod has no source files, treats it as fresh
    /// (nothing could have changed).
    /// </summary>
    public static Result Check(ModInfo mod)
    {
        var bundleDir = mod.BundleV2Dir;
        DateTime newestBundle = DateTime.MinValue;
        if (Directory.Exists(bundleDir))
        {
            foreach (var f in Directory.EnumerateFiles(bundleDir, "*.mod_bundle"))
            {
                var t = File.GetLastWriteTimeUtc(f);
                if (t > newestBundle) newestBundle = t;
            }
        }

        DateTime newestSource = DateTime.MinValue;
        string? newestSourceFile = null;
        foreach (var sub in SourceSubdirs)
        {
            var dir = Path.Combine(mod.ModDir, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var t = File.GetLastWriteTimeUtc(f);
                if (t > newestSource)
                {
                    newestSource = t;
                    newestSourceFile = f;
                }
            }
        }

        // No source tracked → can't be stale.
        if (newestSource == DateTime.MinValue)
            return new Result(false, newestSource, newestBundle, null);

        // No bundle at all → stale (nothing to ship that matches source).
        if (newestBundle == DateTime.MinValue)
            return new Result(true, newestSource, newestBundle, newestSourceFile);

        return new Result(newestSource > newestBundle, newestSource, newestBundle, newestSourceFile);
    }
}

/// <summary>
/// Shells out to a qa/*.ps1 static lint, scoped to a single mod directory, and
/// maps its exit code (0 ok / 1 warnings / 2 errors) into a gate verdict.
/// </summary>
public static class QaScriptGate
{
    public enum Verdict { Ok, Warn, Error, NotRun }

    public sealed record Result(Verdict Verdict, int ExitCode, string ScriptName, string Stdout);

    /// <summary>Repo root for a mod is the parent of its mod directory (&lt;repo&gt;/&lt;mod&gt;).</summary>
    public static string RepoRootFor(ModInfo mod) => Path.GetDirectoryName(mod.ModDir.TrimEnd('\\', '/'))!;

    /// <summary>
    /// Run &lt;repo&gt;/qa/&lt;scriptName&gt; with -RepoRoot pointed at the SINGLE mod dir
    /// (the qa scripts recurse from RepoRoot, so scoping to the mod folder keeps
    /// the scan to just that mod). Returns NotRun if the script or a PowerShell
    /// host can't be found — the gate is best-effort and never blocks on its own
    /// absence.
    /// </summary>
    public static async Task<Result> RunAsync(ModInfo mod, string scriptName, Action<string>? log, CancellationToken ct = default)
    {
        var repoRoot = RepoRootFor(mod);
        var script = Path.Combine(repoRoot, "qa", scriptName);
        if (!File.Exists(script))
            return new Result(Verdict.NotRun, -1, scriptName, $"qa/{scriptName} not found");

        var pwsh = PowerShellLocator.Find();
        if (pwsh == null)
            return new Result(Verdict.NotRun, -1, scriptName, "no PowerShell host (pwsh/powershell) on PATH");

        var args = new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", script,
            "-RepoRoot", mod.ModDir,
            "-Quiet",
        };
        var res = await ProcessRunner.RunAsync(pwsh, args, repoRoot, log, null, ct);
        var verdict = res.ExitCode switch
        {
            0 => Verdict.Ok,
            1 => Verdict.Warn,
            _ => Verdict.Error,   // 2 (errors) or any unexpected non-zero
        };
        return new Result(verdict, res.ExitCode, scriptName, res.Stdout);
    }
}

/// <summary>Locate a PowerShell host: prefer pwsh (7+), fall back to Windows powershell.exe.</summary>
public static class PowerShellLocator
{
    private static string? _cached;
    private static bool _resolved;

    public static string? Find()
    {
        if (_resolved) return _cached;
        _resolved = true;
        foreach (var candidate in new[] { "pwsh.exe", "powershell.exe", "pwsh" })
        {
            var path = ResolveOnPath(candidate);
            if (path != null) { _cached = path; return _cached; }
        }
        _cached = null;
        return null;
    }

    private static string? ResolveOnPath(string exe)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full)) return full;
            }
            catch { /* malformed PATH segment */ }
        }
        return null;
    }
}
