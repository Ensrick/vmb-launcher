using System.Diagnostics;
using System.IO;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public class StandalonePublicationSnapshotTests : MutationTestBase
{
    [Fact]
    public void WarlockUsesRootCommitBlobsAndIgnoresWorktreeReplacement()
    {
        using var tmp = new TempDir();
        tmp.Write("itemV2.cfg", "published_id = 3794172730L;\npreview = \"preview.png\";\n");
        tmp.Write("preview.png", "original-preview");
        tmp.Write("scripts/mods/doomrocket/doomrocket.lua", "local MOD_VERSION = \"0.1.68-dev\"");
        tmp.Write("bundleV2/doomrocket.mod", "original-descriptor");
        tmp.Write("bundleV2/ac226cc769a897ae.mod_bundle", "original-bundle");
        Git(tmp.Path, "init");
        Git(tmp.Path, "config", "user.name", "Fixture");
        Git(tmp.Path, "config", "user.email", "fixture@example.invalid");
        Git(tmp.Path, "add", ".");
        Git(tmp.Path, "commit", "-m", "fixture");
        var commit = Git(tmp.Path, "rev-parse", "HEAD");
        var cfgBlob = Git(tmp.Path, "rev-parse", commit + ":itemV2.cfg");
        tmp.Write("itemV2.cfg", "published_id = 3771657344L;\n");
        tmp.Write("bundleV2/ac226cc769a897ae.mod_bundle", "replacement");
        var snapshot = PublicationReceiptGate.ReadCommitSnapshot(tmp.Path, commit, "doomrocket");
        Assert.Equal("3794172730", snapshot.PublishedId);
        Assert.Equal("0.1.68-dev", snapshot.Version);
        Assert.Equal(cfgBlob, snapshot.ItemCfgGitBlob);
        Assert.Equal(2, snapshot.BundleFiles.Count);
        Assert.Equal(15, snapshot.BundleFiles.Single(x => x.Path.EndsWith(".mod_bundle")).Length);
        Assert.True(snapshot.PreviewFile.Present);
        Assert.ThrowsAny<Exception>(() => PublicationReceiptGate.ReadCommitSnapshot(tmp.Path, commit, "other_mod"));
    }

    private static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory=directory,
            UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        foreach (var value in arguments) start.ArgumentList.Add(value);
        using var process = Process.Start(start)!;
        var result=process.StandardOutput.ReadToEnd();
        var error=process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode==0,error);
        return result.Trim();
    }
}
