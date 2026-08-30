using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace VmbLauncher.Services;

/// <summary>
/// Continuous physical-namespace leases for the receipt-deploy transaction.
/// Directory creation and every central rename are handle-bound; snapshots pin
/// their complete leaf set before hashing and may remain live through cleanup.
/// </summary>
internal static partial class LocalExactSetDeployment
{
#if VMBLAUNCHER_TEST_HOOKS
    internal static Action<string>? SnapshotHashTransitionForTest;
#endif
    private const uint SynchronizeAccess = 0x00100000;
    private const uint FileCreateDisposition = 2;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileWriteThroughOption = 0x00000002;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint ObjectCaseInsensitive = 0x00000040;

    private sealed class ExactDirectoryLease : IDisposable
    {
        private SafeFileHandle? _handle;

        internal SafeFileHandle Handle => _handle is { IsClosed: false }
            ? _handle
            : throw new ObjectDisposedException(nameof(ExactDirectoryLease));
        internal string CurrentPath { get; private set; }
        internal PhysicalDirectoryIdentity Identity { get; }

        private ExactDirectoryLease(
            string path,
            SafeFileHandle handle,
            PhysicalDirectoryIdentity identity)
        {
            CurrentPath = path;
            _handle = handle;
            Identity = identity;
        }

        internal static ExactDirectoryLease OpenExisting(
            string path,
            PhysicalDirectoryIdentity? expected = null,
            string context = "exact-set directory")
        {
            var normalized = Normalize(path);
            var handle = OpenPinnedDirectoryForDelete(normalized);
            try
            {
                var identity = IdentityFromHandle(handle, normalized);
                if (expected != null) RequireIdentity(identity, expected, context);
                return new ExactDirectoryLease(normalized, handle, identity);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        internal static ExactDirectoryLease CreateNew(
            ExactDirectoryLease parentLease,
            string path)
        {
            var normalized = Normalize(path);
            var parent = Normalize(Path.GetDirectoryName(normalized)
                ?? throw new InvalidDataException("Atomic stage path has no parent."));
            var leaf = Path.GetFileName(normalized);
            if (string.IsNullOrWhiteSpace(leaf) ||
                leaf.Contains(Path.DirectorySeparatorChar) ||
                leaf.Contains(Path.AltDirectorySeparatorChar))
                throw new InvalidDataException("Atomic stage leaf is noncanonical.");

            parentLease.RequireCurrentPath("atomic stage creation parent");
            if (!string.Equals(parentLease.CurrentPath, parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Atomic stage path escapes its pinned parent.");

            SafeFileHandle? created = null;
            var text = IntPtr.Zero;
            var unicodePointer = IntPtr.Zero;
            try
            {
                text = Marshal.StringToHGlobalUni(leaf);
                var unicode = new UnicodeString
                {
                    Length = checked((ushort)(leaf.Length * sizeof(char))),
                    MaximumLength = checked((ushort)((leaf.Length + 1) * sizeof(char))),
                    Buffer = text,
                };
                unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
                Marshal.StructureToPtr(unicode, unicodePointer, fDeleteOld: false);
                var attributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf<ObjectAttributes>(),
                    RootDirectory = parentLease.Handle.DangerousGetHandle(),
                    ObjectName = unicodePointer,
                    Attributes = ObjectCaseInsensitive,
                };
                var status = NtCreateFile(
                    out created,
                    FileListDirectory | FileReadAttributes | DeleteAccess | SynchronizeAccess,
                    ref attributes,
                    out _,
                    IntPtr.Zero,
                    FileAttributes.Normal,
                    FileShare.Read | FileShare.Write | FileShare.Delete,
                    FileCreateDisposition,
                    FileDirectoryFile | FileSynchronousIoNonAlert,
                    IntPtr.Zero,
                    0);
                if (status < 0 || created == null || created.IsInvalid)
                {
                    created?.Dispose();
                    throw new IOException(
                        $"Could not atomically create receipt-deploy stage '{normalized}' " +
                        $"(NTSTATUS 0x{status:x8}, Win32 {RtlNtStatusToDosError(status)}).");
                }
                var identity = IdentityFromHandle(created, normalized);
                var lease = new ExactDirectoryLease(normalized, created, identity);
                created = null;
                return lease;
            }
            finally
            {
                created?.Dispose();
                if (unicodePointer != IntPtr.Zero) Marshal.FreeHGlobal(unicodePointer);
                if (text != IntPtr.Zero) Marshal.FreeHGlobal(text);
            }
        }

        internal void RequireCurrentPath(string context)
        {
            var actual = IdentityFromHandle(Handle, CurrentPath);
            RequireIdentity(actual, Identity, context);
            if (!string.Equals(actual.FinalPath, CurrentPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{context} no longer owns its canonical path.");
        }

        internal void RenameTo(
            ExactDirectoryLease destinationParent,
            string destination,
            string context)
        {
            var normalized = Normalize(destination);
            destinationParent.RequireCurrentPath("rename destination parent");
            if (!string.Equals(
                    Normalize(Path.GetDirectoryName(normalized)
                        ?? throw new InvalidDataException("Rename destination has no parent.")),
                    destinationParent.CurrentPath,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Rename destination escapes its pinned parent.");
            try
            {
                RenamePinnedObject(
                    Handle,
                    normalized,
                    replaceIfExists: false,
                    destinationParent.Handle);
            }
            catch (Exception ex)
            {
                throw new IOException($"Could not rename {context} by its pinned identity: {ex.Message}", ex);
            }
            var actual = IdentityFromHandle(Handle, normalized);
            RequireIdentity(actual, Identity, context);
            if (!string.Equals(actual.FinalPath, normalized, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{context} handle-bound rename landed at another path.");
            CurrentPath = normalized;
        }

        internal void DeleteWhenEmpty(string context)
        {
            RequireCurrentPath(context);
            if (Directory.EnumerateFileSystemEntries(CurrentPath).Take(1).Any())
                throw new InvalidDataException($"Refusing to delete nonempty {context}.");
            MarkDeleteOnClose(Handle);
            Dispose();
            if (Directory.Exists(CurrentPath) || File.Exists(CurrentPath))
                throw new IOException($"{context} was replaced while its pinned identity was deleted.");
        }

        public void Dispose()
        {
            _handle?.Dispose();
            _handle = null;
        }
    }

    private static SafeFileHandle CreateNewPinnedFile(
        ExactDirectoryLease parentLease,
        string path)
    {
        var normalized = Normalize(path);
        var parent = Normalize(Path.GetDirectoryName(normalized)
            ?? throw new InvalidDataException("Atomic file path has no parent."));
        var leaf = Path.GetFileName(normalized);
        parentLease.RequireCurrentPath("atomic file creation parent");
        if (!string.Equals(parentLease.CurrentPath, parent, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(leaf) ||
            leaf.Contains(Path.DirectorySeparatorChar) ||
            leaf.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidDataException("Atomic file path escapes its pinned parent.");

        SafeFileHandle? created = null;
        var text = IntPtr.Zero;
        var unicodePointer = IntPtr.Zero;
        try
        {
            text = Marshal.StringToHGlobalUni(leaf);
            var unicode = new UnicodeString
            {
                Length = checked((ushort)(leaf.Length * sizeof(char))),
                MaximumLength = checked((ushort)((leaf.Length + 1) * sizeof(char))),
                Buffer = text,
            };
            unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicode, unicodePointer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parentLease.Handle.DangerousGetHandle(),
                ObjectName = unicodePointer,
                Attributes = ObjectCaseInsensitive,
            };
            var status = NtCreateFile(
                out created,
                GenericRead | GenericWrite | DeleteAccess | FileReadAttributes | SynchronizeAccess,
                ref attributes,
                out _,
                IntPtr.Zero,
                FileAttributes.Normal,
                FileShare.Read,
                FileCreateDisposition,
                FileNonDirectoryFile | FileSynchronousIoNonAlert | FileWriteThroughOption,
                IntPtr.Zero,
                0);
            if (status < 0 || created == null || created.IsInvalid)
            {
                created?.Dispose();
                throw new IOException(
                    $"Could not atomically create receipt-deploy file '{normalized}' " +
                    $"(NTSTATUS 0x{status:x8}, Win32 {RtlNtStatusToDosError(status)}).");
            }
            var information = ImmutableBundleSourceLease.GetHandleInformation(created, normalized);
            if ((information.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                information.NumberOfLinks != 1 ||
                !string.Equals(
                    Normalize(ImmutableBundleSourceLease.GetFinalPath(created)),
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Atomic receipt-deploy file lost its exact created identity.");
            var result = created;
            created = null;
            return result;
        }
        finally
        {
            created?.Dispose();
            if (unicodePointer != IntPtr.Zero) Marshal.FreeHGlobal(unicodePointer);
            if (text != IntPtr.Zero) Marshal.FreeHGlobal(text);
        }
    }

    private sealed class ExactDirectorySnapshotLease : IDisposable
    {
        private readonly List<FileStream> _streams;
        internal ExactDirectorySnapshot Snapshot { get; }

        private ExactDirectorySnapshotLease(
            ExactDirectorySnapshot snapshot,
            List<FileStream> streams)
        {
            Snapshot = snapshot;
            _streams = streams;
        }

        internal static ExactDirectorySnapshotLease Capture(
            ExactDirectoryLease directory,
            string modName,
            bool requireExactOwner)
        {
            directory.RequireCurrentPath("exact-set census root");
            var entries = EnumerateCanonicalLeaves(directory.CurrentPath);
            var streams = new List<FileStream>(entries.Count);
            try
            {
                long aggregate = 0;
                foreach (var entry in entries)
                {
                    var stream = new FileStream(
                        entry.Path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        128 * 1024,
                        FileOptions.SequentialScan);
                    streams.Add(stream);
                    var information = ImmutableBundleSourceLease.GetHandleInformation(
                        stream.SafeFileHandle,
                        entry.Path);
                    if ((information.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                        throw new InvalidDataException(
                            $"Exact-set file is not a regular file: {entry.Name}");
                    if (information.NumberOfLinks != 1)
                        throw new InvalidDataException(
                            $"Exact-set file is hard-link aliased: {entry.Name}");
                    var finalPath = Normalize(ImmutableBundleSourceLease.GetFinalPath(stream.SafeFileHandle));
                    if (!string.Equals(finalPath, entry.Path, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            $"Exact-set file resolves through a reparse or alias: {entry.Name}");
                    try { aggregate = checked(aggregate + stream.Length); }
                    catch (OverflowException)
                    {
                        throw new InvalidDataException(
                            "Exact-set directory census length overflowed its safety bound.");
                    }
                    if (aggregate > MaximumManagedBytes)
                        throw new InvalidDataException(
                            "Exact-set directory census exceeds the 32-GiB safety bound.");
                }

                RequireExactLeafNames(directory.CurrentPath, entries);
                directory.RequireCurrentPath("pinned exact-set census root");
                var files = new List<CommitQualifiedOutputFile>(entries.Count);
                var fileIdentities = new Dictionary<string, PhysicalDirectoryIdentity>(StringComparer.Ordinal);
                for (var index = 0; index < entries.Count; index++)
                {
                    var stream = streams[index];
                    stream.Position = 0;
                    SnapshotHashCheckpoint(entries[index].Name);
                    files.Add(new CommitQualifiedOutputFile(
                        entries[index].Name,
                        stream.Length,
                        Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()));
                    var id = ImmutableBundleSourceLease.GetFileIdInformation(
                        stream.SafeFileHandle,
                        entries[index].Path);
                    fileIdentities.Add(
                        entries[index].Name,
                        new PhysicalDirectoryIdentity(
                            id.VolumeSerialNumber,
                            id.FileIdLow,
                            id.FileIdHigh,
                            entries[index].Path));
                    stream.Position = 0;
                }
                RequireExactLeafNames(directory.CurrentPath, entries);
                directory.RequireCurrentPath("hashed exact-set census root");

                if (requireExactOwner)
                {
                    var descriptors = files
                        .Where(file => file.Name.EndsWith(".mod", StringComparison.Ordinal))
                        .ToArray();
                    if (descriptors.Length != 1 || descriptors[0].Name != $"{modName}.mod")
                        throw new InvalidDataException(
                            $"Destination ownership is ambiguous; expected exactly one '{modName}.mod' descriptor.");
                }
                var snapshot = new ExactDirectorySnapshot(
                    directory.Identity,
                    Array.AsReadOnly(files.OrderBy(file => file.Name, StringComparer.Ordinal).ToArray()),
                    new System.Collections.ObjectModel.ReadOnlyDictionary<string, PhysicalDirectoryIdentity>(
                        fileIdentities));
                return new ExactDirectorySnapshotLease(snapshot, streams);
            }
            catch
            {
                foreach (var stream in streams.AsEnumerable().Reverse()) stream.Dispose();
                throw;
            }
        }

        internal void RequireCurrentNamespace(ExactDirectoryLease directory, string context)
        {
            var expected = Snapshot.Files
                .Select(file => (file.Name, Path: Normalize(Path.Combine(directory.CurrentPath, file.Name))))
                .ToArray();
            RequireExactLeafNames(directory.CurrentPath, expected);
            directory.RequireCurrentPath(context);
            for (var index = 0; index < _streams.Count; index++)
            {
                var finalPath = Normalize(ImmutableBundleSourceLease.GetFinalPath(
                    _streams[index].SafeFileHandle));
                if (!string.Equals(finalPath, expected[index].Path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{context} leaf namespace changed after proof.");
            }
        }

        public void Dispose()
        {
            foreach (var stream in _streams.AsEnumerable().Reverse()) stream.Dispose();
            _streams.Clear();
        }
    }

    private static void SnapshotHashCheckpoint(string name)
    {
#if VMBLAUNCHER_TEST_HOOKS
        SnapshotHashTransitionForTest?.Invoke(name);
#endif
    }

    private static List<(string Name, string Path)> EnumerateCanonicalLeaves(string root)
    {
        var entries = new List<(string Name, string Path)>();
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (entries.Count >= MaximumManagedFiles)
                throw new InvalidDataException(
                    "Exact-set directory census exceeds the 4096-file safety bound.");
            var name = Path.GetFileName(entry);
            var attributes = File.GetAttributes(entry);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                !IsCanonicalManagedOutput(name) ||
                !folded.Add(name))
                throw new InvalidDataException(
                    $"Exact-set directory contains an unknown, nested, reparse, or case-colliding entry: {name}");
            entries.Add((name, Normalize(entry)));
        }
        entries.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        return entries;
    }

    private static void RequireExactLeafNames(
        string root,
        IReadOnlyList<(string Name, string Path)> expected)
    {
        var actual = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (actual.Count >= MaximumManagedFiles)
                throw new InvalidDataException(
                    "Exact-set directory recensus exceeds the 4096-file safety bound.");
            var attributes = File.GetAttributes(entry);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("Exact-set directory recensus found a nested/reparse entry.");
            actual.Add(Path.GetFileName(entry));
        }
        actual.Sort(StringComparer.Ordinal);
        if (!actual.SequenceEqual(expected.Select(entry => entry.Name), StringComparer.Ordinal))
            throw new InvalidDataException("Exact-set directory leaf namespace changed during its pinned census.");
    }

    private static PhysicalDirectoryIdentity IdentityFromHandle(
        SafeFileHandle handle,
        string expectedPath)
    {
        var information = ImmutableBundleSourceLease.GetHandleInformation(handle, expectedPath);
        if ((information.FileAttributes & FileAttributes.ReparsePoint) != 0 ||
            (information.FileAttributes & FileAttributes.Directory) == 0)
            throw new InvalidDataException($"Exact-set directory is not a regular directory: {expectedPath}");
        var fileId = ImmutableBundleSourceLease.GetFileIdInformation(handle, expectedPath);
        return new PhysicalDirectoryIdentity(
            fileId.VolumeSerialNumber,
            fileId.FileIdLow,
            fileId.FileIdHigh,
            Normalize(ImmutableBundleSourceLease.GetFinalPath(handle)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal int Length;
        internal IntPtr RootDirectory;
        internal IntPtr ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor;
        internal IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal IntPtr Status;
        internal UIntPtr Information;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        FileAttributes fileAttributes,
        FileShare shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
