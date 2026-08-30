using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VmbLauncher.Services;

internal static partial class LocalExactSetDeployment
{
    /// <summary>
    /// The exact-set transaction is intentionally gated to NTFS until the full
    /// FILE_ID_INFO, rename, delete-disposition, and notification protocol has
    /// an independent ReFS fixture. This is a capability refusal, not a legacy
    /// deploy restriction; callers invoke it only after exact journal/receipt
    /// ownership is established.
    /// </summary>
    private static void RequireSupportedExactSetFileSystem(string path, string context)
    {
        var volumePath = new StringBuilder(1024);
        if (!GetVolumePathNameW(Normalize(path), volumePath, volumePath.Capacity))
            throw new IOException(
                $"Cannot resolve {context} filesystem (Win32 {Marshal.GetLastWin32Error()}).");
        var fileSystem = new StringBuilder(64);
        if (!GetVolumeInformationW(
                volumePath.ToString(),
                null,
                0,
                out _,
                out _,
                out _,
                fileSystem,
                fileSystem.Capacity))
            throw new IOException(
                $"Cannot identify {context} filesystem (Win32 {Marshal.GetLastWin32Error()}).");
        if (!string.Equals(fileSystem.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Receipt-authority exact-set {context} requires NTFS; '{fileSystem}' is not yet supported.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(
        string fileName,
        StringBuilder volumePathName,
        int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPathName,
        StringBuilder? volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        int fileSystemNameSize);
}
