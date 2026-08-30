using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Runtime.InteropServices;

namespace VmbLauncher.Services;

/// <summary>
/// Kernel-backed target-membership arbitration for the final journal witness
/// transition. Only ERROR_OPERATION_ABORTED for this exact OVERLAPPED request
/// authorizes retirement. Every notification completion, error, and unresolved
/// cancellation is treated as changed/uncertain and retains the durable journal.
/// </summary>
internal static partial class LocalExactSetDeployment
{
    private enum MembershipArbitration
    {
        CancelWon,
        ChangedOrUncertain,
    }

    internal enum MembershipNativeCompletion
    {
        Terminal,
        TimedOut,
        SignaledButIncomplete,
    }

    internal enum MembershipDisposePlan
    {
        Release,
        CloseThenWait,
        CloseAndRetain,
    }

    internal enum MembershipPendingDisposePlan
    {
        ObserveCompletion,
        Retain,
    }

    internal static MembershipDisposePlan PlanMembershipNativeDisposal(
        MembershipNativeCompletion observation) => observation switch
        {
            MembershipNativeCompletion.Terminal => MembershipDisposePlan.Release,
            MembershipNativeCompletion.TimedOut => MembershipDisposePlan.CloseThenWait,
            MembershipNativeCompletion.SignaledButIncomplete =>
                MembershipDisposePlan.CloseAndRetain,
            _ => MembershipDisposePlan.CloseAndRetain,
        };

    internal static MembershipPendingDisposePlan PlanMembershipPendingDisposal(
        bool directoryUsable,
        bool overlappedAvailable) =>
        directoryUsable && overlappedAvailable
            ? MembershipPendingDisposePlan.ObserveCompletion
            : MembershipPendingDisposePlan.Retain;

    private sealed class DirectoryMembershipMonitor : IDisposable
    {
        private const uint FileNotifyChangeFileName = 0x00000001;
        private const uint FileNotifyChangeDirName = 0x00000002;
        private const uint FileFlagOverlapped = 0x40000000;
        private const int ErrorIoIncomplete = 996;
        private const int ErrorOperationAborted = 995;
        private const int ErrorNotFound = 1168;
        private const int BufferBytes = 64 * 1024;
        private static readonly TimeSpan CancellationTimeout = TimeSpan.FromSeconds(5);
        private static readonly object RetainedPendingGate = new();
        private static DirectoryMembershipMonitor? RetainedPendingHead;

        private enum RequestState
        {
            Unarmed,
            Pending,
            Terminal,
            Retained,
        }

        private SafeFileHandle? _directory;
        private SafeWaitHandle? _event;
        private IntPtr _buffer;
        private IntPtr _overlapped;
        private DirectoryMembershipMonitor? _retainedNext;
        private RequestState _state;

        private DirectoryMembershipMonitor() { }

        internal static DirectoryMembershipMonitor Arm(ExactDirectoryLease lease, string context)
        {
            lease.RequireCurrentPath(context);
            var monitor = new DirectoryMembershipMonitor();
            try
            {
                monitor._directory = CreateFileForMembershipMonitorW(
                    lease.CurrentPath,
                    FileListDirectory | FileReadAttributes,
                    FileShare.Read | FileShare.Write | FileShare.Delete,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagBackupSemantics | FileFlagOpenReparsePoint | FileFlagOverlapped,
                    IntPtr.Zero);
                if (monitor._directory.IsInvalid)
                    throw new IOException(
                        $"Cannot arm target membership monitor (Win32 {Marshal.GetLastWin32Error()}).");
                var identity = IdentityFromHandle(monitor._directory, lease.CurrentPath);
                RequireIdentity(identity, lease.Identity, context);
                monitor._event = CreateEventW(
                    IntPtr.Zero,
                    manualReset: true,
                    initialState: false,
                    name: null);
                if (monitor._event.IsInvalid)
                    throw new IOException(
                        $"Cannot create target membership monitor event (Win32 {Marshal.GetLastWin32Error()}).");
                monitor._buffer = Marshal.AllocHGlobal(BufferBytes);
                monitor._overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlappedState>());
                Marshal.StructureToPtr(new NativeOverlappedState
                {
                    EventHandle = monitor._event.DangerousGetHandle(),
                }, monitor._overlapped, fDeleteOld: false);
                if (!ReadDirectoryChangesW(
                        monitor._directory,
                        monitor._buffer,
                        BufferBytes,
                        watchSubtree: false,
                        FileNotifyChangeFileName | FileNotifyChangeDirName,
                        out _,
                        monitor._overlapped,
                        IntPtr.Zero))
                    throw new IOException(
                        $"Cannot arm target membership notification (Win32 {Marshal.GetLastWin32Error()}).");
                monitor._state = RequestState.Pending;
                Checkpoint("membership-monitor-armed");
                return monitor;
            }
            catch
            {
                monitor.Dispose();
                throw;
            }
        }

        internal MembershipArbitration CancelAndArbitrate()
        {
            if (_state != RequestState.Pending ||
                _directory == null || _directory.IsInvalid || _directory.IsClosed ||
                _overlapped == IntPtr.Zero)
                throw new InvalidOperationException("Target membership monitor is not armed.");

            var cancelReturned = CancelIoEx(_directory, _overlapped);
            var cancelError = cancelReturned ? 0 : Marshal.GetLastWin32Error();
            if (!cancelReturned && cancelError != ErrorNotFound)
            {
                // This path has not first proved the manual-reset event was
                // unsignaled. Closing cannot make a preexisting signal new
                // terminal evidence, so retain the request storage outright.
                RetainAndClosePendingRequest();
                throw new IOException(
                    $"Target membership monitor cancellation failed (Win32 {cancelError}).");
            }

            if (!WaitForTerminalEvent(CancellationTimeout))
            {
                CloseAfterTimedOutWaitAndAwaitTerminalOrRetain();
                throw new IOException(
                    "Target membership monitor cancellation did not reach terminal completion within the bounded wait.");
            }
            var completed = GetOverlappedResult(
                _directory,
                _overlapped,
                out _,
                wait: false);
            var completionError = completed ? 0 : Marshal.GetLastWin32Error();
            if (!completed && completionError == ErrorIoIncomplete)
            {
                // A signaled event paired with ERROR_IO_INCOMPLETE is not safe
                // authority to release memory still owned by the native request.
                RetainAndClosePendingRequest();
                throw new IOException(
                    "Target membership monitor reported a nonterminal completion after signaling.");
            }
            _state = RequestState.Terminal;
            Checkpoint("membership-monitor-terminal");

            // CancelIoEx returning true plus ERROR_OPERATION_ABORTED is the
            // only status that authorizes retirement. Normal completion,
            // ERROR_NOT_FOUND races, and every other status fail closed.
            if (cancelReturned && !completed && completionError == ErrorOperationAborted)
            {
                Checkpoint("membership-monitor-cancel-won");
                return MembershipArbitration.CancelWon;
            }
            Checkpoint("membership-monitor-change-won");
            return MembershipArbitration.ChangedOrUncertain;
        }

        private bool WaitForTerminalEvent(TimeSpan timeout)
        {
            if (_event == null || _event.IsInvalid || _event.IsClosed) return false;
            using var wait = new EventWaitHandle(false, EventResetMode.ManualReset);
            wait.SafeWaitHandle = new SafeWaitHandle(
                _event.DangerousGetHandle(),
                ownsHandle: false);
            return wait.WaitOne(timeout);
        }

        private void CloseAfterTimedOutWaitAndAwaitTerminalOrRetain()
        {
            try
            {
                _directory?.Dispose();
                if (WaitForTerminalEvent(CancellationTimeout))
                {
                    _state = RequestState.Terminal;
                    return;
                }
            }
            finally
            {
                // A resource failure while constructing or performing the
                // post-close wait is not terminal-completion evidence.
                if (_state != RequestState.Terminal)
                    RetainPendingRequest();
            }
        }

        private void RetainPendingRequest()
        {
            if (_state == RequestState.Retained) return;
            lock (RetainedPendingGate)
            {
                if (_state == RequestState.Retained) return;
                // An intrusive chain establishes the process-lifetime root
                // without a List growth allocation that could fail between
                // changing state and retaining this monitor.
                _retainedNext = RetainedPendingHead;
                RetainedPendingHead = this;
                _state = RequestState.Retained;
            }
        }

        private void RetainAndClosePendingRequest()
        {
            // Establish the process-lifetime root before closing. Even if
            // SafeHandle disposal fails, no caller can subsequently release
            // storage still owned by the unresolved native request.
            RetainPendingRequest();
            try { _directory?.Dispose(); }
            catch { }
        }

        private MembershipNativeCompletion ObserveCompletionForDispose()
        {
            if (!WaitForTerminalEvent(CancellationTimeout))
                return MembershipNativeCompletion.TimedOut;
            var completed = GetOverlappedResult(
                _directory!,
                _overlapped,
                out _,
                wait: false);
            var completionError = completed ? 0 : Marshal.GetLastWin32Error();
            if (!completed && completionError == ErrorIoIncomplete)
                return MembershipNativeCompletion.SignaledButIncomplete;
            _state = RequestState.Terminal;
            return MembershipNativeCompletion.Terminal;
        }

        public void Dispose()
        {
            if (_state == RequestState.Pending)
            {
                var directoryUsable =
                    _directory is { IsInvalid: false, IsClosed: false };
                if (PlanMembershipPendingDisposal(
                        directoryUsable,
                        _overlapped != IntPtr.Zero) ==
                    MembershipPendingDisposePlan.Retain)
                {
                    RetainAndClosePendingRequest();
                    return;
                }

                try { CancelIoEx(_directory!, _overlapped); }
                catch { }
                try
                {
                    var observation = ObserveCompletionForDispose();
                    switch (PlanMembershipNativeDisposal(observation))
                    {
                        case MembershipDisposePlan.Release:
                            break;
                        case MembershipDisposePlan.CloseThenWait:
                            CloseAfterTimedOutWaitAndAwaitTerminalOrRetain();
                            break;
                        case MembershipDisposePlan.CloseAndRetain:
                        default:
                            // A manual-reset event observed before
                            // ERROR_IO_INCOMPLETE remains signaled after close.
                            // Never re-wait that stale signal.
                            RetainAndClosePendingRequest();
                            break;
                    }
                }
                catch
                {
                    RetainAndClosePendingRequest();
                }
            }
            if (_state == RequestState.Pending)
            {
                // Only Unarmed (no request issued) and Terminal requests own
                // releasable native storage. Any unresolved Pending fallback
                // is retained, irrespective of SafeHandle state.
                RetainAndClosePendingRequest();
                return;
            }
            if (_state == RequestState.Retained) return;
            _directory?.Dispose();
            _directory = null;
            _event?.Dispose();
            _event = null;
            if (_overlapped != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_overlapped);
                _overlapped = IntPtr.Zero;
            }
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlappedState
    {
        internal IntPtr Internal;
        internal IntPtr InternalHigh;
        internal uint Offset;
        internal uint OffsetHigh;
        internal IntPtr EventHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileForMembershipMonitorW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadDirectoryChangesW(
        SafeFileHandle directory,
        IntPtr buffer,
        uint bufferLength,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint notifyFilter,
        out uint bytesReturned,
        IntPtr overlapped,
        IntPtr completionRoutine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle file,
        IntPtr overlapped,
        out uint bytesTransferred,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateEventW(
        IntPtr eventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState,
        string? name);
}
