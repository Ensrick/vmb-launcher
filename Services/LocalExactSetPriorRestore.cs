using System.IO;
using System.Security.Cryptography;

namespace VmbLauncher.Services;

/// <summary>
/// Reconstructs the exact recorded prior set when an unrecorded leaf appears
/// after its proof handles close. The mixed container is preserved by physical
/// identity; only journal-recorded old leaves move into a fresh recorded
/// restore directory. Unknown content is never enumerated for deletion.
/// </summary>
internal static partial class LocalExactSetDeployment
{
    private static void ReconstructPriorAndRequireManualReview(
        TransactionPaths initialPaths,
        DeployJournal journal,
        string modName,
        ExactDirectorySnapshot prior,
        ExactDirectoryLease parentLease,
        ExactJournalLease? journalLease,
        Exception membershipFailure)
    {
        var paths = initialPaths;
        ExactDirectoryLease? restoreLease = null;
        ExactDirectoryLease? mixedLease = null;
        ExactDirectorySnapshotLease? restoreProof = null;
        try
        {
            restoreLease = OpenOrCreateRecordedRestore(
                ref paths,
                journal,
                parentLease,
                journalLease);
            mixedLease = OpenOrQuarantineMixedPrior(
                ref paths,
                journal,
                prior,
                parentLease,
                journalLease,
                restoreLease.Identity);

            MoveRecordedPriorLeaves(
                mixedLease,
                restoreLease,
                prior);
            journal.State = "prior_reconstructing";
            WriteJournal(paths.Journal, journal, replace: true, parentLease, journalLease);
            Checkpoint("prior-restore-leaves-complete");

            restoreProof = ExactDirectorySnapshotLease.Capture(
                restoreLease,
                modName,
                requireExactOwner: true);
            RequireIdentity(
                restoreProof.Snapshot.Identity,
                restoreLease.Identity,
                "reconstructed prior deployment");
            RequireExact(
                restoreProof.Snapshot.Files,
                prior.Files,
                "reconstructed prior deployment");
            restoreProof.RequireCurrentNamespace(
                restoreLease,
                "reconstructed prior deployment");
            if (Directory.Exists(paths.Target) || File.Exists(paths.Target))
                throw new InvalidDataException(
                    "Cannot promote reconstructed prior deployment because the canonical target is occupied.");

            journal.State = "prior_reconstructed";
            WriteJournal(paths.Journal, journal, replace: true, parentLease, journalLease);
            restoreProof.Dispose();
            restoreProof = null;
            Checkpoint("prior-restore-promotion-prepared");
            restoreLease.RenameTo(
                parentLease,
                paths.Target,
                "reconstructed prior deployment");
            Checkpoint("prior-restore-promoted-before-journal");
            restoreProof = ExactDirectorySnapshotLease.Capture(
                restoreLease,
                modName,
                requireExactOwner: true);
            RequireExact(
                restoreProof.Snapshot.Files,
                prior.Files,
                "promoted reconstructed prior deployment");
            restoreProof.RequireCurrentNamespace(
                restoreLease,
                "promoted reconstructed prior deployment");
            journal.State = "manual_review";
            WriteJournal(paths.Journal, journal, replace: true, parentLease, journalLease);
            Checkpoint("prior-restore-manual-review-durable");
            throw new InvalidDataException(
                "Mixed prior deployment was preserved in its journal-bound quarantine and the exact recorded prior set was reconstructed; manual review is required.",
                membershipFailure);
        }
        finally
        {
            restoreProof?.Dispose();
            mixedLease?.Dispose();
            restoreLease?.Dispose();
        }
    }

    private static ExactDirectoryLease OpenOrCreateRecordedRestore(
        ref TransactionPaths paths,
        DeployJournal journal,
        ExactDirectoryLease parentLease,
        ExactJournalLease? journalLease)
    {
        if (journal.RestoreIdentity != null)
        {
            var expected = journal.RestoreIdentity.ToPhysical(paths.Restore);
            if (Directory.Exists(paths.Restore))
                return ExactDirectoryLease.OpenExisting(
                    paths.Restore,
                    expected,
                    "recorded prior restore directory");
            if (Directory.Exists(paths.Target))
                return ExactDirectoryLease.OpenExisting(
                    paths.Target,
                    journal.RestoreIdentity.ToPhysical(paths.Target),
                    "promoted prior restore directory");
            throw new InvalidDataException(
                "Recorded prior restore directory is missing from both pre- and post-promotion paths.");
        }

        for (var attempt = 0; attempt < 16; attempt++)
        {
            if (Directory.Exists(paths.Restore) || File.Exists(paths.Restore))
            {
                paths = RecordFreshArtifactPath(
                    paths,
                    journal,
                    parentLease,
                    journalLease,
                    kind: "restore");
                continue;
            }
            ExactDirectoryLease? created = null;
            try
            {
                created = ExactDirectoryLease.CreateNew(parentLease, paths.Restore);
                Checkpoint("prior-restore-directory-created-unrecorded");
                journal.RestoreIdentity = DeployDirectoryIdentity.From(created.Identity);
                journal.State = "prior_restore_prepared";
                WriteJournal(paths.Journal, journal, replace: true, parentLease, journalLease);
                Checkpoint("prior-restore-directory-recorded");
                return created;
            }
            catch (Exception ex) when (BypassesAutomaticRecovery(ex))
            {
                throw;
            }
            catch (IOException) when (created == null &&
                (Directory.Exists(paths.Restore) || File.Exists(paths.Restore)))
            {
                paths = RecordFreshArtifactPath(
                    paths,
                    journal,
                    parentLease,
                    journalLease,
                    kind: "restore");
            }
            catch
            {
                if (created != null && journal.RestoreIdentity == null)
                    created.DeleteWhenEmpty("uncommitted prior restore directory");
                throw;
            }
        }
        throw new IOException("Could not allocate a collision-free recorded prior restore directory.");
    }

    private static ExactDirectoryLease OpenOrQuarantineMixedPrior(
        ref TransactionPaths paths,
        DeployJournal journal,
        ExactDirectorySnapshot prior,
        ExactDirectoryLease parentLease,
        ExactJournalLease? journalLease,
        PhysicalDirectoryIdentity restoreIdentity)
    {
        if (journal.QuarantineIdentity != null && Directory.Exists(paths.Quarantine))
            return ExactDirectoryLease.OpenExisting(
                paths.Quarantine,
                journal.QuarantineIdentity.ToPhysical(paths.Quarantine),
                "mixed prior quarantine");

        ExactDirectoryLease? mixed = null;
        foreach (var candidate in new[] { paths.Backup, paths.Target })
        {
            if (!Directory.Exists(candidate)) continue;
            try
            {
                var identity = ImmutableBundleSourceLease.InspectDirectory(candidate);
                if (identity.SameObject(restoreIdentity)) continue;
                if (!identity.SameObject(prior.Identity)) continue;
                mixed = ExactDirectoryLease.OpenExisting(
                    candidate,
                    prior.Identity with { FinalPath = Normalize(candidate) },
                    "mixed prior container");
                break;
            }
            catch (InvalidDataException)
            {
                // A foreign candidate is preserved. Continue only if the exact
                // recorded old container can be proved at the other path.
            }
        }
        if (mixed == null)
            throw new InvalidDataException(
                "The exact recorded prior container cannot be located for reconstruction.");

        for (var attempt = 0; attempt < 16; attempt++)
        {
            if (Directory.Exists(paths.Quarantine) || File.Exists(paths.Quarantine))
            {
                paths = RecordFreshArtifactPath(
                    paths,
                    journal,
                    parentLease,
                    journalLease,
                    kind: "quarantine");
                continue;
            }
            journal.QuarantineIdentity = DeployDirectoryIdentity.From(mixed.Identity);
            journal.State = "prior_quarantine_prepared";
            WriteJournal(paths.Journal, journal, replace: true, parentLease, journalLease);
            Checkpoint("prior-quarantine-rename-prepared");
            try
            {
                mixed.RenameTo(
                    parentLease,
                    paths.Quarantine,
                    "mixed prior quarantine");
            }
            catch (IOException) when (
                Directory.Exists(paths.Quarantine) || File.Exists(paths.Quarantine))
            {
                paths = RecordFreshArtifactPath(
                    paths,
                    journal,
                    parentLease,
                    journalLease,
                    kind: "quarantine");
                continue;
            }
            Checkpoint("prior-quarantined-before-journal");
            journal.State = "prior_quarantined";
            WriteJournal(paths.Journal, journal, replace: true, parentLease, journalLease);
            Checkpoint("prior-quarantine-recorded");
            return mixed;
        }
        mixed.Dispose();
        throw new IOException("Could not allocate a collision-free mixed-prior quarantine.");
    }

    private static TransactionPaths RecordFreshArtifactPath(
        TransactionPaths paths,
        DeployJournal journal,
        ExactDirectoryLease parentLease,
        ExactJournalLease? journalLease,
        string kind)
    {
        var suffix = Guid.NewGuid().ToString("N");
        if (kind == "quarantine")
        {
            var initial = Path.Combine(
                paths.Parent,
                $".vmblauncher-receipt-deploy-{journal.PublishedId}.{journal.OperationId}.quarantine");
            journal.QuarantineDirectory = initial + "." + suffix;
            journal.QuarantineIdentity = null;
            paths = paths with { Quarantine = Normalize(journal.QuarantineDirectory) };
        }
        else if (kind == "restore")
        {
            var initial = Path.Combine(
                paths.Parent,
                $".vmblauncher-receipt-deploy-{journal.PublishedId}.{journal.OperationId}.restore");
            journal.RestoreDirectory = initial + "." + suffix;
            journal.RestoreIdentity = null;
            paths = paths with { Restore = Normalize(journal.RestoreDirectory) };
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        WriteJournal(paths.Journal, journal, replace: true, parentLease, journalLease);
        Checkpoint($"{kind}-alternate-path-recorded");
        return paths;
    }

    private static TransactionPaths QuarantineRecordedReplacement(
        TransactionPaths paths,
        DeployJournal journal,
        ExactDirectoryLease parentLease,
        ExactJournalLease? journalLease,
        ExactDirectoryLease replacementLease,
        PhysicalDirectoryIdentity replacementIdentity,
        string context)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            if (Directory.Exists(paths.Quarantine) || File.Exists(paths.Quarantine))
            {
                try
                {
                    using var existing = ExactDirectoryLease.OpenExisting(
                        paths.Quarantine,
                        replacementIdentity with { FinalPath = Normalize(paths.Quarantine) },
                        context);
                    journal.QuarantineIdentity = DeployDirectoryIdentity.From(replacementIdentity);
                    return paths;
                }
                catch
                {
                    paths = RecordFreshArtifactPath(
                        paths,
                        journal,
                        parentLease,
                        journalLease,
                        kind: "quarantine");
                    continue;
                }
            }
            journal.QuarantineIdentity = DeployDirectoryIdentity.From(replacementIdentity);
            WriteJournal(paths.Journal, journal, replace: true, parentLease, journalLease);
            Checkpoint("replacement-quarantine-rename-prepared");
            try
            {
                replacementLease.RenameTo(parentLease, paths.Quarantine, context);
            }
            catch (IOException) when (
                Directory.Exists(paths.Quarantine) || File.Exists(paths.Quarantine))
            {
                paths = RecordFreshArtifactPath(
                    paths,
                    journal,
                    parentLease,
                    journalLease,
                    kind: "quarantine");
                continue;
            }
            Checkpoint("replacement-quarantined");
            return paths;
        }
        throw new IOException("Could not allocate a collision-free replacement quarantine.");
    }

    private static void MoveRecordedPriorLeaves(
        ExactDirectoryLease mixedLease,
        ExactDirectoryLease restoreLease,
        ExactDirectorySnapshot prior)
    {
        foreach (var file in prior.Files.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            var source = Path.Combine(mixedLease.CurrentPath, file.Name);
            var destination = Path.Combine(restoreLease.CurrentPath, file.Name);
            var identity = prior.FileIdentities[file.Name];
            var sourceExists = File.Exists(source);
            var destinationExists = File.Exists(destination);
            if (sourceExists && destinationExists)
                throw new InvalidDataException(
                    $"Recorded prior leaf exists in both quarantine and restore: {file.Name}");
            if (destinationExists)
            {
                VerifyRecordedPriorLeaf(destination, file, identity);
                continue;
            }
            if (!sourceExists)
                throw new InvalidDataException(
                    $"Recorded prior leaf is missing from quarantine and restore: {file.Name}");
            using var stream = OpenPinnedRegularFile(source);
            VerifyRecordedPriorLeaf(stream, source, file, identity);
            RenamePinnedObject(
                stream.SafeFileHandle,
                destination,
                replaceIfExists: false,
                restoreLease.Handle);
            Checkpoint("prior-leaf-moved");
            if (!string.Equals(
                    Normalize(ImmutableBundleSourceLease.GetFinalPath(stream.SafeFileHandle)),
                    Normalize(destination),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Recorded prior leaf promotion landed at another path: {file.Name}");
        }
    }

    private static bool PathHasRecordedIdentity(
        string path,
        DeployDirectoryIdentity recorded)
    {
        try
        {
            return Directory.Exists(path) &&
                ImmutableBundleSourceLease.InspectDirectory(path).SameObject(
                    recorded.ToPhysical(path));
        }
        catch
        {
            return false;
        }
    }

    private static void VerifyRecordedPriorLeaf(
        string path,
        CommitQualifiedOutputFile file,
        PhysicalDirectoryIdentity identity)
    {
        using var stream = OpenPinnedRegularFile(path);
        VerifyRecordedPriorLeaf(stream, path, file, identity);
    }

    private static void VerifyRecordedPriorLeaf(
        FileStream stream,
        string path,
        CommitQualifiedOutputFile file,
        PhysicalDirectoryIdentity identity)
    {
        var actual = ImmutableBundleSourceLease.GetFileIdInformation(stream.SafeFileHandle, path);
        if (actual.VolumeSerialNumber != identity.VolumeSerialNumber ||
            actual.FileIdLow != identity.FileIdLow ||
            actual.FileIdHigh != identity.FileIdHigh ||
            stream.Length != file.Length)
            throw new InvalidDataException(
                $"Recorded prior leaf identity/length changed: {file.Name}");
        stream.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (hash != file.Sha256)
            throw new InvalidDataException(
                $"Recorded prior leaf bytes changed: {file.Name}");
    }
}
