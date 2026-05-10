using System.IO;

namespace VmbLauncher.Services;

public enum CheckStatus { Ok, Warn, Error }

public sealed record CheckResult(string Title, CheckStatus Status, string Detail, string? FixAction = null);

public static class Diagnostics
{
    public static List<CheckResult> RunAll(Settings s)
    {
        var results = new List<CheckResult>();

        // VMB tool
        var vmb = VmbLocator.Resolve(s.VmbRoot);
        if (vmb == null)
            results.Add(new CheckResult("VMB", CheckStatus.Error,
                "Vermintide Mod Builder isn't configured. Either point us at your VMB folder, or download VMB from github.",
                "browse-vmb"));
        else
            results.Add(new CheckResult("VMB", CheckStatus.Ok, $"{vmb.Flavor} at {vmb.Root}"));

        // Project folder (where .vmbrc + mods live).
        var project = VmbProject.Resolve(s.ProjectRoot) ?? (s.VmbRoot != null ? VmbProject.Resolve(s.VmbRoot) : null);
        if (project == null)
        {
            results.Add(new CheckResult("Project folder", CheckStatus.Error,
                "Pick the folder where your .vmbrc and mod folders live. For most users this is the same as the VMB folder.",
                "browse-project"));
        }
        else if (!project.HasVmbRc)
        {
            results.Add(new CheckResult("Project folder", CheckStatus.Warn,
                $"Project folder is {project.Root} but it has no .vmbrc — VMB hasn't been configured here. Click Configure to run 'vmb config --mods_dir=<dir> --cwd' for you.",
                "configure-project"));
        }
        else if (!Directory.Exists(project.ModsDir))
        {
            results.Add(new CheckResult("Project folder", CheckStatus.Error,
                $"Project folder {project.Root} has .vmbrc with mods_dir = \"{project.ModsDirRaw}\", but the resolved mods directory ({project.ModsDir}) doesn't exist.",
                "browse-project"));
        }
        else
        {
            results.Add(new CheckResult("Project folder", CheckStatus.Ok, $"{project.Root} (mods_dir=\"{project.ModsDirRaw}\")"));
        }

        // Steam
        if (string.IsNullOrEmpty(s.SteamRoot) || !Directory.Exists(s.SteamRoot))
            results.Add(new CheckResult("Steam", CheckStatus.Error, "Steam install folder not found. Auto-detect or browse to it.", "browse-steam"));
        else if (!SteamLocator.IsSteamRunning())
            results.Add(new CheckResult("Steam", CheckStatus.Warn, "Steam isn't running. Uploads need it.", "start-steam"));
        else
            results.Add(new CheckResult("Steam", CheckStatus.Ok, $"running, {s.SteamRoot}"));

        // SDK
        if (string.IsNullOrEmpty(s.Vt2SdkRoot) || !Directory.Exists(s.Vt2SdkRoot))
            results.Add(new CheckResult("Vermintide 2 SDK", CheckStatus.Error,
                "The SDK contains ugc_tool.exe (used to push to Workshop) plus the Stingray compiler. Install via Steam → Library → Tools → 'Vermintide 2 SDK'.",
                "install-sdk"));
        else
            results.Add(new CheckResult("Vermintide 2 SDK", CheckStatus.Ok, s.Vt2SdkRoot));

        // ugc_tool
        if (string.IsNullOrEmpty(s.UgcToolPath) || !File.Exists(s.UgcToolPath))
            results.Add(new CheckResult("ugc_tool.exe", CheckStatus.Error,
                "Inside the SDK at ugc_uploader\\ugc_tool.exe. Install the SDK and this fills in automatically.",
                "browse-tool"));
        else
            results.Add(new CheckResult("ugc_tool.exe", CheckStatus.Ok, s.UgcToolPath));

        // Workshop folder
        if (string.IsNullOrEmpty(s.WorkshopContentRoot) || !Directory.Exists(s.WorkshopContentRoot))
            results.Add(new CheckResult("Workshop content folder", CheckStatus.Warn,
                "Steam creates this when you subscribe to any VT2 mod. Subscribe to one (or your own, after creating it) and it'll appear.",
                "open-workshop"));
        else
            results.Add(new CheckResult("Workshop content folder", CheckStatus.Ok, s.WorkshopContentRoot));

        return results;
    }

    public static bool HasErrors(IEnumerable<CheckResult> rs) => rs.Any(r => r.Status == CheckStatus.Error);
    public static bool HasIssues(IEnumerable<CheckResult> rs) => rs.Any(r => r.Status != CheckStatus.Ok);
}
