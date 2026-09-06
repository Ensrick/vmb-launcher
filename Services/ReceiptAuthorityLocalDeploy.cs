using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VmbLauncher.Services;

internal sealed record CommitQualifiedOutputFile(
    string Name,
    long Length,
    string Sha256);

internal sealed class VerifiedCommitQualifiedExpectedSet
{
    private int _consumed;
    private readonly DateTime _expiresAtUtc;

    internal string Mod { get; }
    internal string PublishedId { get; }
    internal string SourceCommit { get; }
    internal string AuthorityFingerprint { get; }
    internal string OutputFingerprint { get; }
    internal IReadOnlyList<CommitQualifiedOutputFile> Files { get; }

    internal VerifiedCommitQualifiedExpectedSet(
        string mod,
        string publishedId,
        string sourceCommit,
        string authorityFingerprint,
        DateTime expiresAtUtc,
        IReadOnlyList<CommitQualifiedOutputFile> files)
    {
        LocalExactSetDeployment.ValidateExpectedMap(mod, files);
        Mod = mod;
        PublishedId = publishedId;
        SourceCommit = sourceCommit;
        AuthorityFingerprint = authorityFingerprint;
        _expiresAtUtc = expiresAtUtc.ToUniversalTime();
        var immutableFiles = files
            .Select(file => new CommitQualifiedOutputFile(file.Name, file.Length, file.Sha256))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        Files = Array.AsReadOnly(immutableFiles);
        OutputFingerprint = Fingerprint(mod, publishedId, sourceCommit, immutableFiles);
    }

    internal bool IsFresh(string mod, DateTime nowUtc) =>
        string.Equals(Mod, mod, StringComparison.Ordinal) &&
        nowUtc.ToUniversalTime() < _expiresAtUtc;

    internal bool TryConsume(string mod, DateTime nowUtc)
    {
        if (!string.Equals(Mod, mod, StringComparison.Ordinal) ||
            nowUtc.ToUniversalTime() >= _expiresAtUtc)
            return false;
        return Interlocked.Exchange(ref _consumed, 1) == 0;
    }

    internal static string Fingerprint(
        string mod,
        string publishedId,
        string sourceCommit,
        IReadOnlyList<CommitQualifiedOutputFile> files)
    {
        // This public-to-the-assembly helper is also used during recovery.
        // Reassert the complete count/aggregate/canonical map bound here so no
        // caller can construct an authority fingerprint from an oversized map.
        LocalExactSetDeployment.ValidateExpectedMap(mod, files);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(mod);
            writer.Write(publishedId);
            writer.Write(sourceCommit);
            writer.Write(files.Count);
            foreach (var file in files.OrderBy(file => file.Name, StringComparer.Ordinal))
            {
                writer.Write(file.Name);
                writer.Write(file.Length);
                writer.Write(file.Sha256);
            }
            writer.Flush();
        }
        stream.Position = 0;
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

internal sealed record CommitQualifiedExpectedSetResult(bool Ok, string Message)
{
    internal VerifiedCommitQualifiedExpectedSet? Verified { get; init; }
}

internal sealed record CommitQualifiedReceiptProof(
    PublicationReceipt Receipt,
    CommitPublicationSnapshot Committed,
    string RepositoryRoot,
    string AuthorityFingerprint);

internal sealed record CommitQualifiedReceiptProofResult(bool Ok, string Message)
{
    internal CommitQualifiedReceiptProof? Proof { get; init; }
}

internal enum CommitQualifiedReceiptPurpose
{
    Publication,
    LocalDeploy,
}

public static partial class PublicationReceiptGate
{
    /// <summary>
    /// Authenticates the same canonical hosted receipt used by publication,
    /// independently reconstructs its source-commit authority proof, and
    /// returns only an immutable semantic output map. It does not trust or
    /// open mutable bundleV2 bytes; each consumer must capture those bytes
    /// under its own restrictive handles before acting.
    /// </summary>
    internal static CommitQualifiedExpectedSetResult AuthorizeReceiptAuthorityExpectedSet(
        string? receiptPath,
        ModInfo mod,
        string? configuredProjectRoot,
        DateTime nowUtc)
    {
        var qualified = AuthorizeCommitQualifiedReceipt(
            receiptPath,
            mod,
            configuredProjectRoot,
            nowUtc,
            CommitQualifiedReceiptPurpose.LocalDeploy);
        if (!qualified.Ok || qualified.Proof == null)
            return new(false, qualified.Message);

        try
        {
            var receipt = qualified.Proof.Receipt;
            var committed = qualified.Proof.Committed;
            if (receipt.BundleAuthority != "receipt" ||
                committed.BundleAuthority != "receipt")
                return new(false,
                    "Receipt-authority local deploy requires an explicit receipt bundle authority.");

            var files = committed.BundleFiles
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .Select(file => new CommitQualifiedOutputFile(
                    file.Path,
                    file.Length,
                    file.Sha256))
                .ToArray();
            return new(
                true,
                "Hosted receipt and commit-qualified schema-3 output map passed")
            {
                Verified = new VerifiedCommitQualifiedExpectedSet(
                    receipt.Mod,
                    committed.PublishedId,
                    receipt.SourceCommit,
                    qualified.Proof.AuthorityFingerprint,
                    receipt.ExpiresAtUtc,
                    files),
            };
        }
        catch (Exception ex)
        {
            return new(false,
                $"Independent receipt-authority deploy verification failed: {ex.Message}");
        }
    }

    /// <summary>
    /// One shared hosted/live/claim/commit authorization boundary for every
    /// receipt consumer. It returns only independently reconstructed commit
    /// state; upload and deploy must separately prove their mutable byte and
    /// destination seams.
    /// </summary>
    internal static CommitQualifiedReceiptProofResult AuthorizeCommitQualifiedReceipt(
        string? receiptPath,
        ModInfo mod,
        string? configuredProjectRoot,
        DateTime nowUtc,
        CommitQualifiedReceiptPurpose purpose)
    {
        MachineTransactionLease.RequireCurrent("Commit-qualified hosted receipt proof");
        if (string.IsNullOrWhiteSpace(receiptPath))
            return new(false,
                purpose == CommitQualifiedReceiptPurpose.LocalDeploy
                    ? "A canonical hosted --deployment-receipt from tools/ship/ship.ps1 is required; a claim alone is not authorization."
                    : "A canonical hosted --publication-receipt from tools/ship/ship.ps1 is required; a claim alone is not authorization.");
        if (!File.Exists(receiptPath))
            return new(false, $"Publication receipt does not exist: {receiptPath}");
        if (string.IsNullOrWhiteSpace(configuredProjectRoot))
            return new(false, "Configured project root is missing.");

        try
        {
            var callerBytes = ReadBoundedReceiptFile(receiptPath);
            var callerFingerprint = HashBytes(callerBytes);
            var untrusted = DeserializeReceipt(callerBytes);
            var expectedAssetPrefix = purpose == CommitQualifiedReceiptPurpose.LocalDeploy
                ? "deployment-receipt-"
                : "publication-receipt-";
            if (untrusted.Repository != GitHubRepo ||
                string.IsNullOrWhiteSpace(untrusted.ReleaseTag) ||
                !System.Text.RegularExpressions.Regex.IsMatch(untrusted.Mod, "^[a-z0-9_]+$") ||
                untrusted.ReceiptAssetName != expectedAssetPrefix + untrusted.Mod + ".json")
                return new(false, "Publication receipt release coordinates are invalid.");

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
                untrusted.Repository,
                untrusted.ReleaseTag,
                untrusted.ReceiptAssetName);
            var receipt = AuthenticateHostedReceipt(callerBytes, hostedBytes);
            var repositoryRoot = Run(
                "git", new[] { "-C", mod.ModDir, "rev-parse", "--show-toplevel" }).Trim();
            var committed = string.IsNullOrEmpty(receipt.BundleAuthority)
                ? ReadCommitSnapshot(repositoryRoot, receipt.SourceCommit, receipt.Mod)
                : ReadAuthorizedCommitSnapshot(
                    repositoryRoot,
                    receipt.SourceCommit,
                    receipt.Mod,
                    receipt.BundleAuthority);
            if (string.IsNullOrWhiteSpace(committed.PublishedId))
                throw new InvalidDataException(
                    "Exact source-commit itemV2.cfg has no published_id field.");

            var live = QueryLiveSnapshot(mod.ModDir, receipt.SourceCommit);
            var owner = ShipOwnerId.Resolve(live.SourceRoot);
            var claim = ShipClaimGate.Evaluate(
                ShipClaimGate.DefaultClaimsDir(),
                mod.Name,
                committed.Version,
                nowUtc,
                owner);
            var evaluated = EvaluateCommitQualifiedSnapshot(
                receipt,
                live,
                mod.Name,
                committed.Version,
                owner,
                nowUtc,
                callerFingerprint,
                HashBytes(hostedBytes),
                committed.ItemCfgSha256,
                committed.ItemCfgGitBlob,
                committed.PublishedId,
                committed.BundleFiles,
                committed.PreviewFile,
                purpose == CommitQualifiedReceiptPurpose.LocalDeploy
                    ? "local_deploy"
                    : committed.PublishedId == "0"
                        ? "workshop_bootstrap"
                        : "workshop_upload",
                claim,
                committed.BundleAuthorityProof);
            if (!evaluated.Ok)
                return new(false, evaluated.Message);
            return new(true, evaluated.Message)
            {
                Proof = new CommitQualifiedReceiptProof(
                    receipt,
                    committed,
                    repositoryRoot,
                    callerFingerprint),
            };
        }
        catch (Exception ex)
        {
            return new(false, $"Independent hosted receipt verification failed: {ex.Message}");
        }
    }
}
