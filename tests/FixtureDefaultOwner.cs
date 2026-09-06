using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace VmbLauncher.Tests;

/// <summary>
/// Establishes the documented creation precondition in test executables only.
/// TokenOwner affects newly created objects, not existing ACLs or token privileges:
/// https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-token_owner
/// https://learn.microsoft.com/windows/win32/secauthz/access-rights-for-access-token-objects
/// </summary>
internal static class FixtureDefaultOwner
{
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const int TokenUser = 1, TokenGroups = 2, TokenPrivileges = 3, TokenOwner = 4;
    private static readonly object Gate = new();
    internal static int SuccessfulOwnerChanges { get; private set; }
    internal static int InitializedForProcessId { get; private set; }

    [ModuleInitializer]
    internal static void Initialize() => EnsureCurrentProcessDefaultOwner();

    internal static Snapshot EnsureCurrentProcessDefaultOwner()
    {
        // No caller-supplied PID/token and no production assembly may use this seam.
        var assembly = Assembly.GetExecutingAssembly().GetName().Name;
        if (!OperatingSystem.IsWindows() ||
            (assembly != "VmbLauncher.Tests" && assembly != "VmbLauncher.TransactionLeaseWorker"))
            throw new InvalidOperationException("Default-owner setup is restricted to isolated Windows test executables.");
        lock (Gate)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenAdjustDefault, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open this test process's default-owner token.");
            using (token)
            {
                var before = ReadSnapshot(token);
                if (before.Owner != before.User)
                {
                    var sid = new SecurityIdentifier(before.User);
                    var bytes = new byte[sid.BinaryLength];
                    sid.GetBinaryForm(bytes, 0);
                    var ownerSid = Marshal.AllocHGlobal(bytes.Length);
                    try
                    {
                        Marshal.Copy(bytes, 0, ownerSid, bytes.Length);
                        var owner = new TokenOwnerValue { Owner = ownerSid };
                        if (!SetTokenInformation(token, TokenOwner, ref owner, Marshal.SizeOf<TokenOwnerValue>()))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot establish this test process's own default owner.");
                        SuccessfulOwnerChanges++;
                    }
                    finally { Marshal.FreeHGlobal(ownerSid); }
                }
                var after = ReadSnapshot(token);
                RequireIdentityUnchangedAndUserOwner(before, after);
                InitializedForProcessId = Environment.ProcessId;
                return after;
            }
        }
    }

    internal sealed record Snapshot(string User, string Owner, string[] Groups, string[] Privileges);

    // Semantic values, not native pointers/padding, are compared. Nothing other than
    // TokenOwner is ever set; a failed readback aborts test setup rather than skipping.
    internal static void RequireIdentityUnchangedAndUserOwner(Snapshot before, Snapshot after)
    {
        if (string.IsNullOrWhiteSpace(before.User) || before.User != after.User ||
            after.Owner != after.User || !before.Groups.SequenceEqual(after.Groups) ||
            !before.Privileges.SequenceEqual(after.Privileges))
            throw new InvalidOperationException("Test default-owner setup changed identity/groups/privileges or failed owner readback.");
    }

    private static Snapshot ReadSnapshot(SafeAccessTokenHandle token) => new(
        ReadInformation(token, TokenUser, (p, _) => new SecurityIdentifier(Marshal.ReadIntPtr(p)).Value),
        ReadInformation(token, TokenOwner, (p, _) => new SecurityIdentifier(Marshal.ReadIntPtr(p)).Value),
        ReadInformation(token, TokenGroups, (p, length) =>
        {
            var count = Marshal.ReadInt32(p);
            var offset = Marshal.OffsetOf<TokenGroupsValue>(nameof(TokenGroupsValue.First)).ToInt32();
            var stride = Marshal.SizeOf<SidAndAttributes>();
            RequireArrayFits(count, offset, stride, length);
            var values = new string[count];
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<SidAndAttributes>(IntPtr.Add(p, offset + index * stride));
                values[index] = new SecurityIdentifier(row.Sid).Value + ":" + row.Attributes.ToString("X8");
            }
            return values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }),
        ReadInformation(token, TokenPrivileges, (p, length) =>
        {
            var count = Marshal.ReadInt32(p);
            const int offset = 4, stride = 12; // DWORD count; LUID low/high + DWORD attributes.
            RequireArrayFits(count, offset, stride, length);
            var values = new string[count];
            for (var index = 0; index < count; index++)
            {
                var row = IntPtr.Add(p, offset + index * stride);
                values[index] = Marshal.ReadInt32(row).ToString("X8") + ":" +
                    Marshal.ReadInt32(row, 4).ToString("X8") + ":" + Marshal.ReadInt32(row, 8).ToString("X8");
            }
            return values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }));

    private static T ReadInformation<T>(SafeAccessTokenHandle token, int kind, Func<IntPtr, int, T> read)
    {
        var minimum = kind is TokenUser or TokenOwner ? IntPtr.Size : sizeof(int);
        if (GetTokenInformation(token, kind, IntPtr.Zero, 0, out var length) ||
            Marshal.GetLastWin32Error() != 122 || length < minimum || length > 65536)
            throw new InvalidOperationException("Test token information did not provide a bounded native buffer.");
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, kind, buffer, length, out var returned))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read this test process's token information.");
            if (returned < minimum || returned > length)
                throw new InvalidOperationException("Test token information returned an invalid native length.");
            return read(buffer, returned);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void RequireArrayFits(int count, int offset, int stride, int length)
    {
        if (count == 0 && length >= sizeof(int)) return;
        if (count < 0 || offset > length || count > (length - offset) / stride)
            throw new InvalidOperationException("Test token information contains an invalid array length.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenOwnerValue { internal IntPtr Owner; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes { internal IntPtr Sid; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenGroupsValue { internal uint Count; internal SidAndAttributes First; }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr buffer, int length, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int kind, ref TokenOwnerValue owner, int length);
}
