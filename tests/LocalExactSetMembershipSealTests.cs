using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using VmbLauncher.Services;
using Xunit.Abstractions;

namespace VmbLauncher.Tests;

[Collection("receipt-deploy-serial")]
public sealed class LocalExactSetMembershipSealTests : MutationTestBase
{
    private readonly ITestOutputHelper _output;

    public LocalExactSetMembershipSealTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ParentAndTargetSeals_BlockNamespaceMutation_AndRestoreExactAcls()
    {
        using var temp = new TempDir();
        var parent = temp.CreateSubdir("workshop");
        var target = temp.CreateSubdir(@"workshop\123456789");
        var expected = temp.Write(@"workshop\123456789\modx.mod", "descriptor");
        var descendantDirectory = temp.CreateSubdir(@"workshop\123456789\nested");
        var descendant = temp.Write(@"workshop\123456789\nested\child.txt", "child");
        var siblingDirectory = temp.CreateSubdir(@"workshop\unrelated");
        var sibling = temp.Write(@"workshop\unrelated\unrelated.txt", "unrelated");
        var incoming = temp.Write("incoming.mod_bundle", "incoming");
        var parentAcl = GetAcl(parent);
        var targetAcl = GetAcl(target);
        var leafAcl = GetAcl(expected);
        var untouched = CaptureDescriptors(
            descendantDirectory,
            descendant,
            siblingDirectory,
            sibling);
        using var pinnedLeaf = new FileStream(
            expected,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        WriteMembershipDescriptorDiagnostics("parent-before-prepare", parent);
        WriteMembershipDescriptorDiagnostics("target-before-prepare", target);
        using var parentSeal = LocalExactSetMembershipSeal.Prepare(
            parent,
            ImmutableBundleSourceLease.InspectDirectory(parent));
        using var targetSeal = LocalExactSetMembershipSeal.Prepare(
            target,
            ImmutableBundleSourceLease.InspectDirectory(target));
        Assert.Equal(parentAcl, GetAcl(parent));
        Assert.Equal(targetAcl, GetAcl(target));

        parentSeal.Apply();
        AssertDescriptors(untouched);
        targetSeal.Apply();
        parentSeal.RequireApplied();
        targetSeal.RequireApplied();
        AssertDescriptors(untouched);

        Assert.ThrowsAny<UnauthorizedAccessException>(() =>
            File.WriteAllText(Path.Combine(target, "inserted.mod_bundle"), "foreign"));
        Assert.ThrowsAny<UnauthorizedAccessException>(() =>
            Directory.CreateDirectory(Path.Combine(target, "inserted-directory")));
        Assert.ThrowsAny<IOException>(() => File.Delete(expected));
        Assert.ThrowsAny<IOException>(() =>
            File.Move(expected, Path.Combine(temp.Path, "moved.mod")));
        Assert.ThrowsAny<UnauthorizedAccessException>(() =>
            File.Move(incoming, Path.Combine(target, "incoming.mod_bundle")));
        Assert.ThrowsAny<IOException>(() =>
            Directory.Move(target, Path.Combine(parent, "renamed-target")));
        Assert.Equal(leafAcl, GetAcl(expected));
        AssertDescriptors(untouched);

        targetSeal.Restore();
        parentSeal.Restore();
        targetSeal.RequireOriginal();
        parentSeal.RequireOriginal();
        Assert.Equal(targetAcl, GetAcl(target));
        Assert.Equal(parentAcl, GetAcl(parent));
        Assert.Equal(leafAcl, GetAcl(expected));

        var postRestore = Path.Combine(target, "post-restore.mod_bundle");
        File.WriteAllText(postRestore, "allowed");
        Assert.Equal("allowed", File.ReadAllText(postRestore));
    }

    private void WriteMembershipDescriptorDiagnostics(string label, string path)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var bytes = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path),
                AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access)
                .GetSecurityDescriptorBinaryForm();
            if (bytes.Length > 4096) throw new InvalidDataException("descriptor exceeds 4096-byte diagnostic bound");
            var descriptor = new RawSecurityDescriptor(bytes, 0);
            _output.WriteLine($"[membership-diagnostic] {label} current_sid={identity.User?.Value ?? "<null>"} path={path}");
            _output.WriteLine($"owner={descriptor.Owner?.Value ?? "<null>"} group={descriptor.Group?.Value ?? "<null>"} control=0x{(int)descriptor.ControlFlags:X4} ({descriptor.ControlFlags})");
            _output.WriteLine("sddl=" + descriptor.GetSddlForm(
                AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access));
        }
        catch (Exception ex)
        {
            // Reporting cannot replace the fixture's real assertion/failure.
            try { _output.WriteLine($"[membership-diagnostic] {label} unavailable: {ex.GetType().Name}: {ex.Message[..Math.Min(ex.Message.Length, 1024)]}"); }
            catch { }
        }
    }

    [Fact]
    public void Resume_AcceptsOnlyExactOriginalOrSealedState_AndRestoresAfterCrash()
    {
        using var temp = new TempDir();
        var parent = temp.CreateSubdir("workshop");
        var target = temp.CreateSubdir(@"workshop\123456789");
        temp.Write(@"workshop\123456789\modx.mod", "descriptor");
        var descendant = temp.Write(@"workshop\123456789\child.txt", "child");
        var sibling = temp.Write(@"workshop\sibling.txt", "sibling");
        var untouched = CaptureDescriptors(descendant, sibling);
        var parentIdentity = ImmutableBundleSourceLease.InspectDirectory(parent);
        var targetIdentity = ImmutableBundleSourceLease.InspectDirectory(target);

        var parentSeal = LocalExactSetMembershipSeal.Prepare(parent, parentIdentity);
        var targetSeal = LocalExactSetMembershipSeal.Prepare(target, targetIdentity);
        var parentPlan = parentSeal.Plan;
        var targetPlan = targetSeal.Plan;
        parentSeal.Apply();
        targetSeal.Apply();
        AssertDescriptors(untouched);
        targetSeal.AbandonWithoutRestore();
        parentSeal.AbandonWithoutRestore();

        using (var resumedParent = LocalExactSetMembershipSeal.Resume(parentPlan, parentIdentity))
        using (var resumedTarget = LocalExactSetMembershipSeal.Resume(targetPlan, targetIdentity))
        {
            resumedParent.RequireApplied();
            resumedTarget.RequireApplied();
            resumedTarget.Restore();
            resumedParent.Restore();
            resumedTarget.RequireOriginal();
            resumedParent.RequireOriginal();
        }

        File.WriteAllText(Path.Combine(target, "after-recovery.mod_bundle"), "allowed");
        AssertDescriptors(untouched);
    }

    [Fact]
    public void Apply_RefusesAclDriftWithoutOverwritingForeignState()
    {
        using var temp = new TempDir();
        var target = temp.CreateSubdir("target");
        var child = temp.Write(@"target\child.txt", "child");
        var childDescriptor = GetAcl(child);
        using var seal = LocalExactSetMembershipSeal.Prepare(
            target,
            ImmutableBundleSourceLease.InspectDirectory(target));

        var security = FileSystemAclExtensions.GetAccessControl(
            new DirectoryInfo(target),
            AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ReadAttributes,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny));
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(target), security);
        var foreign = GetAcl(target);

        Assert.Throws<InvalidDataException>(() => seal.Apply());
        Assert.Equal(foreign, GetAcl(target));
        Assert.Equal(childDescriptor, GetAcl(child));
    }

    [Fact]
    public void ApplyFailureAfterDaclMutation_RestoresRootWithoutTouchingDescendants()
    {
        using var temp = new TempDir();
        var parent = temp.CreateSubdir("parent");
        var target = temp.CreateSubdir(@"parent\target");
        var childDirectory = temp.CreateSubdir(@"parent\target\nested");
        var child = temp.Write(@"parent\target\nested\child.txt", "child");
        var sibling = temp.Write(@"parent\sibling.txt", "sibling");
        var parentDescriptor = GetAcl(parent);
        var untouched = CaptureDescriptors(target, childDirectory, child, sibling);
        using var seal = LocalExactSetMembershipSeal.Prepare(
            parent,
            ImmutableBundleSourceLease.InspectDirectory(parent));
        LocalExactSetMembershipSeal.AppliedForTest = _ =>
            throw new IOException("planted post-SetSecurityInfo failure");
        try
        {
            Assert.Throws<InvalidDataException>(() => seal.Apply());
        }
        finally
        {
            LocalExactSetMembershipSeal.AppliedForTest = null;
        }

        seal.RequireOriginal();
        Assert.Equal(parentDescriptor, GetAcl(parent));
        AssertDescriptors(untouched);
    }

    [Fact]
    public void ProtectedSeal_PreventsParentInheritanceDrift_AndPreservesChildAcls()
    {
        using var temp = new TempDir();
        var parent = temp.CreateSubdir("parent");
        var target = temp.CreateSubdir(@"parent\target");
        var child = temp.Write(@"parent\target\child.mod", "bytes");
        var childAcl = GetAcl(child);
        var parentSecurity = FileSystemAclExtensions.GetAccessControl(
            new DirectoryInfo(parent),
            AccessControlSections.Access);
        using var seal = LocalExactSetMembershipSeal.Prepare(
            target,
            ImmutableBundleSourceLease.InspectDirectory(target));
        seal.Apply();

        var inheritedRule = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ReadAttributes,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Deny);
        parentSecurity.AddAccessRule(inheritedRule);
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(parent), parentSecurity);
        seal.RequireApplied();
        Assert.Equal(childAcl, GetAcl(child));

        parentSecurity.RemoveAccessRuleSpecific(inheritedRule);
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(parent), parentSecurity);
        seal.Restore();
        seal.RequireOriginal();
        Assert.Equal(childAcl, GetAcl(child));
    }

    [Fact]
    public void AppliedAclDrift_IsDetectedAndNeverOverwrittenByRestore()
    {
        using var temp = new TempDir();
        var target = temp.CreateSubdir("target");
        var original = FileSystemAclExtensions.GetAccessControl(
            new DirectoryInfo(target),
            AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
        using var seal = LocalExactSetMembershipSeal.Prepare(
            target,
            ImmutableBundleSourceLease.InspectDirectory(target));
        seal.Apply();

        var drifted = FileSystemAclExtensions.GetAccessControl(
            new DirectoryInfo(target),
            AccessControlSections.Access);
        drifted.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ReadPermissions,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny));
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(target), drifted);
        var foreign = GetAcl(target);

        Assert.Throws<InvalidDataException>(() => seal.RequireApplied());
        Assert.Throws<InvalidDataException>(() => seal.Restore());
        Assert.Equal(foreign, GetAcl(target));

        seal.AbandonWithoutRestore();
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(target), original);
    }

    [Fact]
    public void UnsupportedFilesystem_RefusesBeforeAclMutation()
    {
        using var temp = new TempDir();
        var target = temp.CreateSubdir("target");
        var before = GetAcl(target);
        LocalExactSetDeployment.FileSystemNameForTest = _ => "ReFS";
        try
        {
            var error = Assert.Throws<InvalidDataException>(() =>
                LocalExactSetMembershipSeal.Prepare(
                    target,
                    ImmutableBundleSourceLease.InspectDirectory(target)));
            Assert.Contains("requires NTFS", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            LocalExactSetDeployment.FileSystemNameForTest = null;
        }
        Assert.Equal(before, GetAcl(target));
    }

    [Fact]
    public void Resume_RefusesAPlanThatIsNotTheDeterministicOwnerSeal()
    {
        using var temp = new TempDir();
        var target = temp.CreateSubdir("target");
        var identity = ImmutableBundleSourceLease.InspectDirectory(target);
        using var seal = LocalExactSetMembershipSeal.Prepare(target, identity);
        var weakened = seal.Plan with
        {
            DeniedRights = checked((int)FileSystemRights.CreateFiles),
        };

        Assert.Throws<InvalidDataException>(() =>
            LocalExactSetMembershipSeal.Resume(weakened, identity));
        seal.RequireOriginal();
    }

    [Fact]
    public void Resume_RefusesAPlanOwnedByAnotherWindowsIdentity()
    {
        using var temp = new TempDir();
        var target = temp.CreateSubdir("target");
        var identity = ImmutableBundleSourceLease.InspectDirectory(target);
        using var seal = LocalExactSetMembershipSeal.Prepare(target, identity);
        var original = new RawSecurityDescriptor(
            Convert.FromBase64String(seal.Plan.OriginalDescriptor),
            0);
        var foreignOwner = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var foreign = new RawSecurityDescriptor(
            original.ControlFlags,
            foreignOwner,
            original.Group,
            original.SystemAcl,
            original.DiscretionaryAcl);
        var bytes = new byte[foreign.BinaryLength];
        foreign.GetBinaryForm(bytes, 0);
        var foreignPlan = seal.Plan with
        {
            OriginalDescriptor = Convert.ToBase64String(bytes),
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            LocalExactSetMembershipSeal.Resume(foreignPlan, identity));
        Assert.Contains("current Windows identity", error.Message, StringComparison.Ordinal);
        seal.RequireOriginal();
    }

    private static byte[] GetAcl(string path)
    {
        FileSystemSecurity security = Directory.Exists(path)
            ? FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(path),
                AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access)
            : FileSystemAclExtensions.GetAccessControl(
                new FileInfo(path),
                AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
        return security.GetSecurityDescriptorBinaryForm();
    }

    private static Dictionary<string, byte[]> CaptureDescriptors(params string[] paths) =>
        paths.ToDictionary(path => path, GetAcl, StringComparer.OrdinalIgnoreCase);

    private static void AssertDescriptors(IReadOnlyDictionary<string, byte[]> expected)
    {
        foreach (var pair in expected)
            Assert.Equal(pair.Value, GetAcl(pair.Key));
    }
}
