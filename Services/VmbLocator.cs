using System.IO;

namespace VmbLauncher.Services;

public enum VmbFlavor { None, Binary, NodeScript }

public sealed record VmbInstall(string Root, VmbFlavor Flavor, string Executable, string? NodePath)
{
    public bool IsBinary => Flavor == VmbFlavor.Binary;
    public string ModsDir => Path.Combine(Root, "mods");
    public string VmbRcPath => Path.Combine(Root, ".vmbrc");
    public bool HasVmbRc => File.Exists(VmbRcPath);
}

public static class VmbLocator
{
    public static VmbInstall? Resolve(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;

        var exe = Path.Combine(root, "vmb.exe");
        if (File.Exists(exe))
            return new VmbInstall(root, VmbFlavor.Binary, exe, null);

        var js = Path.Combine(root, "vmb.js");
        if (File.Exists(js))
        {
            var node = FindNode();
            if (node != null)
                return new VmbInstall(root, VmbFlavor.NodeScript, js, node);
        }

        return null;
    }

    public static string? FindNode()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "node.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* invalid path char */ }
        }
        foreach (var guess in new[] {
            @"C:\Program Files\nodejs\node.exe",
            @"C:\Program Files (x86)\nodejs\node.exe"
        })
        {
            if (File.Exists(guess)) return guess;
        }
        return null;
    }

    public static IEnumerable<string> CommonGuessRoots()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos", "vmb");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "vmb");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "VMB");
        foreach (var d in new[] { "C:\\", "D:\\", "E:\\" })
        {
            yield return Path.Combine(d, "vmb");
            yield return Path.Combine(d, "VMB");
            yield return Path.Combine(d, "tools", "vmb");
        }
    }

    public static VmbInstall? AutoDetect()
    {
        foreach (var guess in CommonGuessRoots())
        {
            var inst = Resolve(guess);
            if (inst != null) return inst;
        }
        return null;
    }
}
