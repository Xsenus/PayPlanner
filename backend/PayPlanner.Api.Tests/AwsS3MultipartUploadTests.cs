using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class AwsS3MultipartUploadTests
{
    private const int PartSize = 16 * 1024 * 1024;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeUploadUsesBoundedOrderedPartsAndKeepsCallerStreamOpen(bool forwardOnly)
    {
        var bytes = Enumerable.Range(0, PartSize * 2 + 257).Select(i => (byte)(i % 251)).ToArray();
        using var source = new MemoryStream(bytes);
        using var forward = new ForwardOnlyStream(source);
        using var sdk = new RecordingS3Client();
        await using var client = new AwsS3StorageClient(sdk);

        await client.PutAsync("bucket", "backup", forwardOnly ? forward : source, "application/octet-stream", CancellationToken.None);

        Assert.Equal(0, sdk.SinglePuts);
        Assert.Equal(bytes.LongLength, sdk.PartLengths.Sum());
        Assert.InRange(sdk.PartLengths.Count, 2, 3);
        Assert.All(sdk.PartLengths.SkipLast(1), length => Assert.InRange(length, PartSize, PartSize + sdk.Config.BufferSize));
        Assert.Equal(Enumerable.Range(1, sdk.PartLengths.Count), sdk.CompletedPartNumbers);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), sdk.Sha256);
        Assert.Equal(1, sdk.Completions);
        Assert.Equal(0, sdk.Aborts);
        Assert.False(sdk.IsDisposed);
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task ThresholdUploadKeepsExistingSinglePutPath()
    {
        using var source = new MemoryStream(new byte[PartSize]);
        using var sdk = new RecordingS3Client();
        await using var client = new AwsS3StorageClient(sdk);
        await client.PutAsync("bucket", "backup", source, "application/octet-stream", CancellationToken.None);
        Assert.Equal(1, sdk.SinglePuts);
        Assert.Empty(sdk.PartLengths);
        Assert.Equal(0, sdk.Completions);
        Assert.False(sdk.IsDisposed);
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedPartAbortsOnlyItsUploadWithoutClosingCallerSource(bool cancel, bool forwardOnly)
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new MemoryStream(new byte[PartSize * 2 + 1]);
        using var forward = new ForwardOnlyStream(source);
        using var sdk = new RecordingS3Client { FailPart = 2, Cancellation = cancel ? cancellation : null };
        await using var client = new AwsS3StorageClient(sdk);
        Stream input = forwardOnly ? forward : source;
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PutAsync("bucket", "backup", input, "application/octet-stream", cancellation.Token));
        else
        {
            var error = await Assert.ThrowsAsync<S3StorageOperationException>(() => client.PutAsync("bucket", "backup", input, "application/octet-stream", cancellation.Token));
            Assert.Equal(503, error.StatusCode);
        }
        Assert.Equal(0, sdk.Completions);
        Assert.Equal(1, sdk.Aborts);
        Assert.False(sdk.AbortTokenWasCancelled);
        Assert.False(sdk.IsDisposed);
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task FailedCompletionAbortsUploadAndDoesNotReportSuccess()
    {
        using var source = new MemoryStream(new byte[PartSize + 1]);
        using var sdk = new RecordingS3Client { FailCompletion = true };
        await using var client = new AwsS3StorageClient(sdk);
        var error = await Assert.ThrowsAsync<S3StorageOperationException>(() => client.PutAsync("bucket", "backup", source, "application/octet-stream", CancellationToken.None));
        Assert.Equal(503, error.StatusCode);
        Assert.Equal(0, sdk.Completions);
        Assert.Equal(1, sdk.Aborts);
        Assert.False(sdk.AbortTokenWasCancelled);
        Assert.False(sdk.IsDisposed);
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipartStartsAtCurrentPosition(bool forwardOnly)
    {
        var bytes = Enumerable.Range(0, PartSize + 1024).Select(i => (byte)(i % 251)).ToArray();
        using var source = new MemoryStream(bytes);
        source.Position = 17;
        using var forward = new ForwardOnlyStream(source);
        using var sdk = new RecordingS3Client();
        await using var client = new AwsS3StorageClient(sdk);
        await client.PutAsync("bucket", "backup", forwardOnly ? forward : source, "application/octet-stream", CancellationToken.None);
        Assert.Equal(bytes.Length - 17, sdk.PartLengths.Sum());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes.AsSpan(17))), sdk.Sha256);
        Assert.Equal(1, sdk.Completions);
        Assert.False(sdk.IsDisposed);
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task MultipartRejectsIncorrectForwardOnlyLengthBeforeCompletion(int lengthDifference)
    {
        using var source = new MemoryStream(new byte[PartSize + 257]);
        using var forward = new ForwardOnlyStream(source, source.Length + lengthDifference);
        using var sdk = new RecordingS3Client();
        await using var client = new AwsS3StorageClient(sdk);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.PutAsync("bucket", "backup", forward, "application/octet-stream", CancellationToken.None));
        Assert.Equal(0, sdk.Completions);
        Assert.Equal(1, sdk.Aborts);
        Assert.False(sdk.IsDisposed);
        Assert.True(source.CanRead);
    }

    private sealed class RecordingS3Client() : AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config
    {
        ServiceURL = "http://127.0.0.1:1",
        AuthenticationRegion = "us-east-1",
        ForcePathStyle = true,
    })
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public int SinglePuts { get; private set; }
        public int Completions { get; private set; }
        public int Aborts { get; private set; }
        public bool AbortTokenWasCancelled { get; private set; }
        public bool IsDisposed { get; private set; }
        public List<long> PartLengths { get; } = [];
        public List<int> CompletedPartNumbers { get; } = [];
        public int? FailPart { get; init; }
        public bool FailCompletion { get; init; }
        public CancellationTokenSource? Cancellation { get; init; }
        public string? Sha256 { get; private set; }

        public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
        {
            Assert.False(request.AutoCloseStream);
            SinglePuts++;
            await request.InputStream.CopyToAsync(Stream.Null, cancellationToken);
            return new() { HttpStatusCode = HttpStatusCode.OK };
        }

        public override Task<InitiateMultipartUploadResponse> InitiateMultipartUploadAsync(InitiateMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal("bucket", request.BucketName);
            Assert.Equal("backup", request.Key);
            Assert.Equal("application/octet-stream", request.ContentType);
            return Task.FromResult(new InitiateMultipartUploadResponse { UploadId = "owned-upload", HttpStatusCode = HttpStatusCode.OK });
        }

        public override async Task<UploadPartResponse> UploadPartAsync(UploadPartRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal("owned-upload", request.UploadId);
            Assert.Equal(PartLengths.Count + 1, request.PartNumber);
            // Forward-only SDK uploads may fill the last read buffer beyond the
            // nominal part boundary; memory remains bounded by one extra buffer.
            Assert.InRange(request.PartSize!.Value, 1, PartSize + Config.BufferSize);
            if (request.PartNumber == FailPart)
            {
                Cancellation?.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                throw new AmazonS3Exception("Synthetic part rejection") { StatusCode = HttpStatusCode.ServiceUnavailable };
            }
            var remaining = request.PartSize.Value;
            var buffer = new byte[81920];
            while (remaining > 0)
            {
                var count = await request.InputStream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), cancellationToken);
                if (count == 0) throw new EndOfStreamException();
                _hash.AppendData(buffer, 0, count);
                remaining -= count;
            }
            PartLengths.Add(request.PartSize.Value);
            return new() { ETag = "part-" + request.PartNumber, PartNumber = request.PartNumber, HttpStatusCode = HttpStatusCode.OK };
        }

        public override Task<CompleteMultipartUploadResponse> CompleteMultipartUploadAsync(CompleteMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal("owned-upload", request.UploadId);
            if (FailCompletion)
                throw new AmazonS3Exception("Synthetic completion rejection") { StatusCode = HttpStatusCode.ServiceUnavailable };
            CompletedPartNumbers.AddRange(request.PartETags.Select(part => part.PartNumber!.Value));
            Completions++;
            Sha256 = Convert.ToHexString(_hash.GetHashAndReset());
            return Task.FromResult(new CompleteMultipartUploadResponse { HttpStatusCode = HttpStatusCode.OK });
        }

        public override Task<AbortMultipartUploadResponse> AbortMultipartUploadAsync(AbortMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal("bucket", request.BucketName);
            Assert.Equal("backup", request.Key);
            Assert.Equal("owned-upload", request.UploadId);
            Aborts++;
            AbortTokenWasCancelled = cancellationToken.IsCancellationRequested;
            return Task.FromResult(new AbortMultipartUploadResponse { HttpStatusCode = HttpStatusCode.NoContent });
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ForwardOnlyStream(Stream source, long? declaredLength = null) : Stream
    {
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => declaredLength ?? source.Length;
        public override long Position { get => source.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => source.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
