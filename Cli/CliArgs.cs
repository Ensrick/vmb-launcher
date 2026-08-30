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
    public bool NoRemote { get; init; }
    public bool DryRunTitleRewrite { get; init; }
    public bool Help { get; init; }
    public string? ConfigPath { get; init; }
    public int ConfigCount { get; init; }
    public string? PublicationReceiptPath { get; init; }
    public int PublicationReceiptCount { get; init; }
    public string? DeploymentReceiptPath { get; init; }
    public int DeploymentReceiptCount { get; init; }
    public List<string> Unknown { get; init; } = new();

    public static CliArgs Parse(string[] args)
    {
        string? verb = null;
        string? modName = null;
        bool clean = false;
        bool allowPublic = false;
        bool noBanner = false;
        bool noRemote = false;
        bool dryRunTitleRewrite = false;
        bool help = false;
        string? configPath = null;
        int configCount = 0;
        string? publicationReceiptPath = null;
        int publicationReceiptCount = 0;
        string? deploymentReceiptPath = null;
        int deploymentReceiptCount = 0;
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
            else if (a == "--no-remote")
            {
                noRemote = true;
            }
            else if (a == "--dry-run-title-rewrite")
            {
                dryRunTitleRewrite = true;
            }
            else if (a == "--config")
            {
                configCount++;
                if (configCount > 1)
                    unknown.Add("--config (duplicate)");
                if (!TryTakeValue(args, ref i, out var value))
                {
                    unknown.Add("--config (missing value)");
                }
                else if (string.IsNullOrWhiteSpace(value))
                {
                    unknown.Add("--config (empty value)");
                }
                else if (configCount == 1)
                {
                    configPath = value;
                }
            }
            else if (a == "--publication-receipt")
            {
                publicationReceiptCount++;
                if (publicationReceiptCount > 1)
                    unknown.Add("--publication-receipt (duplicate)");
                if (!TryTakeValue(args, ref i, out var value))
                {
                    unknown.Add("--publication-receipt (missing value)");
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(value))
                        unknown.Add("--publication-receipt (empty value)");
                    else if (publicationReceiptCount == 1)
                        publicationReceiptPath = value;
                }
            }
            else if (a == "--deployment-receipt")
            {
                deploymentReceiptCount++;
                if (deploymentReceiptCount > 1)
                    unknown.Add("--deployment-receipt (duplicate)");

                if (!TryTakeValue(args, ref i, out var value))
                {
                    unknown.Add("--deployment-receipt (missing value)");
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(value))
                        unknown.Add("--deployment-receipt (empty value)");
                    else if (deploymentReceiptCount == 1)
                        deploymentReceiptPath = value;
                }
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
            NoRemote = noRemote,
            DryRunTitleRewrite = dryRunTitleRewrite,
            Help = help,
            ConfigPath = configPath,
            ConfigCount = configCount,
            PublicationReceiptPath = publicationReceiptPath,
            PublicationReceiptCount = publicationReceiptCount,
            DeploymentReceiptPath = deploymentReceiptPath,
            DeploymentReceiptCount = deploymentReceiptCount,
            Unknown = unknown,
        };
    }

    /// <summary>
    /// Value-taking flags never consume another flag/help token. In particular,
    /// the short Windows help forms are not prefixed with "--", so testing only
    /// StartsWith("--") would turn a malformed receipt into a successful help
    /// invocation and could hide the remaining authorization flags.
    /// </summary>
    private static bool TryTakeValue(string[] args, ref int index, out string value)
    {
        value = "";
        if (index + 1 >= args.Length || IsFlagOrHelpToken(args[index + 1]))
            return false;
        value = args[++index];
        return true;
    }

    private static bool IsFlagOrHelpToken(string value) =>
        value.StartsWith("-", StringComparison.Ordinal) || value == "/?";
}
