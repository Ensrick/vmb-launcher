using System.IO;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

/// <summary>
/// Guards the 2026-07-05 #344 STAGING-COLLISION mechanism (the follow-up to the crossed-id incident
/// pinned by <see cref="WorkshopIdGuardTests"/>): two launcher processes ran ugc_tool concurrently and
/// both staged into the SINGLE shared <c>&lt;SDK&gt;/ugc_uploader/sample_item/</c> dir. Session B's
/// Stage() overwrote session A's staged content between stage and push, so A's upload pushed B's mod
/// content onto A's Workshop item. The crossed-id guard validates the staged CFG id but not the staged
/// CONTENT, so it could not catch a same-target content swap. The launcher now verifies the staged
/// content/ dir is owned by the mod being uploaded before invoking ugc_tool.
///
/// These pin the decision logic of <see cref="ModRunner.InspectStagedContent"/>. (The cross-process
/// upload semaphore that serializes two launcher processes is not cheaply unit-testable and is not
/// covered here.)
///
/// The ownership marker is the mod's <c>&lt;name&gt;.mod</c> entry file (VMB names it after the mod
/// directory); the sibling hash-named <c>*.mod_bundle</c> files are NOT owner markers.
/// </summary>
public class StagedContentGuardTests
{
    [Fact]
    public void InspectStagedContent_owned_when_our_mod_entry_is_staged()
    {
        // The normal, safe case: Stage() copied career_tweaker's bundle (its <name>.mod + a hash bundle).
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "career_tweaker.mod"), "stub");
        File.WriteAllBytes(Path.Combine(dir.Path, "209fb8c3c0a8c3a4.mod_bundle"), new byte[] { 1 });

        var (verdict, foreign) = ModRunner.InspectStagedContent(dir.Path, "career_tweaker");

        Assert.Equal(ModRunner.StagedContentOwnership.OwnedByMod, verdict);
        Assert.Null(foreign);
    }

    [Fact]
    public void InspectStagedContent_foreign_when_another_mods_content_was_staged_over_ours()
    {
        // The exact staging-collision shape: we are uploading gui_tweaker_dev, but a concurrent
        // session B re-staged chaos_wastes_tweaker_dev's content into the shared dir first.
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "chaos_wastes_tweaker_dev.mod"), "stub");
        File.WriteAllBytes(Path.Combine(dir.Path, "195af59fc68656a5.mod_bundle"), new byte[] { 1 });

        var (verdict, foreign) = ModRunner.InspectStagedContent(dir.Path, "gui_tweaker_dev");

        Assert.Equal(ModRunner.StagedContentOwnership.ForeignOwner, verdict);
        Assert.Equal("chaos_wastes_tweaker_dev", foreign);
    }

    [Fact]
    public void InspectStagedContent_owned_is_case_insensitive_on_the_mod_name()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "Career_Tweaker.mod"), "stub");

        var (verdict, foreign) = ModRunner.InspectStagedContent(dir.Path, "career_tweaker");

        Assert.Equal(ModRunner.StagedContentOwnership.OwnedByMod, verdict);
        Assert.Null(foreign);
    }

    [Fact]
    public void InspectStagedContent_no_mod_entry_when_only_bundles_are_present()
    {
        // A dir with only hash-named .mod_bundle files (a clobbered / half-copied stage, no .mod entry
        // yet) must read as NoModEntry — NOT as owned (which would let a broken stage upload) and NOT
        // as a foreign owner named after a bundle hash. Also pins the exact-extension rule: EnumerateFiles
        // with a "*.mod" glob would wrongly match "*.mod_bundle" on Windows.
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "209fb8c3c0a8c3a4.mod_bundle"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(dir.Path, "4e6a9317aab221e1.mod_bundle"), new byte[] { 2 });

        var (verdict, foreign) = ModRunner.InspectStagedContent(dir.Path, "career_tweaker");

        Assert.Equal(ModRunner.StagedContentOwnership.NoModEntry, verdict);
        Assert.Null(foreign);
    }

    [Fact]
    public void InspectStagedContent_no_mod_entry_for_empty_dir()
    {
        using var dir = new TempDir();

        var (verdict, foreign) = ModRunner.InspectStagedContent(dir.Path, "career_tweaker");

        Assert.Equal(ModRunner.StagedContentOwnership.NoModEntry, verdict);
        Assert.Null(foreign);
    }

    [Fact]
    public void InspectStagedContent_no_mod_entry_for_missing_dir()
    {
        using var dir = new TempDir();
        var missing = Path.Combine(dir.Path, "content");
        Assert.False(Directory.Exists(missing));

        var (verdict, foreign) = ModRunner.InspectStagedContent(missing, "career_tweaker");

        Assert.Equal(ModRunner.StagedContentOwnership.NoModEntry, verdict);
        Assert.Null(foreign);
    }

    [Fact]
    public void InspectStagedContent_no_mod_entry_for_empty_or_null_content_dir()
    {
        Assert.Equal(ModRunner.StagedContentOwnership.NoModEntry, ModRunner.InspectStagedContent("", "career_tweaker").Verdict);
        Assert.Equal(ModRunner.StagedContentOwnership.NoModEntry, ModRunner.InspectStagedContent(null!, "career_tweaker").Verdict);
    }
}
