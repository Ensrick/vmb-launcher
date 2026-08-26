using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VmbLauncher.Services;

public sealed class PublicationBundleAuthorityProof
{
    [JsonPropertyName("authority")] public string Authority { get; set; } = "";
    [JsonPropertyName("source_commit")] public string SourceCommit { get; set; } = "";
    [JsonPropertyName("inventory_git_blob")] public string InventoryGitBlob { get; set; } = "";
    [JsonPropertyName("ignore_git_blob")] public string IgnoreGitBlob { get; set; } = "";
    [JsonPropertyName("root_bundle")] public string RootBundle { get; set; } = "";
    [JsonPropertyName("byte_source")] public string ByteSource { get; set; } = "";
    [JsonPropertyName("build_receipt_path")] public string BuildReceiptPath { get; set; } = "";
    [JsonPropertyName("build_receipt_git_blob")] public string BuildReceiptGitBlob { get; set; } = "";
    [JsonPropertyName("build_receipt_sha256")] public string BuildReceiptSha256 { get; set; } = "";
    [JsonPropertyName("receipt_schema")] public int ReceiptSchema { get; set; }
    [JsonPropertyName("source_fingerprint_sha256")] public string SourceFingerprintSha256 { get; set; } = "";
    [JsonPropertyName("output_algorithm")] public string OutputAlgorithm { get; set; } = "";
    [JsonPropertyName("output_fingerprint_sha256")] public string OutputFingerprintSha256 { get; set; } = "";
    [JsonPropertyName("builder_name")] public string BuilderName { get; set; } = "";
    [JsonPropertyName("builder_version")] public string BuilderVersion { get; set; } = "";
    [JsonPropertyName("normalization_policy_algorithm")] public string NormalizationPolicyAlgorithm { get; set; } = "";
    [JsonPropertyName("normalization_policy_fingerprint_sha256")] public string NormalizationPolicyFingerprintSha256 { get; set; } = "";
}

internal sealed class BuildReceiptV3
{
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("mod")] public string Mod { get; set; } = "";
    [JsonPropertyName("source_algorithm")] public string SourceAlgorithm { get; set; } = "";
    [JsonPropertyName("source_fingerprint_sha256")] public string SourceFingerprintSha256 { get; set; } = "";
    [JsonPropertyName("source_files")] public List<BuildReceiptSourceFile> SourceFiles { get; set; } = new();
    [JsonPropertyName("output_algorithm")] public string OutputAlgorithm { get; set; } = "";
    [JsonPropertyName("output_fingerprint_sha256")] public string OutputFingerprintSha256 { get; set; } = "";
    [JsonPropertyName("output_files")] public List<BuildReceiptOutputFile> OutputFiles { get; set; } = new();
    [JsonPropertyName("root_bundle")] public string RootBundle { get; set; } = "";
    [JsonPropertyName("root_bundle_sha256")] public string RootBundleSha256 { get; set; } = "";
    [JsonPropertyName("descriptor")] public BuildReceiptDescriptor Descriptor { get; set; } = new();
    [JsonPropertyName("builder")] public BuildReceiptBuilder Builder { get; set; } = new();
    [JsonPropertyName("normalization_policy")] public BuildReceiptNormalizationPolicy NormalizationPolicy { get; set; } = new();
}

internal sealed class BuildReceiptSourceFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("git_blob")] public string GitBlob { get; set; } = "";
    [JsonPropertyName("build_sha256")] public string BuildSha256 { get; set; } = "";
}

internal sealed class BuildReceiptOutputFile
{
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("length")] public long Length { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

internal sealed class BuildReceiptDescriptor
{
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("source_path")] public string SourcePath { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

internal sealed class BuildReceiptBuilder
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

internal sealed class BuildReceiptNormalizationPolicy
{
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "";
    [JsonPropertyName("fingerprint_sha256")] public string FingerprintSha256 { get; set; } = "";
    [JsonPropertyName("excluded_outputs")] public List<BuildReceiptExcludedOutput> ExcludedOutputs { get; set; } = new();
}

internal sealed class BuildReceiptExcludedOutput
{
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

public static partial class PublicationReceiptGate
{
    private const string OutputAlgorithm = "vt2-normalized-bundle-output-set-sha256-v1";
    private const string SourceAlgorithm = "git-blob-build-byte-map-sha256-v2";
    private const string NormalizationAlgorithm = "exact-build-artifact-exclusions-sha256-v1";

    internal static CommitPublicationSnapshot ReadAuthorizedCommitSnapshot(
        string repositoryRoot,
        string sourceCommit,
        string modName,
        string authority)
    {
        MachineTransactionLease.RequireCurrent("Receipt-authority commit proof");
        if (authority != "tracked" && authority != "receipt")
            throw new InvalidDataException($"Unsupported bundle authority '{authority}'.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(sourceCommit, "^[0-9a-f]{40}$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(modName, "^[a-z0-9_]+$"))
            throw new InvalidDataException("Receipt source commit or mod identity is noncanonical.");

        var root = NormalizeRoot(repositoryRoot);
        var entries = ReadVerifiedCommitTree(root, sourceCommit);
        var snapshot = ReadCommitSnapshot(
            root,
            sourceCommit,
            modName,
            allowEmptyBundleFiles: authority == "receipt",
            entries);
        var metadata = ReadAuthorityMetadata(root, modName, entries);
        if (metadata.Authority != authority)
            throw new InvalidDataException(
                $"Receipt bundle authority '{authority}' is not source-commit inventory authority '{metadata.Authority}'.");

        ValidateIgnoreState(metadata.IgnoreText, modName, authority);
        if (authority == "tracked")
        {
            var descriptor = ReadCommitBlobProof(
                root, entries, $"{modName}/{modName}.mod");
            var output = ValidateOutputSet(
                snapshot.BundleFiles,
                modName,
                metadata.RootBundle,
                descriptor.Sha256);
            var trackedProof = new PublicationBundleAuthorityProof
            {
                Authority = "tracked",
                SourceCommit = sourceCommit,
                InventoryGitBlob = metadata.InventoryGitBlob,
                IgnoreGitBlob = metadata.IgnoreGitBlob,
                RootBundle = metadata.RootBundle,
                ByteSource = "git_commit_blobs",
                OutputAlgorithm = output.Algorithm,
                OutputFingerprintSha256 = output.Fingerprint,
            };
            return snapshot with
            {
                BundleAuthority = authority,
                BundleAuthorityProof = trackedProof,
            };
        }

        if (snapshot.BundleFiles.Count != 0)
            throw new InvalidDataException(
                "Receipt bundle authority forbids tracked source-commit bundleV2 blobs.");

        var receiptPath = $"{modName}/.build-receipt.json";
        var receiptBlob = ReadCommitBlobProof(root, entries, receiptPath);
        BuildReceiptV3 buildReceipt;
        try
        {
            ValidateBuildReceiptJsonShape(receiptBlob.Bytes);
            buildReceipt = JsonSerializer.Deserialize<BuildReceiptV3>(receiptBlob.Bytes, JsonOptions)
                ?? throw new InvalidDataException("schema-3 build receipt JSON was empty");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException($"Committed schema-3 build receipt is unreadable: {ex.Message}", ex);
        }

        var receiptOutput = ValidateBuildReceipt(
            root,
            modName,
            metadata,
            buildReceipt,
            entries);
        var bundles = buildReceipt.OutputFiles.Select(file => new PublicationBundleFile
        {
            Path = file.Filename,
            Length = file.Length,
            Sha256 = file.Sha256,
            GitBlob = "",
        }).ToList();

        var receiptProof = new PublicationBundleAuthorityProof
        {
            Authority = "receipt",
            SourceCommit = sourceCommit,
            InventoryGitBlob = metadata.InventoryGitBlob,
            IgnoreGitBlob = metadata.IgnoreGitBlob,
            RootBundle = metadata.RootBundle,
            ByteSource = "materialized_restrictive_handles",
            BuildReceiptPath = receiptPath,
            BuildReceiptGitBlob = receiptBlob.GitBlob,
            BuildReceiptSha256 = receiptBlob.Sha256,
            ReceiptSchema = buildReceipt.Schema,
            SourceFingerprintSha256 = buildReceipt.SourceFingerprintSha256,
            OutputAlgorithm = receiptOutput.Algorithm,
            OutputFingerprintSha256 = receiptOutput.Fingerprint,
            BuilderName = buildReceipt.Builder.Name,
            BuilderVersion = buildReceipt.Builder.Version,
            NormalizationPolicyAlgorithm = buildReceipt.NormalizationPolicy.Algorithm,
            NormalizationPolicyFingerprintSha256 = buildReceipt.NormalizationPolicy.FingerprintSha256,
        };
        return snapshot with
        {
            BundleFiles = bundles,
            BundleAuthority = authority,
            BundleAuthorityProof = receiptProof,
        };
    }

    private static PublicationGateResult CompareBundleAuthorityProof(
        PublicationBundleAuthorityProof? expected,
        PublicationBundleAuthorityProof? actual,
        string authority,
        string sourceCommit)
    {
        if (expected == null || actual == null)
            return new(false, "Explicit bundle authority requires a complete independently reconstructed proof.");
        if (expected.Authority != authority || actual.Authority != authority ||
            !SameSha(expected.SourceCommit, sourceCommit) ||
            !SameSha(actual.SourceCommit, sourceCommit))
            return new(false, "Bundle-authority proof is not bound to the receipt source commit and authority.");

        var pairs = new (string Name, string Expected, string Actual)[]
        {
            ("inventory Git blob", expected.InventoryGitBlob, actual.InventoryGitBlob),
            ("ignore Git blob", expected.IgnoreGitBlob, actual.IgnoreGitBlob),
            ("root bundle", expected.RootBundle, actual.RootBundle),
            ("byte source", expected.ByteSource, actual.ByteSource),
            ("build receipt path", expected.BuildReceiptPath, actual.BuildReceiptPath),
            ("build receipt Git blob", expected.BuildReceiptGitBlob, actual.BuildReceiptGitBlob),
            ("build receipt SHA-256", expected.BuildReceiptSha256, actual.BuildReceiptSha256),
            ("source fingerprint", expected.SourceFingerprintSha256, actual.SourceFingerprintSha256),
            ("output algorithm", expected.OutputAlgorithm, actual.OutputAlgorithm),
            ("output fingerprint", expected.OutputFingerprintSha256, actual.OutputFingerprintSha256),
            ("builder name", expected.BuilderName, actual.BuilderName),
            ("builder version", expected.BuilderVersion, actual.BuilderVersion),
            ("normalization algorithm", expected.NormalizationPolicyAlgorithm, actual.NormalizationPolicyAlgorithm),
            ("normalization fingerprint", expected.NormalizationPolicyFingerprintSha256, actual.NormalizationPolicyFingerprintSha256),
        };
        foreach (var pair in pairs)
        {
            if (!string.Equals(pair.Expected, pair.Actual, StringComparison.Ordinal))
                return new(false, $"Bundle-authority {pair.Name} does not match the source-commit proof.");
        }
        if (expected.ReceiptSchema != actual.ReceiptSchema)
            return new(false, "Bundle-authority build-receipt schema does not match the source-commit proof.");

        if (!IsLowerGitObjectId(expected.InventoryGitBlob) || !IsLowerGitObjectId(expected.IgnoreGitBlob) ||
            !IsCanonicalRootBundle(expected.RootBundle) ||
            expected.OutputAlgorithm != OutputAlgorithm ||
            !IsLowerSha256(expected.OutputFingerprintSha256))
            return new(false, "Bundle-authority proof contains a noncanonical source/output identity.");

        if (authority == "tracked")
        {
            if (expected.ByteSource != "git_commit_blobs" ||
                expected.ReceiptSchema != 0 ||
                expected.BuildReceiptPath.Length != 0 ||
                expected.BuildReceiptGitBlob.Length != 0 ||
                expected.BuildReceiptSha256.Length != 0 ||
                expected.SourceFingerprintSha256.Length != 0 ||
                expected.BuilderName.Length != 0 ||
                expected.BuilderVersion.Length != 0 ||
                expected.NormalizationPolicyAlgorithm.Length != 0 ||
                expected.NormalizationPolicyFingerprintSha256.Length != 0)
                return new(false, "Tracked bundle-authority proof contains receipt-only fields.");
        }
        else
        {
            if (expected.ByteSource != "materialized_restrictive_handles" ||
                expected.BuildReceiptPath.Length == 0 ||
                !IsLowerGitObjectId(expected.BuildReceiptGitBlob) ||
                !IsLowerSha256(expected.BuildReceiptSha256) ||
                expected.ReceiptSchema != 3 ||
                !IsLowerSha256(expected.SourceFingerprintSha256) ||
                expected.BuilderName != "VMBLauncher" ||
                expected.BuilderVersion != CurrentBuilderVersion() ||
                expected.NormalizationPolicyAlgorithm != NormalizationAlgorithm ||
                !IsLowerSha256(expected.NormalizationPolicyFingerprintSha256))
                return new(false, "Receipt bundle-authority proof is incomplete or noncanonical.");
        }

        return new(true, "Bundle-authority proof matches the source commit.");
    }

    private static AuthorityMetadata ReadAuthorityMetadata(
        string repositoryRoot,
        string modName,
        IReadOnlyDictionary<string, GitTreeEntry> entries)
    {
        var inventory = ReadCommitBlobProof(
            repositoryRoot, entries, "tools/mod-inventory.psd1");
        var ignore = ReadCommitBlobProof(repositoryRoot, entries, ".gitignore");
        var inventoryText = StrictUtf8(inventory.Bytes, "Source-commit mod inventory");
        var root = PowerShellDataParser.Parse(inventoryText) as Dictionary<string, object?>
            ?? throw new InvalidDataException("Source-commit mod inventory root is not a hashtable.");
        if (!root.TryGetValue("Mods", out var modsValue) || modsValue is not List<object?> mods)
            throw new InvalidDataException("Source-commit mod inventory lacks a Mods array.");
        var matches = mods.OfType<Dictionary<string, object?>>()
            .Where(row => DataString(row, "Dir") == modName)
            .ToList();
        if (matches.Count != 1)
            throw new InvalidDataException(
                $"Source-commit mod inventory contains {matches.Count} exact rows for '{modName}'.");
        var selected = matches[0];
        var authority = DataString(selected, "BundleAuthority");
        var rootBundle = DataString(selected, "RootBundle");
        if (authority != "tracked" && authority != "receipt")
            throw new InvalidDataException($"Source-commit inventory authority '{authority}' is invalid.");
        if (!IsCanonicalRootBundle(rootBundle))
            throw new InvalidDataException($"Source-commit inventory root bundle '{rootBundle}' is invalid.");

        var exclusions = new List<BuildReceiptExcludedOutput>();
        if (selected.TryGetValue("BuildArtifactExclusions", out var exclusionsValue))
        {
            if (exclusionsValue is not List<object?> rows)
                throw new InvalidDataException("BuildArtifactExclusions is not an array.");
            foreach (var row in rows)
            {
                if (row is not Dictionary<string, object?> exclusion)
                    throw new InvalidDataException("BuildArtifactExclusions contains a non-hashtable row.");
                var name = DataString(exclusion, "Name");
                var sha256 = DataString(exclusion, "Sha256");
                var reason = DataString(exclusion, "Reason");
                if (!IsCanonicalBundleFile(name) || name == rootBundle || !IsLowerSha256(sha256) ||
                    string.IsNullOrWhiteSpace(reason))
                    throw new InvalidDataException($"Invalid source-commit normalization exclusion '{name}'.");
                exclusions.Add(new BuildReceiptExcludedOutput { Filename = name, Sha256 = sha256 });
            }
        }
        exclusions = exclusions.OrderBy(row => row.Filename, StringComparer.Ordinal).ToList();
        if (exclusions.Select(row => row.Filename).Distinct(StringComparer.Ordinal).Count() != exclusions.Count)
            throw new InvalidDataException("Source-commit normalization exclusions contain duplicate filenames.");

        return new AuthorityMetadata(
            authority,
            rootBundle,
            inventory.GitBlob,
            ignore.GitBlob,
            StrictUtf8(ignore.Bytes, "Source-commit .gitignore"),
            exclusions);
    }

    private static void ValidateIgnoreState(string text, string modName, string authority)
    {
        var rule = $"/{modName}/bundleV2/";
        var count = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Count(line => line == rule);
        if (authority == "receipt" && count != 1)
            throw new InvalidDataException(
                $"Receipt authority requires exactly one scoped ignore rule '{rule}' (found {count}).");
        if (authority == "tracked" && count != 0)
            throw new InvalidDataException(
                $"Tracked authority forbids scoped ignore rule '{rule}' (found {count}).");
    }

    private static OutputSetProof ValidateBuildReceipt(
        string repositoryRoot,
        string modName,
        AuthorityMetadata metadata,
        BuildReceiptV3 receipt,
        IReadOnlyDictionary<string, GitTreeEntry> entries)
    {
        if (receipt.Schema != 3 || receipt.Mod != modName || receipt.SourceAlgorithm != SourceAlgorithm ||
            !IsLowerSha256(receipt.SourceFingerprintSha256))
            throw new InvalidDataException("Committed build receipt schema/mod/source identity is invalid.");
        if (receipt.SourceFiles == null || receipt.SourceFiles.Count == 0)
            throw new InvalidDataException("Committed build receipt source_files map is empty.");

        var prefix = $"{modName}/";
        var sourceEntries = entries.Values
            .Where(entry => entry.Path.StartsWith(prefix, StringComparison.Ordinal))
            .Where(entry => IsBuildReceiptSourcePath(entry.Path[prefix.Length..]))
            .OrderBy(entry => entry.Path[prefix.Length..], StringComparer.Ordinal)
            .ToList();
        foreach (var entry in sourceEntries) RequireRegularBlob(entry, entry.Path);
        if (sourceEntries.Count != receipt.SourceFiles.Count)
            throw new InvalidDataException("Committed build receipt source file set differs from the source commit.");

        var fingerprint = new StringBuilder();
        var materialized = ReconstructCheckoutHashes(
            repositoryRoot, modName, entries, sourceEntries);
        for (var i = 0; i < sourceEntries.Count; i++)
        {
            var entry = sourceEntries[i];
            var relative = entry.Path[prefix.Length..];
            var declared = receipt.SourceFiles[i];
            if (declared.Path != relative || declared.GitBlob != entry.ObjectId ||
                !IsLowerSha256(declared.BuildSha256) ||
                !materialized.TryGetValue(relative, out var buildSha) ||
                declared.BuildSha256 != buildSha)
                throw new InvalidDataException($"Committed build receipt source proof mismatch: {relative}");
            fingerprint.Append(relative).Append('\0')
                .Append(entry.ObjectId).Append('\0')
                .Append(declared.BuildSha256).Append('\n');
        }
        var sourceFingerprint = HashBytes(Encoding.UTF8.GetBytes(fingerprint.ToString()));
        if (receipt.SourceFingerprintSha256 != sourceFingerprint)
            throw new InvalidDataException("Committed build receipt source fingerprint is invalid.");

        if (receipt.Descriptor.Filename != $"{modName}.mod" ||
            receipt.Descriptor.SourcePath != $"{modName}.mod" ||
            !IsLowerSha256(receipt.Descriptor.Sha256))
            throw new InvalidDataException("Committed build receipt descriptor identity is invalid.");
        var descriptorSource = receipt.SourceFiles.SingleOrDefault(
            file => file.Path == $"{modName}.mod")
            ?? throw new InvalidDataException("Committed source map lacks the mod descriptor.");
        if (receipt.Descriptor.Sha256 != descriptorSource.BuildSha256)
            throw new InvalidDataException("Build receipt output descriptor differs from its source descriptor.");

        var outputs = receipt.OutputFiles.Select(file => new PublicationBundleFile
        {
            Path = file.Filename,
            Length = file.Length,
            Sha256 = file.Sha256,
        }).ToList();
        var output = ValidateOutputSet(
            outputs, modName, receipt.RootBundle, receipt.Descriptor.Sha256);
        if (receipt.OutputAlgorithm != output.Algorithm ||
            receipt.OutputFingerprintSha256 != output.Fingerprint ||
            receipt.RootBundle != metadata.RootBundle ||
            receipt.RootBundleSha256 != output.RootSha256)
            throw new InvalidDataException("Committed build receipt output-map identity is invalid.");
        var declaredOrder = receipt.OutputFiles.Select(file => file.Filename);
        if (!declaredOrder.SequenceEqual(
                declaredOrder.OrderBy(name => name, StringComparer.Ordinal),
                StringComparer.Ordinal))
            throw new InvalidDataException("Committed build receipt output_files are not in canonical ordinal order.");

        if (receipt.Builder.Name != "VMBLauncher" ||
            receipt.Builder.Version != CurrentBuilderVersion())
            throw new InvalidDataException("Committed build receipt builder identity is invalid.");
        ValidateNormalizationPolicy(receipt.NormalizationPolicy, metadata.Exclusions, outputs);
        return output;
    }

    private static void ValidateBuildReceiptJsonShape(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        RequireExactJsonProperties(root, "schema-3 build receipt", new[]
        {
            "schema", "mod", "source_algorithm", "source_fingerprint_sha256", "source_files",
            "output_algorithm", "output_fingerprint_sha256", "output_files", "root_bundle",
            "root_bundle_sha256", "descriptor", "builder", "normalization_policy",
        });
        RequireJsonArray(root, "source_files", "schema-3 source files", element =>
            RequireExactJsonProperties(element, "schema-3 source file", new[]
            {
                "path", "git_blob", "build_sha256",
            }));
        RequireJsonArray(root, "output_files", "schema-3 output files", element =>
            RequireExactJsonProperties(element, "schema-3 output file", new[]
            {
                "filename", "length", "sha256",
            }));
        RequireExactJsonProperties(
            root.GetProperty("descriptor"), "schema-3 descriptor", new[]
            {
                "filename", "source_path", "sha256",
            });
        RequireExactJsonProperties(
            root.GetProperty("builder"), "schema-3 builder", new[] { "name", "version" });
        var policy = root.GetProperty("normalization_policy");
        RequireExactJsonProperties(policy, "schema-3 normalization policy", new[]
        {
            "algorithm", "fingerprint_sha256", "excluded_outputs",
        });
        RequireJsonArray(policy, "excluded_outputs", "schema-3 excluded outputs", element =>
            RequireExactJsonProperties(element, "schema-3 excluded output", new[]
            {
                "filename", "sha256",
            }));
    }

    private static void ValidatePublicationBundleAuthorityJsonShape(
        byte[] bytes,
        PublicationReceipt receipt)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Publication receipt root is not an object.");
        var properties = document.RootElement.EnumerateObject().ToList();
        var authorityCount = properties.Count(property => property.Name == "bundle_authority");
        var proofCount = properties.Count(property => property.Name == "bundle_authority_proof");
        if (authorityCount == 0 && proofCount == 0) return;
        if (authorityCount != 1 || proofCount != 1 ||
            (receipt.BundleAuthority != "tracked" && receipt.BundleAuthority != "receipt"))
            throw new InvalidDataException(
                "Explicit publication bundle authority requires one canonical discriminator and proof.");
        var proof = properties.Single(property => property.Name == "bundle_authority_proof").Value;
        RequireExactJsonProperties(proof, "publication bundle-authority proof", new[]
        {
            "authority", "source_commit", "inventory_git_blob", "ignore_git_blob", "root_bundle",
            "byte_source", "build_receipt_path", "build_receipt_git_blob", "build_receipt_sha256",
            "receipt_schema", "source_fingerprint_sha256", "output_algorithm",
            "output_fingerprint_sha256", "builder_name", "builder_version",
            "normalization_policy_algorithm", "normalization_policy_fingerprint_sha256",
        });
    }

    private static void RequireJsonArray(
        JsonElement parent,
        string property,
        string label,
        Action<JsonElement> validate)
    {
        var array = parent.GetProperty(property);
        if (array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{label} is not an array.");
        foreach (var element in array.EnumerateArray()) validate(element);
    }

    private static void RequireExactJsonProperties(
        JsonElement element,
        string label,
        IReadOnlyCollection<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{label} is not an object.");
        var actual = element.EnumerateObject().Select(property => property.Name).ToList();
        if (actual.Count != expected.Count ||
            actual.Distinct(StringComparer.Ordinal).Count() != actual.Count ||
            expected.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
            throw new InvalidDataException($"{label} properties are missing, duplicated, or noncanonical.");
    }

    private static void ValidateNormalizationPolicy(
        BuildReceiptNormalizationPolicy policy,
        IReadOnlyList<BuildReceiptExcludedOutput> expected,
        IReadOnlyList<PublicationBundleFile> outputs)
    {
        if (policy.Algorithm != NormalizationAlgorithm || policy.ExcludedOutputs == null)
            throw new InvalidDataException("Committed build receipt normalization policy is invalid.");
        var actual = policy.ExcludedOutputs;
        if (actual.Count != expected.Count)
            throw new InvalidDataException("Build receipt normalization policy differs from source inventory.");
        for (var i = 0; i < actual.Count; i++)
        {
            if (actual[i].Filename != expected[i].Filename || actual[i].Sha256 != expected[i].Sha256)
                throw new InvalidDataException("Build receipt normalization policy differs from source inventory.");
            if (outputs.Any(output => output.Path == actual[i].Filename))
                throw new InvalidDataException(
                    $"Normalized excluded output remains in complete output set: {actual[i].Filename}");
        }
        var builder = new StringBuilder();
        foreach (var row in actual)
            builder.Append(row.Filename).Append('\0').Append(row.Sha256).Append('\n');
        if (!IsLowerSha256(policy.FingerprintSha256) ||
            policy.FingerprintSha256 != HashBytes(Encoding.UTF8.GetBytes(builder.ToString())))
            throw new InvalidDataException("Build receipt normalization fingerprint is invalid.");
    }

    private static OutputSetProof ValidateOutputSet(
        IReadOnlyList<PublicationBundleFile> files,
        string modName,
        string rootBundle,
        string descriptorSha256)
    {
        if (files.Count == 0 || !IsCanonicalRootBundle(rootBundle) || !IsLowerSha256(descriptorSha256))
            throw new InvalidDataException("Complete bundle output map is empty or lacks canonical roots.");
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (!IsCanonicalLeaf(file.Path) ||
                (file.Path != $"{modName}.mod" && !IsCanonicalBundleFile(file.Path)) ||
                file.Length < 0 || !IsLowerSha256(file.Sha256) ||
                !exact.Add(file.Path) || !folded.Add(file.Path))
                throw new InvalidDataException($"Complete bundle output map contains an invalid record: '{file.Path}'.");
        }
        var descriptor = files.SingleOrDefault(file => file.Path == $"{modName}.mod")
            ?? throw new InvalidDataException("Complete bundle output map lacks its exact descriptor.");
        var root = files.SingleOrDefault(file => file.Path == rootBundle)
            ?? throw new InvalidDataException("Complete bundle output map lacks its declared root bundle.");
        if (descriptor.Sha256 != descriptorSha256)
            throw new InvalidDataException("Complete bundle output descriptor differs from source.");

        var ordered = files.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
        var builder = new StringBuilder(OutputAlgorithm).Append('\n');
        foreach (var file in ordered)
            builder.Append(file.Path).Append('\0')
                .Append(file.Length.ToString(CultureInfo.InvariantCulture)).Append('\0')
                .Append(file.Sha256).Append('\n');
        return new OutputSetProof(
            OutputAlgorithm,
            HashBytes(Encoding.UTF8.GetBytes(builder.ToString())),
            root.Sha256);
    }

    private static Dictionary<string, string> ReconstructCheckoutHashes(
        string repositoryRoot,
        string modName,
        IReadOnlyDictionary<string, GitTreeEntry> entries,
        IReadOnlyList<GitTreeEntry> sourceEntries)
    {
        var foldedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in sourceEntries)
        {
            RequireWindowsMaterializableRepoPath(entry.Path);
            if (!foldedPaths.Add(entry.Path))
                throw new InvalidDataException(
                    $"Committed source map contains a case-colliding path: {entry.Path}");
        }

        // Reconstruct the intentionally narrow checkout policy from committed
        // .gitattributes blobs ourselves. This avoids every mutable Git config,
        // $GIT_DIR/info/attributes, external filter, worktree, and temporary-path
        // input while retaining the LF/CRLF/binary semantics used by the build.
        var attributeEntries = entries.Values
            .Where(entry => entry.Path == ".gitattributes" ||
                (entry.Path.StartsWith($"{modName}/", StringComparison.Ordinal) &&
                 entry.Path.EndsWith("/.gitattributes", StringComparison.Ordinal)))
            .OrderBy(entry => entry.Path.Count(character => character == '/'))
            .ThenBy(entry => entry.Path, StringComparer.Ordinal)
            .ToList();
        if (attributeEntries.Count == 0 || attributeEntries[0].Path != ".gitattributes")
            throw new InvalidDataException("Committed source tree lacks a root .gitattributes policy.");
        var rules = new List<CheckoutAttributeRule>();
        foreach (var attributeEntry in attributeEntries)
        {
            RequireRegularBlob(attributeEntry, attributeEntry.Path);
            var directory = attributeEntry.Path == ".gitattributes"
                ? ""
                : attributeEntry.Path[..attributeEntry.Path.LastIndexOf('/')];
            rules.AddRange(ParseCheckoutAttributeRules(
                StrictUtf8(ReadGitBlob(NormalizeRoot(repositoryRoot), attributeEntry.ObjectId),
                    $"Committed {attributeEntry.Path}"),
                directory,
                attributeEntry.Path));
        }

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in sourceEntries)
        {
            var blob = ReadGitBlob(NormalizeRoot(repositoryRoot), entry.ObjectId);
            var policy = ResolveCheckoutPolicy(entry.Path, rules);
            var bytes = ApplyCheckoutPolicy(entry.Path, blob, policy);
            hashes.Add(
                entry.Path[(modName.Length + 1)..],
                HashBytes(bytes));
        }
        return hashes;
    }

    private static IReadOnlyList<CheckoutAttributeRule> ParseCheckoutAttributeRules(
        string text,
        string directory,
        string sourcePath)
    {
        if (text.Length >= 1_048_576)
            throw new InvalidDataException(
                $"Committed checkout policy exceeds the narrow one-MiB file limit: {sourcePath}.");
        if (text.Any(character => character == '\r' || character == '\0' ||
                (character < 0x20 && character is not '\n' and not '\t') || character > 0x7e))
            throw new InvalidDataException(
                $"Committed checkout policy is not canonical ASCII/LF text: {sourcePath}.");
        var rules = new List<CheckoutAttributeRule>();
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].Length >= 2048)
                throw new InvalidDataException(
                    $"Committed checkout policy line exceeds the narrow 2048-byte limit at {sourcePath}:{index + 1}.");
            var line = lines[index].Trim(' ', '\t');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2 || !IsSupportedAttributePattern(tokens[0]))
                throw new InvalidDataException(
                    $"Committed checkout policy is unsupported at {sourcePath}:{index + 1}.");

            var attributes = tokens.Skip(1).ToArray();
            CheckoutPolicyUpdate update;
            if (attributes.Length == 1 && attributes[0] == "text=auto")
                update = new(CheckoutTextMode.Auto, null);
            else if (attributes.Length == 1 && attributes[0] is "binary" or "-text")
                update = new(CheckoutTextMode.Binary, null);
            else if (attributes.Length == 2 &&
                     attributes.Count(value => value == "text") == 1)
            {
                var eol = attributes.First(value => value != "text");
                if (eol is not ("eol=lf" or "eol=crlf"))
                    throw new InvalidDataException(
                        $"Committed checkout policy attributes are unsupported at {sourcePath}:{index + 1}.");
                update = new(CheckoutTextMode.Text, eol[4..]);
            }
            else
                throw new InvalidDataException(
                    $"Committed checkout policy attributes are unsupported at {sourcePath}:{index + 1}.");
            rules.Add(new CheckoutAttributeRule(directory, tokens[0], update));
        }
        return rules;
    }

    private static CheckoutPolicy ResolveCheckoutPolicy(
        string repoPath,
        IReadOnlyList<CheckoutAttributeRule> rules)
    {
        var policy = new CheckoutPolicy();
        foreach (var rule in rules)
        {
            if (!MatchesAttributeRule(repoPath, rule)) continue;
            policy.Text = rule.Update.Text;
            policy.Eol = rule.Update.Eol;
        }
        return policy;
    }

    private static byte[] ApplyCheckoutPolicy(
        string repoPath,
        byte[] blob,
        CheckoutPolicy policy)
    {
        if (policy.Text == CheckoutTextMode.Binary) return blob;
        if (policy.Text == CheckoutTextMode.Auto && blob.Contains((byte)0)) return blob;
        if (policy.Text == CheckoutTextMode.Unspecified || policy.Text == CheckoutTextMode.Auto ||
            policy.Eol is not ("lf" or "crlf"))
            throw new InvalidDataException(
                $"Committed source path lacks a deterministic LF/CRLF/binary checkout policy: {repoPath}");
        if (blob.Contains((byte)0) || blob.Contains((byte)'\r'))
            throw new InvalidDataException(
                $"Committed text blob is not canonical LF-only content: {repoPath}");
        if (policy.Eol == "lf") return blob;

        using var converted = new MemoryStream(blob.Length + blob.Count(value => value == (byte)'\n'));
        foreach (var value in blob)
        {
            if (value == (byte)'\n') converted.WriteByte((byte)'\r');
            converted.WriteByte(value);
        }
        return converted.ToArray();
    }

    private static bool MatchesAttributeRule(string repoPath, CheckoutAttributeRule rule)
    {
        var prefix = rule.Directory.Length == 0 ? "" : rule.Directory + "/";
        if (!repoPath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var relative = repoPath[prefix.Length..];
        if (relative.Length == 0) return false;
        if (rule.Pattern == "*") return true;
        if (rule.Pattern.StartsWith("*.", StringComparison.Ordinal))
            return Path.GetFileName(relative).EndsWith(rule.Pattern[1..], StringComparison.Ordinal);
        return rule.Pattern.Contains('/')
            ? relative == rule.Pattern
            : Path.GetFileName(relative) == rule.Pattern;
    }

    private static bool IsSupportedAttributePattern(string pattern)
    {
        if (pattern.Length == 0 || pattern.StartsWith('!') || pattern.StartsWith('/') ||
            pattern.EndsWith('/') || pattern.Contains('\\') || pattern.Contains('[') ||
            pattern.Contains('?')) return false;
        var starCount = pattern.Count(character => character == '*');
        return starCount == 0 || pattern == "*" ||
            (starCount == 1 && pattern.StartsWith("*.", StringComparison.Ordinal) &&
             !pattern[2..].Contains('/'));
    }

    internal static void RequireWindowsMaterializableRepoPath(string repoPath)
    {
        if (!IsCanonicalRepoPath(repoPath))
            throw new InvalidDataException($"Committed source path is not canonical: {repoPath}");
        foreach (var segment in repoPath.Split('/'))
        {
            if (segment.Length > 255 || segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.Any(character => character < 0x20 || "<>:\"/\\|?*".Contains(character)))
                throw new InvalidDataException(
                    $"Committed source path is not materializable on Windows: {repoPath}");
            var device = segment.Split('.')[0];
            if (device.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(
                    device, "^(COM|LPT)([1-9]|[¹²³])$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidDataException(
                    $"Committed source path uses a reserved Windows device name: {repoPath}");
        }
    }

    private static CommitBlobProof ReadCommitBlobProof(
        string repositoryRoot,
        IReadOnlyDictionary<string, GitTreeEntry> entries,
        string repoPath)
    {
        if (!IsCanonicalRepoPath(repoPath))
            throw new InvalidDataException($"Source-commit proof path is not canonical: '{repoPath}'.");
        var entry = RequireBlob(entries, repoPath);
        var bytes = ReadGitBlob(NormalizeRoot(repositoryRoot), entry.ObjectId);
        return new CommitBlobProof(entry.ObjectId, bytes, HashBytes(bytes));
    }

    private static string StrictUtf8(byte[] bytes, string label)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException($"{label} is not strict UTF-8.", ex);
        }
    }

    private static bool IsBuildReceiptSourcePath(string relative) =>
        relative.Length != 0 && relative != ".build-receipt.json" &&
        !relative.StartsWith("bundleV2/", StringComparison.Ordinal);

    private static bool IsCanonicalRepoPath(string path) =>
        path.Length != 0 && !path.Contains('\\') && !Path.IsPathRooted(path) &&
        !path.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..");

    private static bool IsCanonicalLeaf(string name) =>
        name.Length != 0 && name == Path.GetFileName(name) &&
        name is not "." and not ".." && name == name.TrimEnd(' ', '.') &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool IsCanonicalBundleFile(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "^[0-9a-f]{16}\\.mod_bundle$");

    private static bool IsCanonicalRootBundle(string name) => IsCanonicalBundleFile(name);
    private static bool IsLowerSha256(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-f]{64}$");
    private static bool IsLowerGitObjectId(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-f]{40}$");

    private static string CurrentBuilderVersion()
    {
        var version = typeof(PublicationReceiptGate).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrWhiteSpace(version) || version != version.Trim())
            throw new InvalidOperationException("Executing VMBLauncher has no canonical informational version.");
        return version;
    }

    private static string DataString(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is not string text)
            throw new InvalidDataException($"Source-commit inventory row lacks string property '{key}'.");
        return text;
    }

    private enum CheckoutTextMode
    {
        Unspecified,
        Auto,
        Text,
        Binary,
    }

    private sealed class CheckoutPolicy
    {
        internal CheckoutTextMode Text { get; set; }
        internal string? Eol { get; set; }
    }

    private sealed record CheckoutPolicyUpdate(CheckoutTextMode Text, string? Eol);

    private sealed record CheckoutAttributeRule(
        string Directory,
        string Pattern,
        CheckoutPolicyUpdate Update);

    private sealed record AuthorityMetadata(
        string Authority,
        string RootBundle,
        string InventoryGitBlob,
        string IgnoreGitBlob,
        string IgnoreText,
        IReadOnlyList<BuildReceiptExcludedOutput> Exclusions);

    private sealed record OutputSetProof(string Algorithm, string Fingerprint, string RootSha256);
    private sealed record CommitBlobProof(string GitBlob, byte[] Bytes, string Sha256);
}

internal sealed class PowerShellDataParser
{
    private readonly List<Token> _tokens;
    private int _position;

    private PowerShellDataParser(string text) => _tokens = Tokenize(text);

    internal static object? Parse(string text)
    {
        var parser = new PowerShellDataParser(text);
        parser.ConsumeStatementSeparators();
        var value = parser.ParseValue();
        parser.ConsumeStatementSeparators();
        if (parser.Peek().Kind != TokenKind.End)
            throw new InvalidDataException("PowerShell data contains trailing or executable content.");
        return value;
    }

    private object? ParseValue()
    {
        var token = Peek();
        if (token.Kind == TokenKind.HashStart) return ParseHashtable();
        if (token.Kind == TokenKind.ArrayStart) return ParseArray();
        _position++;
        return token.Kind switch
        {
            TokenKind.String => token.Value,
            TokenKind.True => true,
            TokenKind.False => false,
            _ => throw new InvalidDataException($"Unsupported PowerShell data value near token '{token.Value}'."),
        };
    }

    private Dictionary<string, object?> ParseHashtable()
    {
        Expect(TokenKind.HashStart);
        // PowerShell hashtable keys are case-insensitive. Mirror that semantic
        // so a case-only duplicate cannot be accepted here while the canonical
        // Import-PowerShellDataFile consumer would reject or reinterpret it.
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            ConsumeStatementSeparators();
            if (Peek().Kind == TokenKind.CloseBrace)
            {
                _position++;
                return result;
            }
            var key = Peek();
            if (key.Kind is not TokenKind.Identifier and not TokenKind.String)
                throw new InvalidDataException("PowerShell data hashtable key is not a constant name.");
            _position++;
            Expect(TokenKind.Equals);
            var value = ParseValue();
            if (!result.TryAdd(key.Value, value))
                throw new InvalidDataException($"PowerShell data hashtable duplicates key '{key.Value}'.");
            if (Peek().Kind == TokenKind.CloseBrace) continue;
            if (!ConsumeStatementSeparators())
                throw new InvalidDataException("PowerShell data hashtable entries require a newline or semicolon separator.");
        }
    }

    private List<object?> ParseArray()
    {
        Expect(TokenKind.ArrayStart);
        var result = new List<object?>();
        while (true)
        {
            ConsumeStatementSeparators();
            if (Peek().Kind == TokenKind.CloseParen)
            {
                _position++;
                return result;
            }
            result.Add(ParseValue());
            if (Peek().Kind == TokenKind.CloseParen) continue;
            if (Peek().Kind == TokenKind.Comma)
            {
                _position++;
                ConsumeStatementSeparators();
                if (Peek().Kind is TokenKind.Comma or TokenKind.CloseParen)
                    throw new InvalidDataException("PowerShell data array contains a leading, repeated, or trailing comma.");
                continue;
            }
            if (!ConsumeStatementSeparators())
                throw new InvalidDataException("PowerShell data array values require a newline, semicolon, or single comma separator.");
        }
    }

    private bool ConsumeStatementSeparators()
    {
        var consumed = false;
        while (Peek().Kind is TokenKind.Semicolon or TokenKind.Newline)
        {
            consumed = true;
            _position++;
        }
        return consumed;
    }

    private void Expect(TokenKind kind)
    {
        if (Peek().Kind != kind)
            throw new InvalidDataException($"PowerShell data expected {kind} near '{Peek().Value}'.");
        _position++;
    }

    private Token Peek() => _tokens[_position];

    private static List<Token> Tokenize(string text)
    {
        var result = new List<Token>();
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                result.Add(new(TokenKind.Newline, "\\n")); i++; continue;
            }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '#')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (i + 1 < text.Length && c == '@' && text[i + 1] == '{')
            {
                result.Add(new(TokenKind.HashStart, "@{")); i += 2; continue;
            }
            if (i + 1 < text.Length && c == '@' && text[i + 1] == '(')
            {
                result.Add(new(TokenKind.ArrayStart, "@(")); i += 2; continue;
            }
            if (c == '\'')
            {
                var builder = new StringBuilder();
                i++;
                var closed = false;
                while (i < text.Length)
                {
                    if (text[i] == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        builder.Append('\''); i += 2; continue;
                    }
                    if (text[i] == '\'') { i++; closed = true; break; }
                    builder.Append(text[i++]);
                }
                if (!closed) throw new InvalidDataException("PowerShell data has an unterminated string.");
                result.Add(new(TokenKind.String, builder.ToString()));
                continue;
            }
            var punctuation = c switch
            {
                '}' => TokenKind.CloseBrace,
                ')' => TokenKind.CloseParen,
                '=' => TokenKind.Equals,
                ';' => TokenKind.Semicolon,
                ',' => TokenKind.Comma,
                _ => TokenKind.End,
            };
            if (punctuation != TokenKind.End)
            {
                result.Add(new(punctuation, c.ToString())); i++; continue;
            }
            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                var start = i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '-')) i++;
                var value = text[start..i];
                if (value.StartsWith('$') && value is not "$true" and not "$false")
                    throw new InvalidDataException("PowerShell data contains a variable expression.");
                result.Add(new(
                    value == "$true" ? TokenKind.True : value == "$false" ? TokenKind.False : TokenKind.Identifier,
                    value));
                continue;
            }
            throw new InvalidDataException($"Unsupported PowerShell data character U+{(int)c:X4}.");
        }
        result.Add(new(TokenKind.End, ""));
        return result;
    }

    private enum TokenKind
    {
        End,
        HashStart,
        ArrayStart,
        CloseBrace,
        CloseParen,
        Equals,
        Semicolon,
        Comma,
        Newline,
        Identifier,
        String,
        True,
        False,
    }

    private sealed record Token(TokenKind Kind, string Value);
}
