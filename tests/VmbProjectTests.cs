using System.IO;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public class VmbProjectTests
{
    [Fact]
    public void ResolveModsDir_dot_returns_root_unchanged()
    {
        Assert.Equal(@"C:\some\root", VmbProject.ResolveModsDir(@"C:\some\root", "."));
    }

    [Fact]
    public void ResolveModsDir_relative_joins_under_root()
    {
        var got = VmbProject.ResolveModsDir(@"C:\some\root", "mods");
        Assert.Equal(Path.GetFullPath(@"C:\some\root\mods"), got);
    }

    [Fact]
    public void ResolveModsDir_nested_relative_resolves_through_dotdot()
    {
        var got = VmbProject.ResolveModsDir(@"C:\a\b\c", @"..\mods");
        Assert.Equal(Path.GetFullPath(@"C:\a\b\mods"), got);
    }

    [Fact]
    public void ResolveModsDir_absolute_is_unchanged()
    {
        var got = VmbProject.ResolveModsDir(@"C:\ignored", @"D:\mymods");
        Assert.Equal(@"D:\mymods", got);
    }

    [Fact]
    public void ResolveModsDir_empty_or_null_returns_root()
    {
        Assert.Equal(@"C:\r", VmbProject.ResolveModsDir(@"C:\r", ""));
        Assert.Equal(@"C:\r", VmbProject.ResolveModsDir(@"C:\r", null!));
    }

    [Fact]
    public void Resolve_no_dir_returns_null()
    {
        Assert.Null(VmbProject.Resolve(null));
        Assert.Null(VmbProject.Resolve(""));
        Assert.Null(VmbProject.Resolve(@"C:\definitely\does\not\exist\" + Guid.NewGuid()));
    }

    [Fact]
    public void Resolve_no_vmbrc_falls_back_to_mods_subfolder()
    {
        using var t = new TempDir();
        var p = VmbProject.Resolve(t.Path);
        Assert.NotNull(p);
        Assert.False(p!.HasVmbRc);
        Assert.Equal("mods", p.ModsDirRaw);
        Assert.Equal(Path.Combine(t.Path, "mods"), p.ModsDir);
    }

    [Fact]
    public void Resolve_with_vmbrc_dot_uses_root_as_mods_dir()
    {
        using var t = new TempDir();
        t.Write(".vmbrc", "{ \"mods_dir\": \".\" }");
        var p = VmbProject.Resolve(t.Path);
        Assert.NotNull(p);
        Assert.True(p!.HasVmbRc);
        Assert.Equal(".", p.ModsDirRaw);
        Assert.Equal(t.Path, p.ModsDir);
    }

    [Fact]
    public void Resolve_with_vmbrc_named_dir_uses_subfolder()
    {
        using var t = new TempDir();
        t.Write(".vmbrc", "{ \"mods_dir\": \"my_mods\" }");
        var p = VmbProject.Resolve(t.Path);
        Assert.NotNull(p);
        Assert.Equal("my_mods", p!.ModsDirRaw);
        Assert.Equal(Path.Combine(t.Path, "my_mods"), p.ModsDir);
    }

    [Fact]
    public void Resolve_malformed_vmbrc_falls_back_to_mods_default()
    {
        using var t = new TempDir();
        t.Write(".vmbrc", "{ this is not valid json :::: }");
        var p = VmbProject.Resolve(t.Path);
        Assert.NotNull(p);
        // HasVmbRc still true (the file exists) but ModsDirRaw is the default.
        Assert.True(p!.HasVmbRc);
        Assert.Equal("mods", p.ModsDirRaw);
    }

    [Fact]
    public void Resolve_vmbrc_with_no_mods_dir_uses_default()
    {
        using var t = new TempDir();
        t.Write(".vmbrc", "{ \"other\": \"thing\" }");
        var p = VmbProject.Resolve(t.Path);
        Assert.NotNull(p);
        Assert.Equal("mods", p!.ModsDirRaw);
    }

    [Fact]
    public void AutoDetect_prefers_vmbroot_when_it_has_vmbrc()
    {
        using var vmb = new TempDir();
        vmb.Write(".vmbrc", "{ \"mods_dir\": \"mods\" }");
        var p = VmbProject.AutoDetect(vmb.Path);
        Assert.NotNull(p);
        Assert.Equal(vmb.Path, p!.Root);
        Assert.True(p.HasVmbRc);
    }

    [Fact]
    public void AutoDetect_falls_back_to_vmbroot_when_no_vmbrc_anywhere()
    {
        using var vmb = new TempDir();
        // No .vmbrc anywhere; pass empty candidate list so we don't scan the real disk
        // (which on a dev machine would find the launcher author's actual project root).
        var p = VmbProject.AutoDetect(vmb.Path, extraCandidates: Array.Empty<string>());
        Assert.NotNull(p);
        Assert.Equal(vmb.Path, p!.Root);
        Assert.False(p.HasVmbRc);
    }

    [Fact]
    public void AutoDetect_finds_vmbrc_via_extra_candidates()
    {
        using var vmb = new TempDir();
        using var project = new TempDir();
        project.Write(".vmbrc", "{ \"mods_dir\": \".\" }");
        var p = VmbProject.AutoDetect(vmb.Path, extraCandidates: new[] { project.Path });
        Assert.NotNull(p);
        Assert.Equal(project.Path, p!.Root);
        Assert.True(p.HasVmbRc);
    }
}
