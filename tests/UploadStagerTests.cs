using System.IO;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public class UploadStagerTests
{
    private sealed class FakeSdk : IDisposable
    {
        public TempDir SdkDir { get; }
        public TempDir ModDir { get; }
        public string UgcToolPath { get; }
        public ModInfo Mod { get; }

        public FakeSdk(bool withPreview = true, bool withBundles = true, string? publishedId = null)
        {
            SdkDir = new TempDir();
            var uploaderDir = SdkDir.CreateSubdir("ugc_uploader");
            UgcToolPath = Path.Combine(uploaderDir, "ugc_tool.exe");
            File.WriteAllBytes(UgcToolPath, Array.Empty<byte>());

            ModDir = new TempDir();
            var bundleV2 = ModDir.CreateSubdir("bundleV2");
            if (withBundles)
            {
                File.WriteAllBytes(Path.Combine(bundleV2, "abc123.mod_bundle"), new byte[] { 1, 2, 3 });
                File.WriteAllBytes(Path.Combine(bundleV2, "def456.mod_bundle"), new byte[] { 4, 5, 6 });
                File.WriteAllText(Path.Combine(bundleV2, "mymod.mod"), "stub");
            }
            if (withPreview)
            {
                File.WriteAllBytes(Path.Combine(ModDir.Path, "item_preview.png"), new byte[] { 0x89, 0x50 });
            }

            var cfgContent = $"title = \"My Mod\";\ndescription = \"desc\";\npreview = \"item_preview.png\";\ncontent = \"bundleV2\";\nlanguage = \"english\";\nvisibility = \"private\";\n";
            if (publishedId != null) cfgContent += $"published_id = {publishedId}L;\n";
            cfgContent += "apply_for_sanctioned_status = false;\ntags = [ ];\n";
            var cfgPath = Path.Combine(ModDir.Path, "itemV2.cfg");
            File.WriteAllText(cfgPath, cfgContent);

            Mod = new ModInfo
            {
                Name = "mymod",
                ModDir = ModDir.Path,
                ItemCfgPath = cfgPath,
            };
            ModDiscovery.ParseItemCfg(Mod);
        }
        public void Dispose() { SdkDir.Dispose(); ModDir.Dispose(); }
    }

    [Fact]
    public void Stage_creates_staging_folder_with_bundles_in_content()
    {
        using var fake = new FakeSdk();
        var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);

        Assert.True(Directory.Exists(staged.StagingDir));
        var contentDir = Path.Combine(staged.StagingDir, "content");
        Assert.True(Directory.Exists(contentDir));
        Assert.Equal(3, Directory.EnumerateFiles(contentDir).Count());
        Assert.True(File.Exists(Path.Combine(contentDir, "abc123.mod_bundle")));
        Assert.True(File.Exists(Path.Combine(contentDir, "mymod.mod")));
        Assert.Equal(3, staged.FilesCopied);
    }

    [Fact]
    public void Stage_copies_preview_to_staging_root()
    {
        using var fake = new FakeSdk(withPreview: true);
        var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
        Assert.True(File.Exists(Path.Combine(staged.StagingDir, "item_preview.png")));
    }

    [Fact]
    public void Stage_works_without_preview()
    {
        using var fake = new FakeSdk(withPreview: false);
        var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
        Assert.False(File.Exists(Path.Combine(staged.StagingDir, "item_preview.png")));
        // Cfg still gets written; ugc_tool will accept it for updates if preview was previously set.
        Assert.True(File.Exists(staged.CfgPath));
    }

    [Fact]
    public void Stage_writes_cfg_with_relative_content_path()
    {
        using var fake = new FakeSdk();
        var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
        var raw = File.ReadAllText(staged.CfgPath);
        Assert.Contains("content = \"content\";", raw);
        Assert.Contains("preview = \"item_preview.png\";", raw);
    }

    [Fact]
    public void Stage_preserves_published_id_when_present()
    {
        using var fake = new FakeSdk(publishedId: "12345");
        var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
        var raw = File.ReadAllText(staged.CfgPath);
        Assert.Contains("published_id = 12345L;", raw);
    }

    [Fact]
    public void Stage_omits_published_id_when_absent()
    {
        using var fake = new FakeSdk(publishedId: null);
        var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
        var raw = File.ReadAllText(staged.CfgPath);
        Assert.DoesNotContain("published_id", raw);
    }

    [Fact]
    public void Stage_throws_when_no_bundles()
    {
        using var fake = new FakeSdk(withBundles: false);
        var ex = Assert.Throws<InvalidOperationException>(() => UploadStager.Stage(fake.Mod, fake.UgcToolPath));
        Assert.Contains("Run Build first", ex.Message);
    }

    [Fact]
    public void Stage_wipes_previous_staging_contents()
    {
        using var fake = new FakeSdk();
        var staged1 = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
        // Drop a sentinel file inside the staging area.
        File.WriteAllText(Path.Combine(staged1.StagingDir, "stale.txt"), "should be gone next stage");

        var staged2 = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
        Assert.False(File.Exists(Path.Combine(staged2.StagingDir, "stale.txt")));
    }

    [Fact]
    public void UpsertPublishedId_inserts_when_absent()
    {
        var raw = "title = \"x\";\nvisibility = \"private\";\n";
        var got = UploadStager.UpsertPublishedId(raw, "999");
        Assert.Contains("published_id = 999L;", got);
        // Should sit before visibility line.
        var idIdx = got.IndexOf("published_id");
        var visIdx = got.IndexOf("visibility");
        Assert.True(idIdx < visIdx);
    }

    [Fact]
    public void UpsertPublishedId_replaces_when_present()
    {
        var raw = "title = \"x\";\npublished_id = 111L;\nvisibility = \"private\";\n";
        var got = UploadStager.UpsertPublishedId(raw, "222");
        Assert.Contains("published_id = 222L;", got);
        Assert.DoesNotContain("published_id = 111L", got);
    }

    [Fact]
    public void UpsertPublishedId_appends_when_no_visibility_line()
    {
        var raw = "title = \"x\";\n";
        var got = UploadStager.UpsertPublishedId(raw, "777");
        Assert.Contains("published_id = 777L;", got);
    }

    [Fact]
    public void PropagatePublishedIdBack_writes_new_id_to_mod_cfg()
    {
        using var fake = new FakeSdk(publishedId: null);
        var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
        // Simulate ugc_tool writing a new id into the staged cfg.
        var stagedRaw = File.ReadAllText(staged.CfgPath);
        File.WriteAllText(staged.CfgPath, stagedRaw + "\npublished_id = 555L;\n");

        var updated = UploadStager.PropagatePublishedIdBack(staged, fake.Mod);
        Assert.True(updated);

        var modRaw = File.ReadAllText(fake.Mod.ItemCfgPath);
        Assert.Contains("published_id = 555L;", modRaw);
    }

    [Fact]
    public void PropagatePublishedIdBack_skips_when_no_id_in_staged()
    {
        using var fake = new FakeSdk(publishedId: null);
        var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
        // Staged cfg has no published_id (we didn't simulate ugc_tool writing one).
        Assert.False(UploadStager.PropagatePublishedIdBack(staged, fake.Mod));
    }

    [Fact]
    public void PropagatePublishedIdBack_skips_when_already_matches()
    {
        using var fake = new FakeSdk(publishedId: "999");
        var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
        // Staged has the same id; nothing to propagate.
        Assert.False(UploadStager.PropagatePublishedIdBack(staged, fake.Mod));
    }

    [Fact]
    public void GetStagingDir_lives_under_uploader_folder()
    {
        var dir = UploadStager.GetStagingDir(@"C:\sdk\ugc_uploader");
        Assert.Equal(@"C:\sdk\ugc_uploader\vmblauncher_staging", dir);
    }
}
