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
        AssertNativeInheritanceReadback(untouched);
        targetSeal.Apply();
        parentSeal.RequireApplied();
        targetSeal.RequireApplied();
        AssertNativeInheritanceReadback(untouched);

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
        AssertNativeInheritanceReadback(untouched);

        targetSeal.Restore();
        parentSeal.Restore();
        targetSeal.RequireOriginal();
        parentSeal.RequireOriginal();
        NativeAclReadbackFixture.AssertReadback(targetAcl, GetAcl(target), "restored target");
        NativeAclReadbackFixture.AssertReadback(parentAcl, GetAcl(parent), "restored parent");
        NativeAclReadbackFixture.AssertReadback(leafAcl, GetAcl(expected), "restored descendant leaf");
        AssertNativeInheritanceReadback(untouched);

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
        AssertNativeInheritanceReadback(untouched);
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
        AssertNativeInheritanceReadback(untouched);
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
        NativeAclReadbackFixture.AssertReadback(parentDescriptor, GetAcl(parent), "failed-apply restored root");
        AssertNativeInheritanceReadback(untouched);
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

    [Fact]
    public void JournalPlanWithoutAutoInheritedMarker_ResumesAppliesAndRestoresObservedMarker()
    {
        using var temp = new TempDir();
        var target = temp.CreateSubdir("target");
        NativeAclReadbackFixture.EstablishAutoInheritedDirectory(temp, target);
        var child = temp.Write(@"target\child.txt", "untouched");
        var childBefore = GetAcl(child);
        var identity = ImmutableBundleSourceLease.InspectDirectory(target);
        using var prepared = LocalExactSetMembershipSeal.Prepare(target, identity);
        AssertAutoInherited(GetAcl(target));
        var recorded = WithoutAutoInherited(prepared.Plan);
        LocalExactSetMembershipSeal.ValidatePlan(recorded);
        Assert.False(prepared.Plan.SemanticallyMatches(recorded));
        prepared.AbandonWithoutRestore();

        using (var resumed = LocalExactSetMembershipSeal.Resume(recorded, identity))
        {
            resumed.RequireOriginal();
            resumed.Apply();
            resumed.RequireApplied();
            AssertAutoInherited(GetAcl(target));
            Assert.Equal(childBefore, GetAcl(child));
            resumed.AbandonWithoutRestore();
        }

        using (var recovered = LocalExactSetMembershipSeal.Resume(recorded, identity))
        {
            recovered.RequireApplied();
            recovered.Restore();
            recovered.RequireOriginal();
            // Repeated restore recognizes observed-original without another mutation.
            var restored = GetAcl(target);
            AssertAutoInherited(restored);
            recovered.Restore();
            Assert.Equal(restored, GetAcl(target));
        }
        Assert.Equal(childBefore, GetAcl(child));
        File.WriteAllText(Path.Combine(target, "after-recovery.txt"), "allowed");
        // Neither successful readback nor recovery rewrites the recorded authority.
        Assert.False(HasAutoInherited(Convert.FromBase64String(recorded.OriginalDescriptor)));
        Assert.False(HasAutoInherited(Convert.FromBase64String(recorded.SealedDescriptor)));
    }

    [Fact]
    public void PlanComparisonAndRecomputation_RejectAnAutoInheritedOnlyPlanChange()
    {
        using var temp = new TempDir();
        var target = temp.CreateSubdir("target");
        using var prepared = LocalExactSetMembershipSeal.Prepare(
            target, ImmutableBundleSourceLease.InspectDirectory(target));
        var recorded = WithoutAutoInherited(prepared.Plan);
        LocalExactSetMembershipSeal.ValidatePlan(recorded);
        var originalChanged = recorded with
        {
            OriginalDescriptor = ChangeFlags(recorded.OriginalDescriptor,
                flags => flags | ControlFlags.DiscretionaryAclAutoInherited),
        };
        var sealedChanged = recorded with
        {
            SealedDescriptor = ChangeFlags(recorded.SealedDescriptor,
                flags => flags | ControlFlags.DiscretionaryAclAutoInherited),
        };
        foreach (var changed in new[] { originalChanged, sealedChanged })
        {
            Assert.False(recorded.SemanticallyMatches(changed));
            Assert.False(changed.SemanticallyMatches(recorded));
            Assert.Throws<InvalidDataException>(() => LocalExactSetMembershipSeal.ValidatePlan(changed));
        }
        prepared.RequireOriginal();
    }

    [Fact]
    public void FailedApplyWithJournalMarkerAbsent_RestoresWithoutReplacingOriginalFailure()
    {
        using var temp = new TempDir();
        var target = temp.CreateSubdir("target");
        NativeAclReadbackFixture.EstablishAutoInheritedDirectory(temp, target);
        var child = temp.Write(@"target\child.txt", "untouched");
        var original = GetAcl(target);
        var childBefore = GetAcl(child);
        AssertAutoInherited(original);
        var identity = ImmutableBundleSourceLease.InspectDirectory(target);
        using var prepared = LocalExactSetMembershipSeal.Prepare(target, identity);
        var recorded = WithoutAutoInherited(prepared.Plan);
        prepared.AbandonWithoutRestore();
        using var resumed = LocalExactSetMembershipSeal.Resume(recorded, identity);
        var failure = new IOException("planted failure after real SetSecurityInfo");
        LocalExactSetMembershipSeal.AppliedForTest = _ => throw failure;
        try
        {
            var error = Assert.Throws<InvalidDataException>(() => resumed.Apply());
            Assert.Same(failure, error.InnerException);
        }
        finally
        {
            LocalExactSetMembershipSeal.AppliedForTest = null;
        }
        resumed.RequireOriginal();
        Assert.Equal(original, GetAcl(target));
        Assert.Equal(childBefore, GetAcl(child));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void ObservedDescriptorComparison_AllowsOnlyMonotonicMarker(
        bool expectedMarker, bool actualMarker, bool matches)
    {
        var descriptor = new RawSecurityDescriptor("O:BAG:SYD:(D;;WD;;;BU)(A;;FA;;;SY)(A;CI;FR;;;BU)");
        var basis = Encode(descriptor);
        var expected = ChangeFlags(basis, flags => expectedMarker
            ? flags | ControlFlags.DiscretionaryAclAutoInherited
            : flags & ~ControlFlags.DiscretionaryAclAutoInherited);
        var actual = ChangeFlags(basis, flags => actualMarker
            ? flags | ControlFlags.DiscretionaryAclAutoInherited
            : flags & ~ControlFlags.DiscretionaryAclAutoInherited);
        Assert.Equal(matches, LocalExactSetMembershipSeal.ObservedDescriptorMatchesForTest(actual, expected));
    }

    [Theory]
    [InlineData(ControlFlags.DiscretionaryAclProtected)]
    [InlineData(ControlFlags.DiscretionaryAclAutoInheritRequired)]
    [InlineData(ControlFlags.DiscretionaryAclDefaulted)]
    public void ObservedDescriptorComparison_RejectsEveryOtherValidatedControlChange(ControlFlags change)
    {
        var descriptor = new RawSecurityDescriptor("O:BAG:SYD:(A;;FA;;;SY)");
        var expected = Encode(descriptor);
        var actual = ChangeFlags(expected,
            flags => flags | change | ControlFlags.DiscretionaryAclAutoInherited);
        Assert.False(LocalExactSetMembershipSeal.ObservedDescriptorMatchesForTest(actual, expected));
        Assert.False(LocalExactSetMembershipSeal.ObservedDescriptorMatchesForTest(expected, actual));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("group")]
    [InlineData("rights")]
    [InlineData("ace-sid")]
    [InlineData("ace-flags")]
    [InlineData("order")]
    [InlineData("revision")]
    [InlineData("empty")]
    public void ObservedDescriptorComparison_RejectsIdentityAndOrderedDaclDrift(string change)
    {
        var descriptor = new RawSecurityDescriptor("O:BAG:SYD:(D;;WD;;;BU)(A;;FA;;;SY)(A;CI;FR;;;BU)");
        var expected = Encode(descriptor);
        descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited);
        switch (change)
        {
            case "owner": descriptor.Owner = new SecurityIdentifier("S-1-5-18"); break;
            case "group": descriptor.Group = new SecurityIdentifier("S-1-5-32-545"); break;
            case "rights": ((CommonAce)descriptor.DiscretionaryAcl![0]).AccessMask ^= 1; break;
            case "ace-sid": ((CommonAce)descriptor.DiscretionaryAcl![0]).SecurityIdentifier = new SecurityIdentifier("S-1-1-0"); break;
            case "ace-flags": descriptor.DiscretionaryAcl![0].AceFlags ^= AceFlags.Inherited; break;
            case "order":
                var first = descriptor.DiscretionaryAcl![0];
                descriptor.DiscretionaryAcl[0] = descriptor.DiscretionaryAcl[1];
                descriptor.DiscretionaryAcl[1] = first;
                break;
            case "revision":
                var oldAcl = descriptor.DiscretionaryAcl!;
                var newAcl = new RawAcl(4, oldAcl.Count);
                for (var index = 0; index < oldAcl.Count; index++) newAcl.InsertAce(index, oldAcl[index]);
                descriptor.DiscretionaryAcl = newAcl;
                break;
            case "empty": descriptor.DiscretionaryAcl = new RawAcl(2, 0); break;
        }
        Assert.False(LocalExactSetMembershipSeal.ObservedDescriptorMatchesForTest(Encode(descriptor), expected));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("group")]
    [InlineData("null-dacl")]
    [InlineData("absent-dacl")]
    public void ObservedDescriptorComparison_RejectsIncompleteAuthority(string change)
    {
        var descriptor = new RawSecurityDescriptor("O:BAG:SYD:(A;;FA;;;SY)");
        var expected = Encode(descriptor);
        descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited);
        switch (change)
        {
            case "owner": descriptor.Owner = null; break;
            case "group": descriptor.Group = null; break;
            case "null-dacl": descriptor.DiscretionaryAcl = null; break;
            case "absent-dacl": descriptor.SetFlags(descriptor.ControlFlags & ~ControlFlags.DiscretionaryAclPresent); break;
        }
        var incomplete = Encode(descriptor);
        Assert.Throws<InvalidDataException>(() =>
            LocalExactSetMembershipSeal.ObservedDescriptorMatchesForTest(incomplete, expected));
        Assert.Throws<InvalidDataException>(() =>
            LocalExactSetMembershipSeal.ObservedDescriptorMatchesForTest(expected, incomplete));
    }

    private static LocalExactSetMembershipSeal.SecurityPlan WithoutAutoInherited(
        LocalExactSetMembershipSeal.SecurityPlan plan) => plan with
    {
        OriginalDescriptor = ChangeFlags(plan.OriginalDescriptor,
            flags => flags & ~ControlFlags.DiscretionaryAclAutoInherited),
        SealedDescriptor = ChangeFlags(plan.SealedDescriptor,
            flags => flags & ~ControlFlags.DiscretionaryAclAutoInherited),
    };

    private static string ChangeFlags(string descriptor, Func<ControlFlags, ControlFlags> change)
    {
        var raw = new RawSecurityDescriptor(Convert.FromBase64String(descriptor), 0);
        raw.SetFlags(change(raw.ControlFlags));
        return Encode(raw);
    }

    private static string Encode(RawSecurityDescriptor descriptor)
    {
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return Convert.ToBase64String(bytes);
    }

    private static bool HasAutoInherited(byte[] descriptor) =>
        (new RawSecurityDescriptor(descriptor, 0).ControlFlags &
            ControlFlags.DiscretionaryAclAutoInherited) != 0;

    private static void AssertAutoInherited(byte[] descriptor) => Assert.True(HasAutoInherited(descriptor),
        "This fixture must exercise actual NTFS readback with SE_DACL_AUTO_INHERITED set.");

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

    private static void AssertNativeInheritanceReadback(IReadOnlyDictionary<string, byte[]> expected)
    {
        // Setting a parent's DACL can mark unprotected descendants as converted
        // to Windows' current inheritance model even when every ACE is unchanged.
        // Only these post-native-write assertions allow the marker; the complete
        // descriptor otherwise remains byte-exact, including inherited ACE flags.
        foreach (var pair in expected)
            NativeAclReadbackFixture.AssertReadback(pair.Value, GetAcl(pair.Key), pair.Key);
    }
}
