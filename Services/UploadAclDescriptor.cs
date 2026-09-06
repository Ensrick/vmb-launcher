using System.IO;
using System.Security.AccessControl;

namespace VmbLauncher.Services;

/// <summary>Compares recorded ACL authority, never effective-access approximations.</summary>
internal static class UploadAclDescriptor
{
    internal static bool HasRecordedIdentity(byte[] bytes)
    {
        var descriptor = new RawSecurityDescriptor(bytes, 0);
        if ((descriptor.Owner == null) != (descriptor.Group == null))
            throw new InvalidDataException("ACL authority has only one of owner/group.");
        return descriptor.Owner != null;
    }

    internal static bool Matches(byte[] expected, byte[] actual)
    {
        // Older schema-2 Access-only records have no directory-owner proof.
        // Keep their former exact-byte lane; never synthesize missing authority.
        if (!HasRecordedIdentity(expected)) return expected.SequenceEqual(actual);

        var left = new RawSecurityDescriptor(expected, 0);
        var right = new RawSecurityDescriptor(actual, 0);
        if (!left.Owner!.Equals(right.Owner) || !left.Group!.Equals(right.Group) ||
            expected[0] != actual[0] || expected[1] != actual[1]) return false;

        // SetNamedSecurityInfo may mark the unchanged DACL as converted to the
        // current inheritance model. Only this monotonic bit transition is
        // admissible; protection, request bits and every ACE remain authority.
        // https://learn.microsoft.com/en-us/windows/win32/secauthz/automatic-propagation-of-inheritable-aces
        var expectedFlags = left.ControlFlags;
        var actualFlags = right.ControlFlags;
        if (actualFlags != expectedFlags &&
            actualFlags != (expectedFlags | ControlFlags.DiscretionaryAclAutoInherited)) return false;
        return SameAcl(left.DiscretionaryAcl, right.DiscretionaryAcl) &&
            SameAcl(left.SystemAcl, right.SystemAcl);
    }

    internal static bool SameRecordedIdentity(byte[] original, byte[] frozen)
    {
        if (HasRecordedIdentity(original) != HasRecordedIdentity(frozen)) return false;
        var left = new RawSecurityDescriptor(original, 0);
        var right = new RawSecurityDescriptor(frozen, 0);
        return Equals(left.Owner, right.Owner) && Equals(left.Group, right.Group);
    }

    private static bool SameAcl(RawAcl? expected, RawAcl? actual)
    {
        if (expected == null || actual == null) return expected == null && actual == null;
        var left = new byte[expected.BinaryLength];
        var right = new byte[actual.BinaryLength];
        expected.GetBinaryForm(left, 0);
        actual.GetBinaryForm(right, 0);
        return left.SequenceEqual(right);
    }
}
