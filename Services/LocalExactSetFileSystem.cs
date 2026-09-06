using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace VmbLauncher.Services;

/// <summary>
/// Authenticated filesystem primitives for receipt-authority exact-set
/// deployment. Destructive operations remain handle-bound from proof through
/// delete/rename so a path replacement cannot redirect them.
/// </summary>
internal static partial class LocalExactSetDeployment
{
    private static void DeletePrecommittedTemp(
        string path,
        ulong? expectedVolumeSerialNumber = null,
        ulong? expectedFileIdLow = null,
        ulong? expectedFileIdHigh = null)
    {
        if (!expectedVolumeSerialNumber.HasValue ||
            !expectedFileIdLow.HasValue ||
            !expectedFileIdHigh.HasValue)
            throw new InvalidDataException(
                "Unidentified precommitted stage temporary leaf is preserved; its name or emptiness is not deletion authority.");
        using var stream = OpenPinnedRegularFile(path);
        var identity = ImmutableBundleSourceLease.GetFileIdInformation(
            stream.SafeFileHandle,
            path);
        if (identity.VolumeSerialNumber != expectedVolumeSerialNumber.Value ||
            identity.FileIdLow != expectedFileIdLow.Value ||
            identity.FileIdHigh != expectedFileIdHigh.Value)
            throw new InvalidDataException(
                "Recorded stage temporary leaf no longer has its durable file identity.");
        MarkDeleteOnClose(stream.SafeFileHandle);
        stream.Dispose();
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("Recorded stage temporary leaf was replaced during deletion.");
    }

    private static void RequireExactFile(
        string path,
        CommitQualifiedOutputFile expected,
        bool delete,
        ulong? expectedVolumeSerialNumber = null,
        ulong? expectedFileIdLow = null,
        ulong? expectedFileIdHigh = null)
    {
        if (expectedVolumeSerialNumber.HasValue != expectedFileIdLow.HasValue ||
            expectedFileIdLow.HasValue != expectedFileIdHigh.HasValue)
            throw new InvalidDataException("Recorded exact file identity is incomplete.");
        using var stream = OpenPinnedRegularFile(path);
        if (expectedVolumeSerialNumber.HasValue)
        {
            var identity = ImmutableBundleSourceLease.GetFileIdInformation(
                stream.SafeFileHandle,
                path);
            if (identity.VolumeSerialNumber != expectedVolumeSerialNumber.Value ||
                identity.FileIdLow != expectedFileIdLow!.Value ||
                identity.FileIdHigh != expectedFileIdHigh!.Value)
                throw new InvalidDataException(
                    $"Recorded stage file no longer has its durable physical identity: {expected.Name}");
        }
        if (stream.Length != expected.Length)
            throw new InvalidDataException(
                $"Recorded stage file length differs from its proof: {expected.Name}");
        var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (actualHash != expected.Sha256)
            throw new InvalidDataException(
                $"Recorded stage file differs from its proof: {expected.Name}");
        if (delete)
        {
            MarkDeleteOnClose(stream.SafeFileHandle);
            stream.Dispose();
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException("Recorded stage canonical leaf was replaced during deletion.");
        }
    }

    private static FileStream OpenPinnedRegularFile(
        string path,
        bool allowDeleteShare = false)
    {
        var normalized = Normalize(path);
        var handle = CreateFileForDeleteW(
            normalized,
            GenericRead | DeleteAccess | FileReadAttributes,
            allowDeleteShare
                ? FileShare.Read | FileShare.Write | FileShare.Delete
                : FileShare.Read,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException(
                $"Cannot pin recorded stage leaf for deletion: {normalized} (Win32 {Marshal.GetLastWin32Error()}).");
        }
        FileStream? stream = null;
        try
        {
            var information = ImmutableBundleSourceLease.GetHandleInformation(handle, normalized);
            if ((information.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException(
                    $"Recorded stage leaf is not a regular file: {normalized}");
            if (information.NumberOfLinks != 1)
                throw new InvalidDataException(
                    $"Recorded stage leaf is hard-link aliased: {normalized}");
            if (!string.Equals(
                    Normalize(ImmutableBundleSourceLease.GetFinalPath(handle)),
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Recorded stage leaf resolves through a reparse or alias: {normalized}");
            stream = new FileStream(handle, FileAccess.Read, 128 * 1024, isAsync: false);
            return stream;
        }
        catch
        {
            stream?.Dispose();
            if (stream == null) handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenPinnedDirectoryForDelete(string path)
    {
        var normalized = Normalize(path);
        var handle = CreateFileForDeleteW(
            normalized,
            FileListDirectory | FileReadAttributes | DeleteAccess,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException(
                $"Cannot pin exact-set directory for deletion: {normalized} (Win32 {Marshal.GetLastWin32Error()}).");
        }
        try
        {
            var information = ImmutableBundleSourceLease.GetHandleInformation(handle, normalized);
            if ((information.FileAttributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    $"Exact-set deletion directory is a reparse point: {normalized}");
            if (!string.Equals(
                    Normalize(ImmutableBundleSourceLease.GetFinalPath(handle)),
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Exact-set deletion directory resolves through a reparse or alias: {normalized}");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void MarkDeleteOnClose(SafeFileHandle handle)
    {
        var disposition = new FileDispositionInformation { DeleteFile = 1 };
        if (!SetFileInformationByHandle(
                handle,
                FileInformationByHandleClass.FileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInformation>()))
            throw new IOException(
                $"Could not mark authenticated exact-set object for deletion (Win32 {Marshal.GetLastWin32Error()}).");
    }

    private static void RenamePinnedObject(
        SafeFileHandle handle,
        string destination,
        bool replaceIfExists,
        SafeFileHandle? destinationParent = null)
    {
        var fullDestination = Normalize(destination);
        var destinationName = destinationParent == null
            ? fullDestination
            : Path.GetFileName(fullDestination);
        if (destinationParent != null &&
            (string.IsNullOrWhiteSpace(destinationName) ||
             destinationName.Contains(Path.DirectorySeparatorChar) ||
             destinationName.Contains(Path.AltDirectorySeparatorChar)))
            throw new InvalidDataException("Handle-bound rename destination leaf is noncanonical.");
        var nameBytes = Encoding.Unicode.GetBytes(destinationName);
        var headerSize = IntPtr.Size == 8 ? 20 : 12;
        var bufferLength = checked(headerSize + nameBytes.Length + sizeof(char));
        var buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            for (var offset = 0; offset < bufferLength; offset++) Marshal.WriteByte(buffer, offset, 0);
            if (replaceIfExists)
            {
                // FILE_RENAME_INFO_EX with POSIX replacement keeps both the
                // source and displaced journal identities handle-bound even
                // while the old journal is pinned for transaction proof.
                Marshal.WriteInt32(
                    buffer,
                    0,
                    FileRenameFlagReplaceIfExists | FileRenameFlagPosixSemantics);
            }
            else
            {
                Marshal.WriteByte(buffer, 0, 0);
            }
            Marshal.WriteIntPtr(
                buffer,
                IntPtr.Size == 8 ? 8 : 4,
                destinationParent?.DangerousGetHandle() ?? IntPtr.Zero);
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 16 : 8, nameBytes.Length);
            Marshal.Copy(nameBytes, 0, IntPtr.Add(buffer, headerSize), nameBytes.Length);
            if (destinationParent != null && !replaceIfExists)
            {
                var status = NtSetInformationFile(
                    handle,
                    out _,
                    buffer,
                    (uint)bufferLength,
                    FileRenameInformation);
                if (status < 0)
                    throw new IOException(
                        $"Could not atomically rename pinned exact-set object " +
                        $"(NTSTATUS 0x{status:x8}, Win32 {RtlNtStatusToDosError(status)}).");
            }
            else if (!SetFileInformationByHandleBuffer(
                         handle,
                         replaceIfExists
                             ? FileInformationByHandleClass.FileRenameInfoEx
                             : FileInformationByHandleClass.FileRenameInfo,
                         buffer,
                         (uint)bufferLength))
            {
                throw new IOException(
                    $"Could not atomically rename pinned exact-set object (Win32 {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ExactDirectorySnapshot CaptureSnapshot(
        string directory,
        string modName,
        bool requireExactOwner)
    {
        using var directoryLease = ExactDirectoryLease.OpenExisting(directory);
        using var snapshot = ExactDirectorySnapshotLease.Capture(
            directoryLease,
            modName,
            requireExactOwner);
        return snapshot.Snapshot;
    }

    private static void DeleteRemainingExactDirectory(
        string directory,
        IReadOnlyList<CommitQualifiedOutputFile> allowed,
        IReadOnlyDictionary<string, PhysicalDirectoryIdentity> expectedFileIdentities,
        PhysicalDirectoryIdentity expectedIdentity,
        string context)
    {
        using var lease = ExactDirectoryLease.OpenExisting(directory, expectedIdentity, context);
        DeleteRemainingExactDirectory(lease, allowed, expectedFileIdentities, context);
    }

    private static void DeleteRemainingExactDirectory(
        ExactDirectoryLease lease,
        IReadOnlyList<CommitQualifiedOutputFile> allowed,
        IReadOnlyDictionary<string, PhysicalDirectoryIdentity> expectedFileIdentities,
        string context)
    {
        lease.RequireCurrentPath(context);
        var root = lease.CurrentPath;
        if (allowed.Count > MaximumManagedFiles)
            throw new InvalidDataException($"Refusing to delete {context}; its proof is oversized.");
        var expected = allowed.ToDictionary(file => file.Name, StringComparer.Ordinal);
        if (expectedFileIdentities.Count != expected.Count ||
            !expected.Keys.All(expectedFileIdentities.ContainsKey))
            throw new InvalidDataException(
                $"Refusing to delete {context}; its per-leaf physical identity proof is incomplete.");
        var names = new List<string>();
        long aggregate = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (names.Count >= MaximumManagedFiles)
                throw new InvalidDataException(
                    $"Refusing to delete {context}; its census exceeds the 4096-file bound.");
            var name = Path.GetFileName(entry);
            if (!expected.TryGetValue(name, out var proof))
                throw new InvalidDataException(
                    $"Refusing to delete {context}; '{name}' is not in the proven set.");
            try { aggregate = checked(aggregate + proof.Length); }
            catch (OverflowException)
            {
                throw new InvalidDataException(
                    $"Refusing to delete {context}; its census length overflowed.");
            }
            if (aggregate > MaximumManagedBytes)
                throw new InvalidDataException(
                    $"Refusing to delete {context}; its census exceeds the 32-GiB bound.");
            names.Add(name);
        }
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            throw new InvalidDataException(
                $"Refusing to delete {context}; its remaining set is case-colliding.");

        foreach (var name in names.OrderBy(name => name, StringComparer.Ordinal))
        {
            var path = Path.Combine(root, name);
            using var stream = OpenPinnedRegularFile(path);
            var proof = expected[name];
            var expectedIdentity = expectedFileIdentities[name];
            var actualIdentity = ImmutableBundleSourceLease.GetFileIdInformation(
                stream.SafeFileHandle,
                path);
            if (actualIdentity.VolumeSerialNumber != expectedIdentity.VolumeSerialNumber ||
                actualIdentity.FileIdLow != expectedIdentity.FileIdLow ||
                actualIdentity.FileIdHigh != expectedIdentity.FileIdHigh)
                throw new InvalidDataException(
                    $"Refusing to delete {context}; '{name}' no longer has its recorded physical identity.");
            if (stream.Length != proof.Length)
                throw new InvalidDataException(
                    $"Refusing to delete {context}; '{name}' length differs from the proven byte identity.");
            var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (actualHash != proof.Sha256)
                throw new InvalidDataException(
                    $"Refusing to delete {context}; '{name}' differs from the proven byte identity.");
            Checkpoint("delete-file-pinned");
            MarkDeleteOnClose(stream.SafeFileHandle);
            stream.Dispose();
            Checkpoint("cleanup-file-deleted");
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException(
                    $"Refusing to delete {context}; '{name}' was replaced during deletion.");
        }
        if (Directory.EnumerateFileSystemEntries(root).Take(1).Any())
            throw new InvalidDataException(
                $"Refusing to delete {context}; its directory did not become empty.");
        Checkpoint("cleanup-files-cleared");
        lease.DeleteWhenEmpty(context);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileForDeleteW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInformationByHandleClass fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandleBuffer(
        SafeFileHandle file,
        FileInformationByHandleClass fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(
        SafeFileHandle file,
        out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        int fileInformationClass);

    private enum FileInformationByHandleClass
    {
        FileRenameInfo = 3,
        FileDispositionInfo = 4,
        FileRenameInfoEx = 22,
    }

    private const int FileRenameFlagReplaceIfExists = 0x00000001;
    private const int FileRenameFlagPosixSemantics = 0x00000002;
    private const int FileRenameInformation = 10;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        internal byte DeleteFile;
    }
}
