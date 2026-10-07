namespace PayPlanner.Api.Services;

// Counts manifest bytes without allocating a second manifest or creating a
// file; with an inner stream, enforces the admitted physical file extent.
// ZIP entries use trailing data descriptors, keeping already written bytes
// immutable instead of seeking back to patch their local headers.
internal sealed class EvidenceArchiveWriteStream(Stream? inner, long limit, Action validatePolicy, bool flushInner = true) : Stream
{
    private long _count;
    public override bool CanRead => false;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => inner?.Length ?? _count;
    public override long Position
    {
        get => inner?.Position ?? _count;
        set => throw new NotSupportedException();
    }

    private void CheckWrite(int count)
    {
        validatePolicy();
        if (count > limit - Position)
            throw new InvalidOperationException("Доказательный архив превышает зарезервированный размер.");
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CheckWrite(buffer.Length);
        if (inner is null) _count += buffer.Length;
        else inner.Write(buffer);
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckWrite(buffer.Length);
        if (inner is null) _count += buffer.Length;
        else await inner.WriteAsync(buffer, cancellationToken);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush()
    {
        validatePolicy();
        if (flushInner) inner?.Flush();
    }
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        validatePolicy();
        return flushInner ? inner?.FlushAsync(cancellationToken) ?? Task.CompletedTask : Task.CompletedTask;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    // Ownership of the physical stream stays with the caller, which must close
    // it before ConfirmPhysicalSizeAsync observes the completed ZIP.
}
