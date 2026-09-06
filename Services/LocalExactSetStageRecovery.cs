using System.IO;

namespace VmbLauncher.Services;

/// <summary>
/// Normalizes only the journal-recorded stage membership before rollback or
/// recovery can delete that exact set.
/// </summary>
internal static partial class LocalExactSetDeployment
{
    private static void NormalizeRecordedStage(
        ExactDirectoryLease stageLease,
        DeployJournal journal,
        IReadOnlyList<CommitQualifiedOutputFile> staged,
        IReadOnlyList<DeployJournalFile> stagedRecords,
        IReadOnlyList<CommitQualifiedOutputFile> expected)
    {
        stageLease.RequireCurrentPath("recorded staging directory");
        var stageDirectory = stageLease.CurrentPath;
        var allowedNames = staged.Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
        var pending = journal.PendingFile;
        if (pending != null)
        {
            allowedNames.Add(pending.TempName);
            allowedNames.Add(pending.CanonicalName);
        }
        var entries = Directory.EnumerateFileSystemEntries(stageDirectory)
            .Take(MaximumManagedFiles + 1)
            .ToArray();
        if (entries.Length > MaximumManagedFiles)
            throw new InvalidDataException("Recorded stage inventory exceeds its safety bound.");
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            var attributes = File.GetAttributes(entry);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                !allowedNames.Contains(name) || !folded.Add(name))
                throw new InvalidDataException(
                    $"Recorded stage contains an unknown, nested, reparse, or case-colliding entry: {name}");
        }
        var stagedIdentityMap = ToPhysicalMap(
            stagedRecords,
            stageDirectory,
            requireIdentity: true);
        foreach (var file in staged)
        {
            var identity = stagedIdentityMap[file.Name];
            RequireExactFile(
                Path.Combine(stageDirectory, file.Name),
                file,
                delete: false,
                identity.VolumeSerialNumber,
                identity.FileIdLow,
                identity.FileIdHigh);
        }

        if (pending != null)
        {
            var tempPath = Path.Combine(stageDirectory, pending.TempName);
            var canonicalPath = Path.Combine(stageDirectory, pending.CanonicalName);
            var hasTemp = File.Exists(tempPath);
            var hasCanonical = File.Exists(canonicalPath);
            if (hasTemp && hasCanonical)
                throw new InvalidDataException(
                    "Pending stage file has both temporary and canonical leaves.");
            if (hasTemp)
                DeletePrecommittedTemp(
                    tempPath,
                    pending.TempVolumeSerialNumber,
                    pending.TempFileIdLow,
                    pending.TempFileIdHigh);
            else if (hasCanonical)
                RequireExactFile(
                    canonicalPath,
                    new CommitQualifiedOutputFile(
                        pending.CanonicalName,
                        pending.Length,
                        pending.Sha256),
                    delete: true,
                    pending.TempVolumeSerialNumber,
                    pending.TempFileIdLow,
                    pending.TempFileIdHigh);
            journal.PendingFile = null;
        }

        using var normalized = ExactDirectorySnapshotLease.Capture(
            stageLease,
            modName: "",
            requireExactOwner: false);
        RequireExact(normalized.Snapshot.Files, staged, "normalized staging directory");
    }

    private static TransactionPaths NormalizeOrQuarantineRecordedStage(
        TransactionPaths paths,
        DeployJournal journal,
        IReadOnlyList<CommitQualifiedOutputFile> staged,
        IReadOnlyList<CommitQualifiedOutputFile> expected,
        ExactDirectoryLease parentLease,
        ExactJournalLease journalLease,
        ExactDirectoryLease stageLease,
        PhysicalDirectoryIdentity stageIdentity,
        ExactDirectoryLease priorLease,
        ExactDirectorySnapshotLease priorProof,
        IReadOnlyList<CommitQualifiedOutputFile> prior,
        string context)
    {
        try
        {
            NormalizeRecordedStage(
                stageLease,
                journal,
                staged,
                journal.StagedFiles,
                expected);
            return paths;
        }
        catch (Exception ex) when (BypassesAutomaticRecovery(ex))
        {
            throw;
        }
        catch (Exception membershipFailure)
        {
            paths = QuarantineRecordedReplacement(
                paths,
                journal,
                parentLease,
                journalLease,
                stageLease,
                stageIdentity,
                context);
            Checkpoint("rollback-stage-quarantined");
            throw DurableManualReviewAfterPriorRestored(
                paths,
                journal,
                parentLease,
                journalLease,
                priorLease,
                priorProof,
                prior,
                membershipFailure);
        }
    }

    private static InvalidDataException DurableManualReviewAfterPriorRestored(
        TransactionPaths paths,
        DeployJournal journal,
        ExactDirectoryLease parentLease,
        ExactJournalLease journalLease,
        ExactDirectoryLease priorLease,
        ExactDirectorySnapshotLease priorProof,
        IReadOnlyList<CommitQualifiedOutputFile> prior,
        Exception membershipFailure)
    {
        priorLease.RequireCurrentPath("manual-review restored prior target");
        RequireExact(
            priorProof.Snapshot.Files,
            prior,
            "manual-review restored prior target");
        priorProof.RequireCurrentNamespace(
            priorLease,
            "manual-review restored prior target");
        var staged = ToOutputs(journal.StagedFiles);
        var expected = ToOutputs(journal.ExpectedFiles);
        if (journal.PendingFile == null && MapEquals(staged, expected))
        {
            journal.State = "manual_review";
            WriteJournal(
                paths.Journal,
                journal,
                replace: true,
                parentLease,
                journalLease);
        }
        else
        {
            // Partial-stage states retain their exact original schema shape.
            // The durable quarantine identity is itself the manual-review
            // boundary; promoting an incomplete journal to manual_review
            // would violate the committed-stage invariant on the next read.
            journalLease.RequireVersion(
                journal,
                "partial-stage quarantine manual-review authority");
        }
        Checkpoint("mixed-replacement-manual-review-durable");
        return new InvalidDataException(
            "Mixed replacement was preserved in its journal-bound quarantine and the exact prior deployment was restored; manual review is required. " +
            $"Membership proof failed: {membershipFailure.Message}",
            membershipFailure);
    }
}
