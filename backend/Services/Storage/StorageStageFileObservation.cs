using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PayPlanner.Api.Services.Storage;

// Observation of the SAME retained descriptor; not permission to modify its
// path. Callers must compare all fields with fresh mapped-root/row authority.
public sealed record StorageStageFileObservation(string FileIdentity, string AbsolutePath, string GroupId)
{
    public static StorageStageFileObservation? Observe(SafeFileHandle? handle)
    {
        if (handle is null || handle.IsClosed || handle.IsInvalid) return null;
        var retained = false;
        try
        {
            handle.DangerousAddRef(ref retained);
            var identity = StorageStageFileIdentity.Observe(handle);
            if (identity is null) return null;
            string? path;
            string groupIdentity;
            if (OperatingSystem.IsWindows())
            {
                path = WindowsPath(handle, 0); // normalized DOS path
                var volumePath = WindowsPath(handle, 1); // normalized volume GUID
                const string prefix = "\\\\?\\Volume{";
                if (path is null || volumePath is null || !volumePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
                var end = volumePath.IndexOf("}\\", StringComparison.Ordinal);
                if (end <= prefix.Length || !Guid.TryParseExact(volumePath[prefix.Length..end], "D", out _)) return null;
                groupIdentity = "windows:" + volumePath[..(end + 2)].ToUpperInvariant();
                if (!path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) return null;
                path = path[4..];
                if (!Path.IsPathFullyQualified(path)) return null;
                path = Path.GetFullPath(path);
            }
            else if (OperatingSystem.IsLinux())
            {
                var fd = handle.DangerousGetHandle().ToInt64();
                if (fd is < 0 or > int.MaxValue) return null;
                var bytes = new byte[32768];
                var count = ReadLink("/proc/self/fd/" + fd.ToString(CultureInfo.InvariantCulture), bytes, (nuint)bytes.Length).ToInt64();
                if (count <= 0 || count >= bytes.Length) return null;
                path = new UTF8Encoding(false, true).GetString(bytes, 0, (int)count);
                if (!path.StartsWith('/') || path.EndsWith(" (deleted)", StringComparison.Ordinal) || path.Contains('\0')) return null;
                var parts = identity.Split(':');
                if (parts.Length != 6 || parts[0] != "linux-stage-file-v1"
                    || !uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var major)
                    || !uint.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var minor)) return null;
                // Match LocalStorageCapacityProbe's device + boot scope exactly.
                // A missing proc mount or boot identity remains unknown.
                using var boot = File.OpenRead("/proc/sys/kernel/random/boot_id");
                var bootBytes = new byte[129];
                var used = 0;
                int read;
                while (used < bootBytes.Length && (read = boot.Read(bootBytes, used, bootBytes.Length - used)) > 0) used += read;
                if (used == bootBytes.Length || !Guid.TryParse(Encoding.ASCII.GetString(bootBytes, 0, used).Trim(), out var epoch)) return null;
                groupIdentity = $"linux:{epoch:N}:{major.ToString(CultureInfo.InvariantCulture)}:{minor.ToString(CultureInfo.InvariantCulture)}";
                path = Path.GetFullPath(path);
            }
            else return null;
            if (identity != StorageStageFileIdentity.Observe(handle)) return null;
            return new(identity, path, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(groupIdentity))).ToLowerInvariant());
        }
        catch (Exception error) when (error is ObjectDisposedException or IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        { return null; }
        finally { if (retained) handle.DangerousRelease(); }
    }

    private static string? WindowsPath(SafeFileHandle handle, uint flags)
    {
        var buffer = new StringBuilder(32768);
        var count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, flags);
        return count > 0 && count < buffer.Capacity ? buffer.ToString() : null;
    }

    // Normalized paths and GUID volumes come from the supplied HANDLE.
    // https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew
    // /proc/self/fd/N names the file behind the retained Linux descriptor.
    // https://man7.org/linux/man-pages/man5/proc_pid_fd.5.html
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("libc", EntryPoint = "readlink", ExactSpelling = true)]
    private static extern nint ReadLink([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer, nuint length);
}
