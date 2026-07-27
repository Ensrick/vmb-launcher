using System.Diagnostics;
using System.IO;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public class CommitPublicationSnapshotTests
{
    [Fact]
    public void ReadCommitSnapshot_IgnoresHeadAndWorkingTreeSwap()
    {
        using var tmp = new TempDir();
        SeedMod(tmp, "preview-a", "bundle-a", "123");
        Git(tmp.Path, "init");
        Git(tmp.Path, "config", "user.email", "tests@example.invalid");
        Git(tmp.Path, "config", "user.name", "VMBLauncher Tests");
        Git(tmp.Path, "add", ".");
        Git(tmp.Path, "commit", "-m", "commit-a");
        var commitA = Git(tmp.Path, "rev-parse", "HEAD");
        var cfgBlobA = Git(tmp.Path, "rev-parse", $"{commitA}:modx/itemV2.cfg");

        SeedMod(tmp, "preview-b", "bundle-b", "999");
        Git(tmp.Path, "add", ".");
        Git(tmp.Path, "commit", "-m", "commit-b");
        Assert.NotEqual(commitA, Git(tmp.Path, "rev-parse", "HEAD"));
        tmp.Write(@"modx\preview.jpg", "uncommitted-preview-c");
        tmp.Write(@"modx\bundleV2\modx.mod", "uncommitted-bundle-c");

        var snapshot = PublicationReceiptGate.ReadCommitSnapshot(tmp.Path, commitA, "modx");

        Assert.Equal(cfgBlobA, snapshot.ItemCfgGitBlob);
        Assert.Equal("123", snapshot.PublishedId);
        Assert.Equal("1.2.3-dev", snapshot.Version);
        Assert.Equal(Sha256("preview-a"), snapshot.PreviewFile.Sha256);
        Assert.Equal(Sha256("bundle-a"), Assert.Single(snapshot.BundleFiles).Sha256);
        Assert.All(snapshot.BundleFiles, file => Assert.Matches("^[0-9a-f]{40}$", file.GitBlob));
    }

    [Fact]
    public void ReadCommitSnapshot_ReportsFirstUploadSentinelFromSelectedCommit()
    {
        using var tmp = new TempDir();
        SeedMod(tmp, "preview-a", "bundle-a", "0");
        Git(tmp.Path, "init");
        Git(tmp.Path, "config", "user.email", "tests@example.invalid");
        Git(tmp.Path, "config", "user.name", "VMBLauncher Tests");
        Git(tmp.Path, "add", ".");
        Git(tmp.Path, "commit", "-m", "bootstrap-required");

        var snapshot = PublicationReceiptGate.ReadCommitSnapshot(
            tmp.Path, Git(tmp.Path, "rev-parse", "HEAD"), "modx");

        Assert.Equal("0", snapshot.PublishedId);
    }

    [Fact]
    public void CommitProofRejectsStagingBuiltFromMutatedWorkingTree()
    {
        using var repo = new TempDir();
        SeedMod(repo, "preview-a", "bundle-a", "123");
        Git(repo.Path, "init");
        Git(repo.Path, "config", "user.email", "tests@example.invalid");
        Git(repo.Path, "config", "user.name", "VMBLauncher Tests");
        Git(repo.Path, "add", ".");
        Git(repo.Path, "commit", "-m", "authorized");
        var commit = Git(repo.Path, "rev-parse", "HEAD");
        var committed = PublicationReceiptGate.ReadCommitSnapshot(
            repo.Path, commit, "modx");

        // This is the attack the old path permitted: keep the authorized commit
        // but replace mutable worktree inputs before Stage().
        SeedMod(repo, "preview-evil", "bundle-evil", "123");
        var mod = new ModInfo
        {
            Name = "modx",
            ModDir = Path.Combine(repo.Path, "modx"),
            ItemCfgPath = Path.Combine(repo.Path, "modx", "itemV2.cfg"),
        };
        ModDiscovery.ParseItemCfg(mod);
        using var sdk = new TempDir();
        var uploader = sdk.CreateSubdir("ugc_uploader");
        var tool = sdk.Write(@"ugc_uploader\ugc_tool.exe", "tool");
        var staged = UploadStager.Stage(mod, tool);
        using var lease = UploadPathLease.Capture(staged, tool);

        var now = new DateTime(2026, 7, 27, 1, 0, 0, DateTimeKind.Utc);
        var owner = "codex:724";
        var receipt = new PublicationReceipt
        {
            Schema = PublicationReceiptGate.Schema,
            Purpose = "workshop_upload",
            Nonce = "0123456789abcdef0123456789abcdef",
            IssuedAtUtc = now.AddMinutes(-1),
            ExpiresAtUtc = now.AddMinutes(4),
            Repository = PublicationReceiptGate.GitHubRepo,
            ReleaseTag = "mods-2026-07-27",
            ReceiptAssetName = "publication-receipt-modx.json",
            SourceRoot = repo.Path,
            SourceCommit = commit,
            Mod = "modx",
            Version = committed.Version,
            Owner = owner,
            ItemCfgSha256 = committed.ItemCfgSha256,
            ItemCfgGitBlob = committed.ItemCfgGitBlob,
            BundleFiles = committed.BundleFiles.ToList(),
            PreviewFile = committed.PreviewFile,
            Authorization = new PublicationAuthorization
            {
                Mode = "hosted_qa",
                SourceCommit = commit,
                DefaultBranch = "master",
                DefaultBranchCommit = commit,
                MergedPrNumber = 724,
                QaCheck = "qa-gate",
                QaCheckUrl = "https://example.invalid/qa/724",
                QaCompletedAtUtc = now.AddMinutes(-2),
            },
        };
        var expectedCfg = UploadStager.BuildStagedCfgTextFromSourceCfg(
            committed.ItemCfgText, "modx", committed.PreviewFile.Path);
        var result = PublicationReceiptGate.EvaluateSnapshot(
            receipt,
            new LivePublicationSnapshot(
                repo.Path, commit, false, "master", commit, 724,
                receipt.Authorization.QaCheckUrl,
                receipt.Authorization.QaCompletedAtUtc),
            "modx",
            committed.Version,
            owner,
            now,
            new string('f', 64),
            new string('f', 64),
            committed.ItemCfgSha256,
            committed.ItemCfgGitBlob,
            committed.PublishedId,
            committed.BundleFiles,
            committed.PreviewFile,
            Sha256(expectedCfg),
            lease.CfgSha256,
            lease.BundleFiles,
            lease.PreviewFile,
            new ShipClaimGate.Evaluation(
                ShipClaimGate.Verdict.Match,
                new ShipClaimGate.ClaimInfo(
                    "modx", committed.Version, owner, now.AddMinutes(-2)),
                null));

        Assert.False(result.Ok);
        Assert.Contains("SDK-staged content", result.Message);
    }

    [Fact]
    public void BootstrapWriteBackRepositoryRejectsHeadSwap()
    {
        using var repo = new TempDir();
        SeedMod(repo, "preview-a", "bundle-a", "0");
        Git(repo.Path, "init");
        Git(repo.Path, "config", "user.email", "tests@example.invalid");
        Git(repo.Path, "config", "user.name", "VMBLauncher Tests");
        Git(repo.Path, "add", ".");
        Git(repo.Path, "commit", "-m", "authorized-bootstrap");
        var authorized = Git(repo.Path, "rev-parse", "HEAD");

        var before = PublicationReceiptGate.ValidateBootstrapWriteBackRepository(
            Path.Combine(repo.Path, "modx"), repo.Path, authorized);
        Assert.True(before.Ok, before.Message);

        repo.Write(@"modx\unrelated.txt", "head swap");
        Git(repo.Path, "add", ".");
        Git(repo.Path, "commit", "-m", "swapped");
        var after = PublicationReceiptGate.ValidateBootstrapWriteBackRepository(
            Path.Combine(repo.Path, "modx"), repo.Path, authorized);
        Assert.False(after.Ok);
        Assert.Contains("HEAD changed", after.Message);
    }

    private static void SeedMod(TempDir tmp, string preview, string bundle, string publishedId)
    {
        tmp.Write(
            @"modx\itemV2.cfg",
            "title = \"Mod X v1.2.3-dev\";\n" +
            "description = \"fixture\";\n" +
            "preview = \"preview.jpg\";\n" +
            "content = \"bundleV2\";\n" +
            "language = \"english\";\n" +
            "visibility = \"private\";\n" +
            $"published_id = {publishedId}L;\n");
        tmp.Write(@"modx\preview.jpg", preview);
        tmp.Write(@"modx\bundleV2\modx.mod", bundle);
        tmp.Write(
            @"modx\scripts\mods\modx\modx.lua",
            "local MOD_VERSION = \"1.2.3-dev\"\n");
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Git(string root, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Could not start git.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}");
        return stdout.Trim();
    }
}
