namespace VmbLauncher.Services;

// Repository coordinates and layout are an explicit allowlist, not caller policy.
// Warlock keeps its two standalone repositories and existing Workshop identities.
internal static class PublicationRepositoryProfile
{
    internal static bool Allows(string repository, string mod) => repository switch
    {
        PublicationReceiptGate.GitHubRepo => mod != "doomrocket",
        "Ensrick/doomrocket-private" or "Ensrick/doomrocket-public" => mod == "doomrocket",
        _ => false,
    };

    internal static string Prefix(string mod) => mod == "doomrocket" ? "" : mod + "/";

    internal static bool MatchesChannel(string repository, string mod, string workshopId,
        string version, string branch) => repository switch
    {
        "Ensrick/doomrocket-private" => mod == "doomrocket" && workshopId == "3794172730"
            && version.EndsWith("-dev", StringComparison.Ordinal) && branch == "private-copy",
        "Ensrick/doomrocket-public" => mod == "doomrocket" && workshopId == "3771657344"
            && version.EndsWith("-alpha", StringComparison.Ordinal) && branch == "main",
        PublicationReceiptGate.GitHubRepo => mod != "doomrocket",
        _ => false,
    };
}
