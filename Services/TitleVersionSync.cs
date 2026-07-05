using System.IO;
using System.Text.RegularExpressions;

namespace VmbLauncher.Services;

/// <summary>
/// Reads MOD_VERSION from a mod's main lua file and rewrites the cfg <c>title</c>
/// field so its trailing version suffix matches. Per PROJECT_STANDARDS §6.3 and
/// memory feedback_version_in_workshop_title: every upload appends/refreshes a
/// trailing <c> v&lt;MOD_VERSION&gt;</c> suffix on the cfg title from the mod's
/// lua MOD_VERSION constant. Only the suffix is auto-managed — the base title,
/// description, visibility, and preview are user-dictated and never auto-changed
/// (memory feedback_workshop_metadata_user_dictates).
///
/// Behavior contracts:
///   - Idempotent: running twice yields the same result.
///   - Throws <see cref="InvalidOperationException"/> when MOD_VERSION cannot be
///     parsed — per PROJECT_STANDARDS §6.1, do NOT fall back to a date stamp.
///     The launcher aborts the upload so the gap surfaces instead of hides.
///   - Only the title field is touched; all other cfg fields (including
///     description, which may also mention the version in its body) are
///     preserved byte-for-byte alongside line ordering and line endings.
/// </summary>
public static class TitleVersionSync
{
    // Mirrors qa/check_versions.ps1 and tools/publish-release/publish-release.ps1's
    // Read-ModVersion regex. The lua source uses `local MOD_VERSION = "X.Y.Z[-tag]"`.
    private static readonly Regex ModVersionRegex = new(
        @"MOD_VERSION\s*=\s*""([^""]+)""",
        RegexOptions.Compiled);

    // Strips an existing " v<version>" suffix off the title. Permissive: matches
    // 1.2 / 1.2.3 / 1.2.3.4 with an optional -tag tail (alpha/beta/dev/rc/etc.).
    // Leading whitespace consumed so the strip is clean.
    private static readonly Regex TitleSuffixRegex = new(
        @"\s+v[\d.]+(?:-\w+)?\s*$",
        RegexOptions.Compiled);

    // Matches the title line in a cfg file. Captures the indent + leading
    // `title = "` prefix (group 1), the title value (group 2), and the
    // trailing `";` (group 3) so we can rewrite just the title value while
    // preserving everything around it.
    private static readonly Regex TitleLineRegex = new(
        @"(?m)^(\s*title\s*=\s*"")((?:[^""\\]|\\.)*)(""\s*;)",
        RegexOptions.Compiled);

    /// <summary>
    /// Read MOD_VERSION from <paramref name="luaPath"/>. Throws
    /// <see cref="InvalidOperationException"/> when the file is missing or the
    /// constant cannot be parsed.
    /// </summary>
    public static string ReadModVersion(string luaPath)
    {
        if (!File.Exists(luaPath))
        {
            throw new InvalidOperationException(
                $"MOD_VERSION lua file not found: {luaPath}. Per PROJECT_STANDARDS §6.1 every mod must define `local MOD_VERSION = \"X.Y.Z\"` near the top of its main lua. Cannot proceed with title-version sync.");
        }

        var text = File.ReadAllText(luaPath);
        var m = ModVersionRegex.Match(text);
        if (!m.Success)
        {
            throw new InvalidOperationException(
                $"MOD_VERSION constant not found in {luaPath}. Per PROJECT_STANDARDS §6.1 every mod must define `local MOD_VERSION = \"X.Y.Z\"` near the top of its main lua. Cannot proceed with title-version sync.");
        }

        return m.Groups[1].Value;
    }

    /// <summary>
    /// Returns the canonical path to the main lua file for the given mod
    /// (<c>&lt;ModDir&gt;/scripts/mods/&lt;name&gt;/&lt;name&gt;.lua</c>). The
    /// file may or may not exist; the caller should rely on
    /// <see cref="ReadModVersion"/> for the existence check + clear error.
    /// </summary>
    public static string ResolveModLuaPath(ModInfo mod)
        => Path.Combine(mod.ModDir, "scripts", "mods", mod.Name, $"{mod.Name}.lua");

    /// <summary>
    /// Strip an existing <c> v&lt;version&gt;</c> suffix off <paramref name="title"/>
    /// (if any) and append <c> v&lt;modVersion&gt;</c>. Idempotent.
    /// </summary>
    public static string ApplyVersionSuffix(string title, string modVersion)
    {
        if (string.IsNullOrEmpty(title)) return $"v{modVersion}";
        var stripped = TitleSuffixRegex.Replace(title.TrimEnd(), "");
        return $"{stripped} v{modVersion}";
    }

    /// <summary>
    /// Rewrite the cfg's title line so its trailing version suffix matches the
    /// supplied <paramref name="modVersion"/>. Preserves indent, quoting,
    /// surrounding whitespace, line ordering, and the file's existing line
    /// endings (the read-modify-write keeps the original bytes outside the
    /// title value). Returns the rewritten cfg text and the (old, new) title
    /// pair so the caller can log the change.
    /// </summary>
    public static TitleRewriteResult RewriteCfgTitle(string cfgText, string modVersion)
    {
        var m = TitleLineRegex.Match(cfgText);
        if (!m.Success)
        {
            // No title line — nothing to rewrite. Treat as no-op; the cfg
            // probably came from a hand-scaffold and ParseItemCfg would surface
            // it elsewhere. Don't synthesize a title here; that's not in scope.
            return new TitleRewriteResult(cfgText, "", "", Changed: false);
        }

        var rawTitleValue = m.Groups[2].Value;
        var oldTitle = UnescapeCfg(rawTitleValue);
        var newTitle = ApplyVersionSuffix(oldTitle, modVersion);

        if (string.Equals(oldTitle, newTitle, StringComparison.Ordinal))
        {
            // Idempotent: already in sync. No write needed.
            return new TitleRewriteResult(cfgText, oldTitle, newTitle, Changed: false);
        }

        var escapedNew = EscapeCfg(newTitle);
        var rewritten = TitleLineRegex.Replace(cfgText, m2 =>
            m2.Groups[1].Value + escapedNew + m2.Groups[3].Value, 1);

        return new TitleRewriteResult(rewritten, oldTitle, newTitle, Changed: true);
    }

    /// <summary>
    /// End-to-end: read MOD_VERSION from the mod's main lua, rewrite the cfg's
    /// title suffix to match, and (when <paramref name="dryRun"/> is false)
    /// write the cfg back to disk. Returns the result struct so the caller can
    /// log the (old, new) pair. Throws when MOD_VERSION cannot be parsed —
    /// callers must surface the failure rather than silently fall back.
    /// </summary>
    public static TitleRewriteResult SyncTitle(ModInfo mod, bool dryRun = false)
    {
        var luaPath = ResolveModLuaPath(mod);
        var modVersion = ReadModVersion(luaPath);

        var cfgText = File.ReadAllText(mod.ItemCfgPath);
        var result = RewriteCfgTitle(cfgText, modVersion);

        if (result.Changed && !dryRun)
        {
            File.WriteAllText(mod.ItemCfgPath, result.NewCfgText);
            // Refresh the in-memory ModInfo so downstream code (UploadStager,
            // etc.) sees the new title.
            mod.Title = result.NewTitle;
        }

        return result;
    }

    // Match ModDiscovery.Unescape/escape so round-trip parity holds.
    private static string UnescapeCfg(string s) =>
        s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\t", "\t");

    private static string EscapeCfg(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
}

public sealed record TitleRewriteResult(string NewCfgText, string OldTitle, string NewTitle, bool Changed);
