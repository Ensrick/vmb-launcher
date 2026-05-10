using System.IO;
using System.Text.RegularExpressions;

namespace VmbLauncher.Services;

public sealed class ModInfo
{
    public required string Name { get; init; }
    public required string ModDir { get; init; }
    public required string ItemCfgPath { get; init; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Visibility { get; set; } = "private";
    public string Language { get; set; } = "english";
    public string ContentDir { get; set; } = "bundleV2";
    public string PublishedId { get; set; } = "";
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
    public bool HasBuildOutput { get; set; }
    public int BundleCount { get; set; }

    public string BundleV2Dir => Path.Combine(ModDir, "bundleV2");

    public bool IsPublic => string.Equals(Visibility, "public", StringComparison.OrdinalIgnoreCase);
}

public static class ModDiscovery
{
    public static List<ModInfo> ScanMods(Settings settings)
    {
        var project = VmbProject.Resolve(settings.ProjectRoot)
                      ?? (settings.VmbRoot != null ? VmbProject.Resolve(settings.VmbRoot) : null);
        if (project == null) return new List<ModInfo>();
        return ScanModsAt(project.ModsDir);
    }

    public static List<ModInfo> ScanModsAt(string modsDir)
    {
        var mods = new List<ModInfo>();
        if (!Directory.Exists(modsDir)) return mods;

        foreach (var dir in Directory.EnumerateDirectories(modsDir))
        {
            var cfg = Path.Combine(dir, "itemV2.cfg");
            if (!File.Exists(cfg)) continue;

            var name = Path.GetFileName(dir);
            var info = new ModInfo
            {
                Name = name,
                ModDir = dir,
                ItemCfgPath = cfg,
            };
            try { ParseItemCfg(info); } catch { /* leave defaults */ }

            var bundleDir = info.BundleV2Dir;
            if (Directory.Exists(bundleDir))
            {
                var bundles = Directory.EnumerateFiles(bundleDir, "*.mod_bundle").ToArray();
                info.HasBuildOutput = bundles.Length > 0;
                info.BundleCount = bundles.Length;
            }
            mods.Add(info);
        }
        return mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void ParseItemCfg(ModInfo info)
    {
        var raw = File.ReadAllText(info.ItemCfgPath);
        info.Title = ExtractString(raw, "title") ?? info.Name;
        info.Description = ExtractString(raw, "description") ?? "";
        info.Visibility = ExtractString(raw, "visibility") ?? "private";
        info.Language = ExtractString(raw, "language") ?? "english";
        info.ContentDir = ExtractString(raw, "content") ?? "bundleV2";
        info.PublishedId = ExtractPublishedId(raw) ?? "";
    }

    private static readonly Regex StringFieldRegex = new(@"(\w+)\s*=\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
    private static readonly Regex PublishedIdRegex = new(@"published_id\s*=\s*(\d+)L?", RegexOptions.Compiled);

    public static string? ExtractString(string raw, string key)
    {
        foreach (Match m in StringFieldRegex.Matches(raw))
        {
            if (string.Equals(m.Groups[1].Value, key, StringComparison.OrdinalIgnoreCase))
                return Unescape(m.Groups[2].Value);
        }
        return null;
    }

    public static string? ExtractPublishedId(string raw)
    {
        var m = PublishedIdRegex.Match(raw);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string Unescape(string s) =>
        s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\t", "\t");

    /// <summary>Atomically rewrite itemV2.cfg preserving unknown fields.</summary>
    public static void WriteItemCfg(ModInfo info)
    {
        var raw = File.Exists(info.ItemCfgPath) ? File.ReadAllText(info.ItemCfgPath) : DefaultCfgTemplate();
        raw = ReplaceStringField(raw, "title", info.Title);
        raw = ReplaceStringField(raw, "description", info.Description);
        raw = ReplaceStringField(raw, "visibility", info.Visibility);
        raw = ReplaceStringField(raw, "language", info.Language);
        raw = ReplaceStringField(raw, "content", info.ContentDir);
        File.WriteAllText(info.ItemCfgPath, raw);
    }

    private static string ReplaceStringField(string raw, string key, string newValue)
    {
        var escaped = newValue.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        var pattern = new Regex(@"(?m)^(\s*" + Regex.Escape(key) + @"\s*=\s*)""(?:[^""\\]|\\.)*"";");
        if (pattern.IsMatch(raw))
            return pattern.Replace(raw, "$1\"" + escaped + "\";", 1);
        return raw.TrimEnd() + Environment.NewLine + key + " = \"" + escaped + "\";" + Environment.NewLine;
    }

    private static string DefaultCfgTemplate() =>
        "title = \"\";\ndescription = \"\";\npreview = \"preview.jpg\";\ncontent = \"bundleV2\";\nlanguage = \"english\";\nvisibility = \"private\";\napply_for_sanctioned_status = false;\ntags = [ ];\n";
}
