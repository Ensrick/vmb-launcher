using System.IO;
using System.Text;

namespace VmbLauncher.Services;

public sealed record ModScaffoldRequest(string Name, string Title, string Description, string Visibility);

public sealed record ScaffoldResult(bool Ok, string Message, string? ModDir = null);

/// <summary>
/// Scaffolds a new VT2 mod by copying VMB's .template-vmf folder with placeholder substitution.
///
/// Why we don't shell out to `vmb create`:
/// VMB v1.8.4's create.js calls uploader.uploadMod() immediately after scaffolding, but ugc_tool
/// refuses with "empty content directory" because bundleV2/*.mod_bundle doesn't exist yet. VMB
/// then deletes the entire scaffold on the upload failure. Ugly. We replicate the scaffold step
/// (which is just file copy + placeholder substitution) and let the user build + upload manually
/// from the launcher when they're actually ready.
/// </summary>
public static class ModScaffolder
{
    public static ScaffoldResult Scaffold(VmbInstall vmb, VmbProject project, ModScaffoldRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return new ScaffoldResult(false, "Name is empty.");

        var modDir = Path.Combine(project.ModsDir, req.Name);
        if (Directory.Exists(modDir))
            return new ScaffoldResult(false, $"Folder already exists: {modDir}");

        var templateDir = FindTemplateDir(vmb.Root);
        if (templateDir == null)
            return new ScaffoldResult(false, $"No .template-vmf or .template folder found in {vmb.Root}. Re-download VMB.");

        try
        {
            Directory.CreateDirectory(modDir);
            CopyTemplateWithSubstitution(templateDir, modDir, req);
            WriteItemCfg(modDir, req);
            return new ScaffoldResult(true, $"Scaffolded {req.Name}", modDir);
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(modDir)) Directory.Delete(modDir, recursive: true); } catch { }
            return new ScaffoldResult(false, $"Scaffold failed: {ex.Message}");
        }
    }

    public static string? FindTemplateDir(string vmbRoot)
    {
        // VMF mods use .template-vmf; pre-VMF mods use .template. Prefer .template-vmf.
        var preferred = Path.Combine(vmbRoot, ".template-vmf");
        if (Directory.Exists(preferred)) return preferred;
        var fallback = Path.Combine(vmbRoot, ".template");
        if (Directory.Exists(fallback)) return fallback;
        return null;
    }

    /// <summary>Recursively copy <paramref name="sourceDir"/> to <paramref name="destDir"/>, substituting %%name/%%title/%%description in both file paths and text-file contents.</summary>
    public static void CopyTemplateWithSubstitution(string sourceDir, string destDir, ModScaffoldRequest req)
    {
        Directory.CreateDirectory(destDir);
        foreach (var srcFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, srcFile);
            var renamedRelative = Substitute(relative, req);
            var destFile = Path.Combine(destDir, renamedRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

            if (IsTextFile(srcFile))
            {
                var content = File.ReadAllText(srcFile);
                content = Substitute(content, req);
                File.WriteAllText(destFile, content);
            }
            else
            {
                File.Copy(srcFile, destFile, overwrite: true);
            }
        }
    }

    public static string Substitute(string s, ModScaffoldRequest req) =>
        s.Replace("%%name", req.Name)
         .Replace("%%title", EscapeForLuaString(req.Title))
         .Replace("%%description", EscapeForLuaString(req.Description));

    /// <summary>Escape characters that would break a Lua double-quoted string literal.</summary>
    public static string EscapeForLuaString(string s) =>
        (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");

    public static bool IsTextFile(string path)
    {
        // Whitelist text-like extensions. .mod, .package, .lua are the template files we expect to
        // contain placeholders; png/jpg/dds are binary copies. Empty extension is treated as text
        // (covers the rare case of extensionless template files).
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mod" or ".package" or ".lua" or ".cfg" or ".txt" or ".md" or ".ini" or ".json" or ".xml" or "";
    }

    public static void WriteItemCfg(string modDir, ModScaffoldRequest req)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"title = \"{EscapeForCfg(req.Title)}\";");
        sb.AppendLine($"description = \"{EscapeForCfg(req.Description)}\";");
        sb.AppendLine("preview = \"item_preview.png\";");
        sb.AppendLine("content = \"bundleV2\";");
        sb.AppendLine("language = \"english\";");
        sb.AppendLine($"visibility = \"{req.Visibility}\";");
        sb.AppendLine("apply_for_sanctioned_status = false;");
        sb.AppendLine("tags = [ ];");
        File.WriteAllText(Path.Combine(modDir, "itemV2.cfg"), sb.ToString());
    }

    public static string EscapeForCfg(string s) =>
        (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
}
