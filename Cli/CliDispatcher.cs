using VmbLauncher.Services;

namespace VmbLauncher.Cli;

/// <summary>
/// Top-level CLI router. Parses argv into a verb + a parsed-arg container and hands off
/// to the matching command handler. Designed to be predictable from PowerShell:
///
///   vmblauncher.exe &lt;verb&gt; [&lt;mod-name&gt;] [flags...]
///
/// Verbs mirror the GUI buttons one-for-one. Output is plain text on stdout (log lines)
/// with errors on stderr; the exit code is what scripts care about.
///
/// All commands honour --config &lt;path&gt; for an alternate settings.json, and --no-banner
/// to suppress the version line (useful when piping output into another tool).
/// </summary>
public static class CliDispatcher
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitBadUsage = 2;
    public const int ExitPreflight = 3;

    public static int Run(string[] args)
    {
        var parsed = CliArgs.Parse(args);
        if (parsed.Help || parsed.Verb == "help")
        {
            PrintHelp();
            return ExitOk;
        }
        if (parsed.Unknown.Count > 0)
        {
            Console.Error.WriteLine($"vmblauncher: unknown or malformed argument(s): {string.Join(", ", parsed.Unknown)}");
            return ExitBadUsage;
        }

        if (!parsed.NoBanner)
        {
            var asmVer = typeof(CliDispatcher).Assembly.GetName().Version?.ToString() ?? "?";
            Console.WriteLine($"vmblauncher {asmVer} (headless)");
        }

        if (parsed.Verb == "capabilities")
            return CapabilitiesCommand.Run();

        if (IsMutationVerb(parsed.Verb))
        {
            // Acquire before reading global/private settings. A wrapper owner
            // may already bind an exact root; this wildcard request is refined
            // and validated immediately after the serialized reload.
            using var transaction = MachineTransactionLease.Enter(
                $"cli-{parsed.Verb}", parsed.ModName, projectRoot: null, Console.WriteLine);
            var settings = LoadSettings(parsed.ConfigPath, forMutation: true);
            var changed = settings.AutoFillMissing();
            var project = settings.ResolveMutationProject()
                ?? throw new InvalidOperationException("Project folder not configured.");
            MachineTransactionLease.RefineOwnedProjectRoot(project.Root);
            using var exactScope = MachineTransactionLease.Enter(
                $"cli-{parsed.Verb}-scope", parsed.ModName, project.Root, Console.WriteLine);
            if (changed) settings.Save();
            return Dispatch(parsed, settings);
        }

        // Read-only verbs may auto-fill their in-memory view, but never persist
        // it and therefore never race a settings or ship transaction.
        var readOnlySettings = LoadSettings(parsed.ConfigPath, forMutation: false);
        readOnlySettings.AutoFillMissing();
        return Dispatch(parsed, readOnlySettings);
    }

    private static int Dispatch(CliArgs parsed, Settings settings) =>
        parsed.Verb switch
        {
            "build"   => BuildCommand.Run(parsed, settings),
            "deploy"  => DeployCommand.Run(parsed, settings),
            "upload"  => UploadCommand.Run(parsed, settings),
            "all"     => AllCommand.Run(parsed, settings),
            "list"    => ListCommand.Run(parsed, settings),
            "info"    => InfoCommand.Run(parsed, settings),
            "doctor"  => DoctorCommand.Run(parsed, settings),
            null or "" => MissingVerb(),
            _         => UnknownVerb(parsed.Verb!),
        };

    private static bool IsMutationVerb(string? verb) =>
        verb is "build" or "deploy" or "upload" or "all";

    private static Settings LoadSettings(string? overridePath, bool forMutation)
    {
        if (!string.IsNullOrEmpty(overridePath) && !System.IO.File.Exists(overridePath))
        {
            Console.Error.WriteLine($"vmblauncher: --config path does not exist yet: {overridePath} (a new file will be created on Save)");
        }
        return forMutation
            ? Settings.LoadForMutation(overridePath)
            : Settings.Load(overridePath);
    }

    private static int MissingVerb()
    {
        Console.Error.WriteLine("vmblauncher: missing verb. Run 'vmblauncher help' for usage.");
        return ExitBadUsage;
    }

    private static int UnknownVerb(string verb)
    {
        Console.Error.WriteLine($"vmblauncher: unknown verb '{verb}'. Run 'vmblauncher help' for usage.");
        return ExitBadUsage;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
vmblauncher.exe — headless companion to the VMB Launcher GUI.

USAGE
  vmblauncher <verb> [<mod-name>] [flags...]
  vmblauncher              # launches the GUI (same as double-clicking)
  vmblauncher --gui        # forces GUI even if args are present

VERBS
  list                              List all discovered mods.
  info     <mod-name>               Print cfg + bundle state for one mod.
  doctor                            Run diagnostics (same checks the GUI's first-run dialog runs).
  capabilities                      Print machine-readable publication capabilities and schemas.
  build    <mod-name> [--clean]     VMB build the mod into bundleV2/.
  deploy   <mod-name> [--no-remote] Copy bundleV2/ into Workshop content folder (hash-verified),
                                    then push to every enabled remote target in
                                    settings.json (default: pc-b via Tailscale, auto-detected).
                                    --no-remote skips the remote push for this invocation only.
  upload   <mod-name> [--allow-public] [--publication-receipt <path>]
                                    Internal final Workshop mutation used by tools/ship/ship.ps1.
                                    Before staging, rewrites itemV2.cfg's `title` suffix to
                                    " v<MOD_VERSION>" from the mod's main lua MOD_VERSION
                                    constant. --allow-public is REQUIRED if visibility="public".
                                    --dry-run-title-rewrite prints the would-be title change
                                    and exits without writing the cfg or pushing to Workshop.
                                    Real publication requires a short-lived GitHub-hosted receipt plus
                                    independent clean/default-head/merged-PR/qa-gate, claim-owner,
                                    version, cfg, and bundle-hash verification.
  all      <mod-name> [--clean] [--allow-public] [--no-remote]
                                    build + deploy + publication, stopping on first failure.
                                    Publication requires the same hosted receipt as upload.
                                    Use tools/ship/ship.ps1; a claim alone never authorizes upload.

GLOBAL FLAGS
  --no-banner       Suppress the version banner (useful for piping).
  --help, -h        Show this help.

EXIT CODES
  0  success
  1  command failed (build/deploy/upload returned not-ok, or runtime error)
  2  bad usage (missing args, unknown verb)
  3  preflight failed (missing settings, diagnostics blocking the requested action)

EXAMPLES
  vmblauncher list
  vmblauncher info general_tweaker
  vmblauncher build general_tweaker
  vmblauncher deploy general_tweaker
  tools\ship\ship.ps1 -Mod general_tweaker
""");
    }
}
