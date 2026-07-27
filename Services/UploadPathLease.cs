using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace VmbLauncher.Services;

/// <summary>
/// Pins every path consumed by ugc_tool from authorization through process exit.
/// File handles deny writes and replacement; directory handles deny additions,
/// removals, and renames in the staged tree. This protects against arbitrary
/// same-user processes, not only VMBLauncher instances that honor its semaphore.
/// </summary>
internal sealed class UploadPathLease : IDisposable
{
    private const uint FileListDirectory = 0x0001;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    private readonly List<IDisposable> _handles = new();
    private FileStream? _cfgLease;
    private DirectoryAclLease? _stagingRootLease;

    internal string ToolPath { get; }
    internal string CfgPath { get; }
    internal string CfgSha256 { get; }
    internal IReadOnlyList<PublicationBundleFile> BundleFiles { get; }
    internal PublicationPreviewFile PreviewFile { get; }

    private UploadPathLease(
        string toolPath,
        string cfgPath,
        string cfgSha256,
        IReadOnlyList<PublicationBundleFile> bundleFiles,
        PublicationPreviewFile previewFile,
        FileStream cfgLease,
        DirectoryAclLease stagingRootLease,
        IEnumerable<IDisposable> handles)
    {
        ToolPath = toolPath;
        CfgPath = cfgPath;
        CfgSha256 = cfgSha256;
        BundleFiles = bundleFiles;
        PreviewFile = previewFile;
        _cfgLease = cfgLease;
        _stagingRootLease = stagingRootLease;
        _handles.AddRange(handles);
    }

    internal static UploadPathLease Capture(StagedUpload staged, string ugcToolPath)
    {
        var retained = new List<IDisposable>();
        try
        {
            var stagingRoot = Normalize(staged.StagingDir);
            var contentRoot = Normalize(Path.Combine(stagingRoot, "content"));
            var cfgPath = Normalize(staged.CfgPath);
            var toolPath = Normalize(ugcToolPath);
            var toolDirectory = Normalize(Path.GetDirectoryName(toolPath)
                ?? throw new InvalidDataException("ugc_tool path has no parent directory."));
            var previewPath = Normalize(Path.Combine(stagingRoot, staged.PreviewName));

            RequireChild(stagingRoot, contentRoot, "content directory");
            RequireChild(stagingRoot, cfgPath, "staged cfg");
            RequireChild(stagingRoot, previewPath, "staged preview");

            var initialDirectories = EnumerateDirectories(stagingRoot);
            var directoryLeases = new List<DirectoryAclLease>();
            foreach (var directory in initialDirectories)
            {
                var directoryLease = OpenDirectoryLease(directory);
                directoryLeases.Add(directoryLease);
                retained.Add(directoryLease);
            }

            // Freeze the known directory set before testing any file for
            // presence. Otherwise a missing preview (or a new content path)
            // could be created after the absence check but before Freeze().
            // A directory inserted while the known parents are being frozen
            // is detected by the second census and fails the capture.
            foreach (var directoryLease in directoryLeases.AsEnumerable().Reverse())
                directoryLease.Freeze();

            var finalDirectories = EnumerateDirectories(stagingRoot);
            if (!initialDirectories.SequenceEqual(finalDirectories, StringComparer.OrdinalIgnoreCase))
                throw new IOException("SDK staging directories changed while the upload snapshot was being pinned.");

            var cfg = OpenReadLease(cfgPath);
            retained.Add(cfg);
            var cfgHash = HashStream(cfg);

            var initialFiles = EnumerateFiles(contentRoot);
            var bundleStreams = new List<(string Path, FileStream Stream)>();
            foreach (var path in initialFiles)
            {
                var stream = OpenReadLease(path);
                retained.Add(stream);
                bundleStreams.Add((path, stream));
            }
            var finalFiles = EnumerateFiles(contentRoot);
            if (!initialFiles.SequenceEqual(finalFiles, StringComparer.OrdinalIgnoreCase))
                throw new IOException("SDK-staged content changed while the upload snapshot was being pinned.");
            if (bundleStreams.Count == 0)
                throw new InvalidDataException("SDK-staged content contains no files.");

            var bundles = bundleStreams.Select(pair => new PublicationBundleFile
            {
                Path = Path.GetRelativePath(contentRoot, pair.Path).Replace('\\', '/'),
                Length = pair.Stream.Length,
                Sha256 = HashStream(pair.Stream),
            }).ToList();

            PublicationPreviewFile preview;
            if (File.Exists(previewPath))
            {
                var stream = OpenReadLease(previewPath);
                retained.Add(stream);
                preview = new PublicationPreviewFile
                {
                    Path = staged.PreviewName.Replace('\\', '/'),
                    Present = true,
                    Length = stream.Length,
                    Sha256 = HashStream(stream),
                };
            }
            else
            {
                preview = new PublicationPreviewFile
                {
                    Path = staged.PreviewName.Replace('\\', '/'),
                    Present = false,
                    Length = 0,
                    Sha256 = "",
                };
            }

            // Keep the executable itself pinned. CreateProcess still receives a
            // path, but no other process can replace or modify those bytes while
            // this read lease is alive. The parent directory handle separately
            // denies a rename-and-recreate attack against the entire uploader
            // path without changing the directory ACL ugc_tool may rely on.
            retained.Add(OpenDirectoryLease(toolDirectory));
            retained.Add(OpenReadLease(toolPath));

            return new UploadPathLease(
                toolPath,
                cfgPath,
                cfgHash,
                bundles,
                preview,
                cfg,
                directoryLeases.Single(
                    item => string.Equals(
                        item.Path, stagingRoot, StringComparison.OrdinalIgnoreCase)),
                retained);
        }
        catch (Exception captureFailure)
        {
            List<Exception>? releaseFailures = null;
            foreach (var handle in retained.AsEnumerable().Reverse())
            {
                try
                {
                    handle.Dispose();
                }
                catch (Exception ex)
                {
                    releaseFailures ??= new List<Exception>();
                    releaseFailures.Add(ex);
                }
            }
            if (releaseFailures != null)
                throw new AggregateException(
                    "Upload snapshot capture failed and one or more leases could not be released.",
                    new[] { captureFailure }.Concat(releaseFailures));
            throw;
        }
    }

    /// <summary>
    /// First-item creation is the sole operation for which Steam's ugc_tool
    /// must rewrite item.cfg (it replaces published_id=0 with the allocated
    /// ID). Keep every other input pinned and release only this exact cfg
    /// handle and its parent-directory lease immediately before process
    /// creation. Releasing the parent is required because some ugc_tool builds
    /// save by replace rather than in-place. Preview and all content files,
    /// content directories, and the executable remain pinned. The caller must
    /// validate the complete post-process cfg and perform the guarded source
    /// write-back before disposing this lease.
    /// </summary>
    internal void ReleaseCfgForBootstrapWrite()
    {
        var cfg = _cfgLease
            ?? throw new InvalidOperationException(
                "Bootstrap cfg write boundary was already opened.");
        if (!_handles.Remove(cfg))
            throw new InvalidOperationException(
                "Bootstrap cfg lease is not part of the pinned upload snapshot.");
        cfg.Dispose();
        _cfgLease = null;

        var root = _stagingRootLease
            ?? throw new InvalidOperationException(
                "Bootstrap staging-root boundary was already opened.");
        if (!_handles.Remove(root))
            throw new InvalidOperationException(
                "Bootstrap staging-root lease is not part of the pinned upload snapshot.");
        root.Dispose();
        _stagingRootLease = null;
    }

    public void Dispose()
    {
        List<Exception>? failures = null;
        foreach (var handle in _handles.AsEnumerable().Reverse())
        {
            try
            {
                handle.Dispose();
            }
            catch (Exception ex)
            {
                failures ??= new List<Exception>();
                failures.Add(ex);
            }
        }
        _handles.Clear();
        _cfgLease = null;
        _stagingRootLease = null;
        if (failures != null)
            throw new AggregateException("One or more upload snapshot leases could not be released.", failures);
    }

    private static string[] EnumerateDirectories(string root) =>
        new[] { root }
            .Concat(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            .Select(Normalize)
            .OrderBy(path => path.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] EnumerateFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(Normalize)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static FileStream OpenReadLease(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    private static DirectoryAclLease OpenDirectoryLease(string path)
    {
        var handle = CreateFileW(
            path,
            FileListDirectory | ReadControl | WriteDac,
            FileShare.Read,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Could not pin staged directory '{path}' (Win32 {error}).");
        }
        return new DirectoryAclLease(path, handle);
    }

    private static string HashStream(FileStream stream)
    {
        stream.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        return hash;
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void RequireChild(string root, string path, string label)
    {
        var prefix = root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label} escapes the SDK staging directory.");
    }

    private sealed class DirectoryAclLease : IDisposable
    {
        private const uint DaclSecurityInformation = 0x00000004;
        private readonly string _path;
        private readonly SafeFileHandle _handle;
        private byte[]? _originalDescriptor;
        internal string Path => _path;

        internal DirectoryAclLease(string path, SafeFileHandle handle)
        {
            _path = path;
            _handle = handle;
        }

        internal void Freeze()
        {
            var info = new DirectoryInfo(_path);
            var security = FileSystemAclExtensions.GetAccessControl(
                info, AccessControlSections.Access);
            _originalDescriptor = security.GetSecurityDescriptorBinaryForm();
            var sid = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Current Windows identity has no SID.");
            var denied = FileSystemRights.CreateFiles |
                FileSystemRights.CreateDirectories |
                FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.Delete |
                FileSystemRights.ChangePermissions |
                FileSystemRights.TakeOwnership |
                FileSystemRights.WriteAttributes |
                FileSystemRights.WriteExtendedAttributes;
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                denied,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Deny));
            FileSystemAclExtensions.SetAccessControl(info, security);
        }

        public void Dispose()
        {
            try
            {
                if (_originalDescriptor != null &&
                    !SetKernelObjectSecurity(
                        _handle, DaclSecurityInformation, _originalDescriptor))
                {
                    throw new IOException(
                        $"Could not restore staged directory ACL '{_path}' (Win32 {Marshal.GetLastWin32Error()}).");
                }
            }
            finally
            {
                _handle.Dispose();
            }
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(
        SafeFileHandle handle,
        uint securityInformation,
        byte[] securityDescriptor);
}
