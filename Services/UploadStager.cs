using System.IO;
using System.Text;

namespace VmbLauncher.Services;

public sealed record StagedUpload(string StagingDir, string CfgPath, int FilesCopied);

/// <summary>
/// Stages a mod for upload by copying its bundles, preview, and a derived cfg into a dedicated
/// folder under the SDK's ugc_uploader directory. Matches the pattern documented in
/// vermintide-2-tweaker/DEVELOPMENT.md as the fix for
/// "generic failure (probably empty content directory)" 0x2.
///
/// Why this is needed despite VMB's design:
/// ugc_tool's resolution of relative paths in itemV2.cfg is buggy when the cfg lives elsewhere on
/// disk. Even with forward slashes, even with cwd set to the mod folder, brand-new uploads fail
/// with "empty content directory". Staging into the SDK's own uploader subtree with relative paths
/// matches what the SDK's bundled upload.bat does (<c>ugc_tool -c sample_item/item.cfg</c>) and is
/// what the maintainer's existing institutional knowledge says works reliably.
/// </summary>
public static class UploadStager
{
    // The SDK's own sample_item folder. Empirically what ugc_tool wants — likely because the
    // tool has the name hardcoded in its content-resolution path, or because it only resolves
    // relative cfg paths and "sample_item/item.cfg" is what its own upload.bat ships. Custom
    // staging folder names (we tried "vmblauncher_staging" in v0.2.6) failed on at least one
    // user's setup with "generic failure (probably empty content directory)" 0x2 despite the
    // staging directory being a sibling of sample_item. The maintainer's pre-VMB-migration
    // upload.ps1 used this folder; converging on the same.
    public const string StagingFolderName = "sample_item";

    // ugc_tool's bundled upload.bat uses "item.cfg" (not "itemV2.cfg"). Match it.
    public const string StagedCfgFileName = "item.cfg";

    public static string GetStagingDir(string ugcUploaderDir)
        => Path.Combine(ugcUploaderDir, StagingFolderName);

    /// <summary>Stage <paramref name="mod"/> for upload. Returns the staged cfg path.</summary>
    public static StagedUpload Stage(ModInfo mod, string ugcToolPath)
    {
        var uploaderDir = Path.GetDirectoryName(ugcToolPath)
            ?? throw new InvalidOperationException("ugc_tool.exe path has no parent directory.");
        var stagingDir = GetStagingDir(uploaderDir);
        var contentDir = Path.Combine(stagingDir, "content");

        // Wipe and recreate the staging folder. Best-effort cleanup; if we can't delete (e.g.
        // somebody's holding a handle), recreating may still work as long as we can overwrite.
        if (Directory.Exists(stagingDir))
        {
            try { Directory.Delete(stagingDir, recursive: true); } catch { /* see below */ }
        }
        Directory.CreateDirectory(contentDir);

        // Copy bundles.
        var copied = 0;
        foreach (var src in Directory.EnumerateFiles(mod.BundleV2Dir))
        {
            var dst = Path.Combine(contentDir, Path.GetFileName(src));
            File.Copy(src, dst, overwrite: true);
            copied++;
        }
        if (copied == 0)
            throw new InvalidOperationException($"No files in {mod.BundleV2Dir} to stage. Run Build first.");

        // Copy preview if present. Default to item_preview.png; fall back to preview.jpg for older
        // mods, otherwise leave it absent (ugc_tool will accept that for an item update where the
        // preview was already set previously).
        var stagedPreviewName = "item_preview.png";
        foreach (var candidate in new[] { "item_preview.png", "preview.jpg", "preview.png" })
        {
            var srcPreview = Path.Combine(mod.ModDir, candidate);
            if (File.Exists(srcPreview))
            {
                stagedPreviewName = candidate;
                File.Copy(srcPreview, Path.Combine(stagingDir, candidate), overwrite: true);
                break;
            }
        }

        // Write the staged cfg with relative paths. Filename matches the SDK's convention.
        var stagedCfgPath = Path.Combine(stagingDir, StagedCfgFileName);
        WriteStagedCfg(stagedCfgPath, mod, stagedPreviewName);

        return new StagedUpload(stagingDir, stagedCfgPath, copied);
    }

    /// <summary>After a successful upload, propagate any newly-written published_id from the staged cfg back to the mod's real cfg. Skips the sentinel "0" value (we write `published_id = 0L;` before first upload; ugc_tool replaces it with the real ID on success).</summary>
    public static bool PropagatePublishedIdBack(StagedUpload staged, ModInfo mod)
    {
        if (!File.Exists(staged.CfgPath)) return false;
        var stagedRaw = File.ReadAllText(staged.CfgPath);
        var stagedId = ModDiscovery.ExtractPublishedId(stagedRaw);
        if (string.IsNullOrEmpty(stagedId)) return false;
        // 0 is our sentinel for "ugc_tool hasn't written a real id yet". Don't propagate it.
        if (stagedId == "0") return false;

        // Update mod's cfg if it doesn't already have the same id.
        var modRaw = File.ReadAllText(mod.ItemCfgPath);
        var existingId = ModDiscovery.ExtractPublishedId(modRaw);
        if (existingId == stagedId) return false;

        var newCfg = UpsertPublishedId(modRaw, stagedId);
        File.WriteAllText(mod.ItemCfgPath, newCfg);
        return true;
    }

    public static string UpsertPublishedId(string raw, string newId)
    {
        var line = $"published_id = {newId}L;";
        var pattern = new System.Text.RegularExpressions.Regex(@"(?m)^\s*published_id\s*=\s*\d+L?\s*;.*$");
        if (pattern.IsMatch(raw))
            return pattern.Replace(raw, line, 1);

        // Insert before visibility line if found, otherwise append.
        var visPattern = new System.Text.RegularExpressions.Regex(@"(?m)^(\s*visibility\s*=)");
        if (visPattern.IsMatch(raw))
            return visPattern.Replace(raw, line + Environment.NewLine + "$1", 1);

        return raw.TrimEnd() + Environment.NewLine + line + Environment.NewLine;
    }

    private static void WriteStagedCfg(string path, ModInfo mod, string previewName)
    {
        // Per maintainer's old-backup/ANTIGRAVITY.md:129 — "The tool adds tags = [ ]; automatically
        // after a successful upload — do NOT add it manually." Adding it pre-emptively breaks the
        // upload's content-transfer step (causes the 0x2 "empty content directory" error on first
        // uploads).
        //
        // Per ANTIGRAVITY.md:114 — "For a new item, set published_id = 0L; — the tool will populate
        // it after creation." Omitting the line entirely is NOT the same as 0L.
        var sb = new StringBuilder();
        sb.AppendLine($"title = \"{EscapeForCfg(mod.Title)}\";");
        sb.AppendLine($"description = \"{EscapeForCfg(mod.Description)}\";");
        sb.AppendLine($"preview = \"{previewName}\";");
        sb.AppendLine("content = \"content\";");
        sb.AppendLine($"language = \"{mod.Language}\";");
        sb.AppendLine($"visibility = \"{mod.Visibility}\";");
        var idForCfg = string.IsNullOrEmpty(mod.PublishedId) ? "0" : mod.PublishedId;
        sb.AppendLine($"published_id = {idForCfg}L;");
        sb.AppendLine("apply_for_sanctioned_status = false;");
        File.WriteAllText(path, sb.ToString());
    }

    private static string EscapeForCfg(string s) =>
        (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
}
