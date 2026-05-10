using System.IO;
using Microsoft.Win32;

namespace VmbLauncher.Services;

public static class SteamLocator
{
    public const int Vt2AppId = 552500;
    public const string Vt2SdkFolderName = "Vermintide 2 SDK";

    public static string? FindSteamInstall()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var k = hklm.OpenSubKey(@"SOFTWARE\Valve\Steam") ?? hklm.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            var path = k?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
        }
        using var hkcu = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var p = hkcu?.GetValue("SteamPath") as string;
        if (!string.IsNullOrEmpty(p))
        {
            p = p.Replace('/', Path.DirectorySeparatorChar);
            if (Directory.Exists(p)) return p;
        }
        foreach (var guess in new[] {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam"
        })
        {
            if (Directory.Exists(guess)) return guess;
        }
        return null;
    }

    public static IEnumerable<string> EnumerateLibraryFolders()
    {
        var steam = FindSteamInstall();
        if (steam == null) yield break;
        yield return Path.Combine(steam, "steamapps");
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        foreach (var line in File.ReadLines(vdf))
        {
            // crude: matches  "path"   "D:\\Games\\SteamLibrary"
            var t = line.Trim();
            if (!t.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
            var firstQuote = t.IndexOf('"', 6);
            if (firstQuote < 0) continue;
            var lastQuote = t.LastIndexOf('"');
            if (lastQuote <= firstQuote) continue;
            var raw = t.Substring(firstQuote + 1, lastQuote - firstQuote - 1).Replace(@"\\", @"\");
            var sa = Path.Combine(raw, "steamapps");
            if (Directory.Exists(sa)) yield return sa;
        }
    }

    public static string? FindWorkshopContentRoot()
    {
        foreach (var sa in EnumerateLibraryFolders())
        {
            var ws = Path.Combine(sa, "workshop", "content", Vt2AppId.ToString());
            if (Directory.Exists(ws)) return ws;
            var wsRoot = Path.Combine(sa, "workshop", "content");
            if (Directory.Exists(wsRoot))
            {
                Directory.CreateDirectory(Path.Combine(wsRoot, Vt2AppId.ToString()));
                return Path.Combine(wsRoot, Vt2AppId.ToString());
            }
        }
        return null;
    }

    public static string? FindVt2Sdk()
    {
        foreach (var sa in EnumerateLibraryFolders())
        {
            var p = Path.Combine(sa, "common", Vt2SdkFolderName);
            if (Directory.Exists(p)) return p;
        }
        return null;
    }

    public static string? FindUgcTool()
    {
        var sdk = FindVt2Sdk();
        if (sdk == null) return null;
        var tool = Path.Combine(sdk, "ugc_uploader", "ugc_tool.exe");
        return File.Exists(tool) ? tool : null;
    }

    public static bool IsSteamRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("steam").Length > 0;
        }
        catch { return false; }
    }
}
