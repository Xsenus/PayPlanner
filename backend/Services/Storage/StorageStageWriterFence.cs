using System.Security.Cryptography;
using System.Text;

namespace PayPlanner.Api.Services.Storage;

// A persisted marker is created after the reservation is committed. Recovery
// opens the SAME marker exclusively; it must never recreate a missing marker.
// ObjectId is unique to one admission and is never reused by another writer.
internal sealed class StorageStageWriterFence : IAsyncDisposable
{
    private FileStream? _stream;
    private readonly string _path;
    private bool _retired;
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly byte[] _proof;
    private readonly string _rootIdentity;
    private readonly bool _issuedWriter;
    private bool _closing;
    internal string Identity { get; }

    private StorageStageWriterFence(FileStream stream, string path, byte[] proof, string identity, string rootIdentity, bool issuedWriter)
    {
        (_stream, _path, _proof, _issuedWriter) = (stream, path, proof, issuedWriter);
        Identity = identity;
        _rootIdentity = rootIdentity;
    }

    internal async Task<IDisposable> HoldWriterAsync(StorageStageCapacityTicket ticket, CancellationToken ct)
    {
        await _writerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireWriter(ticket);
            return new WriterHold(_writerGate);
        }
        catch { _writerGate.Release(); throw; }
    }

    internal void RequireWriter(StorageStageCapacityTicket ticket)
    {
        if (!_issuedWriter || Volatile.Read(ref _closing) || _retired || _stream is null
            || ticket.RootIdentity != _rootIdentity || StorageStageRootIdentity.Observe(Path.GetDirectoryName(Path.GetDirectoryName(_path)!)!) != _rootIdentity
            || ticket.WriterMarkerIdentity != Identity || StorageStageFileIdentity.Observe(_stream.SafeFileHandle) != Identity
            || !CryptographicOperations.FixedTimeEquals(_proof, Proof(ticket)))
            throw new InvalidOperationException("Stage writer authority is closed or changed.");
    }

    private sealed class WriterHold(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    internal static async Task<StorageStageWriterFence> CreateAsync(
        string root, StorageStageCapacityTicket ticket, CancellationToken ct)
    {
        var path = ResolvePath(root, ticket);
        var rootIdentity = ticket.RootIdentity;
        if (rootIdentity is null || StorageStageRootIdentity.Observe(root) != rootIdentity)
            throw new IOException("Stage root changed before marker creation.");
        var directory = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        RequireOrdinaryDirectory(directory);
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(path, fileOptions);
        var identity = StorageStageFileIdentity.Observe(stream.SafeFileHandle);
        var proof = Proof(ticket);
        try
        {
            if (identity is null) throw new IOException("Stage marker native identity is unavailable.");
            await stream.WriteAsync(proof, ct);
            await stream.FlushAsync(ct);
            stream.Flush(flushToDisk: true);
            if (StorageStageFileIdentity.Observe(stream.SafeFileHandle) != identity)
                throw new IOException("Stage marker identity changed during creation.");
            if (StorageStageRootIdentity.Observe(root) != ticket.RootIdentity)
                throw new IOException("Stage root changed during marker creation.");
            return new(stream, path, proof, identity, rootIdentity, issuedWriter: true);
        }
        catch
        {
            await stream.DisposeAsync();
            // A partial or replaced marker is retained. Never delete a path
            // merely because this invocation originally created a file there.
            if (StorageStageRootIdentity.Observe(root) == rootIdentity)
                await VerifiedOriginalReadFile.TryDeleteAsync(path, identity, proof.Length, Hash(proof));
            throw;
        }
    }

    internal static async Task<StorageStageWriterFence> OpenForRecoveryAsync(
        string root, StorageStageCapacityTicket ticket, CancellationToken ct)
    {
        var path = ResolvePath(root, ticket);
        RequireOrdinaryDirectory(Path.GetDirectoryName(path)!);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Stage writer marker is not an ordinary file.");
        if (!OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new IOException("Stage writer marker permissions are unsafe.");
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
        try
        {
            var proof = Proof(ticket);
            var rootIdentity = ticket.RootIdentity;
            if (rootIdentity is null || StorageStageRootIdentity.Observe(root) != rootIdentity)
                throw new IOException("Stage marker lacks its original root identity.");
            var identity = StorageStageFileIdentity.Observe(stream.SafeFileHandle);
            if (ticket.WriterMarkerIdentity is null || identity != ticket.WriterMarkerIdentity)
                throw new IOException("Stage marker lacks its original durable native identity.");
            if (stream.Length != proof.Length) throw new IOException("Stage writer marker proof is invalid.");
            var bytes = new byte[proof.Length];
            await stream.ReadExactlyAsync(bytes, ct);
            if (!CryptographicOperations.FixedTimeEquals(bytes, proof))
                throw new IOException("Stage writer marker authority changed.");
            if (StorageStageFileIdentity.Observe(stream.SafeFileHandle) != identity)
                throw new IOException("Stage marker native identity changed during verification.");
            if (StorageStageRootIdentity.Observe(root) != ticket.RootIdentity)
                throw new IOException("Stage root changed during marker verification.");
            return new(stream, path, proof, identity!, rootIdentity, issuedWriter: false);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    // Call only after the exact reservation is confirmed released. Pending
    // markers survive disposal/crashes so later recovery can prove quiescence.
    internal void Retire() => _retired = true;

    internal async Task<bool> RetireAndConfirmAbsenceAsync()
    {
        Retire();
        await DisposeAsync();
        if (StorageStageRootIdentity.Observe(Path.GetDirectoryName(Path.GetDirectoryName(_path)!)!) != _rootIdentity) return false;
        try { _ = File.GetAttributes(_path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref _closing, true);
        await _writerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var stream = Interlocked.Exchange(ref _stream, null);
            if (stream is null) return;
            try { await stream.DisposeAsync(); }
            catch (IOException) { return; }
            if (_retired && StorageStageRootIdentity.Observe(Path.GetDirectoryName(Path.GetDirectoryName(_path)!)!) == _rootIdentity)
            {
                // Reopen with the original identity and exact bytes using protected
                // deletion. A replacement or another holder is retained.
                await VerifiedOriginalReadFile.TryDeleteAsync(_path, Identity, _proof.Length, Hash(_proof));
            }
        }
        finally { _writerGate.Release(); }
    }

    private static string ResolvePath(string root, StorageStageCapacityTicket ticket)
    {
        RequireOrdinaryDirectory(root);
        if (ticket.ObjectId == Guid.Empty) throw new ArgumentException("Stage writer identity is missing.", nameof(ticket));
        return Path.Combine(Path.GetFullPath(root), ".stage-writer-fences-v1", ticket.ObjectId.ToString("N") + ".lock");
    }

    private static void RequireOrdinaryDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Stage writer fence directory is unsafe.");
        if (!OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new IOException("Stage writer fence directory permissions are unsafe.");
    }

    private static byte[] Proof(StorageStageCapacityTicket ticket)
    {
        var absolute = Path.GetFullPath(ticket.AbsolutePath);
        if (OperatingSystem.IsWindows()) absolute = absolute.ToUpperInvariant();
        var content = string.Join('\n', "PayPlanner.StageWriter.v1", ticket.Key, ticket.ObjectId.ToString("N"),
            ticket.ResourceKey, ticket.GroupId, ticket.RelativePath, absolute);
        return Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))));
    }

    private static string Hash(byte[] proof) => Convert.ToHexString(SHA256.HashData(proof)).ToLowerInvariant();
}
