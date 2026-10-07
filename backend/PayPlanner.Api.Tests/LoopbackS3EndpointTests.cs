using System.Text;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class LoopbackS3EndpointTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(65536)]
    public async Task KnownLengthNonSeekableStreamUploadsWithoutBufferingOrRewinding(int size)
    {
        await using var endpoint = await LoopbackS3Endpoint.StartAsync();
        await using var client = new S3StorageClientFactory().Create(endpoint.Connection);
        var bytes = Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();
        using var content = new ForwardOnlyStream(bytes, knownLength: true);
        await client.PutAsync(LoopbackS3Endpoint.Bucket, "fixture/nonseekable.bin", content,
            "application/octet-stream", CancellationToken.None);
        Assert.Equal(size, content.BytesRead);
        var downloaded = await client.OpenReadAsync(LoopbackS3Endpoint.Bucket, "fixture/nonseekable.bin", CancellationToken.None);
        await using (downloaded.Content)
        {
            using var received = new MemoryStream();
            await downloaded.Content.CopyToAsync(received);
            Assert.Equal(bytes, received.ToArray());
            Assert.Equal(size, downloaded.SizeBytes);
        }
        Assert.Equal(1, endpoint.PutCount);
    }

    [Fact]
    public async Task UnknownLengthNonSeekableStreamDoesNotCreateAnObject()
    {
        await using var endpoint = await LoopbackS3Endpoint.StartAsync();
        await using var client = new S3StorageClientFactory().Create(endpoint.Connection);
        using var content = new ForwardOnlyStream([1, 2, 3], knownLength: false);
        await Assert.ThrowsAnyAsync<Exception>(() => client.PutAsync(LoopbackS3Endpoint.Bucket,
            "fixture/unknown.bin", content, "application/octet-stream", CancellationToken.None));
        Assert.Equal(0, content.BytesRead);
        Assert.Equal(0, endpoint.PutCount);
        Assert.False(await client.ExistsAsync(LoopbackS3Endpoint.Bucket, "fixture/unknown.bin", CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidDeclaredLengthDoesNotSilentlyDiscardBytes(long declaredLength)
    {
        await using var endpoint = await LoopbackS3Endpoint.StartAsync();
        await using var client = new S3StorageClientFactory().Create(endpoint.Connection);
        using var content = new ForwardOnlyStream([1, 2, 3], knownLength: true, declaredLength);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.PutAsync(LoopbackS3Endpoint.Bucket,
            "fixture/invalid.bin", content, "application/octet-stream", CancellationToken.None));
        Assert.Equal(0, endpoint.PutCount);
        Assert.False(await client.ExistsAsync(LoopbackS3Endpoint.Bucket, "fixture/invalid.bin", CancellationToken.None));
    }

    [Fact]
    public async Task RealAwsClientStoresAndReadsSyntheticBytesOverLoopbackOnly()
    {
        await using var endpoint = await LoopbackS3Endpoint.StartAsync();
        await using var client = new S3StorageClientFactory().Create(endpoint.Connection);
        const string key = "fixture/task-file/synthetic.txt";
        Assert.False(await client.ExistsAsync(LoopbackS3Endpoint.Bucket, key, CancellationToken.None));
        var bytes = Encoding.UTF8.GetBytes("Synthetic S3 second copy: Вложение задачи");
        using var content = new MemoryStream(bytes);
        await client.PutAsync(LoopbackS3Endpoint.Bucket, key, content, "text/plain", CancellationToken.None);
        Assert.True(await client.ExistsAsync(LoopbackS3Endpoint.Bucket, key, CancellationToken.None));
        var downloaded = await client.OpenReadAsync(LoopbackS3Endpoint.Bucket, key, CancellationToken.None);
        using (downloaded.Content)
        {
            using var received = new MemoryStream();
            await downloaded.Content.CopyToAsync(received);
            Assert.Equal(bytes, received.ToArray());
            Assert.Equal(bytes.Length, downloaded.SizeBytes);
        }
        Assert.Equal(1, endpoint.PutCount);
    }

    private sealed class ForwardOnlyStream(byte[] bytes, bool knownLength, long? declaredLength = null) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => knownLength ? declaredLength ?? bytes.Length : throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException("Rewinding is forbidden."); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, bytes.Length - BytesRead);
            bytes.AsSpan(BytesRead, read).CopyTo(buffer.AsSpan(offset, read));
            BytesRead += read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("Seeking is forbidden.");
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
