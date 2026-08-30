using VmbLauncher.Cli;
using System.IO;
using System.Security.Cryptography;

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
        Assert.Equal(1, parsed.PublicationReceiptCount);
        Assert.Empty(parsed.Unknown);
    }

    [Fact]
    public void Parse_PublicationReceiptMissingValueDoesNotConsumeFollowingFlag()
    {
        var parsed = CliArgs.Parse(new[]
        {
            "deploy", "modx", "--publication-receipt", "--no-remote"
        });

        Assert.Equal(1, parsed.PublicationReceiptCount);
        Assert.Null(parsed.PublicationReceiptPath);
        Assert.True(parsed.NoRemote);
        Assert.Contains("--publication-receipt (missing value)", parsed.Unknown);
    }

    [Fact]
    public void Parse_PublicationReceiptDuplicateEmptyCannotErasePresence()
    {
        var parsed = CliArgs.Parse(new[]
        {
            "deploy", "modx",
            "--publication-receipt", @"C:\temp\first.json",
            "--publication-receipt", "   "
        });

        Assert.Equal(2, parsed.PublicationReceiptCount);
        Assert.Equal(@"C:\temp\first.json", parsed.PublicationReceiptPath);
        Assert.Contains("--publication-receipt (duplicate)", parsed.Unknown);
        Assert.Contains("--publication-receipt (empty value)", parsed.Unknown);
    }

    [Theory]
    [InlineData("--deployment-receipt")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void Parse_ConfigMissingValueDoesNotConsumeFlagOrHelpToken(string boundary)
    {
        var parsed = CliArgs.Parse(new[]
        {
            "deploy", "modx", "--config", boundary, "--no-remote"
        });

        Assert.Equal(1, parsed.ConfigCount);
        Assert.Null(parsed.ConfigPath);
        Assert.Contains("--config (missing value)", parsed.Unknown);
        Assert.True(parsed.NoRemote);
        if (boundary == "--deployment-receipt")
            Assert.Equal(1, parsed.DeploymentReceiptCount);
        else
            Assert.True(parsed.Help);
    }

    [Theory]
    [InlineData("--publication-receipt", "--clean")]
    [InlineData("--publication-receipt", "--allow-public")]
    [InlineData("--publication-receipt", "--no-banner")]
    [InlineData("--publication-receipt", "--no-remote")]
    [InlineData("--publication-receipt", "--dry-run-title-rewrite")]
    [InlineData("--publication-receipt", "--config")]
    [InlineData("--publication-receipt", "--publication-receipt")]
    [InlineData("--publication-receipt", "--deployment-receipt")]
    [InlineData("--publication-receipt", "--help")]
    [InlineData("--publication-receipt", "-h")]
    [InlineData("--publication-receipt", "/?")]
    [InlineData("--deployment-receipt", "--clean")]
    [InlineData("--deployment-receipt", "--allow-public")]
    [InlineData("--deployment-receipt", "--no-banner")]
    [InlineData("--deployment-receipt", "--no-remote")]
    [InlineData("--deployment-receipt", "--dry-run-title-rewrite")]
    [InlineData("--deployment-receipt", "--config")]
    [InlineData("--deployment-receipt", "--publication-receipt")]
    [InlineData("--deployment-receipt", "--deployment-receipt")]
    [InlineData("--deployment-receipt", "--help")]
    [InlineData("--deployment-receipt", "-h")]
    [InlineData("--deployment-receipt", "/?")]
    public void Parse_ReceiptValueNeverConsumesRecognizedFlagOrHelpToken(
        string receiptFlag,
        string boundary)
    {
        var parsed = CliArgs.Parse(new[] { "deploy", "modx", receiptFlag, boundary });

        Assert.Contains($"{receiptFlag} (missing value)", parsed.Unknown);
        if (receiptFlag == "--publication-receipt")
            Assert.Null(parsed.PublicationReceiptPath);
        else
            Assert.Null(parsed.DeploymentReceiptPath);
    }

    [Theory]
    [InlineData("empty-publication")]
    [InlineData("missing-publication")]
    [InlineData("duplicate-publication")]
    [InlineData("wrong-receipt-flag")]
    public void MalformedOrWrongDeployReceiptFlags_AbortBeforeSettingsMutation(string scenario)
    {
        using var temp = new TempDir();
        var config = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(config, "sentinel-invalid-json");
        var args = scenario switch
        {
            "empty-publication" => new[]
            {
                "deploy", "modx", "--publication-receipt", "   ", "--config", config
            },
            "missing-publication" => new[]
            {
                "deploy", "modx", "--publication-receipt", "--no-remote", "--config", config
            },
            "duplicate-publication" => new[]
            {
                "deploy", "modx", "--publication-receipt", @"C:\temp\first.json",
                "--publication-receipt", "", "--config", config
            },
            _ => new[]
            {
                "deploy", "modx", "--no-remote", "--publication-receipt",
                @"C:\temp\receipt.json", "--config", config
            },
        };

        var result = CliDispatcher.Run(args);

        Assert.Equal(CliDispatcher.ExitBadUsage, result);
        Assert.Equal("sentinel-invalid-json", File.ReadAllText(config));
    }

    [Theory]
    [InlineData("config-consumes-deployment")]
    [InlineData("deployment-short-help")]
    [InlineData("deployment-windows-help")]
    [InlineData("publication-short-help")]
    [InlineData("publication-windows-help")]
    [InlineData("duplicate-deployment")]
    [InlineData("empty-deployment")]
    public void MalformedValueSequences_AbortBeforeSettingsOrWorkshopMutation(string scenario)
    {
        using var temp = new TempDir();
        var config = temp.Write("settings.json", "sentinel-settings-bytes");
        temp.Write(@"workshop\123\sentinel.mod_bundle", "sentinel-workshop-bytes");
        temp.CreateSubdir(@"workshop\123\empty-directory");
        var workshop = Path.Combine(temp.Path, "workshop");
        var settingsBefore = File.ReadAllBytes(config);
        var workshopBefore = Census(workshop);
        var args = scenario switch
        {
            "config-consumes-deployment" => new[]
            {
                "deploy", "modx", "--config", "--deployment-receipt", "--no-remote"
            },
            "deployment-short-help" => new[]
            {
                "deploy", "modx", "--deployment-receipt", "-h", "--config", config, "--no-remote"
            },
            "deployment-windows-help" => new[]
            {
                "deploy", "modx", "--deployment-receipt", "/?", "--config", config, "--no-remote"
            },
            "publication-short-help" => new[]
            {
                "upload", "modx", "--publication-receipt", "-h", "--config", config
            },
            "publication-windows-help" => new[]
            {
                "upload", "modx", "--publication-receipt", "/?", "--config", config
            },
            "duplicate-deployment" => new[]
            {
                "deploy", "modx", "--deployment-receipt", "first.json",
                "--deployment-receipt", "second.json", "--config", config, "--no-remote"
            },
            _ => new[]
            {
                "deploy", "modx", "--deployment-receipt", "", "--config", config, "--no-remote"
            },
        };

        var result = CliDispatcher.Run(args);

        Assert.Equal(CliDispatcher.ExitBadUsage, result);
        Assert.Equal(settingsBefore, File.ReadAllBytes(config));
        Assert.Equal(workshopBefore, Census(workshop));
    }

    [Fact]
    public void Parse_ReceiptAuthorityLocalDeployRequiresExplicitInputsToRemainVisible()
    {
        var parsed = CliArgs.Parse(new[]
        {
            "deploy", "modx", "--no-remote", "--deployment-receipt", @"C:\temp\receipt.json"
        });
        Assert.True(parsed.NoRemote);
        Assert.Equal(@"C:\temp\receipt.json", parsed.DeploymentReceiptPath);
        Assert.Equal(1, parsed.DeploymentReceiptCount);
        Assert.Empty(parsed.Unknown);
        Assert.Null(CliDispatcher.ValidateArgumentCombination(parsed));
    }

    [Fact]
    public void Parse_DeploymentReceiptMissingValueDoesNotConsumeFollowingFlag()
    {
        var parsed = CliArgs.Parse(new[]
        {
            "deploy", "modx", "--deployment-receipt", "--no-remote"
        });

        Assert.Equal(1, parsed.DeploymentReceiptCount);
        Assert.Null(parsed.DeploymentReceiptPath);
        Assert.True(parsed.NoRemote);
        Assert.Contains("--deployment-receipt (missing value)", parsed.Unknown);
        Assert.Contains("nonempty path", CliDispatcher.ValidateArgumentCombination(parsed), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DeploymentReceiptRejectsWhitespaceValue()
    {
        var parsed = CliArgs.Parse(new[]
        {
            "deploy", "modx", "--no-remote", "--deployment-receipt", "   "
        });

        Assert.Equal(1, parsed.DeploymentReceiptCount);
        Assert.Null(parsed.DeploymentReceiptPath);
        Assert.Contains("--deployment-receipt (empty value)", parsed.Unknown);
        Assert.Contains("nonempty path", CliDispatcher.ValidateArgumentCombination(parsed), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DeploymentReceiptRejectsDuplicateWithoutOverwritingFirstValue()
    {
        var parsed = CliArgs.Parse(new[]
        {
            "deploy", "modx", "--no-remote",
            "--deployment-receipt", @"C:\temp\first.json",
            "--deployment-receipt", @"C:\temp\second.json"
        });

        Assert.Equal(2, parsed.DeploymentReceiptCount);
        Assert.Equal(@"C:\temp\first.json", parsed.DeploymentReceiptPath);
        Assert.Contains("--deployment-receipt (duplicate)", parsed.Unknown);
        Assert.Contains("exactly once", CliDispatcher.ValidateArgumentCombination(parsed), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DeploymentReceiptDuplicateEmptyValueCannotEraseReceiptLane()
    {
        var parsed = CliArgs.Parse(new[]
        {
            "deploy", "modx", "--no-remote",
            "--deployment-receipt", @"C:\temp\first.json",
            "--deployment-receipt", ""
        });

        Assert.Equal(2, parsed.DeploymentReceiptCount);
        Assert.Equal(@"C:\temp\first.json", parsed.DeploymentReceiptPath);
        Assert.Contains("--deployment-receipt (duplicate)", parsed.Unknown);
        Assert.Contains("--deployment-receipt (empty value)", parsed.Unknown);
        Assert.Contains("exactly once", CliDispatcher.ValidateArgumentCombination(parsed), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateArgumentCombination_RejectsReceiptDeployWithoutNoRemoteBeforeMutation()
    {
        var parsed = CliArgs.Parse(new[]
        {
            "deploy", "modx", "--deployment-receipt", @"C:\temp\receipt.json"
        });
        var error = CliDispatcher.ValidateArgumentCombination(parsed);
        Assert.Contains("--no-remote", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateArgumentCombination_RejectsPublicationReceiptAsDeployAuthority()
    {
        var parsed = CliArgs.Parse(new[]
        {
            "deploy", "modx", "--no-remote", "--publication-receipt", @"C:\temp\receipt.json"
        });
        var error = CliDispatcher.ValidateArgumentCombination(parsed);
        Assert.Contains("distinct --deployment-receipt", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateArgumentCombination_RejectsDeploymentReceiptOnOtherVerb()
    {
        var parsed = CliArgs.Parse(new[]
        {
            "upload", "modx", "--no-remote", "--deployment-receipt", @"C:\temp\receipt.json"
        });

        var error = CliDispatcher.ValidateArgumentCombination(parsed);
        Assert.Contains("only with the deploy verb", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NoClaimIsRejectedAsUnknown()
    {
        var parsed = CliArgs.Parse(new[] { "upload", "modx", "--no-claim" });
        Assert.Contains("--no-claim", parsed.Unknown);
    }

    private static IReadOnlyList<string> Census(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (Directory.Exists(path)) return $"D:{relative}";
                using var stream = File.OpenRead(path);
                var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                return $"F:{relative}:{stream.Length}:{hash}";
            })
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
}
