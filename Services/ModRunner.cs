using System.IO;
using System.Security.Cryptography;

namespace VmbLauncher.Services;

public sealed record RunOutcome(bool Ok, string Message);

public sealed class ModRunner
{
    private readonly Settings _settings;
    private readonly Action<string> _log;

    public ModRunner(Settings settings, Action<string> log)
    {
        _settings = settings;
        _log = log;
    }

    private void L(string s) => _log(s);

    public async Task<RunOutcome> BuildAsync(ModInfo mod, bool clean, CancellationToken ct = default)
    {
        var vmb = VmbLocator.Resolve(_settings.VmbRoot);
        if (vmb == null) return new RunOutcome(false, "VMB not configured. Open Settings and set the VMB folder.");
        var project = VmbProject.Resolve(_settings.ProjectRoot) ?? VmbProject.Resolve(_settings.VmbRoot);
        if (project == null) return new RunOutcome(false, "Project folder not configured.");

        L($"[build] {mod.Name}");

        var args = new List<string>();
        if (vmb.Flavor == VmbFlavor.NodeScript)
        {
            args.Add(vmb.Executable); // vmb.js
        }
        args.AddRange(new[] { "build", mod.Name, "--no-workshop", "--cwd" });
        if (clean) args.Add("--clean");

        var fileName = vmb.Flavor == VmbFlavor.Binary ? vmb.Executable : (vmb.NodePath ?? "node.exe");

        // Run from the project folder so VMB reads .vmbrc from cwd (--cwd), respects mods_dir, etc.
        var result = await ProcessRunner.RunAsync(fileName, args, project.Root, L, null, ct);
        if (result.ExitCode != 0)
            return new RunOutcome(false, $"VMB build exited with code {result.ExitCode}");

        // Verify bundle output landed.
        if (!Directory.Exists(mod.BundleV2Dir))
            return new RunOutcome(false, $"bundleV2 missing after build: {mod.BundleV2Dir}");
        var bundles = Directory.EnumerateFiles(mod.BundleV2Dir, "*.mod_bundle").ToArray();
        if (bundles.Length == 0)
            return new RunOutcome(false, "Build produced no .mod_bundle files (silent failure).");

        L($"[build] OK -- {bundles.Length} bundle(s)");
        return new RunOutcome(true, $"Built {bundles.Length} bundle(s)");
    }

    public async Task<RunOutcome> DeployAsync(ModInfo mod, CancellationToken ct = default)
    {
        await Task.Yield();

        var workshopRoot = _settings.WorkshopContentRoot;
        if (string.IsNullOrEmpty(workshopRoot))
            return new RunOutcome(false, "Workshop content folder not configured. Open Settings.");

        var id = ResolveWorkshopId(mod);
        if (string.IsNullOrEmpty(id))
            return new RunOutcome(false, $"No Workshop ID for {mod.Name}. Set published_id in itemV2.cfg or override in Settings.");

        var dst = Path.Combine(workshopRoot, id);
        if (!Directory.Exists(dst))
            return new RunOutcome(false, $"Workshop folder missing:\n{dst}\n\nSubscribe to your own Workshop item in Steam first (Steam doesn't auto-subscribe to your own uploads).");

        if (!Directory.Exists(mod.BundleV2Dir))
            return new RunOutcome(false, $"No build output. Run Build first ({mod.BundleV2Dir} not found).");

        L($"[deploy] {mod.Name} -> {dst}");

        // Clean stale bundles.
        foreach (var f in Directory.EnumerateFiles(dst, "*.mod"))
            File.Delete(f);
        foreach (var f in Directory.EnumerateFiles(dst, "*.mod_bundle"))
            File.Delete(f);

        // Copy + hash verify.
        var copied = 0;
        foreach (var src in Directory.EnumerateFiles(mod.BundleV2Dir))
        {
            var dest = Path.Combine(dst, Path.GetFileName(src));
            File.Copy(src, dest, overwrite: true);
            var sh = HashFile(src);
            var dh = HashFile(dest);
            if (sh != dh)
                return new RunOutcome(false, $"Hash mismatch on {Path.GetFileName(src)} -- copy did not land cleanly");
            copied++;
        }
        L($"[deploy] OK -- {copied} file(s) copied to {Path.GetFileName(dst)}/");
        return new RunOutcome(true, $"Deployed {copied} file(s)");
    }

    public async Task<RunOutcome> UploadAsync(ModInfo mod, bool allowPublic, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_settings.UgcToolPath) || !File.Exists(_settings.UgcToolPath))
            return new RunOutcome(false, "ugc_tool.exe not found. Set the path in Settings.");

        if (!Directory.Exists(mod.BundleV2Dir) || !Directory.EnumerateFiles(mod.BundleV2Dir, "*.mod_bundle").Any())
            return new RunOutcome(false, "No build output. Run Build first.");

        if (!SteamLocator.IsSteamRunning())
            return new RunOutcome(false, "Steam isn't running. Start Steam, then retry.");

        if (mod.IsPublic && !allowPublic)
            return new RunOutcome(false, "itemV2.cfg has visibility = \"public\". Re-run with the Allow Public confirmation. Public mods can be flagged irreversibly.");

        L($"[upload] {mod.Name}");
        var result = await ProcessRunner.RunWithEulaYesAsync(_settings.UgcToolPath!, new[] { "-c", mod.ItemCfgPath, "-x" }, L, ct);
        if (result.ExitCode != 0)
            return new RunOutcome(false, $"ugc_tool exited with code {result.ExitCode}");

        L("[upload] ugc_tool reported finished. VERIFY the Workshop page shows updated file size -- ugc_tool prints success even on transfer failures.");
        return new RunOutcome(true, "Upload finished (verify size on Workshop page)");
    }

    private string? ResolveWorkshopId(ModInfo mod)
    {
        if (_settings.WorkshopIdOverrides.TryGetValue(mod.Name, out var ov) && !string.IsNullOrEmpty(ov))
            return ov;
        return string.IsNullOrEmpty(mod.PublishedId) ? null : mod.PublishedId;
    }

    private static string HashFile(string path)
    {
        using var s = File.OpenRead(path);
        using var md5 = MD5.Create();
        return Convert.ToHexString(md5.ComputeHash(s));
    }
}
