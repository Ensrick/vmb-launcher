using System.IO;
using System.Security.Cryptography;
using System.Text;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public class UploadStagerTests : MutationTestBase
{
    [Fact]
    public void StageFailsClosedWhenOldTreeCannotBeDeleted()
    {
        using var fake = new FakeSdk();
        var first = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
        var heldPath = Path.Combine(first.StagingDir, "content", "mymod.mod");
        using var held = new FileStream(
            heldPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = Assert.Throws<IOException>(() =>
            UploadStager.Stage(fake.Mod, fake.UgcToolPath));
        Assert.Contains("Refusing to mix stale and current upload bytes", ex.Message);
    }

    private sealed class FakeSdk : IDisposable
    {
        public TempDir SdkDir { get; }
        public TempDir ModDir { get; }
        public string UgcToolPath { get; }
        public ModInfo Mod { get; }

        public FakeSdk(bool withPreview = true, bool withBundles = true, string? publishedId = null, string? cfgContent = null)
        {
            SdkDir = new TempDir();
            var uploaderDir = SdkDir.CreateSubdir("ugc_uploader");
            UgcToolPath = Path.Combine(uploaderDir, "ugc_tool.exe");
            File.WriteAllBytes(UgcToolPath, Array.Empty<byte>());

            ModDir = new TempDir();
            var modPath = ModDir.CreateSubdir("mymod");
            var bundleV2 = Path.Combine(modPath, "bundleV2");
            Directory.CreateDirectory(bundleV2);
            if (withBundles)
            {
                File.WriteAllBytes(Path.Combine(bundleV2, "abc123.mod_bundle"), new byte[] { 1, 2, 3 });
                File.WriteAllBytes(Path.Combine(bundleV2, "def456.mod_bundle"), new byte[] { 4, 5, 6 });
                File.WriteAllText(Path.Combine(bundleV2, "mymod.mod"), "stub");
            }
            if (withPreview)
            {
                File.WriteAllBytes(Path.Combine(modPath, "item_preview.png"), new byte[] { 0x89, 0x50 });
            }

            if (cfgContent == null)
            {
                cfgContent = $"title = \"My Mod\";\ndescription = \"desc\";\npreview = \"item_preview.png\";\ncontent = \"bundleV2\";\nlanguage = \"english\";\nvisibility = \"private\";\n";
                if (publishedId != null) cfgContent += $"published_id = {publishedId}L;\n";
                cfgContent += "apply_for_sanctioned_status = false;\ntags = [ ];\n";
            }
            var cfgPath = Path.Combine(modPath, "itemV2.cfg");
            File.WriteAllText(cfgPath, cfgContent);

            Mod = new ModInfo
            {
                Name = "mymod",
                ModDir = modPath,
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
    public void Stage_copies_cfg_named_preview_when_set()
    {
        // When itemV2.cfg's `preview` field names a custom filename (e.g. a unified thumbnail used
        // across multiple friends-only mods) and that file exists in the mod dir, the launcher must
        // stage THAT file under its literal name — NOT silently fall through to item_preview.png.
        // Regression-guards the bug where the fixed candidate list `{ item_preview.png, preview.jpg,
        // preview.png }` made the cfg field effectively dead.
        var cfg = "title = \"My Mod\";\ndescription = \"desc\";\npreview = \"my_custom.jpg\";\n"
                + "content = \"bundleV2\";\nlanguage = \"english\";\nvisibility = \"private\";\n"
                + "apply_for_sanctioned_status = false;\n";
        using var fake = new FakeSdk(withPreview: false, cfgContent: cfg);
        // Drop the custom-named preview file into the mod dir.
        File.WriteAllBytes(Path.Combine(fake.Mod.ModDir, "my_custom.jpg"), new byte[] { 0xFF, 0xD8 });

        var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);

        Assert.True(File.Exists(Path.Combine(staged.StagingDir, "my_custom.jpg")));
        Assert.False(File.Exists(Path.Combine(staged.StagingDir, "item_preview.png")));
        var rawCfg = File.ReadAllText(staged.CfgPath);
        Assert.Contains("preview = \"my_custom.jpg\";", rawCfg);
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
    public void Stage_rejects_preview_path_traversal()
    {
        var cfg = "title = \"My Mod\";\ndescription = \"desc\";\npreview = \"..\\\\outside.jpg\";\n"
                + "content = \"bundleV2\";\nlanguage = \"english\";\nvisibility = \"private\";\n"
                + "apply_for_sanctioned_status = false;\n";
        using var fake = new FakeSdk(withPreview: false, cfgContent: cfg);
        var outside = Path.Combine(fake.Mod.ModDir, "..", "outside.jpg");
        File.WriteAllText(outside, "must-not-stage");

        var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);

        Assert.Equal("item_preview.png", staged.PreviewName);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(staged.StagingDir)!, "outside.jpg")));
    }

    [Theory]
    [InlineData("C:outside.jpg")]
    [InlineData("preview.jpg:alternate")]
    [InlineData("CON")]
    [InlineData("preview.jpg.")]
    public void ResolvePreviewName_rejects_windows_alias_and_device_names(string configured)
    {
        var cfg = $"preview = \"{configured}\";";
        var resolved = UploadStager.ResolvePreviewNameFromSourceCfg(cfg, _ => true);
        Assert.Equal("item_preview.png", resolved);
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
    public void Stage_writes_published_id_0L_for_new_mods()
    {
        // Per ANTIGRAVITY.md docs, new items need `published_id = 0L;` explicit.
        // Omitting the line is NOT the same as 0L (causes 0x2 error on first upload).
        using var fake = new FakeSdk(publishedId: null);
        var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
        var raw = File.ReadAllText(staged.CfgPath);
        Assert.Contains("published_id = 0L;", raw);
    }

    [Fact]
    public void Stage_does_not_write_tags_line()
    {
        // Per ANTIGRAVITY.md docs, ugc_tool ADDS tags=[] itself after first successful upload.
        // Pre-adding it manually breaks the upload's content step on first uploads.
        using var fake = new FakeSdk();
        var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
        var raw = File.ReadAllText(staged.CfgPath);
        Assert.DoesNotContain("tags", raw);
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
    public void BootstrapWriteBack_ChangesOnlyAuthorizedZeroId()
    {
        using var fake = new FakeSdk(publishedId: "0");
        var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
        var expectedStaged = File.ReadAllText(staged.CfgPath);
        File.WriteAllText(
            staged.CfgPath,
            UploadStager.UpsertPublishedId(expectedStaged, "555") + "tags = [ ];\n");
        var sourceBytes = File.ReadAllBytes(fake.Mod.ItemCfgPath);

        var result = UploadStager.CompleteBootstrapWriteBack(
            staged, fake.Mod, expectedStaged, Sha256(sourceBytes));

        Assert.True(result.Ok, result.Message);
        Assert.Equal("555", result.PublishedId);
        var actual = File.ReadAllText(fake.Mod.ItemCfgPath);
        Assert.Contains("published_id = 555L;", actual);
        Assert.Equal(
            UploadStager.UpsertPublishedId(Encoding.UTF8.GetString(sourceBytes), "555"),
            actual);
    }

    [Fact]
    public void BootstrapWriteBack_RejectsMutatedSourceWithoutClobberingIt()
    {
        using var fake = new FakeSdk(publishedId: "0");
        var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
        var expectedStaged = File.ReadAllText(staged.CfgPath);
        var sourceHash = Sha256(File.ReadAllBytes(fake.Mod.ItemCfgPath));
        File.WriteAllText(
            staged.CfgPath,
            UploadStager.UpsertPublishedId(expectedStaged, "555"));
        File.AppendAllText(fake.Mod.ItemCfgPath, "description = \"concurrent edit\";\n");
        var concurrentBytes = File.ReadAllBytes(fake.Mod.ItemCfgPath);

        var result = UploadStager.CompleteBootstrapWriteBack(
            staged, fake.Mod, expectedStaged, sourceHash);

        Assert.False(result.Ok);
        Assert.Equal("555", result.PublishedId);
        Assert.Equal(concurrentBytes, File.ReadAllBytes(fake.Mod.ItemCfgPath));
    }

    [Fact]
    public void BootstrapWriteBack_RejectsDuplicateSourcePublishedIdSentinels()
    {
        using var fake = new FakeSdk(publishedId: "0");
        File.AppendAllText(fake.Mod.ItemCfgPath, "published_id = 0L;\n");
        var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
        var expectedStaged = File.ReadAllText(staged.CfgPath);
        File.WriteAllText(
            staged.CfgPath,
            UploadStager.UpsertPublishedId(expectedStaged, "555"));
        var sourceBytes = File.ReadAllBytes(fake.Mod.ItemCfgPath);

        var result = UploadStager.CompleteBootstrapWriteBack(
            staged, fake.Mod, expectedStaged, Sha256(sourceBytes));

        Assert.False(result.Ok);
        Assert.Contains("exactly one", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sourceBytes, File.ReadAllBytes(fake.Mod.ItemCfgPath));
    }

    [Fact]
    public void BootstrapWriteBack_RejectsAssignedIdAlreadyOwnedBySiblingMod()
    {
        using var fake = new FakeSdk(publishedId: "0");
        var sibling = Path.Combine(
            Directory.GetParent(fake.Mod.ModDir)!.FullName,
            "bootstrap-collision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sibling);
        try
        {
            File.WriteAllText(
                Path.Combine(sibling, "itemV2.cfg"),
                "title = \"Sibling\";\npublished_id = 555L;\n");
            var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
            var expectedStaged = File.ReadAllText(staged.CfgPath);
            File.WriteAllText(
                staged.CfgPath,
                UploadStager.UpsertPublishedId(expectedStaged, "555"));
            var sourceBytes = File.ReadAllBytes(fake.Mod.ItemCfgPath);

            var result = UploadStager.CompleteBootstrapWriteBack(
                staged, fake.Mod, expectedStaged, Sha256(sourceBytes));

            Assert.False(result.Ok);
            Assert.Contains("already owns", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(sourceBytes, File.ReadAllBytes(fake.Mod.ItemCfgPath));
        }
        finally
        {
            File.Delete(Path.Combine(sibling, "itemV2.cfg"));
            Directory.Delete(sibling);
        }
    }

    [Theory]
    [InlineData("visibility = \"private\";", "visibility = \"public\";")]
    [InlineData("content = \"content\";", "content = \"..\\\\foreign\";")]
    [InlineData("preview = \"item_preview.png\";", "preview = \"foreign.png\";")]
    public void BootstrapWriteBack_RejectsSecurityFieldMutation(
        string original,
        string replacement)
    {
        using var fake = new FakeSdk(publishedId: "0");
        var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
        var expectedStaged = File.ReadAllText(staged.CfgPath);
        var sourceBytes = File.ReadAllBytes(fake.Mod.ItemCfgPath);
        var malicious = UploadStager.UpsertPublishedId(expectedStaged, "555")
            .Replace(original, replacement, StringComparison.Ordinal);
        File.WriteAllText(staged.CfgPath, malicious);

        var result = UploadStager.CompleteBootstrapWriteBack(
            staged, fake.Mod, expectedStaged, Sha256(sourceBytes));

        Assert.False(result.Ok);
        Assert.Equal(sourceBytes, File.ReadAllBytes(fake.Mod.ItemCfgPath));
    }

    [Fact]
    public void BootstrapWriteBack_RejectsInjectedCfgDirective()
    {
        using var fake = new FakeSdk(publishedId: "0");
        var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
        var expectedStaged = File.ReadAllText(staged.CfgPath);
        var sourceBytes = File.ReadAllBytes(fake.Mod.ItemCfgPath);
        File.WriteAllText(
            staged.CfgPath,
            UploadStager.UpsertPublishedId(expectedStaged, "555") +
            "content = \"C:/attacker\";\n");

        var result = UploadStager.CompleteBootstrapWriteBack(
            staged, fake.Mod, expectedStaged, Sha256(sourceBytes));

        Assert.False(result.Ok);
        Assert.Equal(sourceBytes, File.ReadAllBytes(fake.Mod.ItemCfgPath));
    }

    [Fact]
    public void BootstrapWriteBack_RejectsSourceTargetOutsideSelectedMod()
    {
        using var fake = new FakeSdk(publishedId: "0");
        var staged = UploadStager.Stage(fake.Mod, fake.UgcToolPath);
        var expectedStaged = File.ReadAllText(staged.CfgPath);
        File.WriteAllText(
            staged.CfgPath,
            UploadStager.UpsertPublishedId(expectedStaged, "555"));
        using var outside = new TempDir();
        var outsideCfg = outside.Write("itemV2.cfg", File.ReadAllText(fake.Mod.ItemCfgPath));
        var escaped = new ModInfo
        {
            Name = fake.Mod.Name,
            ModDir = fake.Mod.ModDir,
            ItemCfgPath = outsideCfg,
        };
        var outsideBytes = File.ReadAllBytes(outsideCfg);

        var result = UploadStager.CompleteBootstrapWriteBack(
            staged, escaped, expectedStaged, Sha256(outsideBytes));

        Assert.False(result.Ok);
        Assert.Contains("escapes", result.Message);
        Assert.Equal(outsideBytes, File.ReadAllBytes(outsideCfg));
    }

    [Fact]
    public void UpsertPublishedId_RejectsNonDecimalId()
    {
        Assert.Throws<InvalidDataException>(() =>
            UploadStager.UpsertPublishedId("published_id = 0L;", "1; visibility = \"public\""));
    }

    [Fact]
    public void UpsertPublishedId_PreservesWhitespaceAndCommentBytes()
    {
        const string source = "  published_id   =   0L; // assigned by Steam\r\n";
        Assert.Equal(
            "  published_id   =   724L; // assigned by Steam\r\n",
            UploadStager.UpsertPublishedId(source, "724"));
    }

    [Fact]
    public void GetStagingDir_lives_under_uploader_folder()
    {
        var dir = UploadStager.GetStagingDir(@"C:\sdk\ugc_uploader");
        Assert.Equal(@"C:\sdk\ugc_uploader\sample_item", dir);
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
