using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace VmbLauncher.Services;

/// <summary>
/// Places the launcher itself in a kill-on-close Windows Job before any
/// mutation. Every subsequently spawned VMB, Stingray, ugc_tool, git, gh, ssh,
/// or helper descendant inherits the job atomically at process creation. A hard
/// launcher death closes the process-owned job handle and terminates the whole
/// descendant tree before another transaction can safely proceed.
/// </summary>
internal static class ProcessTreeGuard
{
    private static readonly object Sync = new();
    private static SafeJobHandle? _processJob;
    private static string? _jobName;

    internal static string EnsureCurrentProcessContained()
    {
        lock (Sync)
        {
            if (_processJob != null) return _jobName!;
            using var currentIdentity = Process.GetCurrentProcess();
            var name = $@"Global\Ensrick.VMBLauncher.Transaction.Process.{currentIdentity.Id}.{currentIdentity.StartTime.ToUniversalTime().Ticks}.{Guid.NewGuid():N}";
            var handle = CreateJobObjectW(IntPtr.Zero, name);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create process-tree containment job.");
            try
            {
                var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        // Only the explicitly audited non-mutation shell boundary
                        // requests breakaway. VMB/Stingray/git/gh/ugc_tool children
                        // continue to inherit this kill-on-close job atomically.
                        LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitBreakawayOk,
                    },
                };
                var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var memory = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(limits, memory, false);
                    if (!SetInformationJobObject(handle, 9, memory, (uint)size))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not configure process-tree containment job.");
                }
                finally { Marshal.FreeHGlobal(memory); }

                using var current = Process.GetCurrentProcess();
                if (!AssignProcessToJobObject(handle, current.Handle))
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not prove process-tree containment; refusing mutation before starting any child process.");
                _processJob = handle;
                _jobName = name;
                return name;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
    }

    internal static void WaitForRecordedJobToDrain(string jobName, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(jobName) ||
            !jobName.StartsWith(@"Global\Ensrick.VMBLauncher.Transaction.Process.", StringComparison.Ordinal))
            throw new InvalidDataException("Abandoned transaction record has no valid process-tree job identity.");
        var deadline = timeout == Timeout.InfiniteTimeSpan
            ? DateTime.MaxValue
            : DateTime.UtcNow + timeout;
        while (true)
        {
            uint activeProcesses;
            using (var job = OpenJobObjectW(JobObjectQuery, false, jobName))
            {
                if (job.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorFileNotFound) return;
                    throw new Win32Exception(error, "Could not open abandoned transaction process-tree job.");
                }
                activeProcesses = QueryActiveProcessCount(job);
            }

            // A Job object is not ordinarily signalled when ActiveProcesses
            // reaches zero. Close each temporary query handle immediately: if
            // the dead owner was still releasing its last KILL_ON_JOB_CLOSE
            // handle, retaining ours would prevent the kernel kill from firing.
            if (activeProcesses == 0) return;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"Abandoned transaction process tree retained {activeProcesses} active process(es) beyond the recovery timeout.");
            Thread.Sleep(20);
        }
    }

    internal static void TerminateAndWaitForResidualDescendants(TimeSpan timeout)
    {
        SafeJobHandle job;
        lock (Sync)
            job = _processJob ?? throw new InvalidOperationException("Process-tree containment is not active.");
        using var current = Process.GetCurrentProcess();
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var active = QueryActiveProcessIds(job);
            var residual = active.Where(pid => pid != (uint)current.Id).ToArray();
            if (residual.Length == 0) return;
            foreach (var pid in residual)
            {
                try
                {
                    using var process = Process.GetProcessById(checked((int)pid));
                    if (!process.HasExited)
                    {
                        if (!IsProcessInJob(process.Handle, job, out var belongs))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not authenticate residual process job membership.");
                        if (belongs) process.Kill(entireProcessTree: true);
                    }
                }
                catch (ArgumentException) { }
            }
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"Transaction process tree retained descendant PID(s) {string.Join(",", residual)}; mutex was not released.");
            Thread.Sleep(20);
        }
    }

    /// <summary>
    /// Starts an explicitly non-mutating user shell process outside the launcher
    /// transaction job. Do not use this for VMB, Stingray, git, gh, ugc_tool, ssh,
    /// upload helpers, or any process capable of project/shared-state mutation.
    /// </summary>
    internal static Process StartBreakawayExplorer(IEnumerable<string> arguments)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows))
            throw new InvalidOperationException("The canonical Windows directory is unavailable.");
        var explorer = Path.GetFullPath(Path.Combine(windows, "explorer.exe"));
        if (!File.Exists(explorer) ||
            !string.Equals(Path.GetDirectoryName(explorer)?.TrimEnd('\\'),
                Path.GetFullPath(windows).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The canonical Windows Explorer executable is unavailable.");
        return StartBreakawayProcess(explorer, arguments, createNoWindow: false);
    }

#if VMBLAUNCHER_TEST_HOOKS
    /// <summary>Test-fixture entry point; arbitrary breakaway is absent from shipping builds.</summary>
    internal static Process StartBreakawayProcessForTest(
        string fullPath, IEnumerable<string> arguments, bool createNoWindow = true)
    {
        if (!Path.IsPathFullyQualified(fullPath) || !File.Exists(fullPath))
            throw new ArgumentException("A test breakaway executable must be an existing absolute path.", nameof(fullPath));
        return StartBreakawayProcess(Path.GetFullPath(fullPath), arguments, createNoWindow);
    }
#endif

    private static Process StartBreakawayProcess(
        string fullPath, IEnumerable<string> arguments, bool createNoWindow)
    {
        if (!Path.IsPathFullyQualified(fullPath) || !File.Exists(fullPath))
            throw new ArgumentException("A breakaway executable must be an existing absolute path.", nameof(fullPath));
        EnsureCurrentProcessContained();

        var commandLine = new System.Text.StringBuilder(QuoteArgument(fullPath));
        foreach (var argument in arguments)
            commandLine.Append(' ').Append(QuoteArgument(argument));
        var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        var flags = CreateBreakawayFromJob | CreateUnicodeEnvironment;
        if (createNoWindow) flags |= CreateNoWindow;
        if (!CreateProcessW(
                fullPath, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags,
                IntPtr.Zero, null, ref startup, out var processInfo))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not start audited breakaway process '{fullPath}'.");
        try
        {
            return Process.GetProcessById(checked((int)processInfo.dwProcessId));
        }
        finally
        {
            if (processInfo.hThread != IntPtr.Zero) CloseHandle(processInfo.hThread);
            if (processInfo.hProcess != IntPtr.Zero) CloseHandle(processInfo.hProcess);
        }
    }

    private static string QuoteArgument(string value)
    {
        if (value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"'))
            return value;
        var result = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }

    private static uint[] QueryActiveProcessIds(SafeJobHandle job)
    {
        const int bytes = 65536;
        var memory = Marshal.AllocHGlobal(bytes);
        try
        {
            if (!QueryInformationJobObject(job, 3, memory, (uint)bytes, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not query transaction process-tree membership.");
            var active = Marshal.ReadInt32(memory, 4);
            var result = new uint[active];
            for (var i = 0; i < active; i++)
                result[i] = checked((uint)Marshal.ReadIntPtr(memory, 8 + i * IntPtr.Size).ToInt64());
            return result;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static uint QueryActiveProcessCount(SafeJobHandle job)
    {
        var size = Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(job, 1, memory, (uint)size, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Could not query abandoned transaction process-tree accounting.");
            return Marshal.PtrToStructure<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(memory).ActiveProcesses;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

#if VMBLAUNCHER_TEST_HOOKS
    internal static IDisposable CreateOrdinarilyEmptiedJobForTest(
        string jobName, string fullPath, IEnumerable<string> arguments)
    {
        var job = CreateJobObjectW(IntPtr.Zero, jobName);
        if (job.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create planted empty Job fixture.");
        try
        {
            using var child = StartBreakawayProcessForTest(fullPath, arguments, createNoWindow: true);
            if (!AssignProcessToJobObject(job, child.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign planted Job fixture child.");
            if (!child.WaitForExit(10_000))
                throw new TimeoutException("Planted Job fixture child did not exit.");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (QueryActiveProcessCount(job) != 0)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException("Planted Job fixture did not ordinarily drain to zero active processes.");
                Thread.Sleep(20);
            }
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }
#endif

    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint JobObjectLimitBreakawayOk = 0x00000800;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint JobObjectQuery = 0x0004;
    private const int ErrorFileNotFound = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle OpenJobObjectW(uint desiredAccess, bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        SafeJobHandle job, int informationClass, IntPtr information,
        uint informationLength, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(
        IntPtr process, SafeJobHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job, int informationClass, IntPtr information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? applicationName,
        System.Text.StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
