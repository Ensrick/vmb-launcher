using VmbLauncher.Services;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace VmbLauncher.Tests;

public class UploadPathLeaseTests
{
    private static UploadPathLease Capture(StagedUpload staged, string tool)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(staged.StagingDir))
            ?? throw new InvalidOperationException("fixture staging root is invalid");
        using var transaction = MachineTransactionLease.Enter(
            "upload-acl-fixture", mod: null, root,
            recordPath: Path.Combine(root, "fixture-owner.json"),
            mutexName: @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N"));
        return UploadPathLease.Capture(staged, tool);
    }

    [Fact]
    public void DeadOwnerJournalRecoversExactOriginalAclBeforeNextStage()
    {
        using var tmp = new TempDir();
        var staging = tmp.CreateSubdir(@"uploader\sample_item");
        var content = tmp.CreateSubdir(@"uploader\sample_item\content");
        var cfg = tmp.Write(@"uploader\sample_item\item.cfg", "content = \"content\";");
        tmp.Write(@"uploader\sample_item\content\modx.mod", "bundle-v1");
        var tool = tmp.Write(@"uploader\ugc_tool.exe", "tool-v1");
        var originalStaging = GetAclBytes(staging);
        var originalContent = GetAclBytes(content);

        var crashed = Capture(
            new StagedUpload(staging, cfg, "preview.jpg", 1), tool);
        crashed.AbandonForCrashFixture();

        Assert.False(GetAclBytes(staging).SequenceEqual(originalStaging));
        Assert.False(GetAclBytes(content).SequenceEqual(originalContent));

        var record = System.IO.Path.Combine(tmp.Path, "transaction.json");
        using (MachineTransactionLease.Enter(
            "upload", "modx", tmp.Path, timeout: TimeSpan.FromSeconds(1),
            recordPath: record,
            mutexName: @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N")))
        {
            UploadPathLease.RecoverStaleAclLease(tool);
            // Recovery is idempotent: with no journal or exact legacy ACE,
            // the next startup is a no-op.
            UploadPathLease.RecoverStaleAclLease(tool);
        }

        Assert.Equal(originalStaging, GetAclBytes(staging));
        Assert.Equal(originalContent, GetAclBytes(content));
        File.WriteAllText(System.IO.Path.Combine(content, "next-stage.mod"), "ok");
    }

    [Fact]
    public void PartiallyRestoredMultiDirectoryJournalCompletesRecovery()
    {
        using var tmp = new TempDir();
        var staging = tmp.CreateSubdir(@"uploader\sample_item");
        var content = tmp.CreateSubdir(@"uploader\sample_item\content");
        var nested = tmp.CreateSubdir(@"uploader\sample_item\content\nested");
        var cfg = tmp.Write(@"uploader\sample_item\item.cfg", "content = \"content\";");
        tmp.Write(@"uploader\sample_item\content\nested\modx.mod", "bundle-v1");
        var tool = tmp.Write(@"uploader\ugc_tool.exe", "tool-v1");
        var originals = new Dictionary<string, byte[]>
        {
            [staging] = GetAclBytes(staging),
            [content] = GetAclBytes(content),
            [nested] = GetAclBytes(nested),
        };

        var crashed = Capture(
            new StagedUpload(staging, cfg, "preview.jpg", 1), tool);
        crashed.AbandonForCrashFixture(restoreDirectoryCount: 1);

        using (MachineTransactionLease.Enter(
            "upload", "modx", tmp.Path, timeout: TimeSpan.FromSeconds(1),
            recordPath: System.IO.Path.Combine(tmp.Path, "transaction.json"),
            mutexName: @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N")))
        {
            UploadPathLease.RecoverStaleAclLease(tool);
        }

        foreach (var pair in originals)
            Assert.Equal(pair.Value, GetAclBytes(pair.Key));
    }

    [Fact]
    public void LegacyRecoveryIncludesEveryRecursivelyFrozenDirectory()
    {
        using var tmp = new TempDir();
        var staging = tmp.CreateSubdir(@"uploader\sample_item");
        var content = tmp.CreateSubdir(@"uploader\sample_item\content");
        var nested = tmp.CreateSubdir(@"uploader\sample_item\content\nested\deeper");
        var cfg = tmp.Write(@"uploader\sample_item\item.cfg", "content = \"content\";");
        tmp.Write(@"uploader\sample_item\content\nested\deeper\modx.mod", "bundle-v1");
        var tool = tmp.Write(@"uploader\ugc_tool.exe", "tool-v1");
        var originals = new[] { staging, content, Path.GetDirectoryName(nested)!, nested }
            .ToDictionary(path => path, GetAclBytes);

        var crashed = Capture(
            new StagedUpload(staging, cfg, "preview.jpg", 1), tool);
        crashed.AbandonForCrashFixture();
        File.Delete(Path.Combine(tmp.Path, @"uploader\.vmblauncher-upload-acl-lease.json"));

        using (MachineTransactionLease.Enter(
            "upload", "modx", tmp.Path,
            recordPath: Path.Combine(tmp.Path, "transaction.json"),
            mutexName: @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N")))
            UploadPathLease.RecoverStaleAclLease(tool);

        foreach (var pair in originals) Assert.Equal(pair.Value, GetAclBytes(pair.Key));
    }

    [Fact]
    public void JournalIsDurableBeforeFirstDirectoryFreeze()
    {
        using var tmp = new TempDir();
        var staging = tmp.CreateSubdir(@"uploader\sample_item");
        var content = tmp.CreateSubdir(@"uploader\sample_item\content");
        var cfg = tmp.Write(@"uploader\sample_item\item.cfg", "content = \"content\";");
        tmp.Write(@"uploader\sample_item\content\modx.mod", "bundle-v1");
        var tool = tmp.Write(@"uploader\ugc_tool.exe", "tool-v1");
        var originalRoot = GetAclBytes(staging);
        var originalContent = GetAclBytes(content);
        var observed = false;
        UploadPathLease.JournalDurableBeforeFreezeForTest = path =>
        {
            observed = true;
            Assert.True(File.Exists(path));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.True(exclusive.Length > 0);
            Assert.Equal(originalRoot, GetAclBytes(staging));
            Assert.Equal(originalContent, GetAclBytes(content));
        };
        try
        {
            using var lease = Capture(
                new StagedUpload(staging, cfg, "preview.jpg", 1), tool);
            Assert.True(observed);
        }
        finally { UploadPathLease.JournalDurableBeforeFreezeForTest = null; }
    }

    [Fact]
    public void NewTransactionInSameLongLivedProcessRecoversPriorLeaseJournal()
    {
        using var tmp = new TempDir();
        var staging = tmp.CreateSubdir(@"uploader\sample_item");
        var content = tmp.CreateSubdir(@"uploader\sample_item\content");
        var cfg = tmp.Write(@"uploader\sample_item\item.cfg", "content = \"content\";");
        tmp.Write(@"uploader\sample_item\content\modx.mod", "bundle-v1");
        var tool = tmp.Write(@"uploader\ugc_tool.exe", "tool-v1");
        var rootAcl = GetAclBytes(staging);
        var contentAcl = GetAclBytes(content);
        var mutex = @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N");
        var record = Path.Combine(tmp.Path, "transaction.json");

        using (MachineTransactionLease.Enter(
            "old-upload", "modx", tmp.Path, recordPath: record, mutexName: mutex))
        {
            var failed = Capture(
                new StagedUpload(staging, cfg, "preview.jpg", 1), tool);
            failed.AbandonForCrashFixture(deadOwnerPid: null);
        }

        using (MachineTransactionLease.Enter(
            "new-upload", "modx", tmp.Path, recordPath: record, mutexName: mutex))
            UploadPathLease.RecoverStaleAclLease(tool);

        Assert.Equal(rootAcl, GetAclBytes(staging));
        Assert.Equal(contentAcl, GetAclBytes(content));
    }

    [Theory]
    [InlineData("owner_session_id")]
    [InlineData("owner_sid")]
    public void ForeignSessionOrSidJournalFailsClosedWithoutAclMutation(string field)
    {
        using var tmp = new TempDir();
        var staging = tmp.CreateSubdir(@"uploader\sample_item");
        var content = tmp.CreateSubdir(@"uploader\sample_item\content");
        var cfg = tmp.Write(@"uploader\sample_item\item.cfg", "content = \"content\";");
        tmp.Write(@"uploader\sample_item\content\modx.mod", "bundle-v1");
        var tool = tmp.Write(@"uploader\ugc_tool.exe", "tool-v1");
        var failed = Capture(
            new StagedUpload(staging, cfg, "preview.jpg", 1), tool);
        failed.AbandonForCrashFixture();
        var journalPath = Path.Combine(tmp.Path, @"uploader\.vmblauncher-upload-acl-lease.json");
        var authentic = File.ReadAllText(journalPath);
        var node = JsonNode.Parse(authentic)!.AsObject();
        if (field == "owner_session_id") node[field] = int.MaxValue;
        else node[field] = "S-1-5-18";
        File.WriteAllText(journalPath, node.ToJsonString());
        var frozenRoot = GetAclBytes(staging);
        var frozenContent = GetAclBytes(content);

        using (MachineTransactionLease.Enter(
            "upload", "modx", tmp.Path,
            recordPath: Path.Combine(tmp.Path, "transaction.json"),
            mutexName: @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N")))
        {
            Assert.Throws<InvalidDataException>(() => UploadPathLease.RecoverStaleAclLease(tool));
            Assert.Equal(frozenRoot, GetAclBytes(staging));
            Assert.Equal(frozenContent, GetAclBytes(content));
            File.WriteAllText(journalPath, authentic);
            UploadPathLease.RecoverStaleAclLease(tool);
        }
    }

    [Fact]
    public void UnfrozenToolDirectoryLeaseDoesNotRestoreOverUnrelatedAclChange()
    {
        using var tmp = new TempDir();
        var uploader = tmp.CreateSubdir("uploader");
        var staging = tmp.CreateSubdir(@"uploader\sample_item");
        tmp.CreateSubdir(@"uploader\sample_item\content");
        var cfg = tmp.Write(@"uploader\sample_item\item.cfg", "content = \"content\";");
        tmp.Write(@"uploader\sample_item\content\modx.mod", "bundle-v1");
        var tool = tmp.Write(@"uploader\ugc_tool.exe", "tool-v1");
        var lease = Capture(
            new StagedUpload(staging, cfg, "preview.jpg", 1), tool);
        var security = FileSystemAclExtensions.GetAccessControl(
            new DirectoryInfo(uploader), AccessControlSections.Access);
        var added = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadAttributes,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow);
        security.AddAccessRule(added);
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(uploader), security);
        var changed = GetAclBytes(uploader);

        lease.Dispose();
        Assert.Equal(changed, GetAclBytes(uploader));
    }

    [Fact]
    public void LegacyRecoveryValidatesSecondCandidateBeforeMutatingFirst()
    {
        using var tmp = new TempDir();
        var staging = tmp.CreateSubdir(@"uploader\sample_item");
        var content = tmp.CreateSubdir(@"uploader\sample_item\content");
        var cfg = tmp.Write(@"uploader\sample_item\item.cfg", "content = \"content\";");
        tmp.Write(@"uploader\sample_item\content\modx.mod", "bundle-v1");
        var tool = tmp.Write(@"uploader\ugc_tool.exe", "tool-v1");

        // Unrelated explicit child semantics make legacy inference ambiguous.
        var childSecurity = FileSystemAclExtensions.GetAccessControl(
            new DirectoryInfo(content), AccessControlSections.Access);
        childSecurity.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ReadAttributes,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(content), childSecurity);

        var crashed = Capture(
            new StagedUpload(staging, cfg, "preview.jpg", 1), tool);
        crashed.AbandonForCrashFixture();
        var journalPath = Path.Combine(tmp.Path, @"uploader\.vmblauncher-upload-acl-lease.json");
        var journalBytes = File.ReadAllBytes(journalPath);
        File.Delete(journalPath); // force the one-time v0.5.9 inference lane
        var frozenParent = GetAclBytes(staging);

        using (MachineTransactionLease.Enter(
            "upload", "modx", tmp.Path, timeout: TimeSpan.FromSeconds(1),
            recordPath: Path.Combine(tmp.Path, "transaction.json"),
            mutexName: @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N")))
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                UploadPathLease.RecoverStaleAclLease(tool));
            Assert.Contains("parent-child Access semantics", ex.Message);
            Assert.Equal(frozenParent, GetAclBytes(staging));

            // Restore the authenticated journal so fixture cleanup also proves
            // the ordinary recovery lane preserves the unrelated child ACE.
            File.WriteAllBytes(journalPath, journalBytes);
            UploadPathLease.RecoverStaleAclLease(tool);
        }
    }

    [Fact]
    public void LegacyRecoveryRejectsLoneExactLikeDenyWithoutMutation()
    {
        using var tmp = new TempDir();
        var staging = tmp.CreateSubdir(@"uploader\sample_item");
        var content = tmp.CreateSubdir(@"uploader\sample_item\content");
        var cfg = tmp.Write(@"uploader\sample_item\item.cfg", "content = \"content\";");
        tmp.Write(@"uploader\sample_item\content\modx.mod", "bundle-v1");
        var tool = tmp.Write(@"uploader\ugc_tool.exe", "tool-v1");

        var crashed = Capture(new StagedUpload(staging, cfg, "preview.jpg", 1), tool);
        crashed.AbandonForCrashFixture();
        var journalPath = Path.Combine(tmp.Path, @"uploader\.vmblauncher-upload-acl-lease.json");
        var journalBytes = File.ReadAllBytes(journalPath);
        File.Delete(journalPath);

        // Remove only content's planted launcher deny. The coincidentally exact
        // deny left on sample_item must not be treated as a v0.5.9 signature.
        var contentSecurity = FileSystemAclExtensions.GetAccessControl(
            new DirectoryInfo(content), AccessControlSections.Access);
        foreach (var rule in contentSecurity
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Deny &&
                WindowsIdentity.GetCurrent().User!.Equals(rule.IdentityReference))
            .ToArray())
            contentSecurity.RemoveAccessRuleSpecific(rule);
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(content), contentSecurity);
        var frozenRoot = GetAclBytes(staging);
        var alteredContent = GetAclBytes(content);

        using (MachineTransactionLease.Enter(
            "upload", "modx", tmp.Path, timeout: TimeSpan.FromSeconds(1),
            recordPath: Path.Combine(tmp.Path, "transaction.json"),
            mutexName: @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N")))
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                UploadPathLease.RecoverStaleAclLease(tool));
            Assert.Contains("both sample_item and content", ex.Message);
            Assert.Equal(frozenRoot, GetAclBytes(staging));
            Assert.Equal(alteredContent, GetAclBytes(content));

            File.WriteAllBytes(journalPath, journalBytes);
            UploadPathLease.RecoverStaleAclLease(tool);
        }
    }

    [Fact]
    public void LegacyRecoveryRejectsExactLikeDenyOnIndependentDeeperChildBeforeAnyWrite()
    {
        using var tmp = new TempDir();
        var staging = tmp.CreateSubdir(@"uploader\sample_item");
        var content = tmp.CreateSubdir(@"uploader\sample_item\content");
        var nested = tmp.CreateSubdir(@"uploader\sample_item\content\nested");
        var cfg = tmp.Write(@"uploader\sample_item\item.cfg", "content = \"content\";");
        tmp.Write(@"uploader\sample_item\content\nested\modx.mod", "bundle-v1");
        var tool = tmp.Write(@"uploader\ugc_tool.exe", "tool-v1");

        var crashed = Capture(new StagedUpload(staging, cfg, "preview.jpg", 1), tool);
        crashed.AbandonForCrashFixture();
        var journalPath = Path.Combine(tmp.Path, @"uploader\.vmblauncher-upload-acl-lease.json");
        var journalBytes = File.ReadAllBytes(journalPath);
        File.Delete(journalPath);

        var nestedSecurity = FileSystemAclExtensions.GetAccessControl(
            new DirectoryInfo(nested), AccessControlSections.Access);
        var independentRule = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadAttributes,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow);
        nestedSecurity.AddAccessRule(independentRule);
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(nested), nestedSecurity);
        var before = new[] { staging, content, nested }.ToDictionary(path => path, GetAclBytes);

        using (MachineTransactionLease.Enter(
            "upload", "modx", tmp.Path, timeout: TimeSpan.FromSeconds(1),
            recordPath: Path.Combine(tmp.Path, "transaction.json"),
            mutexName: @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N")))
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                UploadPathLease.RecoverStaleAclLease(tool));
            Assert.Contains("independent Access semantics", ex.Message);
            foreach (var pair in before) Assert.Equal(pair.Value, GetAclBytes(pair.Key));

            // Restore the exact frozen descriptor before using the authenticated
            // journal for fixture cleanup.
            nestedSecurity = FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(nested), AccessControlSections.Access);
            nestedSecurity.RemoveAccessRuleSpecific(independentRule);
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(nested), nestedSecurity);
            File.WriteAllBytes(journalPath, journalBytes);
            UploadPathLease.RecoverStaleAclLease(tool);
        }
    }

    private static byte[] GetAclBytes(string path) =>
        System.IO.FileSystemAclExtensions.GetAccessControl(
            new DirectoryInfo(path),
            System.Security.AccessControl.AccessControlSections.Access)
        .GetSecurityDescriptorBinaryForm();

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

        using var lease = Capture(staged, tool);

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

        using (var absent = Capture(
            new StagedUpload(staging, cfg, "preview.jpg", 1), tool))
        {
            Assert.False(absent.PreviewFile.Present);
            Assert.Equal("preview.jpg", absent.PreviewFile.Path);
            Assert.ThrowsAny<UnauthorizedAccessException>(() =>
                File.WriteAllText(Path.Combine(staging, "preview.jpg"), "late preview"));
        }

        var preview = tmp.Write(@"uploader\sample_item\preview.jpg", "preview-v1");
        using var present = Capture(
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

        using var lease = Capture(
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

        using (Capture(
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

        Assert.ThrowsAny<IOException>(() => Capture(
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
