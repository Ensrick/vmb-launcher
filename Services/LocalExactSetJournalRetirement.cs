using System.IO;

namespace VmbLauncher.Services;

/// <summary>
/// Final journal retirement is mediated by a durable same-file witness and a
/// kernel directory-notification cancellation race. This does not claim a
/// continuous namespace seal; retirement is authorized only when the exact
/// notification request terminates with ERROR_OPERATION_ABORTED.
/// </summary>
internal static partial class LocalExactSetDeployment
{
    private static void RetireJournalAfterStableMembership(
        string journalPath,
        DeployJournal journal,
        ExactDirectoryLease parentLease,
        ExactDirectoryLease protectedDirectory,
        ExactDirectorySnapshotLease protectedProof,
        IReadOnlyList<CommitQualifiedOutputFile> expected,
        string context)
    {
        var witnessPath = Normalize(journal.RetirementJournalPath);
        if (File.Exists(witnessPath) || Directory.Exists(witnessPath))
            throw new InvalidDataException(
                "Receipt-deploy journal retirement witness path is occupied; preserving it for manual review.");

        // The witness path and current cleanup/rollback state reach durable
        // storage before the canonical journal name is removed.
        WriteJournal(journalPath, journal, replace: true, parentLease);
        Checkpoint("journal-retirement-prepared");

        using var monitor = DirectoryMembershipMonitor.Arm(
            protectedDirectory,
            context + " membership monitor");
        protectedDirectory.RequireCurrentPath(context);
        RequireExact(protectedProof.Snapshot.Files, expected, context);
        protectedProof.RequireCurrentNamespace(protectedDirectory, context);

        using var journalStream = OpenPinnedJournalForUpdate(journalPath);
        RequireSameJournalVersion(
            ReadJournal(journalStream, journalPath),
            journal,
            "journal retirement authority");
        RenamePinnedObject(
            journalStream.SafeFileHandle,
            witnessPath,
            replaceIfExists: false,
            parentLease.Handle);
        Checkpoint("journal-retirement-witness");

        MembershipArbitration arbitration;
        try
        {
            arbitration = monitor.CancelAndArbitrate();
        }
        catch (Exception ex) when (BypassesAutomaticRecovery(ex))
        {
            throw;
        }
        catch (Exception arbitrationFailure)
        {
            TryRestoreRetirementWitness(
                journalStream,
                journalPath,
                witnessPath,
                parentLease,
                journal,
                manualReview: true);
            throw new InvalidDataException(
                "Target membership arbitration was uncertain; the durable journal was retained.",
                arbitrationFailure);
        }

        if (arbitration != MembershipArbitration.CancelWon)
        {
            TryRestoreRetirementWitness(
                journalStream,
                journalPath,
                witnessPath,
                parentLease,
                journal,
                manualReview: true);
            throw new InvalidDataException(
                "Target membership changed or could not be proven stable; the durable journal was retained for manual review.");
        }

        // Cancellation completed the exact kernel notification request with
        // ERROR_OPERATION_ABORTED. That is the transaction commit point.
        // Later changes are postcommit activity; witness deletion is cleanup.
        MarkDeleteOnClose(journalStream.SafeFileHandle);
        journalStream.Dispose();
        Checkpoint("journal-retirement-deleted");
        if (File.Exists(witnessPath) || Directory.Exists(witnessPath) ||
            File.Exists(journalPath) || Directory.Exists(journalPath))
            throw new IOException("Receipt-deploy journal witness was replaced during retirement.");
    }

    private static void TryRestoreRetirementWitness(
        FileStream witnessStream,
        string journalPath,
        string witnessPath,
        ExactDirectoryLease parentLease,
        DeployJournal journal,
        bool manualReview)
    {
        if (!string.Equals(
                Normalize(ImmutableBundleSourceLease.GetFinalPath(witnessStream.SafeFileHandle)),
                witnessPath,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Receipt-deploy journal retirement witness lost its recorded path.");
        if (File.Exists(journalPath) || Directory.Exists(journalPath))
            throw new InvalidDataException(
                "Receipt-deploy canonical journal path was occupied while its witness was retained.");
        RenamePinnedObject(
            witnessStream.SafeFileHandle,
            journalPath,
            replaceIfExists: false,
            parentLease.Handle);
        if (manualReview)
        {
            journal.State = "manual_review";
            WriteReplacementJournal(witnessStream, journalPath, journal);
        }
    }
}
