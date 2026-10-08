using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class InternalChatControllerTests
{
    [Theory]
    [InlineData(false, "accepted")]
    [InlineData(true, "accepted")]
    [InlineData(false, "allCommittedGap")]
    [InlineData(true, "allCommittedGap")]
    [InlineData(false, "reservationGap")]
    [InlineData(true, "reservationGap")]
    [InlineData(false, "partial")]
    [InlineData(true, "partial")]
    [InlineData(false, "missingBytes")]
    [InlineData(true, "missingBytes")]
    [InlineData(false, "corruptContext")]
    [InlineData(true, "corruptContext")]
    [InlineData(false, "wrongPointer")]
    [InlineData(true, "wrongPointer")]
    [InlineData(false, "revoked")]
    [InlineData(true, "revoked")]
    [InlineData(false, "revokedDuringRead")]
    [InlineData(true, "revokedDuringRead")]
    [InlineData(false, "deletedDuringRead")]
    [InlineData(true, "deletedDuringRead")]
    [InlineData(false, "staleLease")]
    [InlineData(true, "staleLease")]
    [InlineData(false, "pendingLocal")]
    [InlineData(true, "pendingLocal")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "corruptFrozen")]
    [InlineData(true, "corruptFrozen")]
    [InlineData(false, "disabled")]
    [InlineData(true, "disabled")]
    [InlineData(false, "invalidPolicy")]
    [InlineData(true, "invalidPolicy")]
    [InlineData(false, "published")]
    [InlineData(true, "published")]
    [InlineData(false, "metadataOnly")]
    [InlineData(true, "metadataOnly")]
    [InlineData(false, "catalogGapMissing")]
    [InlineData(true, "catalogGapMissing")]
    [InlineData(false, "wrongReservation")]
    [InlineData(true, "wrongReservation")]
    [InlineData(false, "reservedMissing")]
    [InlineData(true, "reservedMissing")]
    [InlineData(false, "denyReadCapacity")]
    [InlineData(true, "denyReadCapacity")]
    [InlineData(false, "missingStageService")]
    [InlineData(true, "missingStageService")]
    [InlineData(false, "changedOriginal")]
    [InlineData(true, "changedOriginal")]
    [InlineData(false, "expiryDuringRead")]
    [InlineData(true, "expiryDuringRead")]
    public async Task ChatOriginalRecoveryUsesFrozenDurableBytesWithoutBrowserFiles(bool postgres, string scenario)
    {
        await using var f = await CreateMigratedFixtureAsync(postgres);
        var actor = await f.AddUserAsync("chat-recovery@example.test", "Автор");
        var thread = await CaptureChannelAsync(f, actor);
        var request = CaptureSnapshot(thread.Id, actor.Id);
        using var root = new ChatOriginalRoot();
        var environment = new ChatOriginalEnvironment(root.Path);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [LocalStoragePaths.RootConfigurationKey] = root.Path,
            ["Email:Enabled"] = "false",
            ["BackgroundServices:Enabled"] = "false"
        }).Build();
        var settings = new StorageOrchestrationOptions
        {
            Enabled = true,
            UploadCapacityChecksEnabled = true,
            UploadMinimumFreeBytes = 0,
            ProductWriteEnabledClasses = ["chat-attachment"]
        };
        var monitor = new ChatOriginalOptions(settings);
        var local = new ChatRecoveryLocal(new LocalStorageProvider(environment, config));
        var providers = new StorageProviderRegistry([local, new ChatRecoveryMirror(local)]);
        var policy = new StoragePolicyService(monitor, providers);
        var objects = new StorageObjectService(f.Db, policy, providers, monitor);
        var interrupted = new ChatOriginalStorage(objects);
        if (scenario is "allCommittedGap" or "catalogGapMissing") interrupted.AfterWrite = (write, call)
            => Task.FromResult(call == 2 ? write with { ObjectId = Guid.Empty } : write);
        if (scenario == "partial") interrupted.FailCall = 2;
        if (scenario is "reservationGap" or "wrongReservation" or "reservedMissing") local.ThrowPutAckAt = 2;
        var initialWriter = new InternalChatOriginalWriter(f.Db, interrupted,
            new StorageStageCapacityService(f.Db, providers, monitor, environment, config), environment, config, monitor);
        if (scenario == "metadataOnly")
            Assert.Equal("recorded", (await new InternalChatSendAttemptRegistry(f.Db).CaptureAsync(request, default)).Status);
        else if (scenario is "reservationGap" or "wrongReservation" or "reservedMissing")
        {
            await Assert.ThrowsAsync<IOException>(() => initialWriter.WriteAsync(request, ChatOriginalInputs(request), default));
            Assert.Equal(2, local.PutCalls);
            Assert.Single(await f.Db.StorageObjects.ToArrayAsync());
            Assert.False(await f.Db.InternalChatSendAttempts.AnyAsync(item => item.AcceptedAt != null));
            var expired = DateTime.UtcNow.AddMinutes(-1);
            await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLeaseExpiresAt, expired));
        }
        else
        {
            var initial = await initialWriter.WriteAsync(request, ChatOriginalInputs(request), default);
            Assert.Equal(scenario is "allCommittedGap" or "catalogGapMissing" or "partial" ? "unavailable" : "accepted", initial.Status);
        }
        var originalObjectIds = await f.Db.StorageObjects.AsNoTracking().Select(item => item.Id).ToArrayAsync();
        var beforeAttempt = await f.Db.InternalChatSendAttempts.AsNoTracking().SingleAsync();
        var secondName = request.Files[1].FileName;
        var reservedIdentity = await f.Db.StorageCapacityReservations.Where(item => item.Status == StorageCapacityReservationStatuses.Pending
            && item.ProductContextJson != null).Select(item => (Guid?)item.ObjectId).SingleOrDefaultAsync();
        if (scenario is "missingBytes" or "catalogGapMissing" or "changedOriginal")
        {
            var locator = await f.Db.StorageOperations.Where(item => item.OperationType == "commit" && item.StorageObject.LogicalName == secondName)
                .SelectMany(item => item.StorageObjectGeneration!.Replicas).Select(item => item.Locator).SingleAsync();
            var path = Path.GetFullPath(Path.Combine(root.Path, locator));
            Assert.StartsWith(root.Path + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
            if (scenario == "changedOriginal") await File.WriteAllBytesAsync(path, new byte[] { 6, 5, 4 });
            else File.Delete(path);
        }
        if (scenario == "reservedMissing")
        {
            var locator = await f.Db.StorageCapacityReservations.Where(item => item.Status == StorageCapacityReservationStatuses.Pending
                && item.ProductContextJson != null).Select(item => item.LocalLocator).SingleAsync();
            var path = Path.GetFullPath(Path.Combine(root.Path, locator));
            Assert.StartsWith(root.Path + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
            File.Delete(path);
        }
        if (scenario == "wrongReservation") await f.Db.StorageCapacityReservations.Where(item => item.Status == StorageCapacityReservationStatuses.Pending
            && item.ProductContextJson != null).ExecuteUpdateAsync(set => set.SetProperty(item => item.ProductContextJson, "{}"));
        if (scenario == "corruptContext")
        {
            await f.Db.StorageOperations.Where(item => item.OperationType == "commit" && item.StorageObject.LogicalName == secondName)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.PayloadJson, "{}"));
        }
        if (scenario == "wrongPointer")
        {
            var unrelated = Guid.NewGuid();
            await f.Db.InternalChatSendAttemptFiles.Where(item => item.Index == 1)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.StorageObjectId, unrelated));
        }
        if (scenario == "revoked") await f.Db.InternalChatMembers.Where(item => item.UserId == actor.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false));
        if (scenario == "pendingLocal")
        {
            settings.DefaultPolicy.WriteProviders = ["mirror"];
            settings.DefaultPolicy.ReadProviders = ["mirror"];
            await f.Db.StorageReplicas.ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, StorageReplicaStatuses.Pending));
        }
        if (scenario == "corruptFrozen") await f.Db.InternalChatSendAttempts
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.Text, "Повреждённый frozen текст"));
        if (scenario == "disabled") settings.ProductWriteEnabledClasses.Clear();
        if (scenario == "invalidPolicy") settings.DefaultPolicy.WriteProviders = ["unregistered"];
        if (scenario == "denyReadCapacity") settings.UploadMinimumFreeBytes = long.MaxValue;
        if (scenario == "published") await f.Db.InternalChatSendAttempts
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.PublishedMessageId, 987654));

        await using var fresh = CaptureFreshDb(f);
        var readStages = new StorageOriginalReadStagingService(new StorageStageCapacityService(fresh, providers, monitor, environment, config),
            environment, config, monitor, NullLogger<StorageOriginalReadStagingService>.Instance);
        var recoveredObjects = new StorageObjectService(fresh, policy, providers, monitor,
            originalReadStages: scenario == "missingStageService" ? null : readStages);
        var recovery = new InternalChatOriginalWriter(fresh, recoveredObjects,
            new StorageStageCapacityService(fresh, providers, monitor, environment, config), environment, config, monitor);
        local.ReadCalls = 0;
        using var cancellation = new CancellationTokenSource();
        var successor = Guid.NewGuid();
        if (scenario == "staleLease") local.BeforeReadReturn = async () =>
            await fresh.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLeaseToken, successor));
        if (scenario == "cancel") local.BeforeReadReturn = () => { cancellation.Cancel(); return Task.CompletedTask; };
        if (scenario == "revokedDuringRead") local.BeforeReadReturn = async () =>
            await fresh.InternalChatMembers.Where(item => item.UserId == actor.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false));
        if (scenario == "deletedDuringRead") local.BeforeReadReturn = async () =>
            await fresh.StorageObjects.ExecuteUpdateAsync(set => set.SetProperty(item => item.IsDeleted, true));
        if (scenario == "expiryDuringRead") local.BeforeReadReturn = async () =>
        {
            var expired = DateTime.UtcNow.AddMinutes(-1);
            await fresh.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLeaseExpiresAt, expired));
        };
        var putsBefore = local.PutCalls;
        InternalChatOriginalWriteResult? result = null;
        if (scenario == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.ResumeAsync(thread.Id, actor.Id, request.ClientRequestId, cancellation.Token));
        else
        {
            result = await recovery.ResumeAsync(thread.Id, actor.Id, request.ClientRequestId, default);
            var expected = scenario switch
            {
                "accepted" or "allCommittedGap" or "reservationGap" or "pendingLocal" => "accepted",
                "partial" or "metadataOnly" => "needsInput",
                "missingBytes" or "catalogGapMissing" or "reservedMissing" or "deletedDuringRead" or "invalidPolicy"
                    or "denyReadCapacity" or "missingStageService" or "changedOriginal" => "unavailable",
                "revoked" or "revokedDuringRead" => "forbidden",
                "staleLease" or "expiryDuringRead" => "busy",
                "disabled" => "disabled",
                "published" => "published",
                _ => "conflict"
            };
            Assert.Equal(expected, result.Status);
            Assert.Equal(expected == "accepted", result.Accepted);
        }
        Assert.Equal(putsBefore, local.PutCalls);
        var afterAttempt = await fresh.InternalChatSendAttempts.AsNoTracking().SingleAsync();
        if (result?.Accepted == true)
        {
            Assert.Equal(2, await fresh.StorageObjects.CountAsync());
            Assert.NotNull(afterAttempt.AcceptedAt);
            Assert.Equal(2, local.ReadCalls);
            foreach (var id in originalObjectIds) Assert.True(await fresh.StorageObjects.AnyAsync(item => item.Id == id));
            if (beforeAttempt.AcceptedAt.HasValue) Assert.Equal(beforeAttempt.AcceptedAt, afterAttempt.AcceptedAt);
            if (scenario == "reservationGap") Assert.False(await fresh.StorageCapacityReservations
                .AnyAsync(item => item.Status == StorageCapacityReservationStatuses.Pending));
            if (reservedIdentity.HasValue) Assert.Equal(reservedIdentity,
                await fresh.InternalChatSendAttemptFiles.Where(item => item.Index == 1).Select(item => item.StorageObjectId).SingleAsync());
        }
        else Assert.Equal(beforeAttempt.AcceptedAt, afterAttempt.AcceptedAt);
        if (scenario is "corruptContext" or "wrongPointer" or "revoked" or "corruptFrozen" or "disabled" or "published" or "invalidPolicy"
            or "partial" or "metadataOnly" or "wrongReservation" or "denyReadCapacity" or "missingStageService")
            Assert.Equal(0, local.ReadCalls);
        if (scenario == "staleLease") Assert.Equal(successor, afterAttempt.RecoveryLeaseToken);
        if (scenario == "cancel") Assert.NotNull(afterAttempt.RecoveryLeaseToken);
        Assert.Empty(await fresh.InternalChatMessages.ToArrayAsync());
        Assert.Empty(await fresh.InternalChatAttachments.ToArrayAsync());
        Assert.Empty(await fresh.StorageProductBindings.ToArrayAsync());
        Assert.Null(await fresh.InternalChatThreads.Select(item => item.LastMessageAt).SingleAsync());
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp", SearchOption.AllDirectories));
        Assert.False(await fresh.StorageCapacityReservations.AnyAsync(item => item.IdempotencyKey.StartsWith("stage:")
            && item.Status == StorageCapacityReservationStatuses.Pending));
    }

    private sealed class ChatRecoveryLocal(LocalStorageProvider inner) : IStorageProvider, IStorageCapacityProvider
    {
        public string Key => "local";
        public StorageProviderCapabilities Capabilities => inner.Capabilities;
        public int PutCalls { get; private set; }
        public int ReadCalls { get; set; }
        public int? ThrowPutAckAt { get; set; }
        public Func<Task>? BeforeReadReturn { get; set; }
        public async Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct)
        {
            PutCalls++;
            var result = await inner.PutAsync(request, ct);
            if (PutCalls == ThrowPutAckAt) throw new IOException("Owned synthetic local PUT acknowledgement lost.");
            return result;
        }
        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct) => inner.HeadAsync(locator, ct);
        public async Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct)
        {
            ReadCalls++;
            var read = await inner.OpenReadAsync(locator, ct);
            try { if (BeforeReadReturn is not null) await BeforeReadReturn(); }
            catch { await read.DisposeAsync(); throw; }
            return read;
        }
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct) => inner.DeleteAsync(locator, ct);
        public Task<StorageCapacityResource> ObserveCapacityAsync(string resourceKey, string? connectionId, long minimumFreeBytes, CancellationToken ct)
            => inner.ObserveCapacityAsync(resourceKey, connectionId, minimumFreeBytes, ct);
    }

    private sealed class ChatRecoveryMirror(ChatRecoveryLocal local) : IStorageProvider
    {
        public string Key => "mirror";
        public StorageProviderCapabilities Capabilities => local.Capabilities;
        public Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct) => throw new InvalidOperationException("No mirror I/O expected in original recovery.");
        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct) => throw new InvalidOperationException("No mirror I/O expected in original recovery.");
        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct) => throw new InvalidOperationException("No mirror I/O expected in original recovery.");
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct) => throw new InvalidOperationException("No mirror I/O expected in original recovery.");
    }
}
