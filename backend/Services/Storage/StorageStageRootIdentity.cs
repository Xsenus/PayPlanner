using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PayPlanner.Api.Services.Storage;

// A native directory object observation. Path text, timestamps alone and volume
// identity do not identify the original root. This grants no write/delete rights.
public static class StorageStageRootIdentity
{
    public static string? ObserveDescriptor(SafeFileHandle? handle)
        => StorageStageFileIdentity.ObserveDirectory(handle);

    public static string? Observe(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)
                || (OperatingSystem.IsWindows() && path.StartsWith("\\\\", StringComparison.Ordinal))) return null;
            var full = Path.GetFullPath(path);
            // Reject unproven/reparse ancestors before opening a directory.
            if (string.IsNullOrWhiteSpace(LocalStorageCapacityProbe.Observe(full, "stage-root-identity", 0).GroupId)) return null;
            if (OperatingSystem.IsWindows())
            {
                // Read attributes only. OPEN_REPARSE_POINT prevents following
                // a final-component replacement; native metadata rejects it.
                using var handle = OpenWindows(WindowsStoragePath.ForNativeCall(full), 0x80, 1 | 2 | 4, IntPtr.Zero, 3,
                    0x02000000 | 0x00200000, IntPtr.Zero); // BACKUP_SEMANTICS | OPEN_REPARSE_POINT
                return StorageStageFileIdentity.ObserveDirectory(handle);
            }
            if (OperatingSystem.IsLinux())
            {
                var fd = OpenLinux(full, 0x10000 | 0x20000 | 0x80000 | 0x800); // DIRECTORY | NOFOLLOW | CLOEXEC | NONBLOCK
                if (fd < 0) return null;
                using var handle = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
                return StorageStageFileIdentity.ObserveDirectory(handle);
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        { return null; }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenWindows(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("libc", EntryPoint = "open", ExactSpelling = true, SetLastError = true)]
    private static extern int OpenLinux([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}
