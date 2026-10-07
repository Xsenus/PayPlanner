using System.Buffers;
using System.Security.Cryptography;

namespace PayPlanner.Api.Services.Storage;

// Owns the CreateNew descriptor and fence hold. Producers must coalesce writes
// into bounded blocks; arbitrary metadata cannot be supplied as file ownership.
public sealed class StorageStageCheckpointWriter : IAsyncDisposable
{
    private readonly FileStream _file;
    private readonly IDisposable _hold;
    private readonly Func<StorageStageFileReceipt?, StorageStageFileReceipt, CancellationToken, Task> _save;
    private readonly Func<StorageStageFileReceipt, CancellationToken, Task> _verify;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StorageStageFileReceipt _receipt;
    internal StorageStageFileReceipt InitialReceipt { get; }
    private bool _failed, _disposed, _completed;

    internal StorageStageCheckpointWriter(FileStream file, IDisposable hold, StorageStageFileReceipt receipt,
        Func<StorageStageFileReceipt?, StorageStageFileReceipt, CancellationToken, Task> save,
        Func<StorageStageFileReceipt, CancellationToken, Task> verify)
        => (_file, _hold, _receipt, InitialReceipt, _save, _verify) = (file, hold, receipt, receipt, save, verify);

    public async Task WriteBlockAsync(ReadOnlyMemory<byte> block, CancellationToken ct)
    {
        if (block.Length is <= 0 or > StorageStageFileReceipt.MaximumBlockBytes) throw new ArgumentOutOfRangeException(nameof(block));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        byte[]? copy = null;
        try
        {
            RequireOpen();
            if (block.Length > _receipt.UpperBound - _receipt.PlannedBytes) throw new InvalidOperationException("Stage write exceeds admission.");
            // Caller memory must not change between planned SHA and physical write.
            copy = ArrayPool<byte>.Shared.Rent(block.Length);
            block.CopyTo(copy.AsMemory(0, block.Length));
            _hash.AppendData(copy, 0, block.Length);
            var planned = _receipt.Plan(_receipt.PlannedBytes + block.Length,
                Convert.ToHexString(_hash.GetCurrentHash()).ToLowerInvariant());
            await _save(_receipt, planned, ct).ConfigureAwait(false);
            await _verify(planned, ct).ConfigureAwait(false);
            if (_file.Length != _receipt.PlannedBytes || _file.Position != _receipt.PlannedBytes)
                throw new InvalidOperationException("Stage write boundary changed.");
            await _file.WriteAsync(copy.AsMemory(0, block.Length), ct).ConfigureAwait(false);
            await _file.FlushAsync(ct).ConfigureAwait(false);
            _file.Flush(flushToDisk: true);
            _receipt = planned;
        }
        catch { _failed = true; throw; }
        finally { if (copy is not null) ArrayPool<byte>.Shared.Return(copy, clearArray: true); _gate.Release(); }
    }

    public async Task<StorageStageFileReceipt> CompleteAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireOpen();
            if (_receipt.PlannedBytes == 0) throw new InvalidOperationException("Stage writer produced no bytes.");
            await _verify(_receipt, ct).ConfigureAwait(false);
            if (_file.Length != _receipt.PlannedBytes) throw new InvalidOperationException("Stage size changed.");
            _file.Position = 0;
            var actual = await ReadBoundedHashAsync(ct).ConfigureAwait(false);
            if (actual != _receipt.PlannedSha256 || _file.Length != _receipt.PlannedBytes)
                throw new InvalidOperationException("Stage bytes changed.");
            await _verify(_receipt, ct).ConfigureAwait(false);
            _completed = true;
            return _receipt;
        }
        catch { _failed = true; throw; }
        finally { _gate.Release(); }
    }

    private void RequireOpen()
    {
        if (_disposed || _failed || _completed) throw new InvalidOperationException("Stage writer cannot continue.");
    }

    private async Task<string> ReadBoundedHashAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            long count = 0;
            while (true)
            {
                var remaining = _receipt.PlannedBytes - count;
                var limit = remaining >= 65536 ? 65536 : (int)remaining + 1;
                var read = await _file.ReadAsync(buffer.AsMemory(0, limit), ct).ConfigureAwait(false);
                if (read == 0) break;
                if (read > remaining) throw new InvalidOperationException("Stage grew during verification.");
                hash.AppendData(buffer, 0, read);
                count += read;
            }
            if (count != _receipt.PlannedBytes) throw new InvalidOperationException("Stage shortened during verification.");
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            try { await _file.DisposeAsync().ConfigureAwait(false); }
            finally { _hash.Dispose(); _hold.Dispose(); }
        }
        finally { _gate.Release(); }
    }
}
