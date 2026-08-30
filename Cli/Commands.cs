using VmbLauncher.Services;

namespace VmbLauncher.Cli;

// --- Shared helpers -------------------------------------------------------------------------

internal static class CmdShared
{
    /// <summary>
    /// Resolve &lt;mod-name&gt; argument into a ModInfo, or print an error + return null.
    /// </summary>
    public static ModInfo? ResolveMod(CliArgs args, Settings settings, string verb)
    {
        if (string.IsNullOrEmpty(args.ModName))
        {
            Console.Error.WriteLine($"vmblauncher: '{verb}' requires <mod-name>. Run 'vmblauncher list' to see mods.");
            return null;
        }
        var mods = ModDiscovery.ScanMods(settings);
        var mod = mods.FirstOrDefault(m => string.Equals(m.Name, args.ModName, StringComparison.OrdinalIgnoreCase));
        if (mod == null)
        {
            Console.Error.WriteLine($"vmblauncher: no mod named '{args.ModName}' in {settings.ProjectRoot ?? "(project not configured)"}");
            return null;
        }
        return mod;
    }

    /// <summary>Print the hijack-abort message for a published_id collision.</summary>
    public static void WriteCollisionError(ModInfo mod, ModInfo collision)
    {
        Console.Error.WriteLine(
            $"vmblauncher: ABORT — '{mod.Name}' and '{collision.Name}' BOTH use published_id {mod.PublishedId}. " +
            $"Uploading would HIJACK '{collision.Name}'s Workshop item (overwrite its title + content). " +
            $"Fix the published_id in one of the itemV2.cfg files first — see qa/PUBLISHED_IDS.md.");
    }

    /// <summary>
    /// Same gate the GUI's Preflight() runs before each action — error-level diagnostics
    /// matching the action's required titles block execution.
    /// </summary>
    public static bool Preflight(Settings settings, string actionLabel, params string[] requiredFor)
    {
        var checks = Diagnostics.RunAll(settings);
        var blocking = checks.Where(c => c.Status == CheckStatus.Error && requiredFor.Contains(c.Title)).ToList();
        if (blocking.Count == 0) return true;

        Console.Error.WriteLine($"vmblauncher: cannot {actionLabel.ToLowerInvariant()} yet:");
        foreach (var b in blocking)
        {
            Console.Error.WriteLine($"  ! {b.Title} — {b.Detail}");
        }
        Console.Error.WriteLine("Run 'vmblauncher doctor' for a full report, or launch the GUI to fix interactively.");
        return false;
    }

    public static int RunOutcome(RunOutcome outcome, string label)
    {
        if (outcome.Ok)
        {
            Console.WriteLine($"[{label}] {outcome.Message}");
            return CliDispatcher.ExitOk;
        }
        Console.Error.WriteLine($"[{label}] FAILED: {outcome.Message}");
        return CliDispatcher.ExitFailed;
    }

    /// <summary>
    /// Fail-closed machine-global claim gate. An exact live owner/version match
    /// is necessary but is not publication authorization; ModRunner performs
    /// the independent hosted-receipt gate again immediately before ugc_tool.
    /// </summary>
    public static int? ShipClaimCheck(Settings settings, ModInfo mod)
    {
        string sourceVersion;
        string owner;
        try
        {
            sourceVersion = TitleVersionSync.ReadModVersion(TitleVersionSync.ResolveModLuaPath(mod));
            owner = ShipOwnerId.Resolve(settings.ProjectRoot ?? "");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[claim-gate] REFUSING publication: cannot derive exact source version and owner ({ex.Message}).");
            return CliDispatcher.ExitPreflight;
        }

        var eval = ShipClaimGate.Evaluate(
            ShipClaimGate.DefaultClaimsDir(), mod.Name, sourceVersion, DateTime.UtcNow, owner);
        switch (eval.Verdict)
        {
            case ShipClaimGate.Verdict.Match:
                Console.WriteLine($"[claim-gate] OK - live machine-global claim matches '{mod.Name}' v{sourceVersion}, owner {owner}.");
                return null;

            case ShipClaimGate.Verdict.Mismatch:
                Console.Error.WriteLine($"[claim-gate] REFUSING publication of '{mod.Name}' v{sourceVersion}: {eval.Detail}.");
                return CliDispatcher.ExitPreflight;

            case ShipClaimGate.Verdict.OwnerMismatch:
                Console.Error.WriteLine(
                    $"[claim-gate] REFUSING publication: live claim belongs to '{eval.Claim!.Session}', current owner is '{owner}'.");
                return CliDispatcher.ExitPreflight;

            case ShipClaimGate.Verdict.Stale:
                Console.Error.WriteLine($"[claim-gate] REFUSING publication: claim is stale ({eval.AgeHours:0.00} h >= {ShipClaimGate.StaleHours} h).");
                return CliDispatcher.ExitPreflight;

            case ShipClaimGate.Verdict.Unreadable:
                Console.Error.WriteLine($"[claim-gate] REFUSING publication: claim is unreadable ({eval.Detail}).");
                return CliDispatcher.ExitPreflight;

            default: // NoClaim
                Console.Error.WriteLine($"[claim-gate] REFUSING publication: no machine-global claim exists for '{mod.Name}'.");
                return CliDispatcher.ExitPreflight;
        }
    }

    public static int? PublicationReceiptPresenceCheck(CliArgs args)
    {
        if (args.DryRunTitleRewrite ||
            (args.PublicationReceiptCount == 1 &&
             !string.IsNullOrWhiteSpace(args.PublicationReceiptPath)))
            return null;
        Console.Error.WriteLine(
            "vmblauncher: Workshop publication requires --publication-receipt from tools/ship/ship.ps1. " +
            "A claim alone, direct upload/all, and the GUI are not publication authority.");
        return CliDispatcher.ExitPreflight;
    }
}

// --- capabilities ---------------------------------------------------------------------------

internal static class CapabilitiesCommand
{
    internal const int CapabilitySchema = 1;
    internal const int PublicationReceiptSchema = PublicationReceiptGate.Schema;
    internal const int DeploymentReceiptSchema = PublicationReceiptGate.Schema;
    internal const string LockedUploadSnapshot = "locked-upload-snapshot-v1";
    internal const string HostedReceipt = "hosted-publication-receipt-v3";
    internal const string CommitBlobSnapshot = "git-commit-blob-snapshot-v1";
    internal const string ConstrainedBootstrap = "constrained-first-upload-bootstrap-v1";
    internal const string MachineTransactionLease = "machine-transaction-lease-v1";
    internal const string CrashSafeUploadAclJournal = "crash-safe-upload-acl-journal-v1";
    internal const string ReceiptAuthorityPublication = "receipt-authority-publication-v1";
    internal const string ReceiptAuthorityLocalDeploy = "receipt-authority-local-deploy-v1";

    internal static IReadOnlyList<string> Lines()
    {
        var version = typeof(CapabilitiesCommand).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        return new[]
        {
            $"capability_schema={CapabilitySchema}",
            $"version={version}",
            $"publication_receipt_schema={PublicationReceiptSchema}",
            $"deployment_receipt_schema={DeploymentReceiptSchema}",
            $"capabilities={HostedReceipt},{LockedUploadSnapshot},{CommitBlobSnapshot},{ConstrainedBootstrap},{MachineTransactionLease},{CrashSafeUploadAclJournal},{ReceiptAuthorityPublication},{ReceiptAuthorityLocalDeploy}",
        };
    }

    internal static int Run()
    {
        foreach (var line in Lines()) Console.WriteLine(line);
        return CliDispatcher.ExitOk;
    }
}

// --- list -----------------------------------------------------------------------------------

internal static class ListCommand
{
    public static int Run(CliArgs args, Settings settings)
    {
        if (!CmdShared.Preflight(settings, "List", "VMB", "Project folder")) return CliDispatcher.ExitPreflight;

        var mods = ModDiscovery.ScanMods(settings);
        if (mods.Count == 0)
        {
            Console.WriteLine("(no mods found)");
            return CliDispatcher.ExitOk;
        }

        // Fixed-width columns mirroring the GUI's mod list row.
        var nameW   = Math.Max(20, mods.Max(m => m.Name.Length));
        var visW    = Math.Max(12, mods.Max(m => (m.Visibility ?? "").Length));
        Console.WriteLine($"{"NAME".PadRight(nameW)}  {"VISIBILITY".PadRight(visW)}  WORKSHOP_ID    BUILT");
        Console.WriteLine($"{new string('-', nameW)}  {new string('-', visW)}  {new string('-', 12)}  {new string('-', 10)}");
        foreach (var m in mods)
        {
            var built = m.HasBuildOutput ? $"{m.BundleCount} bundle(s)" : "no build";
            var id = string.IsNullOrEmpty(m.PublishedId) ? "(none)" : m.PublishedId;
            Console.WriteLine($"{m.Name.PadRight(nameW)}  {(m.Visibility ?? "").PadRight(visW)}  {id.PadRight(12)}  {built}");
        }
        return CliDispatcher.ExitOk;
    }
}

// --- info -----------------------------------------------------------------------------------

internal static class InfoCommand
{
    public static int Run(CliArgs args, Settings settings)
    {
        if (!CmdShared.Preflight(settings, "Info", "VMB", "Project folder")) return CliDispatcher.ExitPreflight;

        var mod = CmdShared.ResolveMod(args, settings, "info");
        if (mod == null) return CliDispatcher.ExitBadUsage;

        Console.WriteLine($"Name:         {mod.Name}");
        Console.WriteLine($"Title:        {mod.Title}");
        Console.WriteLine($"Visibility:   {mod.Visibility}");
        Console.WriteLine($"Workshop ID:  {(string.IsNullOrEmpty(mod.PublishedId) || mod.PublishedId == "0" ? "(pending — canonical ship will use constrained first-upload bootstrap)" : mod.PublishedId)}");
        Console.WriteLine($"Mod folder:   {mod.ModDir}");
        Console.WriteLine($"Build state:  {(mod.HasBuildOutput ? $"{mod.BundleCount} bundle(s) in {mod.BundleV2Dir}" : "not built yet")}");
        if (!string.IsNullOrEmpty(mod.Description))
        {
            Console.WriteLine();
            Console.WriteLine("Description (first 200 chars):");
            var d = mod.Description.Length > 200 ? mod.Description[..200] + "…" : mod.Description;
            Console.WriteLine($"  {d.Replace("\n", "\n  ")}");
        }
        return CliDispatcher.ExitOk;
    }
}

// --- doctor ---------------------------------------------------------------------------------

internal static class DoctorCommand
{
    public static int Run(CliArgs args, Settings settings)
    {
        var checks = Diagnostics.RunAll(settings);
        var hasError = false;
        foreach (var c in checks)
        {
            var mark = c.Status switch
            {
                CheckStatus.Ok => " ok ",
                CheckStatus.Warn => "warn",
                CheckStatus.Error => "ERR ",
                _ => " ?  "
            };
            Console.WriteLine($"[{mark}] {c.Title}: {c.Detail}");
            if (c.Status == CheckStatus.Error) hasError = true;
        }
        return hasError ? CliDispatcher.ExitPreflight : CliDispatcher.ExitOk;
    }
}

// --- build ----------------------------------------------------------------------------------

internal static class BuildCommand
{
    public static int Run(CliArgs args, Settings settings)
    {
        if (!CmdShared.Preflight(settings, "Build", "VMB", "Project folder")) return CliDispatcher.ExitPreflight;
        var mod = CmdShared.ResolveMod(args, settings, "build");
        if (mod == null) return CliDispatcher.ExitBadUsage;

        var runner = new ModRunner(settings, Console.WriteLine);
        var outcome = runner.BuildAsync(mod, clean: args.Clean, ct: default).GetAwaiter().GetResult();
        return CmdShared.RunOutcome(outcome, "build");
    }
}

// --- deploy ---------------------------------------------------------------------------------

internal static class DeployCommand
{
    public static int Run(CliArgs args, Settings settings)
    {
        if (!CmdShared.Preflight(settings, "Deploy", "VMB", "Project folder", "Workshop content folder")) return CliDispatcher.ExitPreflight;
        var mod = CmdShared.ResolveMod(args, settings, "deploy");
        if (mod == null) return CliDispatcher.ExitBadUsage;

        var runner = new ModRunner(settings, Console.WriteLine);
        var outcome = runner.DeployAsync(
            mod,
            skipRemote: args.NoRemote,
            deploymentReceiptPath: args.DeploymentReceiptPath,
            ct: default).GetAwaiter().GetResult();
        return CmdShared.RunOutcome(outcome, "deploy");
    }
}

// --- upload ---------------------------------------------------------------------------------

internal static class UploadCommand
{
    public static int Run(CliArgs args, Settings settings)
    {
        if (!CmdShared.Preflight(settings, "Upload", "VMB", "Project folder", "Vermintide 2 SDK", "ugc_tool.exe", "Steam"))
            return CliDispatcher.ExitPreflight;
        var mod = CmdShared.ResolveMod(args, settings, "upload");
        if (mod == null) return CliDispatcher.ExitBadUsage;

        // Hijack guard: refuse to upload if another mod's cfg shares this published_id.
        var collision = ModDiscovery.FindPublishedIdCollision(mod, ModDiscovery.ScanMods(settings));
        if (collision != null) { CmdShared.WriteCollisionError(mod, collision); return CliDispatcher.ExitBadUsage; }

        // Mirror MainWindow.BtnUpload_Click: when visibility=public, the GUI shows a modal
        // confirmation. Headless equivalent: require --allow-public on the command line so
        // the dangerous case can't slip through unattended.
        if (mod.IsPublic && !args.AllowPublic)
        {
            Console.Error.WriteLine($"vmblauncher: {mod.Name} has visibility=\"public\". Re-run with --allow-public to confirm. Public mods that get flagged are removed irreversibly.");
            return CliDispatcher.ExitBadUsage;
        }

        var receiptAbort = CmdShared.PublicationReceiptPresenceCheck(args);
        if (receiptAbort.HasValue) return receiptAbort.Value;
        var claimAbort = CmdShared.ShipClaimCheck(settings, mod);
        if (claimAbort.HasValue) return claimAbort.Value;

        var runner = new ModRunner(settings, Console.WriteLine);
        var outcome = runner.UploadAsync(
            mod,
            allowPublic: args.AllowPublic,
            dryRunTitleRewrite: args.DryRunTitleRewrite,
            publicationReceiptPath: args.PublicationReceiptPath,
            ct: default).GetAwaiter().GetResult();
        return CmdShared.RunOutcome(outcome, "upload");
    }
}

// --- all (build + deploy + upload, short-circuit on failure) --------------------------------

internal static class AllCommand
{
    public static int Run(CliArgs args, Settings settings)
    {
        if (!CmdShared.Preflight(settings, "Full pipeline", "VMB", "Project folder", "Vermintide 2 SDK", "ugc_tool.exe", "Steam", "Workshop content folder"))
            return CliDispatcher.ExitPreflight;
        var mod = CmdShared.ResolveMod(args, settings, "all");
        if (mod == null) return CliDispatcher.ExitBadUsage;

        // Hijack guard: refuse the whole pipeline if another mod's cfg shares this published_id.
        var collision = ModDiscovery.FindPublishedIdCollision(mod, ModDiscovery.ScanMods(settings));
        if (collision != null) { CmdShared.WriteCollisionError(mod, collision); return CliDispatcher.ExitBadUsage; }

        if (mod.IsPublic && !args.AllowPublic)
        {
            Console.Error.WriteLine($"vmblauncher: {mod.Name} has visibility=\"public\". Re-run with --allow-public to confirm.");
            return CliDispatcher.ExitBadUsage;
        }

        // Machine-global ship/version claim gate (issue #724). Checked up front so a mismatched
        // claim fails FAST (before the build), mirroring ship.ps1's gate-before-build ordering;
        // the compared values (claim version vs source MOD_VERSION) can't change during the build.
        var receiptAbort = CmdShared.PublicationReceiptPresenceCheck(args);
        if (receiptAbort.HasValue) return receiptAbort.Value;
        var claimAbort = CmdShared.ShipClaimCheck(settings, mod);
        if (claimAbort.HasValue) return claimAbort.Value;

        var runner = new ModRunner(settings, Console.WriteLine);
        var b = runner.BuildAsync(mod, clean: args.Clean, ct: default).GetAwaiter().GetResult();
        if (!b.Ok) return CmdShared.RunOutcome(b, "build");
        // Defensive: `all` just built, so the bundle must be fresh. A stale bundle here means
        // the build reported success without writing bundles — hard-fail rather than ship it.
        var fresh = runner.AssertBundleFresh(mod);
        if (!fresh.Ok) return CmdShared.RunOutcome(fresh, "build");
        var d = runner.DeployAsync(mod, skipRemote: args.NoRemote, ct: default).GetAwaiter().GetResult();
        if (!d.Ok) return CmdShared.RunOutcome(d, "deploy");
        var u = runner.UploadAsync(
            mod,
            allowPublic: args.AllowPublic,
            dryRunTitleRewrite: args.DryRunTitleRewrite,
            publicationReceiptPath: args.PublicationReceiptPath,
            ct: default).GetAwaiter().GetResult();
        return CmdShared.RunOutcome(u, "upload");
    }
}
