using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PayPlanner.Api.Services.Storage;

// Own the verified file and both directory objects throughout domain registration.
// Never close a successful byte proof and then rename a newly resolved source path.
internal sealed class VerifiedSignatureSnapshot : IAsyncDisposable
{
    private readonly SafeFileHandle _root, _directory;
    private readonly string _rootPath, _rootIdentity, _directoryPath, _directoryIdentity, _fileIdentity, _sha256;
    private readonly long _size;
    private int _disposed;
    internal FileStream Content { get; }
    internal string FullPath { get; }

    private VerifiedSignatureSnapshot(SafeFileHandle root, SafeFileHandle directory, FileStream content,
        string rootPath, string rootIdentity, string directoryPath, string directoryIdentity,
        string fileIdentity, long size, string sha256)
    {
        (_root, _directory, Content, _rootPath, _rootIdentity, _directoryPath, _directoryIdentity,
            _fileIdentity, _size, _sha256) = (root, directory, content, rootPath,
            rootIdentity, directoryPath, directoryIdentity, fileIdentity, size, sha256);
        FullPath = Path.Combine(directoryPath, "original.pdf");
    }

    internal static async Task<VerifiedSignatureSnapshot?> MoveAsync(string temporaryPath, string rootPath,
        string? rootIdentity, string? fileIdentity, long size, string? sha256, string directoryName, CancellationToken ct)
    {
        if (rootIdentity is null || fileIdentity is null || sha256 is null || size <= 0
            || !Guid.TryParseExact(directoryName, "N", out _) || !Path.IsPathFullyQualified(rootPath)
            || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(temporaryPath)), Path.GetFullPath(rootPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
        SafeFileHandle? root = null, directory = null;
        FileStream? content = null;
        var handedOff = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (StorageStageRootIdentity.Observe(rootPath) != rootIdentity) return null;
            root = OpenDirectory(rootPath);
            if (root is null || StorageStageRootIdentity.ObserveDescriptor(root) != rootIdentity) return null;
            content = await VerifiedOriginalReadFile.OpenVerifiedAsync(temporaryPath, fileIdentity, size, sha256,
                () => { }, ct, deleteOnDispose: false).ConfigureAwait(false);
            if (content is null || StorageStageRootIdentity.Observe(rootPath) != rootIdentity) return null;
            var directoryPath = Path.Combine(rootPath, directoryName);
            if (OperatingSystem.IsWindows())
            {
                if (!CreateDirectoryWindows(WindowsStoragePath.ForNativeCall(directoryPath), IntPtr.Zero)) return null; // exclusive creation
                directory = OpenDirectory(directoryPath);
            }
            else if (OperatingSystem.IsLinux())
            {
                if (MkdirAt(root.DangerousGetHandle().ToInt32(), directoryName, 0x1c0) != 0) return null; // 0700
                var fd = OpenAt(root.DangerousGetHandle().ToInt32(), directoryName, DirectoryFlags);
                if (fd >= 0) directory = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
            }
            else return null;
            var directoryIdentity = StorageStageRootIdentity.ObserveDescriptor(directory);
            if (directory is null || directoryIdentity is null || StorageStageRootIdentity.Observe(rootPath) != rootIdentity
                || StorageStageRootIdentity.Observe(directoryPath) != directoryIdentity) return null;
            ct.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows())
            {
                if (!RenameWindows(content.SafeFileHandle, directory, Path.Combine(directoryPath, "original.pdf"))) return null;
            }
            else
            {
                if (!await RenameLinuxCandidateAsync(root, directory, content, Path.GetFileName(temporaryPath),
                    directoryPath, fileIdentity, size, sha256, ct).ConfigureAwait(false)) return null;
            }
            var result = new VerifiedSignatureSnapshot(root, directory, content, rootPath, rootIdentity,
                directoryPath, directoryIdentity, fileIdentity, size, sha256);
            if (!await result.VerifyAsync(ct).ConfigureAwait(false)) return null;
            handedOff = true;
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DllNotFoundException
            or EntryPointNotFoundException or PlatformNotSupportedException)
        { return null; }
        finally
        {
            if (!handedOff)
            {
                if (content is not null) await content.DisposeAsync().ConfigureAwait(false);
                directory?.Dispose();
                root?.Dispose();
            }
        }
    }

    internal async Task<bool> VerifyAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0 || !MatchesNamespace()) return false;
        if (!await VerifiedOriginalReadFile.MatchesAsync(Content, _fileIdentity, _size, _sha256, ct).ConfigureAwait(false)) return false;
        return MatchesNamespace();
    }

    private bool MatchesNamespace()
    {
        if (StorageStageRootIdentity.ObserveDescriptor(_root) != _rootIdentity
            || StorageStageRootIdentity.ObserveDescriptor(_directory) != _directoryIdentity
            || StorageStageRootIdentity.Observe(_rootPath) != _rootIdentity
            || StorageStageRootIdentity.Observe(_directoryPath) != _directoryIdentity) return false;
        using var file = OpenObservation(FullPath);
        return file is not null && StorageStageFileIdentity.Observe(file) == _fileIdentity && RandomAccess.GetLength(file) == _size;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await Content.DisposeAsync().ConfigureAwait(false); }
        finally { _directory.Dispose(); _root.Dispose(); }
    }

    private const int DirectoryFlags = 0x10000 | 0x20000 | 0x80000 | 0x800;

    private static async Task<bool> RenameLinuxCandidateAsync(SafeFileHandle root, SafeFileHandle directory,
        FileStream content, string sourceName, string directoryPath, string identity, long size, string sha256, CancellationToken ct)
    {
        // Retain one link: native identity deliberately rejects hard links.
        // Isolate the name in this invocation's exclusive 0700 directory and
        // prove that candidate is the original held fd BEFORE publication.
        var rootFd = root.DangerousGetHandle().ToInt32();
        var directoryFd = directory.DangerousGetHandle().ToInt32();
        if (RenameAt2(rootFd, sourceName, directoryFd, "candidate", 1) != 0) return false; // NOREPLACE
        var restore = true;
        try
        {
            using var candidate = OpenObservation(Path.Combine(directoryPath, "candidate"));
            if (candidate is null || StorageStageFileIdentity.Observe(candidate) != identity
                || RandomAccess.GetLength(candidate) != size
                || !await VerifiedOriginalReadFile.MatchesAsync(content, identity, size, sha256, ct).ConfigureAwait(false)) return false;
            ct.ThrowIfCancellationRequested();
            if (RenameAt2(directoryFd, "candidate", directoryFd, "original.pdf", 1) != 0) return false;
            restore = false;
            return true;
        }
        finally
        {
            // Never overwrite a new original name. A failed restoration keeps
            // the candidate and competing file; an unknown candidate is not deleted.
            if (restore) RenameAt2(directoryFd, "candidate", rootFd, sourceName, 1);
            GC.KeepAlive(root);
            GC.KeepAlive(directory);
        }
    }

    private static SafeFileHandle? OpenDirectory(string path)
    {
        SafeFileHandle? handle = null;
        if (OperatingSystem.IsWindows())
            handle = OpenWindows(WindowsStoragePath.ForNativeCall(path), 0xa7, 1 | 2, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        else if (OperatingSystem.IsLinux())
        {
            var fd = OpenLinux(path, DirectoryFlags);
            if (fd >= 0) handle = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        }
        if (handle is not null && !handle.IsInvalid && StorageStageRootIdentity.ObserveDescriptor(handle) is not null) return handle;
        handle?.Dispose();
        return null;
    }

    private static SafeFileHandle? OpenObservation(string path)
    {
        SafeFileHandle? handle = null;
        if (OperatingSystem.IsWindows())
            handle = OpenWindows(WindowsStoragePath.ForNativeCall(path), 0x80, 1 | 2 | 4, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        else if (OperatingSystem.IsLinux())
        {
            var fd = OpenLinux(path, 0x20000 | 0x80000 | 0x800);
            if (fd >= 0) handle = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        }
        if (handle is not null && !handle.IsInvalid) return handle;
        handle?.Dispose();
        return null;
    }

    private static bool RenameWindows(SafeFileHandle source, SafeFileHandle directory, string name)
    {
        var bytes = Encoding.Unicode.GetBytes(WindowsStoragePath.ForNativeCall(name));
        var rootOffset = Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.RootDirectory)).ToInt32();
        var lengthOffset = Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.FileNameLength)).ToInt32();
        var nameOffset = Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.FileName)).ToInt32();
        var length = checked(nameOffset + bytes.Length + 2);
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            for (var i = 0; i < length; i++) Marshal.WriteByte(buffer, i, 0); // ReplaceIfExists = false
            // SetFileInformationByHandle rejects this relative RootDirectory
            // form on Windows (ERROR_INVALID_PARAMETER). Both original directory
            // objects remain pinned without delete sharing while resolving the
            // absolute destination; the source is still this exact retained fd.
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, bytes.Length);
            Marshal.Copy(bytes, 0, IntPtr.Add(buffer, nameOffset), bytes.Length);
            var renamed = SetRename(source, 3, buffer, checked((uint)length));
            GC.KeepAlive(directory);
            return renamed;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RenameInfo { public uint Flags; public IntPtr RootDirectory; public uint FileNameLength; public ushort FileName; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenWindows(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryWindows(string path, IntPtr security);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetRename(SafeFileHandle source, int kind, IntPtr information, uint length);
    [DllImport("libc", EntryPoint = "open", ExactSpelling = true, SetLastError = true)]
    private static extern int OpenLinux([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "openat", ExactSpelling = true, SetLastError = true)]
    private static extern int OpenAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
    [DllImport("libc", EntryPoint = "mkdirat", ExactSpelling = true, SetLastError = true)]
    private static extern int MkdirAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint mode);
    [DllImport("libc", EntryPoint = "renameat2", ExactSpelling = true, SetLastError = true)]
    private static extern int RenameAt2(int sourceDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        int destinationDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);
}
