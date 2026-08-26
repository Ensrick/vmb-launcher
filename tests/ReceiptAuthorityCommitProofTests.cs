using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public class ReceiptAuthorityCommitProofTests : MutationTestBase
{
    private const string RootBundle = "0123456789abcdef.mod_bundle";
    private const string Attributes = "* text=auto\n*.cfg text eol=lf\n*.lua text eol=lf\n*.mod text eol=lf\n*.json text eol=lf\n*.ps1 text eol=crlf\n*.jpg binary\n*.mod_bundle binary\n";

    [Fact]
    public void ReadAuthorizedCommitSnapshot_ReconstructsReceiptAuthorityFromCommit()
    {
        using var fixture = CreateFixture();

        var snapshot = PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
            fixture.Path, fixture.Commit, "modx", "receipt");

        Assert.Equal("receipt", snapshot.BundleAuthority);
        Assert.Equal(2, snapshot.BundleFiles.Count);
        Assert.All(snapshot.BundleFiles, file => Assert.Equal("", file.GitBlob));
        Assert.Equal(RootBundle, snapshot.BundleAuthorityProof!.RootBundle);
        Assert.Equal("materialized_restrictive_handles", snapshot.BundleAuthorityProof.ByteSource);
        Assert.Equal(3, snapshot.BundleAuthorityProof.ReceiptSchema);
        Assert.Equal(fixture.Commit, snapshot.BundleAuthorityProof.SourceCommit);
    }

    [Fact]
    public void ReadAuthorizedCommitSnapshot_IgnoresWorkingTreeAttributeDrift()
    {
        using var fixture = CreateFixture();
        File.WriteAllText(
            Path.Combine(fixture.Path, ".gitattributes"),
            "*.ps1 text eol=lf\n",
            new UTF8Encoding(false));

        var snapshot = PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
            fixture.Path, fixture.Commit, "modx", "receipt");

        Assert.Equal("receipt", snapshot.BundleAuthority);
        Assert.Equal(2, snapshot.BundleFiles.Count);
    }

    [Fact]
    public void ReadAuthorizedCommitSnapshot_IgnoresMutableGitAttributeAndFilterConfig()
    {
        using var fixture = CreateFixture();
        var info = Path.Combine(fixture.Path, ".git", "info", "attributes");
        File.WriteAllText(info, "*.ps1 filter=fixture eol=lf\n", new UTF8Encoding(false));
        Git(fixture.Path, "config", "filter.fixture.smudge", "cmd /c exit 99");
        Git(fixture.Path, "config", "filter.fixture.required", "true");

        var snapshot = PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
            fixture.Path, fixture.Commit, "modx", "receipt");

        Assert.Equal("receipt", snapshot.BundleAuthority);
    }

    [Fact]
    public void ReadAuthorizedCommitSnapshot_RejectsUnsupportedCommittedFilterPolicy()
    {
        using var fixture = CreateFixture(
            attributesText: Attributes + "*.lua filter=fixture\n");

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
                fixture.Path, fixture.Commit, "modx", "receipt"));

        Assert.Contains("unsupported", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAuthorizedCommitSnapshot_RejectsAutoTextWithoutExplicitEol()
    {
        using var fixture = CreateFixture(
            attributesText: Attributes.Replace("*.lua text eol=lf\n", "", StringComparison.Ordinal));

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
                fixture.Path, fixture.Commit, "modx", "receipt"));

        Assert.Contains("deterministic", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAuthorizedCommitSnapshot_RejectsNoncanonicalTextBlob()
    {
        using var fixture = CreateFixture(luaText: "local MOD_VERSION = \"1.2.3-dev\"\r");

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
                fixture.Path, fixture.Commit, "modx", "receipt"));

        Assert.Contains("LF-only", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAuthorizedCommitSnapshot_RejectsUnsupportedAttributePattern()
    {
        using var fixture = CreateFixture(
            attributesText: Attributes + "[ab]*.lua text eol=lf\n");

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
                fixture.Path, fixture.Commit, "modx", "receipt"));

        Assert.Contains("unsupported", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("*.lua -text eol=crlf\n")]
    [InlineData("*.lua eol=crlf -text\n")]
    [InlineData("*.lua text=auto eol=crlf\n")]
    [InlineData("*.lua eol=crlf text=auto\n")]
    public void ReadAuthorizedCommitSnapshot_RejectsMixedCheckoutAttributeModes(string rule)
    {
        using var fixture = CreateFixture(attributesText: Attributes + rule);

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
                fixture.Path, fixture.Commit, "modx", "receipt"));

        Assert.Contains("unsupported", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("*.lua text eol=lf\r*.lua text eol=crlf\n")]
    [InlineData("*.lua\u00a0text eol=crlf\n")]
    public void ReadAuthorizedCommitSnapshot_RejectsNoncanonicalAttributeLexing(string rule)
    {
        using var fixture = CreateFixture(attributesText: Attributes + rule);

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
                fixture.Path, fixture.Commit, "modx", "receipt"));

        Assert.Contains("canonical", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAuthorizedCommitSnapshot_RejectsOverlongAttributeLineBeforeTrimming()
    {
        using var fixture = CreateFixture(
            attributesText: Attributes + "*.lua text eol=crlf" +
                new string(' ', 2048 - "*.lua text eol=crlf".Length) + "\n");

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
                fixture.Path, fixture.Commit, "modx", "receipt"));

        Assert.Contains("2048-byte", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseTreeObject_RejectsDuplicateFileDirectoryAndCaseCollisions()
    {
        var duplicateDirectory = TreeObject(
            ("40000", "same", (byte)1),
            ("40000", "same", (byte)2));
        var fileDirectory = TreeObject(
            ("100644", "same", (byte)1),
            ("40000", "same", (byte)2));
        var foldedDirectory = TreeObject(
            ("40000", "Foo", (byte)1),
            ("40000", "foo", (byte)2));

        Assert.Throws<InvalidDataException>(() => PublicationReceiptGate.ParseTreeObject(duplicateDirectory));
        Assert.Throws<InvalidDataException>(() => PublicationReceiptGate.ParseTreeObject(fileDirectory));
        Assert.Throws<InvalidDataException>(() => PublicationReceiptGate.ParseTreeObject(foldedDirectory));
    }

    [Fact]
    public void ParseTreeObject_RejectsNoncanonicalSiblingOrder()
    {
        var bytes = TreeObject(
            ("100644", "b.lua", (byte)1),
            ("100644", "a.lua", (byte)2));

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ParseTreeObject(bytes));

        Assert.Contains("canonical Git order", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAuthorizedCommitSnapshot_IgnoresMutableGitTreeReplacement()
    {
        using var fixture = CreateFixture();
        File.WriteAllText(
            Path.Combine(fixture.Path, ".gitattributes"),
            Attributes + "*.lua filter=fixture\n",
            new UTF8Encoding(false));
        Git(fixture.Path, "add", ".gitattributes");
        Git(fixture.Path, "commit", "-m", "replacement tree");
        var originalTree = Git(fixture.Path, "rev-parse", $"{fixture.Commit}^{{tree}}");
        var replacementTree = Git(fixture.Path, "rev-parse", "HEAD^{tree}");
        Git(fixture.Path, "replace", originalTree, replacementTree);

        var snapshot = PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
            fixture.Path, fixture.Commit, "modx", "receipt");

        Assert.Equal("receipt", snapshot.BundleAuthority);
    }

    [Fact]
    public void ReadAuthorizedCommitSnapshot_RejectsCorruptNestedTreeObject()
    {
        using var fixture = CreateFixture();
        var modTree = Git(fixture.Path, "rev-parse", $"{fixture.Commit}:modx");
        WriteLooseObject(fixture.Path, modTree, "tree", Array.Empty<byte>());

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
                fixture.Path, fixture.Commit, "modx", "receipt"));

        Assert.Contains("tree object bytes do not match", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseTreeObject_RejectsInvalidUtf8Name()
    {
        var bytes = Encoding.ASCII.GetBytes("100644 ")
            .Concat(new byte[] { 0xc3, 0x28, 0 })
            .Concat(new byte[20])
            .ToArray();

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ParseTreeObject(bytes));

        Assert.Contains("non-UTF-8", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("modx/AUX.lua")]
    [InlineData("modx/CON.txt")]
    [InlineData("modx/LPT9")]
    [InlineData("modx/COM¹.lua")]
    [InlineData("modx/lpt²")]
    [InlineData("modx/COM³.profile")]
    [InlineData("modx/trailing.")]
    [InlineData("modx/trailing ")]
    [InlineData("modx/alternate:data.lua")]
    [InlineData("modx/control\u0001.lua")]
    public void WindowsSourcePathValidation_RejectsUnmaterializableSegments(string path)
    {
        Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.RequireWindowsMaterializableRepoPath(path));
    }

    [Fact]
    public void ReadAuthorizedCommitSnapshot_PreservesExplicitTrackedAuthority()
    {
        using var fixture = CreateFixture(
            forceTrackBundle: true,
            inventoryAuthority: "tracked",
            ignoreText: "# tracked outputs\n");

        var snapshot = PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
            fixture.Path, fixture.Commit, "modx", "tracked");

        Assert.Equal("tracked", snapshot.BundleAuthority);
        Assert.All(snapshot.BundleFiles, file => Assert.Matches("^[0-9a-f]{40}$", file.GitBlob));
        Assert.Equal("git_commit_blobs", snapshot.BundleAuthorityProof!.ByteSource);
        Assert.Equal(0, snapshot.BundleAuthorityProof.ReceiptSchema);
        Assert.Equal("", snapshot.BundleAuthorityProof.BuildReceiptGitBlob);
    }

    [Fact]
    public void ReadAuthorizedCommitSnapshot_RejectsTrackedOutputInReceiptCommit()
    {
        using var fixture = CreateFixture(forceTrackBundle: true);

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
                fixture.Path, fixture.Commit, "modx", "receipt"));

        Assert.Contains("forbids tracked", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAuthorizedCommitSnapshot_RejectsSourceBlobTamperInBuildReceipt()
    {
        using var fixture = CreateFixture(receipt =>
            receipt.SourceFiles[0].GitBlob = new string('f', 40));

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
                fixture.Path, fixture.Commit, "modx", "receipt"));

        Assert.Contains("source proof mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAuthorizedCommitSnapshot_RejectsOutputFingerprintTamper()
    {
        using var fixture = CreateFixture(receipt =>
            receipt.OutputFingerprintSha256 = new string('f', 64));

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
                fixture.Path, fixture.Commit, "modx", "receipt"));

        Assert.Contains("output-map identity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAuthorizedCommitSnapshot_RejectsBuilderTamper()
    {
        using var fixture = CreateFixture(receipt => receipt.Builder.Name = "OtherBuilder");

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
                fixture.Path, fixture.Commit, "modx", "receipt"));

        Assert.Contains("builder identity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAuthorizedCommitSnapshot_RejectsBuilderVersionDrift()
    {
        using var fixture = CreateFixture(receipt => receipt.Builder.Version = "0.6.0+" + new string('f', 40));

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
                fixture.Path, fixture.Commit, "modx", "receipt"));

        Assert.Contains("builder identity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAuthorizedCommitSnapshot_RejectsNormalizationPolicyTamper()
    {
        using var fixture = CreateFixture(receipt =>
            receipt.NormalizationPolicy.FingerprintSha256 = new string('f', 64));

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
                fixture.Path, fixture.Commit, "modx", "receipt"));

        Assert.Contains("normalization fingerprint", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAuthorizedCommitSnapshot_RejectsIncompleteSchemaThreeShape()
    {
        using var fixture = CreateFixture(
            mutateReceiptJson: json => json.Replace(",\"excluded_outputs\":[]", "", StringComparison.Ordinal));

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
                fixture.Path, fixture.Commit, "modx", "receipt"));

        Assert.Contains("properties are missing", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAuthorizedCommitSnapshot_RejectsMissingScopedIgnoreRule()
    {
        using var fixture = CreateFixture(ignoreText: "# no receipt rule\n");

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
                fixture.Path, fixture.Commit, "modx", "receipt"));

        Assert.Contains("scoped ignore rule", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAuthorizedCommitSnapshot_RejectsInventoryAuthorityMismatch()
    {
        using var fixture = CreateFixture(inventoryAuthority: "tracked");

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ReadAuthorizedCommitSnapshot(
                fixture.Path, fixture.Commit, "modx", "receipt"));

        Assert.Contains("inventory authority", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerShellDataParser_RejectsExecutableInventoryTail()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            PowerShellDataParser.Parse("@{ Mods = @() }; Remove-Item 'C:\\\\unsafe'"));

        Assert.Contains("trailing or executable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerShellDataParser_RejectsCaseFoldedDuplicateKeys()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            PowerShellDataParser.Parse("@{ Mods = @(); mods = @() }"));

        Assert.Contains("duplicates key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerShellDataParser_RejectsVariableExpression()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            PowerShellDataParser.Parse("@{ Mods = $inventoryRows }"));

        Assert.Contains("variable expression", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("@{ A = 'x' B = 'y' }")]
    [InlineData("@('x' 'y')")]
    [InlineData("@{ A = 'x', B = 'y' }")]
    public void PowerShellDataParser_RejectsMissingOrInvalidSeparators(string text)
    {
        Assert.Throws<InvalidDataException>(() => PowerShellDataParser.Parse(text));
    }

    [Theory]
    [InlineData("@('x',,'y')")]
    [InlineData("@('x',)")]
    public void PowerShellDataParser_RejectsUnaryOrTrailingComma(string text)
    {
        var error = Assert.Throws<InvalidDataException>(() => PowerShellDataParser.Parse(text));

        Assert.Contains("comma", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerShellDataParser_AcceptsCommentsMultipleRowsAndExclusions()
    {
        var parsed = Assert.IsType<Dictionary<string, object?>>(PowerShellDataParser.Parse("""
            # inventory fixture
            @{
                Mods = @(
                    @{ Dir = 'first'; Public = $false; BundleAuthority = 'tracked'; RootBundle = 'aaaaaaaaaaaaaaaa.mod_bundle' }
                    @{
                        Dir = 'modx'; Public = $true; BundleAuthority = 'receipt'; RootBundle = '0123456789abcdef.mod_bundle';
                        BuildArtifactExclusions = @(
                            @{ Name = '1111111111111111.mod_bundle'; Sha256 = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'; Reason = 'fixture' }
                        )
                    }
                )
            }
            """));

        Assert.Equal(2, Assert.IsType<List<object?>>(parsed["Mods"]).Count);
    }

    private static ReceiptFixture CreateFixture(
        Action<BuildReceiptV3>? mutateReceipt = null,
        bool forceTrackBundle = false,
        string inventoryAuthority = "receipt",
        string? ignoreText = null,
        Func<string, string>? mutateReceiptJson = null,
        string? attributesText = null,
        string luaText = "local MOD_VERSION = \"1.2.3-dev\"\n")
    {
        var tmp = new TempDir();
        tmp.Write(".gitattributes", attributesText ?? Attributes);
        tmp.Write(".gitignore", ignoreText ?? "/modx/bundleV2/\n");
        tmp.Write(
            @"tools\mod-inventory.psd1",
            $"@{{ Mods = @( @{{ Dir = 'modx'; BundleAuthority = '{inventoryAuthority}'; RootBundle = '{RootBundle}'; BuildArtifactExclusions = @() }} ) }}\n");
        tmp.Write(
            @"modx\itemV2.cfg",
            "title = \"Mod X v1.2.3-dev\";\n" +
            "description = \"fixture\";\n" +
            "preview = \"preview.jpg\";\n" +
            "content = \"bundleV2\";\n" +
            "language = \"english\";\n" +
            "visibility = \"private\";\n" +
            "published_id = 123L;\n");
        tmp.Write(@"modx\preview.jpg", "preview-bytes");
        tmp.Write(@"modx\modx.mod", "descriptor-bytes");
        tmp.Write(@"modx\tool.ps1", "Write-Output 'fixture'\r\n");
        tmp.Write(
            @"modx\scripts\mods\modx\modx.lua",
            luaText);

        Git(tmp.Path, "init");
        Git(tmp.Path, "config", "user.email", "tests@example.invalid");
        Git(tmp.Path, "config", "user.name", "VMBLauncher Tests");
        Git(tmp.Path, "add", ".");
        Git(tmp.Path, "commit", "-m", "receipt source");
        var sourceCommit = Git(tmp.Path, "rev-parse", "HEAD");

        var sourceFiles = new[]
        {
            "itemV2.cfg",
            "modx.mod",
            "preview.jpg",
            "scripts/mods/modx/modx.lua",
            "tool.ps1",
        }.Select(relative => new BuildReceiptSourceFile
        {
            Path = relative,
            GitBlob = Git(tmp.Path, "rev-parse", $"{sourceCommit}:modx/{relative}"),
            BuildSha256 = FileSha(Path.Combine(tmp.Path, "modx", relative.Replace('/', Path.DirectorySeparatorChar))),
        }).OrderBy(file => file.Path, StringComparer.Ordinal).ToList();

        var rootPath = tmp.Write(@"modx\bundleV2\0123456789abcdef.mod_bundle", "root-bundle-bytes");
        var descriptorPath = tmp.Write(@"modx\bundleV2\modx.mod", "descriptor-bytes");
        var outputs = new List<BuildReceiptOutputFile>
        {
            new() { Filename = RootBundle, Length = new FileInfo(rootPath).Length, Sha256 = FileSha(rootPath) },
            new() { Filename = "modx.mod", Length = new FileInfo(descriptorPath).Length, Sha256 = FileSha(descriptorPath) },
        }.OrderBy(file => file.Filename, StringComparer.Ordinal).ToList();
        var receipt = new BuildReceiptV3
        {
            Schema = 3,
            Mod = "modx",
            SourceAlgorithm = "git-blob-build-byte-map-sha256-v2",
            SourceFingerprintSha256 = SourceFingerprint(sourceFiles),
            SourceFiles = sourceFiles,
            OutputAlgorithm = "vt2-normalized-bundle-output-set-sha256-v1",
            OutputFingerprintSha256 = OutputFingerprint(outputs),
            OutputFiles = outputs,
            RootBundle = RootBundle,
            RootBundleSha256 = outputs.Single(file => file.Filename == RootBundle).Sha256,
            Descriptor = new BuildReceiptDescriptor
            {
                Filename = "modx.mod",
                SourcePath = "modx.mod",
                Sha256 = outputs.Single(file => file.Filename == "modx.mod").Sha256,
            },
            Builder = new BuildReceiptBuilder
            {
                Name = "VMBLauncher",
                Version = typeof(PublicationReceiptGate).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                    .InformationalVersion,
            },
            NormalizationPolicy = new BuildReceiptNormalizationPolicy
            {
                Algorithm = "exact-build-artifact-exclusions-sha256-v1",
                FingerprintSha256 = Sha256(Array.Empty<byte>()),
                ExcludedOutputs = new(),
            },
        };
        mutateReceipt?.Invoke(receipt);
        var receiptJson = JsonSerializer.Serialize(receipt);
        if (mutateReceiptJson != null) receiptJson = mutateReceiptJson(receiptJson);
        tmp.Write(@"modx\.build-receipt.json", receiptJson + "\n");
        Git(tmp.Path, "add", "modx/.build-receipt.json");
        if (forceTrackBundle) Git(tmp.Path, "add", "-f", "modx/bundleV2");
        Git(tmp.Path, "commit", "-m", "receipt proof");
        return new ReceiptFixture(tmp, Git(tmp.Path, "rev-parse", "HEAD"));
    }

    private static string SourceFingerprint(IEnumerable<BuildReceiptSourceFile> files)
    {
        var builder = new StringBuilder();
        foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
            builder.Append(file.Path).Append('\0').Append(file.GitBlob).Append('\0')
                .Append(file.BuildSha256).Append('\n');
        return Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string OutputFingerprint(IEnumerable<BuildReceiptOutputFile> files)
    {
        var builder = new StringBuilder("vt2-normalized-bundle-output-set-sha256-v1\n");
        foreach (var file in files.OrderBy(file => file.Filename, StringComparer.Ordinal))
            builder.Append(file.Filename).Append('\0').Append(file.Length).Append('\0')
                .Append(file.Sha256).Append('\n');
        return Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string FileSha(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

    private static void WriteLooseObject(string root, string objectId, string type, byte[] payload)
    {
        var path = Path.Combine(root, ".git", "objects", objectId[..2], objectId[2..]);
        Assert.True(File.Exists(path), $"Expected loose Git object: {path}");
        File.SetAttributes(path, FileAttributes.Normal);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var compressed = new ZLibStream(file, CompressionLevel.SmallestSize);
        var header = Encoding.ASCII.GetBytes($"{type} {payload.Length}\0");
        compressed.Write(header);
        compressed.Write(payload);
    }

    private static byte[] TreeObject(params (string Mode, string Name, byte IdByte)[] entries)
    {
        using var stream = new MemoryStream();
        foreach (var entry in entries)
        {
            stream.Write(Encoding.ASCII.GetBytes(entry.Mode + " "));
            stream.Write(Encoding.UTF8.GetBytes(entry.Name));
            stream.WriteByte(0);
            stream.Write(Enumerable.Repeat(entry.IdByte, 20).ToArray());
        }
        return stream.ToArray();
    }

    private sealed class ReceiptFixture : IDisposable
    {
        private readonly TempDir _temp;
        internal string Path => _temp.Path;
        internal string Commit { get; }

        internal ReceiptFixture(TempDir temp, string commit)
        {
            _temp = temp;
            Commit = commit;
        }

        public void Dispose() => _temp.Dispose();
    }
}
