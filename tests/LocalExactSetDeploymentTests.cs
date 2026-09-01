using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

[CollectionDefinition("receipt-deploy-serial", DisableParallelization = true)]
public sealed class ReceiptDeploySerialCollection { }

[Collection("receipt-deploy-serial")]
public sealed class LocalExactSetDeploymentTests : MutationTestBase
{
    private const string Mod = "modx";
    private const string PublishedId = "123456789";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string NewBundle = "0123456789abcdef.mod_bundle";
    private const string OldBundle = "fedcba9876543210.mod_bundle";

    [Fact]
    public void Reconcile_ReplacesOwnedDirectoryWithCompleteExpectedSet()
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();

        var result = LocalExactSetDeployment.Reconcile(
            fixture.Target, fixture.Authorization(), source);

        Assert.True(result.Ok, result.Message);
        fixture.AssertTarget(fixture.Expected);
        Assert.False(File.Exists(Path.Combine(fixture.Target, OldBundle)));
        fixture.AssertNoTransactionArtifacts();
    }

    [Fact]
    public void CommittedDeploy_IsNotReportedFailedWhenTheLogSinkThrows()
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();

        var result = LocalExactSetDeployment.Reconcile(
            fixture.Target,
            fixture.Authorization(),
            source,
            _ => throw new IOException("planted log failure"));

        Assert.True(result.Ok, result.Message);
        fixture.AssertTarget(fixture.Expected);
        fixture.AssertNoTransactionArtifacts();
    }

    [Fact]
    public void StageAndTargetAreProvenOnTheSameVolumeBeforeCommit()
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        var observed = false;
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point != "stage-created") return;
            var stage = Directory.EnumerateDirectories(
                fixture.Parent,
                ".vmblauncher-receipt-deploy-*.stage").Single();
            var targetIdentity = ImmutableBundleSourceLease.InspectDirectory(fixture.Target);
            var stageIdentity = ImmutableBundleSourceLease.InspectDirectory(stage);
            var parentIdentity = ImmutableBundleSourceLease.InspectDirectory(fixture.Parent);
            Assert.Equal(targetIdentity.VolumeSerialNumber, stageIdentity.VolumeSerialNumber);
            Assert.Equal(parentIdentity.VolumeSerialNumber, stageIdentity.VolumeSerialNumber);
            observed = true;
        };
        RunOutcome result;
        try
        {
            result = LocalExactSetDeployment.Reconcile(
                fixture.Target,
                fixture.Authorization(),
                source);
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        Assert.True(observed);
        Assert.True(result.Ok, result.Message);
        fixture.AssertTarget(fixture.Expected);
        fixture.AssertNoTransactionArtifacts();
    }

    [Fact]
    public void HostedReceiptReplayOfTheSameExactSet_IsSafeAndIdempotent()
    {
        using var fixture = new Fixture();
        using (var source = fixture.Capture())
            Assert.True(LocalExactSetDeployment.Reconcile(
                fixture.Target, fixture.Authorization(), source).Ok);

        using var secondSource = fixture.Capture();
        var second = LocalExactSetDeployment.Reconcile(
            fixture.Target, fixture.Authorization(), secondSource);

        Assert.True(second.Ok, second.Message);
        fixture.AssertTarget(fixture.Expected);
        fixture.AssertNoTransactionArtifacts();
    }

    [Fact]
    public void HostedReceiptReplay_RefusesChangedSourceBytesWithoutTargetMutation()
    {
        using var fixture = new Fixture();
        using (var firstSource = fixture.Capture())
            Assert.True(LocalExactSetDeployment.Reconcile(
                fixture.Target,
                fixture.Authorization(),
                firstSource).Ok);
        File.WriteAllText(Path.Combine(fixture.Source, NewBundle), "changed source bytes");

        var error = Assert.Throws<InvalidDataException>(() => fixture.Capture());

        Assert.Contains("commit proof", error.Message, StringComparison.OrdinalIgnoreCase);
        fixture.AssertTarget(fixture.Expected);
        fixture.AssertNoTransactionArtifacts();
    }

    [Fact]
    public void Reconcile_DoesNotCreateAnAbsentWorkshopTarget()
    {
        using var fixture = new Fixture();
        Directory.Delete(fixture.Target, recursive: true);
        using var source = fixture.Capture();

        var result = LocalExactSetDeployment.Reconcile(
            fixture.Target, fixture.Authorization(), source);

        Assert.False(result.Ok);
        Assert.False(Directory.Exists(fixture.Target));
        fixture.AssertNoTransactionArtifacts();
    }

    [Fact]
    public void Reconcile_RefusesWhenPinnedSourceIsTheDestination()
    {
        using var fixture = new Fixture();
        foreach (var path in Directory.EnumerateFiles(fixture.Target)) File.Delete(path);
        foreach (var path in Directory.EnumerateFiles(fixture.Source))
            File.Copy(path, Path.Combine(fixture.Target, Path.GetFileName(path)));
        using var source = ImmutableBundleSourceLease.Capture(
            fixture.Target, Mod, fixture.Expected);

        var result = LocalExactSetDeployment.Reconcile(
            fixture.Target, fixture.Authorization(), source);

        Assert.False(result.Ok);
        Assert.Contains("identical", result.Message, StringComparison.OrdinalIgnoreCase);
        fixture.AssertTarget(fixture.Expected);
        fixture.AssertNoTransactionArtifacts();
    }

    [Theory]
    [InlineData("foreign.mod", false)]
    [InlineData("nested", true)]
    [InlineData("notes.txt", false)]
    public void Reconcile_RefusesAmbiguousOrUnknownDestinationWithoutMutation(
        string planted,
        bool directory)
    {
        using var fixture = new Fixture();
        var prior = fixture.TargetBytes();
        var path = Path.Combine(fixture.Target, planted);
        if (directory) Directory.CreateDirectory(path);
        else File.WriteAllText(path, "foreign");
        using var source = fixture.Capture();

        var result = LocalExactSetDeployment.Reconcile(
            fixture.Target, fixture.Authorization(), source);

        Assert.False(result.Ok);
        foreach (var entry in prior)
            Assert.Equal(entry.Value, File.ReadAllBytes(Path.Combine(fixture.Target, entry.Key)));
        Assert.True(directory ? Directory.Exists(path) : File.Exists(path));
        fixture.AssertNoTransactionArtifacts();
    }

    [Fact]
    public void Reconcile_RefusesHardLinkedManagedOutputsWithoutMutation()
    {
        using var fixture = new Fixture();
        var alias = Path.Combine(fixture.Target, "aaaaaaaaaaaaaaaa.mod_bundle");
        Assert.True(CreateHardLinkW(
            alias,
            Path.Combine(fixture.Target, OldBundle),
            IntPtr.Zero));
        using var source = fixture.Capture();

        var result = LocalExactSetDeployment.Reconcile(
            fixture.Target,
            fixture.Authorization(),
            source);

        Assert.False(result.Ok);
        Assert.Contains("hard-link", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(alias));
        Assert.True(File.Exists(Path.Combine(fixture.Target, OldBundle)));
        fixture.AssertNoTransactionArtifacts();
    }

    [Fact]
    public void SourceLease_RequiresAnExactTopLevelCensus()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Source, "extra.txt"), "extra");

        var error = Assert.Throws<InvalidDataException>(() => fixture.Capture());

        Assert.Contains("census", error.Message, StringComparison.OrdinalIgnoreCase);
        fixture.AssertTarget(fixture.Prior);
    }

    [Fact]
    public void SourceLease_EnforcesFileBoundDuringEnumeration()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < 4095; i++)
            File.WriteAllText(Path.Combine(fixture.Source, $"extra-{i:x4}.tmp"), "x");

        var error = Assert.Throws<InvalidDataException>(() => fixture.Capture());

        Assert.Contains("4096-file", error.Message, StringComparison.OrdinalIgnoreCase);
        fixture.AssertTarget(fixture.Prior);
        fixture.AssertNoTransactionArtifacts();
    }

    [Fact]
    public void SourceLease_RejectsMissingAndSameLengthWrongBytes()
    {
        using var fixture = new Fixture();
        var bundle = Path.Combine(fixture.Source, NewBundle);
        var original = File.ReadAllText(bundle);
        File.Delete(bundle);
        Assert.ThrowsAny<Exception>(() => fixture.Capture());
        File.WriteAllText(bundle, new string('x', original.Length));

        var error = Assert.Throws<InvalidDataException>(() => fixture.Capture());

        Assert.Contains("commit proof", error.Message, StringComparison.OrdinalIgnoreCase);
        fixture.AssertTarget(fixture.Prior);
    }

    [Fact]
    public void SourceLease_SecondCensusRejectsAnInjectedExtraEntry()
    {
        using var fixture = new Fixture();
        var injected = Path.Combine(fixture.Source, "injected.txt");
        ImmutableBundleSourceLease.CaptureTransitionForTest = point =>
        {
            if (point == "handles-open") File.WriteAllText(injected, "late");
        };
        try
        {
            var error = Assert.Throws<InvalidDataException>(() => fixture.Capture());
            Assert.Contains("census", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ImmutableBundleSourceLease.CaptureTransitionForTest = null;
        }
        Assert.True(File.Exists(injected));
        fixture.AssertTarget(fixture.Prior);
    }

    [Fact]
    public void SourceLease_PinsExpectedBytesAgainstWriteAndDelete()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Source, NewBundle);
        using (var source = fixture.Capture())
        {
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, "changed"));
            Assert.ThrowsAny<IOException>(() => File.Delete(path));
        }

        File.WriteAllText(path, "released");
        Assert.Equal("released", File.ReadAllText(path));
    }

    [Fact]
    public void DestinationCensus_EnforcesFileBoundDuringEnumeration()
    {
        using var fixture = new Fixture();
        var hashes = 0;
        LocalExactSetDeployment.SnapshotHashTransitionForTest = _ => hashes++;
        for (var i = 0; i < 4095; i++)
            File.WriteAllText(
                Path.Combine(fixture.Target, i.ToString("x16") + ".mod_bundle"),
                "x");
        using var source = fixture.Capture();

        var result = LocalExactSetDeployment.Reconcile(
            fixture.Target,
            fixture.Authorization(),
            source);

        Assert.False(result.Ok);
        Assert.Contains("4096-file", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, hashes);
        fixture.AssertNoTransactionArtifacts();
    }

    [Fact]
    public void DestinationCensus_EnforcesByteBoundBeforeHashingOversizedLeaf()
    {
        using var fixture = new Fixture();
        using (var oversized = new FileStream(
                   Path.Combine(fixture.Target, OldBundle),
                   FileMode.Open,
                   FileAccess.Write,
                   FileShare.None))
            oversized.SetLength(32L * 1024 * 1024 * 1024 + 1);
        using var source = fixture.Capture();

        var result = LocalExactSetDeployment.Reconcile(
            fixture.Target,
            fixture.Authorization(),
            source);

        Assert.False(result.Ok);
        Assert.Contains("32-GiB", result.Message, StringComparison.OrdinalIgnoreCase);
        fixture.AssertNoTransactionArtifacts();
    }

    [Fact]
    public void Reconcile_RechecksExpiryImmediatelyBeforeCommitAndRestoresPrior()
    {
        using var fixture = new Fixture();
        var valid = DateTime.UtcNow;
        var authorization = fixture.Authorization(valid.AddSeconds(1));
        using var source = fixture.Capture();
        var calls = 0;

        var result = LocalExactSetDeployment.Reconcile(
            fixture.Target,
            authorization,
            source,
            utcNow: () => ++calls == 1 ? valid : valid.AddMinutes(1));

        Assert.False(result.Ok);
        Assert.Contains("expired", result.Message, StringComparison.OrdinalIgnoreCase);
        fixture.AssertTarget(fixture.Prior);
        fixture.AssertNoTransactionArtifacts();
    }

    [Theory]
    [InlineData("journal-durable")]
    [InlineData("journal-initial-durable")]
    [InlineData("stage-created")]
    [InlineData("stage-file-prepared")]
    [InlineData("stage-temp-created")]
    [InlineData("stage-temp-partial-write")]
    [InlineData("stage-temp-flushed")]
    [InlineData("stage-file-renamed")]
    [InlineData("stage-file-copied")]
    [InlineData("stage-verified")]
    [InlineData("precommit-census-complete")]
    [InlineData("target-moved-before-journal")]
    [InlineData("target-backed-up")]
    [InlineData("replacement-moved-before-journal")]
    [InlineData("replacement-installed")]
    [InlineData("target-verified")]
    [InlineData("cleanup-durable")]
    [InlineData("cleanup-file-deleted")]
    [InlineData("cleanup-files-cleared")]
    [InlineData("backup-deleted")]
    [InlineData("journal-retirement-witness")]
    [InlineData("membership-seals-applied")]
    public void InterruptedCheckpoint_IsRecoveredThenFreshAttemptCompletes(string checkpoint)
    {
        using var fixture = new Fixture();
        using var firstSource = fixture.Capture();
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point == checkpoint)
                throw new LocalDeploySimulatedCrashForTest(point);
        };
        try
        {
            Assert.Throws<LocalDeploySimulatedCrashForTest>(() =>
                LocalExactSetDeployment.Reconcile(
                    fixture.Target, fixture.Authorization(), firstSource));
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        using var retrySource = fixture.Capture();
        var retry = LocalExactSetDeployment.Reconcile(
            fixture.Target, fixture.Authorization(), retrySource);

        Assert.True(retry.Ok, retry.Message);
        fixture.AssertTarget(fixture.Expected);
        fixture.AssertNoTransactionArtifacts();
    }

    [Theory]
    [InlineData("journal-initial-created-unidentified")]
    [InlineData("journal-initial-created")]
    [InlineData("journal-initial-sized")]
    [InlineData("journal-initial-payload-partial")]
    [InlineData("journal-initial-payload-flushed")]
    [InlineData("journal-initial-header-partial")]
    [InlineData("stage-directory-created-unrecorded")]
    [InlineData("stage-temp-created-unrecorded")]
    public void HardCrashBeforePhysicalIdentityDurability_PreservesAndBlocks(
        string checkpoint)
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        fixture.CrashAt(checkpoint, source);

        var recovered = LocalExactSetDeployment.RecoverInterruptedSafety(
            fixture.Parent,
            Mod);

        Assert.False(recovered.Ok);
        Assert.Contains(
            checkpoint.StartsWith("journal-initial", StringComparison.Ordinal)
                ? "journal"
                : "Unidentified",
            recovered.Message,
            StringComparison.OrdinalIgnoreCase);
        fixture.AssertTarget(fixture.Prior);
        Assert.Contains(
            Directory.EnumerateFileSystemEntries(fixture.Parent),
            path => Path.GetFileName(path).StartsWith(
                $".vmblauncher-receipt-deploy-{PublishedId}",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("stage-directory-created-unrecorded")]
    [InlineData("stage-temp-created-unrecorded")]
    public void OrdinaryFailureBeforePhysicalIdentityDurability_UsesOnlyTheLiveCreatingHandle(
        string checkpoint)
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        var injected = false;
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point != checkpoint || injected) return;
            injected = true;
            throw new IOException("ordinary pre-identity interruption");
        };
        RunOutcome result;
        try
        {
            result = LocalExactSetDeployment.Reconcile(
                fixture.Target,
                fixture.Authorization(),
                source);
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        Assert.True(injected);
        Assert.False(result.Ok);
        fixture.AssertTarget(fixture.Prior);
        fixture.AssertNoTransactionArtifacts();
    }

    [Fact]
    public void PreparedStageSubstitutionBeforeIdentityRecord_IsPreservedAndBlocks()
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        string? substituted = null;
        string? original = null;
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point != "stage-directory-created-unrecorded") return;
            substituted = Assert.Single(Directory.EnumerateDirectories(
                fixture.Parent,
                ".vmblauncher-receipt-deploy-*.stage"));
            original = substituted + ".original";
            Directory.Move(substituted, original);
            Directory.CreateDirectory(substituted);
            File.WriteAllText(Path.Combine(substituted, "human-owned.txt"), "preserve me");
        };
        RunOutcome result;
        try
        {
            result = LocalExactSetDeployment.Reconcile(
                fixture.Target,
                fixture.Authorization(),
                source);
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        Assert.False(result.Ok);
        Assert.NotNull(substituted);
        Assert.NotNull(original);
        Assert.Equal("preserve me", File.ReadAllText(Path.Combine(substituted!, "human-owned.txt")));
        Assert.True(Directory.Exists(original));
        fixture.AssertTarget(fixture.Prior);
        var recovery = LocalExactSetDeployment.RecoverInterruptedSafety(fixture.Parent, Mod);
        Assert.False(recovery.Ok);
        Assert.Equal("preserve me", File.ReadAllText(Path.Combine(substituted!, "human-owned.txt")));
    }

    [Fact]
    public void PendingTempSubstitutionWithoutRecordedIdentity_IsPreservedAndBlocks()
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        fixture.CrashAt("stage-temp-created-unrecorded", source);
        var stage = Assert.Single(Directory.EnumerateDirectories(
            fixture.Parent,
            ".vmblauncher-receipt-deploy-*.stage"));
        var temp = Assert.Single(Directory.EnumerateFiles(
            stage,
            ".vmblauncher-receipt-file-*.tmp"));
        File.Delete(temp);
        File.WriteAllText(temp, "substitute must survive");

        var recovery = LocalExactSetDeployment.RecoverInterruptedSafety(fixture.Parent, Mod);

        Assert.False(recovery.Ok);
        Assert.Contains("Unidentified", recovery.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("substitute must survive", File.ReadAllText(temp));
        fixture.AssertTarget(fixture.Prior);
    }

    [Theory]
    [InlineData("journal-slot-invalidated")]
    [InlineData("journal-slot-payload-partial")]
    [InlineData("journal-slot-payload-flushed")]
    [InlineData("journal-slot-header-partial")]
    [InlineData("journal-slot-durable")]
    public void InterruptedJournalSlotUpdate_UsesThePriorDurableState(string checkpoint)
    {
        using var fixture = new Fixture();
        using var firstSource = fixture.Capture();
        var occurrences = 0;
        var targetOccurrence = checkpoint == "journal-slot-invalidated" ? 3 : 2;
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point == checkpoint && ++occurrences == targetOccurrence)
                throw new LocalDeploySimulatedCrashForTest(point);
        };
        try
        {
            Assert.Throws<LocalDeploySimulatedCrashForTest>(() =>
                LocalExactSetDeployment.Reconcile(
                    fixture.Target,
                    fixture.Authorization(),
                    firstSource));
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        using var retrySource = fixture.Capture();
        var retry = LocalExactSetDeployment.Reconcile(
            fixture.Target,
            fixture.Authorization(),
            retrySource);

        Assert.True(retry.Ok, retry.Message);
        fixture.AssertTarget(fixture.Expected);
        fixture.AssertNoTransactionArtifacts();
    }

    [Fact]
    public void Recovery_RequiresTheExactReceiptAuthorityFingerprint()
    {
        using var fixture = new Fixture();
        using var firstSource = fixture.Capture();
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point == "stage-created")
                throw new LocalDeploySimulatedCrashForTest(point);
        };
        try
        {
            Assert.Throws<LocalDeploySimulatedCrashForTest>(() =>
                LocalExactSetDeployment.Reconcile(
                    fixture.Target,
                    fixture.Authorization(authorityFingerprint: new string('a', 64)),
                    firstSource));
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }
        var before = SnapshotTree(fixture.Parent);
        using (var foreignReceiptSource = fixture.Capture())
        {
            var refused = LocalExactSetDeployment.Reconcile(
                fixture.Target,
                fixture.Authorization(authorityFingerprint: new string('b', 64)),
                foreignReceiptSource);
            Assert.False(refused.Ok);
        }
        Assert.Equal(before, SnapshotTree(fixture.Parent));

        using var exactReceiptSource = fixture.Capture();
        var recovered = LocalExactSetDeployment.Reconcile(
            fixture.Target,
            fixture.Authorization(authorityFingerprint: new string('a', 64)),
            exactReceiptSource);
        Assert.True(recovered.Ok, recovered.Message);
        fixture.AssertTarget(fixture.Expected);
        fixture.AssertNoTransactionArtifacts();
    }

    [Theory]
    [InlineData("target-backed-up")]
    [InlineData("replacement-installed")]
    public void SafetyRecovery_RestoresPriorWithoutFreshReceiptOrSource(
        string checkpoint)
    {
        using var fixture = new Fixture();
        using (var source = fixture.Capture())
            fixture.CrashAt(checkpoint, source);
        Directory.Delete(fixture.Source, recursive: true);

        var recovered = LocalExactSetDeployment.RecoverInterruptedSafety(
            fixture.Parent,
            Mod);

        Assert.True(recovered.Ok, recovered.Message);
        fixture.AssertTarget(fixture.Prior);
        fixture.AssertNoTransactionArtifacts();
        Assert.False(Directory.Exists(fixture.Source));
    }

    [Theory]
    [InlineData("journal-slot-payload-flushed@2")]
    [InlineData("target-moved-before-journal")]
    [InlineData("membership-seals-planned")]
    [InlineData("parent-membership-seal-applied")]
    [InlineData("membership-seals-applied")]
    public void HardProcessDeath_IsRecoveredByAFreshLeaseWithoutReceiptOrSource(
        string checkpoint)
    {
        using var temp = new TempDir();
        var source = temp.CreateSubdir("source");
        var parent = temp.CreateSubdir("workshop");
        var target = temp.CreateSubdir(Path.Combine("workshop", PublishedId));
        File.WriteAllText(Path.Combine(source, Mod + ".mod"), "new descriptor");
        File.WriteAllText(Path.Combine(source, NewBundle), "new bundle bytes");
        File.WriteAllText(Path.Combine(target, Mod + ".mod"), "old descriptor");
        File.WriteAllText(Path.Combine(target, OldBundle), "old bundle bytes");
        var prior = Census(target);
        var worker = FindTransactionWorker();
        var mutex = @"Local\VMBLauncher.Tests.ReceiptDeploy." + Guid.NewGuid().ToString("N");
        var record = Path.Combine(temp.Path, "owner.json");
        var crashMarker = Path.Combine(temp.Path, "crashed.txt");

        using (var crash = StartTransactionWorker(
                   worker,
                   "receipt-deploy-owner-crash",
                   mutex,
                   record,
                   temp.Path,
                   crashMarker,
                   checkpoint))
        {
            WaitForWorkerMarker(crashMarker, crash);
            Assert.True(crash.WaitForExit(10_000), "receipt-deploy crash worker did not exit");
            Assert.NotEqual(0, crash.ExitCode);
            Assert.Equal(checkpoint.Split('@')[0], File.ReadAllText(crashMarker));
        }
        Assert.True(File.Exists(record));
        Directory.Delete(source, recursive: true);

        var recoveryMarker = Path.Combine(temp.Path, "recovered.txt");
        using (var recovery = StartTransactionWorker(
                   worker,
                   "receipt-deploy-recover",
                   mutex,
                   record,
                   temp.Path,
                   recoveryMarker,
                   release: ""))
        {
            WaitForWorkerMarker(recoveryMarker, recovery);
            Assert.True(recovery.WaitForExit(10_000), "receipt-deploy recovery worker did not exit");
            var detail = File.ReadAllText(recoveryMarker);
            Assert.True(
                recovery.ExitCode == 0,
                $"receipt-deploy recovery failed ({recovery.ExitCode}): {detail}\n{recovery.StandardError.ReadToEnd()}");
            Assert.StartsWith("True|", detail, StringComparison.Ordinal);
        }

        Assert.Equal(prior, Census(target));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(parent),
            path => Path.GetFileName(path).StartsWith(
                $".vmblauncher-receipt-deploy-{PublishedId}",
                StringComparison.Ordinal));
        Assert.False(File.Exists(record));
        Assert.False(Directory.Exists(source));
    }

    [Theory]
    [InlineData("cleanup-durable", false)]
    [InlineData("target-membership-seal-restored-before-parent", false)]
    [InlineData("parent-membership-seal-restored-before-reproof", false)]
    [InlineData("journal-retirement-witness", true)]
    public void HardProcessDeathDuringJournalRetirement_RecoversInstalledSet(
        string checkpoint,
        bool witnessExpected)
    {
        using var temp = new TempDir();
        var source = temp.CreateSubdir("source");
        var parent = temp.CreateSubdir("workshop");
        var target = temp.CreateSubdir(Path.Combine("workshop", PublishedId));
        File.WriteAllText(Path.Combine(source, Mod + ".mod"), "new descriptor");
        File.WriteAllText(Path.Combine(source, NewBundle), "new bundle bytes");
        File.WriteAllText(Path.Combine(target, Mod + ".mod"), "old descriptor");
        File.WriteAllText(Path.Combine(target, OldBundle), "old bundle bytes");
        var siblingDirectory = temp.CreateSubdir(@"workshop\unrelated");
        var siblingFile = temp.Write(@"workshop\unrelated\unrelated.txt", "unrelated");
        var expected = Census(source);
        var parentAcl = GetAcl(parent);
        var targetAcl = GetAcl(target);
        var priorDescriptorAcl = GetAcl(Path.Combine(target, Mod + ".mod"));
        var priorBundleAcl = GetAcl(Path.Combine(target, OldBundle));
        var siblingDirectoryAcl = GetAcl(siblingDirectory);
        var siblingFileAcl = GetAcl(siblingFile);
        var worker = FindTransactionWorker();
        var mutex = @"Local\VMBLauncher.Tests.ReceiptRetirement." +
            Guid.NewGuid().ToString("N");
        var record = Path.Combine(temp.Path, "owner.json");
        var crashMarker = Path.Combine(temp.Path, "crashed.txt");

        using (var crash = StartTransactionWorker(
                   worker,
                   "receipt-deploy-owner-crash",
                   mutex,
                   record,
                   temp.Path,
                   crashMarker,
                   checkpoint))
        {
            WaitForWorkerMarker(crashMarker, crash);
            Assert.True(
                crash.WaitForExit(30_000),
                "receipt-deploy retirement crash worker did not exit");
            Assert.NotEqual(0, crash.ExitCode);
            Assert.Equal(checkpoint, File.ReadAllText(crashMarker));
        }
        Assert.True(File.Exists(record));
        Assert.Equal(expected, Census(target));
        Assert.Equal(siblingDirectoryAcl, GetAcl(siblingDirectory));
        Assert.Equal(siblingFileAcl, GetAcl(siblingFile));
        Assert.Equal(priorDescriptorAcl, GetAcl(Path.Combine(target, Mod + ".mod")));
        Assert.Equal(priorBundleAcl, GetAcl(Path.Combine(target, NewBundle)));
        Assert.Equal(
            witnessExpected ? 0 : 1,
            Directory.EnumerateFiles(
                parent,
                ".vmblauncher-receipt-deploy-*.journal.json").Count());
        Assert.Equal(
            witnessExpected ? 1 : 0,
            Directory.EnumerateFiles(
                parent,
                ".vmblauncher-receipt-deploy-*.journal.json.retiring-*").Count());
        Directory.Delete(source, recursive: true);

        var recoveryMarker = Path.Combine(temp.Path, "recovered.txt");
        using (var recovery = StartTransactionWorker(
                   worker,
                   "receipt-deploy-recover",
                   mutex,
                   record,
                   temp.Path,
                   recoveryMarker,
                   release: ""))
        {
            WaitForWorkerMarker(recoveryMarker, recovery);
            Assert.True(
                recovery.WaitForExit(10_000),
                "receipt-deploy retirement recovery worker did not exit");
            var detail = File.ReadAllText(recoveryMarker);
            Assert.True(
                recovery.ExitCode == 0,
                $"receipt-deploy retirement recovery failed ({recovery.ExitCode}): {detail}\n{recovery.StandardError.ReadToEnd()}");
            Assert.StartsWith("True|", detail, StringComparison.Ordinal);
        }

        Assert.Equal(expected, Census(target));
        Assert.Equal(parentAcl, GetAcl(parent));
        Assert.Equal(targetAcl, GetAcl(target));
        Assert.Equal(siblingDirectoryAcl, GetAcl(siblingDirectory));
        Assert.Equal(siblingFileAcl, GetAcl(siblingFile));
        Assert.Equal(priorDescriptorAcl, GetAcl(Path.Combine(target, Mod + ".mod")));
        Assert.Equal(priorBundleAcl, GetAcl(Path.Combine(target, NewBundle)));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(parent),
            path => Path.GetFileName(path).StartsWith(
                $".vmblauncher-receipt-deploy-{PublishedId}",
                StringComparison.Ordinal));
        Assert.False(File.Exists(record));
        Assert.False(Directory.Exists(source));
    }

    [Theory]
    [InlineData("stage-verified", "target")]
    [InlineData("stage-verified", "stage")]
    [InlineData("precommit-census-complete", "target")]
    [InlineData("precommit-census-complete", "stage")]
    public void PrecommitMutation_NeverLeavesAnUnprovenReplacementAtTarget(
        string checkpoint,
        string mutation)
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point != checkpoint) return;
            var transactionPath = mutation == "target"
                ? fixture.Target
                : Directory.EnumerateDirectories(
                    fixture.Parent,
                    ".vmblauncher-receipt-deploy-*.stage").Single();
            var leaf = mutation == "target" ? OldBundle : NewBundle;
            File.WriteAllText(Path.Combine(transactionPath, leaf), "external drift");
        };
        RunOutcome result;
        try
        {
            result = LocalExactSetDeployment.Reconcile(
                fixture.Target,
                fixture.Authorization(),
                source);
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        Assert.False(result.Ok);
        Assert.True(Directory.Exists(fixture.Target));
        Assert.False(File.Exists(Path.Combine(fixture.Target, NewBundle)));
        fixture.AssertTarget(fixture.Prior);
    }

    [Fact]
    public void HandleBoundCleanup_DeniesRenameAndReplacementAfterProof()
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        var attempted = false;
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point != "delete-file-pinned" || attempted) return;
            attempted = true;
            var backup = Directory.EnumerateDirectories(
                fixture.Parent,
                ".vmblauncher-receipt-deploy-*.backup").Single();
            var pinned = Directory.EnumerateFiles(backup).OrderBy(path => path, StringComparer.Ordinal).First();
            var moved = pinned + ".moved";
            Assert.ThrowsAny<IOException>(() => File.Move(pinned, moved));
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(pinned, "replacement"));
        };
        RunOutcome result;
        try
        {
            result = LocalExactSetDeployment.Reconcile(
                fixture.Target,
                fixture.Authorization(),
                source);
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        Assert.True(attempted);
        Assert.True(result.Ok, result.Message);
        fixture.AssertTarget(fixture.Expected);
        fixture.AssertNoTransactionArtifacts();
    }

    [Theory]
    [InlineData("cleanup-file-deleted")]
    [InlineData("cleanup-files-cleared")]
    [InlineData("backup-deleted")]
    [InlineData("journal-retirement-prepared")]
    public void OrdinaryCleanupFailure_IsFinalizedWithoutRollingBackInstalledBytes(
        string checkpoint)
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        var injected = false;
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point == checkpoint && !injected)
            {
                injected = true;
                throw new IOException("ordinary cleanup interruption");
            }
        };
        RunOutcome interrupted;
        try
        {
            interrupted = LocalExactSetDeployment.Reconcile(
                fixture.Target,
                fixture.Authorization(),
                source);
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        Assert.True(injected);
        Assert.False(interrupted.Ok);
        Assert.Contains("cleanup", interrupted.Message, StringComparison.OrdinalIgnoreCase);
        fixture.AssertTarget(fixture.Expected);

        var recovered = LocalExactSetDeployment.RecoverInterruptedSafety(
            fixture.Parent,
            Mod);
        Assert.True(recovered.Ok, recovered.Message);
        fixture.AssertTarget(fixture.Expected);
        fixture.AssertNoTransactionArtifacts();
    }

    [Theory]
    [InlineData("journal-slot-invalidated")]
    [InlineData("journal-slot-payload-partial")]
    [InlineData("journal-slot-payload-flushed")]
    [InlineData("journal-slot-header-partial")]
    public void CleanupStateWriteFailureBeforeDurability_RollsBackToPrior(
        string checkpoint)
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        var armed = false;
        var injected = false;
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point == "membership-seals-applied") armed = true;
            if (!armed || point != checkpoint || injected) return;
            injected = true;
            throw new IOException("ordinary cleanup journal interruption");
        };
        RunOutcome result;
        try
        {
            result = LocalExactSetDeployment.Reconcile(
                fixture.Target,
                fixture.Authorization(),
                source);
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        Assert.True(injected);
        Assert.False(result.Ok);
        fixture.AssertTarget(fixture.Prior);
        fixture.AssertNoTransactionArtifacts();
    }

    [Fact]
    public void CleanupStateWriteFailureAfterDurability_KeepsInstalledBytesForFinalization()
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        var armed = false;
        var injected = false;
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point == "membership-seals-applied") armed = true;
            if (!armed || point != "journal-slot-durable" || injected) return;
            injected = true;
            throw new IOException("ordinary durable cleanup journal interruption");
        };
        RunOutcome result;
        try
        {
            result = LocalExactSetDeployment.Reconcile(
                fixture.Target,
                fixture.Authorization(),
                source);
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        Assert.True(injected);
        Assert.False(result.Ok);
        Assert.Contains("cleanup", result.Message, StringComparison.OrdinalIgnoreCase);
        fixture.AssertTarget(fixture.Expected);
        var recovered = LocalExactSetDeployment.RecoverInterruptedSafety(fixture.Parent, Mod);
        Assert.True(recovered.Ok, recovered.Message);
        fixture.AssertTarget(fixture.Expected);
        fixture.AssertNoTransactionArtifacts();
    }

    [Theory]
    [InlineData("stage-verified")]
    [InlineData("precommit-census-complete")]
    public void PrecommitDirectoryLeases_DenyTargetAndStageNamespaceSwaps(string checkpoint)
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        var attempted = false;
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point != checkpoint || attempted) return;
            attempted = true;
            var stage = Directory.EnumerateDirectories(
                fixture.Parent,
                ".vmblauncher-receipt-deploy-*.stage").Single();
            Assert.ThrowsAny<IOException>(() => Directory.Move(stage, stage + ".foreign"));
            Assert.ThrowsAny<IOException>(() => Directory.Move(fixture.Target, fixture.Target + ".foreign"));
        };
        RunOutcome result;
        try
        {
            result = LocalExactSetDeployment.Reconcile(
                fixture.Target,
                fixture.Authorization(),
                source);
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        Assert.True(attempted);
        Assert.True(result.Ok, result.Message);
        fixture.AssertTarget(fixture.Expected);
        fixture.AssertNoTransactionArtifacts();
    }

    [Theory]
    [InlineData("replacement-installed")]
    [InlineData("target-verified")]
    [InlineData("cleanup-durable")]
    public void InstalledTargetProof_DeniesByteAndNamespaceMutation(string checkpoint)
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        var attempted = false;
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point != checkpoint || attempted) return;
            attempted = true;
            var leaf = Path.Combine(fixture.Target, NewBundle);
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(leaf, "foreign"));
            Assert.ThrowsAny<IOException>(() => File.Delete(leaf));
            Assert.ThrowsAny<IOException>(() => Directory.Move(fixture.Target, fixture.Target + ".foreign"));
        };
        RunOutcome result;
        try
        {
            result = LocalExactSetDeployment.Reconcile(
                fixture.Target,
                fixture.Authorization(),
                source);
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        Assert.True(attempted);
        Assert.True(result.Ok, result.Message);
        fixture.AssertTarget(fixture.Expected);
        fixture.AssertNoTransactionArtifacts();
    }

    [Fact]
    public void MixedReplacementBeforeDurableCommit_IsQuarantinedAndPriorIsRestored()
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        var foreign = Path.Combine(fixture.Target, "human-owned.txt");
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point == "replacement-moved-before-journal")
                File.WriteAllText(foreign, "preserve me");
        };
        RunOutcome result;
        try
        {
            result = LocalExactSetDeployment.Reconcile(
                fixture.Target,
                fixture.Authorization(),
                source);
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        Assert.False(result.Ok);
        Assert.Contains("quarantine", result.Message, StringComparison.OrdinalIgnoreCase);
        fixture.AssertTarget(fixture.Prior);
        var quarantine = Assert.Single(Directory.EnumerateDirectories(
            fixture.Parent,
            ".vmblauncher-receipt-deploy-*.quarantine"));
        Assert.Equal("preserve me", File.ReadAllText(Path.Combine(quarantine, "human-owned.txt")));
        var recovery = LocalExactSetDeployment.RecoverInterruptedSafety(fixture.Parent, Mod);
        Assert.False(recovery.Ok);
        fixture.AssertTarget(fixture.Prior);
        Assert.True(Directory.Exists(quarantine));
    }

    [Fact]
    public void ForeignInsertionIntoBackupBeforeItsJournalUpdate_IsPreservedAndBlocks()
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        string? foreign = null;
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point != "target-moved-before-journal") return;
            var backup = Assert.Single(Directory.EnumerateDirectories(
                fixture.Parent,
                ".vmblauncher-receipt-deploy-*.backup"));
            foreign = Path.Combine(backup, "human-owned.txt");
            File.WriteAllText(foreign, "preserve me");
        };
        RunOutcome result;
        try
        {
            result = LocalExactSetDeployment.Reconcile(
                fixture.Target,
                fixture.Authorization(),
                source);
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        Assert.False(result.Ok);
        Assert.NotNull(foreign);
        fixture.AssertTarget(fixture.Prior);
        var quarantine = Assert.Single(Directory.EnumerateDirectories(
            fixture.Parent,
            ".vmblauncher-receipt-deploy-*.quarantine*"));
        Assert.Equal(
            "preserve me",
            File.ReadAllText(Path.Combine(quarantine, "human-owned.txt")));
        var recovery = LocalExactSetDeployment.RecoverInterruptedSafety(fixture.Parent, Mod);
        Assert.False(recovery.Ok);
        fixture.AssertTarget(fixture.Prior);
        Assert.Equal(
            "preserve me",
            File.ReadAllText(Path.Combine(quarantine, "human-owned.txt")));
    }

    [Fact]
    public void ForeignInsertionAtDurableCommit_IsDeniedByTheOsSeal()
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        var foreign = Path.Combine(fixture.Target, "human-owned.txt");
        var attempted = false;
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point == "cleanup-durable")
            {
                attempted = true;
                Assert.ThrowsAny<UnauthorizedAccessException>(() =>
                    File.WriteAllText(foreign, "must be denied"));
            }
        };
        RunOutcome result;
        try
        {
            result = LocalExactSetDeployment.Reconcile(
                fixture.Target,
                fixture.Authorization(),
                source);
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        Assert.True(attempted);
        Assert.True(result.Ok, result.Message);
        Assert.False(File.Exists(foreign));
        fixture.AssertTarget(fixture.Expected);
        fixture.AssertNoTransactionArtifacts();
    }

    [Fact]
    public void FinalMembershipBoundary_OsSealsRefuseIndependentProcessMutation()
    {
        const string gatedCheckpoint = "membership-seals-applied";
        using var temp = new TempDir();
        var source = temp.CreateSubdir("source");
        var parent = temp.CreateSubdir("workshop");
        var target = temp.CreateSubdir(Path.Combine("workshop", PublishedId));
        File.WriteAllText(Path.Combine(source, Mod + ".mod"), "new descriptor");
        File.WriteAllText(Path.Combine(source, NewBundle), "new bundle bytes");
        File.WriteAllText(Path.Combine(target, Mod + ".mod"), "old descriptor");
        File.WriteAllText(Path.Combine(target, OldBundle), "old bundle bytes");
        var siblingDirectory = temp.CreateSubdir(@"workshop\unrelated");
        var siblingFile = temp.Write(@"workshop\unrelated\unrelated.txt", "unrelated");
        var parentAcl = GetAcl(parent);
        var targetAcl = GetAcl(target);
        var siblingDirectoryAcl = GetAcl(siblingDirectory);
        var siblingFileAcl = GetAcl(siblingFile);
        var worker = FindTransactionWorker();
        var mutex = @"Local\VMBLauncher.Tests.MembershipRace." + Guid.NewGuid().ToString("N");
        var record = Path.Combine(temp.Path, "owner.json");
        var ready = Path.Combine(temp.Path, "worker.ready");
        var release = Path.Combine(temp.Path, "worker.release");
        var resultPath = Path.Combine(temp.Path, "worker.result.json");
        var foreign = Path.Combine(target, "final-boundary-foreign.txt");
        var foreignDirectory = Path.Combine(target, "final-boundary-directory");
        var expectedDescriptor = Path.Combine(target, Mod + ".mod");
        var movedDescriptor = Path.Combine(temp.Path, "moved-descriptor.mod");
        var incoming = Path.Combine(temp.Path, "incoming.mod_bundle");
        File.WriteAllText(incoming, "incoming");

        using var process = StartTransactionWorker(
            worker,
            "receipt-deploy-membership-race",
            mutex,
            record,
            temp.Path,
            ready,
            release,
            "20000",
            gatedCheckpoint,
            resultPath);
        try
        {
            WaitForWorkerMarker(ready, process);
            var readyParts = File.ReadAllText(ready).Split('|', 2);
            Assert.Equal(gatedCheckpoint, readyParts[1]);
            Assert.True(int.TryParse(readyParts[0], out var workerPid));
            Assert.Equal(process.Id, workerPid);
            Assert.NotEqual(Environment.ProcessId, workerPid);
            var descriptorAclWhileSealed = GetAcl(expectedDescriptor);
            var bundleAclWhileSealed = GetAcl(Path.Combine(target, NewBundle));

            AssertAccessDenied(() => File.WriteAllText(foreign, "foreign"));
            AssertAccessDenied(() => Directory.CreateDirectory(foreignDirectory));
            AssertAccessDenied(() => File.Delete(expectedDescriptor));
            AssertAccessDenied(() => File.Move(expectedDescriptor, movedDescriptor));
            AssertAccessDenied(() =>
                File.Move(incoming, Path.Combine(target, "incoming.mod_bundle")));
            AssertAccessDenied(() =>
                Directory.Move(target, Path.Combine(parent, "renamed-target")));
            Assert.False(File.Exists(foreign));
            Assert.False(Directory.Exists(foreignDirectory));
            Assert.True(File.Exists(expectedDescriptor));
            File.WriteAllText(release, "release");

            Assert.True(
                process.WaitForExit(20_000),
                "membership-race worker did not exit");
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(
                File.Exists(resultPath),
                $"membership-race worker produced no result (exit {process.ExitCode}): {stderr}");
            var workerResult = JsonSerializer.Deserialize<MembershipRaceWorkerResult>(
                File.ReadAllText(resultPath));
            Assert.NotNull(workerResult);
            Assert.Equal(workerPid, workerResult.Pid);
            Assert.True(workerResult.Ok, workerResult.Message);
            Assert.Equal(0, process.ExitCode);
            AssertTraceOrder(workerResult.Trace, new[]
            {
                "membership-seals-planned",
                "parent-membership-seal-applied",
                "membership-seals-applied",
                "cleanup-durable",
                "target-membership-seal-restored-before-parent",
                "parent-membership-seal-restored-before-reproof",
                "membership-seals-restored",
                "journal-retirement-witness",
                "journal-retirement-deleted",
            });
            Assert.DoesNotContain(
                workerResult.Trace,
                point => point.StartsWith("membership-monitor-", StringComparison.Ordinal));
            Assert.False(File.Exists(foreign));
            Assert.Equal("new descriptor", File.ReadAllText(Path.Combine(target, Mod + ".mod")));
            Assert.Equal("new bundle bytes", File.ReadAllText(Path.Combine(target, NewBundle)));
            Assert.False(File.Exists(Path.Combine(target, OldBundle)));
            Assert.Equal(parentAcl, GetAcl(parent));
            Assert.Equal(targetAcl, GetAcl(target));
            Assert.Equal(siblingDirectoryAcl, GetAcl(siblingDirectory));
            Assert.Equal(siblingFileAcl, GetAcl(siblingFile));
            Assert.Equal(descriptorAclWhileSealed, GetAcl(expectedDescriptor));
            Assert.Equal(bundleAclWhileSealed, GetAcl(Path.Combine(target, NewBundle)));
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(parent),
                path => Path.GetFileName(path).StartsWith(
                    $".vmblauncher-receipt-deploy-{PublishedId}",
                    StringComparison.Ordinal));
        }
        finally
        {
            if (!process.HasExited)
            {
                File.WriteAllText(release, "release");
                if (!process.WaitForExit(2_000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5_000);
                }
            }
        }
    }

    [Theory]
    [InlineData("rollback-replacement-pinned")]
    [InlineData("rollback-backup-pinned")]
    public void RollbackProof_DeniesNamespaceSwapAndRestoresPrior(string protectedCheckpoint)
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        var attempted = false;
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point == "replacement-installed")
                throw new IOException("force rollback");
            if (point != protectedCheckpoint || attempted) return;
            attempted = true;
            var protectedPath = protectedCheckpoint == "rollback-replacement-pinned"
                ? fixture.Target
                : Directory.EnumerateDirectories(
                    fixture.Parent,
                    ".vmblauncher-receipt-deploy-*.backup").Single();
            Assert.ThrowsAny<IOException>(() =>
                Directory.Move(protectedPath, protectedPath + ".foreign"));
        };
        RunOutcome result;
        try
        {
            result = LocalExactSetDeployment.Reconcile(
                fixture.Target,
                fixture.Authorization(),
                source);
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        Assert.True(attempted);
        Assert.False(result.Ok);
        Assert.Contains("Previous deployment restored", result.Message, StringComparison.OrdinalIgnoreCase);
        fixture.AssertTarget(fixture.Prior);
        fixture.AssertNoTransactionArtifacts();
    }

    [Theory]
    [InlineData("recovery-replacement-pinned")]
    [InlineData("recovery-backup-pinned")]
    public void RecoveryProof_DeniesNamespaceSwapAndRestoresPrior(string protectedCheckpoint)
    {
        using var fixture = new Fixture();
        using (var source = fixture.Capture())
            fixture.CrashAt("replacement-installed", source);
        var attempted = false;
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point != protectedCheckpoint || attempted) return;
            attempted = true;
            var protectedPath = protectedCheckpoint == "recovery-replacement-pinned"
                ? fixture.Target
                : Directory.EnumerateDirectories(
                    fixture.Parent,
                    ".vmblauncher-receipt-deploy-*.backup").Single();
            Assert.ThrowsAny<IOException>(() =>
                Directory.Move(protectedPath, protectedPath + ".foreign"));
        };
        RunOutcome recovered;
        try
        {
            recovered = LocalExactSetDeployment.RecoverInterruptedSafety(
                fixture.Parent,
                Mod);
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        Assert.True(attempted);
        Assert.True(recovered.Ok, recovered.Message);
        fixture.AssertTarget(fixture.Prior);
        fixture.AssertNoTransactionArtifacts();
    }

    [Theory]
    [InlineData("journal-slot-payload-flushed")]
    [InlineData("journal-retirement-witness")]
    public void JournalHandleProof_DeniesWriteAndNamespaceSwap(string checkpoint)
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        var attempted = false;
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point != checkpoint || attempted) return;
            attempted = true;
            var pattern = checkpoint == "journal-retirement-witness"
                ? $".vmblauncher-receipt-deploy-{PublishedId}.journal.json.retiring-*"
                : $".vmblauncher-receipt-deploy-{PublishedId}.journal.json";
            var journal = Assert.Single(Directory.EnumerateFiles(fixture.Parent, pattern));
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(journal, "foreign"));
            Assert.ThrowsAny<IOException>(() => File.Move(journal, journal + ".foreign"));
        };
        RunOutcome result;
        try
        {
            result = LocalExactSetDeployment.Reconcile(
                fixture.Target,
                fixture.Authorization(),
                source);
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        Assert.True(attempted);
        Assert.True(result.Ok, result.Message);
        fixture.AssertTarget(fixture.Expected);
        fixture.AssertNoTransactionArtifacts();
    }

    [Fact]
    public async Task ModRunnerLegacyDeploy_RecoversOldJournalBeforeAnyLegacyMutation()
    {
        using var fixture = new Fixture();
        using (var source = fixture.Capture())
            fixture.CrashAt("replacement-installed", source);
        var projectRoot = MachineTransactionLease.CurrentIdentity!.ProjectRoot!;
        var mod = new ModInfo
        {
            Name = Mod,
            ModDir = Path.Combine(projectRoot, "missing-mod"),
            ItemCfgPath = Path.Combine(projectRoot, "missing-mod", "itemV2.cfg"),
            PublishedId = "",
        };
        var runner = new ModRunner(
            new Settings
            {
                ProjectRoot = projectRoot,
                WorkshopContentRoot = fixture.Parent,
            },
            _ => { });

        var result = await runner.DeployAsync(mod, skipRemote: true);

        Assert.False(result.Ok);
        Assert.Contains("No Workshop ID", result.Message, StringComparison.OrdinalIgnoreCase);
        fixture.AssertTarget(fixture.Prior);
        fixture.AssertNoTransactionArtifacts();
    }

    [Fact]
    public async Task InvalidReceiptAuthority_NeverFallsThroughToLegacyDeploy()
    {
        using var fixture = new Fixture();
        var projectRoot = MachineTransactionLease.CurrentIdentity!.ProjectRoot!;
        var mod = new ModInfo
        {
            Name = Mod,
            ModDir = Path.Combine(projectRoot, "missing-mod"),
            ItemCfgPath = Path.Combine(projectRoot, "missing-mod", "itemV2.cfg"),
            PublishedId = PublishedId,
        };
        var runner = new ModRunner(
            new Settings
            {
                ProjectRoot = projectRoot,
                WorkshopContentRoot = fixture.Parent,
            },
            _ => { });
        var before = SnapshotTree(fixture.Parent);

        var result = await runner.DeployAsync(
            mod,
            skipRemote: true,
            deploymentReceiptPath: Path.Combine(projectRoot, "missing-deployment-receipt.json"));

        Assert.False(result.Ok);
        Assert.Contains("receipt", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, SnapshotTree(fixture.Parent));
        fixture.AssertTarget(fixture.Prior);
    }

    [Fact]
    public async Task JournalFreeLegacyDeploy_DoesNotRejectAnUppercaseModName()
    {
        using var temp = new TempDir();
        var workshop = temp.CreateSubdir("workshop");
        var project = MachineTransactionLease.CurrentIdentity!.ProjectRoot!;
        var mod = new ModInfo
        {
            Name = "LegacyUppercaseMod",
            ModDir = Path.Combine(project, "missing-mod"),
            ItemCfgPath = Path.Combine(project, "missing-mod", "itemV2.cfg"),
            PublishedId = "",
        };
        var runner = new ModRunner(
            new Settings
            {
                ProjectRoot = project,
                WorkshopContentRoot = workshop,
            },
            _ => { });

        var result = await runner.DeployAsync(mod, skipRemote: true);

        Assert.False(result.Ok);
        Assert.Contains("No Workshop ID", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("noncanonical", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workshop));
    }

    [Fact]
    public void JournalFreeSafetyProbe_IsANoopForAnAliasedWorkshopRoot()
    {
        using var temp = new TempDir();
        var real = temp.CreateSubdir("workshop-real");
        var alias = Path.Combine(temp.Path, "workshop-alias");
        try
        {
            Directory.CreateSymbolicLink(alias, real);
        }
        catch (UnauthorizedAccessException)
        {
            return; // Host policy does not permit unprivileged directory links.
        }
        catch (IOException)
        {
            return; // Host filesystem does not support directory links.
        }

        var before = SnapshotTree(temp.Path);
        var result = LocalExactSetDeployment.RecoverInterruptedSafety(
            alias,
            "LegacyUppercaseMod");

        Assert.True(result.Ok, result.Message);
        Assert.Equal(before, SnapshotTree(temp.Path));
    }

    [Fact]
    public void ArbitraryOrphanedJournalTemp_IsPreservedBeforeForwardMutation()
    {
        using var fixture = new Fixture();
        var temp = Path.Combine(
            fixture.Parent,
            $".vmblauncher-receipt-deploy-{PublishedId}.journal.json.tmp-{Guid.NewGuid():N}");
        File.WriteAllText(temp, "orphan");
        using var source = fixture.Capture();

        var result = LocalExactSetDeployment.Reconcile(
            fixture.Target,
            fixture.Authorization(),
            source);

        Assert.False(result.Ok);
        Assert.Contains("unowned", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(temp));
        fixture.AssertTarget(fixture.Prior);
    }

    [Fact]
    public void JournalTempWithoutCanonicalJournal_RefusesMatchingTransactionArtifacts()
    {
        using var fixture = new Fixture();
        var operation = Guid.NewGuid().ToString("N");
        var prefix = $".vmblauncher-receipt-deploy-{PublishedId}.{operation}";
        var temp = Path.Combine(
            fixture.Parent,
            $".vmblauncher-receipt-deploy-{PublishedId}.journal.json.tmp-{operation}");
        var stage = Path.Combine(fixture.Parent, prefix + ".stage");
        File.WriteAllText(temp, "orphan");
        Directory.CreateDirectory(stage);
        using var source = fixture.Capture();

        var result = LocalExactSetDeployment.Reconcile(
            fixture.Target,
            fixture.Authorization(),
            source);

        Assert.False(result.Ok);
        Assert.Contains("unowned", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(temp));
        Assert.True(Directory.Exists(stage));
        fixture.AssertTarget(fixture.Prior);
    }

    [Fact]
    public void HardLinkedJournalTemp_IsRefusedWithoutDeletingEitherLink()
    {
        using var fixture = new Fixture();
        var backing = Path.Combine(fixture.Parent, "human-owned.txt");
        var temp = Path.Combine(
            fixture.Parent,
            $".vmblauncher-receipt-deploy-{PublishedId}.journal.json.tmp-{Guid.NewGuid():N}");
        File.WriteAllText(backing, "human bytes");
        Assert.True(CreateHardLinkW(temp, backing, IntPtr.Zero));
        using var source = fixture.Capture();

        var result = LocalExactSetDeployment.Reconcile(
            fixture.Target,
            fixture.Authorization(),
            source);

        Assert.False(result.Ok);
        Assert.Contains("unowned", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("human bytes", File.ReadAllText(backing));
        Assert.Equal("human bytes", File.ReadAllText(temp));
        fixture.AssertTarget(fixture.Prior);
    }

    [Fact]
    public void JournalTempInventoryBound_IsCheckedBeforeAnyTempDeletion()
    {
        using var fixture = new Fixture();
        string? first = null;
        string? last = null;
        for (var index = 0; index < 4097; index++)
        {
            var temp = Path.Combine(
                fixture.Parent,
                $".vmblauncher-receipt-deploy-{PublishedId}.journal.json.tmp-{Guid.NewGuid():N}");
            using (File.Create(temp)) { }
            first ??= temp;
            last = temp;
        }
        using var source = fixture.Capture();

        var result = LocalExactSetDeployment.Reconcile(
            fixture.Target,
            fixture.Authorization(),
            source);

        Assert.False(result.Ok);
        Assert.Contains("inventory", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(last));
        Assert.Equal(
            4097,
            Directory.EnumerateFileSystemEntries(
                fixture.Parent,
                $".vmblauncher-receipt-deploy-{PublishedId}.journal.json.tmp-*").Count());
        fixture.AssertTarget(fixture.Prior);
    }

    [Fact]
    public void JournalPathOccupiedByDirectory_IsRefusedWithoutMutation()
    {
        using var fixture = new Fixture();
        var journal = Path.Combine(
            fixture.Parent,
            $".vmblauncher-receipt-deploy-{PublishedId}.journal.json");
        Directory.CreateDirectory(journal);
        using var source = fixture.Capture();

        var result = LocalExactSetDeployment.Reconcile(
            fixture.Target,
            fixture.Authorization(),
            source);

        Assert.False(result.Ok);
        Assert.Contains("not a regular file", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(journal));
        fixture.AssertTarget(fixture.Prior);
    }

    [Fact]
    public void HardLinkedCanonicalJournal_IsRefusedBeforeRecoveryMutation()
    {
        using var fixture = new Fixture();
        using (var source = fixture.Capture())
            fixture.CrashAt("stage-created", source);
        var journal = Directory.EnumerateFiles(
            fixture.Parent,
            ".vmblauncher-receipt-deploy-*.journal.json").Single();
        var alias = Path.Combine(fixture.Parent, "journal-human-alias.json");
        Assert.True(CreateHardLinkW(alias, journal, IntPtr.Zero));

        var result = LocalExactSetDeployment.RecoverInterruptedSafety(
            fixture.Parent,
            Mod);

        Assert.False(result.Ok);
        Assert.Contains("hard-link", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(journal));
        Assert.True(File.Exists(alias));
        fixture.AssertTarget(fixture.Prior);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordedStageWithUnknownOrNestedEntry_IsPreservedAndNeverInstalled(
        bool nested)
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        string? planted = null;
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point != "stage-verified") return;
            var stage = Directory.EnumerateDirectories(
                fixture.Parent,
                ".vmblauncher-receipt-deploy-*.stage").Single();
            planted = Path.Combine(stage, nested ? "nested" : "notes.txt");
            if (nested) Directory.CreateDirectory(planted);
            else File.WriteAllText(planted, "human bytes");
        };
        RunOutcome result;
        try
        {
            result = LocalExactSetDeployment.Reconcile(
                fixture.Target,
                fixture.Authorization(),
                source);
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        Assert.False(result.Ok);
        Assert.NotNull(planted);
        Assert.True(nested ? Directory.Exists(planted) : File.Exists(planted));
        fixture.AssertTarget(fixture.Prior);
        Assert.False(File.Exists(Path.Combine(fixture.Target, NewBundle)));
    }

    [Theory]
    [InlineData("stage-temp-created")]
    [InlineData("stage-file-renamed")]
    public void RecordedPendingFileReplacementWithIdenticalBytes_IsPreservedAndBlocks(
        string checkpoint)
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        fixture.CrashAt(checkpoint, source);
        var stage = Assert.Single(Directory.EnumerateDirectories(
            fixture.Parent,
            ".vmblauncher-receipt-deploy-*.stage"));
        var pending = Assert.Single(Directory.EnumerateFiles(stage));
        var bytes = File.ReadAllBytes(pending);
        File.Delete(pending);
        File.WriteAllBytes(pending, bytes);

        var recovery = LocalExactSetDeployment.RecoverInterruptedSafety(fixture.Parent, Mod);

        Assert.False(recovery.Ok);
        Assert.Contains("identity", recovery.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bytes, File.ReadAllBytes(pending));
        fixture.AssertTarget(fixture.Prior);
    }

    [Fact]
    public void RecordedCompletedStageFileReplacementWithIdenticalBytes_IsPreservedAndBlocks()
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        fixture.CrashAt("stage-file-copied", source);
        var stage = Assert.Single(Directory.EnumerateDirectories(
            fixture.Parent,
            ".vmblauncher-receipt-deploy-*.stage"));
        var completed = Assert.Single(Directory.EnumerateFiles(stage));
        var bytes = File.ReadAllBytes(completed);
        File.Delete(completed);
        File.WriteAllBytes(completed, bytes);

        var recovery = LocalExactSetDeployment.RecoverInterruptedSafety(fixture.Parent, Mod);

        Assert.False(recovery.Ok);
        Assert.Contains("identity", recovery.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bytes, File.ReadAllBytes(completed));
        fixture.AssertTarget(fixture.Prior);
    }

    [Fact]
    public void PostInstallByteMutation_IsDeniedAndPriorIsRestored()
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point == "replacement-installed")
                File.WriteAllText(Path.Combine(fixture.Target, NewBundle), "external drift");
        };
        RunOutcome result;
        try
        {
            result = LocalExactSetDeployment.Reconcile(
                fixture.Target, fixture.Authorization(), source);
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }

        Assert.False(result.Ok);
        Assert.Contains("Previous deployment restored", result.Message, StringComparison.OrdinalIgnoreCase);
        fixture.AssertTarget(fixture.Prior);
        fixture.AssertNoTransactionArtifacts();
    }

    [Fact]
    public void UnjournaledArtifact_IsNeverAdoptedOrDeleted()
    {
        using var fixture = new Fixture();
        var planted = Path.Combine(
            fixture.Parent,
            $".vmblauncher-receipt-deploy-{PublishedId}.{Guid.NewGuid():N}.stage");
        Directory.CreateDirectory(planted);
        File.WriteAllText(Path.Combine(planted, Mod + ".mod"), "human bytes");
        using var source = fixture.Capture();

        var result = LocalExactSetDeployment.Reconcile(
            fixture.Target, fixture.Authorization(), source);

        Assert.False(result.Ok);
        Assert.True(File.Exists(Path.Combine(planted, Mod + ".mod")));
        fixture.AssertTarget(fixture.Prior);
    }

    [Fact]
    public void DuplicatePropertyJournal_IsRejectedWithoutRecoveryMutation()
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point == "journal-durable")
                throw new LocalDeploySimulatedCrashForTest(point);
        };
        try
        {
            Assert.Throws<LocalDeploySimulatedCrashForTest>(() =>
                LocalExactSetDeployment.Reconcile(
                    fixture.Target, fixture.Authorization(), source));
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }
        var journal = Directory.EnumerateFiles(
            fixture.Parent, ".vmblauncher-receipt-deploy-*.journal.json").Single();
        ReplaceNewestJournalPayload(
            journal,
            System.Text.Encoding.UTF8.GetBytes("{\"schema\":3,\"schema\":3}"));
        using var retrySource = fixture.Capture();

        var retry = LocalExactSetDeployment.Reconcile(
            fixture.Target, fixture.Authorization(), retrySource);

        Assert.False(retry.Ok);
        Assert.Contains("duplicate", retry.Message, StringComparison.OrdinalIgnoreCase);
        fixture.AssertTarget(fixture.Prior);
        Assert.True(File.Exists(journal));
    }

    [Fact]
    public void PreReleaseSchema4Journal_IsExplicitlyPreservedWithoutRecoveryMutation()
    {
        using var fixture = new Fixture();
        using var source = fixture.Capture();
        LocalExactSetDeployment.TransitionForTest = point =>
        {
            if (point == "journal-durable")
                throw new LocalDeploySimulatedCrashForTest(point);
        };
        try
        {
            Assert.Throws<LocalDeploySimulatedCrashForTest>(() =>
                LocalExactSetDeployment.Reconcile(
                    fixture.Target, fixture.Authorization(), source));
        }
        finally
        {
            LocalExactSetDeployment.TransitionForTest = null;
        }
        var journal = Directory.EnumerateFiles(
            fixture.Parent, ".vmblauncher-receipt-deploy-*.journal.json").Single();
        var current = System.Text.Encoding.UTF8.GetString(ReadNewestJournalPayload(journal));
        Assert.Contains("\"schema\": 5", current, StringComparison.Ordinal);
        var schema4 = current.Replace("\"schema\": 5", "\"schema\": 4", StringComparison.Ordinal);
        ReplaceNewestJournalPayload(journal, System.Text.Encoding.UTF8.GetBytes(schema4));
        using var retrySource = fixture.Capture();

        var retry = LocalExactSetDeployment.Reconcile(
            fixture.Target, fixture.Authorization(), retrySource);

        Assert.False(retry.Ok);
        Assert.Contains("schema-4", retry.Message, StringComparison.OrdinalIgnoreCase);
        fixture.AssertTarget(fixture.Prior);
        Assert.True(File.Exists(journal));
    }

    [Fact]
    public void AuthorizationObject_IsReadOnlyAndSingleConsumeWithinProcess()
    {
        using var fixture = new Fixture();
        var authorization = fixture.Authorization();

        Assert.IsType<ReadOnlyCollection<CommitQualifiedOutputFile>>(authorization.Files);
        Assert.True(authorization.TryConsume(Mod, DateTime.UtcNow));
        Assert.False(authorization.TryConsume(Mod, DateTime.UtcNow));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _temp = new();
        internal string Source { get; }
        internal string Parent { get; }
        internal string Target { get; }
        internal IReadOnlyList<CommitQualifiedOutputFile> Expected { get; }
        internal IReadOnlyList<CommitQualifiedOutputFile> Prior { get; }

        internal Fixture()
        {
            Source = _temp.CreateSubdir("source");
            Parent = _temp.CreateSubdir("workshop");
            Target = _temp.CreateSubdir(Path.Combine("workshop", PublishedId));
            File.WriteAllText(Path.Combine(Source, Mod + ".mod"), "new descriptor");
            File.WriteAllText(Path.Combine(Source, NewBundle), "new bundle bytes");
            File.WriteAllText(Path.Combine(Target, Mod + ".mod"), "old descriptor");
            File.WriteAllText(Path.Combine(Target, OldBundle), "old bundle bytes");
            Expected = Census(Source);
            Prior = Census(Target);
        }

        internal VerifiedCommitQualifiedExpectedSet Authorization(
            DateTime? expires = null,
            string? authorityFingerprint = null) => new(
            Mod,
            PublishedId,
            Commit,
            authorityFingerprint ?? new string('a', 64),
            expires ?? DateTime.UtcNow.AddMinutes(30),
            Expected);

        internal ImmutableBundleSourceLease Capture() =>
            ImmutableBundleSourceLease.Capture(Source, Mod, Expected);

        internal void CrashAt(string checkpoint, ImmutableBundleSourceLease source)
        {
            LocalExactSetDeployment.TransitionForTest = point =>
            {
                if (point == checkpoint)
                    throw new LocalDeploySimulatedCrashForTest(point);
            };
            try
            {
                Assert.Throws<LocalDeploySimulatedCrashForTest>(() =>
                    LocalExactSetDeployment.Reconcile(
                        Target,
                        Authorization(),
                        source));
            }
            finally
            {
                LocalExactSetDeployment.TransitionForTest = null;
            }
        }

        internal Dictionary<string, byte[]> TargetBytes() =>
            Directory.EnumerateFiles(Target).ToDictionary(
                path => Path.GetFileName(path)!,
                File.ReadAllBytes,
                StringComparer.Ordinal);

        internal void AssertTarget(IReadOnlyList<CommitQualifiedOutputFile> expected)
            => AssertDirectory(Target, expected);

        internal void AssertDirectory(
            string directory,
            IReadOnlyList<CommitQualifiedOutputFile> expected)
        {
            var actual = Census(directory);
            Assert.Equal(expected.Count, actual.Count);
            foreach (var file in expected)
            {
                var match = Assert.Single(actual, item => item.Name == file.Name);
                Assert.Equal(file.Length, match.Length);
                Assert.Equal(file.Sha256, match.Sha256);
            }
        }

        internal void AssertNoTransactionArtifacts()
        {
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(Parent),
                path => Path.GetFileName(path).StartsWith(
                    $".vmblauncher-receipt-deploy-{PublishedId}",
                    StringComparison.Ordinal));
        }

        public void Dispose()
        {
            LocalExactSetDeployment.TransitionForTest = null;
            ImmutableBundleSourceLease.CaptureTransitionForTest = null;
            LocalExactSetDeployment.SnapshotHashTransitionForTest = null;
            _temp.Dispose();
        }
    }

    private static IReadOnlyList<CommitQualifiedOutputFile> Census(string directory) =>
        Directory.EnumerateFiles(directory)
            .Select(path =>
            {
                var bytes = File.ReadAllBytes(path);
                return new CommitQualifiedOutputFile(
                    Path.GetFileName(path),
                    bytes.LongLength,
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            })
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();

    private static void ReplaceNewestJournalPayload(string path, byte[] payload)
    {
        const int slotBytes = 8 * 1024 * 1024;
        const int headerBytes = 48;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(2L * slotBytes, stream.Length);
        var header = new byte[headerBytes];
        long newestSequence = 0;
        var newestSlot = -1;
        for (var slot = 0; slot < 2; slot++)
        {
            stream.Position = (long)slot * slotBytes;
            stream.ReadExactly(header);
            var sequence = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0, sizeof(long)));
            if (sequence > newestSequence)
            {
                newestSequence = sequence;
                newestSlot = slot;
            }
        }
        Assert.InRange(newestSlot, 0, 1);
        Array.Clear(header);
        BinaryPrimitives.WriteInt64LittleEndian(
            header.AsSpan(0, sizeof(long)),
            newestSequence);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(8, sizeof(int)),
            payload.Length);
        SHA256.HashData(payload).CopyTo(header, 16);
        stream.Position = (long)newestSlot * slotBytes;
        stream.Write(header);
        stream.Write(payload);
        stream.Flush(flushToDisk: true);
    }

    private static byte[] ReadNewestJournalPayload(string path)
    {
        const int slotBytes = 8 * 1024 * 1024;
        const int headerBytes = 48;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[headerBytes];
        long newestSequence = 0;
        var newestSlot = -1;
        var newestLength = 0;
        for (var slot = 0; slot < 2; slot++)
        {
            stream.Position = (long)slot * slotBytes;
            stream.ReadExactly(header);
            var sequence = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0, sizeof(long)));
            if (sequence <= newestSequence) continue;
            newestSequence = sequence;
            newestSlot = slot;
            newestLength = BinaryPrimitives.ReadInt32LittleEndian(
                header.AsSpan(8, sizeof(int)));
        }
        Assert.InRange(newestSlot, 0, 1);
        Assert.InRange(newestLength, 1, slotBytes - headerBytes);
        var payload = new byte[newestLength];
        stream.Position = (long)newestSlot * slotBytes + headerBytes;
        stream.ReadExactly(payload);
        return payload;
    }

    private static string[] SnapshotTree(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var relative = Path.GetRelativePath(root, path);
                if (Directory.Exists(path)) return "D:" + relative;
                var bytes = File.ReadAllBytes(path);
                return $"F:{relative}:{bytes.LongLength}:" +
                    Convert.ToHexString(SHA256.HashData(bytes));
            })
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static Process StartTransactionWorker(
        string worker,
        string mode,
        string mutex,
        string record,
        string root,
        string marker,
        string release,
        string timeoutMs = "5000",
        params string[] additionalArguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = worker,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     mode,
                     mutex,
                     record,
                     root,
                     marker,
                     release,
                     timeoutMs,
                  })
            start.ArgumentList.Add(argument);
        foreach (var argument in additionalArguments)
            start.ArgumentList.Add(argument);
        return Process.Start(start)
            ?? throw new InvalidOperationException("could not start transaction worker");
    }

    private static void WaitForWorkerMarker(string marker, Process process)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(marker))
        {
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"transaction worker exited {process.ExitCode}: {process.StandardError.ReadToEnd()}");
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"transaction worker marker timeout: {marker}");
            Thread.Sleep(20);
        }
    }

    private static string FindTransactionWorker()
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var bin = testOutput;
        while (!string.Equals(bin.Name, "bin", StringComparison.OrdinalIgnoreCase))
            bin = bin.Parent
                ?? throw new InvalidOperationException("test output has no bin directory");
        var testsRoot = bin.Parent?.FullName
            ?? throw new InvalidOperationException("test output has no tests project directory");
        var worker = Path.Combine(
            testsRoot,
            "TransactionLeaseWorker",
            "bin",
            Path.GetRelativePath(bin.FullName, testOutput.FullName),
            "VmbLauncher.TransactionLeaseWorker.exe");
        Assert.True(File.Exists(worker), $"transaction worker missing: {worker}");
        return worker;
    }

    private static void AssertTraceOrder(string[] trace, params string[] expected)
    {
        var previous = -1;
        foreach (var checkpoint in expected)
        {
            var index = Array.IndexOf(trace, checkpoint);
            Assert.True(
                index > previous,
                $"checkpoint '{checkpoint}' was absent or out of order: {string.Join(", ", trace)}");
            previous = index;
        }
    }

    private static void AssertAccessDenied(Action mutation)
    {
        var error = Record.Exception(mutation);
        Assert.True(
            error is IOException or UnauthorizedAccessException,
            $"Expected an OS access refusal, got {error?.GetType().FullName ?? "no exception"}: {error?.Message}");
    }

    private static byte[] GetAcl(string path)
    {
        FileSystemSecurity security = Directory.Exists(path)
            ? FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(path),
                AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access)
            : FileSystemAclExtensions.GetAccessControl(
                new FileInfo(path),
                AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
        return security.GetSecurityDescriptorBinaryForm();
    }

    private sealed record MembershipRaceWorkerResult(
        int Pid,
        bool Ok,
        string Message,
        string[] Trace);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
