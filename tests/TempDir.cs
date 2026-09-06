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
        try
        {
            // Validate the complete census before the first deletion. Do not traverse
            // links, even when they appear to lead back into this test's directory.
            var directories = new List<string> { Path };
            var files = new List<string>();
            var pending = new Stack<string>();
            pending.Push(Path);
            while (pending.Count != 0)
            {
                var directory = pending.Pop();
                if ((ValidateCleanupPath(Path, directory, File.GetAttributes) & FileAttributes.Directory) == 0)
                    throw new IOException("Temporary cleanup expected a directory.");
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = ValidateCleanupPath(Path, entry, File.GetAttributes);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directories.Add(entry);
                        pending.Push(entry);
                    }
                    else
                    {
                        files.Add(entry);
                    }
                }
            }

            foreach (var file in files)
            {
                if ((ValidateCleanupPath(Path, file, File.GetAttributes) & FileAttributes.Directory) != 0)
                    throw new IOException("Temporary cleanup file changed to a directory.");
                File.Delete(file);
            }
            // A parent is always recorded before its children, so reversal removes
            // only empty directories, children first. No recursive deletion or ACL fixup.
            for (var index = directories.Count - 1; index >= 0; index--)
            {
                var directory = directories[index];
                if ((ValidateCleanupPath(Path, directory, File.GetAttributes) & FileAttributes.Directory) == 0)
                    throw new IOException("Temporary cleanup directory changed to a file.");
                Directory.Delete(directory, recursive: false);
            }
        }
        catch { } // Cleanup remains best effort and must not mask the test's failure.
    }

    // The probe seam permits link-policy tests without requiring symlink privileges.
    internal static FileAttributes ValidateCleanupPath(
        string ownedRoot, string path, Func<string, FileAttributes> readAttributes)
    {
        if (!System.IO.Path.IsPathFullyQualified(ownedRoot) || !System.IO.Path.IsPathFullyQualified(path))
            throw new IOException("Temporary cleanup requires absolute owned paths.");
        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(ownedRoot));
        var candidate = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Temporary cleanup path escapes its owned directory.");

        var attributes = readAttributes(candidate);
        for (string? current = candidate; current is not null; current = System.IO.Path.GetDirectoryName(current))
        {
            var currentAttributes = current == candidate ? attributes : readAttributes(current);
            if ((currentAttributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Temporary cleanup refuses reparse paths and ancestry.");
        }
        return attributes;
    }
}
