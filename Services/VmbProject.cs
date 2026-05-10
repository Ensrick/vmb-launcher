using System.IO;
using System.Text.Json;

namespace VmbLauncher.Services;

/// <summary>
/// Represents a "project folder" — the directory containing .vmbrc + the mods. For users with the
/// standard layout this is the same as the VMB folder; for users running vmb with --cwd from
/// somewhere else, this is the --cwd target.
/// </summary>
public sealed class VmbProject
{
    public string Root { get; }
    public string VmbRcPath => Path.Combine(Root, ".vmbrc");
    public bool HasVmbRc => File.Exists(VmbRcPath);

    /// <summary>Resolved absolute path to the mods directory.</summary>
    public string ModsDir { get; }

    /// <summary>Raw mods_dir value from .vmbrc, or "mods" if no .vmbrc.</summary>
    public string ModsDirRaw { get; }

    public VmbProject(string root, string modsDirRaw)
    {
        Root = root;
        ModsDirRaw = modsDirRaw;
        ModsDir = ResolveModsDir(root, modsDirRaw);
    }

    public static string ResolveModsDir(string root, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == ".") return root;
        if (Path.IsPathRooted(raw)) return raw;
        return Path.GetFullPath(Path.Combine(root, raw));
    }

    public static VmbProject? Resolve(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        var rcPath = Path.Combine(root, ".vmbrc");
        var modsDirRaw = "mods";
        if (File.Exists(rcPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(rcPath));
                if (doc.RootElement.TryGetProperty("mods_dir", out var md) && md.ValueKind == JsonValueKind.String)
                {
                    var v = md.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) modsDirRaw = v!;
                }
            }
            catch { /* malformed .vmbrc — fall back to default */ }
        }
        return new VmbProject(root, modsDirRaw);
    }

    /// <summary>Search common locations for a directory containing .vmbrc.</summary>
    /// <param name="extraCandidates">Optional override for the candidate-path scan (defaults to common repo locations). Tests pass an empty enumerable to disable disk scan.</param>
    public static VmbProject? AutoDetect(string? vmbRootHint, IEnumerable<string>? extraCandidates = null)
    {
        // 1. VMB root itself (standard layout).
        if (!string.IsNullOrEmpty(vmbRootHint))
        {
            var p = Resolve(vmbRootHint);
            if (p != null && p.HasVmbRc) return p;
        }

        // 2. Common paths with --cwd projects.
        foreach (var guess in extraCandidates ?? EnumerateCandidates())
        {
            try
            {
                if (!Directory.Exists(guess)) continue;
                var rc = Path.Combine(guess, ".vmbrc");
                if (File.Exists(rc))
                {
                    return Resolve(guess);
                }
            }
            catch { }
        }

        // 3. Fall back to VMB root with no .vmbrc (standard mods/ layout assumed).
        if (!string.IsNullOrEmpty(vmbRootHint) && Directory.Exists(vmbRootHint))
            return Resolve(vmbRootHint);
        return null;
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var repoRoots = new[]
        {
            Path.Combine(home, "source", "repos"),
            Path.Combine(home, "Documents", "GitHub"),
            Path.Combine(home, "Projects"),
            Path.Combine(home, "Downloads"),
            @"C:\repos",
            @"D:\repos",
            @"E:\repos",
        };
        foreach (var rr in repoRoots)
        {
            if (!Directory.Exists(rr)) continue;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(rr); }
            catch { continue; }
            foreach (var d in subs) yield return d;
        }
    }
}
