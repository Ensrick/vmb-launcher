using System.IO;
using System.Security.AccessControl;

namespace VmbLauncher.Tests;

/// <summary>Independent test oracle for one documented native descriptor-marker change.</summary>
internal static class NativeAclReadbackFixture
{
    internal static void AssertReadback(byte[] expected, byte[] actual, string context)
    {
        var before = new RawSecurityDescriptor(expected, 0);
        var after = new RawSecurityDescriptor(actual, 0);
        var normalized = actual.ToArray();
        // SECURITY_DESCRIPTOR_CONTROL is little-endian at bytes 2..3. Compare
        // the entire descriptor after removing ONLY an observed 0 -> 1 AI bit;
        // this does not reuse the production comparator or mask ACE/owner drift.
        if ((before.ControlFlags & ControlFlags.DiscretionaryAclAutoInherited) == 0 &&
            (after.ControlFlags & ControlFlags.DiscretionaryAclAutoInherited) != 0)
            normalized[3] &= 0xfb;
        Assert.True(expected.SequenceEqual(normalized),
            $"{context}: native descriptor changed beyond monotonic AI; " +
            $"expected control=0x{(int)before.ControlFlags:X4}, " +
            $"actual control=0x{(int)after.ControlFlags:X4}; " +
            $"expected={before.GetSddlForm(AccessControlSections.All)}; " +
            $"actual={after.GetSddlForm(AccessControlSections.All)}");
    }

    internal static void EstablishAutoInheritedDirectory(TempDir owner, string path)
    {
        var attributes = TempDir.ValidateCleanupPath(owner.Path, path, File.GetAttributes);
        Assert.True((attributes & FileAttributes.Directory) != 0);
        Assert.Empty(Directory.EnumerateFileSystemEntries(path));
        var info = new DirectoryInfo(path);
        var before = FileSystemAclExtensions.GetAccessControl(info,
            AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access)
            .GetSecurityDescriptorBinaryForm();
        var descriptor = new RawSecurityDescriptor(before, 0);
        descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access);
        // SetAccessControl persists through the native security API. Do this before any
        // descendant fixture is created, then verify the actual NTFS readback.
        FileSystemAclExtensions.SetAccessControl(info, security);
        var after = FileSystemAclExtensions.GetAccessControl(info,
            AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access)
            .GetSecurityDescriptorBinaryForm();
        AssertReadback(before, after, "native fixture initialization");
        Assert.True((new RawSecurityDescriptor(after, 0).ControlFlags &
            ControlFlags.DiscretionaryAclAutoInherited) != 0,
            "Native fixture initialization did not establish SE_DACL_AUTO_INHERITED.");
    }
}
