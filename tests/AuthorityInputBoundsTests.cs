using System.IO;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public sealed class AuthorityInputBoundsTests : MutationTestBase
{
    [Fact]
    public void ReadBoundedReceiptFile_RejectsOversizedCallerBeforeAllocatingPayload()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "oversized-receipt.json");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(PublicationReceiptGate.MaximumReceiptBytes + 1L);

        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.ReadBoundedReceiptFile(path));

        Assert.Contains("Caller receipt", error.Message, StringComparison.Ordinal);
        Assert.Contains("8-MiB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedProcessCapture_RejectsOversizedHostedOrApiOutput()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.RunBoundedProcessForTest(
                "powershell.exe",
                new[]
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "$b = [byte[]]::new(4096); [Console]::OpenStandardOutput().Write($b, 0, $b.Length)",
                },
                maximumOutputBytes: 1024));

        Assert.Contains("bounded capture", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BoundedProcessCapture_RejectsOversizedStandardError()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.RunBoundedProcessForTest(
                "powershell.exe",
                new[]
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "$b = [byte[]]::new(1048577); [Console]::OpenStandardError().Write($b, 0, $b.Length)",
                },
                PublicationReceiptGate.MaximumProcessOutputBytes));

        Assert.Contains("standard error", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1-MiB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GitObjectPreflight_RejectsOversizedObjectBeforeProcessCaptureOrHash()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            PublicationReceiptGate.RequireGitObjectByteLimitForTest(
                PublicationReceiptGate.MaximumGitObjectBytes + 1L));

        Assert.Contains("512-MiB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthorityFingerprint_RejectsOver4096OutputsBeforeConstruction()
    {
        var files = Enumerable.Range(
                0,
                PublicationReceiptGate.MaximumSemanticMapEntries + 1)
            .Select(index => new CommitQualifiedOutputFile(
                $"{index:x16}.mod_bundle",
                1,
                new string('a', 64)))
            .ToArray();

        Assert.Throws<InvalidDataException>(() =>
            VerifiedCommitQualifiedExpectedSet.Fingerprint(
                "modx", "123", new string('b', 40), files));
    }

    [Fact]
    public void AuthorityFingerprint_RejectsOver32GiBAggregateBeforeConstruction()
    {
        var files = new[]
        {
            new CommitQualifiedOutputFile(
                "modx.mod",
                PublicationReceiptGate.MaximumSemanticMapBytes,
                new string('a', 64)),
            new CommitQualifiedOutputFile(
                "0123456789abcdef.mod_bundle",
                1,
                new string('b', 64)),
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            VerifiedCommitQualifiedExpectedSet.Fingerprint(
                "modx", "123", new string('c', 40), files));

        Assert.Contains("32-GiB", error.Message, StringComparison.Ordinal);
    }
}
