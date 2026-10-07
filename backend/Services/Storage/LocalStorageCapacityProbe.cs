using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PayPlanner.Api.Services.Storage;

public interface IStorageCapacityProvider
{
    Task<StorageCapacityResource> ObserveCapacityAsync(string resourceKey, string? connectionId,
        long minimumFreeBytes, CancellationToken ct);
}

public static class LocalStorageCapacityProbe
{
    public static StorageCapacityResource Observe(string path, string key, long minimumFreeBytes)
    {
        StorageCapacityResource Unknown() => new(key, "", null, minimumFreeBytes, DateTimeOffset.UtcNow);
        try
        {
            if (!Path.IsPathFullyQualified(path) || minimumFreeBytes < 0
                || (OperatingSystem.IsWindows() && path.StartsWith("\\\\", StringComparison.Ordinal))) return Unknown();
            var parent = new DirectoryInfo(Path.GetFullPath(path));
            while (!parent.Exists)
            {
                if (File.Exists(parent.FullName) || parent.LinkTarget is not null) return Unknown();
                parent = parent.Parent ?? throw new IOException("No existing storage ancestor.");
            }
            for (var ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor.LinkTarget is not null || (ancestor.Attributes & FileAttributes.ReparsePoint) != 0) return Unknown();
            string identity;
            long free;
            if (OperatingSystem.IsWindows())
            {
                var mount = new StringBuilder(32768);
                var volume = new StringBuilder(128);
                var nativePath = WindowsStoragePath.ForNativeCall(parent.FullName);
                if (!GetVolumePathNameW(nativePath, mount, (uint)mount.Capacity)
                    || !GetVolumeNameForVolumeMountPointW(mount.ToString(), volume, (uint)volume.Capacity)
                    || !GetDiskFreeSpaceExW(nativePath, out var available, out _, out _) || available > long.MaxValue)
                    return Unknown();
                identity = "windows:" + volume.ToString().ToUpperInvariant(); free = (long)available;
            }
            else if (OperatingSystem.IsLinux())
            {
                var mounts = ReadBoundedText("/proc/self/mountinfo", 4 * 1024 * 1024);
                var boot = ReadBoundedText("/proc/sys/kernel/random/boot_id", 128).Trim();
                if (!Guid.TryParse(boot, out var epoch)) return Unknown();
                var selected = SelectLinuxMount(mounts, parent.FullName);
                if (selected is null) return Unknown();
                identity = $"linux:{epoch:N}:{selected.Value.Device}";
                free = new DriveInfo(parent.FullName).AvailableFreeSpace;
            }
            else return Unknown();
            if (free < 0) return Unknown();
            var group = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            return new(key, group, free, minimumFreeBytes, DateTimeOffset.UtcNow);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Unknown(); }
    }

    // Bind mounts with the same major:minor share a budget. This identifies a
    // filesystem capacity group, NOT independent hardware or backup failure domains.
    public static (string Device, string Mount)? SelectLinuxMount(string table, string path)
    {
        if (table.Length > 4 * 1024 * 1024 || !path.StartsWith('/') || path.Contains('\0')) return null;
        (string Device, string Mount)? selected = null;
        foreach (var line in table.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10 || !fields.Contains("-") || fields[2].Split(':') is not [var major, var minor]
                || !uint.TryParse(major, out _) || !uint.TryParse(minor, out _)) return null;
            var mount = fields[4].Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");
            if (!mount.StartsWith('/') || mount.Contains('\0')) return null;
            var prefix = mount.TrimEnd('/') + "/";
            if (path != mount && !path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (selected is not null && selected.Value.Mount.Length == mount.Length) return null;
            if (selected is null || mount.Length > selected.Value.Mount.Length) selected = (fields[2], mount);
        }
        return selected;
    }

    private static string ReadBoundedText(string path, int limit)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var content = new MemoryStream(); var bytes = new byte[8192];
        while (true)
        {
            var read = input.Read(bytes, 0, (int)Math.Min(bytes.Length, limit + 1 - content.Length));
            if (read == 0) break;
            content.Write(bytes, 0, read);
            if (content.Length > limit) throw new IOException("Capacity metadata exceeds supported limits.");
        }
        return Encoding.UTF8.GetString(content.GetBuffer(), 0, (int)content.Length);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string path, StringBuilder volumePath, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mount, StringBuilder volume, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong totalFree);
}
