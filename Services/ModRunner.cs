using System.IO;
using System.Security.Cryptography;

namespace VmbLauncher.Services;

public sealed record RunOutcome(bool Ok, string Message);

public sealed class ModRunner
{
    private readonly Settings _settings;
    private readonly Action<string> _log;

    // Cross-process upload lock (issue #344). Named system-wide so two launcher PROCESSES serialize
    // around the SINGLE shared <SDK>/ugc_uploader/sample_item/ staging dir. It is a Semaphore, not a
    // Mutex — see the rationale at the acquisition site (Mutex thread-affinity breaks across the
    // `await` on ugc_tool, so ReleaseMutex would throw on the continuation thread).
    private const string UploadLockName = @"Global\VMBLauncher_ugc_upload";
    private static readonly TimeSpan UploadLockTimeout = TimeSpan.FromMinutes(5);

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
        => await DeployAsync(mod, skipRemote: false, ct);

    public async Task<RunOutcome> DeployAsync(ModInfo mod, bool skipRemote, CancellationToken ct = default)
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

        // Issue #344 crossed-published_id guard. Before we DELETE + overwrite files in this
        // Workshop folder, verify the folder is actually owned by THIS mod. A crossed id would
        // otherwise write this mod's bundle into another mod's item folder — the user-facing #344
        // breakage: gut_dev's ship carried ct_dev's id, so a deploy wrote ct files into gut's
        // folder, the game then double-loaded ct (VMF duplicate-mod fatal) and never loaded gut.
        var deployGuard = CheckWorkshopIdOwnership(id, mod, "deploy");
        if (!deployGuard.Ok) return deployGuard;

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

        // Remote push (PC-B and any other configured target) runs by default. The user's
        // standing rule (feedback_deploy_both_machines) is that local-only deploys mask
        // host/client sync bugs because the test PC keeps running the stale build. The
        // launcher enforces the rule so individual workflows can't forget. Opt out per-
        // invocation with --no-remote when the user really only wants the local push.
        if (!skipRemote && _settings.RemoteDeployTargets.Count > 0)
        {
            var remote = await RemoteDeploy.PushAsync(_settings.RemoteDeployTargets, mod.BundleV2Dir, id, _log, ct);
            if (!remote.Ok)
                return new RunOutcome(false, $"local deploy OK, but remote push failed: {remote.Message}");
        }

        return new RunOutcome(true, $"Deployed {copied} file(s)");
    }

    public async Task<RunOutcome> UploadAsync(ModInfo mod, bool allowPublic, CancellationToken ct = default)
        => await UploadAsync(mod, allowPublic, dryRunTitleRewrite: false, publicationReceiptPath: null, ct);

    public async Task<RunOutcome> UploadAsync(ModInfo mod, bool allowPublic, bool dryRunTitleRewrite, CancellationToken ct = default)
        => await UploadAsync(mod, allowPublic, dryRunTitleRewrite, publicationReceiptPath: null, ct);

    public async Task<RunOutcome> UploadAsync(
        ModInfo mod,
        bool allowPublic,
        bool dryRunTitleRewrite,
        string? publicationReceiptPath,
        CancellationToken ct = default)
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

        // --- Stale-bundle guard ----------------------------------------------------------
        // upload only stages the existing bundleV2/. If source changed since the last build
        // the bundle is stale and we'd ship the OLD content (the documented "uploaded v0.2,
        // game ran v0.1" burn — tools/vmb-launcher/CLAUDE.md § "When to use all instead of
        // upload"). We WARN loudly but proceed, so a deliberate re-upload of a known-current
        // bundle isn't hard-blocked. `all` builds first, so it instead HARD-FAILS via
        // AssertBundleFresh below.
        var fresh = BundleFreshness.Check(mod);
        if (fresh.Stale)
        {
            Console.Error.WriteLine("[upload] WARNING: source newer than bundle — run 'build' first (shipping stale bundle)");
            if (fresh.NewestSourceFile != null)
                Console.Error.WriteLine($"[upload]   newest source: {fresh.NewestSourceFile} ({fresh.NewestSource:u}) > newest bundle ({fresh.NewestBundle:u})");
        }

        // --- Cheap static-lint gate ------------------------------------------------------
        // Promote the qa/*.ps1 lints that the pre-commit hook + CI run into the upload path.
        // The build→deploy iteration loop defers commits (so the hook never fires) and CI is
        // continue-on-error (report-only), so every shipped unescaped-% / invalid-widget-type
        // bug slipped through here. Scope each scan to the single mod dir (ripgrep-fast).
        //   - check_localization.ps1: exit 2 (errors, e.g. unescaped %) BLOCKS; exit 1 warns.
        //   - check_vmf_widget_types.ps1: any non-canonical type is a hard error (exit 2) and
        //     BLOCKS; it has no warning tier.
        var locGate = await QaScriptGate.RunAsync(mod, "check_localization.ps1", L, ct);
        if (locGate.Verdict == QaScriptGate.Verdict.Error)
            return new RunOutcome(false, $"localization check failed (exit {locGate.ExitCode}) — fix before upload (e.g. unescaped % in a *_localization.lua value):\n{locGate.Stdout.TrimEnd()}");
        if (locGate.Verdict == QaScriptGate.Verdict.Warn)
            L($"[upload] localization check: warnings only (exit 1), proceeding");
        else if (locGate.Verdict == QaScriptGate.Verdict.NotRun)
            L($"[upload] localization check skipped: {locGate.Stdout}");

        var widgetGate = await QaScriptGate.RunAsync(mod, "check_vmf_widget_types.ps1", L, ct);
        if (widgetGate.Verdict == QaScriptGate.Verdict.Error)
            return new RunOutcome(false, $"VMF widget-type check failed (exit {widgetGate.ExitCode}) — an invalid widget type breaks the mod's entire options init at load:\n{widgetGate.Stdout.TrimEnd()}");
        if (widgetGate.Verdict == QaScriptGate.Verdict.NotRun)
            L($"[upload] widget-type check skipped: {widgetGate.Stdout}");

        // Auto-sync the cfg title's ` v<MOD_VERSION>` suffix from the mod's lua MOD_VERSION
        // constant. Per PROJECT_STANDARDS §6.3 and memory feedback_version_in_workshop_title,
        // every upload appends/refreshes the trailing version suffix on the cfg title so the
        // Workshop page version matches what's shipping in the bundle. Only the suffix is
        // managed; description and other fields are untouched. Aborts the upload if
        // MOD_VERSION can't be parsed — surface the gap rather than fall back to a date stamp.
        TitleRewriteResult titleResult;
        try
        {
            titleResult = string.IsNullOrWhiteSpace(publicationReceiptPath)
                ? TitleVersionSync.SyncTitle(mod, dryRun: dryRunTitleRewrite)
                : TitleVersionSync.ValidateTitleForPublication(mod);
        }
        catch (Exception ex)
        {
            return new RunOutcome(false, $"Title-version sync failed: {ex.Message}");
        }
        if (titleResult.Changed)
        {
            var verb = dryRunTitleRewrite ? "would rewrite" : "rewrote";
            L($"[upload] {verb} cfg title: '{titleResult.OldTitle}' -> '{titleResult.NewTitle}'");
            if (dryRunTitleRewrite)
            {
                L("[upload] --dry-run-title-rewrite: skipping ugc_tool push.");
                return new RunOutcome(true, $"Dry-run: would rewrite title to '{titleResult.NewTitle}'");
            }
        }
        else if (!string.IsNullOrEmpty(titleResult.NewTitle))
        {
            L($"[upload] cfg title already in sync ('{titleResult.NewTitle}')");
            if (dryRunTitleRewrite)
            {
                L("[upload] --dry-run-title-rewrite: skipping ugc_tool push.");
                return new RunOutcome(true, "Dry-run: title already in sync");
            }
        }

        // --- Cross-process upload serialization + staged-content guard (issue #344) --------------
        // Two concurrent launcher processes ("concurrent ship") share the SINGLE staging dir
        // <SDK>/ugc_uploader/sample_item/. In the 2026-07-05 incident, session B's Stage() overwrote
        // session A's staged content BETWEEN A's stage and A's ugc_tool push, so A's upload pushed
        // B's bundle onto A's Workshop item; A's post-upload cfg write-back then read B's
        // published_id out of the shared staged cfg and stamped it into A's itemV2.cfg (the id-stomp).
        // The v0.5.4 crossed-id guard validates the staged CFG id but NOT the staged CONTENT, so it
        // can't catch a same-target content swap. Two defenses:
        //   (a) a system-wide lock held around the ENTIRE stage -> ugc_tool -> cfg-write-back window
        //       so two launcher processes serialize and B can't touch the staging dir mid-ship;
        //   (b) a staged-content ownership check (inside the lock, right after Stage) verifying the
        //       staged content/ dir carries THIS mod's <name>.mod and no foreign one.
        // The cfg write-back MUST stay inside the lock — it reads the shared staged cfg, the stomp vector.
        //
        // The lock is a NAMED SEMAPHORE, not a Mutex: ugc_tool runs behind `await`, so the release in
        // `finally` can land on a different thread than the acquire, and Mutex.ReleaseMutex() throws
        // when released off the owning thread. A named semaphore (max count 1) is the cross-process
        // equivalent without thread affinity. Trade-off vs a Mutex: no AbandonedMutexException on a
        // crashed holder — but the 5-minute acquire timeout bounds any stuck lock to a clear error,
        // and once all launcher processes exit the kernel object resets.
        Semaphore uploadLock;
        try { uploadLock = new Semaphore(1, 1, UploadLockName); }
        catch (Exception ex) { return new RunOutcome(false, $"[upload] couldn't create the cross-process upload lock: {ex.Message}"); }

        var acquired = false;
        try
        {
            try { acquired = uploadLock.WaitOne(UploadLockTimeout); }
            catch (AbandonedMutexException) { acquired = true; } // semaphores don't raise this; belt-and-suspenders
            if (!acquired)
                return new RunOutcome(false, $"[upload] another VMBLauncher upload is in progress (upload lock held > {UploadLockTimeout.TotalMinutes:0} min). Concurrent uploads share one staging dir and can cross content (issue #344) — wait for the other ship to finish, then retry.");

            // Stage the mod into <sdk>/ugc_uploader/sample_item/ and invoke ugc_tool from the
            // ugc_uploader directory with a relative cfg path — verbatim match for the SDK's own
            // upload.bat ("ugc_tool -c sample_item/item.cfg") and for the maintainer's legacy
            // old-backup/upload.ps1. Custom staging folders + absolute cfg paths (v0.2.6) produce
            // "generic failure (probably empty content directory)" 0x2 on at least one user's setup.
            StagedUpload staged;
            try { staged = UploadStager.Stage(mod, _settings.UgcToolPath!); }
            catch (Exception ex) { return new RunOutcome(false, $"Staging failed: {ex.Message}"); }
            L($"[upload] staged {staged.FilesCopied} file(s) into {staged.StagingDir}");

            // Guard (b): the staged content/ dir MUST carry exactly this mod's <name>.mod. A foreign
            // .mod (or none) means another process's Stage() clobbered ours mid-ship — refuse before
            // ugc_tool pushes the wrong bundle onto this mod's item.
            var stagedContentDir = Path.Combine(staged.StagingDir, "content");
            var (stagedVerdict, stagedForeign) = InspectStagedContent(stagedContentDir, mod.Name);
            if (stagedVerdict != StagedContentOwnership.OwnedByMod)
            {
                var found = stagedVerdict == StagedContentOwnership.ForeignOwner
                    ? $"'{stagedForeign}.mod'"
                    : "no .mod entry";
                return new RunOutcome(false, $"[stage-guard] REFUSING upload: staged content contains {found}, expected '{mod.Name}.mod' - concurrent-ship staging collision (issue #344).");
            }
            L($"[stage-guard] staged content owned by '{mod.Name}.mod' - ok");

            // Issue #344 crossed-published_id guard. The staged cfg is EXACTLY what ugc_tool pushes,
            // so read the published_id from it (not from mod.PublishedId) — in the incident the id was
            // correct at ship-start but stomped mid-ship, so a repo-level preflight could miss it; the
            // guard must read the id at the moment we act. If that id maps to a local Workshop folder
            // owned by a different mod, ugc_tool would push THIS mod's content onto the OTHER mod's
            // item (an irreversible hijack). Refuse before invoking ugc_tool.
            var stagedPublishedId = ModDiscovery.ExtractPublishedId(File.ReadAllText(staged.CfgPath));
            var uploadGuard = CheckWorkshopIdOwnership(stagedPublishedId, mod, "upload");
            if (!uploadGuard.Ok) return uploadGuard;

            // Final publication boundary. The receipt is independently
            // downloaded from GitHub, then both source and exact SDK staging
            // bytes are verified immediately before ugc_tool.
            var publication = PublicationReceiptGate.AuthorizeForUpload(
                publicationReceiptPath,
                staged,
                mod,
                _settings.ProjectRoot,
                _settings.UgcToolPath!,
                DateTime.UtcNow);
            if (!publication.Ok)
                return new RunOutcome(false, $"[publication-gate] REFUSING ugc_tool: {publication.Message}");
            using var verified = publication.Verified;
            if (verified == null ||
                !verified.TryConsume(mod.Name, DateTime.UtcNow))
                return new RunOutcome(false, "[publication-gate] REFUSING ugc_tool: verified in-process receipt is absent, expired, or already consumed.");
            L($"[publication-gate] OK - {publication.Message}");

            try
            {
                // Existing-item uploads keep item.cfg immutable through process
                // exit. A first upload opens only the cfg write boundary because
                // ugc_tool must replace published_id=0 with Steam's assigned ID;
                // content, preview, directories, and the executable stay pinned.
                verified.PrepareForUploadProcess();
            }
            catch (Exception ex)
            {
                return new RunOutcome(
                    false,
                    $"[publication-gate] REFUSING ugc_tool: could not open the constrained bootstrap boundary ({ex.Message}).");
            }

            var toolFwd = verified.ToolPath.Replace('\\', '/');
            var uploaderDir = Path.GetDirectoryName(verified.ToolPath)!.Replace('\\', '/');
            var relativeCfgArg = $"{UploadStager.StagingFolderName}/{UploadStager.StagedCfgFileName}";
            var result = await ProcessRunner.RunWithEulaYesAsync(toolFwd, new[] { "-c", relativeCfgArg, "-x" }, uploaderDir, L, ct);
            if (result.ExitCode != 0)
                return new RunOutcome(false, $"ugc_tool exited with code {result.ExitCode}");

            var writeBack = verified.CompleteBootstrapWriteBack(mod);
            if (!writeBack.Ok)
            {
                var recovery = string.IsNullOrWhiteSpace(writeBack.PublishedId)
                    ? ""
                    : $" Steam may have allocated Workshop ID {writeBack.PublishedId}; preserve it for explicit recovery.";
                return new RunOutcome(
                    false,
                    $"[bootstrap-writeback] {writeBack.Message}{recovery}");
            }
            if (verified.IsBootstrap)
                L($"[bootstrap-writeback] {writeBack.Message}");
        }
        finally
        {
            if (acquired)
            {
                try { uploadLock.Release(); } catch { /* releasing a semaphore we own can't legitimately fail; swallow so a stray throw can't mask the real outcome */ }
            }
            uploadLock.Dispose();
        }

        L("[upload] ugc_tool reported finished. VERIFY the Workshop page shows updated file size -- ugc_tool prints success even on transfer failures.");
        return new RunOutcome(true, "Upload finished (verify size on Workshop page)");
    }

    /// <summary>
    /// Defensive post-build freshness assertion used by the `all` pipeline. `all` always
    /// builds first, so a stale bundle here means the build claimed success but didn't write
    /// fresh bundles — a hard failure, not a warning. (The `upload` verb, which doesn't build,
    /// only warns; see UploadAsync.)
    /// </summary>
    public RunOutcome AssertBundleFresh(ModInfo mod)
    {
        var fresh = BundleFreshness.Check(mod);
        if (fresh.Stale)
            return new RunOutcome(false,
                $"bundle still stale after build (newest source {fresh.NewestSourceFile} {fresh.NewestSource:u} > newest bundle {fresh.NewestBundle:u}) — build did not write fresh bundles");
        return new RunOutcome(true, "bundle fresh");
    }

    private string? ResolveWorkshopId(ModInfo mod)
    {
        if (_settings.WorkshopIdOverrides.TryGetValue(mod.Name, out var ov) && !string.IsNullOrEmpty(ov))
            return ov;
        return string.IsNullOrEmpty(mod.PublishedId) ? null : mod.PublishedId;
    }

    /// <summary>
    /// Issue #344 crossed-published_id guard, shared by the upload and deploy paths. Verifies the
    /// local Workshop content folder for <paramref name="publishedId"/> is owned by
    /// <paramref name="mod"/> before the caller acts on that id. Returns a FAILING RunOutcome to
    /// abort (a crossed id would hijack another mod's item on upload, or double-install a foreign
    /// mod on deploy), or an OK RunOutcome — with a pass / no-evidence / not-configured log line —
    /// to proceed. <paramref name="action"/> is "upload" or "deploy" and only shapes the refusal
    /// message. The sentinel "0"/empty id (never-uploaded item) and a missing WorkshopContentRoot
    /// setting both degrade gracefully to "proceed"; the publication gate separately rejects
    /// a zero/absent ID until the bootstrap prerequisite has completed.
    /// </summary>
    private RunOutcome CheckWorkshopIdOwnership(string? publishedId, ModInfo mod, string action)
    {
        var id = publishedId?.Trim();
        if (string.IsNullOrEmpty(id) || id == "0")
        {
            L($"[id-guard] no local owner evidence for {(string.IsNullOrEmpty(id) ? "(none)" : id)} - deferring to publication gate");
            return new RunOutcome(true, "id-guard: no published id yet");
        }

        var root = _settings.WorkshopContentRoot;
        if (string.IsNullOrEmpty(root))
        {
            L($"[id-guard] Workshop content root not configured - cannot verify owner for {id}, proceeding");
            return new RunOutcome(true, "id-guard: no workshop root configured");
        }

        var contentDir = Path.Combine(root, id);
        var foreignOwner = FindForeignModOwner(contentDir, mod.Name);
        if (foreignOwner != null)
        {
            var msg = action == "deploy"
                ? $"[id-guard] REFUSING deploy: target folder {id} owned by '{foreignOwner}', not '{mod.Name}'. A crossed id writes this mod's files into another item's Workshop folder, double-installing it (issue #344)."
                : $"[id-guard] REFUSING upload: cfg published_id {id} maps to Workshop folder owned by '{foreignOwner}', not '{mod.Name}'. A crossed id hijacks another mod's item (issue #344).";
            return new RunOutcome(false, msg);
        }

        // Not foreign: either the folder carries our own <name>.mod (owned - ok) or it has no .mod
        // evidence yet (for example a freshly-subscribed empty folder).
        var hasOwnMarker = Directory.Exists(contentDir)
            && Directory.EnumerateFiles(contentDir)
                .Any(f => string.Equals(Path.GetExtension(f), ".mod", StringComparison.OrdinalIgnoreCase));
        if (hasOwnMarker)
            L($"[id-guard] {id} owned by '{mod.Name}'.mod - ok");
        else
            L($"[id-guard] no local owner evidence for {id} - deferring to publication gate");
        return new RunOutcome(true, "id-guard: ok");
    }

    /// <summary>
    /// Issue #344 self-consistency primitive. A Workshop content folder
    /// <c>…\content\552500\&lt;id&gt;\</c> that already exists on disk carries the owning mod's
    /// <c>&lt;name&gt;.mod</c> entry file (VMB names it after the mod directory; the bundles beside
    /// it are hash-named <c>*.mod_bundle</c>). Returns the basename of a foreign <c>.mod</c> file
    /// when the folder is owned by some mod OTHER than <paramref name="modName"/>, else null. Null
    /// covers "folder is ours", "folder has no .mod evidence yet", and "folder
    /// doesn't exist". A non-null result means the id has been crossed onto <paramref name="modName"/>.
    /// </summary>
    /// <remarks>
    /// The extension match is EXACT (<c>.mod</c>), NOT the <c>*.mod</c> glob: Windows' three-character
    /// search-pattern rule makes <c>Directory.EnumerateFiles(dir, "*.mod")</c> ALSO return
    /// <c>*.mod_bundle</c> files, whose hash basenames never match a mod name and would produce a
    /// false foreign-owner verdict.
    /// </remarks>
    public static string? FindForeignModOwner(string contentDir, string modName)
    {
        if (string.IsNullOrEmpty(contentDir) || !Directory.Exists(contentDir)) return null;
        var modFiles = Directory.EnumerateFiles(contentDir)
            .Where(f => string.Equals(Path.GetExtension(f), ".mod", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (modFiles.Count == 0) return null;
        if (modFiles.Any(f => string.Equals(Path.GetFileNameWithoutExtension(f), modName, StringComparison.OrdinalIgnoreCase)))
            return null;
        return Path.GetFileNameWithoutExtension(modFiles[0]);
    }

    /// <summary>Issue #344 staged-content ownership verdict. See <see cref="InspectStagedContent"/>.</summary>
    public enum StagedContentOwnership
    {
        /// <summary>The staged content/ dir carries this mod's own <c>&lt;name&gt;.mod</c> (safe to upload).</summary>
        OwnedByMod,
        /// <summary>The staged content/ dir carries a DIFFERENT mod's <c>.mod</c> — a staging collision.</summary>
        ForeignOwner,
        /// <summary>The staged content/ dir carries NO <c>.mod</c> entry at all (empty / clobbered mid-copy).</summary>
        NoModEntry,
    }

    /// <summary>
    /// Issue #344 staged-content guard. After <c>UploadStager.Stage</c>, the staged
    /// <c>&lt;SDK&gt;/ugc_uploader/sample_item/content/</c> dir must carry EXACTLY this mod's
    /// <c>&lt;name&gt;.mod</c> entry (VMB names it after the mod dir). A concurrent launcher process
    /// sharing that single staging dir can overwrite it between stage and push, so the content
    /// ugc_tool uploads may belong to a different mod even when the staged cfg id is still ours — the
    /// crossed-id guard can't see a same-target content swap. Returns:
    /// <list type="bullet">
    /// <item><see cref="StagedContentOwnership.OwnedByMod"/> — our <c>&lt;modName&gt;.mod</c> is present and no foreign one is;</item>
    /// <item><see cref="StagedContentOwnership.ForeignOwner"/> — a different mod's <c>.mod</c> is present (out param = its basename);</item>
    /// <item><see cref="StagedContentOwnership.NoModEntry"/> — no <c>.mod</c> entry at all (empty / missing dir).</item>
    /// </list>
    /// Only <see cref="StagedContentOwnership.OwnedByMod"/> is safe to upload.
    /// </summary>
    public static (StagedContentOwnership Verdict, string? ForeignOwner) InspectStagedContent(string stagedContentDir, string modName)
    {
        var foreign = FindForeignModOwner(stagedContentDir, modName);
        if (foreign != null) return (StagedContentOwnership.ForeignOwner, foreign);

        // FindForeignModOwner returns null both when the dir is ours AND when it has no .mod at all.
        // Distinguish: the staged content is only safe when OUR <name>.mod is actually present.
        var hasOwn = !string.IsNullOrEmpty(stagedContentDir)
            && Directory.Exists(stagedContentDir)
            && Directory.EnumerateFiles(stagedContentDir).Any(f =>
                   string.Equals(Path.GetExtension(f), ".mod", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileNameWithoutExtension(f), modName, StringComparison.OrdinalIgnoreCase));
        return hasOwn
            ? (StagedContentOwnership.OwnedByMod, null)
            : (StagedContentOwnership.NoModEntry, null);
    }

    private static string HashFile(string path)
    {
        using var s = File.OpenRead(path);
        using var md5 = MD5.Create();
        return Convert.ToHexString(md5.ComputeHash(s));
    }
}
