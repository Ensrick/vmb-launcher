using System.IO;
using System.Text.Json.Serialization;

namespace VmbLauncher.Services;

/// <summary>Bounded durable model for one local exact-set transaction.</summary>
internal static partial class LocalExactSetDeployment
{
    private sealed class DeployJournal
    {
        [JsonPropertyName("schema")] public int Schema { get; set; }
        [JsonPropertyName("operation_id")] public string OperationId { get; set; } = "";
        [JsonPropertyName("lease_id")] public string LeaseId { get; set; } = "";
        [JsonPropertyName("owner_pid")] public int OwnerPid { get; set; }
        [JsonPropertyName("owner_start_utc_ticks")] public long OwnerStartUtcTicks { get; set; }
        [JsonPropertyName("owner_session_id")] public int OwnerSessionId { get; set; }
        [JsonPropertyName("owner_sid")] public string OwnerSid { get; set; } = "";
        [JsonPropertyName("action")] public string Action { get; set; } = "";
        [JsonPropertyName("owner_mod")] public string? OwnerMod { get; set; }
        [JsonPropertyName("project_root")] public string? ProjectRoot { get; set; }
        [JsonPropertyName("mod")] public string Mod { get; set; } = "";
        [JsonPropertyName("published_id")] public string PublishedId { get; set; } = "";
        [JsonPropertyName("source_commit")] public string SourceCommit { get; set; } = "";
        [JsonPropertyName("authority_fingerprint")] public string AuthorityFingerprint { get; set; } = "";
        [JsonPropertyName("output_fingerprint")] public string OutputFingerprint { get; set; } = "";
        [JsonPropertyName("state")] public string State { get; set; } = "";
        [JsonPropertyName("parent_directory")] public string ParentDirectory { get; set; } = "";
        [JsonPropertyName("target_directory")] public string TargetDirectory { get; set; } = "";
        [JsonPropertyName("stage_directory")] public string StageDirectory { get; set; } = "";
        [JsonPropertyName("backup_directory")] public string BackupDirectory { get; set; } = "";
        [JsonPropertyName("quarantine_directory")] public string QuarantineDirectory { get; set; } = "";
        [JsonPropertyName("restore_directory")] public string RestoreDirectory { get; set; } = "";
        [JsonPropertyName("retirement_journal_path")] public string RetirementJournalPath { get; set; } = "";
        [JsonPropertyName("journal_identity")] public DeployDirectoryIdentity? JournalIdentity { get; set; }
        [JsonPropertyName("parent_identity")] public DeployDirectoryIdentity ParentIdentity { get; set; } = new();
        [JsonPropertyName("prior_identity")] public DeployDirectoryIdentity? PriorIdentity { get; set; }
        [JsonPropertyName("stage_identity")] public DeployDirectoryIdentity? StageIdentity { get; set; }
        [JsonPropertyName("quarantine_identity")] public DeployDirectoryIdentity? QuarantineIdentity { get; set; }
        [JsonPropertyName("restore_identity")] public DeployDirectoryIdentity? RestoreIdentity { get; set; }
        [JsonPropertyName("prior_files")] public List<DeployJournalFile> PriorFiles { get; set; } = new();
        [JsonPropertyName("expected_files")] public List<DeployJournalFile> ExpectedFiles { get; set; } = new();
        [JsonPropertyName("staged_files")] public List<DeployJournalFile> StagedFiles { get; set; } = new();
        [JsonPropertyName("pending_file")] public DeployPendingFile? PendingFile { get; set; }
        [JsonPropertyName("parent_membership_seal")] public DeployMembershipSeal? ParentMembershipSeal { get; set; }
        [JsonPropertyName("target_membership_seal")] public DeployMembershipSeal? TargetMembershipSeal { get; set; }

        internal static DeployJournal Create(
            TransactionIdentity owner,
            string ownerSid,
            TransactionPaths paths,
            PhysicalDirectoryIdentity parentIdentity,
            ExactDirectorySnapshot prior,
            VerifiedCommitQualifiedExpectedSet authorization,
            IReadOnlyList<CommitQualifiedOutputFile> expected) => new()
        {
            Schema = JournalSchema,
            OperationId = paths.OperationId,
            LeaseId = owner.LeaseId,
            OwnerPid = owner.OwnerPid,
            OwnerStartUtcTicks = owner.OwnerStartUtcTicks,
            OwnerSessionId = owner.SessionId,
            OwnerSid = ownerSid,
            Action = owner.Action,
            OwnerMod = owner.Mod,
            ProjectRoot = owner.ProjectRoot,
            Mod = authorization.Mod,
            PublishedId = authorization.PublishedId,
            SourceCommit = authorization.SourceCommit,
            AuthorityFingerprint = authorization.AuthorityFingerprint,
            OutputFingerprint = authorization.OutputFingerprint,
            State = "prepared",
            ParentDirectory = paths.Parent,
            TargetDirectory = paths.Target,
            StageDirectory = paths.Stage,
            BackupDirectory = paths.Backup,
            QuarantineDirectory = paths.Quarantine,
            RestoreDirectory = paths.Restore,
            RetirementJournalPath = paths.Journal + ".retiring-" + paths.OperationId,
            ParentIdentity = DeployDirectoryIdentity.From(parentIdentity),
            PriorIdentity = DeployDirectoryIdentity.From(prior.Identity),
            PriorFiles = FromSnapshot(prior),
            ExpectedFiles = FromOutputs(expected),
        };
    }

    private sealed class DeployMembershipSeal
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("volume_serial_number")] public ulong VolumeSerialNumber { get; set; }
        [JsonPropertyName("file_id_low")] public ulong FileIdLow { get; set; }
        [JsonPropertyName("file_id_high")] public ulong FileIdHigh { get; set; }
        [JsonPropertyName("original_descriptor")] public string OriginalDescriptor { get; set; } = "";
        [JsonPropertyName("sealed_descriptor")] public string SealedDescriptor { get; set; } = "";
        [JsonPropertyName("denied_rights")] public int DeniedRights { get; set; }

        internal static DeployMembershipSeal From(
            LocalExactSetMembershipSeal.SecurityPlan plan) => new()
        {
            Path = plan.Path,
            VolumeSerialNumber = plan.VolumeSerialNumber,
            FileIdLow = plan.FileIdLow,
            FileIdHigh = plan.FileIdHigh,
            OriginalDescriptor = plan.OriginalDescriptor,
            SealedDescriptor = plan.SealedDescriptor,
            DeniedRights = plan.DeniedRights,
        };

        internal LocalExactSetMembershipSeal.SecurityPlan ToPlan() =>
            LocalExactSetMembershipSeal.ValidatePlan(new(
                Path,
                VolumeSerialNumber,
                FileIdLow,
                FileIdHigh,
                OriginalDescriptor,
                SealedDescriptor,
                DeniedRights));
    }

    private sealed class DeployDirectoryIdentity
    {
        [JsonPropertyName("volume_serial_number")] public ulong VolumeSerialNumber { get; set; }
        [JsonPropertyName("file_id_low")] public ulong FileIdLow { get; set; }
        [JsonPropertyName("file_id_high")] public ulong FileIdHigh { get; set; }

        internal static DeployDirectoryIdentity From(PhysicalDirectoryIdentity identity) => new()
        {
            VolumeSerialNumber = identity.VolumeSerialNumber,
            FileIdLow = identity.FileIdLow,
            FileIdHigh = identity.FileIdHigh,
        };

        internal PhysicalDirectoryIdentity ToPhysical(string finalPath) =>
            new(VolumeSerialNumber, FileIdLow, FileIdHigh, Normalize(finalPath));
    }

    private sealed class DeployJournalFile
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("length")] public long Length { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
        [JsonPropertyName("volume_serial_number")] public ulong? VolumeSerialNumber { get; set; }
        [JsonPropertyName("file_id_low")] public ulong? FileIdLow { get; set; }
        [JsonPropertyName("file_id_high")] public ulong? FileIdHigh { get; set; }
    }

    private sealed class DeployPendingFile
    {
        [JsonPropertyName("canonical_name")] public string CanonicalName { get; set; } = "";
        [JsonPropertyName("temp_name")] public string TempName { get; set; } = "";
        [JsonPropertyName("length")] public long Length { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
        [JsonPropertyName("temp_volume_serial_number")] public ulong? TempVolumeSerialNumber { get; set; }
        [JsonPropertyName("temp_file_id_low")] public ulong? TempFileIdLow { get; set; }
        [JsonPropertyName("temp_file_id_high")] public ulong? TempFileIdHigh { get; set; }
    }

    private sealed record ExactDirectorySnapshot(
        PhysicalDirectoryIdentity Identity,
        IReadOnlyList<CommitQualifiedOutputFile> Files,
        IReadOnlyDictionary<string, PhysicalDirectoryIdentity> FileIdentities);

    private sealed record RecoveryContext(
        TransactionLocator Locator,
        TransactionPaths Paths,
        DeployJournal Journal,
        IReadOnlyList<CommitQualifiedOutputFile> Prior,
        IReadOnlyList<CommitQualifiedOutputFile> Expected,
        IReadOnlyList<CommitQualifiedOutputFile> Staged,
        PhysicalDirectoryIdentity PriorIdentity,
        PhysicalDirectoryIdentity? StageIdentity);

    private sealed record TransactionPaths(
        string Target,
        string Parent,
        string Stage,
        string Backup,
        string Quarantine,
        string Restore,
        string Journal,
        string OperationId);

    private sealed record TransactionLocator(
        string Target,
        string Parent,
        string Journal,
        string ArtifactPrefix)
    {
        internal static TransactionLocator Create(string targetDirectory, string publishedId)
        {
            var target = Normalize(targetDirectory);
            if (!System.Text.RegularExpressions.Regex.IsMatch(publishedId, "^[1-9][0-9]{0,19}$") ||
                !string.Equals(Path.GetFileName(target), publishedId, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Receipt-authority target is not the exact published-id directory.");
            var parent = Normalize(Path.GetDirectoryName(target)
                ?? throw new InvalidDataException("Receipt-authority target has no parent."));
            var prefix = $".vmblauncher-receipt-deploy-{publishedId}";
            return new(target, parent, Path.Combine(parent, prefix + ".journal.json"), prefix);
        }

        internal TransactionPaths CreateOperation()
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var operation = Guid.NewGuid().ToString("N");
                var stage = Path.Combine(Parent, $"{ArtifactPrefix}.{operation}.stage");
                var backup = Path.Combine(Parent, $"{ArtifactPrefix}.{operation}.backup");
                var quarantine = Path.Combine(Parent, $"{ArtifactPrefix}.{operation}.quarantine");
                var restore = Path.Combine(Parent, $"{ArtifactPrefix}.{operation}.restore");
                if (!Directory.Exists(stage) && !Directory.Exists(backup) &&
                    !Directory.Exists(quarantine) && !File.Exists(quarantine) &&
                    !Directory.Exists(restore) && !File.Exists(restore))
                    return new(Target, Parent, stage, backup, quarantine, restore, Journal, operation);
            }
            throw new IOException("Could not allocate a unique receipt-deploy operation identity.");
        }

        internal TransactionPaths FromJournal(DeployJournal journal)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(journal.OperationId, "^[0-9a-f]{32}$"))
                throw new InvalidDataException("Receipt-deploy journal operation identity is invalid.");
            var expectedStage = Path.Combine(Parent, $"{ArtifactPrefix}.{journal.OperationId}.stage");
            var expectedBackup = Path.Combine(Parent, $"{ArtifactPrefix}.{journal.OperationId}.backup");
            var expectedQuarantine = Path.Combine(
                Parent,
                $"{ArtifactPrefix}.{journal.OperationId}.quarantine");
            var expectedRestore = Path.Combine(
                Parent,
                $"{ArtifactPrefix}.{journal.OperationId}.restore");
            var expectedRetirementJournal = Journal + ".retiring-" + journal.OperationId;
            static bool IsRecordedVariant(string actual, string initial)
            {
                var normalized = Normalize(actual);
                return string.Equals(normalized, initial, StringComparison.OrdinalIgnoreCase) ||
                    (normalized.StartsWith(initial + ".", StringComparison.OrdinalIgnoreCase) &&
                     System.Text.RegularExpressions.Regex.IsMatch(
                         normalized[(initial.Length + 1)..],
                         "^[0-9a-f]{32}$"));
            }
            if (!string.Equals(Normalize(journal.StageDirectory), expectedStage, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Normalize(journal.BackupDirectory), expectedBackup, StringComparison.OrdinalIgnoreCase) ||
                !IsRecordedVariant(journal.QuarantineDirectory, expectedQuarantine) ||
                !IsRecordedVariant(journal.RestoreDirectory, expectedRestore) ||
                !string.Equals(
                    Normalize(journal.RetirementJournalPath),
                    expectedRetirementJournal,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Receipt-deploy journal artifact path escapes its exact parent.");
            return new(
                Target,
                Parent,
                expectedStage,
                expectedBackup,
                Normalize(journal.QuarantineDirectory),
                Normalize(journal.RestoreDirectory),
                Journal,
                journal.OperationId);
        }
    }
}
