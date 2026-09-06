using System.IO;

namespace VmbLauncher.Services;

/// <summary>
/// Journal retirement occurs only after the exact-set commit was made durable
/// under the recorded NTFS membership seals and those ACLs were restored. The
/// same-file witness keeps interrupted postcommit cleanup discoverable; it is
/// not a namespace or commit authority.
/// </summary>
internal static partial class LocalExactSetDeployment
{
    private static void RetireJournalAfterStableMembership(
        string journalPath,
        DeployJournal journal,
        ExactJournalLease journalLease,
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
        WriteJournal(
            journalPath,
            journal,
            replace: true,
            parentLease,
            journalLease);
        Checkpoint("journal-retirement-prepared");

        protectedDirectory.RequireCurrentPath(context);
        RequireExact(protectedProof.Snapshot.Files, expected, context);
        protectedProof.RequireCurrentNamespace(protectedDirectory, context);

        journalLease.RequireVersion(journal, "journal retirement authority");
        journalLease.RenameTo(
            witnessPath,
            parentLease,
            journal,
            "journal retirement witness");
        Checkpoint("journal-retirement-witness");

        try
        {
            // cleanup was already committed while both OS seals were active.
            // This delete retires only the recovery record; no notification
            // cancellation result is treated as namespace authority.
            journalLease.MarkDeleteOnClose();
        }
        catch (Exception ex) when (BypassesAutomaticRecovery(ex))
        {
            throw;
        }
        catch (Exception retirementFailure)
        {
            TryRestoreRetirementWitness(
                journalLease,
                journalPath,
                witnessPath,
                parentLease,
                journal);
            throw new InvalidDataException(
                "Postcommit journal retirement failed; the durable recovery witness was restored.",
                retirementFailure);
        }
        journalLease.Dispose();
        Checkpoint("journal-retirement-deleted");
        if (File.Exists(witnessPath) || Directory.Exists(witnessPath) ||
            File.Exists(journalPath) || Directory.Exists(journalPath))
            throw new IOException("Receipt-deploy journal witness was replaced during retirement.");
    }

    private static void TryRestoreRetirementWitness(
        ExactJournalLease journalLease,
        string journalPath,
        string witnessPath,
        ExactDirectoryLease parentLease,
        DeployJournal journal)
    {
        if (!string.Equals(
                journalLease.CurrentPath,
                witnessPath,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Receipt-deploy journal retirement witness lost its recorded path.");
        if (File.Exists(journalPath) || Directory.Exists(journalPath))
            throw new InvalidDataException(
                "Receipt-deploy canonical journal path was occupied while its witness was retained.");
        journalLease.RenameTo(
            journalPath,
            parentLease,
            journal,
            "restored journal retirement witness");
    }
}
