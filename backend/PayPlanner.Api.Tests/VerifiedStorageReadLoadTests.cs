using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;
using Xunit.Abstractions;

namespace PayPlanner.Api.Tests;

public sealed class VerifiedStorageReadLoadTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task LargeConcurrentNonSeekingReadsUsePrivateSpoolsAndBoundedChunks(int readers)
    {
        const long size = 32L * 1024 * 1024;
        var root = Path.Combine(Path.GetTempPath(), "pp_read_load_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbOptions = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "catalog.sqlite")};Pooling=False").Options;
        var hash = ComputePatternHash(size);
        var ids = Enumerable.Range(0, readers).Select(_ => Guid.NewGuid()).ToArray();
        try
        {
            await using (var seed = new PaymentContext(dbOptions))
            {
                await seed.Database.EnsureCreatedAsync();
                foreach (var id in ids)
                {
                    var entry = new StorageObject
                    {
                        Id = id,
                        DataClass = "task-file",
                        OwnerType = "task-file",
                        OwnerId = "42",
                        LogicalName = "large.bin",
                        CurrentGeneration = 1
                    };
                    var generation = new StorageObjectGeneration
                    {
                        StorageObject = entry,
                        Generation = 1,
                        SizeBytes = size,
                        Sha256 = hash
                    };
                    entry.Generations.Add(generation);
                    generation.Replicas.Add(new()
                    {
                        ProviderKey = "local",
                        Locator = id.ToString("N"),
                        SizeBytes = size,
                        Sha256 = hash,
                        Status = StorageReplicaStatuses.Available
                    });
                    seed.StorageObjects.Add(entry);
                }
                await seed.SaveChangesAsync();
            }
            var provider = new GeneratedProvider(readers, size);
            var registry = new StorageProviderRegistry([provider]);
            var monitor = new StaticMonitor(new()
            {
                Enabled = true,
                DefaultPolicy = new() { WriteProviders = ["local"], ReadProviders = ["local"] }
            });
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var elapsed = Stopwatch.StartNew();
            await Task.WhenAll(ids.Select(async id =>
            {
                await using var db = new PaymentContext(dbOptions);
                var service = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
                await using var read = await service.OpenReadAsync(id, CancellationToken.None);
                Assert.NotNull(read);
                var file = Assert.IsType<FileStream>(read.Content);
                Assert.True(file.CanSeek);
                Assert.Equal(size, file.Length);
                Assert.Equal(size, read.SizeBytes);
                Assert.Equal(hash, read.Sha256);
                Assert.StartsWith(Path.Combine(Path.GetTempPath(), $"pp_verified_read_{id:N}_"), file.Name);
                Assert.True(File.Exists(file.Name));
                using var receivedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long received = 0;
                int count;
                while ((count = await file.ReadAsync(buffer)) > 0)
                {
                    received += count;
                    receivedHash.AppendData(buffer, 0, count);
                }
                Assert.Equal(size, received);
                Assert.Equal(hash, Convert.ToHexString(receivedHash.GetHashAndReset()));
            }));
            elapsed.Stop();
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            Assert.Equal(readers, provider.Streams.Count);
            Assert.All(provider.Streams, stream =>
            {
                Assert.True(stream.Closed);
                Assert.Equal(size, stream.BytesRead);
                Assert.InRange(stream.MaximumRequestedChunk, 1, 81920);
            });
            foreach (var id in ids)
                Assert.Empty(Directory.EnumerateFiles(Path.GetTempPath(), $"pp_verified_read_{id:N}_*.tmp"));
            output.WriteLine($"Readers={readers}; payloadBytes={size * readers}; elapsedMs={elapsed.ElapsedMilliseconds}; managedAllocatedBytes={allocated}; maxSourceChunk=81920; HEAD=0; all hashes/cleanup verified.");
            // Allocation/elapsed are observations, not environment-sensitive CI thresholds.
            // Run this class alone for a meaningful allocation baseline.
        }
        finally
        {
            var absolute = Path.GetFullPath(root);
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(absolute));
            Assert.StartsWith("pp_read_load_", Path.GetFileName(absolute));
            Directory.Delete(absolute, recursive: true);
        }
    }

    private static string ComputePatternHash(long size)
    {
        var buffer = new byte[81920];
        Array.Fill(buffer, (byte)0x5a);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (long remaining = size; remaining > 0; remaining -= Math.Min(remaining, buffer.Length))
            hash.AppendData(buffer, 0, (int)Math.Min(remaining, buffer.Length));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class GeneratedProvider(int expectedReaders, long size) : IStorageProvider
    {
        private readonly TaskCompletionSource _allReaders = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _opened;
        public ConcurrentBag<PatternStream> Streams { get; } = [];
        public string Key => "local";
        public StorageProviderCapabilities Capabilities { get; } = new(true, true, true, true, false, false, false, false, false);
        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct) => throw new InvalidOperationException("Read must not issue HEAD.");
        public async Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _opened) == expectedReaders) _allReaders.TrySetResult();
            await _allReaders.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            var stream = new PatternStream(size);
            Streams.Add(stream);
            return new(StorageOperationStatus.Succeeded, locator, stream, -1);
        }
        public Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class PatternStream(long size) : Stream
    {
        public bool Closed { get; private set; }
        public long BytesRead { get; private set; }
        public int MaximumRequestedChunk { get; private set; }
        public override bool CanRead => !Closed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Closed, this);
            MaximumRequestedChunk = Math.Max(MaximumRequestedChunk, buffer.Length);
            var count = (int)Math.Min(buffer.Length, size - BytesRead);
            buffer.Span[..count].Fill(0x5a);
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StaticMonitor(StorageOrchestrationOptions value) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => value;
        public StorageOrchestrationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }
}
