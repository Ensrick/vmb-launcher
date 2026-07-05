using System.IO;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

/// <summary>
/// Guards the 2026-07-05 #344 incident: gui_tweaker_dev's itemV2.cfg transiently carried
/// chaos_wastes_tweaker_dev's published_id, so an upload hijacked ct_dev's item and a deploy wrote
/// ct files into gut's Workshop folder (double-loading ct, never loading gut). The launcher now
/// verifies the Workshop content folder for an id is owned by the mod being processed before acting.
/// These pin the shared primitive <see cref="ModRunner.FindForeignModOwner"/> behind that guard.
///
/// The ownership marker is the mod's <c>&lt;name&gt;.mod</c> entry file (VMB names it after the mod
/// directory); the sibling hash-named <c>*.mod_bundle</c> files are NOT owner markers.
/// </summary>
public class WorkshopIdGuardTests
{
    [Fact]
    public void FindForeignModOwner_returns_owner_when_folder_belongs_to_another_mod()
    {
        // The exact incident shape: the target folder is owned by chaos_wastes_tweaker_dev but we
        // are processing gui_tweaker_dev (its cfg carried ct_dev's id).
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "chaos_wastes_tweaker_dev.mod"), "stub");
        File.WriteAllBytes(Path.Combine(dir.Path, "195af59fc68656a5.mod_bundle"), new byte[] { 1 });

        var foreign = ModRunner.FindForeignModOwner(dir.Path, "gui_tweaker_dev");

        Assert.Equal("chaos_wastes_tweaker_dev", foreign);
    }

    [Fact]
    public void FindForeignModOwner_returns_null_when_folder_is_ours()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "career_tweaker.mod"), "stub");
        File.WriteAllBytes(Path.Combine(dir.Path, "209fb8c3c0a8c3a4.mod_bundle"), new byte[] { 1 });

        Assert.Null(ModRunner.FindForeignModOwner(dir.Path, "career_tweaker"));
    }

    [Fact]
    public void FindForeignModOwner_is_case_insensitive_on_the_mod_name()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "Career_Tweaker.mod"), "stub");

        Assert.Null(ModRunner.FindForeignModOwner(dir.Path, "career_tweaker"));
    }

    [Fact]
    public void FindForeignModOwner_ignores_mod_bundle_files_exact_extension_only()
    {
        // Windows' 3-char search-pattern rule makes EnumerateFiles(dir, "*.mod") ALSO match
        // "*.mod_bundle". A folder with ONLY hash-named .mod_bundle files (freshly-subscribed,
        // no .mod entry yet) must read as "no owner evidence" (null), NOT as a foreign owner
        // named after a bundle hash.
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "209fb8c3c0a8c3a4.mod_bundle"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(dir.Path, "4e6a9317aab221e1.mod_bundle"), new byte[] { 2 });

        Assert.Null(ModRunner.FindForeignModOwner(dir.Path, "career_tweaker"));
    }

    [Fact]
    public void FindForeignModOwner_returns_null_for_empty_folder()
    {
        using var dir = new TempDir();
        Assert.Null(ModRunner.FindForeignModOwner(dir.Path, "career_tweaker"));
    }

    [Fact]
    public void FindForeignModOwner_returns_null_for_missing_folder()
    {
        using var dir = new TempDir();
        var missing = Path.Combine(dir.Path, "3999999999");
        Assert.False(Directory.Exists(missing));

        Assert.Null(ModRunner.FindForeignModOwner(missing, "career_tweaker"));
    }

    [Fact]
    public void FindForeignModOwner_returns_null_for_empty_or_null_content_dir()
    {
        Assert.Null(ModRunner.FindForeignModOwner("", "career_tweaker"));
        Assert.Null(ModRunner.FindForeignModOwner(null!, "career_tweaker"));
    }
}
