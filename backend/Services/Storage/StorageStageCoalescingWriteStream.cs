using System.Buffers;

namespace PayPlanner.Api.Services.Storage;

// The service owns this facade and the writer. Callback disposal closes the
// facade for further writes; only successful callback completion publishes its
// remaining buffer. Exception/cancellation disposal never flushes new bytes.
internal sealed class StorageStageCoalescingWriteStream(StorageStageCheckpointWriter writer, long upperBound) : Stream
{
    private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(StorageStageFileReceipt.MaximumBlockBytes);
    private int _count;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _accepted;
    private bool _closed, _failed, _completed;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_closed && !_failed && !_completed && _buffer is not null;
    public override long Length => _accepted;
    public override long Position { get => _accepted; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        Enter();
        try
        {
            RequireOpen();
            ct.ThrowIfCancellationRequested();
            if (bytes.Length > upperBound - _accepted) throw new InvalidOperationException("Stage stream exceeds admission.");
            while (!bytes.IsEmpty)
            {
                ct.ThrowIfCancellationRequested();
                var copied = Math.Min(bytes.Length, StorageStageFileReceipt.MaximumBlockBytes - _count);
                bytes[..copied].CopyTo(_buffer!.AsMemory(_count, copied));
                _count += copied;
                _accepted += copied;
                bytes = bytes[copied..];
                if (_count == StorageStageFileReceipt.MaximumBlockBytes) await FlushBufferAsync(ct).ConfigureAwait(false);
            }
        }
        catch { _failed = true; throw; }
        finally { _gate.Release(); }
    }

    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task FlushAsync(CancellationToken ct)
    {
        Enter();
        try { RequireOpen(); ct.ThrowIfCancellationRequested(); await FlushBufferAsync(ct).ConfigureAwait(false); }
        catch { _failed = true; throw; }
        finally { _gate.Release(); }
    }

    internal async Task<StorageStageFileReceipt> CompleteOwnedAsync(CancellationToken ct)
    {
        Enter();
        try
        {
            // A successful callback may have disposed a ZIP wrapper/facade.
            // The owning service can still finalize bytes accepted beforehand.
            if (_failed || _completed || _buffer is null) throw new InvalidOperationException("Stage stream cannot complete.");
            ct.ThrowIfCancellationRequested();
            await FlushBufferAsync(ct).ConfigureAwait(false);
            var receipt = await writer.CompleteAsync(ct).ConfigureAwait(false);
            _completed = true;
            return receipt;
        }
        catch { _failed = true; throw; }
        finally { _gate.Release(); }
    }

    private async Task FlushBufferAsync(CancellationToken ct)
    {
        if (_count == 0) return;
        await writer.WriteBlockAsync(_buffer!.AsMemory(0, _count), ct).ConfigureAwait(false);
        _count = 0;
    }

    private void Enter()
    {
        if (!_gate.Wait(0)) throw new InvalidOperationException("Concurrent stage stream operations are not supported.");
    }

    private void RequireOpen()
    {
        if (!CanWrite) throw new ObjectDisposedException(nameof(StorageStageCoalescingWriteStream));
    }

    protected override void Dispose(bool disposing) { _closed = true; base.Dispose(disposing); }

    internal async Task ReleaseOwnedBufferAsync()
    {
        _closed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
        finally { _gate.Release(); }
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
