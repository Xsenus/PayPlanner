using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class InternalChatControllerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ChatOriginalStagingDoesNotHandOffOrDeleteAReplacedFile(bool sameBytes, bool settlementFailure)
    {
        using var root = new ChatOriginalRoot();
        var environment = new ChatOriginalEnvironment(root.Path);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [LocalStoragePaths.RootConfigurationKey] = root.Path }).Build();
        var original = new byte[] { 1, 2, 3 };
        var foreign = sameBytes ? original : new byte[] { 9, 8, 7 };
        string? displaced = null;
        var admission = new ChatStagingAdmission("valid", path =>
        {
            displaced = System.IO.Path.Combine(root.Path, "displaced-original.tmp");
            File.Move(path, displaced);
            File.WriteAllBytes(path, foreign);
            if (settlementFailure) throw new IOException("Owned settlement acknowledgement failed.");
        });
        var service = new StorageOriginalReadStagingService(admission, environment, config,
            new ChatOriginalOptions(new() { UploadCapacityChecksEnabled = true }), NullLogger<StorageOriginalReadStagingService>.Instance);
        Task<FileStream?> Run() => service.VerifyAsync("owned-handoff", "owned-original", original.Length, CaptureHash(original),
            _ => Task.FromResult(new StorageReadResult(StorageOperationStatus.Succeeded, "owned-original", new MemoryStream(original), original.Length)), default);
        var verified = settlementFailure ? null : await Run();
        if (settlementFailure)
            Assert.Equal("Owned settlement acknowledgement failed.", (await Assert.ThrowsAsync<IOException>(Run)).Message);
        var handedOffForeign = verified is not null;
        if (verified is not null) await verified.DisposeAsync();
        Assert.True(File.Exists(admission.Path), "Reopening a stage path must not arm deletion of a replacement file.");
        Assert.Equal(foreign, await File.ReadAllBytesAsync(admission.Path!));
        Assert.Equal(original, await File.ReadAllBytesAsync(displaced!));
        Assert.False(handedOffForeign, "Equal size or equal bytes do not prove the CreateNew file identity.");
    }

    [Fact]
    public async Task ChatOriginalStagingDoesNotTrustOrDeleteModifiedBytesOfTheSameFile()
    {
        using var root = new ChatOriginalRoot();
        var environment = new ChatOriginalEnvironment(root.Path);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [LocalStoragePaths.RootConfigurationKey] = root.Path }).Build();
        var original = new byte[] { 1, 2, 3 };
        var modified = new byte[] { 9, 8, 7 };
        var admission = new ChatStagingAdmission("valid", path =>
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var before = Assert.IsType<string>(StorageStageFileIdentity.Observe(file.SafeFileHandle));
            file.Write(modified);
            file.Flush(flushToDisk: true);
            Assert.Equal(before, StorageStageFileIdentity.Observe(file.SafeFileHandle));
        });
        var service = new StorageOriginalReadStagingService(admission, environment, config,
            new ChatOriginalOptions(new() { UploadCapacityChecksEnabled = true }), NullLogger<StorageOriginalReadStagingService>.Instance);
        await using var verified = await service.VerifyAsync("owned-modified-handoff", "owned-original", original.Length, CaptureHash(original),
            _ => Task.FromResult(new StorageReadResult(StorageOperationStatus.Succeeded, "owned-original", new MemoryStream(original), original.Length)), default);
        Assert.Null(verified);
        Assert.Equal(modified, await File.ReadAllBytesAsync(admission.Path!));
        Assert.Equal(new byte[] { 1, 2, 3 }, original);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("different")]
    [InlineData("denied")]
    [InlineData("foreignCreate")]
    [InlineData("providerFailure")]
    [InlineData("cleanupFailure")]
    [InlineData("cancel")]
    public async Task ChatOriginalStagingBoundsBytesAndPreservesForeignFiles(string scenario)
    {
        using var root = new ChatOriginalRoot();
        var environment = new ChatOriginalEnvironment(root.Path);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [LocalStoragePaths.RootConfigurationKey] = root.Path }).Build();
        var admission = new ChatStagingAdmission(scenario);
        var service = new StorageOriginalReadStagingService(admission, environment, config,
            new ChatOriginalOptions(new() { UploadCapacityChecksEnabled = true }), NullLogger<StorageOriginalReadStagingService>.Instance);
        var reads = 0;
        using var cancellation = new CancellationTokenSource();
        Task<FileStream?> Run() => service.VerifyAsync("owned-staging-proof", "owned-original", 3, CaptureHash([1, 2, 3]), ct =>
        {
            reads++;
            Assert.True(admission.Pending);
            if (scenario is "providerFailure" or "cleanupFailure") throw new IOException("Owned provider failure.");
            if (scenario == "cancel") { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
            byte[] bytes = scenario switch
            {
                "short" => [1, 2],
                "long" => [1, 2, 3, 4],
                "different" => [3, 2, 1],
                _ => [1, 2, 3]
            };
            return Task.FromResult(new StorageReadResult(StorageOperationStatus.Succeeded, "owned-original", new MemoryStream(bytes), bytes.Length));
        }, cancellation.Token);
        if (scenario is "providerFailure" or "cleanupFailure")
            Assert.Equal("Owned provider failure.", (await Assert.ThrowsAsync<IOException>(Run)).Message);
        else if (scenario == "foreignCreate") await Assert.ThrowsAsync<IOException>(Run);
        else if (scenario == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
        else
        {
            await using var verified = await Run();
            if (scenario == "valid")
            {
                Assert.NotNull(verified);
                Assert.False(verified.CanWrite);
                Assert.True(verified.CanRead);
                Assert.Equal(3, verified.Length);
                Assert.Equal(0, verified.Position);
                Assert.StartsWith(root.Path + Path.DirectorySeparatorChar, verified.Name, StringComparison.OrdinalIgnoreCase);
                var bytes = new byte[3];
                Assert.Equal(3, await verified.ReadAsync(bytes));
                Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
                Assert.True(File.Exists(verified.Name));
                Assert.Equal(1, admission.Settled);
                Assert.False(admission.Pending);
            }
            else Assert.Null(verified);
        }
        Assert.Equal(scenario == "denied" ? 0 : 1, reads);
        if (scenario == "foreignCreate")
        {
            Assert.True(admission.Pending);
            Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(admission.Path!));
        }
        else
        {
            Assert.Equal(scenario == "cleanupFailure", admission.Pending);
            Assert.Empty(Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories));
        }
    }

    private sealed class ChatStagingAdmission(string scenario, Action<string>? afterSettlement = null) : IStorageStageCapacityService
    {
        // Unit-only admission: real SQL/checkpoint/coalescing behavior is covered
        // by StorageOriginalReadCheckpointTests and the stage writer tests.
        public async Task<StorageStageFileReceipt> WriteCheckpointedFileAsync(StorageStageCapacityTicket ticket,
            Func<Stream, StorageStageFileReceipt, CancellationToken, Task> write, CancellationToken ct)
        {
            await using var file = new FileStream(ticket.AbsolutePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var native = Assert.IsType<string>(StorageStageFileIdentity.Observe(file.SafeFileHandle));
            var initial = StorageStageFileReceipt.Create(ticket.ObjectId, ticket.BindingHash, ticket.SizeBytes, native);
            await write(file, initial, ct);
            await file.FlushAsync(ct);
            file.Flush(flushToDisk: true);
            Assert.InRange(file.Length, 1, ticket.SizeBytes);
            file.Position = 0;
            var sha = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
            return initial.Plan(file.Length, sha);
        }

        public Task<StorageStageCapacityTicket> ConfirmPhysicalSizeAsync(StorageStageCapacityTicket ticket, long actualSizeBytes, CancellationToken ct)
            => throw new NotSupportedException("Written-size confirmation is outside this fixture scope.");

        public bool Pending { get; private set; }
        public int Settled { get; private set; }
        public string? Path { get; private set; }
        public Task<(StorageStageCapacityTicket? Ticket, string? Error)> ReserveAsync(string identity, string path, long size, CancellationToken ct)
        {
            Path = path;
            if (scenario == "denied") return Task.FromResult<(StorageStageCapacityTicket?, string?)>((null, "Owned admission denied."));
            Pending = true;
            if (scenario == "foreignCreate") File.WriteAllBytes(path, [9, 8, 7]);
            return Task.FromResult<(StorageStageCapacityTicket?, string?)>((new("owned-ticket", new string('a', 64), "owned-resource", "owned-group", "owned-relative", path, size, Guid.NewGuid()), null));
        }
        public Task SettlePhysicalAsync(StorageStageCapacityTicket ticket, CancellationToken ct)
        {
            Assert.Equal(ticket.SizeBytes, new FileInfo(ticket.AbsolutePath).Length);
            Pending = false;
            Settled++;
            afterSettlement?.Invoke(ticket.AbsolutePath);
            return Task.CompletedTask;
        }
        public Task ReleaseAsync(StorageStageCapacityTicket ticket, CancellationToken ct)
        {
            if (File.Exists(ticket.AbsolutePath) || scenario == "cleanupFailure") throw new IOException("Owned release must retain debt.");
            Pending = false;
            return Task.CompletedTask;
        }
        public Task<(StorageStageCapacityTicket? Ticket, string? Error)> ReserveCompatibilityAsync(string identity, string path, long size, string alias, CancellationToken ct)
            => throw new InvalidOperationException("No compatibility copy in verified original staging.");
        public Task<StorageStageRecoveryReport> ReconcilePendingAsync(bool apply, int limit, TimeSpan minimumAge, CancellationToken ct, string? afterKey = null)
            => throw new InvalidOperationException("No independent reconciliation during staged read.");
    }
}
