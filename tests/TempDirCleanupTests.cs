using System.IO;

namespace VmbLauncher.Tests;

public sealed class TempDirCleanupTests
{
    [Fact]
    public void Dispose_RemovesNestedOwnedFilesAndEmptyDirectories()
    {
        var temp = new TempDir();
        temp.Write("a/b/one.txt", "one");
        temp.Write("a/two.txt", "two");
        temp.CreateSubdir("empty/nested");

        temp.Dispose();

        Assert.False(Directory.Exists(temp.Path));
        temp.Dispose(); // An already absent root is harmless.
    }

    [Fact]
    public void Dispose_DoesNotDeleteAFileWrittenOutsideTheOwnedRoot()
    {
        using var neighbor = new TempDir();
        var temp = new TempDir();
        var outside = temp.Write(Path.Combine("..", Path.GetFileName(neighbor.Path), "foreign.txt"), "keep");
        temp.Write("owned.txt", "remove");

        temp.Dispose();

        Assert.False(Directory.Exists(temp.Path));
        Assert.Equal("keep", File.ReadAllText(outside));
    }

    [Fact]
    public void Dispose_ContainsSharingFailureAndCanBeRetried()
    {
        var temp = new TempDir();
        var file = temp.Write("held.txt", "keep");
        try
        {
            using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Null(Record.Exception(temp.Dispose));
                Assert.True(File.Exists(file));
                Assert.True(Directory.Exists(temp.Path));
            }
            temp.Dispose();
            Assert.False(Directory.Exists(temp.Path));
        }
        finally
        {
            temp.Dispose();
        }
    }

    [Theory]
    [InlineData("sibling-prefix")]
    [InlineData("parent-escape")]
    [InlineData("relative")]
    public void CleanupPolicy_RejectsUnownedPathsBeforeAttributeReads(string kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "vmblauncher-policy-owned");
        var candidate = kind switch
        {
            "sibling-prefix" => root + "-foreign/file.txt",
            "parent-escape" => Path.Combine(root, "..", "foreign.txt"),
            _ => "relative.txt",
        };
        var reads = 0;
        Assert.Throws<IOException>(() => TempDir.ValidateCleanupPath(root, candidate, _ =>
        {
            reads++;
            return FileAttributes.Normal;
        }));
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("root")]
    [InlineData("ancestor")]
    public void CleanupPolicy_RejectsReparseLeavesDirectoriesAndRootAncestry(string kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "vmblauncher-policy-owned");
        var directory = Path.Combine(root, "nested");
        var file = Path.Combine(directory, "file.txt");
        var reparse = kind switch
        {
            "file" => file,
            "directory" => directory,
            "root" => root,
            _ => Path.GetDirectoryName(root)!,
        };

        Assert.Throws<IOException>(() => TempDir.ValidateCleanupPath(root, file, path =>
            path.Equals(reparse, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.ReparsePoint
                : FileAttributes.Normal));
    }
}
