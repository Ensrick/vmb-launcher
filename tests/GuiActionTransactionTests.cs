using VmbLauncher.Services;
using System.IO;

namespace VmbLauncher.Tests;

public class GuiActionTransactionTests
{
    public static IEnumerable<object[]> GuiActions()
    {
        yield return new object[] { "gui-build" };
        yield return new object[] { "gui-deploy" };
        yield return new object[] { "gui-new-mod" };
    }

    [Theory]
    [MemberData(nameof(GuiActions))]
    public void TwoInstanceActionReloadsSettingsOnlyAfterOwningMachineLease(string action)
    {
        using var tmp = new TempDir();
        var firstProject = tmp.CreateSubdir("first-project");
        var secondProject = tmp.CreateSubdir("second-project");
        var firstWorkshop = tmp.CreateSubdir("first-workshop");
        var secondWorkshop = tmp.CreateSubdir("second-workshop");
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        var mutex = @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N");
        var record = Path.Combine(tmp.Path, "owner.json");

        var initial = CompleteSettings(settingsPath, firstProject, firstWorkshop, "first-tool");
        using (MachineTransactionLease.Enter(
            "fixture-initial", mod: null, projectRoot: null,
            recordPath: record, mutexName: mutex))
            initial.Save();

        // This is the snapshot held by GUI instance B while instance A edits
        // and saves the same settings document.
        var staleInstance = Settings.Load(settingsPath);
        using (MachineTransactionLease.Enter(
            "fixture-instance-a", mod: null, projectRoot: null,
            recordPath: record, mutexName: mutex))
        {
            var instanceA = Settings.Load(settingsPath);
            instanceA.ProjectRoot = secondProject;
            instanceA.VmbRoot = secondProject;
            instanceA.WorkshopContentRoot = secondWorkshop;
            instanceA.UgcToolPath = "second-tool";
            instanceA.Save();
        }

        using var actionScope = GuiActionTransaction.Enter(
            staleInstance, action, "fixture-mod",
            timeout: TimeSpan.FromSeconds(1), recordPath: record, mutexName: mutex);
        Assert.Equal(secondProject, actionScope.Settings.ProjectRoot);
        Assert.Equal(secondProject, actionScope.Settings.VmbRoot);
        Assert.Equal(secondWorkshop, actionScope.Settings.WorkshopContentRoot);
        Assert.Equal("second-tool", actionScope.Settings.UgcToolPath);
        Assert.NotEqual(firstProject, actionScope.Settings.ProjectRoot);
        Assert.NotEqual(firstWorkshop, actionScope.Settings.WorkshopContentRoot);
    }

    private static Settings CompleteSettings(
        string path, string project, string workshop, string tool)
    {
        var settings = Settings.Load(path);
        settings.ProjectRoot = project;
        settings.VmbRoot = project;
        settings.SteamRoot = "configured-steam";
        settings.Vt2SdkRoot = "configured-sdk";
        settings.UgcToolPath = tool;
        settings.WorkshopContentRoot = workshop;
        settings.NodePath = "configured-node";
        return settings;
    }

    [Fact]
    public void InvalidPreferredProjectBindsLeaseToResolvedVmbFallbackRoot()
    {
        using var tmp = new TempDir();
        var fallback = tmp.CreateSubdir("vmb-fallback");
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        var settings = CompleteSettings(
            settingsPath, Path.Combine(tmp.Path, "missing-preferred"),
            tmp.CreateSubdir("workshop"), "configured-tool");
        settings.VmbRoot = fallback;
        var record = Path.Combine(tmp.Path, "owner.json");
        var mutex = @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N");
        using (MachineTransactionLease.Enter(
            "fixture", mod: null, projectRoot: null, recordPath: record, mutexName: mutex))
            settings.Save();

        using var action = GuiActionTransaction.Enter(
            Settings.Load(settingsPath), "gui-build", "fixture-mod",
            timeout: TimeSpan.FromSeconds(1), recordPath: record, mutexName: mutex);
        Assert.Equal(Path.GetFullPath(fallback), action.Project.Root);
        Assert.Equal(Path.GetFullPath(fallback), MachineTransactionLease.CurrentIdentity!.ProjectRoot);
        Assert.NotEqual(action.Settings.ProjectRoot, MachineTransactionLease.CurrentIdentity!.ProjectRoot);
    }

    [Fact]
    public async Task NestedRunnerUsesSameResolvedFallbackRootAsGuiOrCliOwner()
    {
        using var tmp = new TempDir();
        var fallback = tmp.CreateSubdir("vmb-fallback");
        var settings = new Settings
        {
            ProjectRoot = Path.Combine(tmp.Path, "missing-preferred"),
            VmbRoot = fallback,
        };
        var mod = new ModInfo
        {
            Name = "fixture-mod",
            ModDir = Path.Combine(fallback, "mods", "fixture-mod"),
            ItemCfgPath = Path.Combine(fallback, "mods", "fixture-mod", "itemV2.cfg"),
        };
        using var owner = MachineTransactionLease.Enter(
            "gui-build", mod.Name, fallback,
            recordPath: Path.Combine(tmp.Path, "owner.json"),
            mutexName: @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N"));

        var outcome = await new ModRunner(settings, _ => { }).BuildAsync(mod, clean: false);
        Assert.False(outcome.Ok);
        Assert.Contains("VMB not configured", outcome.Message);
    }
}
