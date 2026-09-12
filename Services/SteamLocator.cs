using System.IO;
using System.Diagnostics;
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
            // Discovery is read-only. Creating the app directory during
            // Settings.AutoFillMissing used to mutate shared Steam state before
            // the machine transaction was acquired.
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

    public sealed record UploadReadiness(bool Ready, bool SteamRunning, string Detail);

    /// <summary>
    /// Verify the 32-bit Steamworks registration consumed by the SDK's x86
    /// ugc_tool, not merely the presence of a steam.exe process. A stale
    /// ActiveProcess PID makes SteamAPI_Init fail inside ugc_tool and has
    /// produced repeatable native access violations during publication.
    /// </summary>
    public static UploadReadiness GetWorkshopUploadReadiness(string? expectedSteamRoot = null)
    {
        var liveProcesses = new Dictionary<int, string>();
        try
        {
            foreach (var process in Process.GetProcessesByName("steam"))
            {
                using (process)
                {
                    try { liveProcesses[process.Id] = process.ProcessName; }
                    catch { /* A process can exit while the snapshot is read. */ }
                }
            }
        }
        catch (Exception ex)
        {
            return new UploadReadiness(false, false,
                $"Steam process state could not be inspected ({ex.Message}).");
        }

        int? registeredPid = null;
        string? registeredClientDll = null;
        try
        {
            using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32);
            using var key = hkcu.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (key?.GetValue("pid") is int pid && pid > 0) registeredPid = pid;
            registeredClientDll = key?.GetValue("SteamClientDll") as string;
        }
        catch (Exception ex)
        {
            var steamRunning = liveProcesses.Values.Any(IsSteamProcessName);
            return new UploadReadiness(false, steamRunning,
                $"Steam is running, but its 32-bit Steamworks registration could not be read ({ex.Message}). " +
                "Exit Steam fully and restart it before uploading.");
        }

        // Resolve the registered PID independently so PID reuse by a foreign
        // process is distinguishable from a dead registration.
        if (registeredPid is int registeredProcessId &&
            !liveProcesses.ContainsKey(registeredProcessId))
        {
            try
            {
                using var registered = Process.GetProcessById(registeredProcessId);
                liveProcesses[registered.Id] = registered.ProcessName;
            }
            catch (ArgumentException) { /* The registered process is absent. */ }
            catch (InvalidOperationException) { /* It exited during inspection. */ }
            catch (System.ComponentModel.Win32Exception)
            {
                // An unreadable live PID is not evidence that it is Steam.
                liveProcesses[registeredProcessId] = "<unreadable>";
            }
        }

        var expectedClientDll = string.IsNullOrWhiteSpace(expectedSteamRoot)
            ? null
            : Path.Combine(expectedSteamRoot, "steamclient.dll");
        var registeredClientDllExists = !string.IsNullOrWhiteSpace(registeredClientDll)
            && File.Exists(registeredClientDll);

        return EvaluateWorkshopUploadReadiness(
            liveProcesses,
            registeredPid,
            registeredClientDll,
            registeredClientDllExists,
            expectedClientDll);
    }

    internal static UploadReadiness EvaluateWorkshopUploadReadiness(
        IReadOnlyDictionary<int, string> liveProcesses,
        int? registeredPid,
        string? registeredClientDll,
        bool registeredClientDllExists,
        string? expectedClientDll)
    {
        var steamPids = liveProcesses
            .Where(pair => IsSteamProcessName(pair.Value))
            .Select(pair => pair.Key)
            .ToHashSet();
        if (steamPids.Count == 0)
            return new UploadReadiness(false, false, "Steam isn't running. Uploads need it.");

        if (registeredPid is null)
            return new UploadReadiness(false, true,
                "Steam is running, but its 32-bit Steamworks ActiveProcess registration is missing. " +
                "Exit Steam fully and restart it before uploading.");

        if (!liveProcesses.TryGetValue(registeredPid.Value, out var registeredName))
            return new UploadReadiness(false, true,
                $"Steam is running, but its 32-bit Steamworks ActiveProcess registration points to dead PID {registeredPid}. " +
                "Exit Steam fully and restart it before uploading.");

        if (!IsSteamProcessName(registeredName))
            return new UploadReadiness(false, true,
                $"Steam is running, but its 32-bit Steamworks ActiveProcess PID {registeredPid} now belongs to '{registeredName}'. " +
                "Exit Steam fully and restart it before uploading.");

        if (!registeredClientDllExists)
            return new UploadReadiness(false, true,
                "Steam is running, but its registered 32-bit steamclient.dll is missing. " +
                "Repair or restart Steam before uploading.");

        if (!string.IsNullOrWhiteSpace(expectedClientDll))
        {
            try
            {
                if (!Path.GetFullPath(registeredClientDll!).Equals(
                    Path.GetFullPath(expectedClientDll), StringComparison.OrdinalIgnoreCase))
                {
                    return new UploadReadiness(false, true,
                        $"Steamworks is registered to '{registeredClientDll}', but VMB is configured for '{expectedClientDll}'. " +
                        "Select the matching Steam install or restart Steam before uploading.");
                }
            }
            catch (Exception ex)
            {
                return new UploadReadiness(false, true,
                    $"Steam's registered steamclient.dll path is invalid ({ex.Message}). " +
                    "Repair or restart Steam before uploading.");
            }
        }

        return new UploadReadiness(true, true,
            $"Steamworks upload registration ready (PID {registeredPid}).");
    }

    private static bool IsSteamProcessName(string? name)
        => string.Equals(name, "steam", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "steam.exe", StringComparison.OrdinalIgnoreCase);
}
