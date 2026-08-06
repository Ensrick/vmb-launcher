using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace VmbLauncher.Services;

public sealed record StagedUpload(
    string StagingDir,
    string CfgPath,
    string PreviewName,
    int FilesCopied);

internal sealed record BootstrapWriteBackResult(
    bool Ok,
    string Message,
    string? PublishedId = null);

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
    private static readonly Regex PublishedIdDirectiveRegex = new(
        @"(?im)^[ \t]*published_id[ \t]*=[ \t]*(\d+)L?[ \t]*;",
        RegexOptions.Compiled);

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
        MachineTransactionLease.RequireCurrent("Upload staging");
        var uploaderDir = Path.GetDirectoryName(ugcToolPath)
            ?? throw new InvalidOperationException("ugc_tool.exe path has no parent directory.");
        var stagingDir = GetStagingDir(uploaderDir);
        var contentDir = Path.Combine(stagingDir, "content");

        // Wipe and recreate the staging folder. This is fail-closed: continuing
        // after a recursive-delete failure mixes old and new publication bytes.
        if (Directory.Exists(stagingDir))
        {
            try { Directory.Delete(stagingDir, recursive: true); }
            catch (Exception ex)
            {
                throw new IOException(
                    $"Could not clear shared SDK staging directory '{stagingDir}'. Refusing to mix stale and current upload bytes.", ex);
            }
            if (Directory.Exists(stagingDir))
                throw new IOException(
                    $"Shared SDK staging directory still exists after recursive delete: '{stagingDir}'.");
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

        // Copy preview if present. Primary path: honour the cfg's `preview = "<name>";` field
        // verbatim when set AND the named file exists on disk — that lets users point at custom
        // preview filenames (e.g. a unified thumbnail across multiple mods) without having to
        // rename to `item_preview.png`. Fallback path (legacy/cfg-empty): iterate the historical
        // candidate list. Default to item_preview.png if nothing exists (ugc_tool will accept the
        // absence for an item update where the preview was already set previously).
        var stagedPreviewName = ResolvePreviewName(mod);
        var srcPreview = Path.Combine(mod.ModDir, stagedPreviewName);
        if (File.Exists(srcPreview))
            File.Copy(srcPreview, Path.Combine(stagingDir, stagedPreviewName), overwrite: true);

        // Write the staged cfg with relative paths. Filename matches the SDK's convention.
        var stagedCfgPath = Path.Combine(stagingDir, StagedCfgFileName);
        WriteStagedCfg(stagedCfgPath, mod, stagedPreviewName);

        return new StagedUpload(stagingDir, stagedCfgPath, stagedPreviewName, copied);
    }

    public static string ResolvePreviewName(ModInfo mod)
    {
        return ResolvePreviewNameFromSourceCfg(
            File.ReadAllText(mod.ItemCfgPath),
            name => File.Exists(Path.Combine(mod.ModDir, name)));
    }

    internal static string ResolvePreviewNameFromSourceCfg(
        string sourceCfg,
        Func<string, bool> exists)
    {
        var configured = ModDiscovery.ExtractString(sourceCfg, "preview") ?? "";
        if (IsSafePreviewLeaf(configured) && exists(configured)) return configured;
        foreach (var candidate in new[] { "item_preview.png", "preview.jpg", "preview.png" })
        {
            if (exists(candidate)) return candidate;
        }
        return "item_preview.png";
    }

    private static bool IsSafePreviewLeaf(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            Path.IsPathRooted(name) ||
            !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) ||
            name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(name, name.TrimEnd(' ', '.'), StringComparison.Ordinal))
            return false;
        var stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        return stem is not "CON" and not "PRN" and not "AUX" and not "NUL" &&
            !System.Text.RegularExpressions.Regex.IsMatch(stem, "^(COM|LPT)[1-9]$");
    }

    public static string BuildStagedCfgText(ModInfo mod, string previewName) =>
        BuildStagedCfgText(
            mod.Title,
            mod.Description,
            mod.Language,
            mod.Visibility,
            mod.PublishedId,
            previewName);

    internal static string BuildStagedCfgTextFromSourceCfg(
        string sourceCfg,
        string modName,
        string previewName) =>
        BuildStagedCfgText(
            ModDiscovery.ExtractString(sourceCfg, "title") ?? modName,
            ModDiscovery.ExtractString(sourceCfg, "description") ?? "",
            ModDiscovery.ExtractString(sourceCfg, "language") ?? "english",
            ModDiscovery.ExtractString(sourceCfg, "visibility") ?? "private",
            ModDiscovery.ExtractPublishedId(sourceCfg) ?? "",
            previewName);

    private static string BuildStagedCfgText(
        string title,
        string description,
        string language,
        string visibility,
        string publishedId,
        string previewName)
    {
        // Per maintainer's old-backup/ANTIGRAVITY.md:129 — "The tool adds tags = [ ]; automatically
        // after a successful upload — do NOT add it manually." Adding it pre-emptively breaks the
        // upload's content-transfer step (causes the 0x2 "empty content directory" error on first
        // uploads).
        //
        // Per ANTIGRAVITY.md:114 — "For a new item, set published_id = 0L; — the tool will populate
        // it after creation." Omitting the line entirely is NOT the same as 0L.
        var sb = new StringBuilder();
        sb.AppendLine($"title = \"{EscapeForCfg(title)}\";");
        sb.AppendLine($"description = \"{EscapeForCfg(description)}\";");
        sb.AppendLine($"preview = \"{previewName}\";");
        sb.AppendLine("content = \"content\";");
        sb.AppendLine($"language = \"{language}\";");
        sb.AppendLine($"visibility = \"{visibility}\";");
        var idForCfg = string.IsNullOrEmpty(publishedId) ? "0" : publishedId;
        sb.AppendLine($"published_id = {idForCfg}L;");
        sb.AppendLine("apply_for_sanctioned_status = false;");
        return sb.ToString();
    }

    private static void WriteStagedCfg(string path, ModInfo mod, string previewName) =>
        File.WriteAllText(path, BuildStagedCfgText(mod, previewName));

    /// <summary>
    /// Complete the one-time first-upload handshake without reviving the old
    /// shared-staging id-stomp. ugc_tool may change only published_id=0 to one
    /// nonzero decimal ID (and may append its documented empty tags line).
    /// Source write-back is compare-and-swap against the exact authorized Git
    /// blob and occurs while holding an exclusive handle to itemV2.cfg.
    /// </summary>
    internal static BootstrapWriteBackResult CompleteBootstrapWriteBack(
        StagedUpload staged,
        ModInfo mod,
        string expectedStagedCfgText,
        string expectedSourceCfgSha256)
    {
        try
        {
            var cfgPath = Path.GetFullPath(mod.ItemCfgPath);
            var modRoot = Path.GetFullPath(mod.ModDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!cfgPath.StartsWith(
                    modRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    Path.GetFileName(cfgPath), "itemV2.cfg",
                    StringComparison.OrdinalIgnoreCase))
                return new(false, "Bootstrap write-back target escapes the selected mod.");
            if ((File.GetAttributes(cfgPath) & FileAttributes.ReparsePoint) != 0)
                return new(false, "Bootstrap write-back target is a reparse point.");

            string stagedText;
            using (var stagedStream = new FileStream(
                staged.CfgPath, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var reader = new StreamReader(
                stagedStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false))
                stagedText = reader.ReadToEnd();

            var publishedId = ModDiscovery.ExtractPublishedId(stagedText);
            if (string.IsNullOrWhiteSpace(publishedId) ||
                publishedId == "0" ||
                !Regex.IsMatch(publishedId, "^[1-9][0-9]*$"))
                return new(false, "ugc_tool did not return one canonical nonzero published_id.");

            var expectedWithId = UpsertPublishedId(expectedStagedCfgText, publishedId);
            const string emptyTagsPattern =
                @"(?m)^[ \t]*tags[ \t]*=[ \t]*\[[ \t]*\][ \t]*;[ \t]*(?:\r?\n)?";
            if (Regex.Matches(stagedText, emptyTagsPattern).Count > 1)
                return new(
                    false,
                    "ugc_tool produced more than one empty tags directive.",
                    publishedId);
            var stagedWithoutToolTags = Regex.Replace(
                stagedText, emptyTagsPattern, "");
            if (!string.Equals(
                    NormalizeCfgText(stagedWithoutToolTags),
                    NormalizeCfgText(expectedWithId),
                    StringComparison.Ordinal))
                return new(
                    false,
                    "ugc_tool changed staged item.cfg beyond the permitted first-upload ID assignment.",
                    publishedId);

            using var source = new FileStream(
                cfgPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            RequireExactOpenedPath(source, cfgPath);
            var original = ReadAllBytes(source);
            if (!string.Equals(
                    HashBytes(original), expectedSourceCfgSha256,
                    StringComparison.OrdinalIgnoreCase))
                return new(
                    false,
                    "Source itemV2.cfg no longer matches the authorized Git blob; refusing to overwrite it.",
                    publishedId);

            var originalText = Encoding.UTF8.GetString(original);
            var sourceIdDirectives = PublishedIdDirectiveRegex.Matches(originalText);
            if (sourceIdDirectives.Count != 1 ||
                sourceIdDirectives[0].Groups[1].Value != "0")
                return new(
                    false,
                    "Authorized source itemV2.cfg must contain exactly one first-upload published_id=0 sentinel.",
                    publishedId);

            var collision = FindBootstrapPublishedIdCollision(mod, publishedId);
            if (collision != null)
                return new(
                    false,
                    $"Steam returned published_id {publishedId}, but '{collision}' already owns that ID; refusing source write-back.",
                    publishedId);

            var replacement = Encoding.UTF8.GetBytes(
                UpsertPublishedId(originalText, publishedId));
            try
            {
                source.Position = 0;
                source.SetLength(0);
                source.Write(replacement);
                source.Flush(flushToDisk: true);
                source.Position = 0;
                var readBack = ReadAllBytes(source);
                if (!replacement.SequenceEqual(readBack))
                    throw new IOException("itemV2.cfg read-back did not match the intended write.");
            }
            catch
            {
                source.Position = 0;
                source.SetLength(0);
                source.Write(original);
                source.Flush(flushToDisk: true);
                throw;
            }

            return new(
                true,
                $"Workshop assigned published_id {publishedId}; wrote only that field back to itemV2.cfg.",
                publishedId);
        }
        catch (Exception ex)
        {
            return new(false, $"Bootstrap published_id write-back failed: {ex.Message}");
        }
    }

    internal static string UpsertPublishedId(string raw, string newId)
    {
        if (!Regex.IsMatch(newId, "^[0-9]+$"))
            throw new InvalidDataException("published_id must contain decimal digits only.");
        var line = $"published_id = {newId}L;";
        var pattern = new Regex(
            @"(?m)^(?<prefix>\s*published_id\s*=\s*)\d+(?<suffix>L?\s*;.*)$");
        if (pattern.IsMatch(raw))
            return pattern.Replace(
                raw,
                match => match.Groups["prefix"].Value +
                    newId +
                    match.Groups["suffix"].Value,
                1);

        var visibility = new Regex(@"(?m)^(\s*visibility\s*=)");
        if (visibility.IsMatch(raw))
            return visibility.Replace(raw, line + Environment.NewLine + "$1", 1);
        return raw.TrimEnd() + Environment.NewLine + line + Environment.NewLine;
    }

    private static string? FindBootstrapPublishedIdCollision(ModInfo mod, string publishedId)
    {
        var modsRoot = Directory.GetParent(Path.GetFullPath(mod.ModDir))?.FullName
            ?? throw new InvalidDataException("Selected mod has no repository parent directory.");
        foreach (var directory in Directory.EnumerateDirectories(modsRoot))
        {
            if (string.Equals(
                    Path.GetFullPath(directory).TrimEnd('\\', '/'),
                    Path.GetFullPath(mod.ModDir).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase))
                continue;
            var cfg = Path.Combine(directory, "itemV2.cfg");
            if (!File.Exists(cfg)) continue;
            var raw = File.ReadAllText(cfg, Encoding.UTF8);
            if (PublishedIdDirectiveRegex.Matches(raw)
                .Cast<Match>()
                .Any(match => string.Equals(
                    match.Groups[1].Value,
                    publishedId,
                    StringComparison.Ordinal)))
                return Path.GetFileName(directory);
        }
        return null;
    }

    private static byte[] ReadAllBytes(FileStream stream)
    {
        stream.Position = 0;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static string HashBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string NormalizeCfgText(string value) =>
        value.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd() + "\n";

    private static void RequireExactOpenedPath(FileStream stream, string expectedPath)
    {
        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(
                stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
                throw new IOException(
                    $"Cannot resolve bootstrap write-back handle (Win32 {Marshal.GetLastWin32Error()}).");
            if (length >= buffer.Capacity)
            {
                capacity = checked((int)length + 1);
                continue;
            }
            var actual = NormalizeFinalHandlePath(buffer.ToString());
            var expected = Path.GetFullPath(expectedPath);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException(
                    "Bootstrap write-back handle resolves outside the selected itemV2.cfg path.");
            return;
        }
    }

    private static string NormalizeFinalHandlePath(string value)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string longPrefix = @"\\?\";
        if (value.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            value = @"\\" + value[uncPrefix.Length..];
        else if (value.StartsWith(longPrefix, StringComparison.OrdinalIgnoreCase))
            value = value[longPrefix.Length..];
        return Path.GetFullPath(value);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        Microsoft.Win32.SafeHandles.SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    private static string EscapeForCfg(string s) =>
        (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
}
