using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml.Linq;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public sealed class FixtureDefaultOwnerTests
{
    [Fact]
    public void ModuleSetupIsEarlyAndIdempotentWithoutIdentityChanges()
    {
        Assert.Equal(Environment.ProcessId, FixtureDefaultOwner.InitializedForProcessId);
        var changes = FixtureDefaultOwner.SuccessfulOwnerChanges;
        var first = FixtureDefaultOwner.EnsureCurrentProcessDefaultOwner();
        var second = FixtureDefaultOwner.EnsureCurrentProcessDefaultOwner();
        FixtureDefaultOwner.RequireIdentityUnchangedAndUserOwner(first, second);
        Assert.Equal(WindowsIdentity.GetCurrent().User!.Value, second.Owner);
        Assert.Equal(changes, FixtureDefaultOwner.SuccessfulOwnerChanges);
        Assert.InRange(changes, 0, 1);
    }

    [Fact]
    public void OrdinaryNewDirectoriesUseTheCurrentUserWithoutAclRepair()
    {
        using var tmp = new TempDir();
        var child = Path.Combine(tmp.Path, "new", "nested");
        Directory.CreateDirectory(child);
        var user = WindowsIdentity.GetCurrent().User;
        foreach (var path in new[] { tmp.Path, Path.GetDirectoryName(child)!, child })
            Assert.Equal(user, FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(path), AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("user")]
    [InlineData("groups")]
    [InlineData("privileges")]
    [InlineData("empty")]
    public void InvalidReadbackNeverBecomesSuccessfulSetup(string defect)
    {
        var before = new FixtureDefaultOwner.Snapshot("user", "old-owner", ["group:1"], ["privilege:0"]);
        var after = before with { Owner = "user" };
        switch (defect)
        {
            case "owner": after = after with { Owner = "old-owner" }; break;
            case "user": after = after with { User = "other", Owner = "other" }; break;
            case "groups": after = after with { Groups = ["group:2"] }; break;
            case "privileges": after = after with { Privileges = ["privilege:1"] }; break;
            case "empty": before = before with { User = "" }; after = after with { User = "", Owner = "" }; break;
        }
        Assert.Throws<InvalidOperationException>(() =>
            FixtureDefaultOwner.RequireIdentityUnchangedAndUserOwner(before, after));
    }

    [Fact]
    public void HelperIsCompiledOnlyByTheTwoTestExecutables()
    {
        var tests = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var root = Path.GetDirectoryName(tests)!;
        var shipping = XDocument.Load(Path.Combine(root, "VmbLauncher.csproj"));
        Assert.Contains(shipping.Descendants("Compile"), element => (string?)element.Attribute("Remove") == @"tests\**");
        Assert.DoesNotContain(shipping.Descendants("Compile"), element =>
            ((string?)element.Attribute("Include"))?.Contains(nameof(FixtureDefaultOwner), StringComparison.Ordinal) == true);
        Assert.Null(typeof(Settings).Assembly.GetType(typeof(FixtureDefaultOwner).FullName!));
        var worker = XDocument.Load(Path.Combine(tests, "TransactionLeaseWorker", "VmbLauncher.TransactionLeaseWorker.csproj"));
        Assert.Single(worker.Descendants("Compile"), element =>
            (string?)element.Attribute("Include") == @"..\FixtureDefaultOwner.cs");
        foreach (var source in Directory.EnumerateFiles(Path.Combine(root, "Services"), "*.cs", SearchOption.AllDirectories))
            Assert.DoesNotContain(nameof(FixtureDefaultOwner), File.ReadAllText(source), StringComparison.Ordinal);
    }
}
