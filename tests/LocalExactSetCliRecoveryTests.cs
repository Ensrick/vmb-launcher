using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using VmbLauncher.Cli;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

[Collection("receipt-deploy-serial")]
public sealed class LocalExactSetCliRecoveryTests
{
    private const string Mod = "modx";
    private const string PublishedId = "123456789";

    [Fact]
    public void CliDeploy_RecoversDeadOwnerBeforeMissingProjectAndModDiscovery()
    {
        using var temp = new TempDir();
        var source = temp.CreateSubdir("source");
        var workshop = temp.CreateSubdir("workshop");
        var target = temp.CreateSubdir(Path.Combine("workshop", PublishedId));
        File.WriteAllText(Path.Combine(source, Mod + ".mod"), "new descriptor");
        File.WriteAllText(Path.Combine(source, "0123456789abcdef.mod_bundle"), "new bundle bytes");
        File.WriteAllText(Path.Combine(target, Mod + ".mod"), "old descriptor");
        File.WriteAllText(Path.Combine(target, "fedcba9876543210.mod_bundle"), "old bundle bytes");
        var prior = Census(target);
        var worker = FindTransactionWorker();
        var mutex = @"Local\VMBLauncher.Tests.CliReceiptRecovery." + Guid.NewGuid().ToString("N");
        var ownerRecord = Path.Combine(temp.Path, "dead-owner.json");
        var crashMarker = Path.Combine(temp.Path, "crashed.txt");

        using (var crash = StartWorker(
                   worker,
                   mutex,
                   ownerRecord,
                   temp.Path,
                   crashMarker,
                   "replacement-installed"))
        {
            WaitForMarker(crashMarker, crash);
            Assert.True(crash.WaitForExit(10_000), "receipt-deploy crash worker did not exit");
            Assert.NotEqual(0, crash.ExitCode);
        }
        Directory.Delete(source, recursive: true);

        var missing = Path.Combine(temp.Path, "missing-project");
        var config = Path.Combine(temp.Path, "settings.json");
        var settings = new Settings
        {
            VmbRoot = missing,
            ProjectRoot = missing,
            SteamRoot = missing,
            Vt2SdkRoot = missing,
            UgcToolPath = missing,
            WorkshopContentRoot = workshop,
            NodePath = missing,
        };
        File.WriteAllText(config, JsonSerializer.Serialize(settings));
        var configBefore = File.ReadAllBytes(config);

        Assert.Throws<InvalidOperationException>(() => CliDispatcher.Run(new[]
        {
            "deploy", Mod, "--no-remote", "--no-banner", "--config", config,
        }));

        Assert.Equal(configBefore, File.ReadAllBytes(config));
        Assert.Equal(prior, Census(target));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(workshop),
            path => Path.GetFileName(path).StartsWith(
                $".vmblauncher-receipt-deploy-{PublishedId}",
                StringComparison.Ordinal));
        Assert.False(Directory.Exists(source));
    }

    [Fact]
    public void MalformedCliDeploy_DoesNotRecoverAValidInterruptedTransaction()
    {
        using var temp = new TempDir();
        var source = temp.CreateSubdir("source");
        var workshop = temp.CreateSubdir("workshop");
        var target = temp.CreateSubdir(Path.Combine("workshop", PublishedId));
        File.WriteAllText(Path.Combine(source, Mod + ".mod"), "new descriptor");
        File.WriteAllText(Path.Combine(source, "0123456789abcdef.mod_bundle"), "new bundle bytes");
        File.WriteAllText(Path.Combine(target, Mod + ".mod"), "old descriptor");
        File.WriteAllText(Path.Combine(target, "fedcba9876543210.mod_bundle"), "old bundle bytes");
        var worker = FindTransactionWorker();
        var mutex = @"Local\VMBLauncher.Tests.MalformedCliReceipt." + Guid.NewGuid().ToString("N");
        var ownerRecord = Path.Combine(temp.Path, "dead-owner.json");
        var crashMarker = Path.Combine(temp.Path, "crashed.txt");
        using (var crash = StartWorker(
                   worker,
                   mutex,
                   ownerRecord,
                   temp.Path,
                   crashMarker,
                   "replacement-installed"))
        {
            WaitForMarker(crashMarker, crash);
            Assert.True(crash.WaitForExit(10_000), "receipt-deploy crash worker did not exit");
            Assert.NotEqual(0, crash.ExitCode);
        }
        var config = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(config, "sentinel malformed config");
        var configBefore = File.ReadAllBytes(config);
        var workshopBefore = SnapshotTree(workshop);

        var exit = CliDispatcher.Run(new[]
        {
            "deploy", Mod,
            "--deployment-receipt", "first.json",
            "--deployment-receipt", "second.json",
            "--no-remote", "--no-banner", "--config", config,
        });

        Assert.Equal(CliDispatcher.ExitBadUsage, exit);
        Assert.Equal(configBefore, File.ReadAllBytes(config));
        Assert.Equal(workshopBefore, SnapshotTree(workshop));
    }

    private static Process StartWorker(
        string worker,
        string mutex,
        string record,
        string root,
        string marker,
        string checkpoint)
    {
        var start = new ProcessStartInfo
        {
            FileName = worker,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     "receipt-deploy-owner-crash", mutex, record, root,
                     marker, checkpoint, "5000",
                 })
            start.ArgumentList.Add(argument);
        return Process.Start(start)
            ?? throw new InvalidOperationException("could not start transaction worker");
    }

    private static void WaitForMarker(string marker, Process process)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(marker))
        {
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"transaction worker exited {process.ExitCode}: {process.StandardError.ReadToEnd()}");
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"transaction worker marker timeout: {marker}");
            Thread.Sleep(20);
        }
    }

    private static string FindTransactionWorker()
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var bin = testOutput;
        while (!string.Equals(bin.Name, "bin", StringComparison.OrdinalIgnoreCase))
            bin = bin.Parent
                ?? throw new InvalidOperationException("test output has no bin directory");
        var testsRoot = bin.Parent?.FullName
            ?? throw new InvalidOperationException("test output has no tests project directory");
        var worker = Path.Combine(
            testsRoot,
            "TransactionLeaseWorker",
            "bin",
            Path.GetRelativePath(bin.FullName, testOutput.FullName),
            "VmbLauncher.TransactionLeaseWorker.exe");
        Assert.True(File.Exists(worker), $"transaction worker missing: {worker}");
        return worker;
    }

    private static string[] Census(string directory) =>
        Directory.EnumerateFiles(directory)
            .Select(path =>
            {
                var bytes = File.ReadAllBytes(path);
                return $"{Path.GetFileName(path)}:{bytes.LongLength}:" +
                    Convert.ToHexString(SHA256.HashData(bytes));
            })
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static string[] SnapshotTree(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => Directory.Exists(path)
                ? "D:" + Path.GetRelativePath(root, path)
                : "F:" + Path.GetRelativePath(root, path) + ":" +
                  Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
}
