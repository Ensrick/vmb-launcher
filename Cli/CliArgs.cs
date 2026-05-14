namespace VmbLauncher.Cli;

/// <summary>
/// Parsed argv container. The parser is deliberately simple — we know our own flag set
/// so there's no benefit to pulling in a full command-line library.
///
/// Rules:
///   - First non-flag token is the verb.
///   - Second non-flag token is the mod name (if the verb takes one).
///   - Anything starting with -- is a flag.
///   - Unknown flags are recorded in Unknown so commands can warn about typos.
/// </summary>
public sealed class CliArgs
{
    public string? Verb { get; init; }
    public string? ModName { get; init; }
    public bool Clean { get; init; }
    public bool AllowPublic { get; init; }
    public bool NoBanner { get; init; }
    public bool Help { get; init; }
    public string? ConfigPath { get; init; }
    public List<string> Unknown { get; init; } = new();

    public static CliArgs Parse(string[] args)
    {
        string? verb = null;
        string? modName = null;
        bool clean = false;
        bool allowPublic = false;
        bool noBanner = false;
        bool help = false;
        string? configPath = null;
        var unknown = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--help" || a == "-h" || a == "/?")
            {
                help = true;
            }
            else if (a == "--clean")
            {
                clean = true;
            }
            else if (a == "--allow-public")
            {
                allowPublic = true;
            }
            else if (a == "--no-banner")
            {
                noBanner = true;
            }
            else if (a == "--config")
            {
                if (i + 1 < args.Length) configPath = args[++i];
                else unknown.Add("--config (missing value)");
            }
            else if (a.StartsWith("--"))
            {
                unknown.Add(a);
            }
            else if (verb == null)
            {
                verb = a.ToLowerInvariant();
            }
            else if (modName == null)
            {
                modName = a;
            }
            else
            {
                unknown.Add(a);
            }
        }

        return new CliArgs
        {
            Verb = verb,
            ModName = modName,
            Clean = clean,
            AllowPublic = allowPublic,
            NoBanner = noBanner,
            Help = help,
            ConfigPath = configPath,
            Unknown = unknown,
        };
    }
}
