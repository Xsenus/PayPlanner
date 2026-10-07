using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PayPlanner.Api.Services.Storage;

// An observation of the open file object, NOT permission to delete its path.
// Recovery must also prove stopped writer, exact bytes, fresh DB authority and
// protected application of the action. Missing native evidence stays unknown.
public static class StorageStageFileIdentity
{
    public static string? Observe(SafeFileHandle? handle)
        => ObserveObject(handle, directory: false);

    internal static string? ObserveDirectory(SafeFileHandle? handle)
        => ObserveObject(handle, directory: true);

    private static string? ObserveObject(SafeFileHandle? handle, bool directory)
    {
        if (handle is null || handle.IsClosed || handle.IsInvalid) return null;
        var retained = false;
        try
        {
            // Keep the native descriptor alive through every metadata call;
            // closing/reusing a raw Unix fd must not change what we observe.
            handle.DangerousAddRef(ref retained);
            if (OperatingSystem.IsWindows()) return ObserveWindows(handle, directory);
            if (OperatingSystem.IsLinux()) return ObserveLinux(handle, directory);
            return null;
        }
        catch (Exception error) when (error is ObjectDisposedException or DllNotFoundException
            or EntryPointNotFoundException or PlatformNotSupportedException)
        { return null; }
        finally { if (retained) handle.DangerousRelease(); }
    }

    private static string? ObserveWindows(SafeFileHandle handle, bool directory)
    {
        // FILE_ID_INFO + FILE_BASIC_INFO + FILE_STANDARD_INFO, from the SAME
        // descriptor. Creation time supplements IDs which a filesystem can reuse.
        if (!GetFileId(handle, 18, out var id, 24)
            || !GetFileBasic(handle, 0, out var basic, 40)
            || !GetFileStandard(handle, 1, out var standard, 24)
            || basic.CreationTime <= 0 || standard.DeletePending != 0
            || (directory ? standard.NumberOfLinks == 0 || standard.Directory == 0
                : standard.NumberOfLinks != 1 || standard.Directory != 0)
            || ((basic.Attributes & (uint)FileAttributes.Directory) != 0) != directory
            || (basic.Attributes & (uint)FileAttributes.ReparsePoint) != 0
            || (id.Low == 0 && id.High == 0)) return null;
        return $"windows-stage-{(directory ? "root" : "file")}-v1:{id.Volume:x16}:{id.Low:x16}{id.High:x16}:{basic.CreationTime:x16}";
    }

    private static string? ObserveLinux(SafeFileHandle handle, bool directory)
    {
        const uint required = 0x1 | 0x4 | 0x100 | 0x800; // TYPE, NLINK, INO, BTIME
        var descriptor = handle.DangerousGetHandle().ToInt64();
        if (descriptor is < 0 or > int.MaxValue
            || Statx((int)descriptor, "", 0x1000, required, out var info) != 0 // AT_EMPTY_PATH
            || (info.Mask & required) != required || (info.Mode & 0xf000) != (directory ? 0x4000 : 0x8000)
            || (directory ? info.Links == 0 : info.Links != 1)
            || info.Inode == 0 || info.BirthNanoseconds >= 1_000_000_000)
            return null;
        return $"linux-stage-{(directory ? "root" : "file")}-v1:{info.DeviceMajor:x8}:{info.DeviceMinor:x8}:{info.Inode:x16}:{info.BirthSeconds:x16}:{info.BirthNanoseconds:x8}";
    }

    // Fixed-width hex strings survive JSON consumers without UInt64 rounding.
    // Native layouts: Microsoft FILE_*_INFO and Linux UAPI struct statx (256 B).
    // https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info
    // https://raw.githubusercontent.com/torvalds/linux/v6.6/include/uapi/linux/stat.h
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct WindowsId
    {
        [FieldOffset(0)] public ulong Volume;
        [FieldOffset(8)] public ulong Low;
        [FieldOffset(16)] public ulong High;
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct WindowsBasic
    {
        [FieldOffset(0)] public long CreationTime;
        [FieldOffset(32)] public uint Attributes;
    }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct WindowsStandard
    {
        [FieldOffset(16)] public uint NumberOfLinks;
        [FieldOffset(20)] public byte DeletePending;
        [FieldOffset(21)] public byte Directory;
    }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(0x10)] public uint Links;
        [FieldOffset(0x1c)] public ushort Mode;
        [FieldOffset(0x20)] public ulong Inode;
        [FieldOffset(0x50)] public long BirthSeconds;
        [FieldOffset(0x58)] public uint BirthNanoseconds;
        [FieldOffset(0x88)] public uint DeviceMajor;
        [FieldOffset(0x8c)] public uint DeviceMinor;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileId(SafeFileHandle handle, int kind, out WindowsId info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileBasic(SafeFileHandle handle, int kind, out WindowsBasic info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileStandard(SafeFileHandle handle, int kind, out WindowsStandard info, uint size);
    [DllImport("libc", EntryPoint = "statx", ExactSpelling = true)]
    private static extern int Statx(int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out LinuxStatx info);
}
