using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public class PublicationReceiptGateTests : MutationTestBase
{
    private static readonly DateTime Now = new(2026, 7, 26, 18, 0, 0, DateTimeKind.Utc);
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string Owner = "codex:owner-724";
    private const string QaUrl = "https://example.invalid/check/724";
    private static readonly string ReceiptHash = new('d', 64);
    private static readonly string StagedCfgHash = new('e', 64);
    private static readonly string PreviewHash = new('f', 64);
    private static readonly string CfgBlob = new('1', 40);
    private static readonly string BundleBlobA = new('2', 40);
    private static readonly string BundleBlobB = new('3', 40);
    private static readonly string PreviewBlob = new('4', 40);

    private static PublicationReceipt Receipt() => new()
    {
        Schema = PublicationReceiptGate.Schema,
        Purpose = "workshop_upload",
        Nonce = "0123456789abcdef0123456789abcdef",
        IssuedAtUtc = Now.AddMinutes(-1),
        ExpiresAtUtc = Now.AddMinutes(4),
        Repository = PublicationReceiptGate.GitHubRepo,
        ReleaseTag = "mods-2026-07-26",
        ReceiptAssetName = "publication-receipt-modx.json",
        SourceRoot = @"C:\repo",
        SourceCommit = Sha,
        Mod = "modx",
        Version = "1.2.3-dev",
        Owner = Owner,
        ItemCfgSha256 = new string('a', 64),
        ItemCfgGitBlob = CfgBlob,
        BundleFiles =
        {
            new PublicationBundleFile { Path = "modx.mod", Length = 10, Sha256 = new string('b', 64), GitBlob = BundleBlobA },
            new PublicationBundleFile { Path = "root.mod_bundle", Length = 20, Sha256 = new string('c', 64), GitBlob = BundleBlobB },
        },
        PreviewFile = new PublicationPreviewFile
        {
            Path = "preview.jpg",
            Present = true,
            Length = 30,
            Sha256 = PreviewHash,
            GitBlob = PreviewBlob,
        },
        Authorization = new PublicationAuthorization
        {
            Mode = "hosted_qa",
            SourceCommit = Sha,
            CheckedAtUtc = Now.AddMinutes(-1),
            DefaultBranch = "master",
            DefaultBranchCommit = Sha,
            MergedPrNumber = 724,
            QaCheck = "qa-gate",
            QaCheckUrl = QaUrl,
            QaCompletedAtUtc = Now.AddMinutes(-2),
        },
    };

    private static LivePublicationSnapshot Live(bool clean = true, string? defaultHead = null, int mergedPr = 724) =>
        new(@"C:\repo", Sha, clean, "master", defaultHead ?? Sha, mergedPr, QaUrl, Now.AddMinutes(-2));

    private static IReadOnlyList<PublicationBundleFile> Bundles() =>
        Receipt().BundleFiles.Select(x => new PublicationBundleFile
        {
            Path = x.Path,
            Length = x.Length,
            Sha256 = x.Sha256,
            GitBlob = x.GitBlob,
        }).ToList();

    private static PublicationPreviewFile Preview() => new()
    {
        Path = "preview.jpg",
        Present = true,
        Length = 30,
        Sha256 = PreviewHash,
        GitBlob = PreviewBlob,
    };

    private static PublicationBundleAuthorityProof ReceiptAuthorityProof() => new()
    {
        Authority = "receipt",
        SourceCommit = Sha,
        InventoryGitBlob = new string('5', 40),
        IgnoreGitBlob = new string('6', 40),
        RootBundle = "0123456789abcdef.mod_bundle",
        ByteSource = "materialized_restrictive_handles",
        BuildReceiptPath = "modx/.build-receipt.json",
        BuildReceiptGitBlob = new string('7', 40),
        BuildReceiptSha256 = new string('8', 64),
        ReceiptSchema = 3,
        SourceFingerprintSha256 = new string('9', 64),
        OutputAlgorithm = "vt2-normalized-bundle-output-set-sha256-v1",
        OutputFingerprintSha256 = new string('a', 64),
        BuilderName = "VMBLauncher",
        BuilderVersion = typeof(PublicationReceiptGate).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion,
        NormalizationPolicyAlgorithm = "exact-build-artifact-exclusions-sha256-v1",
        NormalizationPolicyFingerprintSha256 = new string('b', 64),
    };

    private static ShipClaimGate.Evaluation MatchingClaim() =>
        new(
            ShipClaimGate.Verdict.Match,
            new ShipClaimGate.ClaimInfo("modx", "1.2.3-dev", Owner, Now.AddHours(-1)),
            null);

    [Fact]
    public void AuthenticateHostedReceipt_RejectsCallerBytesBeforeUsingHostedAuthority()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.AuthenticateHostedReceipt(
                "caller"u8.ToArray(),
                "hosted"u8.ToArray()));

        Assert.Contains("do not match", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthenticateHostedReceipt_RejectsOversizedCallerAndHostedBytesBeforeJsonParsing()
    {
        var oversized = new byte[PublicationReceiptGate.MaximumReceiptBytes + 1];

        var callerError = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.AuthenticateHostedReceipt(
                oversized,
                Array.Empty<byte>()));
        var hostedError = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.AuthenticateHostedReceipt(
                Array.Empty<byte>(),
                oversized));

        Assert.Contains("Caller receipt", callerError.Message, StringComparison.Ordinal);
        Assert.Contains("Hosted receipt", hostedError.Message, StringComparison.Ordinal);
        Assert.Contains("8-MiB", callerError.Message, StringComparison.Ordinal);
        Assert.Contains("8-MiB", hostedError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthenticateHostedReceipt_RejectsOutputMapOver4096BeforeAuthorityFingerprinting()
    {
        var receipt = Receipt();
        receipt.BundleFiles = Enumerable.Range(
                0,
                PublicationReceiptGate.MaximumSemanticMapEntries + 1)
            .Select(index => new PublicationBundleFile
            {
                Path = $"{index:x16}.mod_bundle",
                Length = 1,
                Sha256 = new string('a', 64),
                GitBlob = new string('b', 40),
            })
            .ToList();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt);

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.AuthenticateHostedReceipt(bytes, bytes));

        Assert.Contains("4096-entry", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthenticateHostedReceipt_RejectsOutputMapOver32GiBBeforeAuthorityFingerprinting()
    {
        var receipt = Receipt();
        receipt.BundleFiles = new()
        {
            new PublicationBundleFile
            {
                Path = "modx.mod",
                Length = PublicationReceiptGate.MaximumSemanticMapBytes,
                Sha256 = new string('a', 64),
                GitBlob = new string('b', 40),
            },
            new PublicationBundleFile
            {
                Path = "0123456789abcdef.mod_bundle",
                Length = 1,
                Sha256 = new string('c', 64),
                GitBlob = new string('d', 40),
            },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt);

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.AuthenticateHostedReceipt(bytes, bytes));

        Assert.Contains("32-GiB", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthenticateHostedReceipt_AcceptsExactExplicitReceiptAuthorityShape()
    {
        var receipt = Receipt();
        receipt.BundleAuthority = "receipt";
        receipt.BundleAuthorityProof = ReceiptAuthorityProof();
        foreach (var bundle in receipt.BundleFiles) bundle.GitBlob = "";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt);

        var authenticated = PublicationReceiptGate.AuthenticateHostedReceipt(bytes, bytes);

        Assert.Equal("receipt", authenticated.BundleAuthority);
        Assert.Equal(3, authenticated.BundleAuthorityProof!.ReceiptSchema);
    }

    [Fact]
    public void AuthenticateHostedReceipt_RejectsIncompleteExplicitAuthorityProofShape()
    {
        var receipt = Receipt();
        receipt.BundleAuthority = "receipt";
        receipt.BundleAuthorityProof = ReceiptAuthorityProof();
        var root = JsonNode.Parse(JsonSerializer.Serialize(receipt))!.AsObject();
        Assert.True(root["bundle_authority_proof"]!.AsObject().Remove("builder_version"));
        var bytes = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.AuthenticateHostedReceipt(bytes, bytes));

        Assert.Contains("properties are missing", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static PublicationGateResult Evaluate(
        PublicationReceipt? receipt = null,
        LivePublicationSnapshot? live = null,
        ShipClaimGate.Evaluation? claim = null,
        string? callerReceiptHash = null,
        string? hostedReceiptHash = null,
        IReadOnlyList<PublicationBundleFile>? sourceBundles = null,
        string? expectedStagedCfgHash = null,
        string? actualStagedCfgHash = null,
        IReadOnlyList<PublicationBundleFile>? stagedBundles = null,
        PublicationPreviewFile? sourcePreview = null,
        PublicationPreviewFile? stagedPreview = null,
        string sourcePublishedId = "123",
        PublicationBundleAuthorityProof? sourceAuthorityProof = null) =>
        PublicationReceiptGate.EvaluateSnapshot(
            receipt ?? Receipt(),
            live ?? Live(),
            "modx",
            "1.2.3-dev",
            Owner,
            Now,
            callerReceiptHash ?? ReceiptHash,
            hostedReceiptHash ?? ReceiptHash,
            new string('a', 64),
            CfgBlob,
            sourcePublishedId,
            sourceBundles ?? Bundles(),
            sourcePreview ?? Preview(),
            expectedStagedCfgHash ?? StagedCfgHash,
            actualStagedCfgHash ?? StagedCfgHash,
            stagedBundles ?? Bundles(),
            stagedPreview ?? Preview(),
            claim ?? MatchingClaim(),
            sourceAuthorityProof);

    [Fact]
    public void EvaluateSnapshot_AcceptsExactHostedReceiptAndStaging()
    {
        Assert.True(Evaluate().Ok);
    }

    [Fact]
    public void EvaluateCommitQualifiedSnapshot_AcceptsDistinctLocalDeployPurposeWithoutUploadStaging()
    {
        var receipt = Receipt();
        receipt.Purpose = "local_deploy";
        receipt.ReceiptAssetName = "deployment-receipt-modx.json";
        receipt.BundleAuthority = "receipt";
        receipt.BundleAuthorityProof = ReceiptAuthorityProof();
        foreach (var file in receipt.BundleFiles) file.GitBlob = "";
        var source = Bundles();
        foreach (var file in source) file.GitBlob = "";

        var result = PublicationReceiptGate.EvaluateCommitQualifiedSnapshot(
            receipt,
            Live(),
            "modx",
            "1.2.3-dev",
            Owner,
            Now,
            ReceiptHash,
            ReceiptHash,
            new string('a', 64),
            CfgBlob,
            "123",
            source,
            Preview(),
            "local_deploy",
            MatchingClaim(),
            ReceiptAuthorityProof());

        Assert.True(result.Ok, result.Message);
        Assert.Null(result.Verified);
    }

    [Fact]
    public void EvaluateCommitQualifiedSnapshot_RejectsLocalDeployAssetForAnotherMod()
    {
        var receipt = Receipt();
        receipt.Purpose = "local_deploy";
        receipt.ReceiptAssetName = "deployment-receipt-other_mod.json";
        receipt.BundleAuthority = "receipt";
        receipt.BundleAuthorityProof = ReceiptAuthorityProof();
        foreach (var file in receipt.BundleFiles) file.GitBlob = "";
        var source = Bundles();
        foreach (var file in source) file.GitBlob = "";

        var result = PublicationReceiptGate.EvaluateCommitQualifiedSnapshot(
            receipt,
            Live(),
            "modx",
            "1.2.3-dev",
            Owner,
            Now,
            ReceiptHash,
            ReceiptHash,
            new string('a', 64),
            CfgBlob,
            "123",
            source,
            Preview(),
            "local_deploy",
            MatchingClaim(),
            ReceiptAuthorityProof());

        Assert.False(result.Ok);
        Assert.Contains("coordinates", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateCommitQualifiedSnapshot_RejectsPublicationAssetForLocalDeploy()
    {
        var receipt = Receipt();
        receipt.Purpose = "local_deploy";

        var result = PublicationReceiptGate.EvaluateCommitQualifiedSnapshot(
            receipt,
            Live(),
            "modx",
            "1.2.3-dev",
            Owner,
            Now,
            ReceiptHash,
            ReceiptHash,
            new string('a', 64),
            CfgBlob,
            "123",
            Bundles(),
            Preview(),
            "local_deploy",
            MatchingClaim());

        Assert.False(result.Ok);
        Assert.Contains("coordinates", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSnapshot_DoesNotMintUploadCapabilityWithoutPinnedPaths()
    {
        var result = Evaluate();
        Assert.Null(result.Verified);
    }

    [Fact]
    public void EvaluateSnapshot_RejectsCanonicalLookingCallerForgedObject()
    {
        var forged = Receipt();
        forged.Nonce = "ffffffffffffffffffffffffffffffff";
        var result = Evaluate(
            receipt: forged,
            callerReceiptHash: new string('f', 64),
            hostedReceiptHash: ReceiptHash);
        Assert.False(result.Ok);
        Assert.Contains("GitHub release asset", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSnapshot_DoesNotUseMutableWorktreeCleanliness()
    {
        var result = Evaluate(live: Live(clean: false));
        Assert.True(result.Ok);
    }

    [Fact]
    public void EvaluateSnapshot_RejectsUntypedAuthorityProof()
    {
        var receipt = Receipt();
        receipt.BundleAuthorityProof = ReceiptAuthorityProof();

        var result = Evaluate(receipt: receipt);

        Assert.False(result.Ok);
        Assert.Contains("untyped", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSnapshot_RejectsPremergeCommit()
    {
        var result = Evaluate(live: Live(mergedPr: 0));
        Assert.False(result.Ok);
        Assert.Contains("merged pull request", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSnapshot_RejectsForgedAuthorizationWhenLiveHeadDiffers()
    {
        var result = Evaluate(live: Live(defaultHead: new string('f', 40)));
        Assert.False(result.Ok);
        Assert.Contains("default-branch HEAD", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSnapshot_RejectsForeignClaimOwner()
    {
        var foreign = new ShipClaimGate.Evaluation(
            ShipClaimGate.Verdict.OwnerMismatch,
            new ShipClaimGate.ClaimInfo("modx", "1.2.3-dev", "codex:other", Now.AddHours(-1)),
            null);
        Assert.False(Evaluate(claim: foreign).Ok);
    }

    [Fact]
    public void EvaluateSnapshot_RejectsReceiptLongerThanFiveMinutes()
    {
        var receipt = Receipt();
        receipt.ExpiresAtUtc = receipt.IssuedAtUtc.AddMinutes(6);
        Assert.False(Evaluate(receipt: receipt).Ok);
    }

    [Fact]
    public void EvaluateSnapshot_RejectsPostStageMutation()
    {
        var staged = Bundles();
        staged[0].Sha256 = new string('9', 64);
        var result = Evaluate(stagedBundles: staged);
        Assert.False(result.Ok);
        Assert.Contains("SDK-staged content", result.Message);
    }

    [Fact]
    public void EvaluateSnapshot_RejectsExtraStagedFile()
    {
        var staged = Bundles().ToList();
        staged.Add(new PublicationBundleFile { Path = "extra.bin", Length = 1, Sha256 = new string('1', 64) });
        var result = Evaluate(stagedBundles: staged);
        Assert.False(result.Ok);
        Assert.Contains("extra or missing", result.Message);
    }

    [Fact]
    public void EvaluateSnapshot_RejectsMissingStagedFile()
    {
        var staged = Bundles().Skip(1).ToList();
        var result = Evaluate(stagedBundles: staged);
        Assert.False(result.Ok);
        Assert.Contains("extra or missing", result.Message);
    }

    [Fact]
    public void EvaluateSnapshot_RejectsSourceStageDivergence()
    {
        var staged = Bundles();
        staged[1].Length++;
        var result = Evaluate(sourceBundles: Bundles(), stagedBundles: staged);
        Assert.False(result.Ok);
        Assert.Contains("SDK-staged content", result.Message);
    }

    [Fact]
    public void EvaluateSnapshot_AcceptsExactReceiptAuthorityWithoutBundleGitBlobs()
    {
        var receipt = Receipt();
        receipt.BundleAuthority = "receipt";
        receipt.BundleAuthorityProof = ReceiptAuthorityProof();
        foreach (var bundle in receipt.BundleFiles) bundle.GitBlob = "";
        var source = Bundles();
        foreach (var bundle in source) bundle.GitBlob = "";

        var result = Evaluate(
            receipt: receipt,
            sourceBundles: source,
            stagedBundles: source,
            sourceAuthorityProof: ReceiptAuthorityProof());

        Assert.True(result.Ok, result.Message);
        Assert.Contains("schema-3 build receipt", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSnapshot_ReceiptAuthorityRejectsMissingIndependentProof()
    {
        var receipt = Receipt();
        receipt.BundleAuthority = "receipt";
        receipt.BundleAuthorityProof = ReceiptAuthorityProof();
        foreach (var bundle in receipt.BundleFiles) bundle.GitBlob = "";
        var source = Bundles();
        foreach (var bundle in source) bundle.GitBlob = "";

        var result = Evaluate(receipt: receipt, sourceBundles: source, stagedBundles: source);

        Assert.False(result.Ok);
        Assert.Contains("independently reconstructed proof", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSnapshot_ReceiptAuthorityRejectsProofTamper()
    {
        var receipt = Receipt();
        receipt.BundleAuthority = "receipt";
        receipt.BundleAuthorityProof = ReceiptAuthorityProof();
        receipt.BundleAuthorityProof.OutputFingerprintSha256 = new string('c', 64);
        foreach (var bundle in receipt.BundleFiles) bundle.GitBlob = "";
        var source = Bundles();
        foreach (var bundle in source) bundle.GitBlob = "";

        var result = Evaluate(
            receipt: receipt,
            sourceBundles: source,
            stagedBundles: source,
            sourceAuthorityProof: ReceiptAuthorityProof());

        Assert.False(result.Ok);
        Assert.Contains("output fingerprint", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSnapshot_ReceiptAuthorityRejectsClaimedBundleGitBlob()
    {
        var receipt = Receipt();
        receipt.BundleAuthority = "receipt";
        receipt.BundleAuthorityProof = ReceiptAuthorityProof();
        var source = Bundles();
        foreach (var bundle in source) bundle.GitBlob = "";

        var result = Evaluate(
            receipt: receipt,
            sourceBundles: source,
            stagedBundles: source,
            sourceAuthorityProof: ReceiptAuthorityProof());

        Assert.False(result.Ok);
        Assert.Contains("must not claim Git bundle blobs", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSnapshot_ReceiptAuthorityRejectsFirstUploadBootstrap()
    {
        var receipt = Receipt();
        receipt.Purpose = "workshop_bootstrap";
        receipt.BundleAuthority = "receipt";
        receipt.BundleAuthorityProof = ReceiptAuthorityProof();
        foreach (var bundle in receipt.BundleFiles) bundle.GitBlob = "";
        var source = Bundles();
        foreach (var bundle in source) bundle.GitBlob = "";

        var result = Evaluate(
            receipt: receipt,
            sourcePublishedId: "0",
            sourceBundles: source,
            stagedBundles: source,
            sourceAuthorityProof: ReceiptAuthorityProof());

        Assert.False(result.Ok);
        Assert.Contains("does not support first-upload bootstrap", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("00")]
    [InlineData("000")]
    [InlineData("18446744073709551616")]
    public void EvaluateSnapshot_ReceiptAuthorityRejectsNoncanonicalOrOverflowingPublishedId(string publishedId)
    {
        var receipt = Receipt();
        receipt.BundleAuthority = "receipt";
        receipt.BundleAuthorityProof = ReceiptAuthorityProof();
        foreach (var bundle in receipt.BundleFiles) bundle.GitBlob = "";
        var source = Bundles();
        foreach (var bundle in source) bundle.GitBlob = "";

        var result = Evaluate(
            receipt: receipt,
            sourcePublishedId: publishedId,
            sourceBundles: source,
            stagedBundles: source,
            sourceAuthorityProof: ReceiptAuthorityProof());

        Assert.False(result.Ok);
        Assert.Contains("canonical positive", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSnapshot_ExplicitTrackedAuthorityRequiresItsIndependentProof()
    {
        var receipt = Receipt();
        receipt.BundleAuthority = "tracked";
        receipt.BundleAuthorityProof = new PublicationBundleAuthorityProof
        {
            Authority = "tracked",
            SourceCommit = Sha,
            InventoryGitBlob = new string('5', 40),
            IgnoreGitBlob = new string('6', 40),
            RootBundle = "0123456789abcdef.mod_bundle",
            ByteSource = "git_commit_blobs",
            OutputAlgorithm = "vt2-normalized-bundle-output-set-sha256-v1",
            OutputFingerprintSha256 = new string('a', 64),
        };

        var result = Evaluate(receipt: receipt);

        Assert.False(result.Ok);
        Assert.Contains("independently reconstructed proof", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSnapshot_RejectsReceiptBlobSwapEvenWhenSha256Matches()
    {
        var receipt = Receipt();
        receipt.BundleFiles[0].GitBlob = new string('9', 40);
        var result = Evaluate(receipt: receipt);
        Assert.False(result.Ok);
        Assert.Contains("source commit", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSnapshot_AcceptsExplicitHostedBootstrapReceipt()
    {
        var receipt = Receipt();
        receipt.Purpose = "workshop_bootstrap";
        var result = Evaluate(receipt: receipt, sourcePublishedId: "0");
        Assert.True(result.Ok);
        Assert.Contains("bootstrap", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSnapshot_RejectsOrdinaryPurposeForFirstUploadSentinel()
    {
        var result = Evaluate(sourcePublishedId: "0");
        Assert.False(result.Ok);
        Assert.Contains("purpose", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSnapshot_RejectsBootstrapPurposeForExistingItem()
    {
        var receipt = Receipt();
        receipt.Purpose = "workshop_bootstrap";
        var result = Evaluate(receipt: receipt);
        Assert.False(result.Ok);
        Assert.Contains("purpose", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSnapshot_RejectsMutatedStagedCfg()
    {
        var result = Evaluate(actualStagedCfgHash: new string('0', 64));
        Assert.False(result.Ok);
        Assert.Contains("staged item.cfg", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSnapshot_RejectsMutatedStagedPreview()
    {
        var preview = Preview();
        preview.Sha256 = new string('0', 64);
        var result = Evaluate(stagedPreview: preview);
        Assert.False(result.Ok);
        Assert.Contains("preview proof", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSnapshot_RejectsPreviewPresenceChange()
    {
        var preview = Preview();
        preview.Present = false;
        preview.Length = 0;
        preview.Sha256 = "";
        var result = Evaluate(stagedPreview: preview);
        Assert.False(result.Ok);
    }

    [Fact]
    public void AuthorizeForUpload_RejectsDirectCallWithoutReceiptBeforeAnyMutation()
    {
        var mod = new ModInfo
        {
            Name = "modx",
            ModDir = @"C:\missing\modx",
            ItemCfgPath = @"C:\missing\modx\itemV2.cfg",
        };
        var staged = new StagedUpload(
            @"C:\missing\sample_item",
            @"C:\missing\sample_item\item.cfg",
            "preview.jpg",
            0);
        var result = PublicationReceiptGate.AuthorizeForUpload(
            null, staged, mod, @"C:\missing", @"C:\missing\ugc_tool.exe", Now);
        Assert.False(result.Ok);
        Assert.Contains("claim alone", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseSuccessfulHostedQaCheck_FindsQaGateOnLaterPage()
    {
        var older = Now.AddMinutes(-4);
        var newer = Now.AddMinutes(-2);
        var json = $$"""
        [
          {
            "total_count": 102,
            "check_runs": [
              {
                "name": "tracker-guard",
                "head_sha": "{{Sha}}",
                "status": "completed",
                "conclusion": "success",
                "completed_at": "{{older:O}}",
                "html_url": "https://example.invalid/check/tracker"
              }
            ]
          },
          {
            "total_count": 102,
            "check_runs": [
              {
                "name": "qa-gate",
                "head_sha": "{{Sha}}",
                "status": "completed",
                "conclusion": "success",
                "completed_at": "{{newer:O}}",
                "html_url": "https://example.invalid/check/qa-page-2"
              }
            ]
          }
        ]
        """;

        var result = PublicationReceiptGate.ParseSuccessfulHostedQaCheck(json, Sha);

        Assert.Equal("https://example.invalid/check/qa-page-2", result.Url);
        Assert.Equal(newer, result.Completed);
    }

    [Fact]
    public void ParseSuccessfulHostedQaCheck_SelectsNewestExactSuccessfulRun()
    {
        var older = Now.AddMinutes(-5);
        var newer = Now.AddMinutes(-1);
        var wrongSha = new string('f', 40);
        var json = $$"""
        [
          {
            "check_runs": [
              { "name": "qa-gate", "head_sha": "{{Sha}}", "status": "completed", "conclusion": "success", "completed_at": "{{older:O}}", "html_url": "https://example.invalid/check/old" },
              { "name": "qa-gate", "head_sha": "{{wrongSha}}", "status": "completed", "conclusion": "success", "completed_at": "{{newer:O}}", "html_url": "https://example.invalid/check/wrong-sha" }
            ]
          },
          {
            "check_runs": [
              { "name": "qa-gate", "head_sha": "{{Sha}}", "status": "completed", "conclusion": "failure", "completed_at": "{{newer:O}}", "html_url": "https://example.invalid/check/failed" },
              { "name": "qa-gate", "head_sha": "{{Sha}}", "status": "completed", "conclusion": "success", "completed_at": "{{newer:O}}", "html_url": "https://example.invalid/check/new" }
            ]
          }
        ]
        """;

        var result = PublicationReceiptGate.ParseSuccessfulHostedQaCheck(json, Sha);

        Assert.Equal("https://example.invalid/check/new", result.Url);
        Assert.Equal(newer, result.Completed);
    }

    [Fact]
    public void ParseSuccessfulHostedQaCheck_FailsClosedOnMalformedPage()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ParseSuccessfulHostedQaCheck("[{}]", Sha));

        Assert.Contains("malformed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseSuccessfulHostedQaCheck_FailsClosedWithoutExactSuccess()
    {
        var json = $$"""
        [{"check_runs":[{"name":"qa-gate","head_sha":"{{Sha}}","status":"completed","conclusion":"failure","completed_at":"{{Now:O}}","html_url":"https://example.invalid/check/failed"}]}]
        """;

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ParseSuccessfulHostedQaCheck(json, Sha));

        Assert.Contains("No successful hosted qa-gate", error.Message);
    }
}
