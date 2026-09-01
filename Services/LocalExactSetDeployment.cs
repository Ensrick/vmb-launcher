using System.IO;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VmbLauncher.Services;

/// <summary>
/// Replaces one owned local Workshop item directory with an exact canonical
/// output set. The complete replacement is staged and hash-checked first; a
/// durable journal plus same-volume directory renames make interrupted work
/// recoverable without deleting unknown or foreign content.
/// </summary>
internal static partial class LocalExactSetDeployment
{
#if VMBLAUNCHER_TEST_HOOKS
    internal static Action<string>? TransitionForTest;
#endif
    private const int JournalSchema = 5;
    private const int MaximumManagedFiles = 4096;
    private const long MaximumManagedBytes = 32L * 1024 * 1024 * 1024;
    private const int MaximumWorkshopJournals = 4096;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileListDirectory = 0x0001;
    private const uint FileReadAttributes = 0x0080;
    private const uint OpenExisting = 3;
    private const uint CreateNew = 1;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagWriteThrough = 0x80000000;

    private static readonly JsonSerializerOptions JournalJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    internal static RunOutcome Reconcile(
        string targetDirectory,
        VerifiedCommitQualifiedExpectedSet authorization,
        ImmutableBundleSourceLease source,
        Action<string>? log = null,
        Func<DateTime>? utcNow = null)
    {
        MachineTransactionLease.RequireCurrent("Receipt-authority local exact-set deploy");
        var now = utcNow ?? (() => DateTime.UtcNow);
        try
        {
            var modName = authorization.Mod;
            var publishedId = authorization.PublishedId;
            var sourceCommit = authorization.SourceCommit;
            var expected = Array.AsReadOnly(authorization.Files
                .Select(file => new CommitQualifiedOutputFile(file.Name, file.Length, file.Sha256))
                .OrderBy(file => file.Name, StringComparer.Ordinal)
                .ToArray());
            ValidateExpectedMap(modName, expected);
            if (!System.Text.RegularExpressions.Regex.IsMatch(publishedId, "^[1-9][0-9]{0,19}$") ||
                !System.Text.RegularExpressions.Regex.IsMatch(sourceCommit, "^[0-9a-f]{40}$") ||
                !System.Text.RegularExpressions.Regex.IsMatch(
                    authorization.OutputFingerprint, "^[0-9a-f]{64}$") ||
                !System.Text.RegularExpressions.Regex.IsMatch(
                    authorization.AuthorityFingerprint, "^[0-9a-f]{64}$") ||
                authorization.OutputFingerprint != VerifiedCommitQualifiedExpectedSet.Fingerprint(
                    modName, publishedId, sourceCommit, expected))
                return new(false, "Receipt-authority deploy identity is noncanonical.");

            var locator = TransactionLocator.Create(targetDirectory, publishedId);
            RecoverInterrupted(locator, authorization, log);
            RequireSupportedExactSetFileSystem(locator.Parent, "Workshop destination");
            RequireSupportedExactSetFileSystem(source.SourceRoot, "immutable source");
            if (!MapEquals(source.Files, expected) || !authorization.IsFresh(modName, now()))
                return new(false, "Receipt-authority deploy identity is noncanonical or expired.");
            if (string.Equals(locator.Target, source.SourceRoot, StringComparison.OrdinalIgnoreCase))
                return new(false, "Receipt-authority source and destination directories are identical.");
            RefuseUnjournaledArtifacts(locator);
            if (!Directory.Exists(locator.Target))
                return new(false,
                    $"Workshop folder missing:\n{locator.Target}\n\nSubscribe to your own Workshop item in Steam first.");

            ExactDirectoryLease? parentLease = ExactDirectoryLease.OpenExisting(
                locator.Parent,
                context: "Workshop destination parent");
            var parentIdentity = parentLease.Identity;
            ExactDirectoryLease? priorLease = null;
            ExactDirectoryLease? stageLease = null;
            ExactDirectorySnapshotLease? priorProof = null;
            ExactDirectorySnapshotLease? stageProof = null;
            ExactDirectorySnapshotLease? backupProof = null;
            ExactDirectorySnapshotLease? deployedProof = null;
            ExactDirectorySnapshot? prior = null;
            DeployJournal? journal = null;
            TransactionPaths? paths = null;
            LocalExactSetMembershipSeal? parentMembershipSeal = null;
            LocalExactSetMembershipSeal? targetMembershipSeal = null;
            ExactJournalLease? journalLease = null;
            try
            {
                priorLease = ExactDirectoryLease.OpenExisting(
                    locator.Target,
                    context: "owned prior deployment");
                priorProof = ExactDirectorySnapshotLease.Capture(
                    priorLease,
                    modName,
                    requireExactOwner: true);
                prior = priorProof.Snapshot;
                ValidateManagedMap(modName, prior.Files, requireExactOwner: true);
                paths = locator.CreateOperation();
                var identity = MachineTransactionLease.CurrentIdentity
                    ?? throw new InvalidOperationException("Receipt deploy has no current transaction identity.");
                journal = DeployJournal.Create(
                    identity,
                    CurrentUserSid(),
                    paths,
                    parentIdentity,
                    prior,
                    authorization,
                    expected);
                WriteJournal(paths.Journal, journal, replace: false, parentLease);
                Checkpoint("journal-durable");
                stageLease = ExactDirectoryLease.CreateNew(parentLease, paths.Stage);
                Checkpoint("stage-directory-created-unrecorded");
                stageLease.RequireCurrentPath("new staging directory");
                var stageIdentity = stageLease.Identity;
                journal.StageIdentity = DeployDirectoryIdentity.From(stageIdentity);
                journal.State = "staging";
                WriteJournal(paths.Journal, journal, replace: true, parentLease);
                Checkpoint("stage-created");
                stageLease.RequireCurrentPath("recorded staging directory");
                var copied = new List<DeployJournalFile>();
                foreach (var file in expected.OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    stageLease.RequireCurrentPath("staging directory before file copy");
                    var tempName = $".vmblauncher-receipt-file-{Guid.NewGuid():N}.tmp";
                    journal.PendingFile = new DeployPendingFile
                    {
                        CanonicalName = file.Name,
                        TempName = tempName,
                        Length = file.Length,
                        Sha256 = file.Sha256,
                    };
                    WriteJournal(paths.Journal, journal, replace: true, parentLease);
                    Checkpoint("stage-file-prepared");
                    var tempPath = Path.Combine(paths.Stage, tempName);
                    var stagePath = Path.Combine(paths.Stage, file.Name);
                    using var tempHandle = CreateNewPinnedFile(stageLease, tempPath);
                    using var output = new FileStream(
                        tempHandle,
                        FileAccess.ReadWrite,
                        128 * 1024,
                        isAsync: false);
                    try
                    {
                        Checkpoint("stage-temp-created-unrecorded");
                        var tempFileId = ImmutableBundleSourceLease.GetFileIdInformation(
                            output.SafeFileHandle,
                            tempPath);
                        journal.PendingFile.TempVolumeSerialNumber = tempFileId.VolumeSerialNumber;
                        journal.PendingFile.TempFileIdLow = tempFileId.FileIdLow;
                        journal.PendingFile.TempFileIdHigh = tempFileId.FileIdHigh;
                        WriteJournal(paths.Journal, journal, replace: true, parentLease);
                    }
                    catch (Exception ex) when (!BypassesAutomaticRecovery(ex))
                    {
                        // Until the temp identity reaches the durable journal,
                        // only this still-live handle is deletion authority.
                        MarkDeleteOnClose(output.SafeFileHandle);
                        throw;
                    }
                    Checkpoint("stage-temp-created");
                    source.CopyTo(
                        file.Name,
                        output,
                        () => Checkpoint("stage-temp-partial-write"));
                    output.Flush(flushToDisk: true);
                    Checkpoint("stage-temp-flushed");
                    if (output.Length != file.Length)
                        throw new InvalidDataException(
                            $"Staged temporary output length differs from its pinned source: {file.Name}");
                    output.Position = 0;
                    var stagedHash = Convert.ToHexString(SHA256.HashData(output)).ToLowerInvariant();
                    if (stagedHash != file.Sha256 ||
                        !string.Equals(
                            Normalize(ImmutableBundleSourceLease.GetFinalPath(output.SafeFileHandle)),
                            tempPath,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            $"Staged temporary output differs from its pinned source: {file.Name}");
                    try
                    {
                        RenamePinnedObject(
                            output.SafeFileHandle,
                            stagePath,
                            replaceIfExists: false,
                            stageLease.Handle);
                    }
                    catch (Exception ex)
                    {
                        throw new IOException($"Pinned stage-file promotion failed: {ex.Message}", ex);
                    }
                    if (!string.Equals(
                            Normalize(ImmutableBundleSourceLease.GetFinalPath(output.SafeFileHandle)),
                            stagePath,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            $"Staged output rename did not preserve its pinned identity: {file.Name}");
                    Checkpoint("stage-file-renamed");
                    copied.Add(new DeployJournalFile
                    {
                        Name = file.Name,
                        Length = file.Length,
                        Sha256 = file.Sha256,
                        VolumeSerialNumber = journal.PendingFile.TempVolumeSerialNumber,
                        FileIdLow = journal.PendingFile.TempFileIdLow,
                        FileIdHigh = journal.PendingFile.TempFileIdHigh,
                    });
                    journal.StagedFiles = copied
                        .OrderBy(item => item.Name, StringComparer.Ordinal)
                        .ToList();
                    journal.PendingFile = null;
                    WriteJournal(paths.Journal, journal, replace: true, parentLease);
                    Checkpoint("stage-file-copied");
                    stageLease.RequireCurrentPath("staging directory after file copy");
                }

                stageProof = ExactDirectorySnapshotLease.Capture(
                    stageLease,
                    modName,
                    requireExactOwner: true);
                var staged = stageProof.Snapshot;
                RequireIdentity(staged.Identity, stageIdentity, "staged replacement");
                RequireExact(staged.Files, expected, "staged replacement");
                journal.State = "staged";
                WriteJournal(paths.Journal, journal, replace: true, parentLease);
                Checkpoint("stage-verified");
                stageProof.RequireCurrentNamespace(stageLease, "staged replacement after verification");

                var parentAtCommit = ImmutableBundleSourceLease.InspectDirectory(paths.Parent);
                RequireIdentity(parentAtCommit, parentIdentity, "precommit Workshop parent");
                priorLease.RequireCurrentPath("precommit prior deployment");
                stageLease.RequireCurrentPath("precommit staged replacement");
                RequireExact(priorProof.Snapshot.Files, prior.Files, "precommit prior deployment");
                RequireExact(stageProof.Snapshot.Files, expected, "precommit staged replacement");
                Checkpoint("precommit-census-complete");
                priorProof.RequireCurrentNamespace(priorLease, "precommit prior deployment");
                stageProof.RequireCurrentNamespace(stageLease, "precommit staged replacement");

                if (!authorization.IsFresh(modName, now()) ||
                    !authorization.TryConsume(modName, now()))
                    throw new InvalidDataException(
                        "Receipt authority expired or was consumed before the destination commit boundary.");

                // Windows cannot rename a nonempty directory while child file
                // handles remain open. Consume authority while the restrictive
                // proofs are still live, then release the prior proof only in
                // the no-checkpoint window immediately before the handle-bound
                // rename. The directory identity lease remains live throughout.
                priorProof.Dispose();
                priorProof = null;
                Checkpoint("target-proof-released-before-rename");
                priorLease.RenameTo(parentLease, paths.Backup, "backed-up prior deployment");
                Checkpoint("target-moved-before-journal");
                try
                {
                    backupProof = ExactDirectorySnapshotLease.Capture(
                        priorLease,
                        modName,
                        requireExactOwner: true);
                    RequireExact(backupProof.Snapshot.Files, prior.Files, "backed-up prior deployment");
                }
                catch (Exception membershipFailure)
                {
                    backupProof?.Dispose();
                    backupProof = null;
                    priorLease.Dispose();
                    priorLease = null;
                    ReconstructPriorAndRequireManualReview(
                        paths,
                        journal,
                        modName,
                        prior,
                        parentLease,
                        null,
                        membershipFailure);
                    throw;
                }
                journal.State = "backed_up";
                WriteJournal(paths.Journal, journal, replace: true, parentLease);
                Checkpoint("target-backed-up");
                stageProof.Dispose();
                stageProof = null;
                stageLease.RenameTo(parentLease, paths.Target, "installed replacement");
                Checkpoint("replacement-moved-before-journal");
                stageLease.RequireCurrentPath("installed replacement");
                deployedProof = ExactDirectorySnapshotLease.Capture(
                    stageLease,
                    modName,
                    requireExactOwner: true);
                RequireExact(deployedProof.Snapshot.Files, expected, "installed replacement");
                journal.State = "installed";
                WriteJournal(paths.Journal, journal, replace: true, parentLease);
                Checkpoint("replacement-installed");

                RequireExact(deployedProof.Snapshot.Files, expected, "deployed target");
                deployedProof.RequireCurrentNamespace(stageLease, "deployed target");
                journal.State = "verified";
                WriteJournal(paths.Journal, journal, replace: true, parentLease);
                Checkpoint("target-verified");
                deployedProof.RequireCurrentNamespace(stageLease, "verified deployed target");

                // Record both exact ACL transitions before either DACL is
                // mutated. The parent seal denies replacement of the target
                // directory; the target seal denies membership insertion and
                // removal. Existing leaf proof handles continue to pin every
                // authenticated output. Together these are the OS-enforced
                // authority across the final proof -> durable commit window.
                parentMembershipSeal = LocalExactSetMembershipSeal.Prepare(
                    paths.Parent,
                    parentIdentity);
                targetMembershipSeal = LocalExactSetMembershipSeal.Prepare(
                    paths.Target,
                    new PhysicalDirectoryIdentity(
                        stageLease.Identity.VolumeSerialNumber,
                        stageLease.Identity.FileIdLow,
                        stageLease.Identity.FileIdHigh,
                        paths.Target));
                journalLease = ExactJournalLease.Open(
                    paths.Journal,
                    journal,
                    "membership-seal journal lease");
                journal.ParentMembershipSeal = DeployMembershipSeal.From(
                    parentMembershipSeal.Plan);
                journal.TargetMembershipSeal = DeployMembershipSeal.From(
                    targetMembershipSeal.Plan);
                WriteJournal(
                    paths.Journal,
                    journal,
                    replace: true,
                    parentLease,
                    journalLease);
                Checkpoint("membership-seals-planned");
                parentMembershipSeal.Apply();
                Checkpoint("parent-membership-seal-applied");
                targetMembershipSeal.Apply();
                Checkpoint("membership-seals-applied");
                parentMembershipSeal.RequireApplied();
                targetMembershipSeal.RequireApplied();
                deployedProof.RequireCurrentNamespace(stageLease, "sealed deployed target");
                journal.State = "cleanup";
                WriteJournal(
                    paths.Journal,
                    journal,
                    replace: true,
                    parentLease,
                    journalLease);
                Checkpoint("cleanup-durable");
                deployedProof.RequireCurrentNamespace(stageLease, "cleanup deployed target");

                // cleanup is the sole durable commit boundary. After it is
                // durable, later namespace activity is postcommit. Restore the
                // exact captured ACLs before deleting the backup or retiring
                // the journal so a crash never strands an unjournaled seal.
                targetMembershipSeal.Restore();
                Checkpoint("target-membership-seal-restored-before-parent");
                parentMembershipSeal.Restore();
                Checkpoint("parent-membership-seal-restored-before-reproof");
                // Parent DACL restoration must not propagate into the target.
                // Recheck the still-open target and parent handles only after
                // both restores have completed.
                targetMembershipSeal.RequireOriginal();
                parentMembershipSeal.RequireOriginal();
                targetMembershipSeal.Dispose();
                targetMembershipSeal = null;
                parentMembershipSeal.Dispose();
                parentMembershipSeal = null;
                Checkpoint("membership-seals-restored");
                backupProof.Dispose();
                backupProof = null;
                DeleteRemainingExactDirectory(
                    priorLease,
                    prior.Files,
                    prior.FileIdentities,
                    "owned prior deployment");
                Checkpoint("backup-deleted");
                stageLease.RequireCurrentPath("final deployed target");
                RequireExact(deployedProof.Snapshot.Files, expected, "final deployed target");
                deployedProof.RequireCurrentNamespace(stageLease, "final deployed target");
                RetireJournalAfterStableMembership(
                    paths.Journal,
                    journal,
                    journalLease,
                    parentLease,
                    stageLease,
                    deployedProof,
                    expected,
                    "final deployed target");
                journalLease = null;
                TryLog(log,
                    $"[receipt-deploy] exact local set installed at {Path.GetFileName(paths.Target)} ({expected.Count} files)");
                return new(true, $"Deployed exact receipt-authority set ({expected.Count} file(s))");
            }
            catch (Exception ex) when (BypassesAutomaticRecovery(ex))
            {
                throw;
            }
            catch (Exception failure)
            {
                Exception? precommitCleanup = null;
                Exception? membershipSealRestoration = null;
                var membershipSealPairRestored = false;
                try
                {
                    var hadMembershipSealPair =
                        targetMembershipSeal != null && parentMembershipSeal != null;
                    targetMembershipSeal?.Restore();
                    parentMembershipSeal?.Restore();
                    // Restoring an unprotected parent must not propagate ACL
                    // changes into the already-restored target. Re-prove both
                    // exact originals before retiring their durable plans.
                    targetMembershipSeal?.RequireOriginal();
                    parentMembershipSeal?.RequireOriginal();
                    membershipSealPairRestored = hadMembershipSealPair;
                }
                catch (Exception ex)
                {
                    membershipSealRestoration = ex;
                    // A failed exact restoration must leave the durable plan
                    // and current ACL in place for authenticated recovery; do
                    // not let Dispose retry and mask the primary disposition.
                    targetMembershipSeal?.AbandonWithoutRestore();
                    parentMembershipSeal?.AbandonWithoutRestore();
                    targetMembershipSeal = null;
                    parentMembershipSeal = null;
                }
                if (stageLease != null && journal?.StageIdentity == null)
                {
                    try
                    {
                        // The stage exists but its identity has not reached the
                        // journal. Only the creating live lease may remove it.
                        stageLease.DeleteWhenEmpty("uncommitted live staging directory");
                        stageLease = null;
                    }
                    catch (Exception ex)
                    {
                        precommitCleanup = ex;
                    }
                }
                if (membershipSealRestoration != null)
                    return new(false,
                        $"Receipt-authority local deploy stopped safely because its exact membership ACL could not be restored: {membershipSealRestoration.Message}. The durable journal was preserved for authenticated recovery.");
                if (journal?.State == "cleanup" &&
                    paths != null &&
                    (journalLease != null
                        ? IsExactDurableJournalVersion(journalLease, journal)
                        : IsExactDurableJournalVersion(paths.Journal, journal)))
                    return new(false,
                        $"Receipt-authority local deploy cleanup was interrupted after the replacement was proven: {failure.Message}. Retry will finalize the journal-bound cleanup without rolling back installed bytes.");
                if (paths == null || journal == null || prior == null)
                    return new(false, $"Receipt-authority local deploy failed before its durable transaction: {failure.Message}.");
                if (!File.Exists(paths.Journal) &&
                    Directory.Exists(paths.Target) &&
                    !Directory.Exists(paths.Stage) &&
                    !Directory.Exists(paths.Backup) &&
                    !Directory.Exists(paths.Quarantine))
                    return new(false,
                        $"Receipt-authority local deploy failed before its durable transaction: {failure.Message}. Previous deployment remains in place.");
                DeployJournal durableRollbackJournal;
                try
                {
                    durableRollbackJournal = journalLease != null
                        ? journalLease.Read("automatic rollback journal authority")
                        : ReadJournal(paths.Journal);
                    RequireSameJournalTransaction(durableRollbackJournal, journal);
                }
                catch (Exception durableStateFailure)
                {
                    return new(false,
                        $"Receipt-authority local deploy failed: {failure.Message}. Automatic rollback stopped safely because the durable journal state could not be proven: {durableStateFailure.Message}");
                }
                if (durableRollbackJournal.State == "manual_review")
                    return new(false,
                        $"Receipt-authority local deploy stopped in a durable manual-review state and left the current journal-bound target contents in place; manual review is required: {failure.Message}");
                var hasParentMembershipSeal =
                    durableRollbackJournal.ParentMembershipSeal != null;
                var hasTargetMembershipSeal =
                    durableRollbackJournal.TargetMembershipSeal != null;
                if (hasParentMembershipSeal != hasTargetMembershipSeal)
                    return new(false,
                        $"Receipt-authority local deploy failed: {failure.Message}. Automatic rollback stopped safely because its durable membership-seal plan pair was incomplete.");
                if (hasParentMembershipSeal)
                {
                    if (!membershipSealPairRestored ||
                        journalLease == null ||
                        stageLease == null ||
                        deployedProof == null)
                        return new(false,
                            $"Receipt-authority local deploy failed: {failure.Message}. Automatic rollback stopped safely because its membership-seal restoration authority was incomplete.");
                    targetMembershipSeal!.RequireOriginal();
                    parentMembershipSeal!.RequireOriginal();
                    stageLease.RequireCurrentPath(
                        "seal-plan retirement replacement target");
                    RequireExact(
                        deployedProof.Snapshot.Files,
                        expected,
                        "seal-plan retirement replacement target");
                    deployedProof.RequireCurrentNamespace(
                        stageLease,
                        "seal-plan retirement replacement target");
                    durableRollbackJournal.ParentMembershipSeal = null;
                    durableRollbackJournal.TargetMembershipSeal = null;
                    WriteJournal(
                        paths.Journal,
                        durableRollbackJournal,
                        replace: true,
                        parentLease,
                        journalLease);
                    Checkpoint("membership-seals-cleared-before-rollback");
                }
                targetMembershipSeal?.Dispose();
                targetMembershipSeal = null;
                parentMembershipSeal?.Dispose();
                parentMembershipSeal = null;
                stageProof?.Dispose();
                stageProof = null;
                backupProof?.Dispose();
                backupProof = null;
                deployedProof?.Dispose();
                deployedProof = null;
                priorProof?.Dispose();
                priorProof = null;
                stageLease?.Dispose();
                stageLease = null;
                priorLease?.Dispose();
                priorLease = null;
                if (durableRollbackJournal.State.StartsWith("prior_", StringComparison.Ordinal))
                {
                    try
                    {
                        var recovery = ValidateRecoveryJournal(
                            TransactionLocator.Create(paths.Target, durableRollbackJournal.PublishedId),
                            durableRollbackJournal,
                            modName,
                            authorization: null);
                        using var recoveryJournalLease = journalLease ??
                            ExactJournalLease.Open(
                                paths.Journal,
                                durableRollbackJournal,
                                "prior reconstruction recovery journal lease");
                        journalLease = null;
                        RecoverValidatedJournal(recovery, recoveryJournalLease, log);
                    }
                    catch (Exception reconstruction)
                    {
                        return new(false,
                            $"Receipt-authority local deploy entered journal-bound prior reconstruction after preserving foreign content: {reconstruction.Message}");
                    }
                }
                var rollback = TryRollback(
                    paths,
                    durableRollbackJournal,
                    modName,
                    prior,
                    expected,
                    parentLease,
                    journalLease);
                if (precommitCleanup != null)
                    rollback = rollback == null
                        ? precommitCleanup
                        : new AggregateException(precommitCleanup, rollback);
                var suffix = rollback == null
                    ? " Previous deployment restored."
                    : $" Automatic rollback stopped safely: {rollback.Message}";
                return new(false,
                    $"Receipt-authority local deploy failed: {failure.Message}.{suffix}");
            }
            finally
            {
                targetMembershipSeal?.Dispose();
                parentMembershipSeal?.Dispose();
                stageProof?.Dispose();
                priorProof?.Dispose();
                backupProof?.Dispose();
                deployedProof?.Dispose();
                journalLease?.Dispose();
                stageLease?.Dispose();
                priorLease?.Dispose();
                parentLease?.Dispose();
            }
        }
        catch (Exception ex) when (BypassesAutomaticRecovery(ex))
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(false, $"Receipt-authority local deploy refused: {ex.Message}");
        }
    }

    internal static void ValidateExpectedMap(
        string modName,
        IReadOnlyList<CommitQualifiedOutputFile> files)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(modName, "^[a-z0-9_]+$") ||
            files.Count == 0 || files.Count > 4096)
            throw new InvalidDataException("Receipt-authority expected output map is empty or noncanonical.");
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long aggregateLength = 0;
        foreach (var file in files)
        {
            if (!IsCanonicalOutputName(file.Name, modName) ||
                file.Length < 0 ||
                !System.Text.RegularExpressions.Regex.IsMatch(file.Sha256, "^[0-9a-f]{64}$") ||
                !exact.Add(file.Name) ||
                !folded.Add(file.Name))
                throw new InvalidDataException(
                    $"Receipt-authority expected output record is invalid: '{file.Name}'.");
            try { aggregateLength = checked(aggregateLength + file.Length); }
            catch (OverflowException)
            {
                throw new InvalidDataException(
                    "Receipt-authority expected output length overflowed its bounded census.");
            }
        }
        if (aggregateLength > 32L * 1024 * 1024 * 1024)
            throw new InvalidDataException(
                "Receipt-authority expected output set exceeds the 32-GiB safety bound.");
        if (!exact.Contains($"{modName}.mod") ||
            files.Count(file => file.Name.EndsWith(".mod", StringComparison.Ordinal)) != 1 ||
            !files.Any(file => file.Name.EndsWith(".mod_bundle", StringComparison.Ordinal)))
            throw new InvalidDataException(
                "Receipt-authority expected output map lacks its exact owner descriptor or bundle.");
    }

    private static void RecoverInterrupted(
        TransactionLocator locator,
        VerifiedCommitQualifiedExpectedSet authorization,
        Action<string>? log)
    {
        var matches = DiscoverDurableJournals(locator.Parent, strictCorruption: true)
            .Where(item => string.Equals(
                item.Locator.Journal,
                locator.Journal,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0) return;
        if (matches.Length != 1)
            throw new InvalidDataException(
                "Receipt-deploy journal namespace is ambiguous for this exact target.");
        var item = matches[0];
        RequireSupportedRecoverySchema(item.Journal);
        using var journalLease = RestoreCanonicalJournalNameAndOpenLease(
            item.Locator,
            item.Journal,
            item.SourcePath);
        RefuseJournalTemps(locator.Parent);
        var journal = journalLease.Read("forward recovery journal authority");
        var recovery = ValidateRecoveryJournal(
            locator,
            journal,
            authorization.Mod,
            authorization);
        RecoverValidatedJournal(recovery, journalLease, log);
    }

    private static RecoveryContext ValidateRecoveryJournal(
        TransactionLocator locator,
        DeployJournal journal,
        string expectedMod,
        VerifiedCommitQualifiedExpectedSet? authorization)
    {
        var paths = locator.FromJournal(journal);
        RequireSupportedRecoverySchema(journal);
        var current = MachineTransactionLease.CurrentIdentity
            ?? throw new InvalidOperationException("Receipt deploy recovery has no transaction identity.");
        var prior = ToOutputs(journal.PriorFiles);
        var expected = ToOutputs(journal.ExpectedFiles);
        var stagedFiles = ToOutputs(journal.StagedFiles);
        ValidateExpectedMap(expectedMod, expected);
        ValidateManagedMap(expectedMod, prior, requireExactOwner: true);
        ValidateStagedPrefix(stagedFiles, expected);
        ValidateJournalFileIdentities(journal.ExpectedFiles, requireIdentity: false);
        ValidateJournalFileIdentities(journal.PriorFiles, requireIdentity: true);
        ValidateJournalFileIdentities(journal.StagedFiles, requireIdentity: true);
        ValidatePendingFile(journal, stagedFiles, expected);
        var expectedFingerprint = VerifiedCommitQualifiedExpectedSet.Fingerprint(
            journal.Mod,
            journal.PublishedId,
            journal.SourceCommit,
            expected);
        if (journal.Schema != JournalSchema ||
            journal.Mod != expectedMod ||
            !System.Text.RegularExpressions.Regex.IsMatch(journal.PublishedId, "^[1-9][0-9]{0,19}$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(journal.SourceCommit, "^[0-9a-f]{40}$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(journal.AuthorityFingerprint, "^[0-9a-f]{64}$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(journal.OutputFingerprint, "^[0-9a-f]{64}$") ||
            journal.OutputFingerprint != expectedFingerprint ||
            journal.PublishedId != Path.GetFileName(locator.Target) ||
            !string.Equals(Normalize(journal.TargetDirectory), locator.Target, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Normalize(journal.ParentDirectory), locator.Parent, StringComparison.OrdinalIgnoreCase) ||
            journal.OwnerSid != CurrentUserSid() ||
            (current.Mod != null &&
             !string.Equals(journal.OwnerMod, current.Mod, StringComparison.OrdinalIgnoreCase)) ||
            (authorization != null && !PathEquals(journal.ProjectRoot, current.ProjectRoot)) ||
            !System.Text.RegularExpressions.Regex.IsMatch(journal.LeaseId, "^[0-9a-f]{32}$") ||
            journal.OwnerPid <= 0 ||
            journal.OwnerStartUtcTicks <= 0 ||
            journal.OwnerSessionId < 0 ||
            string.IsNullOrWhiteSpace(journal.Action) ||
            journal.State is not ("prepared" or "staging" or "staged" or
                "backed_up" or "installed" or "verified" or "cleanup" or
                "rollback_cleanup" or "prior_restore_prepared" or
                "prior_quarantine_prepared" or "prior_quarantined" or
                "prior_reconstructing" or "prior_reconstructed" or
                "manual_review"))
            throw new InvalidDataException(
                "Interrupted receipt-deploy journal does not match its owner scope or canonical target.");
        if (authorization != null)
        {
            if (journal.Mod != authorization.Mod ||
                journal.PublishedId != authorization.PublishedId ||
                journal.SourceCommit != authorization.SourceCommit ||
                journal.OutputFingerprint != authorization.OutputFingerprint ||
                !MapEquals(expected, authorization.Files) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(journal.AuthorityFingerprint),
                    Encoding.ASCII.GetBytes(authorization.AuthorityFingerprint)))
                throw new InvalidDataException(
                    "Interrupted receipt-deploy journal does not match the exact receipt authority.");
        }
        if (MachineTransactionLease.ProcessMatches(journal.OwnerPid, journal.OwnerStartUtcTicks))
        {
            var currentProcessOwner = journal.OwnerPid == current.OwnerPid &&
                journal.OwnerStartUtcTicks == current.OwnerStartUtcTicks;
            if (!currentProcessOwner || journal.OwnerSessionId != current.SessionId)
                throw new IOException(
                    $"Interrupted receipt-deploy journal owner PID {journal.OwnerPid} is still active in another lease/session.");
            // The authenticated current machine-global lease proves any
            // different lease identity from this same process is retired. A
            // nested active lease reuses the current identity, so it is never
            // misclassified as a retired retry.
        }

        RequireSupportedExactSetFileSystem(locator.Parent, "recovery destination");
        var parentIdentity = ImmutableBundleSourceLease.InspectDirectory(locator.Parent);
        RequireIdentity(parentIdentity, journal.ParentIdentity.ToPhysical(locator.Parent), "journal parent");
        if (prior.Count == 0 || journal.PriorIdentity == null)
            throw new InvalidDataException("Interrupted receipt-deploy journal lacks its prior identity/set.");
        var priorIdentity = journal.PriorIdentity.ToPhysical(locator.Target);
        var stageIdentity = journal.StageIdentity?.ToPhysical(paths.Stage);
        ValidateMembershipSealPlans(
            journal,
            paths,
            journal.ParentIdentity.ToPhysical(paths.Parent),
            journal.StageIdentity?.ToPhysical(paths.Target));
        return new RecoveryContext(
            locator,
            paths,
            journal,
            prior,
            expected,
            stagedFiles,
            priorIdentity,
            stageIdentity);
    }

    private static void RequireSupportedRecoverySchema(DeployJournal journal)
    {
        if (journal.Schema == JournalSchema) return;
        throw new InvalidDataException(
            $"Pre-release receipt-deploy schema-{journal.Schema} journal predates the NTFS membership-seal authority and is preserved without recovery mutation.");
    }

    private static void RecoverValidatedJournal(
        RecoveryContext recovery,
        ExactJournalLease journalLease,
        Action<string>? log)
    {
        var paths = recovery.Paths;
        var journal = recovery.Journal;
        var prior = recovery.Prior;
        var expected = recovery.Expected;
        var stagedFiles = recovery.Staged;
        var priorIdentity = recovery.PriorIdentity;
        var stageIdentity = recovery.StageIdentity;
        journalLease.RequireVersion(journal, "validated recovery journal authority");
        if (journal.State == "manual_review")
            throw new InvalidDataException(
                "Interrupted receipt-deploy journal requires manual review; no automatic mutation is authorized.");
        // The parent membership seal denies DELETE while the interrupted
        // transaction is authoritative. Restore both journaled seals before
        // opening any DELETE-capable directory lease; relying on a permissive
        // grandparent FILE_DELETE_CHILD grant makes crash recovery contingent
        // on an unrelated ancestor DACL.
        using var restoredMembershipSeals = RestoreRecordedMembershipSeals(
            journal,
            paths,
            journal.ParentIdentity.ToPhysical(paths.Parent),
            journal.StageIdentity?.ToPhysical(paths.Target));
        using var parentLease = ExactDirectoryLease.OpenExisting(
            paths.Parent,
            journal.ParentIdentity.ToPhysical(paths.Parent),
            "interrupted Workshop parent");
        parentLease.RequireCurrentPath("interrupted Workshop parent after membership-seal restoration");
        if (journal.ParentMembershipSeal != null && journal.State != "cleanup")
        {
            using var sealedReplacementLease = ExactDirectoryLease.OpenExisting(
                paths.Target,
                stageIdentity!,
                "restored seal-plan replacement target");
            using var sealedReplacementProof = ExactDirectorySnapshotLease.Capture(
                sealedReplacementLease,
                journal.Mod,
                requireExactOwner: true);
            RequireExact(
                sealedReplacementProof.Snapshot.Files,
                expected,
                "restored seal-plan replacement target");
            restoredMembershipSeals!.RequireOriginal();
            parentLease.RequireCurrentPath(
                "restored seal-plan parent before retirement");
            sealedReplacementProof.RequireCurrentNamespace(
                sealedReplacementLease,
                "restored seal-plan replacement target");
            journal.ParentMembershipSeal = null;
            journal.TargetMembershipSeal = null;
            WriteJournal(
                paths.Journal,
                journal,
                replace: true,
                parentLease,
                journalLease);
            Checkpoint("membership-seals-cleared-before-rollback");
        }
        // Directory deletion remains pending while either seal handle is
        // open. Their exact-original proof is complete at this point; release
        // them before recovery starts any rename or delete.
        restoredMembershipSeals?.Dispose();
        if (journal.State.StartsWith("prior_", StringComparison.Ordinal))
        {
            var priorSnapshot = new ExactDirectorySnapshot(
                priorIdentity,
                prior,
                ToPhysicalMap(journal.PriorFiles, paths.Target, requireIdentity: true));
            ReconstructPriorAndRequireManualReview(
                paths,
                journal,
                journal.Mod,
                priorSnapshot,
                parentLease,
                journalLease,
                new InvalidDataException(
                    "Interrupted mixed-prior reconstruction resumed from its durable journal."));
        }
        var targetExists = Directory.Exists(paths.Target);
        var backupExists = Directory.Exists(paths.Backup);
        var stageExists = Directory.Exists(paths.Stage);
        var quarantineExists = Directory.Exists(paths.Quarantine);
        RefuseDirectoryPathImpersonation(paths.Target, targetExists, "target");
        RefuseDirectoryPathImpersonation(paths.Backup, backupExists, "backup");
        RefuseDirectoryPathImpersonation(paths.Stage, stageExists, "stage");
        ExactDirectoryLease? stageLease = null;
        ExactDirectoryLease? priorTargetLease = null;
        ExactDirectorySnapshotLease? priorTargetProof = null;
        ExactDirectorySnapshotLease? replacementProof = null;
        Exception? preservedQuarantineFailure = null;
        try
        {
        if (quarantineExists && journal.QuarantineIdentity != null &&
            PathHasRecordedIdentity(paths.Quarantine, journal.QuarantineIdentity))
        {
            RecoverQuarantinedReplacement(
                paths,
                journal,
                prior,
                priorIdentity,
                stageIdentity,
                parentLease,
                targetExists,
                backupExists);
        }
        if (stageExists && stageIdentity == null)
            throw new InvalidDataException(
                "Unidentified precommitted stage is preserved; its name or emptiness is not deletion authority.");
        else if (stageExists && stageIdentity != null && journal.State != "rollback_cleanup")
        {
            stageLease = ExactDirectoryLease.OpenExisting(
                paths.Stage,
                stageIdentity,
                "recorded staging directory");
            NormalizeRecordedStage(
                stageLease,
                journal,
                stagedFiles,
                journal.StagedFiles,
                expected);
        }
        if (journal.State == "cleanup")
        {
            if (!targetExists || stageExists || stageIdentity == null)
                throw new InvalidDataException("Interrupted cleanup has an ambiguous namespace shape.");
            using var installedLease = ExactDirectoryLease.OpenExisting(
                paths.Target,
                stageIdentity,
                "interrupted cleaned target");
            using var installedProof = ExactDirectorySnapshotLease.Capture(
                installedLease,
                journal.Mod,
                requireExactOwner: true);
            RequireExact(installedProof.Snapshot.Files, expected, "interrupted cleaned target");
            if (backupExists)
            {
                using var cleanupBackup = ExactDirectoryLease.OpenExisting(
                    paths.Backup,
                    priorIdentity,
                    "interrupted cleanup backup");
                DeleteRemainingExactDirectory(
                    cleanupBackup,
                    prior,
                    ToPhysicalMap(journal.PriorFiles, paths.Backup, requireIdentity: true),
                    "interrupted cleanup backup");
            }
            installedLease.RequireCurrentPath("interrupted finalized target");
            RequireExact(installedProof.Snapshot.Files, expected, "interrupted finalized target");
            installedProof.RequireCurrentNamespace(installedLease, "interrupted finalized target");
            RetireJournalAfterStableMembership(
                paths.Journal,
                journal,
                journalLease,
                parentLease,
                installedLease,
                installedProof,
                expected,
                "interrupted finalized target");
            TryLog(log, "[receipt-deploy] finalized an interrupted verified local deployment");
            return;
        }
        if (backupExists)
        {
            priorTargetLease = ExactDirectoryLease.OpenExisting(
                paths.Backup,
                priorIdentity,
                "interrupted backup");
            priorTargetProof = ExactDirectorySnapshotLease.Capture(
                priorTargetLease,
                journal.Mod,
                requireExactOwner: true);
            RequireExact(priorTargetProof.Snapshot.Files, prior, "interrupted backup");
            if (targetExists)
            {
                if (stageIdentity == null || stageExists)
                    throw new InvalidDataException("Interrupted installed target has an ambiguous stage shape.");
                stageLease = ExactDirectoryLease.OpenExisting(
                    paths.Target,
                    stageIdentity,
                    "interrupted replacement");
                try
                {
                    replacementProof = ExactDirectorySnapshotLease.Capture(
                        stageLease,
                        journal.Mod,
                        requireExactOwner: true);
                    RequireExact(replacementProof.Snapshot.Files, expected, "interrupted replacement");
                    Checkpoint("recovery-replacement-pinned");
                    replacementProof.Dispose();
                    replacementProof = null;
                    stageLease.RenameTo(
                        parentLease,
                        paths.Stage,
                        "interrupted replacement rollback stage");
                    replacementProof = ExactDirectorySnapshotLease.Capture(
                        stageLease,
                        journal.Mod,
                        requireExactOwner: true);
                    RequireExact(replacementProof.Snapshot.Files, expected, "interrupted rollback stage");
                    stageExists = true;
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
                        "interrupted mixed replacement quarantine");
                    Checkpoint("recovery-replacement-quarantined");
                    preservedQuarantineFailure = membershipFailure;
                }
            }
            Checkpoint("recovery-backup-pinned");
            priorTargetProof.Dispose();
            priorTargetProof = null;
            priorTargetLease.RenameTo(
                parentLease,
                paths.Target,
                "restored prior deployment");
            priorTargetProof = ExactDirectorySnapshotLease.Capture(
                priorTargetLease,
                journal.Mod,
                requireExactOwner: true);
            priorTargetLease.RequireCurrentPath("restored prior deployment");
            RequireExact(priorTargetProof.Snapshot.Files, prior, "restored prior deployment");
            priorTargetProof.RequireCurrentNamespace(priorTargetLease, "restored prior deployment");
            targetExists = true;
            if (preservedQuarantineFailure != null)
                throw new InvalidDataException(
                    "Interrupted mixed replacement was preserved in its journal-bound quarantine and the exact prior deployment was restored; manual review is required.",
                    preservedQuarantineFailure);
        }

        if (!targetExists)
            throw new InvalidDataException("Interrupted receipt-deploy journal has no recoverable prior target.");
        if (priorTargetLease == null)
        {
            priorTargetLease = ExactDirectoryLease.OpenExisting(
                paths.Target,
                priorIdentity,
                "interrupted prior target");
            priorTargetProof = ExactDirectorySnapshotLease.Capture(
                priorTargetLease,
                journal.Mod,
                requireExactOwner: true);
            RequireExact(priorTargetProof.Snapshot.Files, prior, "interrupted prior target");
        }
        if (stageExists)
        {
            if (stageIdentity == null)
                throw new InvalidDataException("Interrupted stage has no authenticated directory identity.");
            stageLease ??= ExactDirectoryLease.OpenExisting(
                paths.Stage,
                stageIdentity,
                "interrupted exact staging set");
            if (journal.State != "rollback_cleanup")
            {
                journal.State = "rollback_cleanup";
                WriteJournal(
                    paths.Journal,
                    journal,
                    replace: true,
                    parentLease,
                    journalLease);
            }
            replacementProof?.Dispose();
            replacementProof = null;
            DeleteRemainingExactDirectory(
                stageLease,
                stagedFiles,
                ToPhysicalMap(journal.StagedFiles, paths.Stage, requireIdentity: true),
                "interrupted exact staging set");
            stageLease = null;
        }
        priorTargetLease.RequireCurrentPath("recovered prior target");
        RequireExact(priorTargetProof!.Snapshot.Files, prior, "recovered prior target");
        priorTargetProof.RequireCurrentNamespace(priorTargetLease, "recovered prior target");
        RetireJournalAfterStableMembership(
            paths.Journal,
            journal,
            journalLease,
            parentLease,
            priorTargetLease,
            priorTargetProof!,
            prior,
            "recovered prior target");
        TryLog(log, "[receipt-deploy] restored an interrupted prior local deployment");
        }
        finally
        {
            replacementProof?.Dispose();
            priorTargetProof?.Dispose();
            stageLease?.Dispose();
            priorTargetLease?.Dispose();
        }
    }

    private static void RecoverQuarantinedReplacement(
        TransactionPaths paths,
        DeployJournal journal,
        IReadOnlyList<CommitQualifiedOutputFile> prior,
        PhysicalDirectoryIdentity priorIdentity,
        PhysicalDirectoryIdentity? stageIdentity,
        ExactDirectoryLease parentLease,
        bool targetExists,
        bool backupExists)
    {
        if (journal.State == "cleanup" || stageIdentity == null ||
            journal.QuarantineIdentity == null)
            throw new InvalidDataException(
                "Receipt-deploy quarantine is incompatible with durable cleanup or lacks its recorded replacement identity.");
        var quarantineIdentity = journal.QuarantineIdentity.ToPhysical(paths.Quarantine);
        RequireIdentity(quarantineIdentity, stageIdentity with { FinalPath = paths.Quarantine },
            "preserved replacement quarantine identity");
        using var quarantineLease = ExactDirectoryLease.OpenExisting(
            paths.Quarantine,
            quarantineIdentity,
            "preserved replacement quarantine");
        quarantineLease.RequireCurrentPath("preserved replacement quarantine");

        if (backupExists)
        {
            if (targetExists)
                throw new InvalidDataException(
                    "Receipt-deploy quarantine has both a target and backup; preserving the ambiguous namespace.");
            using var priorLease = ExactDirectoryLease.OpenExisting(
                paths.Backup,
                priorIdentity,
                "quarantine rollback backup");
            using var priorProof = ExactDirectorySnapshotLease.Capture(
                priorLease,
                journal.Mod,
                requireExactOwner: true);
            RequireExact(priorProof.Snapshot.Files, prior, "quarantine rollback backup");
            priorProof.Dispose();
            priorLease.RenameTo(parentLease, paths.Target, "quarantine restored prior deployment");
            using var restored = ExactDirectorySnapshotLease.Capture(
                priorLease,
                journal.Mod,
                requireExactOwner: true);
            RequireExact(restored.Snapshot.Files, prior, "quarantine restored prior deployment");
            restored.RequireCurrentNamespace(priorLease, "quarantine restored prior deployment");
        }
        else
        {
            if (!targetExists)
                throw new InvalidDataException(
                    "Receipt-deploy quarantine has no recoverable prior target or backup.");
            using var priorLease = ExactDirectoryLease.OpenExisting(
                paths.Target,
                priorIdentity,
                "quarantine restored prior target");
            using var priorProof = ExactDirectorySnapshotLease.Capture(
                priorLease,
                journal.Mod,
                requireExactOwner: true);
            RequireExact(priorProof.Snapshot.Files, prior, "quarantine restored prior target");
            priorProof.RequireCurrentNamespace(priorLease, "quarantine restored prior target");
        }

        throw new InvalidDataException(
            "A mixed replacement is preserved in its journal-bound quarantine; the exact prior deployment is restored, but manual review is required.");
    }

    private static void RefuseDirectoryPathImpersonation(
        string path,
        bool directoryExists,
        string context)
    {
        if (!directoryExists && File.Exists(path))
            throw new InvalidDataException(
                $"Receipt-deploy {context} path is occupied by a non-directory entry.");
    }

    private static void RefuseUnjournaledArtifacts(TransactionLocator locator)
    {
        var prefix = locator.ArtifactPrefix + ".";
        var artifactPattern = "^" +
            System.Text.RegularExpressions.Regex.Escape(locator.ArtifactPrefix) +
            "(?:\\.[0-9a-f]{32}\\.(?:stage|backup|quarantine(?:\\.[0-9a-f]{32})?|restore(?:\\.[0-9a-f]{32})?)|" +
            "\\.journal\\.json(?:\\.retiring-[0-9a-f]{32}|\\.tmp-.+)?)$";
        var artifacts = new List<string>();
        var scanned = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     locator.Parent,
                     locator.ArtifactPrefix + ".*",
                     SearchOption.TopDirectoryOnly))
        {
            if (++scanned > MaximumManagedFiles)
                throw new InvalidDataException(
                    "Workshop receipt-deploy artifact inventory exceeds its safety bound.");
            var name = Path.GetFileName(path);
            if (name.StartsWith(prefix, StringComparison.Ordinal) &&
                System.Text.RegularExpressions.Regex.IsMatch(name, artifactPattern))
            {
                artifacts.Add(path);
            }
        }
        if (artifacts.Count != 0)
            throw new InvalidDataException(
                "Unjournaled/unowned receipt-deploy artifact exists; refusing to infer ownership.");
    }

    private static void ValidateManagedMap(
        string modName,
        IReadOnlyList<CommitQualifiedOutputFile> files,
        bool requireExactOwner)
    {
        if (files.Count == 0 || files.Count > 4096)
            throw new InvalidDataException("Journal managed set is empty or oversized.");
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long aggregate = 0;
        foreach (var file in files)
        {
            if (!IsCanonicalManagedOutput(file.Name) || file.Length < 0 ||
                !System.Text.RegularExpressions.Regex.IsMatch(file.Sha256, "^[0-9a-f]{64}$") ||
                !exact.Add(file.Name) || !folded.Add(file.Name))
                throw new InvalidDataException("Journal managed set is noncanonical.");
            aggregate = checked(aggregate + file.Length);
        }
        if (aggregate > 32L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Journal managed set exceeds the 32-GiB bound.");
        if (requireExactOwner &&
            (files.Count(file => file.Name.EndsWith(".mod", StringComparison.Ordinal)) != 1 ||
             !exact.Contains($"{modName}.mod")))
            throw new InvalidDataException("Journal managed set has ambiguous destination ownership.");
    }

    private static void ValidateStagedPrefix(
        IReadOnlyList<CommitQualifiedOutputFile> staged,
        IReadOnlyList<CommitQualifiedOutputFile> expected)
    {
        if (staged.Count > expected.Count)
            throw new InvalidDataException("Journal staged set exceeds its expected set.");
        var expectedMap = expected.ToDictionary(file => file.Name, StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in staged)
        {
            if (!names.Add(file.Name) || !expectedMap.TryGetValue(file.Name, out var match) ||
                file.Length != match.Length || file.Sha256 != match.Sha256)
                throw new InvalidDataException("Journal staged set is not an exact expected subset.");
        }
    }

    private static void ValidatePendingFile(
        DeployJournal journal,
        IReadOnlyList<CommitQualifiedOutputFile> staged,
        IReadOnlyList<CommitQualifiedOutputFile> expected)
    {
        var pending = journal.PendingFile;
        if (journal.State == "prepared" &&
            (journal.StageIdentity != null || staged.Count != 0 || pending != null))
            throw new InvalidDataException("Prepared journal already claims staged state.");
        if (journal.State == "rollback_cleanup")
        {
            if (pending != null)
                throw new InvalidDataException("Rollback-cleanup journal retains a pending stage file.");
            return;
        }
        if (journal.State is "staged" or "backed_up" or "installed" or "verified" or
            "cleanup" or "prior_restore_prepared" or "prior_quarantine_prepared" or
            "prior_quarantined" or "prior_reconstructing" or "prior_reconstructed" or
            "manual_review")
        {
            if (pending != null || !MapEquals(staged, expected))
                throw new InvalidDataException("Committed stage journal is incomplete.");
            return;
        }
        if (pending == null) return;
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                pending.TempName,
                "^\\.vmblauncher-receipt-file-[0-9a-f]{32}\\.tmp$") ||
            staged.Any(file => file.Name == pending.CanonicalName) ||
            (pending.TempVolumeSerialNumber.HasValue != pending.TempFileIdLow.HasValue ||
             pending.TempFileIdLow.HasValue != pending.TempFileIdHigh.HasValue))
            throw new InvalidDataException("Pending stage-file identity is noncanonical.");
        var match = expected.SingleOrDefault(file => file.Name == pending.CanonicalName);
        if (match == null || match.Length != pending.Length || match.Sha256 != pending.Sha256)
            throw new InvalidDataException("Pending stage-file proof differs from the expected set.");
    }

    private static void RequireExact(
        IReadOnlyList<CommitQualifiedOutputFile> actual,
        IReadOnlyList<CommitQualifiedOutputFile> expected,
        string context)
    {
        if (!MapEquals(actual, expected))
            throw new InvalidDataException(
                $"{context} does not match the complete filename/length/SHA-256 output set.");
    }

    private static bool MapEquals(
        IReadOnlyList<CommitQualifiedOutputFile> left,
        IReadOnlyList<CommitQualifiedOutputFile> right)
    {
        if (left.Count != right.Count) return false;
        var map = right.ToDictionary(file => file.Name, StringComparer.Ordinal);
        return left.All(file =>
            map.TryGetValue(file.Name, out var expected) &&
            file.Length == expected.Length &&
            file.Sha256 == expected.Sha256);
    }

    private static List<DeployJournalFile> FromOutputs(
        IReadOnlyList<CommitQualifiedOutputFile> files) =>
        files.OrderBy(file => file.Name, StringComparer.Ordinal)
            .Select(file => new DeployJournalFile
            {
                Name = file.Name,
                Length = file.Length,
                Sha256 = file.Sha256,
            })
            .ToList();

    private static List<DeployJournalFile> FromSnapshot(ExactDirectorySnapshot snapshot) =>
        snapshot.Files.OrderBy(file => file.Name, StringComparer.Ordinal)
            .Select(file =>
            {
                var identity = snapshot.FileIdentities[file.Name];
                return new DeployJournalFile
                {
                    Name = file.Name,
                    Length = file.Length,
                    Sha256 = file.Sha256,
                    VolumeSerialNumber = identity.VolumeSerialNumber,
                    FileIdLow = identity.FileIdLow,
                    FileIdHigh = identity.FileIdHigh,
                };
            })
            .ToList();

    private static IReadOnlyList<CommitQualifiedOutputFile> ToOutputs(
        IReadOnlyList<DeployJournalFile> files)
    {
        if (files.Count > MaximumManagedFiles)
            throw new InvalidDataException(
                "Receipt-deploy journal output inventory exceeds its safety bound.");
        return files.Select(file => new CommitQualifiedOutputFile(
                file.Name,
                file.Length,
                file.Sha256))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateJournalFileIdentities(
        IReadOnlyList<DeployJournalFile> files,
        bool requireIdentity)
    {
        foreach (var file in files)
        {
            var complete = file.VolumeSerialNumber.HasValue &&
                file.FileIdLow.HasValue &&
                file.FileIdHigh.HasValue;
            var absent = !file.VolumeSerialNumber.HasValue &&
                !file.FileIdLow.HasValue &&
                !file.FileIdHigh.HasValue;
            if ((requireIdentity && !complete) || (!requireIdentity && !absent) ||
                (!complete && !absent))
                throw new InvalidDataException(
                    "Receipt-deploy journal per-leaf physical identity proof is incomplete or misplaced.");
        }
    }

    private static IReadOnlyDictionary<string, PhysicalDirectoryIdentity> ToPhysicalMap(
        IReadOnlyList<DeployJournalFile> files,
        string directory,
        bool requireIdentity)
    {
        ValidateJournalFileIdentities(files, requireIdentity);
        var root = Normalize(directory);
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, PhysicalDirectoryIdentity>(
            files.ToDictionary(
                file => file.Name,
                file => new PhysicalDirectoryIdentity(
                    file.VolumeSerialNumber ?? 0,
                    file.FileIdLow ?? 0,
                    file.FileIdHigh ?? 0,
                    Path.Combine(root, file.Name)),
                StringComparer.Ordinal));
    }

    private static bool IsCanonicalOutputName(string name, string modName) =>
        name == $"{modName}.mod" ||
        System.Text.RegularExpressions.Regex.IsMatch(name, "^[0-9a-f]{16}\\.mod_bundle$");

    private static bool IsCanonicalManagedOutput(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9_]+\\.mod$") ||
        System.Text.RegularExpressions.Regex.IsMatch(name, "^[0-9a-f]{16}\\.mod_bundle$");

    private static void Checkpoint(string name)
    {
#if VMBLAUNCHER_TEST_HOOKS
        TransitionForTest?.Invoke(name);
#endif
    }

    private static bool BypassesAutomaticRecovery(Exception exception)
    {
#if VMBLAUNCHER_TEST_HOOKS
        return exception is LocalDeploySimulatedCrashForTest;
#else
        return false;
#endif
    }

    private static void TryLog(Action<string>? log, string message)
    {
        try { log?.Invoke(message); }
        catch { /* A reporting sink cannot reverse a committed filesystem transaction. */ }
    }

    private static void RequireIdentity(
        PhysicalDirectoryIdentity actual,
        PhysicalDirectoryIdentity expected,
        string context)
    {
        if (!actual.SameObject(expected))
            throw new InvalidDataException(
                $"{context} no longer names the recorded volume/file identity.");
    }

    private static bool PathEquals(string? left, string? right)
    {
        if (left == null || right == null) return left == right;
        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string CurrentUserSid() =>
        WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("Current Windows identity has no SID.");

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

}

#if VMBLAUNCHER_TEST_HOOKS
internal sealed class LocalDeploySimulatedCrashForTest : Exception
{
    internal LocalDeploySimulatedCrashForTest(string checkpoint) :
        base($"Simulated receipt-deploy crash after {checkpoint}.") { }
}
#endif
