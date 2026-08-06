using VmbLauncher.Services;
using System.IO;

namespace VmbLauncher.Tests;

public class MutationBoundaryTests
{
    [Fact]
    public async Task ProcessRunnerWithoutLeaseFailsBeforeProcessCreation()
    {
        using var tmp = new TempDir();
        var marker = Path.Combine(tmp.Path, "must-not-exist.txt");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProcessRunner.RunAsync(
                "cmd.exe",
                new[] { "/d", "/c", $"echo forbidden>\"{marker}\"" },
                tmp.Path,
                onLine: null));
        Assert.Contains("authenticated machine transaction", error.Message);
        Assert.False(File.Exists(marker));
    }
}
