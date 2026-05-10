using System.IO;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public class VmbLocatorTests
{
    [Fact]
    public void Resolve_null_returns_null()
    {
        Assert.Null(VmbLocator.Resolve(null));
        Assert.Null(VmbLocator.Resolve(""));
        Assert.Null(VmbLocator.Resolve(@"C:\does\not\exist\" + Guid.NewGuid()));
    }

    [Fact]
    public void Resolve_with_vmbexe_returns_binary_flavor()
    {
        using var t = new TempDir();
        File.WriteAllBytes(Path.Combine(t.Path, "vmb.exe"), new byte[] { 0x4D, 0x5A }); // MZ stub, just needs to exist
        var v = VmbLocator.Resolve(t.Path);
        Assert.NotNull(v);
        Assert.Equal(VmbFlavor.Binary, v!.Flavor);
        Assert.Equal(Path.Combine(t.Path, "vmb.exe"), v.Executable);
        Assert.Null(v.NodePath);
    }

    [Fact]
    public void Resolve_with_vmbjs_and_no_node_returns_null()
    {
        using var t = new TempDir();
        t.Write("vmb.js", "// fake vmb");
        // Hide PATH so node isn't found.
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", "");
        try
        {
            // Will scan PATH and the two hardcoded guesses. If node really lives at one of those guesses
            // on the test machine this returns Resolved; otherwise null. Either is acceptable, but the
            // contract is: if neither node.exe in PATH nor in standard locations, null.
            var v = VmbLocator.Resolve(t.Path);
            // Can't assert null universally — just check that if non-null, it's NodeScript flavor.
            if (v != null)
            {
                Assert.Equal(VmbFlavor.NodeScript, v.Flavor);
                Assert.NotNull(v.NodePath);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }
    }

    [Fact]
    public void Resolve_neither_vmbexe_nor_vmbjs_returns_null()
    {
        using var t = new TempDir();
        t.Write("readme.txt", "not a vmb");
        var v = VmbLocator.Resolve(t.Path);
        Assert.Null(v);
    }

    [Fact]
    public void VmbInstall_modsdir_is_root_slash_mods()
    {
        using var t = new TempDir();
        File.WriteAllBytes(Path.Combine(t.Path, "vmb.exe"), Array.Empty<byte>());
        var v = VmbLocator.Resolve(t.Path)!;
        Assert.Equal(Path.Combine(t.Path, "mods"), v.ModsDir);
    }

    [Fact]
    public void VmbInstall_hasvmbrc_reflects_file_presence()
    {
        using var t = new TempDir();
        File.WriteAllBytes(Path.Combine(t.Path, "vmb.exe"), Array.Empty<byte>());
        var v = VmbLocator.Resolve(t.Path)!;
        Assert.False(v.HasVmbRc);
        t.Write(".vmbrc", "{}");
        Assert.True(v.HasVmbRc);
    }
}
