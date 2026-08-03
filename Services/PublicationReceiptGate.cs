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
    PublicationPreviewFile PreviewFile);

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
public static class PublicationReceiptGate
{
    public const int Schema = 3;
    public const string GitHubRepo = "Ensrick/vermintide-2-tweaker";
    public const string QaCheckName = "qa-gate";
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(5);

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
        if (string.IsNullOrWhiteSpace(receiptPath))
            return new(false, "Workshop publication requires --publication-receipt from tools/ship/ship.ps1; a claim alone is not authorization.");
        if (!File.Exists(receiptPath))
            return new(false, $"Publication receipt does not exist: {receiptPath}");
        if (string.IsNullOrWhiteSpace(configuredProjectRoot))
            return new(false, "Configured project root is missing.");

        byte[] callerBytes;
        PublicationReceipt receipt;
        UploadPathLease? lease = null;
        try
        {
            callerBytes = File.ReadAllBytes(receiptPath);
            receipt = DeserializeReceipt(callerBytes);
        }
        catch (Exception ex)
        {
            return new(false, $"Publication receipt is unreadable: {ex.Message}");
        }
        if (receipt.Repository != GitHubRepo)
            return new(false, "Publication receipt repository is not canonical.");
        if (string.IsNullOrWhiteSpace(receipt.ReleaseTag) ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                receipt.ReceiptAssetName,
                "^publication-receipt-[a-z0-9_-]+\\.json$"))
            return new(false, "Publication receipt release coordinates are invalid.");

        try
        {
            var configuredProject = VmbProject.Resolve(configuredProjectRoot)
                ?? throw new InvalidDataException("Configured project root cannot be resolved.");
            var modParent = Directory.GetParent(Path.GetFullPath(mod.ModDir))?.FullName
                ?? throw new InvalidDataException("Mod directory has no parent.");
            if (!string.Equals(
                    NormalizeRoot(configuredProject.ModsDir),
                    NormalizeRoot(modParent),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Mod directory is outside the configured project.");

            var hostedBytes = QueryHostedReceipt(
                receipt.Repository, receipt.ReleaseTag, receipt.ReceiptAssetName);
            _ = DeserializeReceipt(hostedBytes);

            // Read the immutable Git object database, never the mutable index or
            // working tree. The receipt's source_commit selects the tree and
            // every proof below is reconstructed from that tree's exact blobs.
            var repositoryRoot = Run(
                "git", new[] { "-C", mod.ModDir, "rev-parse", "--show-toplevel" }).Trim();
            var committed = ReadCommitSnapshot(
                repositoryRoot, receipt.SourceCommit, receipt.Mod);
            if (string.IsNullOrWhiteSpace(committed.PublishedId))
                throw new InvalidDataException(
                    "Exact source-commit itemV2.cfg has no published_id field.");

            lease = UploadPathLease.Capture(staged, ugcToolPath);
            var expectedStagedCfgText =
                UploadStager.BuildStagedCfgTextFromSourceCfg(
                    committed.ItemCfgText, receipt.Mod, committed.PreviewFile.Path);
            var expectedStagedCfgBytes = Encoding.UTF8.GetBytes(expectedStagedCfgText);
            var expectedStagedCfgHash = HashBytes(expectedStagedCfgBytes);

            // Query GitHub authority for the receipt-selected immutable commit.
            // Local HEAD and worktree cleanliness are intentionally irrelevant:
            // neither is an input to the authorized byte snapshot.
            var live = QueryLiveSnapshot(mod.ModDir, receipt.SourceCommit);
            var owner = ShipOwnerId.Resolve(live.SourceRoot);
            var claim = ShipClaimGate.Evaluate(
                ShipClaimGate.DefaultClaimsDir(), mod.Name, committed.Version, nowUtc, owner);

            var evaluated = EvaluateSnapshot(
                receipt,
                live,
                mod.Name,
                committed.Version,
                owner,
                nowUtc,
                HashBytes(callerBytes),
                HashBytes(hostedBytes),
                committed.ItemCfgSha256,
                committed.ItemCfgGitBlob,
                committed.PublishedId,
                committed.BundleFiles,
                committed.PreviewFile,
                expectedStagedCfgHash,
                lease.CfgSha256,
                lease.BundleFiles,
                lease.PreviewFile,
                claim);
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
                    expectedSourceRoot: repositoryRoot,
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
        ShipClaimGate.Evaluation claim)
    {
        if (!SameHash(callerReceiptHash, hostedReceiptHash))
            return new(false, "Caller receipt bytes do not match the independently downloaded GitHub release asset.");
        var bootstrap = sourcePublishedId == "0";
        var expectedPurpose = bootstrap ? "workshop_bootstrap" : "workshop_upload";
        if (receipt.Schema != Schema || receipt.Purpose != expectedPurpose)
            return new(false, "Receipt schema or purpose is not canonical.");
        if (receipt.Repository != GitHubRepo ||
            string.IsNullOrWhiteSpace(receipt.ReleaseTag) ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                receipt.ReceiptAssetName,
                "^publication-receipt-[a-z0-9_-]+\\.json$"))
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
        var sourceResult = CompareBundleFiles(receipt.BundleFiles, sourceBundles, "source commit", requireGitBlob: true);
        if (!sourceResult.Ok) return sourceResult;
        var sourcePreviewResult = ComparePreviewFile(receipt.PreviewFile, sourcePreview, "source commit", requireGitBlob: true);
        if (!sourcePreviewResult.Ok) return sourcePreviewResult;
        if (!SameHash(expectedStagedCfgHash, actualStagedCfgHash))
            return new(false, "SDK-staged item.cfg is not the byte-exact canonical cfg derived from verified source.");
        var stagedResult = CompareBundleFiles(sourceBundles, stagedBundles, "SDK-staged content");
        if (!stagedResult.Ok) return stagedResult;
        var stagedPreviewResult = ComparePreviewFile(sourcePreview, stagedPreview, "SDK-staged");
        if (!stagedPreviewResult.Ok) return stagedPreviewResult;

        var mode = bootstrap
            ? "first-upload bootstrap"
            : "existing-item upload";
        return new(
            true,
            $"GitHub-hosted receipt, live authorization, source-commit blobs, and pinned SDK staging bytes passed ({mode})");
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

    private static PublicationReceipt DeserializeReceipt(byte[] bytes) =>
        JsonSerializer.Deserialize<PublicationReceipt>(bytes, JsonOptions)
        ?? throw new InvalidDataException("receipt JSON was empty");

    private static string HashBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static CommitPublicationSnapshot ReadCommitSnapshot(
        string repositoryRoot,
        string sourceCommit,
        string modName)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                sourceCommit, "^[0-9a-f]{40}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new InvalidDataException("Receipt source_commit is not a full Git commit SHA.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(modName, "^[a-z0-9_]+$"))
            throw new InvalidDataException("Receipt mod name is not canonical.");

        var root = NormalizeRoot(repositoryRoot);
        _ = RunBinary("git", new[] { "-C", root, "cat-file", "-e", $"{sourceCommit}^{{commit}}" });
        var commitBytes = RunBinary("git", new[] { "-C", root, "cat-file", "commit", sourceCommit });
        if (!string.Equals(
                ComputeGitObjectId("commit", commitBytes),
                sourceCommit,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Local source commit object bytes do not match source_commit.");
        var treeBytes = RunBinary("git", new[]
        {
            "-C", root, "ls-tree", "-r", "-z", "--full-tree", sourceCommit, "--", modName
        });
        var entries = ParseTreeEntries(treeBytes);

        var cfgRepoPath = $"{modName}/itemV2.cfg";
        var cfgEntry = RequireBlob(entries, cfgRepoPath);
        var cfgBytes = ReadGitBlob(root, cfgEntry.ObjectId);
        var cfgText = Encoding.UTF8.GetString(cfgBytes);

        var bundlePrefix = $"{modName}/bundleV2/";
        var bundles = entries.Values
            .Where(entry => entry.Path.StartsWith(bundlePrefix, StringComparison.Ordinal))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .Select(entry =>
            {
                RequireRegularBlob(entry, entry.Path);
                var bytes = ReadGitBlob(root, entry.ObjectId);
                return new PublicationBundleFile
                {
                    Path = entry.Path[bundlePrefix.Length..],
                    Length = bytes.LongLength,
                    Sha256 = HashBytes(bytes),
                    GitBlob = entry.ObjectId,
                };
            })
            .ToList();
        if (bundles.Count == 0)
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

    private static Dictionary<string, GitTreeEntry> ParseTreeEntries(byte[] treeBytes)
    {
        var result = new Dictionary<string, GitTreeEntry>(StringComparer.Ordinal);
        var raw = Encoding.UTF8.GetString(treeBytes);
        foreach (var record in raw.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            if (tab <= 0) throw new InvalidDataException("Git tree output is malformed.");
            var header = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (header.Length != 3)
                throw new InvalidDataException("Git tree entry header is malformed.");
            var entry = new GitTreeEntry(header[0], header[1], header[2].ToLowerInvariant(), record[(tab + 1)..]);
            if (!result.TryAdd(entry.Path, entry))
                throw new InvalidDataException($"Git tree contains duplicate path '{entry.Path}'.");
        }
        return result;
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

    private static byte[] ReadGitBlob(string root, string objectId)
    {
        var bytes = RunBinary("git", new[] { "-C", root, "cat-file", "blob", objectId });
        if (!string.Equals(
                ComputeGitObjectId("blob", bytes),
                objectId,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Git blob object bytes do not match object id {objectId}.");
        return bytes;
    }

    private static string ComputeGitObjectId(string type, byte[] bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes($"{type} {bytes.LongLength}\0"));
        hash.AppendData(bytes);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed record GitTreeEntry(string Mode, string Type, string ObjectId, string Path);

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
        _ = RunBinary("git", new[] { "-C", top, "cat-file", "-e", $"{sourceCommit}^{{commit}}" });

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

        // Server-side check_name filter plus the maximum page size: the unfiltered
        // first page holds 30 runs, so unrelated checks on a busy commit could bury
        // the qa-gate run and fail the gate closed on a fully authorized ship.
        // The client-side name/head_sha/status/conclusion filters below stay as an
        // independent recheck of whatever the API returns.
        using var checksDoc = JsonDocument.Parse(Run("gh", new[]
        {
            "api", "-H", "Accept: application/vnd.github+json",
            $"repos/{GitHubRepo}/commits/{sourceCommit}/check-runs" +
                $"?check_name={Uri.EscapeDataString(QaCheckName)}&per_page=100"
        }));
        var qa = checksDoc.RootElement.GetProperty("check_runs").EnumerateArray()
            .Where(c => c.GetProperty("name").GetString() == QaCheckName)
            .Where(c => SameSha(c.GetProperty("head_sha").GetString(), sourceCommit))
            .Where(c => c.GetProperty("status").GetString() == "completed")
            .Where(c => c.GetProperty("conclusion").GetString() == "success")
            .Where(c => c.TryGetProperty("completed_at", out var completed) && completed.ValueKind == JsonValueKind.String)
            .Select(c => new
            {
                Url = c.GetProperty("html_url").GetString() ?? "",
                Completed = c.GetProperty("completed_at").GetDateTime().ToUniversalTime(),
            })
            .OrderByDescending(c => c.Completed)
            .FirstOrDefault();
        if (qa == null) throw new InvalidDataException("No successful hosted qa-gate exists for the receipt source commit.");

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

    private static string Run(string fileName, IReadOnlyList<string> arguments) =>
        Encoding.UTF8.GetString(RunBinary(fileName, arguments));

    private static byte[] RunBinary(string fileName, IReadOnlyList<string> arguments)
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
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        using var output = new MemoryStream();
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(output);
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} exited {process.ExitCode}: {stderrTask.Result.Trim()}");
        return output.ToArray();
    }

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
