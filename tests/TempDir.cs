using System.IO;

namespace VmbLauncher.Tests;

/// <summary>Disposable scratch directory for filesystem-touching tests.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vmblauncher-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }
    public string CreateSubdir(string relative)
    {
        var p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(p);
        return p;
    }
    public string Write(string relative, string contents)
    {
        var p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        File.WriteAllText(p, contents);
        return p;
    }
    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); } catch { }
    }
}
