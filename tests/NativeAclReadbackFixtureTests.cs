using System.Security.AccessControl;
using System.Security.Principal;
using Xunit.Sdk;

namespace VmbLauncher.Tests;

public sealed class NativeAclReadbackFixtureTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void OracleIsDirectional(bool beforeMarker, bool afterMarker, bool accepted)
    {
        var before = Descriptor(beforeMarker);
        var after = Descriptor(afterMarker);
        var exception = Record.Exception(() =>
            NativeAclReadbackFixture.AssertReadback(Bytes(before), Bytes(after), "marker"));
        if (accepted) Assert.Null(exception);
        else Assert.IsAssignableFrom<XunitException>(exception);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("group")]
    [InlineData("ace-order")]
    [InlineData("ace-rights")]
    [InlineData("ace-flags")]
    [InlineData("ace-sid")]
    [InlineData("protected")]
    [InlineData("defaulted")]
    [InlineData("inherit-required")]
    [InlineData("owner-defaulted")]
    [InlineData("resource-byte")]
    public void OracleRejectsOtherChangesEvenAlongsideAllowedMarker(string change)
    {
        var before = Bytes(Descriptor(false));
        var after = Descriptor(true);
        switch (change)
        {
            case "owner": after.Owner = new SecurityIdentifier("S-1-5-18"); break;
            case "group": after.Group = new SecurityIdentifier("S-1-5-32-545"); break;
            case "ace-order":
                var first = after.DiscretionaryAcl![0];
                after.DiscretionaryAcl[0] = after.DiscretionaryAcl[1];
                after.DiscretionaryAcl[1] = first;
                break;
            case "ace-rights": ((CommonAce)after.DiscretionaryAcl![0]).AccessMask ^= 1; break;
            case "ace-flags": after.DiscretionaryAcl![0].AceFlags ^= AceFlags.Inherited; break;
            case "ace-sid": ((CommonAce)after.DiscretionaryAcl![0]).SecurityIdentifier = new SecurityIdentifier("S-1-1-0"); break;
            case "protected": after.SetFlags(after.ControlFlags | ControlFlags.DiscretionaryAclProtected); break;
            case "defaulted": after.SetFlags(after.ControlFlags | ControlFlags.DiscretionaryAclDefaulted); break;
            case "inherit-required": after.SetFlags(after.ControlFlags | ControlFlags.DiscretionaryAclAutoInheritRequired); break;
            case "owner-defaulted": after.SetFlags(after.ControlFlags | ControlFlags.OwnerDefaulted); break;
        }
        var changed = Bytes(after);
        if (change == "resource-byte") changed[1] = 42;
        Assert.ThrowsAny<XunitException>(() =>
            NativeAclReadbackFixture.AssertReadback(before, changed, change));
    }

    private static RawSecurityDescriptor Descriptor(bool marker)
    {
        var descriptor = new RawSecurityDescriptor("O:BAG:SYD:(D;;WD;;;BU)(A;;FA;;;SY)");
        if (marker) descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited);
        return descriptor;
    }

    private static byte[] Bytes(RawSecurityDescriptor descriptor)
    {
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }
}
