using System.Threading;
using System.IO;
using System.Diagnostics;
using System.Text.Json.Nodes;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public class MachineTransactionLeaseTests
{
    [Fact]
    public void ParentDeathDrainsJoinedChildBeforeContenderMutation()
    {
        for (var iteration = 0; iteration < 10; iteration++)
        {
            using var tmp = new TempDir();
            var worker = FindWorker();
            var mutex = @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N");
            var record = Path.Combine(tmp.Path, "owner.json");
            var ownerMarker = Path.Combine(tmp.Path, "owner.marker");
            var release = Path.Combine(tmp.Path, "child.release");
            using var owner = StartWorker(
                worker, "owner-crash", mutex, record, tmp.Path, ownerMarker, release, 5000);
            WaitForFile(ownerMarker, owner);
            var pids = ReadAllTextRetry(ownerMarker, owner).Split(',').Select(int.Parse).ToArray();
            Assert.Equal(2, pids.Length);

            var contenderMarker = Path.Combine(tmp.Path, "contender.marker");
            using var contender = StartWorker(
                worker, "contender-require-dead", mutex, record, tmp.Path,
                contenderMarker, ownerMarker, 5000);
            Thread.Sleep(100);
            Assert.False(File.Exists(contenderMarker));
            File.WriteAllText(ownerMarker + ".crash", "crash");
            Assert.True(owner.WaitForExit(10_000));
            Assert.NotEqual(0, owner.ExitCode);
            Assert.True(contender.WaitForExit(15_000));
            var contenderError = contender.StandardError.ReadToEnd();
            Assert.True(contender.ExitCode == 0,
                $"contender exited {contender.ExitCode}: {contenderError}");
            Assert.True(File.Exists(contenderMarker));
            foreach (var pid in pids) Assert.False(IsAlive(pid));
        }
    }

    [Fact]
    public void FailedAbandonedRecoveryRemainsAbandonedForSecondContender()
    {
        using var tmp = new TempDir();
        var worker = FindWorker();
        var mutex = @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N");
        var record = Path.Combine(tmp.Path, "owner.json");
        var ownerMarker = Path.Combine(tmp.Path, "owner.marker");
        var crash = Path.Combine(tmp.Path, "owner.crash");
        using var owner = StartWorker(
            worker, "owner-crash-simple", mutex, record, tmp.Path,
            ownerMarker, crash, 5000);
        WaitForFile(ownerMarker, owner);
        // Plant the durable-recovery failure while two independent contenders
        // are already queued. Keeping their handles open also preserves the
        // named mutex object across each crashing owner's process exit.
        File.Delete(record);
        var firstMarker = Path.Combine(tmp.Path, "first.marker");
        using var first = StartWorker(
            worker, "contender-first", mutex, record, tmp.Path,
            firstMarker, "", 5000);
        var secondMarker = Path.Combine(tmp.Path, "second.marker");
        using var second = StartWorker(
            worker, "contender-second", mutex, record, tmp.Path,
            secondMarker, "", 5000);
        Thread.Sleep(100);
        Assert.False(File.Exists(firstMarker));
        Assert.False(File.Exists(secondMarker));
        File.WriteAllText(crash, "crash");
        Assert.True(owner.WaitForExit(10_000));
        Assert.NotEqual(0, owner.ExitCode);

        // The first contender takes the abandoned mutex but must not normalize
        // it when the record is absent.
        Assert.True(first.WaitForExit(10_000));
        var firstError = first.StandardError.ReadToEnd();
        Assert.NotEqual(0, first.ExitCode);
        Assert.Contains("abandoned", firstError, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(firstMarker));

        // Process exit re-abandons the retained mutex to the queued second
        // contender rather than allowing it to overwrite authority.
        Assert.True(second.WaitForExit(10_000));
        var secondError = second.StandardError.ReadToEnd();
        Assert.NotEqual(0, second.ExitCode);
        Assert.Contains("abandoned", secondError, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(secondMarker));
        Assert.False(File.Exists(record));
    }

    [Fact]
    public void FailedAbandonedRecoveryAllowsGuiRetryButLeavesNoOwnerThreadResidue()
    {
        using var tmp = new TempDir();
        var worker = FindWorker();
        var mutexName = @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N");
        var record = Path.Combine(tmp.Path, "owner.json");
        var ownerMarker = Path.Combine(tmp.Path, "owner.marker");
        var crash = Path.Combine(tmp.Path, "owner.crash");
        var baselineThreads = MachineTransactionLease.ActiveMutexThreadsForTest;
        using var owner = StartWorker(
            worker, "owner-crash-simple", mutexName, record, tmp.Path,
            ownerMarker, crash, 5000);
        WaitForFile(ownerMarker, owner);

        // Keep the named kernel object alive while the external owner dies so
        // this GUI process can take the first abandoned acquisition.
        using var externalKeeper = Mutex.OpenExisting(mutexName);
        File.Delete(record);
        File.WriteAllText(crash, "crash");
        Assert.True(owner.WaitForExit(10_000));
        Assert.NotEqual(0, owner.ExitCode);

        var first = Assert.ThrowsAny<Exception>(() =>
            MachineTransactionLease.Enter(
                "gui-first-retry", "fixture-mod", tmp.Path,
                timeout: TimeSpan.FromSeconds(1), recordPath: record, mutexName: mutexName));
        Assert.Contains("prior ownership evidence", first.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(baselineThreads, MachineTransactionLease.ActiveMutexThreadsForTest);

        // Abandon() retained a handle but stopped the owner thread. A second
        // action in the same still-running GUI must fail through recovery again,
        // rather than timing out behind leaked ownership or starting mutation.
        var second = Assert.ThrowsAny<Exception>(() =>
            MachineTransactionLease.Enter(
                "gui-second-retry", "fixture-mod", tmp.Path,
                timeout: TimeSpan.FromSeconds(1), recordPath: record, mutexName: mutexName));
        Assert.Contains("prior ownership evidence", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(baselineThreads, MachineTransactionLease.ActiveMutexThreadsForTest);
        Assert.False(File.Exists(record));
    }

    [Fact]
    public void LaterContenderRecoversDurableAuthorityAfterSoleMutexOwnerExits()
    {
        using var tmp = new TempDir();
        var worker = FindWorker();
        var mutex = @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N");
        var record = Path.Combine(tmp.Path, "owner.json");
        var ownerMarker = Path.Combine(tmp.Path, "owner.marker");
        var crash = Path.Combine(tmp.Path, "owner.crash");
        using var owner = StartWorker(
            worker, "owner-crash", mutex, record, tmp.Path,
            ownerMarker, crash, 5000);
        WaitForFile(ownerMarker, owner);
        var pids = ReadAllTextRetry(ownerMarker, owner).Split(',').Select(int.Parse).ToArray();
        File.WriteAllText(ownerMarker + ".crash", "crash");
        Assert.True(owner.WaitForExit(10_000));
        Assert.NotEqual(0, owner.ExitCode);

        // No keeper handle and no waiter existed when the sole owner exited;
        // Windows may destroy and recreate the named mutex object. Durable
        // authority must still be recovered before the later marker mutation.
        var contenderMarker = Path.Combine(tmp.Path, "later.marker");
        using var contender = StartWorker(
            worker, "contender-require-dead", mutex, record, tmp.Path,
            contenderMarker, ownerMarker, 5000);
        Assert.True(contender.WaitForExit(15_000));
        Assert.Equal(0, contender.ExitCode);
        Assert.True(File.Exists(contenderMarker));
        foreach (var pid in pids) Assert.False(IsAlive(pid));
    }

    [Fact]
    public void FreshMutexObjectStillRejectsMalformedDurableAuthorityBeforeMutation()
    {
        using var tmp = new TempDir();
        var worker = FindWorker();
        var mutex = @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N");
        var record = Path.Combine(tmp.Path, "owner.json");
        var ownerMarker = Path.Combine(tmp.Path, "owner.marker");
        var crash = Path.Combine(tmp.Path, "owner.crash");
        using var owner = StartWorker(
            worker, "owner-crash-simple", mutex, record, tmp.Path,
            ownerMarker, crash, 5000);
        WaitForFile(ownerMarker, owner);
        File.WriteAllText(crash, "crash");
        Assert.True(owner.WaitForExit(10_000));
        Assert.NotEqual(0, owner.ExitCode);
        File.WriteAllText(record, "{ malformed durable authority");

        var contenderMarker = Path.Combine(tmp.Path, "must-not-mutate.marker");
        using var contender = StartWorker(
            worker, "later-contender", mutex, record, tmp.Path,
            contenderMarker, "", 5000);
        Assert.True(contender.WaitForExit(10_000));
        var error = contender.StandardError.ReadToEnd();
        Assert.NotEqual(0, contender.ExitCode);
        Assert.Contains("prior ownership evidence", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(contenderMarker));
        Assert.True(File.Exists(record));
    }

    [Fact]
    public void MissingRecordAtNormalReleaseReabandonsWithoutOwnerThreadResidue()
    {
        using var tmp = new TempDir();
        var mutex = @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N");
        var record = Path.Combine(tmp.Path, "owner.json");
        var baseline = MachineTransactionLease.ActiveMutexThreadsForTest;
        var lease = MachineTransactionLease.Enter(
            "gui-build", "fixture-mod", tmp.Path,
            recordPath: record, mutexName: mutex);
        Assert.Equal(baseline + 1, MachineTransactionLease.ActiveMutexThreadsForTest);
        File.Delete(record);

        var release = Assert.Throws<InvalidDataException>(() => lease.Dispose());
        Assert.Contains("disappeared", release.Message);
        Assert.Equal(baseline, MachineTransactionLease.ActiveMutexThreadsForTest);

        var retry = Assert.Throws<InvalidDataException>(() =>
            MachineTransactionLease.Enter(
                "gui-retry", "fixture-mod", tmp.Path,
                timeout: TimeSpan.FromSeconds(1), recordPath: record, mutexName: mutex));
        Assert.Contains("prior ownership evidence", retry.Message);
        Assert.Equal(baseline, MachineTransactionLease.ActiveMutexThreadsForTest);
    }

    [Fact]
    public void UndeletableRecordAtReleaseFailsClosedBeforeMutexRelease()
    {
        using var tmp = new TempDir();
        var mutex = @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N");
        var record = Path.Combine(tmp.Path, "owner.json");
        var baseline = MachineTransactionLease.ActiveMutexThreadsForTest;
        var lease = MachineTransactionLease.Enter(
            "gui-deploy", "fixture-mod", tmp.Path,
            recordPath: record, mutexName: mutex);
        using (var lockRecord = new FileStream(
                   record, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => lease.Dispose());
        }
        Assert.Equal(baseline, MachineTransactionLease.ActiveMutexThreadsForTest);

        var retry = Assert.Throws<InvalidDataException>(() =>
            MachineTransactionLease.Enter(
                "gui-retry", "fixture-mod", tmp.Path,
                timeout: TimeSpan.FromSeconds(1), recordPath: record, mutexName: mutex));
        Assert.Contains("still live", retry.Message);
        Assert.Equal(baseline, MachineTransactionLease.ActiveMutexThreadsForTest);
    }

    [Fact]
    public async Task DifferentFlowTimesOutThenRetriesAfterRelease()
    {
        using var tmp = new TempDir();
        var record = System.IO.Path.Combine(tmp.Path, "transaction.json");
        var semaphore = @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N");
        using (MachineTransactionLease.Enter(
            "build-only", "mod-a", tmp.Path, timeout: TimeSpan.FromSeconds(1),
            recordPath: record, mutexName: semaphore))
        {
            Assert.True(File.Exists(record));
            Task<Exception?> blocked;
            using (ExecutionContext.SuppressFlow())
            {
                blocked = Task.Run(() =>
                {
                    try
                    {
                        using var impossible = MachineTransactionLease.Enter(
                            "build-only", "mod-b", tmp.Path,
                            timeout: TimeSpan.FromMilliseconds(100),
                            recordPath: record, mutexName: semaphore);
                        return null;
                    }
                    catch (Exception ex) { return ex; }
                });
            }
            var timeout = Assert.IsType<TimeoutException>(await blocked);
            Assert.Contains("owner_pid=", timeout.Message);
            Assert.Contains("action=build-only", timeout.Message);

            // Re-entrant work in the owning flow joins without touching the count.
            using var nested = MachineTransactionLease.Enter(
                "deploy", "mod-a", tmp.Path,
                timeout: TimeSpan.FromMilliseconds(100),
                recordPath: record, mutexName: semaphore);
        }

        Assert.False(File.Exists(record));
        using var retry = MachineTransactionLease.Enter(
            "upload", "mod-b", tmp.Path, timeout: TimeSpan.FromSeconds(1),
            recordPath: record, mutexName: semaphore);
    }

    [Fact]
    public void NestedScopeRevalidatesModAndProjectRoot()
    {
        using var tmp = new TempDir();
        var other = tmp.CreateSubdir("other");
        var mutex = @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N");
        var record = Path.Combine(tmp.Path, "scope.json");
        using (MachineTransactionLease.Enter(
            "build", "mod-a", tmp.Path, recordPath: record, mutexName: mutex))
        {
            Assert.Throws<InvalidOperationException>(() =>
                MachineTransactionLease.Enter("deploy", "mod-b", tmp.Path));
            Assert.Throws<InvalidOperationException>(() =>
                MachineTransactionLease.Enter("deploy", "mod-a", other));
            using var valid = MachineTransactionLease.Enter("deploy", "mod-a", tmp.Path);
        }
    }

    [Fact]
    public async Task GuiSettingsMutationWaitsBehindWrapperAndDoesNotChangeBytesOnTimeout()
    {
        using var tmp = new TempDir();
        var mutex = @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N");
        var record = Path.Combine(tmp.Path, "gui-owner.json");
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        var settings = Settings.Load(settingsPath);
        settings.ProjectRoot = tmp.Path;
        settings.VmbRoot = "before";
        using (MachineTransactionLease.Enter(
            "settings-fixture-init", mod: null, tmp.Path,
            recordPath: Path.Combine(tmp.Path, "settings-init-owner.json"),
            mutexName: @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N")))
            settings.Save();
        var before = File.ReadAllBytes(settingsPath);

        using (MachineTransactionLease.Enter(
            "ship-wrapper", "mod-a", tmp.Path, recordPath: record, mutexName: mutex))
        {
            Task<Exception?> blocked;
            using (ExecutionContext.SuppressFlow())
            {
                blocked = Task.Run(() =>
                {
                    try
                    {
                        using var gui = GuiSettingsTransaction.Enter(
                            "gui-settings-fixture", timeout: TimeSpan.FromMilliseconds(100),
                            recordPath: record, mutexName: mutex);
                        settings.VmbRoot = "forbidden";
                        gui.Save(settings);
                        return null;
                    }
                    catch (Exception ex) { return ex; }
                });
            }
            Assert.IsType<TimeoutException>(await blocked);
            Assert.Equal(before, File.ReadAllBytes(settingsPath));
        }

        using (var gui = GuiSettingsTransaction.Enter(
            "gui-settings-fixture", timeout: TimeSpan.FromSeconds(1),
            recordPath: record, mutexName: mutex))
        {
            settings.VmbRoot = "after";
            gui.Save(settings);
        }
        Assert.Contains("after", File.ReadAllText(settingsPath));
    }

    [Fact]
    public void InheritedTokenWithoutAuthenticatedOwnerFailsClosed()
    {
        using var tmp = new TempDir();
        var old = Environment.GetEnvironmentVariable(
            MachineTransactionLease.LeaseIdEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                MachineTransactionLease.LeaseIdEnvironmentVariable,
                Guid.NewGuid().ToString("N"));
            var ex = Assert.Throws<InvalidOperationException>(() =>
                MachineTransactionLease.Enter(
                    "build", "mod-a", tmp.Path,
                    timeout: TimeSpan.FromMilliseconds(100),
                    recordPath: System.IO.Path.Combine(tmp.Path, "missing.json"),
                    mutexName: @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N")));
            Assert.Contains("owner record", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                MachineTransactionLease.LeaseIdEnvironmentVariable, old);
        }
    }

    [Fact]
    public void MutationReauthenticatesCompleteDurableOwnerIdentity()
    {
        using var tmp = new TempDir();
        var record = Path.Combine(tmp.Path, "owner.json");
        var mutex = @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N");
        using var lease = MachineTransactionLease.Enter(
            "identity-owner", "mod-a", tmp.Path,
            recordPath: record, mutexName: mutex);
        var json = JsonNode.Parse(File.ReadAllText(record))!.AsObject();
        json["process_tree_job_name"] =
            @"Global\Ensrick.VMBLauncher.Transaction.Process.forged";
        File.WriteAllText(record, json.ToJsonString());

        var error = Assert.Throws<InvalidOperationException>(() =>
            MachineTransactionLease.RequireCurrent("planted mutation"));
        Assert.Contains("lost its authenticated", error.Message);
    }

    [Fact]
    public void HardLauncherDeathTerminatesContainedToolProcess()
    {
        using var tmp = new TempDir();
        var marker = Path.Combine(tmp.Path, "tool.pid");
        using var worker = StartWorker(
            FindWorker(), "process-runner-crash", "unused", "unused",
            tmp.Path, marker, "", 2000);
        WaitForFile(marker, worker);
        var pids = File.ReadAllText(marker).Split(',').Select(int.Parse).ToArray();
        Assert.Equal(2, pids.Length);
        Assert.True(worker.WaitForExit(10_000));
        Assert.NotEqual(0, worker.ExitCode);

        foreach (var pid in pids)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (IsAlive(pid) && DateTime.UtcNow < deadline) Thread.Sleep(20);
            Assert.False(IsAlive(pid));
        }
    }

    [Fact]
    public void OrdinaryZeroActiveJobIsRecognizedWithoutSignalOrEndTimeLimit()
    {
        var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var jobName = @"Global\Ensrick.VMBLauncher.Transaction.Process.fixture." +
            Guid.NewGuid().ToString("N");
        using var planted = ProcessTreeGuard.CreateOrdinarilyEmptiedJobForTest(
            jobName,
            Path.GetFullPath(commandInterpreter),
            new[] { "/d", "/c", "ping -n 2 127.0.0.1 >nul" });

        var elapsed = Stopwatch.StartNew();
        ProcessTreeGuard.WaitForRecordedJobToDrain(jobName, TimeSpan.FromSeconds(2));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1),
            $"zero-active Job accounting should return immediately, elapsed={elapsed.Elapsed}");
    }

    [Fact]
    public void NormalReleaseDrainsOrphanedGrandchildBeforeContenderMutation()
    {
        for (var iteration = 0; iteration < 5; iteration++)
        {
            using var tmp = new TempDir();
            var worker = FindWorker();
            var mutex = @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N");
            var record = Path.Combine(tmp.Path, "normal-owner.json");
            var ownerMarker = Path.Combine(tmp.Path, "residue-pids.txt");
            var release = Path.Combine(tmp.Path, "normal.release");
            using var owner = StartWorker(
                worker, "owner-success-residue", mutex, record, tmp.Path,
                ownerMarker, release, 5000);
            WaitForFile(ownerMarker, owner);
            var contenderMarker = Path.Combine(tmp.Path, "normal-contender.marker");
            using var contender = StartWorker(
                worker, "contender-require-dead", mutex, record, tmp.Path,
                contenderMarker, ownerMarker, 5000);
            Thread.Sleep(100);
            Assert.False(File.Exists(contenderMarker));
            File.WriteAllText(release, "release");
            Assert.True(owner.WaitForExit(10_000));
            Assert.Equal(0, owner.ExitCode);
            Assert.True(contender.WaitForExit(10_000));
            Assert.Equal(0, contender.ExitCode);
            Assert.True(File.Exists(contenderMarker));
        }
    }

    [Fact]
    public void ExplicitNonMutationBreakawaySurvivesWhileContainedChildIsDrained()
    {
        using var tmp = new TempDir();
        var mutex = @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N");
        var record = Path.Combine(tmp.Path, "breakaway-owner.json");
        Process? contained = null;
        Process? breakaway = null;
        try
        {
            using (MachineTransactionLease.Enter(
                "breakaway-fixture", "mod-a", tmp.Path,
                recordPath: record, mutexName: mutex))
            {
                contained = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/d /c ping -n 30 127.0.0.1 >nul",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }) ?? throw new InvalidOperationException("contained fixture did not start");
                var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                breakaway = ProcessTreeGuard.StartBreakawayProcessForTest(
                    Path.GetFullPath(commandInterpreter),
                    new[] { "/d", "/c", "ping -n 30 127.0.0.1 >nul" },
                    createNoWindow: true);
                Thread.Sleep(100);
                Assert.False(contained.HasExited);
                Assert.False(breakaway.HasExited);
            }

            contained.Refresh();
            breakaway.Refresh();
            Assert.True(contained.HasExited);
            Assert.False(breakaway.HasExited);
        }
        finally
        {
            try { if (breakaway is { HasExited: false }) breakaway.Kill(entireProcessTree: true); } catch { }
            try { breakaway?.WaitForExit(5000); } catch { }
            breakaway?.Dispose();
            contained?.Dispose();
        }
    }

    [Fact]
    public void TwoProcessesReloadSettingsInsideLeaseWithoutLostUpdate()
    {
        using var tmp = new TempDir();
        var mutex = @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N");
        var record = Path.Combine(tmp.Path, "settings-owner.json");
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        var initial = Settings.Load(settingsPath);
        initial.NodePath = "preserved";
        using (MachineTransactionLease.Enter(
            "settings-fixture-init", mod: null, tmp.Path,
            recordPath: Path.Combine(tmp.Path, "settings-init-owner.json"),
            mutexName: @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N")))
            initial.Save();
        var ownerMarker = Path.Combine(tmp.Path, "settings-owner.marker");
        var release = Path.Combine(tmp.Path, "settings.release");
        var contenderMarker = Path.Combine(tmp.Path, "settings-contender.marker");
        using var owner = StartWorker(
            FindWorker(), "settings-owner", mutex, record, tmp.Path,
            ownerMarker, release, 5000);
        WaitForFile(ownerMarker, owner);
        using var contender = StartWorker(
            FindWorker(), "settings-contender", mutex, record, tmp.Path,
            contenderMarker, release, 5000);
        Thread.Sleep(100);
        Assert.False(File.Exists(contenderMarker));
        File.WriteAllText(release, "release");
        Assert.True(owner.WaitForExit(10_000));
        Assert.Equal(0, owner.ExitCode);
        Assert.True(contender.WaitForExit(10_000));
        Assert.Equal(0, contender.ExitCode);

        var final = Settings.Load(settingsPath);
        Assert.Equal("owner-write", final.VmbRoot);
        Assert.Equal("contender-write", final.SteamRoot);
        Assert.Equal("preserved", final.NodePath);
    }

    [Fact]
    public async Task NestedGuiBorrowClosesWithoutReleasingOuterLease()
    {
        using var tmp = new TempDir();
        var mutex = @"Local\VMBLauncher.Tests." + Guid.NewGuid().ToString("N");
        var record = Path.Combine(tmp.Path, "gui-nested-owner.json");
        var settingsPath = Path.Combine(tmp.Path, "settings.json");
        var outer = GuiSettingsTransaction.Enter(
            "gui-first-run", timeout: TimeSpan.FromSeconds(1),
            recordPath: record, mutexName: mutex);
        try
        {
            using (var child = outer.Borrow("gui-settings-dialog-nested"))
            {
                var settings = child.Reload(settingsPath);
                settings.VmbRoot = "child";
                child.Save(settings);
            }
            var afterChild = outer.Reload(settingsPath);
            afterChild.SteamRoot = "outer";
            outer.Save(afterChild);

            Task<Exception?> blocked;
            using (ExecutionContext.SuppressFlow())
            {
                blocked = Task.Run(() =>
                {
                    try
                    {
                        using var contender = MachineTransactionLease.Enter(
                            "gui-contender", mod: null, projectRoot: null,
                            timeout: TimeSpan.FromMilliseconds(100),
                            recordPath: record, mutexName: mutex);
                        return null;
                    }
                    catch (Exception ex) { return ex; }
                });
            }
            Assert.IsType<TimeoutException>(await blocked);
        }
        finally { outer.Dispose(); }

        using var acquired = MachineTransactionLease.Enter(
            "gui-contender", mod: null, projectRoot: null,
            timeout: TimeSpan.FromSeconds(1),
            recordPath: record, mutexName: mutex);
        var final = Settings.Load(settingsPath);
        Assert.Equal("child", final.VmbRoot);
        Assert.Equal("outer", final.SteamRoot);
    }

    private static long GetStartTicks(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.StartTime.ToUniversalTime().Ticks; }
        catch { return 0; }
    }

    private static bool IsAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch { return false; }
    }

    private static Process StartWorker(
        string worker,
        string mode,
        string semaphore,
        string record,
        string root,
        string marker,
        string release,
        int timeoutMs)
    {
        var info = new ProcessStartInfo
        {
            FileName = worker,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var value in new[]
        {
            mode, semaphore, record, root, marker, release, timeoutMs.ToString()
        }) info.ArgumentList.Add(value);
        return Process.Start(info)
            ?? throw new InvalidOperationException("could not start transaction worker");
    }

    private static void WaitForFile(string path, Process process)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(path))
        {
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"worker exited {process.ExitCode}: {process.StandardError.ReadToEnd()}");
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"marker timeout: {path}");
            Thread.Sleep(20);
        }
    }

    private static string ReadAllTextRetry(string path, Process process)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (DateTime.UtcNow <= deadline) { Thread.Sleep(20); }
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"worker exited {process.ExitCode}: {process.StandardError.ReadToEnd()}");
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"stable marker timeout: {path}");
        }
    }

    private static string FindWorker()
    {
        var testsRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", ".."));
        var candidate = Path.Combine(
            testsRoot,
            "TransactionLeaseWorker",
            "bin",
            "Debug",
            "net9.0-windows",
            "VmbLauncher.TransactionLeaseWorker.exe");
        Assert.True(File.Exists(candidate), $"transaction worker missing: {candidate}");
        return candidate;
    }
}
