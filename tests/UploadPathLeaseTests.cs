using VmbLauncher.Services;
using System.IO;

namespace VmbLauncher.Tests;

public class UploadPathLeaseTests
{
    [Fact]
    public void Capture_BlocksCfgContentPreviewAndToolMutation()
    {
        using var tmp = new TempDir();
        var staging = tmp.CreateSubdir(@"uploader\sample_item");
        var content = tmp.CreateSubdir(@"uploader\sample_item\content");
        var cfg = tmp.Write(@"uploader\sample_item\item.cfg", "content = \"content\";");
        var preview = tmp.Write(@"uploader\sample_item\preview.jpg", "preview-v1");
        var bundle = tmp.Write(@"uploader\sample_item\content\modx.mod", "bundle-v1");
        var tool = tmp.Write(@"uploader\ugc_tool.exe", "tool-v1");
        var staged = new StagedUpload(staging, cfg, "preview.jpg", 1);

        using var lease = UploadPathLease.Capture(staged, tool);

        Assert.ThrowsAny<IOException>(() => File.WriteAllText(cfg, "mutated"));
        Assert.ThrowsAny<IOException>(() => File.WriteAllText(preview, "mutated"));
        Assert.ThrowsAny<IOException>(() => File.WriteAllText(bundle, "mutated"));
        Assert.ThrowsAny<IOException>(() => File.WriteAllText(tool, "replacement"));
        Assert.ThrowsAny<IOException>(() =>
            Directory.Move(
                Path.GetDirectoryName(tool)!,
                Path.Combine(tmp.Path, "renamed-uploader")));
        Assert.ThrowsAny<UnauthorizedAccessException>(() =>
            File.WriteAllText(Path.Combine(content, "injected.mod_bundle"), "injected"));
        Assert.ThrowsAny<IOException>(() => File.Delete(bundle));
    }

    [Fact]
    public void Capture_BindsPreviewBytesAndAbsence()
    {
        using var tmp = new TempDir();
        var staging = tmp.CreateSubdir(@"uploader\sample_item");
        tmp.CreateSubdir(@"uploader\sample_item\content");
        var cfg = tmp.Write(@"uploader\sample_item\item.cfg", "content = \"content\";");
        tmp.Write(@"uploader\sample_item\content\modx.mod", "bundle-v1");
        var tool = tmp.Write(@"uploader\ugc_tool.exe", "tool-v1");

        using (var absent = UploadPathLease.Capture(
            new StagedUpload(staging, cfg, "preview.jpg", 1), tool))
        {
            Assert.False(absent.PreviewFile.Present);
            Assert.Equal("preview.jpg", absent.PreviewFile.Path);
            Assert.ThrowsAny<UnauthorizedAccessException>(() =>
                File.WriteAllText(Path.Combine(staging, "preview.jpg"), "late preview"));
        }

        var preview = tmp.Write(@"uploader\sample_item\preview.jpg", "preview-v1");
        using var present = UploadPathLease.Capture(
            new StagedUpload(staging, cfg, "preview.jpg", 1), tool);
        Assert.True(present.PreviewFile.Present);
        Assert.Equal(new FileInfo(preview).Length, present.PreviewFile.Length);
        Assert.Equal(64, present.PreviewFile.Sha256.Length);
    }

    [Fact]
    public void BootstrapBoundary_ReleasesOnlyCfgWhileOtherInputsStayPinned()
    {
        using var tmp = new TempDir();
        var staging = tmp.CreateSubdir(@"uploader\sample_item");
        var content = tmp.CreateSubdir(@"uploader\sample_item\content");
        var cfg = tmp.Write(@"uploader\sample_item\item.cfg", "published_id = 0L;");
        var preview = tmp.Write(@"uploader\sample_item\preview.jpg", "preview-v1");
        var bundle = tmp.Write(@"uploader\sample_item\content\modx.mod", "bundle-v1");
        var tool = tmp.Write(@"uploader\ugc_tool.exe", "tool-v1");

        using var lease = UploadPathLease.Capture(
            new StagedUpload(staging, cfg, "preview.jpg", 1), tool);
        lease.ReleaseCfgForBootstrapWrite();

        var replacement = Path.Combine(staging, "item.cfg.new");
        File.WriteAllText(replacement, "published_id = 724L;");
        File.Move(replacement, cfg, overwrite: true);
        Assert.ThrowsAny<IOException>(() => File.WriteAllText(preview, "mutated"));
        Assert.ThrowsAny<IOException>(() => File.WriteAllText(bundle, "mutated"));
        Assert.ThrowsAny<IOException>(() => File.WriteAllText(tool, "mutated"));
        Assert.ThrowsAny<UnauthorizedAccessException>(() =>
            File.WriteAllText(Path.Combine(content, "injected.mod_bundle"), "injected"));
        Assert.Throws<InvalidOperationException>(() =>
            lease.ReleaseCfgForBootstrapWrite());
    }

    [Fact]
    public void Dispose_RestoresDirectoryAclsAndReleasesEveryHandle()
    {
        using var tmp = new TempDir();
        var staging = tmp.CreateSubdir(@"uploader\sample_item");
        var content = tmp.CreateSubdir(@"uploader\sample_item\content");
        var cfg = tmp.Write(@"uploader\sample_item\item.cfg", "content = \"content\";");
        var preview = tmp.Write(@"uploader\sample_item\preview.jpg", "preview-v1");
        var bundle = tmp.Write(@"uploader\sample_item\content\modx.mod", "bundle-v1");
        var tool = tmp.Write(@"uploader\ugc_tool.exe", "tool-v1");

        using (UploadPathLease.Capture(
            new StagedUpload(staging, cfg, "preview.jpg", 1), tool))
        {
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(cfg, "blocked"));
            Assert.ThrowsAny<UnauthorizedAccessException>(() =>
                File.WriteAllText(Path.Combine(content, "blocked.mod_bundle"), "blocked"));
        }

        File.WriteAllText(cfg, "cfg-v2");
        File.WriteAllText(preview, "preview-v2");
        File.WriteAllText(bundle, "bundle-v2");
        File.WriteAllText(tool, "tool-v2");
        var added = Path.Combine(content, "added.mod_bundle");
        File.WriteAllText(added, "added");

        Assert.Equal("cfg-v2", File.ReadAllText(cfg));
        Assert.Equal("preview-v2", File.ReadAllText(preview));
        Assert.Equal("bundle-v2", File.ReadAllText(bundle));
        Assert.Equal("tool-v2", File.ReadAllText(tool));
        Assert.Equal("added", File.ReadAllText(added));
    }

    [Fact]
    public void CaptureFailure_RestoresAclsAndReleasesPartialHandles()
    {
        using var tmp = new TempDir();
        var staging = tmp.CreateSubdir(@"uploader\sample_item");
        var content = tmp.CreateSubdir(@"uploader\sample_item\content");
        var cfg = tmp.Write(@"uploader\sample_item\item.cfg", "content = \"content\";");
        var bundle = tmp.Write(@"uploader\sample_item\content\modx.mod", "bundle-v1");
        tmp.CreateSubdir("uploader");
        var missingTool = Path.Combine(tmp.Path, @"uploader\missing_ugc_tool.exe");

        Assert.ThrowsAny<IOException>(() => UploadPathLease.Capture(
            new StagedUpload(staging, cfg, "preview.jpg", 1), missingTool));

        File.WriteAllText(cfg, "cfg-v2");
        File.WriteAllText(bundle, "bundle-v2");
        var added = Path.Combine(content, "added.mod_bundle");
        File.WriteAllText(added, "added");

        Assert.Equal("cfg-v2", File.ReadAllText(cfg));
        Assert.Equal("bundle-v2", File.ReadAllText(bundle));
        Assert.Equal("added", File.ReadAllText(added));
    }
}
