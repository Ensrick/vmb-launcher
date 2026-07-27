using VmbLauncher.Cli;

namespace VmbLauncher.Tests;

public class CliArgsTests
{
    [Fact]
    public void Parse_PublicationReceiptPath()
    {
        var parsed = CliArgs.Parse(new[]
        {
            "upload", "modx", "--publication-receipt", @"C:\temp\receipt.json"
        });
        Assert.Equal(@"C:\temp\receipt.json", parsed.PublicationReceiptPath);
        Assert.Empty(parsed.Unknown);
    }

    [Fact]
    public void Parse_NoClaimIsRejectedAsUnknown()
    {
        var parsed = CliArgs.Parse(new[] { "upload", "modx", "--no-claim" });
        Assert.Contains("--no-claim", parsed.Unknown);
    }
}
