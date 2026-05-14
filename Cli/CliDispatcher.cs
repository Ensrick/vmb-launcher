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

        if (!parsed.NoBanner)
        {
            var asmVer = typeof(CliDispatcher).Assembly.GetName().Version?.ToString() ?? "?";
            Console.WriteLine($"vmblauncher {asmVer} (headless)");
        }

        var settings = LoadSettings(parsed.ConfigPath);
        var changed = settings.AutoFillMissing();
        if (changed) settings.Save();

        return parsed.Verb switch
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
    }

    private static Settings LoadSettings(string? overridePath)
    {
        if (!string.IsNullOrEmpty(overridePath) && !System.IO.File.Exists(overridePath))
        {
            Console.Error.WriteLine($"vmblauncher: --config path does not exist yet: {overridePath} (a new file will be created on Save)");
        }
        return Settings.Load(overridePath);
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
  build    <mod-name> [--clean]     VMB build the mod into bundleV2/.
  deploy   <mod-name>               Copy bundleV2/ into Workshop content folder (hash-verified).
  upload   <mod-name> [--allow-public]
                                    Stage and upload to Workshop via ugc_tool.
                                    --allow-public is REQUIRED if itemV2.cfg has visibility="public".
  all      <mod-name> [--clean] [--allow-public]
                                    build + deploy + upload, stopping on first failure.

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
  vmblauncher all general_tweaker
  vmblauncher upload chaos_wastes_tweaker --allow-public
""");
    }
}
