using System.IO;

namespace VmbLauncher.Services;

/// <summary>
/// Exact, journal-bound rollback after the durable forward transaction has
/// stopped before cleanup commit.
/// </summary>
internal static partial class LocalExactSetDeployment
{
    private static Exception? TryRollback(
        TransactionPaths paths,
        DeployJournal journal,
        string modName,
        ExactDirectorySnapshot prior,
        IReadOnlyList<CommitQualifiedOutputFile> expected,
        ExactDirectoryLease parentLease,
        ExactJournalLease? existingJournalLease)
    {
        ExactDirectoryLease? priorLease = null;
        ExactDirectoryLease? stageLease = null;
        ExactDirectorySnapshotLease? priorProof = null;
        ExactDirectorySnapshotLease? replacementProof = null;
        ExactJournalLease? journalLease = null;
        Exception? preservedReplacementFailure = null;
        try
        {
            journalLease = existingJournalLease ?? ExactJournalLease.Open(
                paths.Journal,
                journal,
                "automatic rollback journal lease");
            journalLease.RequireVersion(journal, "automatic rollback journal authority");
            if (journal.State == "cleanup")
                throw new InvalidOperationException(
                    "Verified cleanup may only be finalized; it cannot be rolled back.");
            if (Directory.Exists(paths.Backup))
            {
                priorLease = ExactDirectoryLease.OpenExisting(
                    paths.Backup,
                    prior.Identity,
                    "rollback backup");
                priorProof = ExactDirectorySnapshotLease.Capture(
                    priorLease,
                    modName,
                    requireExactOwner: true);
                RequireExact(priorProof.Snapshot.Files, prior.Files, "rollback backup");
                if (Directory.Exists(paths.Target))
                {
                    if (Directory.Exists(paths.Stage))
                        throw new InvalidDataException("Rollback has both target and stage; ownership is ambiguous.");
                    var stageIdentity = journal.StageIdentity?.ToPhysical(paths.Stage)
                        ?? throw new InvalidDataException("Rollback replacement lacks a stage identity.");
                    stageLease = ExactDirectoryLease.OpenExisting(
                        paths.Target,
                        stageIdentity,
                        "rollback replacement");
                    try
                    {
                        replacementProof = ExactDirectorySnapshotLease.Capture(
                            stageLease,
                            modName,
                            requireExactOwner: true);
                        RequireExact(replacementProof.Snapshot.Files, expected, "rollback replacement");
                        Checkpoint("rollback-replacement-pinned");
                        replacementProof.Dispose();
                        replacementProof = null;
                        stageLease.RenameTo(
                            parentLease,
                            paths.Stage,
                            "rollback replacement stage");
                        replacementProof = ExactDirectorySnapshotLease.Capture(
                            stageLease,
                            modName,
                            requireExactOwner: true);
                        RequireExact(
                            replacementProof.Snapshot.Files,
                            expected,
                            "rollback replacement stage");
                    }
                    catch (Exception ex) when (BypassesAutomaticRecovery(ex))
                    {
                        throw;
                    }
                    catch (Exception membershipFailure)
                    {
                        replacementProof?.Dispose();
                        replacementProof = null;
                        paths = QuarantineRecordedReplacement(
                            paths,
                            journal,
                            parentLease,
                            journalLease,
                            stageLease,
                            stageIdentity,
                            "mixed replacement quarantine");
                        Checkpoint("rollback-replacement-quarantined");
                        preservedReplacementFailure = membershipFailure;
                    }
                }
                Checkpoint("rollback-backup-pinned");
                priorProof.Dispose();
                priorProof = null;
                priorLease.RenameTo(
                    parentLease,
                    paths.Target,
                    "rollback restored target");
                priorProof = ExactDirectorySnapshotLease.Capture(
                    priorLease,
                    modName,
                    requireExactOwner: true);
                RequireExact(priorProof.Snapshot.Files, prior.Files, "rollback restored target");
                priorProof.RequireCurrentNamespace(priorLease, "rollback restored target");
                if (preservedReplacementFailure != null)
                    throw DurableManualReviewAfterPriorRestored(
                        paths,
                        journal,
                        parentLease,
                        journalLease,
                        priorLease,
                        priorProof,
                        prior.Files,
                        preservedReplacementFailure);
            }
            if (priorLease == null)
            {
                priorLease = ExactDirectoryLease.OpenExisting(
                    paths.Target,
                    prior.Identity,
                    "rollback prior target");
                priorProof = ExactDirectorySnapshotLease.Capture(
                    priorLease,
                    modName,
                    requireExactOwner: true);
                RequireExact(priorProof.Snapshot.Files, prior.Files, "rollback prior target");
            }
            priorLease.RequireCurrentPath("rollback restored target before cleanup");
            RequireExact(
                priorProof!.Snapshot.Files,
                prior.Files,
                "rollback restored target before cleanup");
            priorProof.RequireCurrentNamespace(
                priorLease,
                "rollback restored target before cleanup");
            Checkpoint("rollback-prior-restored-before-cleanup-journal");
            if (Directory.Exists(paths.Stage))
            {
                if (journal.StageIdentity == null)
                {
                    throw new InvalidDataException(
                        "Unidentified precommitted stage is preserved; rollback lacks deletion authority.");
                }
                var stageIdentity = journal.StageIdentity.ToPhysical(paths.Stage);
                var staged = ToOutputs(journal.StagedFiles);
                stageLease ??= ExactDirectoryLease.OpenExisting(
                    paths.Stage,
                    stageIdentity,
                    "rollback replacement");
                replacementProof?.Dispose();
                replacementProof = null;
                if (journal.State != "rollback_cleanup")
                {
                    paths = NormalizeOrQuarantineRecordedStage(
                        paths,
                        journal,
                        staged,
                        expected,
                        parentLease,
                        journalLease,
                        stageLease,
                        stageIdentity,
                        priorLease,
                        priorProof!,
                        prior.Files,
                        "mixed rollback stage quarantine");
                    journal.State = "rollback_cleanup";
                    WriteJournal(
                        paths.Journal,
                        journal,
                        replace: true,
                        parentLease,
                        journalLease);
                }
                DeleteRemainingExactDirectory(
                    stageLease,
                    staged,
                    ToPhysicalMap(journal.StagedFiles, paths.Stage, requireIdentity: true),
                    "rollback replacement");
                stageLease = null;
            }
            priorLease.RequireCurrentPath("rollback final prior target");
            RequireExact(priorProof!.Snapshot.Files, prior.Files, "rollback final prior target");
            priorProof.RequireCurrentNamespace(priorLease, "rollback final prior target");
            if (File.Exists(paths.Journal))
                RetireJournalAfterStableMembership(
                    paths.Journal,
                    journal,
                    journalLease,
                    parentLease,
                    priorLease,
                    priorProof!,
                    prior.Files,
                    "rollback final prior target");
            return null;
        }
        catch (Exception ex) when (BypassesAutomaticRecovery(ex))
        {
            throw;
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            replacementProof?.Dispose();
            priorProof?.Dispose();
            stageLease?.Dispose();
            priorLease?.Dispose();
            journalLease?.Dispose();
        }
    }
}
