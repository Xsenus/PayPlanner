using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace PayPlanner.Api.Services.Storage;

// A read-only handoff of the exact CreateNew file. Never arm path-based
// DeleteOnClose on a file which has not passed identity AND byte verification.
internal sealed class VerifiedOriginalReadFile : FileStream
{
    private readonly string _path;
    private readonly string _identity;
    private readonly long _size;
    private readonly string _sha256;
    private readonly Action _retained;
    private bool _cleanup;
    private int _disposed;

    private VerifiedOriginalReadFile(SafeFileHandle handle, string path, string identity, long size,
        string sha256, Action retained) : base(handle, FileAccess.Read, 81920, isAsync: OperatingSystem.IsWindows())
        => (_path, _identity, _size, _sha256, _retained) = (path, identity, size, sha256, retained);

    public override string Name => _path;

    internal static async Task<FileStream?> OpenVerifiedAsync(string path, string? identity, long size,
        string sha256, Action retained, CancellationToken ct, bool deleteOnDispose = true)
    {
        if (identity is null) return null;
        var handle = Open(path);
        if (handle is null) return null;
        VerifiedOriginalReadFile? file = null;
        var handedOff = false;
        try
        {
            file = new(handle, path, identity, size, sha256, retained);
            if (!await MatchesAsync(file, identity, size, sha256, ct).ConfigureAwait(false)) return null;
            if (deleteOnDispose && OperatingSystem.IsWindows())
            {
                // DELETE access was acquired without DeleteOnClose. Only NOW
                // mark this protected, proven file object for deletion.
                var disposition = new Disposition { Delete = 1 };
                if (!SetDisposition(handle, 4, ref disposition, 1)) return null;
            }
            else if (deleteOnDispose) file._cleanup = true;
            var result = file;
            file = null;
            handedOff = true;
            return result;
        }
        finally
        {
            if (file is not null) await file.DisposeAsync().ConfigureAwait(false);
            else if (!handedOff) handle.Dispose();
        }
    }

    internal static async Task<bool> TryDeleteAsync(string path, string? identity, long size, string sha256)
    {
        if (identity is null) return false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (OperatingSystem.IsWindows())
            {
                await using var file = await OpenVerifiedAsync(path, identity, size, sha256, () => { }, deadline.Token).ConfigureAwait(false);
                return file is not null; // OS deletes the same object on disposal.
            }
            if (!OperatingSystem.IsLinux()) return false;
            // Same-parent, private namespace: no product writer creates a file
            // here. mkdir must prove this invocation created the directory.
            // Unknown or interrupted candidates stay intact, never age-deleted.
            var quarantine = path + ".delete-" + Guid.NewGuid().ToString("N");
            if (Mkdir(quarantine, 0x1c0) != 0) return false; // 0700
            var candidate = Path.Combine(quarantine, "candidate");
            try
            {
                File.Move(path, candidate, overwrite: false);
                using var handle = Open(candidate);
                var matches = false;
                if (handle is not null)
                {
                    await using var file = new FileStream(handle, FileAccess.Read, 81920, isAsync: false);
                    try { matches = await MatchesAsync(file, identity, size, sha256, deadline.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    if (matches) File.Delete(candidate); // unlink private name while fd is held
                }
                if (!matches)
                {
                    // Close before restoring; never replace a newly created
                    // original path. A failed restore retains BOTH candidates.
                    try { File.Move(candidate, path, overwrite: false); }
                    catch (IOException) { }
                    return false;
                }
                return true;
            }
            finally
            {
                try { Directory.Delete(quarantine, recursive: false); }
                catch (IOException) { } // retain a nonempty interrupted candidate
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or DllNotFoundException or EntryPointNotFoundException or OperationCanceledException)
        { return false; }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _cleanup && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            if (!TryDeleteAsync(_path, _identity, _size, _sha256).GetAwaiter().GetResult()) _retained();
        }
    }

    private static SafeFileHandle? Open(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)) return null;
            // Linux asm-generic fcntl: CLOEXEC, NOFOLLOW, NONBLOCK. Reject final
            // symlinks and avoid blocking on a substituted FIFO before statx.
            var unixHandle = new SafeFileHandle(new IntPtr(OpenLinux(path, 0x80000 | 0x20000 | 0x800)), ownsHandle: true);
            if (!unixHandle.IsInvalid && Flock(unixHandle.DangerousGetHandle().ToInt32(), 6) == 0) return unixHandle; // LOCK_EX|LOCK_NB
            unixHandle.Dispose();
            return null;
        }
        var full = WindowsStoragePath.ForNativeCall(path);
        var handle = OpenWindows(full, 0x80010000, 1, IntPtr.Zero, 3, 0x40200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        return null;
    }

    internal static async Task<bool> MatchesAsync(FileStream file, string identity, long size, string sha256, CancellationToken ct)
    {
        if (size < 0 || StorageStageFileIdentity.Observe(file.SafeFileHandle) != identity || file.Length != size) return false;
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            file.Position = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long count = 0;
            while (true)
            {
                var remaining = size - count;
                var limit = remaining >= 65536 ? 65536 : (int)remaining + 1;
                var read = await file.ReadAsync(buffer.AsMemory(0, limit), ct).ConfigureAwait(false);
                if (read == 0) break;
                if (read > remaining) return false;
                hash.AppendData(buffer, 0, read);
                count += read;
            }
            file.Position = 0;
            return count == size && file.Length == size && StorageStageFileIdentity.Observe(file.SafeFileHandle) == identity
                && string.Equals(Convert.ToHexString(hash.GetHashAndReset()), sha256, StringComparison.OrdinalIgnoreCase);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Disposition { public byte Delete; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle OpenWindows(string path, uint access, uint share, IntPtr security,
        uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDisposition(SafeFileHandle handle, int kind, ref Disposition info, uint size);
    [DllImport("libc", EntryPoint = "mkdir", ExactSpelling = true)]
    private static extern int Mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc", EntryPoint = "open", ExactSpelling = true)]
    private static extern int OpenLinux([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "flock", ExactSpelling = true)]
    private static extern int Flock(int descriptor, int operation);
}
