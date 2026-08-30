using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VmbLauncher.Services;

public sealed class PublicationReceipt
{
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("purpose")] public string Purpose { get; set; } = "";
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("issued_at_utc")] public DateTime IssuedAtUtc { get; set; }
    [JsonPropertyName("expires_at_utc")] public DateTime ExpiresAtUtc { get; set; }
    [JsonPropertyName("repository")] public string Repository { get; set; } = "";
    [JsonPropertyName("release_tag")] public string ReleaseTag { get; set; } = "";
    [JsonPropertyName("receipt_asset_name")] public string ReceiptAssetName { get; set; } = "";
    [JsonPropertyName("source_root")] public string SourceRoot { get; set; } = "";
    [JsonPropertyName("source_commit")] public string SourceCommit { get; set; } = "";
    [JsonPropertyName("mod")] public string Mod { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("owner")] public string Owner { get; set; } = "";
    [JsonPropertyName("item_cfg_sha256")] public string ItemCfgSha256 { get; set; } = "";
    [JsonPropertyName("item_cfg_git_blob")] public string ItemCfgGitBlob { get; set; } = "";
    [JsonPropertyName("bundle_authority")] public string BundleAuthority { get; set; } = "";
    [JsonPropertyName("bundle_authority_proof")] public PublicationBundleAuthorityProof? BundleAuthorityProof { get; set; }
    [JsonPropertyName("bundle_files")] public List<PublicationBundleFile> BundleFiles { get; set; } = new();
    [JsonPropertyName("preview_file")] public PublicationPreviewFile PreviewFile { get; set; } = new();
    [JsonPropertyName("authorization")] public PublicationAuthorization Authorization { get; set; } = new();
}

public sealed class PublicationBundleFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("length")] public long Length { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("git_blob")] public string GitBlob { get; set; } = "";
}

public sealed class PublicationPreviewFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("present")] public bool Present { get; set; }
    [JsonPropertyName("length")] public long Length { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("git_blob")] public string GitBlob { get; set; } = "";
}

internal sealed record CommitPublicationSnapshot(
    string ItemCfgSha256,
    string ItemCfgGitBlob,
    string ItemCfgText,
    string Version,
    string PublishedId,
    IReadOnlyList<PublicationBundleFile> BundleFiles,
    PublicationPreviewFile PreviewFile)
{
    internal string BundleAuthority { get; init; } = "tracked";
    internal PublicationBundleAuthorityProof? BundleAuthorityProof { get; init; }
}

public sealed class PublicationAuthorization
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("source_commit")] public string SourceCommit { get; set; } = "";
    [JsonPropertyName("checked_at_utc")] public DateTime CheckedAtUtc { get; set; }
    [JsonPropertyName("default_branch")] public string DefaultBranch { get; set; } = "";
    [JsonPropertyName("default_branch_commit")] public string DefaultBranchCommit { get; set; } = "";
    [JsonPropertyName("merged_pr_number")] public int MergedPrNumber { get; set; }
    [JsonPropertyName("qa_check")] public string QaCheck { get; set; } = "";
    [JsonPropertyName("qa_check_url")] public string QaCheckUrl { get; set; } = "";
    [JsonPropertyName("qa_completed_at_utc")] public DateTime QaCompletedAtUtc { get; set; }
}

public sealed record LivePublicationSnapshot(
    string SourceRoot,
    string SourceCommit,
    bool Clean,
    string DefaultBranch,
    string DefaultBranchCommit,
    int MergedPrNumber,
    string QaCheckUrl,
    DateTime QaCompletedAtUtc);

public sealed record PublicationGateResult(bool Ok, string Message)
{
    internal VerifiedPublicationReceipt? Verified { get; init; }
}

internal sealed class VerifiedPublicationReceipt : IDisposable
{
    private int _consumed;
    private readonly string _mod;
    private readonly DateTime _expiresAtUtc;
    private readonly UploadPathLease? _lease;
    private readonly StagedUpload? _staged;
    private readonly string? _expectedStagedCfgText;
    private readonly string? _expectedSourceCfgSha256;
    private readonly string? _expectedSourceRoot;
    private readonly string? _expectedSourceCommit;
    private bool _bootstrapBoundaryOpened;

    internal string ToolPath =>
        _lease?.ToolPath ?? throw new InvalidOperationException("Verified receipt has no pinned upload snapshot.");
    internal bool IsBootstrap { get; }

    internal VerifiedPublicationReceipt(
        string mod,
        DateTime expiresAtUtc,
        UploadPathLease? lease = null,
        bool isBootstrap = false,
        StagedUpload? staged = null,
        string? expectedStagedCfgText = null,
        string? expectedSourceCfgSha256 = null,
        string? expectedSourceRoot = null,
        string? expectedSourceCommit = null)
    {
        _mod = mod;
        _expiresAtUtc = expiresAtUtc.ToUniversalTime();
        _lease = lease;
        IsBootstrap = isBootstrap;
        _staged = staged;
        _expectedStagedCfgText = expectedStagedCfgText;
        _expectedSourceCfgSha256 = expectedSourceCfgSha256;
        _expectedSourceRoot = expectedSourceRoot;
        _expectedSourceCommit = expectedSourceCommit;
    }

    internal bool TryConsume(string mod, DateTime nowUtc)
    {
        if (!string.Equals(_mod, mod, StringComparison.Ordinal) ||
            nowUtc.ToUniversalTime() >= _expiresAtUtc)
            return false;
        return Interlocked.Exchange(ref _consumed, 1) == 0;
    }

    internal void PrepareForUploadProcess()
    {
        if (!IsBootstrap) return;
        if (_lease == null || _staged == null ||
            string.IsNullOrEmpty(_expectedStagedCfgText) ||
            string.IsNullOrEmpty(_expectedSourceCfgSha256))
            throw new InvalidOperationException(
                "Verified bootstrap receipt is missing its constrained write-back state.");
        _lease.ReleaseCfgForBootstrapWrite();
        _bootstrapBoundaryOpened = true;
    }

    internal BootstrapWriteBackResult CompleteBootstrapWriteBack(ModInfo mod)
    {
        if (!IsBootstrap)
            return new(true, "Existing Workshop item requires no ID write-back.");
        if (!_bootstrapBoundaryOpened || _staged == null ||
            _expectedStagedCfgText == null ||
            _expectedSourceCfgSha256 == null ||
            _expectedSourceRoot == null ||
            _expectedSourceCommit == null)
            return new(false, "Bootstrap cfg boundary was not opened by this verified receipt.");
        var repository = PublicationReceiptGate.ValidateBootstrapWriteBackRepository(
            mod.ModDir, _expectedSourceRoot, _expectedSourceCommit);
        if (!repository.Ok)
            return new(false, repository.Message);
        return UploadStager.CompleteBootstrapWriteBack(
            _staged, mod, _expectedStagedCfgText, _expectedSourceCfgSha256);
    }

    public void Dispose() => _lease?.Dispose();
}

/// <summary>
/// Final fail-closed boundary immediately before ugc_tool. Authority is an
/// exact short-lived JSON asset hosted on the canonical GitHub release. The
/// caller's local file is accepted only when its bytes match the independently
/// downloaded asset. Source and SDK-staged bytes are then checked separately.
/// </summary>
public static partial class PublicationReceiptGate
{
    public const int Schema = 3;
    public const string GitHubRepo = "Ensrick/vermintide-2-tweaker";
    public const string QaCheckName = "qa-gate";
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(5);

    // Authority inputs are deliberately much smaller than the semantic byte
    // set they may describe. These caps are checked before allocating a full
    // caller/process/Git payload and before hashing it.
    internal const int MaximumReceiptBytes = 8 * 1024 * 1024;
    internal const int MaximumProcessOutputBytes = 8 * 1024 * 1024;
    internal const int MaximumProcessErrorBytes = 1 * 1024 * 1024;
    internal const int MaximumGitObjectBytes = 512 * 1024 * 1024;
    internal const int MaximumGitTreeLogicalEntries = 200_000;

    // Receipt/source/output inventories describe the exact deployable set.
    // Bound their cardinality and declared/commit-qualified aggregate before
    // constructing any source/output fingerprint.
    internal const int MaximumSemanticMapEntries = 4096;
    internal const long MaximumSemanticMapBytes = 32L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static PublicationGateResult AuthorizeForUpload(
        string? receiptPath,
        StagedUpload staged,
        ModInfo mod,
        string? configuredProjectRoot,
        string ugcToolPath,
        DateTime nowUtc)
    {
        var qualified = AuthorizeCommitQualifiedReceipt(
            receiptPath,
            mod,
            configuredProjectRoot,
            nowUtc,
            CommitQualifiedReceiptPurpose.Publication);
        if (!qualified.Ok || qualified.Proof == null)
            return new(false, qualified.Message);

        UploadPathLease? lease = null;
        try
        {
            var receipt = qualified.Proof.Receipt;
            var committed = qualified.Proof.Committed;
            lease = UploadPathLease.Capture(staged, ugcToolPath);
            var expectedStagedCfgText =
                UploadStager.BuildStagedCfgTextFromSourceCfg(
                    committed.ItemCfgText, receipt.Mod, committed.PreviewFile.Path);
            var expectedStagedCfgBytes = Encoding.UTF8.GetBytes(expectedStagedCfgText);
            var expectedStagedCfgHash = HashBytes(expectedStagedCfgBytes);
            var evaluated = EvaluatePinnedUploadSnapshot(
                committed.BundleFiles,
                committed.PreviewFile,
                expectedStagedCfgHash,
                lease.CfgSha256,
                lease.BundleFiles,
                lease.PreviewFile);
            if (!evaluated.Ok)
            {
                lease.Dispose();
                lease = null;
                return evaluated;
            }
            var result = evaluated with
            {
                Verified = new VerifiedPublicationReceipt(
                    receipt.Mod,
                    receipt.ExpiresAtUtc,
                    lease,
                    isBootstrap: committed.PublishedId == "0",
                    staged: staged,
                    expectedStagedCfgText: expectedStagedCfgText,
                    expectedSourceCfgSha256: committed.ItemCfgSha256,
                    expectedSourceRoot: qualified.Proof.RepositoryRoot,
                    expectedSourceCommit: receipt.SourceCommit)
            };
            lease = null;
            return result;
        }
        catch (Exception ex)
        {
            lease?.Dispose();
            return new(false, $"Independent publication verification failed: {ex.Message}");
        }
    }

    public static PublicationGateResult EvaluateSnapshot(
        PublicationReceipt receipt,
        LivePublicationSnapshot live,
        string modName,
        string sourceVersion,
        string expectedOwner,
        DateTime nowUtc,
        string callerReceiptHash,
        string hostedReceiptHash,
        string sourceCfgHash,
        string sourceCfgGitBlob,
        string sourcePublishedId,
        IReadOnlyList<PublicationBundleFile> sourceBundles,
        PublicationPreviewFile sourcePreview,
        string expectedStagedCfgHash,
        string actualStagedCfgHash,
        IReadOnlyList<PublicationBundleFile> stagedBundles,
        PublicationPreviewFile stagedPreview,
        ShipClaimGate.Evaluation claim,
        PublicationBundleAuthorityProof? sourceBundleAuthorityProof = null)
    {
        var commitQualified = EvaluateCommitQualifiedSnapshot(
            receipt,
            live,
            modName,
            sourceVersion,
            expectedOwner,
            nowUtc,
            callerReceiptHash,
            hostedReceiptHash,
            sourceCfgHash,
            sourceCfgGitBlob,
            sourcePublishedId,
            sourceBundles,
            sourcePreview,
            sourcePublishedId == "0" ? "workshop_bootstrap" : "workshop_upload",
            claim,
            sourceBundleAuthorityProof);
        if (!commitQualified.Ok) return commitQualified;

        var pinnedUpload = EvaluatePinnedUploadSnapshot(
            sourceBundles,
            sourcePreview,
            expectedStagedCfgHash,
            actualStagedCfgHash,
            stagedBundles,
            stagedPreview);
        if (!pinnedUpload.Ok) return pinnedUpload;

        var mode = sourcePublishedId == "0"
            ? "first-upload bootstrap"
            : "existing-item upload";
        var bundleProof = string.IsNullOrEmpty(receipt.BundleAuthority) || receipt.BundleAuthority == "tracked"
            ? "source-commit bundle blobs"
            : "committed schema-3 build receipt";
        return new(
            true,
            $"GitHub-hosted receipt, live authorization, {bundleProof}, and pinned SDK staging bytes passed ({mode})");
    }

    internal static PublicationGateResult EvaluatePinnedUploadSnapshot(
        IReadOnlyList<PublicationBundleFile> sourceBundles,
        PublicationPreviewFile sourcePreview,
        string expectedStagedCfgHash,
        string actualStagedCfgHash,
        IReadOnlyList<PublicationBundleFile> stagedBundles,
        PublicationPreviewFile stagedPreview)
    {
        if (!SameHash(expectedStagedCfgHash, actualStagedCfgHash))
            return new(false, "SDK-staged item.cfg is not the byte-exact canonical cfg derived from verified source.");
        var stagedResult = CompareBundleFiles(sourceBundles, stagedBundles, "SDK-staged content");
        if (!stagedResult.Ok) return stagedResult;
        var stagedPreviewResult = ComparePreviewFile(sourcePreview, stagedPreview, "SDK-staged");
        if (!stagedPreviewResult.Ok) return stagedPreviewResult;
        return new(true, "Pinned SDK staging matches the commit-qualified source snapshot.");
    }

    /// <summary>
    /// Consumer-neutral half of the hosted receipt boundary. It proves that
    /// one exact output map belongs to the authenticated live source commit,
    /// claim owner, inventory authority, and committed build receipt. Consumers
    /// must still pin and validate their own byte source and destination before
    /// they may mutate anything.
    /// </summary>
    internal static PublicationGateResult EvaluateCommitQualifiedSnapshot(
        PublicationReceipt receipt,
        LivePublicationSnapshot live,
        string modName,
        string sourceVersion,
        string expectedOwner,
        DateTime nowUtc,
        string callerReceiptHash,
        string hostedReceiptHash,
        string sourceCfgHash,
        string sourceCfgGitBlob,
        string sourcePublishedId,
        IReadOnlyList<PublicationBundleFile> sourceBundles,
        PublicationPreviewFile sourcePreview,
        string expectedPurpose,
        ShipClaimGate.Evaluation claim,
        PublicationBundleAuthorityProof? sourceBundleAuthorityProof = null)
    {
        if (!SameHash(callerReceiptHash, hostedReceiptHash))
            return new(false, "Caller receipt bytes do not match the independently downloaded GitHub release asset.");
        if (receipt.Schema != Schema || receipt.Purpose != expectedPurpose)
            return new(false, "Receipt schema or purpose is not canonical.");
        var expectedAssetName = expectedPurpose == "local_deploy"
            ? $"deployment-receipt-{modName}.json"
            : expectedPurpose is "workshop_upload" or "workshop_bootstrap"
                ? $"publication-receipt-{modName}.json"
                : "";
        if (receipt.Repository != GitHubRepo ||
            string.IsNullOrWhiteSpace(receipt.ReleaseTag) ||
            !string.Equals(receipt.ReceiptAssetName, expectedAssetName, StringComparison.Ordinal))
            return new(false, "Receipt is not bound to canonical GitHub release coordinates.");
        if (!Guid.TryParseExact(receipt.Nonce, "N", out _))
            return new(false, "Receipt nonce is invalid.");

        var issued = receipt.IssuedAtUtc.ToUniversalTime();
        var expires = receipt.ExpiresAtUtc.ToUniversalTime();
        var now = nowUtc.ToUniversalTime();
        if (expires <= issued || expires - issued > MaximumLifetime || now < issued || now >= expires)
            return new(false, "Receipt is expired, not yet valid, or longer than five minutes.");

        if (!string.Equals(NormalizeRoot(receipt.SourceRoot), NormalizeRoot(live.SourceRoot), StringComparison.OrdinalIgnoreCase))
            return new(false, "Receipt source root does not match the selected repository.");
        if (!SameSha(receipt.SourceCommit, live.SourceCommit))
            return new(false, "Receipt source commit does not match the independently queried commit.");
        if (!string.Equals(receipt.Mod, modName, StringComparison.Ordinal) ||
            !string.Equals(receipt.Version, sourceVersion, StringComparison.Ordinal))
            return new(false, "Receipt mod/version does not match the source being uploaded.");
        if (!string.Equals(receipt.Owner, expectedOwner, StringComparison.Ordinal))
            return new(false, "Receipt owner does not match the current ship owner.");
        if (claim.Verdict != ShipClaimGate.Verdict.Match ||
            claim.Claim == null ||
            !string.Equals(claim.Claim.Session, receipt.Owner, StringComparison.Ordinal))
            return new(false, $"Machine-global claim is not an exact live owner/version match ({claim.Verdict}).");

        var auth = receipt.Authorization;
        if (auth.Mode != "hosted_qa" || auth.QaCheck != QaCheckName)
            return new(false, "Receipt lacks canonical hosted qa-gate authorization.");
        if (!SameSha(auth.SourceCommit, receipt.SourceCommit) ||
            !SameSha(auth.DefaultBranchCommit, receipt.SourceCommit))
            return new(false, "Receipt authorization is not bound to its source commit.");
        if (!string.Equals(auth.DefaultBranch, live.DefaultBranch, StringComparison.Ordinal) ||
            !SameSha(live.DefaultBranchCommit, receipt.SourceCommit))
            return new(false, "Source commit is not the live default-branch HEAD.");
        if (auth.MergedPrNumber <= 0 || auth.MergedPrNumber != live.MergedPrNumber)
            return new(false, "No exact merged pull request authorizes this source commit.");
        if (string.IsNullOrWhiteSpace(auth.QaCheckUrl) ||
            !string.Equals(auth.QaCheckUrl, live.QaCheckUrl, StringComparison.Ordinal) ||
            auth.QaCompletedAtUtc.ToUniversalTime() != live.QaCompletedAtUtc.ToUniversalTime())
            return new(false, "Hosted qa-gate evidence does not match the independently queried successful check.");

        if (string.IsNullOrWhiteSpace(sourcePublishedId))
            return new(false, "Exact source-commit cfg has no published_id.");
        if (!SameHash(receipt.ItemCfgSha256, sourceCfgHash) ||
            !SameGitBlob(receipt.ItemCfgGitBlob, sourceCfgGitBlob))
            return new(false, "Receipt itemV2.cfg proof does not match the exact source-commit blob.");
        var authority = string.IsNullOrEmpty(receipt.BundleAuthority)
            ? "tracked"
            : receipt.BundleAuthority;
        if (string.IsNullOrEmpty(receipt.BundleAuthority) && receipt.BundleAuthorityProof != null)
            return new(false, "Legacy tracked receipt cannot carry an untyped bundle-authority proof.");
        if (authority != "tracked" && authority != "receipt")
            return new(false, $"Receipt bundle authority '{authority}' is not supported.");
        if (authority == "receipt" && !IsCanonicalPositivePublishedId(sourcePublishedId))
            return new(false,
                "Receipt-authority hosted action requires a canonical positive published_id and does not support first-upload bootstrap.");

        if (!string.IsNullOrEmpty(receipt.BundleAuthority))
        {
            var authorityResult = CompareBundleAuthorityProof(
                receipt.BundleAuthorityProof,
                sourceBundleAuthorityProof,
                authority,
                receipt.SourceCommit);
            if (!authorityResult.Ok) return authorityResult;
        }

        if (authority == "receipt" &&
            (receipt.BundleFiles.Any(file => !string.IsNullOrEmpty(file.GitBlob)) ||
             sourceBundles.Any(file => !string.IsNullOrEmpty(file.GitBlob))))
            return new(false, "Receipt-authority output records must not claim Git bundle blobs.");

        var sourceResult = CompareBundleFiles(
            receipt.BundleFiles,
            sourceBundles,
            authority == "tracked" ? "source commit" : "schema-3 build receipt",
            requireGitBlob: authority == "tracked");
        if (!sourceResult.Ok) return sourceResult;
        var sourcePreviewResult = ComparePreviewFile(receipt.PreviewFile, sourcePreview, "source commit", requireGitBlob: true);
        if (!sourcePreviewResult.Ok) return sourcePreviewResult;
        var bundleProof = authority == "tracked"
            ? "source-commit bundle blobs"
            : "committed schema-3 build receipt";
        return new(
            true,
            $"GitHub-hosted receipt, live authorization, and {bundleProof} passed");
    }

    private static PublicationGateResult ComparePreviewFile(
        PublicationPreviewFile expected,
        PublicationPreviewFile actual,
        string context,
        bool requireGitBlob = false)
    {
        if (expected == null || actual == null ||
            string.IsNullOrWhiteSpace(expected.Path) ||
            !string.Equals(expected.Path, actual.Path, StringComparison.Ordinal) ||
            expected.Present != actual.Present ||
            expected.Length != actual.Length ||
            (expected.Present && !SameHash(expected.Sha256, actual.Sha256)) ||
            (!expected.Present && !string.IsNullOrEmpty(expected.Sha256)) ||
            (requireGitBlob && expected.Present && !SameGitBlob(expected.GitBlob, actual.GitBlob)) ||
            (requireGitBlob && !expected.Present &&
                (!string.IsNullOrEmpty(expected.GitBlob) || !string.IsNullOrEmpty(actual.GitBlob))))
            return new(false, $"{context} preview proof does not match the hosted receipt.");
        return new(true, $"{context} preview proof matches");
    }

    private static PublicationGateResult CompareBundleFiles(
        IReadOnlyList<PublicationBundleFile> expected,
        IReadOnlyList<PublicationBundleFile> actual,
        string context,
        bool requireGitBlob = false)
    {
        try
        {
            ValidateDeclaredByteMapBounds(
                expected, file => file.Length, $"{context} expected output map");
            ValidateDeclaredByteMapBounds(
                actual, file => file.Length, $"{context} actual output map");
        }
        catch (InvalidDataException ex)
        {
            return new(false, ex.Message);
        }
        if (expected.Count == 0) return new(false, "Receipt contains no bundle hashes.");
        if (expected.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != expected.Count ||
            actual.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != actual.Count)
            return new(false, $"{context} bundle proof contains duplicate paths.");
        var expectedMap = expected.ToDictionary(x => x.Path, StringComparer.Ordinal);
        var actualMap = actual.ToDictionary(x => x.Path, StringComparer.Ordinal);
        if (expectedMap.Count != actualMap.Count)
            return new(false, $"{context} file set differs from the hosted receipt (extra or missing file).");
        foreach (var pair in expectedMap)
        {
            if (!actualMap.TryGetValue(pair.Key, out var found) ||
                pair.Value.Length != found.Length ||
                !SameHash(pair.Value.Sha256, found.Sha256) ||
                (requireGitBlob && !SameGitBlob(pair.Value.GitBlob, found.GitBlob)))
                return new(false, $"{context} bundle proof mismatch: {pair.Key}");
        }
        return new(true, $"{context} bundle proof matches");
    }

    private static PublicationReceipt DeserializeReceipt(byte[] bytes)
    {
        RequireReceiptByteLimit(bytes.LongLength, "Receipt JSON");
        var receipt = JsonSerializer.Deserialize<PublicationReceipt>(bytes, JsonOptions)
            ?? throw new InvalidDataException("receipt JSON was empty");
        ValidateDeclaredByteMapBounds(
            receipt.BundleFiles, file => file.Length, "Hosted receipt output map");
        ValidatePublicationBundleAuthorityJsonShape(bytes, receipt);
        return receipt;
    }

    internal static PublicationReceipt AuthenticateHostedReceipt(byte[] callerBytes, byte[] hostedBytes)
    {
        RequireReceiptByteLimit(callerBytes.LongLength, "Caller receipt");
        RequireReceiptByteLimit(hostedBytes.LongLength, "Hosted receipt");
        if (!callerBytes.AsSpan().SequenceEqual(hostedBytes))
            throw new InvalidDataException(
                "Caller receipt bytes do not match the independently downloaded GitHub release asset.");
        return DeserializeReceipt(hostedBytes);
    }

    private static string HashBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsCanonicalPositivePublishedId(string value) =>
        ulong.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) &&
        parsed > 0 &&
        value == parsed.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static CommitPublicationSnapshot ReadCommitSnapshot(
        string repositoryRoot,
        string sourceCommit,
        string modName,
        bool allowEmptyBundleFiles = false)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                sourceCommit, "^[0-9a-f]{40}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new InvalidDataException("Receipt source_commit is not a full Git commit SHA.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(modName, "^[a-z0-9_]+$"))
            throw new InvalidDataException("Receipt mod name is not canonical.");

        var root = NormalizeRoot(repositoryRoot);
        var entries = ReadVerifiedCommitTree(root, sourceCommit);
        return ReadCommitSnapshot(root, sourceCommit, modName, allowEmptyBundleFiles, entries);
    }

    private static CommitPublicationSnapshot ReadCommitSnapshot(
        string root,
        string sourceCommit,
        string modName,
        bool allowEmptyBundleFiles,
        IReadOnlyDictionary<string, GitTreeEntry> entries)
    {

        var cfgRepoPath = $"{modName}/itemV2.cfg";
        var cfgEntry = RequireBlob(entries, cfgRepoPath);
        var cfgBytes = ReadGitBlob(root, cfgEntry.ObjectId);
        var cfgText = Encoding.UTF8.GetString(cfgBytes);

        var bundlePrefix = $"{modName}/bundleV2/";
        var bundleEntries = entries.Values
            .Where(entry => entry.Path.StartsWith(bundlePrefix, StringComparison.Ordinal))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToList();
        var bundleSizes = PreflightGitBlobMap(
            root, bundleEntries, "Source-commit bundle output map");
        var bundles = bundleEntries
            .Select(entry =>
            {
                var bytes = ReadGitBlob(root, entry.ObjectId, bundleSizes[entry.Path]);
                return new PublicationBundleFile
                {
                    Path = entry.Path[bundlePrefix.Length..],
                    Length = bytes.LongLength,
                    Sha256 = HashBytes(bytes),
                    GitBlob = entry.ObjectId,
                };
            })
            .ToList();
        if (bundles.Count == 0 && !allowEmptyBundleFiles)
            throw new InvalidDataException(
                $"Source commit {sourceCommit} contains no blobs under {bundlePrefix}.");

        var previewName = UploadStager.ResolvePreviewNameFromSourceCfg(
            cfgText,
            name => entries.ContainsKey($"{modName}/{name}"));
        var previewRepoPath = $"{modName}/{previewName}";
        PublicationPreviewFile preview;
        if (entries.TryGetValue(previewRepoPath, out var previewEntry))
        {
            RequireRegularBlob(previewEntry, previewRepoPath);
            var bytes = ReadGitBlob(root, previewEntry.ObjectId);
            preview = new PublicationPreviewFile
            {
                Path = previewName,
                Present = true,
                Length = bytes.LongLength,
                Sha256 = HashBytes(bytes),
                GitBlob = previewEntry.ObjectId,
            };
        }
        else
        {
            preview = new PublicationPreviewFile
            {
                Path = previewName,
                Present = false,
                Length = 0,
                Sha256 = "",
                GitBlob = "",
            };
        }

        var luaPath = $"{modName}/scripts/mods/{modName}/{modName}.lua";
        var luaEntry = RequireBlob(entries, luaPath);
        var version = TitleVersionSync.ReadModVersionText(
            Encoding.UTF8.GetString(ReadGitBlob(root, luaEntry.ObjectId)), luaPath);

        return new CommitPublicationSnapshot(
            HashBytes(cfgBytes),
            cfgEntry.ObjectId,
            cfgText,
            version,
            ModDiscovery.ExtractPublishedId(cfgText) ?? "",
            bundles,
            preview);
    }

    internal static PublicationGateResult ValidateBootstrapWriteBackRepository(
        string modDirectory,
        string expectedRoot,
        string expectedCommit)
    {
        try
        {
            var actualRoot = Run(
                "git", new[] { "-C", modDirectory, "rev-parse", "--show-toplevel" }).Trim();
            var actualCommit = Run(
                "git", new[] { "-C", modDirectory, "rev-parse", "HEAD" }).Trim();
            if (!string.Equals(
                    NormalizeRoot(actualRoot),
                    NormalizeRoot(expectedRoot),
                    StringComparison.OrdinalIgnoreCase))
                return new(false, "Bootstrap source repository changed before ID write-back.");
            if (!SameSha(actualCommit, expectedCommit))
                return new(false, "Bootstrap source HEAD changed before ID write-back.");
            return new(true, "Bootstrap source repository still matches the authorized commit.");
        }
        catch (Exception ex)
        {
            return new(false, $"Cannot revalidate bootstrap source repository: {ex.Message}");
        }
    }

    internal static IReadOnlyDictionary<string, GitTreeEntry> ReadVerifiedCommitTree(
        string repositoryRoot,
        string sourceCommit)
    {
        var root = NormalizeRoot(repositoryRoot);
        _ = RunBinary("git", new[] { "--no-replace-objects", "-C", root, "cat-file", "-e", $"{sourceCommit}^{{commit}}" });
        var commitBytes = ReadGitObject(root, "commit", sourceCommit);
        var lineEnd = Array.IndexOf(commitBytes, (byte)'\n');
        if (lineEnd != 45 || !commitBytes.AsSpan(0, 5).SequenceEqual("tree "u8))
            throw new InvalidDataException("Source commit lacks one canonical leading tree header.");
        var treeId = Encoding.ASCII.GetString(commitBytes, 5, 40);
        if (!System.Text.RegularExpressions.Regex.IsMatch(treeId, "^[0-9a-f]{40}$"))
            throw new InvalidDataException("Source commit root tree id is noncanonical.");

        var result = new Dictionary<string, GitTreeEntry>(StringComparer.Ordinal);
        var budget = new GitTreeTraversalBudget(MaximumGitTreeLogicalEntries);
        ReadVerifiedTreeRecursive(
            treeId,
            "",
            result,
            depth: 0,
            budget,
            id => ReadGitObject(root, "tree", id));
        return result;
    }

    private static void ReadVerifiedTreeRecursive(
        string treeId,
        string prefix,
        Dictionary<string, GitTreeEntry> result,
        int depth,
        GitTreeTraversalBudget budget,
        Func<string, byte[]> readTree)
    {
        if (depth > 64)
            throw new InvalidDataException("Source commit tree exceeds the bounded proof limits.");
        var bytes = readTree(treeId);
        foreach (var entry in ParseTreeObject(bytes, budget.Remaining))
        {
            budget.Consume();
            var path = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";
            if (entry.Mode == "40000")
            {
                ReadVerifiedTreeRecursive(
                    entry.ObjectId,
                    path,
                    result,
                    depth + 1,
                    budget,
                    readTree);
                continue;
            }
            var type = entry.Mode == "160000" ? "commit" : "blob";
            if (!result.TryAdd(path, new GitTreeEntry(entry.Mode, type, entry.ObjectId, path)))
                throw new InvalidDataException($"Git tree contains duplicate path '{path}'.");
        }
    }

    internal static IReadOnlyList<RawTreeEntry> ParseTreeObject(byte[] bytes)
        => ParseTreeObject(bytes, MaximumGitTreeLogicalEntries);

    internal static IReadOnlyList<RawTreeEntry> ParseTreeObject(
        byte[] bytes,
        int maximumEntries)
    {
        if (maximumEntries < 0 || maximumEntries > MaximumGitTreeLogicalEntries)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        var entries = new List<RawTreeEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var foldedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var strictUtf8 = new UTF8Encoding(false, true);
        var position = 0;
        while (position < bytes.Length)
        {
            if (entries.Count >= maximumEntries)
                throw new InvalidDataException(
                    "Git tree object exceeds the remaining logical-entry traversal budget.");
            var space = Array.IndexOf(bytes, (byte)' ', position);
            var nul = space < 0 ? -1 : Array.IndexOf(bytes, (byte)0, space + 1);
            if (space <= position || nul <= space + 1 || nul + 21 > bytes.Length)
                throw new InvalidDataException("Git tree object is malformed.");
            var mode = Encoding.ASCII.GetString(bytes, position, space - position);
            if (mode is not ("40000" or "100644" or "100755" or "120000" or "160000"))
                throw new InvalidDataException($"Git tree object contains unsupported mode '{mode}'.");
            string name;
            try { name = strictUtf8.GetString(bytes, space + 1, nul - space - 1); }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("Git tree object contains a non-UTF-8 path.", ex);
            }
            if (name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Contains('\0'))
                throw new InvalidDataException("Git tree object contains a noncanonical name.");
            if (!names.Add(name) || !foldedNames.Add(name))
                throw new InvalidDataException(
                    $"Git tree object contains a duplicate or case-colliding sibling name: {name}");
            var nameBytes = bytes.AsSpan(space + 1, nul - space - 1).ToArray();
            var objectId = Convert.ToHexString(bytes.AsSpan(nul + 1, 20)).ToLowerInvariant();
            var entry = new RawTreeEntry(mode, name, objectId, nameBytes);
            if (entries.Count != 0 && CompareTreeNames(entries[^1], entry) >= 0)
                throw new InvalidDataException("Git tree object entries are not in canonical Git order.");
            entries.Add(entry);
            position = nul + 21;
        }
        return entries;
    }
#if VMBLAUNCHER_TEST_HOOKS
    internal static IReadOnlyDictionary<string, GitTreeEntry> ReadVerifiedTreeForTest(
        string rootTreeId,
        Func<string, byte[]> readTree,
        int maximumLogicalEntries)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(rootTreeId, "^[0-9a-f]{40}$"))
            throw new InvalidDataException("Test root tree id is noncanonical.");
        var result = new Dictionary<string, GitTreeEntry>(StringComparer.Ordinal);
        ReadVerifiedTreeRecursive(
            rootTreeId,
            "",
            result,
            depth: 0,
            new GitTreeTraversalBudget(maximumLogicalEntries),
            readTree);
        return result;
    }
#endif
    private sealed class GitTreeTraversalBudget
    {
        private int _remaining;
        internal int Remaining => _remaining;

        internal GitTreeTraversalBudget(int maximum)
        {
            if (maximum < 0 || maximum > MaximumGitTreeLogicalEntries)
                throw new ArgumentOutOfRangeException(nameof(maximum));
            _remaining = maximum;
        }

        internal void Consume()
        {
            if (_remaining == 0)
                throw new InvalidDataException(
                    "Source commit tree exceeds its logical-entry traversal budget.");
            _remaining--;
        }
    }

    private static int CompareTreeNames(RawTreeEntry left, RawTreeEntry right)
    {
        var length = Math.Min(left.NameBytes.Length, right.NameBytes.Length);
        for (var index = 0; index < length; index++)
        {
            var difference = left.NameBytes[index] - right.NameBytes[index];
            if (difference != 0) return difference;
        }
        var leftNext = left.NameBytes.Length == length
            ? left.Mode == "40000" ? (byte)'/' : (byte)0
            : left.NameBytes[length];
        var rightNext = right.NameBytes.Length == length
            ? right.Mode == "40000" ? (byte)'/' : (byte)0
            : right.NameBytes[length];
        return leftNext - rightNext;
    }

    private static GitTreeEntry RequireBlob(
        IReadOnlyDictionary<string, GitTreeEntry> entries,
        string path)
    {
        if (!entries.TryGetValue(path, out var entry))
            throw new InvalidDataException($"Source commit is missing required blob '{path}'.");
        RequireRegularBlob(entry, path);
        return entry;
    }

    private static void RequireRegularBlob(GitTreeEntry entry, string path)
    {
        if (entry.Type != "blob" ||
            (entry.Mode != "100644" && entry.Mode != "100755") ||
            !System.Text.RegularExpressions.Regex.IsMatch(entry.ObjectId, "^[0-9a-f]{40}$"))
            throw new InvalidDataException($"Source path '{path}' is not a regular Git blob.");
    }

    /// <summary>
    /// Performs a complete size-only census before any member of a semantic
    /// Git-blob map is allocated or hashed. Git object IDs are immutable, so
    /// the returned exact lengths can be used for the subsequent bounded reads.
    /// </summary>
    private static IReadOnlyDictionary<string, long> PreflightGitBlobMap(
        string root,
        IReadOnlyList<GitTreeEntry> entries,
        string label)
    {
        if (entries.Count > MaximumSemanticMapEntries)
            throw new InvalidDataException(
                $"{label} exceeds the {MaximumSemanticMapEntries}-entry safety limit.");

        long aggregate = 0;
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            RequireRegularBlob(entry, entry.Path);
            var length = ReadGitObjectSize(root, "blob", entry.ObjectId);
            try { aggregate = checked(aggregate + length); }
            catch (OverflowException ex)
            {
                throw new InvalidDataException($"{label} byte length overflowed.", ex);
            }
            if (aggregate > MaximumSemanticMapBytes)
                throw new InvalidDataException(
                    $"{label} exceeds the 32-GiB aggregate byte limit.");
            sizes.Add(entry.Path, length);
        }
        return sizes;
    }

    private static long ValidateDeclaredByteMapBounds<T>(
        IReadOnlyCollection<T>? entries,
        Func<T, long> lengthSelector,
        string label)
    {
        if (entries == null)
            throw new InvalidDataException($"{label} is missing.");
        if (entries.Count > MaximumSemanticMapEntries)
            throw new InvalidDataException(
                $"{label} exceeds the {MaximumSemanticMapEntries}-entry safety limit.");

        long aggregate = 0;
        foreach (var entry in entries)
        {
            var length = lengthSelector(entry);
            if (length < 0)
                throw new InvalidDataException($"{label} contains a negative byte length.");
            try { aggregate = checked(aggregate + length); }
            catch (OverflowException ex)
            {
                throw new InvalidDataException($"{label} byte length overflowed.", ex);
            }
            if (aggregate > MaximumSemanticMapBytes)
                throw new InvalidDataException(
                    $"{label} exceeds the 32-GiB aggregate byte limit.");
        }
        return aggregate;
    }

    private static void RequireReceiptByteLimit(long length, string label)
    {
        if (length < 0 || length > MaximumReceiptBytes)
            throw new InvalidDataException(
                $"{label} exceeds the 8-MiB receipt safety limit.");
    }

    private static void RequireGitObjectByteLimit(long length, string type, string objectId)
    {
        if (length < 0 || length > MaximumGitObjectBytes)
            throw new InvalidDataException(
                $"Git {type} object {objectId} exceeds the 512-MiB object safety limit.");
    }

    internal static byte[] ReadBoundedReceiptFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        RequireReceiptByteLimit(stream.Length, "Caller receipt");
        var bytes = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
                throw new EndOfStreamException(
                    "Caller receipt became shorter during its bounded read.");
            offset += read;
        }
        if (stream.ReadByte() != -1)
            throw new InvalidDataException(
                "Caller receipt changed or exceeded the 8-MiB safety limit during its bounded read.");
        return bytes;
    }
#if VMBLAUNCHER_TEST_HOOKS
    internal static void RequireGitObjectByteLimitForTest(long length) =>
        RequireGitObjectByteLimit(length, "blob", new string('0', 40));
#endif
    private static byte[] ReadGitBlob(string root, string objectId)
        => ReadGitObject(root, "blob", objectId);

    private static byte[] ReadGitBlob(string root, string objectId, long expectedLength)
        => ReadGitObject(root, "blob", objectId, expectedLength);

    private static byte[] ReadGitObject(string root, string type, string objectId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(objectId, "^[0-9a-f]{40}$"))
            throw new InvalidDataException($"Git {type} object id is noncanonical.");
        var length = ReadGitObjectSize(root, type, objectId);
        return ReadGitObject(root, type, objectId, length);
    }

    private static byte[] ReadGitObject(
        string root,
        string type,
        string objectId,
        long expectedLength)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(objectId, "^[0-9a-f]{40}$"))
            throw new InvalidDataException($"Git {type} object id is noncanonical.");
        RequireGitObjectByteLimit(expectedLength, type, objectId);
        var bytes = RunBinaryExact(
            "git",
            new[] { "--no-replace-objects", "-C", root, "cat-file", type, objectId },
            expectedLength,
            $"Git {type} object {objectId}");
        if (!string.Equals(
                ComputeGitObjectId(type, bytes),
                objectId,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Git {type} object bytes do not match object id {objectId}.");
        return bytes;
    }

    private static long ReadGitObjectSize(string root, string type, string objectId)
    {
        var raw = Run(
            "git",
            new[] { "--no-replace-objects", "-C", root, "cat-file", "-s", objectId })
            .Trim();
        if (!long.TryParse(
                raw,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var length) ||
            raw != length.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new InvalidDataException(
                $"Git {type} object {objectId} reported a noncanonical byte length.");
        RequireGitObjectByteLimit(length, type, objectId);
        return length;
    }

    private static string ComputeGitObjectId(string type, byte[] bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes($"{type} {bytes.LongLength}\0"));
        hash.AppendData(bytes);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal sealed record GitTreeEntry(string Mode, string Type, string ObjectId, string Path);
    internal sealed record RawTreeEntry(string Mode, string Name, string ObjectId, byte[] NameBytes);

    private static byte[] QueryHostedReceipt(string repo, string tag, string assetName)
    {
        var encodedTag = Uri.EscapeDataString(tag);
        using var releaseDoc = JsonDocument.Parse(Run(
            "gh", new[] { "api", $"repos/{repo}/releases/tags/{encodedTag}" }));
        var assets = releaseDoc.RootElement.GetProperty("assets").EnumerateArray()
            .Where(a => a.GetProperty("name").GetString() == assetName)
            .ToArray();
        if (assets.Length != 1)
            throw new InvalidDataException($"Canonical release contains {assets.Length} exact assets named '{assetName}'.");
        var assetId = assets[0].GetProperty("id").GetInt64();
        return RunBinary("gh", new[]
        {
            "api", "-H", "Accept: application/octet-stream",
            $"repos/{repo}/releases/assets/{assetId}"
        });
    }

    private static LivePublicationSnapshot QueryLiveSnapshot(string root, string sourceCommit)
    {
        var top = Run("git", new[] { "-C", root, "rev-parse", "--show-toplevel" }).Trim();
        _ = RunBinary("git", new[] { "--no-replace-objects", "-C", top, "cat-file", "-e", $"{sourceCommit}^{{commit}}" });

        using var repoDoc = JsonDocument.Parse(Run("gh", new[] { "api", $"repos/{GitHubRepo}" }));
        var defaultBranch = repoDoc.RootElement.GetProperty("default_branch").GetString()
            ?? throw new InvalidDataException("GitHub omitted default_branch");

        using var pullsDoc = JsonDocument.Parse(Run("gh", new[]
        {
            "api", "-H", "Accept: application/vnd.github+json",
            $"repos/{GitHubRepo}/commits/{sourceCommit}/pulls?per_page=100"
        }));
        var mergedPr = pullsDoc.RootElement.EnumerateArray()
            .Where(p => p.TryGetProperty("merged_at", out var merged) && merged.ValueKind != JsonValueKind.Null)
            .Where(p => p.GetProperty("base").GetProperty("ref").GetString() == defaultBranch)
            .Where(p => SameSha(p.GetProperty("merge_commit_sha").GetString(), sourceCommit))
            .Select(p => p.GetProperty("number").GetInt32())
            .OrderBy(n => n)
            .FirstOrDefault();

        // GitHub's check-runs endpoint has no check-name query parameter. Fetch every
        // page so unrelated lifecycle checks cannot bury qa-gate beyond page one, then
        // independently recheck every authorization field in-process.
        var qa = ParseSuccessfulHostedQaCheck(Run("gh", new[]
        {
            "api", "--paginate", "--slurp",
            "-H", "Accept: application/vnd.github+json",
            $"repos/{GitHubRepo}/commits/{sourceCommit}/check-runs" +
                "?filter=all&per_page=100"
        }), sourceCommit);

        // The live default branch is the only mutable pointer relevant to
        // authorization. Local HEAD/index/worktree are never consulted for
        // source bytes, so a same-user commit checkout cannot swap the payload.
        using var refDoc = JsonDocument.Parse(Run("gh", new[] { "api", $"repos/{GitHubRepo}/git/ref/heads/{defaultBranch}" }));
        var defaultSha = refDoc.RootElement.GetProperty("object").GetProperty("sha").GetString()
            ?? throw new InvalidDataException("GitHub omitted default branch SHA");

        return new LivePublicationSnapshot(
            top, sourceCommit.ToLowerInvariant(), true, defaultBranch, defaultSha.ToLowerInvariant(),
            mergedPr, qa.Url, qa.Completed);
    }

    internal static HostedQaCheck ParseSuccessfulHostedQaCheck(
        string paginatedJson,
        string sourceCommit)
    {
        using var document = JsonDocument.Parse(paginatedJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub's paginated check-runs response was not an array of pages.");

        var candidates = new List<HostedQaCheck>();
        foreach (var page in document.RootElement.EnumerateArray())
        {
            if (page.ValueKind != JsonValueKind.Object ||
                !page.TryGetProperty("check_runs", out var runs) ||
                runs.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("GitHub returned a malformed check-runs page.");

            foreach (var check in runs.EnumerateArray())
            {
                if (check.GetProperty("name").GetString() != QaCheckName ||
                    !SameSha(check.GetProperty("head_sha").GetString(), sourceCommit) ||
                    check.GetProperty("status").GetString() != "completed" ||
                    check.GetProperty("conclusion").GetString() != "success" ||
                    !check.TryGetProperty("completed_at", out var completed) ||
                    completed.ValueKind != JsonValueKind.String)
                    continue;

                candidates.Add(new HostedQaCheck(
                    check.GetProperty("html_url").GetString() ?? "",
                    completed.GetDateTime().ToUniversalTime()));
            }
        }

        return candidates
            .OrderByDescending(check => check.Completed)
            .FirstOrDefault()
            ?? throw new InvalidDataException("No successful hosted qa-gate exists for the receipt source commit.");
    }

    private static string Run(string fileName, IReadOnlyList<string> arguments) =>
        Encoding.UTF8.GetString(RunBinary(fileName, arguments));

    private static byte[] RunBinary(string fileName, IReadOnlyList<string> arguments) =>
        RunBinaryBounded(
            fileName,
            arguments,
            MaximumProcessOutputBytes,
            $"{fileName} standard output");

    private static byte[] RunBinaryBounded(
        string fileName,
        IReadOnlyList<string> arguments,
        int maximumOutputBytes,
        string outputLabel)
    {
        if (maximumOutputBytes < 0 || maximumOutputBytes > MaximumProcessOutputBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumOutputBytes));
        MachineTransactionLease.RequireCurrent("Publication receipt process creation");
        ProcessTreeGuard.EnsureCurrentProcessContained();
        var psi = AuthorityProcessStartInfo(fileName, arguments);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var anyLimitExceeded = 0;
        void StopOversizedProcess()
        {
            Interlocked.Exchange(ref anyLimitExceeded, 1);
            TryKill(process);
        }
        var stdoutTask = CaptureBoundedAsync(
            process.StandardOutput.BaseStream,
            maximumOutputBytes,
            StopOversizedProcess,
            () => Volatile.Read(ref anyLimitExceeded) != 0);
        var stderrTask = CaptureBoundedAsync(
            process.StandardError.BaseStream,
            MaximumProcessErrorBytes,
            StopOversizedProcess,
            () => Volatile.Read(ref anyLimitExceeded) != 0);
        process.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (stdout.Exceeded)
            throw new InvalidDataException(
                $"{outputLabel} exceeds its {FormatMiB(maximumOutputBytes)} bounded capture limit.");
        if (stderr.Exceeded)
            throw new InvalidDataException(
                $"{fileName} standard error exceeds its 1-MiB bounded capture limit.");
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{fileName} exited {process.ExitCode}: {Encoding.UTF8.GetString(stderr.Bytes).Trim()}");
        return stdout.Bytes;
    }

    private static byte[] RunBinaryExact(
        string fileName,
        IReadOnlyList<string> arguments,
        long expectedOutputBytes,
        string outputLabel)
    {
        if (expectedOutputBytes < 0 || expectedOutputBytes > MaximumGitObjectBytes)
            throw new ArgumentOutOfRangeException(nameof(expectedOutputBytes));
        MachineTransactionLease.RequireCurrent("Publication receipt process creation");
        ProcessTreeGuard.EnsureCurrentProcessContained();
        var exactBuffer = new byte[checked((int)expectedOutputBytes)];
        var psi = AuthorityProcessStartInfo(fileName, arguments);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var anyLimitExceeded = 0;
        void StopOversizedProcess()
        {
            Interlocked.Exchange(ref anyLimitExceeded, 1);
            TryKill(process);
        }
        var stdoutTask = CaptureExactAsync(
            process.StandardOutput.BaseStream,
            exactBuffer,
            StopOversizedProcess,
            () => Volatile.Read(ref anyLimitExceeded) != 0);
        var stderrTask = CaptureBoundedAsync(
            process.StandardError.BaseStream,
            MaximumProcessErrorBytes,
            StopOversizedProcess,
            () => Volatile.Read(ref anyLimitExceeded) != 0);
        process.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (stdout.Exceeded)
            throw new InvalidDataException(
                $"{outputLabel} emitted more than its preflighted {expectedOutputBytes} bytes.");
        if (stderr.Exceeded)
            throw new InvalidDataException(
                $"{fileName} standard error exceeds its 1-MiB bounded capture limit.");
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{fileName} exited {process.ExitCode}: {Encoding.UTF8.GetString(stderr.Bytes).Trim()}");
        if (stdout.Length != exactBuffer.Length)
            throw new InvalidDataException(
                $"{outputLabel} emitted {stdout.Length} bytes after preflighting {exactBuffer.Length} bytes.");
        return exactBuffer;
    }

    private static ProcessStartInfo AuthorityProcessStartInfo(
        string fileName,
        IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        return psi;
    }

    private static async Task<BoundedCapture> CaptureBoundedAsync(
        Stream source,
        int maximumBytes,
        Action onExceeded,
        Func<bool> anyLimitExceeded)
    {
        using var captured = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[64 * 1024];
        var exceeded = false;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0) break;
                if (exceeded) continue;
                if (read > maximumBytes - captured.Length)
                {
                    exceeded = true;
                    onExceeded();
                    continue;
                }
                captured.Write(buffer, 0, read);
            }
        }
        catch (IOException) when (anyLimitExceeded())
        {
            // The process was killed after one of its pipes crossed a cap.
        }
        return new BoundedCapture(captured.ToArray(), exceeded);
    }

    private static async Task<ExactCapture> CaptureExactAsync(
        Stream source,
        byte[] destination,
        Action onExceeded,
        Func<bool> anyLimitExceeded)
    {
        var offset = 0;
        var exceeded = false;
        var overflowBuffer = new byte[64 * 1024];
        try
        {
            while (offset < destination.Length)
            {
                var count = Math.Min(64 * 1024, destination.Length - offset);
                var read = await source.ReadAsync(destination.AsMemory(offset, count)).ConfigureAwait(false);
                if (read == 0) return new ExactCapture(offset, exceeded);
                offset += read;
            }
            var extra = await source.ReadAsync(overflowBuffer).ConfigureAwait(false);
            if (extra != 0)
            {
                exceeded = true;
                onExceeded();
                while (await source.ReadAsync(overflowBuffer).ConfigureAwait(false) != 0) { }
            }
        }
        catch (IOException) when (anyLimitExceeded())
        {
            // The process was killed after one of its pipes crossed a cap.
        }
        return new ExactCapture(offset, exceeded);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static string FormatMiB(int bytes) =>
        bytes % (1024 * 1024) == 0
            ? $"{bytes / (1024 * 1024)}-MiB"
            : $"{bytes}-byte";
#if VMBLAUNCHER_TEST_HOOKS
    internal static byte[] RunBoundedProcessForTest(
        string fileName,
        IReadOnlyList<string> arguments,
        int maximumOutputBytes) =>
        RunBinaryBounded(fileName, arguments, maximumOutputBytes, $"{fileName} standard output");
#endif
    private sealed record BoundedCapture(byte[] Bytes, bool Exceeded);
    private sealed record ExactCapture(int Length, bool Exceeded);

    private static string NormalizeRoot(string path) =>
        Path.GetFullPath(path).TrimEnd('\\', '/');
    private static bool SameSha(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) &&
        string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool SameHash(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) &&
        string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool SameGitBlob(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) &&
        System.Text.RegularExpressions.Regex.IsMatch(a.Trim(), "^[0-9a-f]{40}$") &&
        string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}

internal sealed record HostedQaCheck(string Url, DateTime Completed);
