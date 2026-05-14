using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VmbLauncher.Services;

public sealed class Settings
{
    public string? VmbRoot { get; set; }
    /// <summary>Folder containing .vmbrc + the mods. May equal VmbRoot for standard installs, or be elsewhere for --cwd setups.</summary>
    public string? ProjectRoot { get; set; }
    public string? SteamRoot { get; set; }
    public string? Vt2SdkRoot { get; set; }
    public string? UgcToolPath { get; set; }
    public string? WorkshopContentRoot { get; set; }
    public string? NodePath { get; set; }

    public Dictionary<string, string> WorkshopIdOverrides { get; set; } = new();

    public bool ConfirmedFirstRun { get; set; }

    [JsonIgnore]
    public string ConfigPath { get; private set; } = DefaultConfigPath();

    public static string DefaultConfigPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VMBLauncher");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    public static Settings Load() => Load(null);

    /// <summary>
    /// Load settings from <paramref name="explicitPath"/> if given, else from the default
    /// %APPDATA%\VMBLauncher\settings.json location. Missing or unparseable files yield a
    /// fresh defaulted instance with ConfigPath pointing at the requested path, so a
    /// subsequent Save() writes there.
    /// </summary>
    public static Settings Load(string? explicitPath)
    {
        var path = string.IsNullOrEmpty(explicitPath) ? DefaultConfigPath() : explicitPath;
        if (!File.Exists(path))
        {
            return new Settings { ConfigPath = path };
        }
        try
        {
            var json = File.ReadAllText(path);
            var s = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
            s.ConfigPath = path;
            return s;
        }
        catch
        {
            return new Settings { ConfigPath = path };
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        File.WriteAllText(ConfigPath, json);
    }

    /// <summary>Auto-detect any unset fields and persist if anything changed.</summary>
    public bool AutoFillMissing()
    {
        var changed = false;

        if (string.IsNullOrEmpty(VmbRoot))
        {
            var v = VmbLocator.AutoDetect();
            if (v != null) { VmbRoot = v.Root; changed = true; }
        }
        if (string.IsNullOrEmpty(ProjectRoot))
        {
            var p = VmbProject.AutoDetect(VmbRoot);
            if (p != null) { ProjectRoot = p.Root; changed = true; }
        }
        if (string.IsNullOrEmpty(SteamRoot))
        {
            var s = SteamLocator.FindSteamInstall();
            if (s != null) { SteamRoot = s; changed = true; }
        }
        if (string.IsNullOrEmpty(Vt2SdkRoot))
        {
            var sdk = SteamLocator.FindVt2Sdk();
            if (sdk != null) { Vt2SdkRoot = sdk; changed = true; }
        }
        if (string.IsNullOrEmpty(UgcToolPath))
        {
            var t = SteamLocator.FindUgcTool();
            if (t != null) { UgcToolPath = t; changed = true; }
        }
        if (string.IsNullOrEmpty(WorkshopContentRoot))
        {
            var w = SteamLocator.FindWorkshopContentRoot();
            if (w != null) { WorkshopContentRoot = w; changed = true; }
        }
        if (string.IsNullOrEmpty(NodePath))
        {
            var n = VmbLocator.FindNode();
            if (n != null) { NodePath = n; changed = true; }
        }

        return changed;
    }
}
