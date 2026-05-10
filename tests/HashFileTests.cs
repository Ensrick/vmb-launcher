using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public class HashFileTests
{
    [Fact]
    public void HashFile_same_bytes_same_hash()
    {
        using var t = new TempDir();
        var a = t.Write("a.bin", "hello world");
        var b = t.Write("b.bin", "hello world");
        Assert.Equal(InvokeHash(a), InvokeHash(b));
    }

    [Fact]
    public void HashFile_different_bytes_different_hash()
    {
        using var t = new TempDir();
        var a = t.Write("a.bin", "hello world");
        var b = t.Write("b.bin", "hello WORLD");
        Assert.NotEqual(InvokeHash(a), InvokeHash(b));
    }

    [Fact]
    public void HashFile_matches_md5_of_contents()
    {
        using var t = new TempDir();
        var p = t.Write("data.bin", "deterministic content");
        var expected = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(p)));
        Assert.Equal(expected, InvokeHash(p));
    }

    private static string InvokeHash(string path)
    {
        var m = typeof(ModRunner).GetMethod("HashFile", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)m.Invoke(null, new object[] { path })!;
    }
}
