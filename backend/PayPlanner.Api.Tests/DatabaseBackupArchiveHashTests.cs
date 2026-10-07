using System.Security.Cryptography;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseBackupArchiveHashTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(2097153)]
    public async Task ExactWrittenBytesAreHashedWithoutClosingBorrowedStreams(int length)
    {
        var bytes = Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();
        using var source = new MemoryStream(bytes);
        using var destination = new MemoryStream();
        var result = await DatabaseBackupArchiveHash.CopyAsync(source, destination, CancellationToken.None);
        Assert.Equal(bytes, destination.ToArray());
        Assert.Equal(length, result.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), result.Sha256);
        Assert.True(source.CanRead);
        Assert.True(destination.CanWrite);
        Assert.Equal(result.Sha256, DatabaseBackupArchiveHash.FromComment(DatabaseBackupArchiveHash.ToComment(result.Sha256)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256-v2:unknown")]
    [InlineData("payplanner-sha256-v1:short")]
    [InlineData("payplanner-sha256-v1:000000000000000000000000000000000000000000000000000000000000000Z")]
    public void MalformedOrMissingMetadataIsRejected(string comment)
        => Assert.Throws<InvalidDataException>(() => DatabaseBackupArchiveHash.FromComment(comment));

    [Fact]
    public async Task CancellationAfterFirstWriteLeavesOwnershipWithCaller()
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new MemoryStream(new byte[81921]);
        using var destination = new CancellingDestination(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatabaseBackupArchiveHash.CopyAsync(source, destination, cancellation.Token));
        Assert.Equal(81920, destination.Length);
        Assert.True(source.CanRead);
        Assert.True(destination.CanWrite);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(2097153)]
    public async Task StableCopyPreservesExactBytesAndBorrowedStreamOwnership(int length)
    {
        var bytes = Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();
        using var source = new MemoryStream(bytes);
        using var destination = new MemoryStream();
        var hash = await DatabaseBackupArchiveHash.CopyStableAsync(source, destination, CancellationToken.None);
        Assert.Equal(bytes, destination.ToArray());
        Assert.Equal(length, hash.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), hash.Sha256);
        Assert.True(source.CanRead);
        Assert.True(destination.CanWrite);
    }

    [Theory]
    [InlineData("overwrite")]
    [InlineData("append")]
    [InlineData("truncate")]
    public async Task StableCopyRejectsChangesToUnreadBytesDuringArchiving(string mutation)
    {
        using var source = new MemoryStream();
        source.Write(new byte[81921]);
        source.Position = 0;
        using var destination = new MutatingDestination(() =>
        {
            var position = source.Position;
            if (mutation == "truncate") source.SetLength(81920);
            else
            {
                source.Position = mutation == "append" ? source.Length : 81920;
                source.WriteByte(1);
            }
            source.Position = position;
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseBackupArchiveHash.CopyStableAsync(source, destination, CancellationToken.None));
        Assert.True(source.CanRead);
        Assert.True(destination.CanWrite);
    }

    [Fact]
    public async Task StableCopyCancellationDoesNotCloseBorrowedStreams()
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new MemoryStream(new byte[81921]);
        using var destination = new CancellingDestination(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatabaseBackupArchiveHash.CopyStableAsync(source, destination, cancellation.Token));
        Assert.Equal(81920, destination.Length);
        Assert.True(source.CanRead);
        Assert.True(destination.CanWrite);
    }

    private sealed class MutatingDestination(Action mutate) : MemoryStream
    {
        private bool _mutated;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            if (!_mutated) { _mutated = true; mutate(); }
        }
    }

    private sealed class CancellingDestination(CancellationTokenSource cancellation) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            cancellation.Cancel();
        }
    }
}
