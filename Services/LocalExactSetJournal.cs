using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace VmbLauncher.Services;

/// <summary>
/// Durable, duplicate-intolerant receipt-deploy journal I/O. A single
/// canonical file contains two bounded checksummed slots. Updating the
/// inactive slot never destroys the last proven state, eliminating all
/// replacement-temp ownership gaps while keeping journal growth constant.
/// </summary>
internal static partial class LocalExactSetDeployment
{
    private const int JournalSlotBytes = 8 * 1024 * 1024;
    private const int JournalSlotHeaderBytes = 48;
    private const int MaximumJournalPayloadBytes = JournalSlotBytes - JournalSlotHeaderBytes;
    private const long JournalFileBytes = 2L * JournalSlotBytes;

    private static void WriteJournal(
        string path,
        DeployJournal journal,
        bool replace,
        ExactDirectoryLease parentLease,
        ExactJournalLease? journalLease = null)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(journal.OperationId, "^[0-9a-f]{32}$"))
            throw new InvalidDataException("Receipt-deploy journal operation identity is invalid.");
        if (!replace)
        {
            WriteInitialJournal(path, journal, parentLease);
            return;
        }
        if (journal.JournalIdentity == null)
            throw new InvalidDataException("Receipt-deploy journal lacks its durable physical identity.");
        var bytes = SerializeJournal(journal);
        if (journalLease != null)
        {
            journalLease.Write(path, journal, bytes, parentLease);
            return;
        }
        WriteReplacementJournal(path, journal, bytes, parentLease);
    }

    private static void WriteInitialJournal(
        string path,
        DeployJournal journal,
        ExactDirectoryLease parentLease)
    {
        SafeFileHandle? rawHandle = null;
        FileStream? stream = null;
        try
        {
            rawHandle = CreateNewPinnedFile(parentLease, path);
            stream = new FileStream(rawHandle, FileAccess.ReadWrite, 4096, isAsync: false);
            rawHandle = null;
            Checkpoint("journal-initial-created-unidentified");
            var journalId = ImmutableBundleSourceLease.GetFileIdInformation(stream.SafeFileHandle, path);
            journal.JournalIdentity = DeployDirectoryIdentity.From(new PhysicalDirectoryIdentity(
                journalId.VolumeSerialNumber,
                journalId.FileIdLow,
                journalId.FileIdHigh,
                Normalize(path)));
            Checkpoint("journal-initial-created");
            var bytes = SerializeJournal(journal);
            stream.SetLength(JournalFileBytes);
            stream.Flush(flushToDisk: true);
            Checkpoint("journal-initial-sized");
            WriteJournalSlot(
                stream,
                slot: 0,
                sequence: 1,
                bytes,
                "journal-initial-payload-partial",
                "journal-initial-payload-flushed",
                "journal-initial-header-partial",
                "journal-initial-durable");
            RequireSameJournalVersion(
                ReadJournal(stream, path),
                journal,
                "initial durable journal postcondition");
        }
        catch (Exception ex) when (BypassesAutomaticRecovery(ex))
        {
            throw;
        }
        catch (Exception failure)
        {
            Exception? cleanup = null;
            try
            {
                if (stream != null) MarkDeleteOnClose(stream.SafeFileHandle);
                else if (rawHandle is { IsInvalid: false }) MarkDeleteOnClose(rawHandle);
            }
            catch (Exception ex) { cleanup = ex; }
            stream?.Dispose();
            stream = null;
            rawHandle?.Dispose();
            rawHandle = null;
            if (cleanup != null)
                throw new AggregateException(
                    "Initial journal write failed and its exact live handle could not remove the uncommitted object.",
                    failure,
                    cleanup);
            throw;
        }
        finally
        {
            stream?.Dispose();
            rawHandle?.Dispose();
        }
    }

    private static byte[] SerializeJournal(DeployJournal journal)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JournalJson);
        if (bytes.Length <= 0 || bytes.Length > MaximumJournalPayloadBytes)
            throw new InvalidDataException("Receipt-deploy journal length is outside its safety bound.");
        return bytes;
    }

    private static void WriteReplacementJournal(
        string path,
        DeployJournal journal,
        byte[] bytes,
        ExactDirectoryLease parentLease)
    {
        parentLease.RequireCurrentPath("journal update parent");
        if (!string.Equals(
                Normalize(Path.GetDirectoryName(path)
                    ?? throw new InvalidDataException("Journal update path has no parent.")),
                parentLease.CurrentPath,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Journal update path escapes its pinned parent.");
        using var stream = OpenPinnedJournalForUpdate(path);
        WriteReplacementJournal(stream, path, journal, bytes);
    }

    private static void WriteReplacementJournal(
        FileStream stream,
        string path,
        DeployJournal journal)
    {
        WriteReplacementJournal(stream, path, journal, SerializeJournal(journal));
    }

    private static void WriteReplacementJournal(
        FileStream stream,
        string path,
        DeployJournal journal,
        byte[] bytes)
    {
        var current = ReadJournalVersion(stream, path);
        RequireSameJournalTransaction(current.Journal, journal);
        if (current.Sequence == long.MaxValue)
            throw new InvalidDataException("Receipt-deploy journal sequence is exhausted.");
        var next = checked(current.Sequence + 1);
        var slot = checked((int)((next - 1) % 2));
        WriteJournalSlot(
            stream,
            slot,
            next,
            bytes,
            "journal-slot-payload-partial",
            "journal-slot-payload-flushed",
            "journal-slot-header-partial",
            "journal-slot-durable");
        RequireSameJournalVersion(
            ReadJournal(stream, path),
            journal,
            "durable journal postcondition");
    }

    private static void WriteJournalSlot(
        FileStream stream,
        int slot,
        long sequence,
        byte[] bytes,
        string payloadPartialCheckpoint,
        string payloadFlushedCheckpoint,
        string headerPartialCheckpoint,
        string durableCheckpoint)
    {
        if (slot is < 0 or > 1 || sequence <= 0 ||
            bytes.Length <= 0 || bytes.Length > MaximumJournalPayloadBytes)
            throw new InvalidDataException("Receipt-deploy journal slot write is noncanonical.");
        var slotOffset = checked((long)slot * JournalSlotBytes);
        var zeroHeader = new byte[JournalSlotHeaderBytes];
        stream.Position = slotOffset;
        stream.Write(zeroHeader);
        stream.Flush(flushToDisk: true);
        Checkpoint("journal-slot-invalidated");

        stream.Position = slotOffset + JournalSlotHeaderBytes;
        var offset = 0;
        var notified = false;
        while (offset < bytes.Length)
        {
            var count = Math.Min(4096, bytes.Length - offset);
            stream.Write(bytes, offset, count);
            offset += count;
            if (!notified)
            {
                notified = true;
                Checkpoint(payloadPartialCheckpoint);
            }
        }
        stream.Flush(flushToDisk: true);
        Checkpoint(payloadFlushedCheckpoint);

        var header = new byte[JournalSlotHeaderBytes];
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0, sizeof(long)), sequence);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, sizeof(int)), bytes.Length);
        SHA256.HashData(bytes).CopyTo(header, 16);
        stream.Position = slotOffset;
        stream.Write(header, 0, sizeof(long));
        Checkpoint(headerPartialCheckpoint);
        stream.Write(header, sizeof(long), header.Length - sizeof(long));
        stream.Flush(flushToDisk: true);
        Checkpoint(durableCheckpoint);
    }

    private static DeployJournal ReadJournal(string path)
    {
        using var stream = OpenPinnedRegularFile(path);
        return ReadJournal(stream, path);
    }

    private static DeployJournal ReadJournal(FileStream stream, string expectedPath)
    {
        return ReadJournalVersion(stream, expectedPath).Journal;
    }

    private static JournalVersion ReadJournalVersion(FileStream stream, string expectedPath)
    {
        if (stream.Length != JournalFileBytes)
            throw new InvalidDataException("Receipt-deploy journal length is outside its exact bounded format.");
        var finalPath = Normalize(ImmutableBundleSourceLease.GetFinalPath(stream.SafeFileHandle));
        if (!string.Equals(finalPath, Normalize(expectedPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Receipt-deploy journal resolves through a reparse or alias.");
        var information = ImmutableBundleSourceLease.GetHandleInformation(
            stream.SafeFileHandle,
            expectedPath);
        if (information.NumberOfLinks != 1 ||
            (information.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("Receipt-deploy journal is not an unaliased regular file.");
        var versions = new List<JournalVersion>(2);
        for (var slot = 0; slot < 2; slot++)
        {
            var version = TryReadJournalSlot(stream, slot);
            if (version != null) versions.Add(version);
        }
        if (versions.Count == 0)
            throw new InvalidDataException("Receipt-deploy journal has no durable checksummed state.");
        if (versions.Count == 2)
        {
            RequireSameJournalTransaction(versions[0].Journal, versions[1].Journal);
            if (versions[0].Sequence == versions[1].Sequence)
                throw new InvalidDataException("Receipt-deploy journal has an ambiguous duplicate sequence.");
        }
        var selected = versions.OrderByDescending(item => item.Sequence).First();
        var recordedIdentity = selected.Journal.JournalIdentity
            ?? throw new InvalidDataException("Receipt-deploy journal lacks its durable physical identity.");
        var actualFileId = ImmutableBundleSourceLease.GetFileIdInformation(
            stream.SafeFileHandle,
            expectedPath);
        var actualIdentity = new PhysicalDirectoryIdentity(
            actualFileId.VolumeSerialNumber,
            actualFileId.FileIdLow,
            actualFileId.FileIdHigh,
            Normalize(expectedPath));
        RequireIdentity(
            actualIdentity,
            recordedIdentity.ToPhysical(expectedPath),
            "receipt-deploy journal");
        return selected;
    }

    private static JournalVersion? TryReadJournalSlot(FileStream stream, int slot)
    {
        var slotOffset = checked((long)slot * JournalSlotBytes);
        var header = new byte[JournalSlotHeaderBytes];
        stream.Position = slotOffset;
        stream.ReadExactly(header);
        var sequence = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0, sizeof(long)));
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, sizeof(int)));
        if (sequence == 0 && length == 0 && header.All(value => value == 0)) return null;
        if (sequence <= 0 || length <= 0 || length > MaximumJournalPayloadBytes) return null;
        var payload = new byte[length];
        stream.Position = slotOffset + JournalSlotHeaderBytes;
        stream.ReadExactly(payload);
        var expectedHash = header.AsSpan(16, SHA256.HashSizeInBytes);
        var actualHash = SHA256.HashData(payload);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash)) return null;
        RequireNoDuplicateJsonProperties(payload);
        var journal = JsonSerializer.Deserialize<DeployJournal>(payload, JournalJson)
            ?? throw new InvalidDataException("Receipt-deploy journal slot is empty.");
        return new JournalVersion(sequence, journal);
    }

    private static FileStream OpenPinnedJournalForUpdate(string path)
    {
        var normalized = Normalize(path);
        var handle = CreateFileForDeleteW(
            normalized,
            GenericRead | GenericWrite | DeleteAccess | FileReadAttributes,
            FileShare.Read,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagWriteThrough,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException(
                $"Cannot pin receipt-deploy journal for update: {normalized} " +
                $"(Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
        }
        try
        {
            var information = ImmutableBundleSourceLease.GetHandleInformation(handle, normalized);
            if ((information.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                information.NumberOfLinks != 1 ||
                !string.Equals(
                    Normalize(ImmutableBundleSourceLease.GetFinalPath(handle)),
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Receipt-deploy journal update target is not an unaliased regular file.");
            return new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Keeps one exact journal identity continuously pinned against external
    /// write, rename, and deletion while a transaction relies on its durable
    /// slots. All slot rewrites and final retirement use this same handle.
    /// </summary>
    private sealed class ExactJournalLease : IDisposable
    {
        private FileStream? _stream;

        internal string CurrentPath { get; private set; }
        internal SafeFileHandle Handle => _stream is { SafeFileHandle.IsClosed: false }
            ? _stream.SafeFileHandle
            : throw new ObjectDisposedException(nameof(ExactJournalLease));

        private FileStream Stream => _stream
            ?? throw new ObjectDisposedException(nameof(ExactJournalLease));

        private ExactJournalLease(string path, FileStream stream)
        {
            CurrentPath = Normalize(path);
            _stream = stream;
        }

        internal static ExactJournalLease Open(
            string path,
            DeployJournal expected,
            string context)
        {
            var normalized = Normalize(path);
            FileStream? stream = null;
            try
            {
                stream = OpenPinnedJournalForUpdate(normalized);
                var lease = Adopt(normalized, stream, expected, context);
                stream = null;
                return lease;
            }
            finally
            {
                stream?.Dispose();
            }
        }

        internal static ExactJournalLease Adopt(
            string path,
            FileStream stream,
            DeployJournal expected,
            string context)
        {
            var lease = new ExactJournalLease(path, stream);
            try
            {
                lease.RequireVersion(expected, context);
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        internal DeployJournal Read(string context)
        {
            var journal = ReadJournal(Stream, CurrentPath);
            if (journal.JournalIdentity == null)
                throw new InvalidDataException($"{context} lacks its durable physical identity.");
            return journal;
        }

        internal void RequireVersion(DeployJournal expected, string context) =>
            RequireSameJournalVersion(Read(context), expected, context);

        internal void Write(
            string expectedPath,
            DeployJournal journal,
            byte[] bytes,
            ExactDirectoryLease parentLease)
        {
            parentLease.RequireCurrentPath("journal lease update parent");
            if (!string.Equals(
                    CurrentPath,
                    Normalize(expectedPath),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Journal lease update path differs from its pinned current path.");
            if (!string.Equals(
                    Normalize(Path.GetDirectoryName(CurrentPath)
                        ?? throw new InvalidDataException("Journal lease path has no parent.")),
                    parentLease.CurrentPath,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Journal lease path escapes its pinned parent.");
            WriteReplacementJournal(Stream, CurrentPath, journal, bytes);
        }

        internal void RenameTo(
            string destination,
            ExactDirectoryLease parentLease,
            DeployJournal expected,
            string context)
        {
            var normalized = Normalize(destination);
            parentLease.RequireCurrentPath(context + " parent");
            if (!string.Equals(
                    Normalize(Path.GetDirectoryName(normalized)
                        ?? throw new InvalidDataException("Journal lease rename has no parent.")),
                    parentLease.CurrentPath,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Journal lease rename escapes its pinned parent.");
            RenamePinnedObject(
                Handle,
                normalized,
                replaceIfExists: false,
                parentLease.Handle);
            CurrentPath = normalized;
            RequireVersion(expected, context);
        }

        internal void MarkDeleteOnClose() =>
            LocalExactSetDeployment.MarkDeleteOnClose(Handle);

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    private sealed record JournalVersion(long Sequence, DeployJournal Journal);

    private static void DeleteJournal(
        string path,
        DeployJournal expected,
        Action? finalNamespaceCheck = null)
    {
        using var stream = OpenPinnedRegularFile(path);
        RequireSameJournalVersion(ReadJournal(stream, path), expected, "journal delete authority");
        Checkpoint("journal-delete-pinned");
        finalNamespaceCheck?.Invoke();
        MarkDeleteOnClose(stream.SafeFileHandle);
        stream.Dispose();
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("Receipt-deploy journal was replaced during deletion.");
    }

    private static bool IsExactDurableJournalVersion(
        string path,
        DeployJournal expected)
    {
        try
        {
            if (!File.Exists(path) || Directory.Exists(path)) return false;
            using var stream = OpenPinnedRegularFile(path);
            RequireSameJournalVersion(
                ReadJournal(stream, path),
                expected,
                "durable journal state proof");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsExactDurableJournalVersion(
        ExactJournalLease journalLease,
        DeployJournal expected)
    {
        try
        {
            journalLease.RequireVersion(expected, "durable leased journal state proof");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RefuseJournalTemps(string parent)
    {
        var paths = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     parent,
                     ".vmblauncher-receipt-deploy-*.journal.json.tmp-*",
                     SearchOption.TopDirectoryOnly))
        {
            if (paths.Count >= MaximumManagedFiles)
                throw new InvalidDataException(
                    "Workshop receipt-deploy journal-temp inventory exceeds its safety bound.");
            paths.Add(path);
        }
        if (paths.Count != 0)
            throw new InvalidDataException(
                "Workshop contains an unowned receipt-deploy journal temporary object; preserving it for explicit investigation.");
    }

    private static void RequireSameJournalTransaction(DeployJournal left, DeployJournal right)
    {
        var leftBytes = JsonSerializer.SerializeToUtf8Bytes(JournalTransactionIdentity.From(left), JournalJson);
        var rightBytes = JsonSerializer.SerializeToUtf8Bytes(JournalTransactionIdentity.From(right), JournalJson);
        if (!CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes))
            throw new InvalidDataException("Receipt-deploy journal temp belongs to another transaction.");
    }

    private static void RequireSameJournalVersion(
        DeployJournal actual,
        DeployJournal expected,
        string context)
    {
        var actualBytes = JsonSerializer.SerializeToUtf8Bytes(actual, JournalJson);
        var expectedBytes = JsonSerializer.SerializeToUtf8Bytes(expected, JournalJson);
        if (!CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes))
            throw new InvalidDataException($"{context} differs from its exact durable state.");
    }

    private sealed record JournalTransactionIdentity(
        int Schema,
        string OperationId,
        string Mod,
        string PublishedId,
        string SourceCommit,
        string AuthorityFingerprint,
        string OutputFingerprint,
        string TargetDirectory,
        string ParentDirectory,
        string StageDirectory,
        string BackupDirectory,
        string RetirementJournalPath,
        int OwnerPid,
        long OwnerStartUtcTicks,
        int OwnerSessionId,
        string LeaseId,
        string OwnerSid,
        string? OwnerMod,
        string? ProjectRoot,
        string Action,
        DeployDirectoryIdentity? JournalIdentity,
        DeployDirectoryIdentity ParentIdentity,
        DeployDirectoryIdentity? PriorIdentity,
        IReadOnlyList<DeployJournalFile> PriorFiles,
        IReadOnlyList<DeployJournalFile> ExpectedFiles)
    {
        internal static JournalTransactionIdentity From(DeployJournal value) => new(
            value.Schema,
            value.OperationId,
            value.Mod,
            value.PublishedId,
            value.SourceCommit,
            value.AuthorityFingerprint,
            value.OutputFingerprint,
            value.TargetDirectory,
            value.ParentDirectory,
            value.StageDirectory,
            value.BackupDirectory,
            value.RetirementJournalPath,
            value.OwnerPid,
            value.OwnerStartUtcTicks,
            value.OwnerSessionId,
            value.LeaseId,
            value.OwnerSid,
            value.OwnerMod,
            value.ProjectRoot,
            value.Action,
            value.JournalIdentity,
            value.ParentIdentity,
            value.PriorIdentity,
            value.PriorFiles,
            value.ExpectedFiles);
    }

    private static void RequireNoDuplicateJsonProperties(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Receipt-deploy journal root must be an object.");
        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException(
                            $"Receipt-deploy journal has duplicate property '{property.Name}'.");
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) Visit(item);
            }
        }
        Visit(document.RootElement);
    }
}
