using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VmbLauncher.Services;

/// <summary>
/// Holds the exact receipt-qualified source bytes open with write/delete
/// sharing denied. Deployment copies from these streams, never from mutable
/// paths after authorization.
/// </summary>
internal sealed class ImmutableBundleSourceLease : IDisposable
{
#if VMBLAUNCHER_TEST_HOOKS
    internal static Action<string>? CaptureTransitionForTest;
#endif
    private const uint FileListDirectory = 0x0001;
    private const uint FileReadAttributes = 0x0080;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int MaximumOutputFiles = 4096;

    private readonly SafeFileHandle _directoryHandle;
    private readonly Dictionary<string, FileStream> _streams;

    internal string SourceRoot { get; }
    internal PhysicalDirectoryIdentity SourceIdentity { get; }
    internal IReadOnlyList<CommitQualifiedOutputFile> Files { get; }

    private ImmutableBundleSourceLease(
        string sourceRoot,
        PhysicalDirectoryIdentity sourceIdentity,
        SafeFileHandle directoryHandle,
        Dictionary<string, FileStream> streams,
        IReadOnlyList<CommitQualifiedOutputFile> files)
    {
        SourceRoot = sourceRoot;
        SourceIdentity = sourceIdentity;
        _directoryHandle = directoryHandle;
        _streams = streams;
        Files = Array.AsReadOnly(files
            .Select(file => new CommitQualifiedOutputFile(file.Name, file.Length, file.Sha256))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToArray());
    }

    internal static ImmutableBundleSourceLease Capture(
        string sourceDirectory,
        string modName,
        IReadOnlyList<CommitQualifiedOutputFile> expected)
    {
        MachineTransactionLease.RequireCurrent("Receipt-authority source-byte lease");
        LocalExactSetDeployment.ValidateExpectedMap(modName, expected);
        var root = Normalize(sourceDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Receipt-authority bundle source is missing: {root}");

        SafeFileHandle? directoryHandle = null;
        var streams = new Dictionary<string, FileStream>(StringComparer.Ordinal);
        try
        {
            directoryHandle = CreateFileW(
                root,
                FileListDirectory | FileReadAttributes,
                FileShare.Read,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (directoryHandle.IsInvalid)
                throw new IOException(
                    $"Cannot pin receipt-authority source directory '{root}' (Win32 {Marshal.GetLastWin32Error()}).");
            var directoryInfo = GetHandleInformation(directoryHandle, root);
            if ((directoryInfo.FileAttributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "Receipt-authority source directory may not be a reparse point.");
            var finalRoot = Normalize(GetFinalPath(directoryHandle));
            if (!string.Equals(root, finalRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Receipt-authority source directory resolves through a reparse or alias.");

            var expectedNames = expected.Select(file => file.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            RequireExactPhysicalCensus(root, expectedNames);
            CaptureCheckpoint("initial-census");

            foreach (var file in expected.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                var path = Path.Combine(root, file.Name);
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new InvalidDataException(
                        $"Receipt-qualified source output is not a regular file: {file.Name}");

                var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.SequentialScan);
                streams.Add(file.Name, stream);
                var fileInformation = GetHandleInformation(stream.SafeFileHandle, path);
                if (fileInformation.NumberOfLinks != 1)
                    throw new InvalidDataException(
                        $"Receipt-qualified source output is hard-link aliased: {file.Name}");
                var finalPath = Normalize(GetFinalPath(stream.SafeFileHandle));
                if (!string.Equals(path, finalPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Receipt-qualified source output resolves through a reparse or alias: {file.Name}");
                if (stream.Length != file.Length ||
                    !string.Equals(Hash(stream), file.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Receipt-qualified source output differs from its commit proof: {file.Name}");
            }

            CaptureCheckpoint("handles-open");
            RequireExactPhysicalCensus(root, expectedNames);

            var fileId = GetFileIdInformation(directoryHandle, root);
            var identity = new PhysicalDirectoryIdentity(
                fileId.VolumeSerialNumber,
                fileId.FileIdLow,
                fileId.FileIdHigh,
                finalRoot);
            return new ImmutableBundleSourceLease(root, identity, directoryHandle, streams, expected);
        }
        catch
        {
            foreach (var stream in streams.Values.Reverse()) stream.Dispose();
            directoryHandle?.Dispose();
            throw;
        }
    }

    internal void CopyTo(
        string name,
        Stream destination,
        Action? afterFirstWrite = null)
    {
        MachineTransactionLease.RequireCurrent("Receipt-authority source copy");
        if (!_streams.TryGetValue(name, out var source))
            throw new InvalidOperationException(
                $"Pinned receipt-authority source does not contain '{name}'.");
        source.Position = 0;
        var buffer = new byte[128 * 1024];
        var notified = false;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            destination.Write(buffer, 0, read);
            if (!notified)
            {
                notified = true;
                afterFirstWrite?.Invoke();
            }
        }
        if (!notified) afterFirstWrite?.Invoke();
        source.Position = 0;
    }

    private static void CaptureCheckpoint(string name)
    {
#if VMBLAUNCHER_TEST_HOOKS
        CaptureTransitionForTest?.Invoke(name);
#endif
    }

    public void Dispose()
    {
        List<Exception>? failures = null;
        foreach (var stream in _streams.Values.Reverse())
        {
            try { stream.Dispose(); }
            catch (Exception ex)
            {
                failures ??= new List<Exception>();
                failures.Add(ex);
            }
        }
        _streams.Clear();
        try { _directoryHandle.Dispose(); }
        catch (Exception ex)
        {
            failures ??= new List<Exception>();
            failures.Add(ex);
        }
        if (failures != null)
            throw new AggregateException(
                "One or more receipt-authority source leases could not be released.",
                failures);
    }

    private static string Hash(FileStream stream)
    {
        stream.Position = 0;
        var result = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        return result;
    }

    private static void RequireExactPhysicalCensus(
        string root,
        IReadOnlyList<string> expectedNames)
    {
        var names = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (names.Count >= MaximumOutputFiles)
                throw new InvalidDataException(
                    "Receipt-authority source census exceeds the 4096-file safety bound.");
            var name = Path.GetFileName(entry);
            var attributes = File.GetAttributes(entry);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException(
                    $"Receipt-authority source contains a nested or reparse entry: {name}");
            names.Add(name);
        }
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            throw new InvalidDataException(
                "Receipt-authority source census is oversized or case-colliding.");
        names.Sort(StringComparer.Ordinal);
        if (!names.SequenceEqual(expectedNames, StringComparer.Ordinal))
            throw new InvalidDataException(
                "Receipt-authority source census contains an extra, missing, or case-mismatched output.");
    }

    internal static PhysicalDirectoryIdentity InspectDirectory(string path)
    {
        var root = Normalize(path);
        using var handle = CreateFileW(
            root,
            FileListDirectory | FileReadAttributes,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException(
                $"Cannot inspect exact-set directory '{root}' (Win32 {Marshal.GetLastWin32Error()}).");
        var information = GetHandleInformation(handle, root);
        if ((information.FileAttributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Exact-set directory is a reparse point: {root}");
        var finalPath = Normalize(GetFinalPath(handle));
        if (!string.Equals(root, finalPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Exact-set directory resolves through a reparse or alias: {root}");
        var fileId = GetFileIdInformation(handle, root);
        return new PhysicalDirectoryIdentity(
            fileId.VolumeSerialNumber,
            fileId.FileIdLow,
            fileId.FileIdHigh,
            finalPath);
    }

    internal static string GetFinalPath(SafeFileHandle handle)
    {
        var required = GetFinalPathNameByHandleW(handle, null, 0, 0);
        if (required == 0)
            throw new IOException(
                $"Cannot resolve pinned source path (Win32 {Marshal.GetLastWin32Error()}).");
        var builder = new StringBuilder(checked((int)required + 1));
        var written = GetFinalPathNameByHandleW(handle, builder, (uint)builder.Capacity, 0);
        if (written == 0 || written >= builder.Capacity)
            throw new IOException(
                $"Cannot resolve pinned source path (Win32 {Marshal.GetLastWin32Error()}).");
        var path = builder.ToString();
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            return "\\\\" + path[8..];
        return path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase)
            ? path[4..]
            : path;
    }

    internal static ByHandleFileInformation GetHandleInformation(
        SafeFileHandle handle,
        string path)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new IOException(
                $"Cannot inspect pinned source '{path}' (Win32 {Marshal.GetLastWin32Error()}).");
        return information;
    }

    internal static FileIdInformation GetFileIdInformation(
        SafeFileHandle handle,
        string path)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileIdInfo,
                out var information,
                (uint)Marshal.SizeOf<FileIdInformation>()))
            throw new IOException(
                $"Cannot inspect full file identity for '{path}' (Win32 {Marshal.GetLastWin32Error()}).");
        return information;
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        internal FileAttributes FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileIdInformation
    {
        internal ulong VolumeSerialNumber;
        internal ulong FileIdLow;
        internal ulong FileIdHigh;
    }

    private enum FileInfoByHandleClass
    {
        FileIdInfo = 18,
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder? filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileIdInformation fileInformation,
        uint bufferSize);
}

internal sealed record PhysicalDirectoryIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh,
    string FinalPath)
{
    internal bool SameObject(PhysicalDirectoryIdentity other) =>
        VolumeSerialNumber == other.VolumeSerialNumber &&
        FileIdLow == other.FileIdLow &&
        FileIdHigh == other.FileIdHigh;
}
