using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Collections.Concurrent;

namespace VmbLauncher.Services;

/// <summary>
/// Serializes every VMB/Stingray mutation on the machine. Mutex ownership is
/// confined to a dedicated thread, so asynchronous continuations never violate
/// Mutex thread affinity and a hard process death produces an abandoned mutex
/// instead of a permanently decremented semaphore.
/// </summary>
internal sealed class MachineTransactionLease : IDisposable
{
    internal const string MutexName = @"Global\Ensrick.VMBLauncher.Transaction.v1";
    internal const string LeaseIdEnvironmentVariable = "VMBLAUNCHER_TRANSACTION_LEASE_ID";
    internal const string OwnerPidEnvironmentVariable = "VMBLAUNCHER_TRANSACTION_OWNER_PID";
    internal const string OwnerStartEnvironmentVariable = "VMBLAUNCHER_TRANSACTION_OWNER_START_UTC_TICKS";
    internal const string RecordPathEnvironmentVariable = "VMBLAUNCHER_TRANSACTION_RECORD_PATH";
    internal const string TestModeEnvironmentVariable = "VMBLAUNCHER_TRANSACTION_TEST_MODE";
    internal const string TestMutexEnvironmentVariable = "VMBLAUNCHER_TRANSACTION_TEST_MUTEX_NAME";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private static readonly AsyncLocal<LeaseState?> Current = new();
    private static readonly ConcurrentDictionary<string, Mutex> RetainedAbandonedHandles =
        new(StringComparer.Ordinal);

    private readonly LeaseState _state;
    private bool _disposed;

    private MachineTransactionLease(LeaseState state) => _state = state;

    internal static string DefaultRecordPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VMBLauncher", "transaction_lease.json");

    internal static TransactionIdentity? CurrentIdentity => Current.Value?.Identity;


    /// <summary>
    /// Narrows a directly owned wildcard root after settings have been reloaded
    /// under the mutex. The durable record is replaced before any mutation.
    /// Wrapper-owned records remain the parent's authority and cannot be edited
    /// by a child.
    /// </summary>
    internal static void RefineOwnedProjectRoot(string? projectRoot)
    {
        var state = Current.Value
            ?? throw new InvalidOperationException("No machine transaction is active.");
        var normalized = NormalizeOptionalPath(projectRoot);
        if (normalized == null) return;
        if (state.Identity.ProjectRoot != null)
        {
            ValidateScope(state.Identity, state.Identity.Mod, normalized, "transaction refinement");
            return;
        }
        if (!state.OwnsRecord) return;
        var previous = state.Identity.ProjectRoot;
        state.Identity.ProjectRoot = normalized;
        try { WriteRecordAtomic(state.RecordPath, state.Identity); }
        catch
        {
            state.Identity.ProjectRoot = previous;
            throw;
        }
        state.Log?.Invoke($"[transaction-lease] refined root={normalized}");
    }

    internal static void RequireCurrent(string operation)
    {
        var state = Current.Value
            ?? throw new InvalidOperationException($"{operation} requires the authenticated machine transaction.");
        var record = ReadRecord(state.RecordPath);
        if (record?.Schema != 2 ||
            !FixedEquals(record.LeaseId, state.Identity.LeaseId) ||
            record.OwnerPid != state.Identity.OwnerPid ||
            record.OwnerStartUtcTicks != state.Identity.OwnerStartUtcTicks ||
            record.SessionId != state.Identity.SessionId ||
            !string.Equals(record.Action, state.Identity.Action, StringComparison.Ordinal) ||
            !string.Equals(record.Mod, state.Identity.Mod, StringComparison.OrdinalIgnoreCase) ||
            !PathEquals(record.ProjectRoot, state.Identity.ProjectRoot) ||
            !string.Equals(record.ProcessTreeJobName, state.Identity.ProcessTreeJobName, StringComparison.Ordinal) ||
            !ProcessMatches(state.Identity.OwnerPid, state.Identity.OwnerStartUtcTicks))
            throw new InvalidOperationException(
                $"{operation} lost its authenticated machine transaction owner record.");
    }

    internal static MachineTransactionLease Enter(
        string action,
        string? mod,
        string? projectRoot,
        Action<string>? log = null,
        TimeSpan? timeout = null,
        string? recordPath = null,
        string? mutexName = null)
    {
        var processTreeJobName = ProcessTreeGuard.EnsureCurrentProcessContained();
        var normalizedRoot = NormalizeOptionalPath(projectRoot);
        var existing = Current.Value;
        if (existing != null)
        {
            ValidateScope(existing.Identity, mod, normalizedRoot, "nested transaction");
            existing.ReferenceCount++;
            return new MachineTransactionLease(existing);
        }

        var actualRecordPath = Path.GetFullPath(
            recordPath ?? Environment.GetEnvironmentVariable(RecordPathEnvironmentVariable) ?? DefaultRecordPath);
        var actualMutexName = ResolveMutexName(mutexName);
        var inheritedLeaseId = Environment.GetEnvironmentVariable(LeaseIdEnvironmentVariable);
        var state = string.IsNullOrWhiteSpace(inheritedLeaseId)
            ? AcquireOwnedLease(mod, normalizedRoot, action, actualRecordPath, actualMutexName, processTreeJobName, log, timeout ?? DefaultTimeout)
            : JoinWrapperLease(inheritedLeaseId, mod, normalizedRoot, action, actualRecordPath, actualMutexName, log);
        Current.Value = state;
        return new MachineTransactionLease(state);
    }

    private static string ResolveMutexName(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested;
#if VMBLAUNCHER_TEST_HOOKS
        if (string.Equals(Environment.GetEnvironmentVariable(TestModeEnvironmentVariable), "1", StringComparison.Ordinal) &&
            Environment.GetEnvironmentVariable(TestMutexEnvironmentVariable) is string testName &&
            testName.StartsWith(@"Local\VMBLauncher.Tests.", StringComparison.Ordinal))
            return testName;
#endif
        return MutexName;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.ReferenceCount--;
        if (_state.ReferenceCount > 0) return;

        Current.Value = null;
        var mutexReleased = false;
        try
        {
            ProcessTreeGuard.TerminateAndWaitForResidualDescendants(TimeSpan.FromSeconds(10));
            if (_state.OwnsRecord)
                DeleteRecordOnlyIfOwned(_state.RecordPath, _state.Identity.LeaseId);
            _state.MutexThread.Dispose();
            mutexReleased = true;
            _state.Log?.Invoke(
                $"[transaction-lease] released owner_pid={_state.Identity.OwnerPid} " +
                $"session={_state.Identity.SessionId} action={_state.Identity.Action} " +
                $"mod={_state.Identity.Mod ?? "(all)"} root={_state.Identity.ProjectRoot ?? "(none)"}");
        }
        catch (Exception ex)
        {
            Exception? abandonFailure = null;
            if (!mutexReleased && _state.OwnsRecord)
            {
                try
                {
                    RetainAbandonedHandle(
                        _state.MutexName, _state.MutexThread.Abandon());
                }
                catch (Exception abandonEx) { abandonFailure = abandonEx; }
            }
            _state.Log?.Invoke(
                $"[transaction-lease] CRITICAL {(mutexReleased ? "mutex released after authenticated cleanup" : "mutex re-abandoned; mutation remains fail-closed")} " +
                $"owner_pid={_state.Identity.OwnerPid} action={_state.Identity.Action}: {ex.Message}" +
                (abandonFailure == null ? "" : $"; abandon failed: {abandonFailure.Message}"));
            if (abandonFailure != null) throw new AggregateException(ex, abandonFailure);
            throw;
        }
    }

    private static LeaseState AcquireOwnedLease(
        string? mod, string? projectRoot, string action, string recordPath,
        string mutexName, string processTreeJobName, Action<string>? log, TimeSpan timeout)
    {
        var started = Stopwatch.StartNew();
        DedicatedMutexThread holder;
        try { holder = DedicatedMutexThread.Acquire(mutexName, timeout); }
        catch (TimeoutException ex)
        {
            var live = ReadRecord(recordPath);
            var owner = live == null
                ? ""
                : $" owner_pid={live.OwnerPid} session={live.SessionId} action={live.Action} " +
                  $"mod={live.Mod ?? "(all)"} root={live.ProjectRoot ?? "(none)"} acquired_utc={live.AcquiredUtc:o}.";
            throw new TimeoutException(ex.Message + owner + " No mutation was started.", ex);
        }
        ReleaseRetainedAbandonedHandle(mutexName);
        try
        {
            RecoverPriorAuthority(recordPath, holder.WasAbandoned, timeout);
        }
        catch
        {
            // Acquisition transferred ownership to this dedicated thread. End
            // it without ReleaseMutex, but retain its kernel handle so the next
            // same-process contender receives abandonment again. This applies
            // to both kernel-abandoned and fresh-object/durable-record recovery.
            RetainAbandonedHandle(mutexName, holder.Abandon());
            throw;
        }
        try
        {
            using var process = Process.GetCurrentProcess();
            var identity = new TransactionIdentity
            {
                Schema = 2,
                LeaseId = Guid.NewGuid().ToString("N"),
                OwnerPid = process.Id,
                OwnerStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                SessionId = process.SessionId,
                Action = action,
                Mod = string.IsNullOrWhiteSpace(mod) ? null : mod,
                ProjectRoot = projectRoot,
                AcquiredUtc = DateTime.UtcNow,
                ProcessTreeJobName = processTreeJobName,
            };
            WriteRecordAtomic(recordPath, identity);
            log?.Invoke(
                $"[transaction-lease] acquired owner_pid={identity.OwnerPid} session={identity.SessionId} " +
                $"action={identity.Action} mod={identity.Mod ?? "(all)"} root={identity.ProjectRoot ?? "(none)"} " +
                $"wait_ms={started.ElapsedMilliseconds}");
            return new LeaseState(holder, ownsRecord: true, identity, recordPath, mutexName, log);
        }
        catch
        {
            holder.Dispose();
            throw;
        }
    }

    private static void RecoverPriorAuthority(
        string recordPath, bool wasAbandoned, TimeSpan timeout)
    {
        var recordExists = File.Exists(recordPath);
        var stale = recordExists ? ReadRecord(recordPath) : null;
        if (stale == null)
        {
            if (recordExists || wasAbandoned)
                throw new InvalidDataException(
                    "The machine transaction has prior ownership evidence but its durable owner record is missing or unreadable; refusing recovery.");
            return;
        }
        if (stale.Schema != 2)
            throw new InvalidDataException(
                $"The prior machine transaction owner record has unsupported schema {stale.Schema}; refusing recovery.");
        if (ProcessMatches(stale.OwnerPid, stale.OwnerStartUtcTicks))
            throw new InvalidDataException(
                $"The machine mutex was available while recorded owner PID {stale.OwnerPid} is still live; refusing inconsistent authority.");
        ProcessTreeGuard.WaitForRecordedJobToDrain(stale.ProcessTreeJobName, timeout);
    }

    private static void RetainAbandonedHandle(string mutexName, Mutex handle)
    {
        RetainedAbandonedHandles.AddOrUpdate(
            mutexName,
            handle,
            (_, previous) =>
            {
                previous.Dispose();
                return handle;
            });
    }

    private static void ReleaseRetainedAbandonedHandle(string mutexName)
    {
        if (RetainedAbandonedHandles.TryRemove(mutexName, out var retained))
            retained.Dispose();
    }

    private static LeaseState JoinWrapperLease(
        string inheritedLeaseId, string? mod, string? projectRoot, string action,
        string recordPath, string mutexName, Action<string>? log)
    {
        var record = ReadRecord(recordPath)
            ?? throw new InvalidOperationException(
                "A wrapper transaction token was inherited, but its machine owner record is missing or unreadable.");
        if (record.Schema != 2 ||
            string.IsNullOrWhiteSpace(record.ProcessTreeJobName) ||
            !record.ProcessTreeJobName.StartsWith(@"Global\Ensrick.VMBLauncher.Transaction.Process.", StringComparison.Ordinal) ||
            !FixedEquals(record.LeaseId, inheritedLeaseId) ||
            !int.TryParse(Environment.GetEnvironmentVariable(OwnerPidEnvironmentVariable), out var inheritedPid) ||
            !long.TryParse(Environment.GetEnvironmentVariable(OwnerStartEnvironmentVariable), out var inheritedStart) ||
            inheritedPid != record.OwnerPid || inheritedStart != record.OwnerStartUtcTicks)
            throw new InvalidOperationException(
                "Refusing wrapper transaction join: inherited identity does not match the machine owner record.");

        var parentPid = GetImmediateParentPid();
        if (parentPid != record.OwnerPid || !ProcessMatches(record.OwnerPid, record.OwnerStartUtcTicks))
            throw new InvalidOperationException(
                "Refusing wrapper transaction join: the recorded live owner is not this launcher's immediate parent.");
        ValidateScope(record, mod, projectRoot, "wrapper transaction");

        // Prove the parent owns the named mutex on a disposable dedicated
        // thread. Probing on this caller thread would make an abandonment race
        // impossible to preserve without terminating the caller.
        DedicatedMutexThread? probe = null;
        try
        {
            probe = DedicatedMutexThread.Acquire(mutexName, TimeSpan.Zero);
        }
        catch (TimeoutException)
        {
            // Expected: the authenticated live parent still owns the mutex.
        }
        if (probe != null)
        {
            if (probe.WasAbandoned)
                RetainAbandonedHandle(mutexName, probe.Abandon());
            else
                probe.Dispose();
            throw new InvalidOperationException(probe.WasAbandoned
                ? "Refusing wrapper transaction join: the parent transaction was already abandoned."
                : "Refusing wrapper transaction join: the recorded parent does not hold the machine mutex.");
        }

        var watcher = DedicatedMutexThread.WatchParent(mutexName);
        log?.Invoke(
            $"[transaction-lease] joined owner_pid={record.OwnerPid} session={record.SessionId} " +
            $"action={action} owner_action={record.Action} mod={record.Mod ?? "(all)"} " +
            $"root={record.ProjectRoot ?? "(none)"}");
        return new LeaseState(watcher, ownsRecord: false, record, recordPath, mutexName, log);
    }

    private static void ValidateScope(TransactionIdentity identity, string? mod, string? projectRoot, string label)
    {
        if (!string.IsNullOrWhiteSpace(identity.Mod) && !string.IsNullOrWhiteSpace(mod) &&
            !string.Equals(identity.Mod, mod, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing {label}: mod differs from the owning transaction.");
        if (identity.ProjectRoot != null && projectRoot != null && !PathEquals(identity.ProjectRoot, projectRoot))
            throw new InvalidOperationException($"Refusing {label}: project root differs from the owning transaction.");
    }

    private static int GetImmediateParentPid()
    {
        var status = NtQueryInformationProcess(Process.GetCurrentProcess().Handle, 0, out var info,
            Marshal.SizeOf<ProcessBasicInformation>(), out _);
        if (status != 0)
            throw new InvalidOperationException($"Could not validate wrapper parent process (NTSTATUS 0x{status:x8}).");
        return checked((int)info.InheritedFromUniqueProcessId.ToInt64());
    }

    internal static bool ProcessMatches(int pid, long startUtcTicks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime().Ticks == startUtcTicks && !process.HasExited;
        }
        catch { return false; }
    }

    private static bool PathEquals(string? left, string? right)
    {
        if (left == null || right == null) return left == right;
        return string.Equals(NormalizeOptionalPath(left), NormalizeOptionalPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool FixedEquals(string? left, string? right)
    {
        if (left == null || right == null) return false;
        var a = System.Text.Encoding.UTF8.GetBytes(left);
        var b = System.Text.Encoding.UTF8.GetBytes(right);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    internal static bool LeaseIdsEqual(string? left, string? right) => FixedEquals(left, right);

    private static TransactionIdentity? ReadRecord(string path)
    {
        try { return JsonSerializer.Deserialize<TransactionIdentity>(File.ReadAllText(path), JsonOptions); }
        catch { return null; }
    }

    private static void WriteRecordAtomic(string path, TransactionIdentity identity)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Transaction owner record has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity, JsonOptions));
            using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    private static void DeleteRecordOnlyIfOwned(string path, string leaseId)
    {
        if (!File.Exists(path))
            throw new InvalidDataException(
                "Transaction owner record disappeared before mutex release.");
        var record = ReadRecord(path)
            ?? throw new InvalidDataException(
                "Transaction owner record became unreadable before mutex release.");
        if (!FixedEquals(record.LeaseId, leaseId))
            throw new InvalidDataException(
                "Transaction owner record identity changed before mutex release.");
        File.Delete(path);
        if (File.Exists(path))
            throw new IOException("Transaction owner record still exists after authenticated deletion.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private sealed class LeaseState
    {
        internal DedicatedMutexThread MutexThread { get; }
        internal bool OwnsRecord { get; }
        internal TransactionIdentity Identity { get; }
        internal string RecordPath { get; }
        internal string MutexName { get; }
        internal Action<string>? Log { get; }
        internal int ReferenceCount { get; set; } = 1;

        internal LeaseState(DedicatedMutexThread mutexThread, bool ownsRecord,
            TransactionIdentity identity, string recordPath, string mutexName, Action<string>? log)
        {
            MutexThread = mutexThread;
            OwnsRecord = ownsRecord;
            Identity = identity;
            RecordPath = recordPath;
            MutexName = mutexName;
            Log = log;
        }
    }

    private sealed class DedicatedMutexThread : IDisposable
    {
        private readonly ManualResetEvent _release = new(false);
        private readonly ManualResetEvent _abandon = new(false);
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Thread _thread;
        private Mutex? _mutex;
        private bool _closed;
        private Exception? _failure;
        private static int _activeCount;
        internal bool WasAbandoned { get; private set; }
        internal static int ActiveCount => Volatile.Read(ref _activeCount);

        private DedicatedMutexThread(string name, TimeSpan? acquireTimeout, bool watcher)
        {
            _thread = new Thread(() => Run(name, acquireTimeout, watcher))
            {
                IsBackground = true,
                Name = watcher ? "VMB transaction parent watcher" : "VMB transaction owner",
            };
            _thread.Start();
            _ready.Wait();
            if (_failure != null)
            {
                _mutex?.Dispose();
                _release.Dispose();
                _abandon.Dispose();
                _ready.Dispose();
                ExceptionDispatchInfo.Capture(_failure).Throw();
            }
        }

        internal static DedicatedMutexThread Acquire(string name, TimeSpan timeout) => new(name, timeout, false);
        internal static DedicatedMutexThread WatchParent(string name) => new(name, null, true);

        private void Run(string name, TimeSpan? timeout, bool watcher)
        {
            Interlocked.Increment(ref _activeCount);
            try
            {
                var mutex = watcher ? Mutex.OpenExisting(name) : new Mutex(false, name);
                _mutex = mutex;
                if (watcher)
                {
                    _ready.Set();
                    var index = WaitAnyTakingAbandoned(new WaitHandle[] { mutex, _release }, Timeout.Infinite);
                    if (index != 0) return;
                }
                else
                {
                    var acquired = WaitOneTakingAbandoned(mutex, timeout!.Value, out var abandoned);
                    WasAbandoned = abandoned;
                    if (!acquired)
                        throw new TimeoutException(
                            $"Another VMB/Stingray transaction held the machine mutex for more than {timeout.Value.TotalSeconds:0.###} seconds. " +
                            "No build, deploy, staging, upload, or parity mutation was started.");
                    _ready.Set();
                }
                var completion = WaitHandle.WaitAny(new WaitHandle[] { _release, _abandon });
                if (completion == 0) mutex.ReleaseMutex();
            }
            catch (Exception ex)
            {
                _failure = ex;
                _ready.Set();
            }
            finally { Interlocked.Decrement(ref _activeCount); }
        }

        private static bool WaitOneTakingAbandoned(Mutex mutex, TimeSpan timeout, out bool abandoned)
        {
            abandoned = false;
            try { return mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { abandoned = true; return true; }
        }

        private static int WaitAnyTakingAbandoned(WaitHandle[] handles, int timeout)
        {
            try { return WaitHandle.WaitAny(handles, timeout); }
            catch (AbandonedMutexException ex) { return ex.MutexIndex; }
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            _release.Set();
            if (!_thread.Join(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Transaction mutex thread did not stop.");
            _mutex?.Dispose();
            _release.Dispose();
            _abandon.Dispose();
            _ready.Dispose();
            if (_failure != null) throw new InvalidOperationException(_failure.Message, _failure);
        }

        internal Mutex Abandon()
        {
            if (_closed) throw new ObjectDisposedException(nameof(DedicatedMutexThread));
            _closed = true;
            _abandon.Set();
            if (!_thread.Join(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Transaction mutex thread did not abandon ownership.");
            _release.Dispose();
            _abandon.Dispose();
            _ready.Dispose();
            if (_failure != null) throw new InvalidOperationException(_failure.Message, _failure);
            return _mutex ?? throw new InvalidOperationException("Transaction mutex handle was unavailable.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        out ProcessBasicInformation processInformation, int processInformationLength, out int returnLength);

#if VMBLAUNCHER_TEST_HOOKS
    internal static int ActiveMutexThreadsForTest => DedicatedMutexThread.ActiveCount;
#endif
}

internal sealed class TransactionIdentity
{
    public int Schema { get; set; }
    public string LeaseId { get; set; } = "";
    public int OwnerPid { get; set; }
    public long OwnerStartUtcTicks { get; set; }
    public int SessionId { get; set; }
    public string Action { get; set; } = "";
    public string? Mod { get; set; }
    public string? ProjectRoot { get; set; }
    public DateTime AcquiredUtc { get; set; }
    public string ProcessTreeJobName { get; set; } = "";
}
