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
        Console.WriteLine($"Workshop ID:  {(string.IsNullOrEmpty(mod.PublishedId) ? "(none — first upload will create one)" : mod.PublishedId)}");
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
        var outcome = runner.DeployAsync(mod, ct: default).GetAwaiter().GetResult();
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

        // Mirror MainWindow.BtnUpload_Click: when visibility=public, the GUI shows a modal
        // confirmation. Headless equivalent: require --allow-public on the command line so
        // the dangerous case can't slip through unattended.
        if (mod.IsPublic && !args.AllowPublic)
        {
            Console.Error.WriteLine($"vmblauncher: {mod.Name} has visibility=\"public\". Re-run with --allow-public to confirm. Public mods that get flagged are removed irreversibly.");
            return CliDispatcher.ExitBadUsage;
        }

        var runner = new ModRunner(settings, Console.WriteLine);
        var outcome = runner.UploadAsync(mod, allowPublic: args.AllowPublic, ct: default).GetAwaiter().GetResult();
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

        if (mod.IsPublic && !args.AllowPublic)
        {
            Console.Error.WriteLine($"vmblauncher: {mod.Name} has visibility=\"public\". Re-run with --allow-public to confirm.");
            return CliDispatcher.ExitBadUsage;
        }

        var runner = new ModRunner(settings, Console.WriteLine);
        var b = runner.BuildAsync(mod, clean: args.Clean, ct: default).GetAwaiter().GetResult();
        if (!b.Ok) return CmdShared.RunOutcome(b, "build");
        var d = runner.DeployAsync(mod, ct: default).GetAwaiter().GetResult();
        if (!d.Ok) return CmdShared.RunOutcome(d, "deploy");
        var u = runner.UploadAsync(mod, allowPublic: args.AllowPublic, ct: default).GetAwaiter().GetResult();
        return CmdShared.RunOutcome(u, "upload");
    }
}
