using VmbLauncher.Cli;

namespace VmbLauncher.Tests;

public class CapabilitiesTests
{
    [Fact]
    public void PublicationCapabilitiesAdvertiseCommitBlobSchemaThreeBoundary()
    {
        var lines = CapabilitiesCommand.Lines();
        Assert.Contains("capability_schema=1", lines);
        Assert.Contains("publication_receipt_schema=3", lines);
        Assert.Contains(
            lines,
            line => line.Contains("hosted-publication-receipt-v3", StringComparison.Ordinal) &&
                    line.Contains("locked-upload-snapshot-v1", StringComparison.Ordinal) &&
                    line.Contains("git-commit-blob-snapshot-v1", StringComparison.Ordinal) &&
                    line.Contains("constrained-first-upload-bootstrap-v1", StringComparison.Ordinal) &&
                    line.Contains("machine-transaction-lease-v1", StringComparison.Ordinal) &&
                    line.Contains("crash-safe-upload-acl-journal-v1", StringComparison.Ordinal));
    }
}
