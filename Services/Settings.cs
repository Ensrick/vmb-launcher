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

    /// <summary>
    /// Remote machines deployed to alongside the local Workshop folder. Auto-populated
    /// on first run from ~/.ssh/config (currently: pc-b). Manage via direct edits to
    /// settings.json; the GUI surface is intentionally minimal.
    /// </summary>
    public List<RemoteDeployTarget> RemoteDeployTargets { get; set; } = new();

    public bool ConfirmedFirstRun { get; set; }

    [JsonIgnore]
    public string ConfigPath { get; private set; } = DefaultConfigPath();

    public static string DefaultConfigPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VMBLauncher");
        return Path.Combine(dir, "settings.json");
    }

    public static Settings Load() => Load(null);

    /// <summary>
    /// Strict load for any flow that may later mutate settings, a project, or
    /// publication state. A missing file is a valid first-run state; an
    /// existing file that cannot be read and decoded is not silently replaced.
    /// </summary>
    public static Settings LoadForMutation(string? explicitPath = null)
    {
        var path = string.IsNullOrEmpty(explicitPath) ? DefaultConfigPath() : explicitPath;
        if (!File.Exists(path)) return new Settings { ConfigPath = path };
        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<Settings>(json)
                ?? throw new InvalidDataException("settings decoded to null");
            settings.ConfigPath = path;
            return settings;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"Refusing mutation: existing settings file '{path}' is unreadable or malformed ({ex.Message}).", ex);
        }
    }

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
        MachineTransactionLease.RequireCurrent("Settings.Save");
        var parent = Path.GetDirectoryName(Path.GetFullPath(ConfigPath))
            ?? throw new InvalidOperationException("Settings path has no parent directory.");
        Directory.CreateDirectory(parent);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        var temporary = ConfigPath + ".tmp." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, ConfigPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
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

        // Auto-populate remote deploy targets once. The detector reads ~/.ssh/config; if the
        // user has the standard `Host pc-b` alias configured, a target gets added with the
        // canonical (025) Steam workshop path pinned from project memory. Repeated AutoFill
        // calls are safe — we only add hosts not already in the list.
        var detected = RemoteDeploy.AutoDetectFromSshConfig();
        foreach (var d in detected)
        {
            if (!RemoteDeployTargets.Any(t => string.Equals(t.SshHost, d.SshHost, StringComparison.OrdinalIgnoreCase)))
            {
                RemoteDeployTargets.Add(d);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>The exact project root downstream mutation will use.</summary>
    internal VmbProject? ResolveMutationProject() =>
        VmbProject.Resolve(ProjectRoot) ?? VmbProject.Resolve(VmbRoot);
}
