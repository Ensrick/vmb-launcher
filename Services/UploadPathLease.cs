using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace VmbLauncher.Services;

/// <summary>
/// Pins every path consumed by ugc_tool from authorization through process exit.
/// File handles deny writes and replacement; directory handles deny additions,
/// removals, and renames in the staged tree. This protects against arbitrary
/// same-user processes, not only VMBLauncher instances that honor its semaphore.
/// </summary>
internal sealed class UploadPathLease : IDisposable
{
    internal static Action<string>? JournalDurableBeforeFreezeForTest;
    private const uint FileListDirectory = 0x0001;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private readonly List<IDisposable> _handles = new();
    private FileStream? _cfgLease;
    private DirectoryAclLease? _stagingRootLease;
    private readonly string _aclJournalPath;

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
        string aclJournalPath,
        IEnumerable<IDisposable> handles)
    {
        ToolPath = toolPath;
        CfgPath = cfgPath;
        CfgSha256 = cfgSha256;
        BundleFiles = bundleFiles;
        PreviewFile = previewFile;
        _cfgLease = cfgLease;
        _stagingRootLease = stagingRootLease;
        _aclJournalPath = aclJournalPath;
        _handles.AddRange(handles);
    }

    internal static UploadPathLease Capture(StagedUpload staged, string ugcToolPath)
    {
        MachineTransactionLease.RequireCurrent("Upload ACL capture");
        var retained = new List<IDisposable>();
        string? aclJournalPath = null;
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

            // Persist every original and exact frozen descriptor before the
            // first DACL mutation. A killed launcher cannot rely on Dispose;
            // the next machine-lease owner authenticates and restores this
            // journal before UploadStager touches the shared SDK directory.
            aclJournalPath = UploadAclJournal.WriteBeforeFreeze(
                stagingRoot,
                directoryLeases);
            JournalDurableBeforeFreezeForTest?.Invoke(aclJournalPath);

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
                aclJournalPath!,
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
            if (aclJournalPath != null)
                UploadAclJournal.DeleteIfRestored(aclJournalPath);
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
        UploadAclJournal.Delete(_aclJournalPath);
    }

    /// <summary>Test-only crash boundary: close kernel handles without rolling the DACL back.</summary>
    internal void AbandonForCrashFixture(
        int? deadOwnerPid = int.MaxValue,
        int restoreDirectoryCount = 0)
    {
        foreach (var handle in _handles.AsEnumerable().Reverse())
        {
            if (handle is DirectoryAclLease directory)
            {
                if (restoreDirectoryCount > 0)
                {
                    directory.Dispose();
                    restoreDirectoryCount--;
                }
                else
                    directory.AbandonForCrashFixture();
            }
            else
                handle.Dispose();
        }
        _handles.Clear();
        _cfgLease = null;
        _stagingRootLease = null;
        if (deadOwnerPid.HasValue)
            UploadAclJournal.MarkOwnerDeadForCrashFixture(_aclJournalPath, deadOwnerPid.Value);
    }

    /// <summary>
    /// Runs before UploadStager.Stage. Recovery is legal only while the caller
    /// owns or has authenticated into the machine transaction lease.
    /// </summary>
    internal static void RecoverStaleAclLease(string ugcToolPath, Action<string>? log = null)
    {
        MachineTransactionLease.RequireCurrent("Upload ACL recovery");
        var uploader = Normalize(Path.GetDirectoryName(Normalize(ugcToolPath))
            ?? throw new InvalidDataException("ugc_tool path has no parent directory."));
        UploadAclJournal.RecoverOrMigrateLegacy(uploader, log);
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
        private readonly byte[] _frozenDescriptor;
        private bool _frozen;
        internal string Path => _path;
        internal byte[] OriginalDescriptor => _originalDescriptor?.ToArray()
            ?? throw new InvalidOperationException("Directory ACL lease was already released.");
        internal byte[] FrozenDescriptor => _frozenDescriptor.ToArray();
        internal uint VolumeSerialNumber { get; }
        internal ulong FileId { get; }

        internal DirectoryAclLease(string path, SafeFileHandle handle)
        {
            _path = path;
            _handle = handle;
            if (!GetFileInformationByHandle(_handle, out var identity))
                throw new IOException(
                    $"Could not bind staged directory identity '{_path}' (Win32 {Marshal.GetLastWin32Error()}).");
            VolumeSerialNumber = identity.VolumeSerialNumber;
            FileId = ((ulong)identity.FileIndexHigh << 32) | identity.FileIndexLow;
            var security = FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(_path), AccessControlSections.Access);
            _originalDescriptor = security.GetSecurityDescriptorBinaryForm();
            security.AddAccessRule(CreateLauncherDenyRule());
            _frozenDescriptor = security.GetSecurityDescriptorBinaryForm();
        }

        internal void Freeze()
        {
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorBinaryForm(
                _frozenDescriptor, AccessControlSections.Access);
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(_path), security);
            _frozen = true;
        }

        public void Dispose()
        {
            try
            {
                if (_frozen && _originalDescriptor != null &&
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

        internal void AbandonForCrashFixture()
        {
            _originalDescriptor = null;
            _handle.Dispose();
        }
    }

    private static FileSystemAccessRule CreateLauncherDenyRule()
    {
        var sid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current Windows identity has no SID.");
        return new FileSystemAccessRule(
            sid,
            LauncherDeniedRights,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);
    }

    private const FileSystemRights LauncherDeniedRights =
        FileSystemRights.CreateFiles |
        FileSystemRights.CreateDirectories |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.Delete |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership |
        FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes;

    private static class UploadAclJournal
    {
        private const string FileName = ".vmblauncher-upload-acl-lease.json";
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
        };

        internal static string WriteBeforeFreeze(
            string stagingRoot,
            IReadOnlyList<DirectoryAclLease> leases)
        {
            var canonicalRoot = Normalize(stagingRoot);
            foreach (var lease in leases)
                EnsureNoReparsePoint(canonicalRoot, Normalize(lease.Path));
            var owner = MachineTransactionLease.CurrentIdentity;
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var journal = new AclJournal
            {
                Schema = 2,
                LeaseId = owner?.LeaseId ?? Guid.NewGuid().ToString("N"),
                OwnerPid = process.Id,
                OwnerStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                OwnerSessionId = process.SessionId,
                OwnerSid = CurrentUserSid(),
                StagingRoot = canonicalRoot,
                Entries = leases.Select(lease => new AclJournalEntry
                {
                    Path = Normalize(lease.Path),
                    VolumeSerialNumber = lease.VolumeSerialNumber,
                    FileId = lease.FileId,
                    OriginalDescriptor = Convert.ToBase64String(lease.OriginalDescriptor),
                    FrozenDescriptor = Convert.ToBase64String(lease.FrozenDescriptor),
                }).ToList(),
            };
            var path = GetPath(stagingRoot);
            WriteAtomic(path, JsonSerializer.Serialize(journal, JsonOptions));
            return path;
        }

        internal static void RecoverOrMigrateLegacy(string uploaderRoot, Action<string>? log)
        {
            var stagingRoot = Normalize(Path.Combine(uploaderRoot, UploadStager.StagingFolderName));
            var journalPath = GetPath(stagingRoot);
            if (File.Exists(journalPath))
            {
                RecoverJournal(stagingRoot, journalPath, log);
                return;
            }
            RecoverLegacyExactAce(stagingRoot, log);
        }

        private static void RecoverJournal(string expectedRoot, string journalPath, Action<string>? log)
        {
            AclJournal journal;
            try
            {
                journal = JsonSerializer.Deserialize<AclJournal>(
                    File.ReadAllText(journalPath), JsonOptions)
                    ?? throw new InvalidDataException("journal decoded to null");
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    $"Refusing upload: ACL recovery journal is unreadable ({ex.Message}).", ex);
            }
            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            if (journal.Schema != 2 ||
                !string.Equals(Normalize(journal.StagingRoot), expectedRoot, StringComparison.OrdinalIgnoreCase) ||
                journal.Entries.Count == 0 ||
                journal.OwnerSessionId != currentProcess.SessionId ||
                !string.Equals(journal.OwnerSid, CurrentUserSid(), StringComparison.Ordinal))
                throw new InvalidDataException("Refusing upload: ACL recovery journal identity is invalid.");
            var currentTransaction = MachineTransactionLease.CurrentIdentity;
            if (MachineTransactionLease.ProcessMatches(journal.OwnerPid, journal.OwnerStartUtcTicks) &&
                (currentTransaction == null || MachineTransactionLease.LeaseIdsEqual(currentTransaction.LeaseId, journal.LeaseId)))
                throw new IOException(
                    $"Refusing upload ACL recovery: exact journal lease {journal.LeaseId} is still active in owner PID {journal.OwnerPid}.");

            foreach (var entry in journal.Entries)
            {
                var path = Normalize(entry.Path);
                if (!IsRootOrChild(expectedRoot, path) || !Directory.Exists(path))
                    throw new InvalidDataException(
                        $"Refusing upload ACL recovery: journal path escapes or is missing: {path}");
                EnsureNoReparsePoint(expectedRoot, path);
                var identity = GetDirectoryIdentity(path);
                if (identity.VolumeSerialNumber != entry.VolumeSerialNumber ||
                    identity.FileId != entry.FileId)
                    throw new InvalidDataException(
                        $"Refusing upload ACL recovery: '{path}' no longer names the recorded volume/file identity.");
                var original = Convert.FromBase64String(entry.OriginalDescriptor);
                var frozen = Convert.FromBase64String(entry.FrozenDescriptor);
                var current = GetDescriptor(path);
                if (current.SequenceEqual(original)) continue;
                if (!current.SequenceEqual(frozen))
                    throw new InvalidDataException(
                        $"Refusing upload ACL recovery: '{path}' drifted from both the recorded original and exact launcher-owned descriptor.");
            }

            foreach (var entry in journal.Entries.AsEnumerable().Reverse())
            {
                var path = Normalize(entry.Path);
                var original = Convert.FromBase64String(entry.OriginalDescriptor);
                if (!GetDescriptor(path).SequenceEqual(original))
                    SetDescriptor(path, original);
            }
            File.Delete(journalPath);
            log?.Invoke(
                $"[upload-acl-recovery] restored {journal.Entries.Count} exact descriptor(s) from dead owner_pid={journal.OwnerPid}");
        }

        private static void RecoverLegacyExactAce(string stagingRoot, Action<string>? log)
        {
            if (!Directory.Exists(stagingRoot)) return;
            var candidates = EnumerateDirectories(stagingRoot);
            var sid = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Current Windows identity has no SID.");
            var plans = new List<(string Path, byte[] Current, byte[] Planned, uint Volume, ulong FileId, bool RemovesAce)>();

            // Phase 1 is read-only. Validate every candidate before changing
            // either DACL, so ambiguity in content can never partially repair
            // sample_item first.
            foreach (var path in candidates)
            {
                EnsureNoReparsePoint(stagingRoot, path);
                var info = new DirectoryInfo(path);
                var security = FileSystemAclExtensions.GetAccessControl(info, AccessControlSections.Access);
                var explicitCurrentUserDenies = security
                    .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>()
                    .Where(rule => rule.AccessControlType == AccessControlType.Deny &&
                                   sid.Equals(rule.IdentityReference))
                    .ToList();
                if (explicitCurrentUserDenies.Count > 1 ||
                    (explicitCurrentUserDenies.Count == 1 &&
                    (explicitCurrentUserDenies[0].FileSystemRights != LauncherDeniedRights ||
                     explicitCurrentUserDenies[0].InheritanceFlags != InheritanceFlags.None ||
                     explicitCurrentUserDenies[0].PropagationFlags != PropagationFlags.None)))
                    throw new InvalidDataException(
                        $"Refusing legacy ACL recovery: '{path}' contains a non-launcher or ambiguous explicit DENY.");

                var current = security.GetSecurityDescriptorBinaryForm();
                var removes = explicitCurrentUserDenies.Count == 1;
                if (removes)
                    security.RemoveAccessRuleSpecific(CreateLauncherDenyRule());
                var planned = security.GetSecurityDescriptorBinaryForm();
                var identity = GetDirectoryIdentity(path);
                plans.Add((path, current, planned, identity.VolumeSerialNumber, identity.FileId, removes));
            }
            var recovered = plans.Count(plan => plan.RemovesAce);
            if (recovered == 0) return;

            // The v0.5.9 lease wrote the same explicit launcher ACE onto a
            // parent and its child. Once that one ACE is removed, their Access
            // descriptors must be semantically equal. Otherwise an unrelated
            // ACE or inheritance drift is present and legacy recovery has no
            // authenticated original descriptor to choose from.
            var normalizedRoot = Normalize(stagingRoot);
            var normalizedContent = Normalize(Path.Combine(stagingRoot, "content"));
            var rootPlan = plans.SingleOrDefault(plan =>
                string.Equals(plan.Path, normalizedRoot, StringComparison.OrdinalIgnoreCase));
            var contentPlan = plans.SingleOrDefault(plan =>
                string.Equals(plan.Path, normalizedContent, StringComparison.OrdinalIgnoreCase));
            if (rootPlan.Path == null || contentPlan.Path == null ||
                !rootPlan.RemovesAce || !contentPlan.RemovesAce)
                throw new InvalidDataException(
                    "Refusing legacy ACL recovery: the exact v0.5.9 signature requires matching launcher DENYs on both sample_item and content.");

            var rootAndContent = new[] { rootPlan, contentPlan };
            var plannedSddl = rootAndContent.Select(plan =>
            {
                var security = new DirectorySecurity();
                security.SetSecurityDescriptorBinaryForm(plan.Planned, AccessControlSections.Access);
                return security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            }).Distinct(StringComparer.Ordinal).ToArray();
            if (plannedSddl.Length != 1)
                throw new InvalidDataException(
                    "Refusing legacy ACL recovery: sample_item/content do not have the same parent-child Access semantics after removing the exact launcher ACE.");

            // The old freeze was recursive. An exact-looking DENY on a deeper
            // directory is attributable to v0.5.9 only when removing it yields
            // the same Access semantics as that directory's immediate parent.
            // Validate the complete tree before creating a journal or writing
            // any descriptor; an independently secured child is ambiguous.
            var byPath = plans.ToDictionary(plan => plan.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var plan in plans.Where(plan => plan.RemovesAce &&
                         !string.Equals(plan.Path, normalizedRoot, StringComparison.OrdinalIgnoreCase)))
            {
                var parentPath = Normalize(Path.GetDirectoryName(plan.Path)
                    ?? throw new InvalidDataException(
                        $"Refusing legacy ACL recovery: '{plan.Path}' has no parent."));
                if (!byPath.TryGetValue(parentPath, out var parentPlan))
                    throw new InvalidDataException(
                        $"Refusing legacy ACL recovery: '{plan.Path}' has no validated parent candidate.");
                var childSddl = AccessSddl(plan.Planned);
                var parentSddl = AccessSddl(parentPlan.Planned);
                if (!string.Equals(childSddl, parentSddl, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Refusing legacy ACL recovery: '{plan.Path}' has independent Access semantics after removing the exact launcher ACE.");
            }

            // Convert the legacy case into the ordinary crash-safe journal and
            // let the same validate-all-then-restore path apply it. If this
            // process dies mid-repair, the journal remains idempotently usable.
            var journalPath = GetPath(stagingRoot);
            var legacyJournal = new AclJournal
            {
                Schema = 2,
                LeaseId = "legacy-v0.5.9-recovery",
                OwnerPid = int.MaxValue,
                OwnerStartUtcTicks = 1,
                OwnerSessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId,
                OwnerSid = CurrentUserSid(),
                StagingRoot = Normalize(stagingRoot),
                Entries = plans.Select(plan => new AclJournalEntry
                {
                    Path = plan.Path,
                    VolumeSerialNumber = plan.Volume,
                    FileId = plan.FileId,
                    OriginalDescriptor = Convert.ToBase64String(plan.Planned),
                    FrozenDescriptor = Convert.ToBase64String(plan.Current),
                }).ToList(),
            };
            WriteAtomic(journalPath, JsonSerializer.Serialize(legacyJournal, JsonOptions));
            RecoverJournal(stagingRoot, journalPath, log);
            log?.Invoke(
                $"[upload-acl-recovery] removed the exact v0.5.9 stranded launcher ACE from {recovered} SDK staging director{(recovered == 1 ? "y" : "ies")}");
        }

        internal static void DeleteIfRestored(string path)
        {
            if (!File.Exists(path)) return;
            AclJournal? journal = null;
            try { journal = JsonSerializer.Deserialize<AclJournal>(File.ReadAllText(path), JsonOptions); }
            catch { }
            if (journal != null && journal.Entries.All(entry =>
                    Directory.Exists(entry.Path) &&
                    GetDescriptor(entry.Path).SequenceEqual(Convert.FromBase64String(entry.OriginalDescriptor))))
                File.Delete(path);
        }

        internal static void Delete(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        internal static void MarkOwnerDeadForCrashFixture(string path, int deadOwnerPid)
        {
            var journal = JsonSerializer.Deserialize<AclJournal>(
                File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("ACL fixture journal decoded to null.");
            journal.OwnerPid = deadOwnerPid;
            journal.OwnerStartUtcTicks = 1;
            WriteAtomic(path, JsonSerializer.Serialize(journal, JsonOptions));
        }

        private static string GetPath(string stagingRoot) =>
            Path.Combine(Path.GetDirectoryName(Normalize(stagingRoot))!, FileName);

        private static string CurrentUserSid() => WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current Windows identity has no SID.");

        private static bool IsRootOrChild(string root, string candidate)
        {
            if (string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            return candidate.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureNoReparsePoint(string root, string candidate)
        {
            var rootFull = Normalize(root);
            var current = rootFull;
            CheckNotReparse(current);
            if (!string.Equals(rootFull, candidate, StringComparison.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(rootFull, candidate);
                foreach (var component in relative.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, component);
                    CheckNotReparse(current);
                }
            }
        }

        private static void CheckNotReparse(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    $"Refusing upload ACL recovery through reparse point '{path}'.");
        }

        private static (uint VolumeSerialNumber, ulong FileId) GetDirectoryIdentity(string path)
        {
            using var handle = CreateFileW(
                path,
                FileListDirectory | ReadControl,
                FileShare.Read,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var identity))
                throw new IOException(
                    $"Could not validate staged directory identity '{path}' (Win32 {Marshal.GetLastWin32Error()}).");
            return (
                identity.VolumeSerialNumber,
                ((ulong)identity.FileIndexHigh << 32) | identity.FileIndexLow);
        }

        private static byte[] GetDescriptor(string path) =>
            FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(path), AccessControlSections.Access)
                .GetSecurityDescriptorBinaryForm();

        private static string AccessSddl(byte[] descriptor)
        {
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
            return security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        }

        private static void SetDescriptor(string path, byte[] descriptor)
        {
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorBinaryForm(
                descriptor, AccessControlSections.Access);
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
        }

        private static void WriteAtomic(string path, string text)
        {
            if (File.Exists(path) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    $"Refusing to replace reparse-point ACL journal '{path}'.");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                using (var stream = new FileStream(
                    temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
    }

    private sealed class AclJournal
    {
        public int Schema { get; set; }
        public string LeaseId { get; set; } = "";
        public int OwnerPid { get; set; }
        public long OwnerStartUtcTicks { get; set; }
        public int OwnerSessionId { get; set; }
        public string OwnerSid { get; set; } = "";
        public string StagingRoot { get; set; } = "";
        public List<AclJournalEntry> Entries { get; set; } = new();
    }

    private sealed class AclJournalEntry
    {
        public string Path { get; set; } = "";
        public uint VolumeSerialNumber { get; set; }
        public ulong FileId { get; set; }
        public string OriginalDescriptor { get; set; } = "";
        public string FrozenDescriptor { get; set; } = "";
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

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation fileInformation);
}
