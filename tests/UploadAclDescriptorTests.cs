using System.Security.AccessControl;
using System.Security.Principal;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public class UploadAclDescriptorTests
{
    private static RawSecurityDescriptor Full() => new(
        "O:BAG:SYD:(D;;WD;;;BU)(A;;FA;;;SY)(A;CI;FR;;;BU)");

    private static byte[] Bytes(RawSecurityDescriptor descriptor)
    {
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    [Fact]
    public void HostedFullIdentityAllowsOnlyDocumentedMonotonicInheritanceMarker()
    {
        var before = Full();
        var expected = Bytes(before);
        before.SetFlags(before.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited);
        var after = Bytes(before);
        Assert.True(UploadAclDescriptor.Matches(expected, expected));
        Assert.True(UploadAclDescriptor.Matches(expected, after));
        Assert.False(UploadAclDescriptor.Matches(after, expected));
        Assert.NotEqual(expected, after);
    }

    [Theory]
    [InlineData(ControlFlags.DiscretionaryAclProtected)]
    [InlineData(ControlFlags.DiscretionaryAclAutoInheritRequired)]
    [InlineData(ControlFlags.DiscretionaryAclDefaulted)]
    [InlineData(ControlFlags.OwnerDefaulted)]
    [InlineData(ControlFlags.GroupDefaulted)]
    [InlineData(ControlFlags.SystemAclAutoInherited)]
    [InlineData(ControlFlags.SystemAclProtected)]
    [InlineData(ControlFlags.RMControlValid)]
    public void OtherControlChangesAreNotNormalization(ControlFlags changed)
    {
        var descriptor = Full();
        var expected = Bytes(descriptor);
        descriptor.SetFlags(descriptor.ControlFlags | changed | ControlFlags.DiscretionaryAclAutoInherited);
        Assert.False(UploadAclDescriptor.Matches(expected, Bytes(descriptor)));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("group")]
    [InlineData("rights")]
    [InlineData("ace-sid")]
    [InlineData("ace-flags")]
    [InlineData("order")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("sacl")]
    [InlineData("resource-byte")]
    public void ChangedAuthorityIsRejectedEvenWithAllowedMarker(string change)
    {
        var descriptor = Full();
        var expected = Bytes(descriptor);
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
            case "empty": descriptor.DiscretionaryAcl = new RawAcl(2, 0); break;
            case "null": descriptor.DiscretionaryAcl = null; break;
            case "sacl":
                descriptor.SystemAcl = new RawAcl(2, 0);
                descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.SystemAclPresent);
                break;
            case "resource-byte":
                var changedBytes = Bytes(descriptor);
                changedBytes[1] = 42;
                Assert.False(UploadAclDescriptor.Matches(expected, changedBytes));
                return;
        }
        Assert.False(UploadAclDescriptor.Matches(expected, Bytes(descriptor)));
    }

    [Fact]
    public void OwnerlessSchema2KeepsExactByteLaneAndCannotBorrowCurrentIdentity()
    {
        var full = Full();
        var fullBytes = Bytes(full);
        full.Owner = null;
        full.Group = null;
        var old = Bytes(full);
        Assert.False(UploadAclDescriptor.HasRecordedIdentity(old));
        Assert.True(UploadAclDescriptor.Matches(old, old));
        Assert.False(UploadAclDescriptor.Matches(old, fullBytes));
        Assert.False(UploadAclDescriptor.Matches(fullBytes, old));
        full.SetFlags(full.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited);
        Assert.False(UploadAclDescriptor.Matches(old, Bytes(full)));
    }

    [Fact]
    public void PartialOwnerAuthorityAndMalformedRecordsFailClosed()
    {
        var descriptor = Full();
        descriptor.Group = null;
        Assert.Throws<System.IO.InvalidDataException>(() => UploadAclDescriptor.HasRecordedIdentity(Bytes(descriptor)));
        Assert.ThrowsAny<ArgumentException>(() => UploadAclDescriptor.Matches(new byte[3], Bytes(Full())));
    }
}
