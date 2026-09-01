using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace VmbLauncher.Services;

/// <summary>
/// An exact, handle-bound NTFS DACL seal for one directory namespace.
///
/// The planned descriptor is recorded before Apply mutates the DACL. Apply is
/// accepted only when NTFS returns that exact ACL/control state; any
/// canonicalization, inheritance, owner/group, identity, or path drift fails
/// closed. The still-open WRITE_DAC handle restores the exact original state.
/// This is authority against ordinary same-user namespace operations, not a
/// sandbox against an administrator or owner deliberately replacing the DACL;
/// persistent ACL drift is detected, while that privileged transient attack is
/// explicitly outside the transaction's cooperative local-process threat model.
/// </summary>
internal sealed class LocalExactSetMembershipSeal : IDisposable
{
#if VMBLAUNCHER_TEST_HOOKS
    internal static Action<string>? AppliedForTest;
#endif
    private const uint FileListDirectory = 0x0001;
    private const uint FileReadAttributes = 0x0080;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint MaximumAllowed = 0x02000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint GroupSecurityInformation = 0x00000002;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private const uint UnprotectedDaclSecurityInformation = 0x20000000;
    private const int SeFileObject = 1;
    private const int MaximumDescriptorBytes = 64 * 1024;

    internal const FileSystemRights NamespaceDeniedRights =
        FileSystemRights.CreateFiles |
        FileSystemRights.CreateDirectories |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.Delete |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership |
        FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes;

    private const ControlFlags DaclControlMask =
        ControlFlags.DiscretionaryAclPresent |
        ControlFlags.DiscretionaryAclDefaulted |
        ControlFlags.DiscretionaryAclAutoInheritRequired |
        ControlFlags.DiscretionaryAclAutoInherited |
        ControlFlags.DiscretionaryAclProtected;

    private SafeFileHandle? _handle;
    private bool _applied;

    internal string Path { get; }
    internal PhysicalDirectoryIdentity Identity { get; }
    internal SecurityPlan Plan { get; }

    private LocalExactSetMembershipSeal(
        string path,
        SafeFileHandle handle,
        PhysicalDirectoryIdentity identity,
        SecurityPlan plan)
    {
        Path = path;
        _handle = handle;
        Identity = identity;
        Plan = plan;
    }

    internal static LocalExactSetMembershipSeal Prepare(
        string path,
        PhysicalDirectoryIdentity expectedIdentity,
        FileSystemRights deniedRights = NamespaceDeniedRights)
    {
        var normalized = Normalize(path);
        LocalExactSetDeployment.RequireSupportedExactSetFileSystem(
            normalized,
            "membership seal");
        if (deniedRights == 0 || (deniedRights & NamespaceDeniedRights) != deniedRights)
            throw new InvalidDataException("Membership seal denied-rights mask is noncanonical.");

        SafeFileHandle? handle = null;
        try
        {
            // SetSecurityInfo's documented filesystem behavior suppresses
            // inheritable-ACE propagation only when the object handle was
            // opened with MAXIMUM_ALLOWED. Query the resulting granted mask
            // immediately; MAXIMUM_ALLOWED itself is never assumed to imply
            // the read/control rights this protocol requires.
            handle = CreateFileW(
                normalized,
                MaximumAllowed,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
                throw new IOException(
                    $"Cannot open membership-seal authority for '{normalized}' " +
                    $"(Win32 {Marshal.GetLastWin32Error()}).");
            RequireGrantedAccess(handle, normalized);

            var information = ImmutableBundleSourceLease.GetHandleInformation(handle, normalized);
            if ((information.FileAttributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Membership-seal directory may not be a reparse point.");
            var fileId = ImmutableBundleSourceLease.GetFileIdInformation(handle, normalized);
            var finalPath = Normalize(ImmutableBundleSourceLease.GetFinalPath(handle));
            var identity = new PhysicalDirectoryIdentity(
                fileId.VolumeSerialNumber,
                fileId.FileIdLow,
                fileId.FileIdHigh,
                finalPath);
            RequireIdentity(identity, expectedIdentity, normalized);

            var original = ReadSnapshot(handle, "membership-seal original DACL");
            var planned = BuildSealedSnapshot(original, deniedRights);
            var plan = new SecurityPlan(
                normalized,
                identity.VolumeSerialNumber,
                identity.FileIdLow,
                identity.FileIdHigh,
                original.DescriptorBase64,
                planned.DescriptorBase64,
                checked((int)deniedRights));
            var result = new LocalExactSetMembershipSeal(normalized, handle, identity, plan);
            handle = null;
            return result;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    internal static LocalExactSetMembershipSeal Resume(
        SecurityPlan plan,
        PhysicalDirectoryIdentity expectedIdentity)
    {
        ValidatePlan(plan);
        var normalized = Normalize(plan.Path);
        LocalExactSetDeployment.RequireSupportedExactSetFileSystem(
            normalized,
            "membership-seal recovery");
        SafeFileHandle? handle = null;
        try
        {
            // Recovery must use the same non-propagating handle contract.
            handle = CreateFileW(
                normalized,
                MaximumAllowed,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
                throw new IOException(
                    $"Cannot reopen membership-seal authority for '{normalized}' " +
                    $"(Win32 {Marshal.GetLastWin32Error()}).");
            RequireGrantedAccess(handle, normalized);
            var information = ImmutableBundleSourceLease.GetHandleInformation(handle, normalized);
            if ((information.FileAttributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Membership-seal recovery directory may not be a reparse point.");
            var fileId = ImmutableBundleSourceLease.GetFileIdInformation(handle, normalized);
            var actualIdentity = new PhysicalDirectoryIdentity(
                fileId.VolumeSerialNumber,
                fileId.FileIdLow,
                fileId.FileIdHigh,
                Normalize(ImmutableBundleSourceLease.GetFinalPath(handle)));
            RequireIdentity(actualIdentity, expectedIdentity, normalized);
            RequireIdentity(actualIdentity, plan.ToIdentity(), normalized);
            var current = ReadSnapshot(handle, "membership-seal recovery DACL");
            var original = Snapshot.FromBase64(plan.OriginalDescriptor);
            var sealedSnapshot = Snapshot.FromBase64(plan.SealedDescriptor);
            var applied = current.SemanticallyEquals(sealedSnapshot);
            if (!applied && !current.SemanticallyEquals(original))
                throw new InvalidDataException(
                    "Membership-seal recovery DACL differs from both exact recorded states.");
            var resumed = new LocalExactSetMembershipSeal(
                normalized,
                handle,
                actualIdentity,
                plan)
            {
                _applied = applied,
            };
            handle = null;
            return resumed;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    internal void Apply()
    {
        MachineTransactionLease.RequireCurrent("Receipt-deploy membership seal apply");
        if (_applied) throw new InvalidOperationException("Membership seal is already applied.");
        RequireCurrentIdentity();
        RequireSnapshot(Plan.OriginalDescriptor, "membership-seal precondition");
        SetSnapshot(Plan.SealedDescriptor, protectedDacl: true);
        try
        {
#if VMBLAUNCHER_TEST_HOOKS
            AppliedForTest?.Invoke(Path);
#endif
            RequireSnapshot(Plan.SealedDescriptor, "membership-seal applied postcondition");
            _applied = true;
        }
        catch (Exception applyFailure)
        {
            Exception? restoreFailure = null;
            try
            {
                SetSnapshot(
                    Plan.OriginalDescriptor,
                    protectedDacl: Snapshot.FromBase64(Plan.OriginalDescriptor).IsProtected);
                RequireSnapshot(Plan.OriginalDescriptor, "membership-seal failed-apply restoration");
            }
            catch (Exception ex) { restoreFailure = ex; }
            if (restoreFailure != null)
                throw new AggregateException(
                    "Membership seal did not apply exactly and its original DACL could not be restored.",
                    applyFailure,
                    restoreFailure);
            throw new InvalidDataException(
                "Membership seal did not apply as its exact pre-recorded descriptor.",
                applyFailure);
        }
    }

    internal void RequireApplied()
    {
        if (!_applied) throw new InvalidOperationException("Membership seal is not applied.");
        RequireCurrentIdentity();
        RequireSnapshot(Plan.SealedDescriptor, "membership-seal authority");
    }

    internal void RequireOriginal()
    {
        RequireCurrentIdentity();
        RequireSnapshot(Plan.OriginalDescriptor, "membership-seal original authority");
    }

    internal void Restore()
    {
        MachineTransactionLease.RequireCurrent("Receipt-deploy membership seal restore");
        RequireCurrentIdentity();
        var current = ReadSnapshot(Handle, "membership-seal restoration precondition");
        var original = Snapshot.FromBase64(Plan.OriginalDescriptor);
        if (current.SemanticallyEquals(original))
        {
            _applied = false;
            return;
        }
        var sealedSnapshot = Snapshot.FromBase64(Plan.SealedDescriptor);
        if (!current.SemanticallyEquals(sealedSnapshot))
            throw new InvalidDataException(
                "Membership-seal DACL drifted from both its exact original and launcher-owned states.");

        SetSnapshot(Plan.OriginalDescriptor, original.IsProtected);
        RequireSnapshot(Plan.OriginalDescriptor, "membership-seal restoration postcondition");
        _applied = false;
    }

    internal void AbandonWithoutRestore()
    {
        _applied = false;
        _handle?.Dispose();
        _handle = null;
    }

    public void Dispose()
    {
        Exception? failure = null;
        try
        {
            if (_applied) Restore();
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            _handle?.Dispose();
            _handle = null;
        }
        if (failure != null) throw failure;
    }

    private SafeFileHandle Handle => _handle is { IsClosed: false, IsInvalid: false }
        ? _handle
        : throw new ObjectDisposedException(nameof(LocalExactSetMembershipSeal));

    private void RequireCurrentIdentity()
    {
        var fileId = ImmutableBundleSourceLease.GetFileIdInformation(Handle, Path);
        var finalPath = Normalize(ImmutableBundleSourceLease.GetFinalPath(Handle));
        var actual = new PhysicalDirectoryIdentity(
            fileId.VolumeSerialNumber,
            fileId.FileIdLow,
            fileId.FileIdHigh,
            finalPath);
        RequireIdentity(actual, Identity, Path);
    }

    private void RequireSnapshot(string expectedBase64, string context)
    {
        var expected = Snapshot.FromBase64(expectedBase64);
        var actual = ReadSnapshot(Handle, context);
        if (!actual.SemanticallyEquals(expected))
            throw new InvalidDataException(
                $"{context} differs from its exact recorded DACL state: " +
                actual.DescribeDifference(expected));
    }

    private void SetSnapshot(string descriptorBase64, bool protectedDacl)
    {
        var descriptor = DecodeDescriptor(descriptorBase64);
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            if (!GetSecurityDescriptorDacl(
                    pinned.AddrOfPinnedObject(),
                    out var present,
                    out var dacl,
                    out _) ||
                !present || dacl == IntPtr.Zero)
                throw new InvalidDataException("Membership-seal descriptor has no usable DACL.");
            var securityInformation = DaclSecurityInformation |
                (protectedDacl
                    ? ProtectedDaclSecurityInformation
                    : UnprotectedDaclSecurityInformation);
            var error = SetSecurityInfo(
                Handle,
                SeFileObject,
                securityInformation,
                IntPtr.Zero,
                IntPtr.Zero,
                dacl,
                IntPtr.Zero);
            if (error != 0)
                throw new IOException(
                    $"Cannot apply membership-seal DACL to '{Path}' (Win32 {error}).");
        }
        finally
        {
            pinned.Free();
        }
    }

    internal static SecurityPlan ValidatePlan(SecurityPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.Path) ||
            plan.VolumeSerialNumber == 0 ||
            (plan.FileIdLow == 0 && plan.FileIdHigh == 0) ||
            plan.DeniedRights <= 0 ||
            (((FileSystemRights)plan.DeniedRights) & NamespaceDeniedRights) !=
                (FileSystemRights)plan.DeniedRights)
            throw new InvalidDataException("Membership-seal plan is noncanonical.");
        var original = Snapshot.FromBase64(plan.OriginalDescriptor);
        var sealedSnapshot = Snapshot.FromBase64(plan.SealedDescriptor);
        var recomputed = BuildSealedSnapshot(
            original,
            checked((FileSystemRights)plan.DeniedRights));
        if (!sealedSnapshot.IsProtected ||
            sealedSnapshot.SemanticallyEquals(original) ||
            !sealedSnapshot.SemanticallyEquals(recomputed))
            throw new InvalidDataException(
                "Membership-seal plan is not the exact deterministic DACL transition for the current owner.");
        return plan;
    }

    private static Snapshot BuildSealedSnapshot(Snapshot original, FileSystemRights deniedRights)
    {
        if (original.Dacl == null)
            throw new InvalidDataException("Membership seal refuses an absent or null original DACL.");
        if (!original.AreAccessRulesCanonical)
            throw new InvalidDataException("Membership seal refuses a noncanonical original DACL.");
        var sid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current Windows identity has no SID.");
        if (!Equals(original.Raw.Owner, sid))
            throw new InvalidDataException(
                "Membership seal requires the current Windows identity to own the directory for fresh-process recovery.");
        var sealAce = new CommonAce(
            AceFlags.None,
            AceQualifier.AccessDenied,
            checked((int)deniedRights),
            sid,
            isCallback: false,
            opaque: null);
        var acl = new RawAcl(original.Dacl.Revision, original.Dacl.Count + 1);
        var insertion = 0;
        while (insertion < original.Dacl.Count && IsExplicitDeny(original.Dacl[insertion]))
            insertion++;
        for (var index = 0; index < original.Dacl.Count + 1; index++)
        {
            if (index == insertion)
            {
                acl.InsertAce(index, sealAce);
                continue;
            }
            var sourceIndex = index < insertion ? index : index - 1;
            // Windows converts inherited ACEs to explicit ACEs when the DACL is
            // protected. Model that documented transition in the durable plan
            // instead of learning a post-mutation descriptor.
            acl.InsertAce(index, CloneAce(original.Dacl[sourceIndex], stripInherited: true));
        }
        var flags = (original.Raw.ControlFlags |
            ControlFlags.DiscretionaryAclPresent |
            ControlFlags.DiscretionaryAclProtected) &
            ~ControlFlags.DiscretionaryAclDefaulted;
        var plannedRaw = new RawSecurityDescriptor(
            flags,
            original.Raw.Owner,
            original.Raw.Group,
            systemAcl: null,
            discretionaryAcl: acl);
        var planned = Snapshot.FromRaw(plannedRaw);
        if (!planned.AreAccessRulesCanonical || !planned.IsProtected)
            throw new InvalidDataException("Membership seal could not construct a canonical protected DACL.");
        return planned;
    }

    private static bool IsExplicitDeny(GenericAce ace) =>
        (ace.AceFlags & AceFlags.Inherited) == 0 &&
        ace is QualifiedAce qualified &&
        qualified.AceQualifier == AceQualifier.AccessDenied;

    private static GenericAce CloneAce(GenericAce ace, bool stripInherited = false)
    {
        var bytes = new byte[ace.BinaryLength];
        ace.GetBinaryForm(bytes, 0);
        var clone = GenericAce.CreateFromBinaryForm(bytes, 0);
        if (stripInherited)
            clone.AceFlags &= ~AceFlags.Inherited;
        return clone;
    }

    private static Snapshot ReadSnapshot(SafeFileHandle handle, string context)
    {
        var error = GetSecurityInfo(
            handle,
            SeFileObject,
            OwnerSecurityInformation | GroupSecurityInformation | DaclSecurityInformation,
            out _,
            out _,
            out _,
            out _,
            out var descriptor);
        if (error != 0 || descriptor == IntPtr.Zero)
            throw new IOException($"Cannot read {context} (Win32 {error}).");
        try
        {
            var length = checked((int)GetSecurityDescriptorLength(descriptor));
            if (length <= 0 || length > MaximumDescriptorBytes)
                throw new InvalidDataException($"{context} exceeds the 64-KiB descriptor bound.");
            var bytes = new byte[length];
            Marshal.Copy(descriptor, bytes, 0, length);
            return Snapshot.FromBytes(bytes);
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    private static void RequireGrantedAccess(SafeFileHandle handle, string path)
    {
        var status = NtQueryObject(
            handle,
            objectInformationClass: 0,
            out var information,
            Marshal.SizeOf<ObjectBasicInformation>(),
            out _);
        if (status < 0)
            throw new IOException(
                $"Cannot verify membership-seal granted access for '{path}' " +
                $"(NTSTATUS 0x{status:x8}, Win32 {RtlNtStatusToDosError(status)}).");
        const uint required = FileListDirectory | FileReadAttributes | ReadControl | WriteDac;
        if ((information.GrantedAccess & required) != required)
            throw new InvalidDataException(
                $"Membership-seal MAXIMUM_ALLOWED handle lacks required access " +
                $"(granted 0x{information.GrantedAccess:x8}, required 0x{required:x8}).");
    }

    private static void RequireIdentity(
        PhysicalDirectoryIdentity actual,
        PhysicalDirectoryIdentity expected,
        string path)
    {
        if (!actual.SameObject(expected) ||
            !string.Equals(actual.FinalPath, Normalize(path), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.FinalPath, Normalize(path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Membership-seal directory identity or canonical path changed.");
    }

    private static string Normalize(string path) =>
        System.IO.Path.GetFullPath(path)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

    private static byte[] DecodeDescriptor(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumDescriptorBytes * 2)
            throw new InvalidDataException("Membership-seal descriptor encoding is outside its safety bound.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value); }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Membership-seal descriptor encoding is invalid.", ex);
        }
        if (bytes.Length <= 0 || bytes.Length > MaximumDescriptorBytes)
            throw new InvalidDataException("Membership-seal descriptor is outside its safety bound.");
        return bytes;
    }

    internal sealed record SecurityPlan(
        string Path,
        ulong VolumeSerialNumber,
        ulong FileIdLow,
        ulong FileIdHigh,
        string OriginalDescriptor,
        string SealedDescriptor,
        int DeniedRights)
    {
        internal PhysicalDirectoryIdentity ToIdentity() => new(
            VolumeSerialNumber,
            FileIdLow,
            FileIdHigh,
            Normalize(Path));

        internal bool SemanticallyMatches(SecurityPlan other) =>
            string.Equals(Normalize(Path), Normalize(other.Path), StringComparison.OrdinalIgnoreCase) &&
            VolumeSerialNumber == other.VolumeSerialNumber &&
            FileIdLow == other.FileIdLow &&
            FileIdHigh == other.FileIdHigh &&
            DeniedRights == other.DeniedRights &&
            Snapshot.FromBase64(OriginalDescriptor)
                .SemanticallyEquals(Snapshot.FromBase64(other.OriginalDescriptor)) &&
            Snapshot.FromBase64(SealedDescriptor)
                .SemanticallyEquals(Snapshot.FromBase64(other.SealedDescriptor));
    }

    private sealed class Snapshot
    {
        internal RawSecurityDescriptor Raw { get; }
        internal RawAcl? Dacl => Raw.DiscretionaryAcl;
        internal string DescriptorBase64 { get; }
        internal bool IsProtected =>
            (Raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0;
        internal bool AreAccessRulesCanonical
        {
            get
            {
                var security = new DirectorySecurity();
                security.SetSecurityDescriptorBinaryForm(
                    Convert.FromBase64String(DescriptorBase64),
                    AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
                return security.AreAccessRulesCanonical;
            }
        }

        private Snapshot(RawSecurityDescriptor raw, byte[] bytes)
        {
            Raw = raw;
            DescriptorBase64 = Convert.ToBase64String(bytes);
        }

        internal static Snapshot FromBase64(string value) => FromBytes(DecodeDescriptor(value));

        internal static Snapshot FromBytes(byte[] bytes)
        {
            if (bytes.Length <= 0 || bytes.Length > MaximumDescriptorBytes)
                throw new InvalidDataException("Membership-seal security descriptor is outside its bound.");
            RawSecurityDescriptor raw;
            try { raw = new RawSecurityDescriptor(bytes, 0); }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
            {
                throw new InvalidDataException("Membership-seal security descriptor is malformed.", ex);
            }
            if ((raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 ||
                raw.DiscretionaryAcl == null || raw.Owner == null || raw.Group == null)
                throw new InvalidDataException("Membership-seal security descriptor is incomplete.");
            return new Snapshot(raw, bytes.ToArray());
        }

        internal static Snapshot FromRaw(RawSecurityDescriptor raw)
        {
            var bytes = new byte[raw.BinaryLength];
            raw.GetBinaryForm(bytes, 0);
            return FromBytes(bytes);
        }

        internal bool SemanticallyEquals(Snapshot other)
        {
            if (!Equals(Raw.Owner, other.Raw.Owner) ||
                !Equals(Raw.Group, other.Raw.Group) ||
                (Raw.ControlFlags & DaclControlMask) !=
                    (other.Raw.ControlFlags & DaclControlMask) ||
                Dacl == null || other.Dacl == null ||
                Dacl.BinaryLength != other.Dacl.BinaryLength)
                return false;
            var left = new byte[Dacl.BinaryLength];
            var right = new byte[other.Dacl.BinaryLength];
            Dacl.GetBinaryForm(left, 0);
            other.Dacl.GetBinaryForm(right, 0);
            return CryptographicOperations.FixedTimeEquals(left, right);
        }

        internal string DescribeDifference(Snapshot expected)
        {
            var actualDacl = Dacl == null ? Array.Empty<byte>() : new byte[Dacl.BinaryLength];
            var expectedDacl = expected.Dacl == null
                ? Array.Empty<byte>()
                : new byte[expected.Dacl.BinaryLength];
            Dacl?.GetBinaryForm(actualDacl, 0);
            expected.Dacl?.GetBinaryForm(expectedDacl, 0);
            return $"owner={Raw.Owner}/{expected.Raw.Owner}; " +
                $"group={Raw.Group}/{expected.Raw.Group}; " +
                $"control=0x{((int)(Raw.ControlFlags & DaclControlMask)):x}/" +
                $"0x{((int)(expected.Raw.ControlFlags & DaclControlMask)):x}; " +
                $"dacl={Convert.ToHexString(SHA256.HashData(actualDacl))}/" +
                $"{Convert.ToHexString(SHA256.HashData(expectedDacl))}; " +
                $"sddl={Raw.GetSddlForm(AccessControlSections.Access)}/" +
                expected.Raw.GetSddlForm(AccessControlSections.Access);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(
        SafeFileHandle handle,
        int objectType,
        uint securityInformation,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(
        SafeFileHandle handle,
        int objectType,
        uint securityInformation,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorDacl(
        IntPtr securityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool daclPresent,
        out IntPtr dacl,
        [MarshalAs(UnmanagedType.Bool)] out bool daclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(
        SafeFileHandle handle,
        int objectInformationClass,
        out ObjectBasicInformation objectInformation,
        int objectInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectBasicInformation
    {
        internal uint Attributes;
        internal uint GrantedAccess;
        internal uint HandleCount;
        internal uint PointerCount;
        internal uint PagedPoolCharge;
        internal uint NonPagedPoolCharge;
        internal uint Reserved1;
        internal uint Reserved2;
        internal uint Reserved3;
        internal uint NameInformationLength;
        internal uint TypeInformationLength;
        internal uint SecurityDescriptorLength;
        internal long CreationTime;
    }
}
