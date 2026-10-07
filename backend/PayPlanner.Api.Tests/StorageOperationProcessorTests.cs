using System.Security.Cryptography;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;
using Xunit.Abstractions;

namespace PayPlanner.Api.Tests;

public sealed class StorageOperationProcessorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedReaderNeverProbesMismatchedConnectionAndCanUseVerifiedFallback(bool fallback)
    {
        await using var f = await Fixture.CreateAsync(connected: true);
        var id = await f.SeedReplicateAsync();
        var operation = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == id);
        var plan = JsonSerializer.Deserialize<StorageOperationPayload>(operation.PayloadJson!)!;
        f.Target.Objects[plan.TargetLocator] = f.Content;
        f.Db.StorageReplicas.Add(new()
        {
            StorageObjectGenerationId = operation.StorageObjectGenerationId!.Value,
            ProviderKey = f.Source.Key,
            ConnectionId = "source",
            Locator = plan.SourceLocator!,
            Status = fallback ? StorageReplicaStatuses.Available : StorageReplicaStatuses.Missing,
            SizeBytes = f.Content.Length,
            Sha256 = f.Sha256
        });
        f.Db.StorageReplicas.Add(new()
        {
            StorageObjectGenerationId = operation.StorageObjectGenerationId.Value,
            ProviderKey = f.Target.Key,
            ConnectionId = "foreign",
            Locator = plan.TargetLocator,
            Status = StorageReplicaStatuses.Available,
            SizeBytes = f.Content.Length,
            Sha256 = f.Sha256
        });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var registry = new StorageProviderRegistry([f.Source, f.Target]);
        var monitor = new StaticOptionsMonitor<StorageOrchestrationOptions>(new()
        {
            Enabled = true,
            ProviderConnections = new() { ["source"] = "source", ["target"] = "target" },
            DefaultPolicy = new() { WriteProviders = ["target", "source"], ReadProviders = ["target", "source"] }
        });
        var storage = new StorageObjectService(f.Db, new StoragePolicyService(monitor, registry), registry, monitor);
        var read = await storage.OpenReadAsync(operation.StorageObjectId, CancellationToken.None);
        Assert.Equal(0, f.Target.HeadCalls); Assert.Equal(0, f.Target.ReadCalls);
        Assert.Equal(StorageReplicaStatuses.Suspect, (await f.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.ProviderKey == f.Target.Key)).Status);
        if (fallback)
        {
            Assert.NotNull(read);
            await using (read)
            {
                using var received = new MemoryStream(); await read.Content.CopyToAsync(received);
                Assert.Equal(f.Content, received.ToArray()); Assert.Equal(f.Source.Key, read.ProviderKey);
            }
        }
        else Assert.Null(read);
    }

    [Theory]
    [InlineData("valid", StorageOperationTypes.Repair)]
    [InlineData("targetMismatch", StorageOperationTypes.Repair)]
    [InlineData("targetMismatch", StorageOperationTypes.Replicate)]
    [InlineData("targetMismatch", StorageOperationTypes.Verify)]
    [InlineData("targetMismatch", StorageOperationTypes.Delete)]
    [InlineData("sourceMismatch", StorageOperationTypes.Repair)]
    [InlineData("sourceMismatch", StorageOperationTypes.Replicate)]
    [InlineData("sourceChangedDuringRead", StorageOperationTypes.Repair)]
    [InlineData("originalMismatch", StorageOperationTypes.Repair)]
    public async Task ConnectedLocatorsRequireMatchingCatalogConnectionBeforeIo(string scenario, string operationType)
    {
        await using var f = await Fixture.CreateAsync(connected: true);
        var id = await f.SeedReplicateAsync();
        var operation = await f.Db.StorageOperations.SingleAsync(o => o.Id == id);
        var original = JsonSerializer.Deserialize<StorageOperationPayload>(operation.PayloadJson!)!;
        operation.OperationType = operationType;
        operation.ConnectionId = scenario == "targetMismatch" ? "foreign" : "target";
        var targetLocator = original.TargetLocator;
        if (scenario == "originalMismatch")
        {
            targetLocator = f.Target.CreateRepairLocator(original.TargetLocator, operation.StorageObjectGenerationId!.Value, operation.IdempotencyKey);
            operation.PayloadJson = JsonSerializer.Serialize(original with { TargetLocator = targetLocator, OriginalTargetLocator = "foreign/original" });
            f.Db.StorageReplicas.Add(new()
            {
                StorageObjectGenerationId = operation.StorageObjectGenerationId.Value,
                ProviderKey = f.Target.Key,
                ConnectionId = "target",
                Locator = "foreign/original",
                Status = StorageReplicaStatuses.Corrupt,
                SizeBytes = f.Content.Length,
                Sha256 = f.Sha256
            });
        }
        f.Db.StorageReplicas.Add(new()
        {
            StorageObjectGenerationId = operation.StorageObjectGenerationId!.Value,
            ProviderKey = f.Source.Key,
            ConnectionId = scenario == "sourceMismatch" ? "foreign" : "source",
            Locator = original.SourceLocator!,
            Status = StorageReplicaStatuses.Available,
            SizeBytes = f.Content.Length,
            Sha256 = f.Sha256
        });
        f.Db.StorageReplicas.Add(new()
        {
            StorageObjectGenerationId = operation.StorageObjectGenerationId.Value,
            ProviderKey = f.Target.Key,
            ConnectionId = operation.ConnectionId,
            Locator = targetLocator,
            Status = StorageReplicaStatuses.Missing,
            SizeBytes = f.Content.Length,
            Sha256 = f.Sha256
        });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        if (scenario == "sourceChangedDuringRead") f.Source.BeforeReadReturn = async () =>
            await f.Db.StorageReplicas.Where(r => r.ProviderKey == f.Source.Key).ExecuteUpdateAsync(s => s.SetProperty(r => r.ConnectionId, "foreign"));
        var result = await f.Processor.ProcessBatchAsync("owned-connection-guard", CancellationToken.None);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(scenario == "valid" ? 1 : 0, result.Succeeded);
        Assert.Equal(scenario == "valid" ? 1 : 0, f.Target.PutCalls);
        if (scenario is "targetMismatch" or "originalMismatch")
        {
            Assert.Equal(0, f.Target.HeadCalls);
            Assert.Equal(0, f.Source.ReadCalls);
            Assert.Equal(0, f.Target.DeleteCalls);
        }
        if (scenario == "sourceMismatch") Assert.Equal(0, f.Source.ReadCalls);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("target")]
    public async Task ReconciliationDoesNotProbeMismatchedConnectionOrChangeItsCatalogState(string badProvider)
    {
        await using var f = await Fixture.CreateAsync(connected: true);
        var id = await f.SeedReplicateAsync();
        var operation = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == id);
        var plan = JsonSerializer.Deserialize<StorageOperationPayload>(operation.PayloadJson!)!;
        f.Db.StorageReplicas.Add(new()
        {
            StorageObjectGenerationId = operation.StorageObjectGenerationId!.Value,
            ProviderKey = f.Source.Key,
            ConnectionId = badProvider == "source" ? "foreign" : "source",
            Locator = plan.SourceLocator!,
            Status = StorageReplicaStatuses.Available,
            SizeBytes = f.Content.Length,
            Sha256 = f.Sha256
        });
        f.Db.StorageReplicas.Add(new()
        {
            StorageObjectGenerationId = operation.StorageObjectGenerationId.Value,
            ProviderKey = f.Target.Key,
            ConnectionId = badProvider == "target" ? "foreign" : "target",
            Locator = plan.TargetLocator,
            Status = StorageReplicaStatuses.Missing,
            SizeBytes = f.Content.Length,
            Sha256 = f.Sha256
        });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var before = await f.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.ProviderKey == badProvider);
        var result = await new StorageReconciliationService(f.Db, new StorageProviderRegistry([f.Source, f.Target])).RunAsync(true, CancellationToken.None);
        Assert.Contains(result.Issues, i => i.ProviderKey == badProvider && i.Kind == "locatorConnectionMismatch");
        Assert.Equal(0, result.RepairOperationsQueued);
        Assert.Equal(0, (badProvider == "source" ? f.Source : f.Target).HeadCalls);
        var after = await f.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.Id == before.Id);
        Assert.Equal(before.Status, after.Status); Assert.Equal(before.UpdatedAt, after.UpdatedAt); Assert.Equal(before.LastVerifiedAt, after.LastVerifiedAt);
        Assert.Equal(1, await f.Db.StorageOperations.CountAsync());
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("connected")]
    [InlineData("lostAck")]
    [InlineData("badSource")]
    [InlineData("unavailable")]
    [InlineData("alreadyGood")]
    [InlineData("planSaveFailure")]
    [InlineData("planCommitFailure")]
    [InlineData("planLostAck")]
    [InlineData("leaseLostAfterPut")]
    public async Task LegacyCorruptRepairUpgradesItsOwnIntentBeforeWriting(string scenario)
    {
        await using var f = await Fixture.CreateAsync(connected: scenario == "connected", maxAttempts: 8);
        var id = await f.SeedReplicateAsync(status: StorageOperationStatuses.Failed);
        var before = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == id);
        var old = JsonSerializer.Deserialize<StorageOperationPayload>(before.PayloadJson!)!;
        await f.Db.StorageOperations.Where(o => o.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(o => o.OperationType, StorageOperationTypes.Repair).SetProperty(o => o.AttemptCount, 3));
        var generationId = before.StorageObjectGenerationId!.Value;
        f.Db.StorageReplicas.Add(new()
        {
            StorageObjectGenerationId = generationId,
            ProviderKey = f.Source.Key,
            ConnectionId = scenario == "connected" ? "source" : null,
            Locator = old.SourceLocator!,
            Status = StorageReplicaStatuses.Available,
            SizeBytes = f.Content.Length,
            Sha256 = f.Sha256
        });
        f.Db.StorageReplicas.Add(new()
        {
            StorageObjectGenerationId = generationId,
            ProviderKey = f.Target.Key,
            ConnectionId = before.ConnectionId,
            Locator = old.TargetLocator,
            Status = StorageReplicaStatuses.Available,
            SizeBytes = f.Content.Length,
            Sha256 = f.Sha256
        });
        var damaged = f.Content.ToArray(); damaged[0] ^= 1;
        f.Target.Objects[old.TargetLocator] = scenario == "alreadyGood" ? f.Content : damaged;
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        if (scenario == "badSource") f.Source.Objects[old.SourceLocator!] = damaged;
        if (scenario == "unavailable") f.Target.HeadUnavailable = true;
        if (scenario == "alreadyGood") f.Source.Objects.Clear();
        if (scenario == "lostAck") f.Target.ReturnUnknownAfterPut = true;
        if (scenario == "planSaveFailure") f.SaveFailure.PlanArmed = true;
        if (scenario == "planCommitFailure") f.CommitFailure.PlanArmed = true;
        if (scenario == "planLostAck") f.CommitFailure.PlanLostAck = true;
        if (scenario == "leaseLostAfterPut") f.Target.AfterPut = async () => await f.Db.StorageOperations.Where(o => o.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.LeaseOwner, "other-worker").SetProperty(o => o.LeaseExpiresAt, f.Clock.UtcNow.AddMinutes(5)));
        var first = await f.Processor.ProcessBatchAsync("legacy-upgrade", CancellationToken.None);
        Assert.Equal(1, first.Claimed);
        var upgraded = scenario is "valid" or "connected" or "lostAck" or "planLostAck" or "leaseLostAfterPut";
        var saved = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == id);
        var payload = JsonSerializer.Deserialize<StorageOperationPayload>(saved.PayloadJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(before.IdempotencyKey, saved.IdempotencyKey);
        Assert.Equal(before.CreatedAt, saved.CreatedAt);
        Assert.Equal(4, saved.AttemptCount);
        Assert.Equal(1, await f.Db.StorageOperations.CountAsync());
        Assert.Equal(upgraded ? old.TargetLocator : null, payload.OriginalTargetLocator);
        Assert.Equal(upgraded ? 3 : 2, await f.Db.StorageReplicas.CountAsync());
        Assert.Equal(scenario == "alreadyGood" ? f.Content : damaged, f.Target.Objects[old.TargetLocator]);
        Assert.Equal(scenario is "valid" or "connected" or "alreadyGood" ? 1 : 0, first.Succeeded);
        if (scenario is "badSource" or "unavailable" or "alreadyGood" or "planSaveFailure" or "planCommitFailure" or "planLostAck")
            Assert.Equal(0, f.Target.PutCalls);
        if (scenario is "lostAck" or "planLostAck" or "planSaveFailure" or "planCommitFailure")
        {
            f.Clock.Advance(TimeSpan.FromHours(1)); f.Target.ReturnUnknownAfterPut = false;
            Assert.Equal(1, (await f.Processor.ProcessBatchAsync("legacy-retry", CancellationToken.None)).Succeeded);
            Assert.Equal(1, f.Target.PutCalls);
        }
        f.Db.Clients.Add(new Client { Name = "Unrelated save after legacy upgrade" }); await f.Db.SaveChangesAsync();
        if (scenario == "leaseLostAfterPut")
        {
            Assert.Equal(StorageReplicaStatuses.RepairPending, (await f.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.Locator == payload.TargetLocator)).Status);
            Assert.Equal("other-worker", (await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == id)).LeaseOwner);
        }
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), $"pp_verified_repair_{id:N}_*.tmp"));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("lostAck")]
    [InlineData("sourceLostAfterWrite")]
    [InlineData("badSource")]
    [InlineData("deletedAfterPut")]
    [InlineData("leaseLostAfterPut")]
    [InlineData("collision")]
    [InlineData("wrongAck")]
    [InlineData("originalReboundDuringRead")]
    [InlineData("receiptSaveFailure")]
    [InlineData("receiptCommitFailure")]
    [InlineData("receiptLostAck")]
    public async Task CorruptRepairUsesDurableIsolatedDestinationAndRetainsOriginal(string scenario)
    {
        await using var f = await Fixture.CreateAsync();
        var seedId = await f.SeedReplicateAsync();
        var seed = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == seedId);
        var original = JsonSerializer.Deserialize<StorageOperationPayload>(seed.PayloadJson!)!;
        await f.Db.StorageOperations.Where(o => o.Id == seedId).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, StorageOperationStatuses.Succeeded));
        f.Db.StorageReplicas.Add(new()
        {
            StorageObjectGenerationId = seed.StorageObjectGenerationId!.Value,
            ProviderKey = f.Source.Key,
            Locator = original.SourceLocator!,
            Status = StorageReplicaStatuses.Available,
            SizeBytes = f.Content.Length,
            Sha256 = f.Sha256
        });
        f.Db.StorageReplicas.Add(new()
        {
            StorageObjectGenerationId = seed.StorageObjectGenerationId.Value,
            ProviderKey = f.Target.Key,
            Locator = original.TargetLocator,
            Status = StorageReplicaStatuses.Available,
            SizeBytes = f.Content.Length,
            Sha256 = f.Sha256
        });
        var damaged = f.Content.ToArray(); damaged[0] ^= 1;
        f.Target.Objects[original.TargetLocator] = damaged;
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var registry = new StorageProviderRegistry([f.Source, f.Target]);
        var reconcile = new StorageReconciliationService(f.Db, registry);
        Assert.Equal(1, (await reconcile.RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
        var operation = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == StorageOperationTypes.Repair);
        var planned = JsonSerializer.Deserialize<StorageOperationPayload>(operation.PayloadJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(original.TargetLocator, planned.OriginalTargetLocator);
        Assert.NotEqual(original.TargetLocator, planned.TargetLocator);
        Assert.Equal(f.Target.CreateRepairLocator(original.TargetLocator, seed.StorageObjectGenerationId.Value, operation.IdempotencyKey), planned.TargetLocator);
        Assert.Equal(StorageReplicaStatuses.RepairPending, (await f.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.Locator == planned.TargetLocator)).Status);
        Assert.Equal(0, (await reconcile.RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
        if (scenario is "lostAck" or "sourceLostAfterWrite") f.Target.ReturnUnknownAfterPut = true;
        if (scenario == "badSource") f.Source.Objects[original.SourceLocator!] = damaged;
        if (scenario == "collision") f.Target.Objects[planned.TargetLocator] = damaged;
        if (scenario == "wrongAck") f.Target.BadAcknowledgement = "badAckLocator";
        if (scenario == "receiptSaveFailure") f.SaveFailure.Armed = true;
        if (scenario == "receiptCommitFailure") f.CommitFailure.Armed = true;
        if (scenario == "receiptLostAck") f.CommitFailure.LostAck = true;
        if (scenario == "deletedAfterPut") f.Target.AfterPut = async () =>
            await f.Db.StorageObjects.Where(o => o.Id == seed.StorageObjectId).ExecuteUpdateAsync(s => s.SetProperty(o => o.IsDeleted, true));
        if (scenario == "leaseLostAfterPut") f.Target.AfterPut = async () =>
            await f.Db.StorageOperations.Where(o => o.Id == operation.Id).ExecuteUpdateAsync(s => s
                .SetProperty(o => o.LeaseOwner, "different-worker").SetProperty(o => o.LeaseExpiresAt, f.Clock.UtcNow.AddMinutes(5)));
        if (scenario == "originalReboundDuringRead") f.Source.BeforeReadReturn = async () =>
            await f.Db.StorageReplicas.Where(r => r.Locator == original.TargetLocator).ExecuteUpdateAsync(s => s.SetProperty(r => r.Locator, "unrelated-original"));
        Assert.NotNull(operation.NextAttemptAt);
        Assert.Equal(0, (await f.Processor.ProcessBatchAsync("isolated-repair-not-due", CancellationToken.None)).Claimed);
        Assert.Equal(0, f.Target.PutCalls);
        f.Clock.Advance(operation.NextAttemptAt.Value - f.Clock.UtcNow); // Run exactly when the persisted repair is due.
        var first = await f.Processor.ProcessBatchAsync("isolated-repair-first", CancellationToken.None);
        Assert.Equal(1, first.Claimed);
        Assert.Equal(damaged, f.Target.Objects[original.TargetLocator]);
        Assert.Equal(scenario is "valid" or "receiptLostAck" ? 1 : 0, first.Succeeded);
        var status = (await f.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.Locator == planned.TargetLocator)).Status;
        Assert.Equal(scenario is "valid" or "receiptLostAck" ? StorageReplicaStatuses.Available : StorageReplicaStatuses.RepairPending, status);
        if (scenario is "lostAck" or "sourceLostAfterWrite" or "wrongAck" or "receiptSaveFailure" or "receiptCommitFailure")
        {
            Assert.Equal(1, f.Target.PutCalls);
            Assert.Equal(1, (await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == operation.Id)).AttemptCount);
            if (scenario == "sourceLostAfterWrite")
            {
                f.Source.Objects.Clear();
                await f.Db.StorageReplicas.Where(r => r.ProviderKey == f.Source.Key).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, StorageReplicaStatuses.Missing));
            }
            f.Clock.Advance(TimeSpan.FromHours(1)); f.Target.ReturnUnknownAfterPut = false; f.Target.BadAcknowledgement = null;
            Assert.Equal(1, (await f.Processor.ProcessBatchAsync("isolated-repair-receipt", CancellationToken.None)).Succeeded);
            Assert.Equal(1, f.Target.PutCalls); // A receipt does not need the now-missing source.
        }
        if (scenario is "valid" or "lostAck" or "sourceLostAfterWrite" or "wrongAck" or "receiptSaveFailure" or "receiptCommitFailure" or "receiptLostAck")
        {
            Assert.Equal(f.Content, f.Target.Objects[planned.TargetLocator]);
            Assert.Equal(StorageReplicaStatuses.RetainedCorrupt, (await f.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.Locator == original.TargetLocator)).Status);
            Assert.Equal(StorageOperationStatuses.Succeeded, (await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == operation.Id)).Status);
            if (scenario != "sourceLostAfterWrite") Assert.Equal(0, (await reconcile.RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
        }
        if (scenario == "receiptLostAck")
        {
            f.Db.Clients.Add(new Client { Name = "Unrelated save after lost commit ACK" }); await f.Db.SaveChangesAsync();
            Assert.Equal(StorageReplicaStatuses.Available, (await f.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.Locator == planned.TargetLocator)).Status);
            Assert.Equal(0, (await f.Processor.ProcessBatchAsync("isolated-after-committed-ack", CancellationToken.None)).Claimed);
            Assert.Equal(1, f.Target.PutCalls);
        }
        if (scenario is "badSource" or "originalReboundDuringRead") Assert.Equal(0, f.Target.PutCalls);
        if (scenario == "leaseLostAfterPut")
        {
            f.Db.Clients.Add(new Client { Name = "Unrelated save must not publish stale replacement" }); await f.Db.SaveChangesAsync();
            Assert.Equal(StorageReplicaStatuses.RepairPending, (await f.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.Locator == planned.TargetLocator)).Status);
            Assert.Equal(StorageReplicaStatuses.Corrupt, (await f.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.Locator == original.TargetLocator)).Status);
            Assert.Equal("different-worker", (await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == operation.Id)).LeaseOwner);
        }
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), $"pp_verified_repair_{operation.Id:N}_*.tmp"));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("nonSeeking")]
    [InlineData("badHash")]
    [InlineData("badSize")]
    [InlineData("oversizedStream")]
    [InlineData("unboundSource")]
    [InlineData("unboundTarget")]
    [InlineData("deletedDuringRead")]
    [InlineData("generationChangedDuringRead")]
    [InlineData("payloadChangedDuringRead")]
    [InlineData("leaseLostDuringRead")]
    [InlineData("leaseLostAfterPut")]
    [InlineData("cancelDuringRead")]
    [InlineData("targetUnavailable")]
    [InlineData("badAckHash")]
    [InlineData("badAckSize")]
    [InlineData("badAckLocator")]
    public async Task RepairVerifiesSourceBytesAndFreshAuthorityBeforeWriting(string scenario)
        => await VerifyCopyEvidenceAsync(scenario, legacyReplication: false);

    [Theory]
    [InlineData("valid")]
    [InlineData("nonSeeking")]
    [InlineData("badHash")]
    [InlineData("badSize")]
    [InlineData("oversizedStream")]
    [InlineData("unboundSource")]
    [InlineData("unboundTarget")]
    [InlineData("deletedDuringRead")]
    [InlineData("generationChangedDuringRead")]
    [InlineData("payloadChangedDuringRead")]
    [InlineData("leaseLostDuringRead")]
    [InlineData("leaseLostAfterPut")]
    [InlineData("cancelDuringRead")]
    [InlineData("targetUnavailable")]
    [InlineData("badAckHash")]
    [InlineData("badAckSize")]
    [InlineData("badAckLocator")]
    [InlineData("currentGenerationChangedDuringRead")]
    [InlineData("targetBytesMismatch")]
    [InlineData("targetMissingAfterPut")]
    public async Task LegacyReplicationVerifiesBytesAndFreshAuthorityBeforeWriting(string scenario)
        => await VerifyCopyEvidenceAsync(scenario, legacyReplication: true);

    [Fact]
    public async Task LegacyReplicationLostAckUsesVerifiedTargetWithoutSecondPutWhenSourceUnavailable()
    {
        await using var f = await Fixture.CreateAsync(legacySource: true);
        var id = await f.SeedReplicateAsync();
        var operation = await f.Db.StorageOperations.SingleAsync(o => o.Id == id);
        var payload = JsonSerializer.Deserialize<StorageOperationPayload>(operation.PayloadJson!)!;
        foreach (var (provider, locator, status) in new[] { (f.Source.Key, payload.SourceLocator!, StorageReplicaStatuses.Available),
            (f.Target.Key, payload.TargetLocator, StorageReplicaStatuses.Pending) })
            f.Db.StorageReplicas.Add(new()
            {
                StorageObjectGenerationId = operation.StorageObjectGenerationId!.Value,
                ProviderKey = provider,
                Locator = locator,
                Status = status,
                SizeBytes = f.Content.Length,
                Sha256 = f.Sha256
            });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        f.Target.ReturnUnknownAfterPut = true;
        Assert.Equal(1, (await f.Processor.ProcessBatchAsync("legacy-lost-ack", CancellationToken.None)).Unknown);
        Assert.Equal(1, f.Target.PutCalls);
        f.Source.Objects.Clear();
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, (await f.Processor.ProcessBatchAsync("legacy-receipt", CancellationToken.None)).Succeeded);
        Assert.Equal(1, f.Target.PutCalls);
        Assert.Equal(1, f.Source.ReadCalls);
        Assert.Equal(1, f.Target.ReadCalls);
        Assert.Equal(f.Content, f.Target.Objects[payload.TargetLocator]);
        Assert.Equal(StorageOperationStatuses.Succeeded, (await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == id)).Status);
        Assert.False(f.Target.LastReadStream!.CanRead);
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), $"pp_verified_repair_{id:N}_*.tmp"));
    }

    [Fact]
    public async Task LegacyReplicationFreshAuthorityRunsOnOwnedMigratedPostgreSqlDatabase()
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        var name = "pp_legacy_copy_" + Guid.NewGuid().ToString("N");
        var adminSettings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        Assert.Contains(adminSettings.Host, new[] { "127.0.0.1", "localhost", "::1" });
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            { await command.ExecuteNonQueryAsync(); created = true; }
            var settings = new NpgsqlConnectionStringBuilder(adminSettings.ConnectionString) { Database = name };
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString).Options;
            await using (var migrate = new PostgreSqlPaymentContext(options)) await migrate.Database.MigrateAsync();
            foreach (var scenario in new[] { "valid", "nonSeeking", "badHash", "badSize", "oversizedStream", "unboundSource",
                "unboundTarget", "deletedDuringRead", "generationChangedDuringRead", "payloadChangedDuringRead",
                "leaseLostDuringRead", "leaseLostAfterPut", "cancelDuringRead", "targetUnavailable", "badAckHash",
                "badAckSize", "badAckLocator", "currentGenerationChangedDuringRead", "targetBytesMismatch", "targetMissingAfterPut" })
            {
                await VerifyCopyEvidenceAsync(scenario, legacyReplication: true, new PostgreSqlPaymentContext(options));
                await using var cleanup = new PostgreSqlPaymentContext(options);
                await cleanup.StorageOperations.ExecuteDeleteAsync();
                await cleanup.StorageObjects.ExecuteDeleteAsync();
                await cleanup.Clients.IgnoreQueryFilters().ExecuteDeleteAsync();
            }
            output.WriteLine("Owned PostgreSQL migrations and all 20 legacy replication source/target authority scenarios executed.");
        }
        finally
        {
            if (created)
            {
                var until = DateTime.UtcNow.AddSeconds(5);
                while (true)
                {
                    await using var active = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @name", admin);
                    active.Parameters.AddWithValue("name", name);
                    if ((long)(await active.ExecuteScalarAsync())! == 0) break;
                    if (DateTime.UtcNow >= until) throw new InvalidOperationException("Owned legacy copy database still has active connections; preserved.");
                    await Task.Delay(100);
                }
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\"", admin);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task VerifyCopyEvidenceAsync(string scenario, bool legacyReplication, PaymentContext? database = null)
    {
        await using var f = await Fixture.CreateAsync(legacySource: legacyReplication, database: database);
        var id = await f.SeedReplicateAsync();
        var operation = await f.Db.StorageOperations.Include(o => o.StorageObjectGeneration).SingleAsync(o => o.Id == id);
        var payload = JsonSerializer.Deserialize<StorageOperationPayload>(operation.PayloadJson!)!;
        operation.OperationType = legacyReplication ? StorageOperationTypes.Replicate : StorageOperationTypes.Repair;
        f.Db.StorageReplicas.Add(new()
        {
            StorageObjectGenerationId = operation.StorageObjectGenerationId!.Value,
            ProviderKey = f.Source.Key,
            Locator = scenario == "unboundSource" ? "other-source" : payload.SourceLocator!,
            Status = StorageReplicaStatuses.Available,
            SizeBytes = f.Content.Length,
            Sha256 = f.Sha256
        });
        f.Db.StorageReplicas.Add(new()
        {
            StorageObjectGenerationId = operation.StorageObjectGenerationId.Value,
            ProviderKey = f.Target.Key,
            Locator = scenario == "unboundTarget" ? "other-target" : payload.TargetLocator,
            Status = StorageReplicaStatuses.Missing,
            SizeBytes = f.Content.Length,
            Sha256 = f.Sha256
        });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        f.Source.NonSeeking = scenario == "nonSeeking";
        using var cancel = new CancellationTokenSource();
        if (scenario == "badHash") f.Source.Objects[payload.SourceLocator!] = new byte[f.Content.Length];
        if (scenario == "badSize") f.Source.Objects[payload.SourceLocator!] = [1];
        if (scenario == "oversizedStream")
        {
            f.Source.Objects[payload.SourceLocator!] = new byte[f.Content.Length + 1];
            f.Source.ReportedReadSize = f.Content.Length;
        }
        if (scenario == "deletedDuringRead") f.Source.BeforeReadReturn = async () =>
            await f.Db.StorageObjects.Where(o => o.Id == operation.StorageObjectId).ExecuteUpdateAsync(s => s.SetProperty(o => o.IsDeleted, true));
        if (scenario == "generationChangedDuringRead") f.Source.BeforeReadReturn = async () =>
            await f.Db.StorageObjectGenerations.Where(g => g.Id == operation.StorageObjectGenerationId).ExecuteUpdateAsync(s => s.SetProperty(g => g.Sha256, new string('0', 64)));
        if (scenario == "currentGenerationChangedDuringRead") f.Source.BeforeReadReturn = async () =>
            await f.Db.StorageObjects.Where(o => o.Id == operation.StorageObjectId).ExecuteUpdateAsync(s => s.SetProperty(o => o.CurrentGeneration, 2));
        if (scenario == "payloadChangedDuringRead") f.Source.BeforeReadReturn = async () =>
        {
            var changedPayload = JsonSerializer.Serialize(new StorageOperationPayload("other-target", f.Source.Key, payload.SourceLocator));
            await f.Db.StorageOperations.Where(o => o.Id == id).ExecuteUpdateAsync(s => s.SetProperty(o => o.PayloadJson, changedPayload));
        };
        if (scenario == "leaseLostDuringRead") f.Source.BeforeReadReturn = async () =>
            await f.Db.StorageOperations.Where(o => o.Id == id).ExecuteUpdateAsync(s => s.SetProperty(o => o.LeaseOwner, "different-worker"));
        if (scenario == "leaseLostAfterPut") f.Target.AfterPut = async () =>
            await f.Db.StorageOperations.Where(o => o.Id == id).ExecuteUpdateAsync(s => s.SetProperty(o => o.LeaseOwner, "different-worker"));
        if (scenario == "targetBytesMismatch") f.Target.AfterPut = () =>
        {
            f.Target.Objects[payload.TargetLocator] = new byte[f.Content.Length]; return Task.CompletedTask;
        };
        if (scenario == "targetMissingAfterPut") f.Target.AfterPut = () =>
        {
            f.Target.Objects.Remove(payload.TargetLocator); return Task.CompletedTask;
        };
        if (scenario == "cancelDuringRead") f.Source.BeforeReadReturn = () => { cancel.Cancel(); return Task.CompletedTask; };
        f.Target.HeadUnavailable = scenario == "targetUnavailable";
        f.Target.BadAcknowledgement = scenario.StartsWith("badAck", StringComparison.Ordinal) ? scenario : null;
        if (scenario == "cancelDuringRead")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Processor.ProcessBatchAsync("repair-evidence", cancel.Token));
            Assert.Equal(0, f.Target.PutCalls);
            Assert.False(f.Source.LastReadStream!.CanRead);
            Assert.Empty(Directory.GetFiles(Path.GetTempPath(), $"pp_verified_repair_{id:N}_*.tmp"));
            return;
        }
        var batch = await f.Processor.ProcessBatchAsync("repair-evidence", cancel.Token);
        var success = scenario is "valid" or "nonSeeking";
        Assert.Equal(success ? 1 : 0, batch.Succeeded);
        var wrote = success || scenario is "leaseLostAfterPut" or "targetBytesMismatch" or "targetMissingAfterPut" || f.Target.BadAcknowledgement is not null;
        Assert.Equal(wrote ? 1 : 0, f.Target.PutCalls);
        if (wrote && scenario is not "targetBytesMismatch" and not "targetMissingAfterPut") Assert.Equal(f.Content, f.Target.Objects[payload.TargetLocator]);
        else if (scenario == "targetBytesMismatch") Assert.NotEqual(f.Content, f.Target.Objects[payload.TargetLocator]);
        else Assert.Empty(f.Target.Objects);
        if (scenario is "leaseLostDuringRead" or "leaseLostAfterPut")
        {
            f.Db.Clients.Add(new Client { Name = "Preserve independent save after lost lease" });
            await f.Db.SaveChangesAsync();
            var retained = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == id);
            Assert.Equal("different-worker", retained.LeaseOwner);
            Assert.Equal(StorageOperationStatuses.Running, retained.Status);
            Assert.Equal(StorageReplicaStatuses.Missing, (await f.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.ProviderKey == f.Target.Key)).Status);
        }
        if (f.Source.LastReadStream is { } read) Assert.False(read.CanRead);
        if (legacyReplication && f.Target.LastReadStream is { } targetRead) Assert.False(targetRead.CanRead);
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), $"pp_verified_repair_{id:N}_*.tmp"));
        if (scenario == "payloadChangedDuringRead")
        {
            var retained = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == id);
            Assert.Equal(StorageOperationStatuses.Running, retained.Status);
            Assert.Contains("other-target", retained.PayloadJson);
        }
        if (f.Target.BadAcknowledgement is not null)
        {
            Assert.Equal(StorageReplicaStatuses.Missing, (await f.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.ProviderKey == f.Target.Key)).Status);
            f.Target.BadAcknowledgement = null; f.Clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(1, (await f.Processor.ProcessBatchAsync("repair-ack-reconcile", CancellationToken.None)).Succeeded);
            Assert.Equal(1, f.Target.PutCalls); // HEAD confirms the already written bytes instead of another PUT.
        }
    }

    [Theory]
    [InlineData("active")]
    [InlineData("noTombstone")]
    [InlineData("wrongReplica")]
    [InlineData("wrongGeneration")]
    [InlineData("hold")]
    [InlineData("retained")]
    [InlineData("expired")]
    [InlineData("changedBytes")]
    [InlineData("sameSizeChangedBytes")]
    [InlineData("missingBytes")]
    [InlineData("unavailableBytes")]
    public async Task PhysicalDeletionRequiresCurrentTombstoneAndExactReplicaAuthority(string scenario)
    {
        await using var f = await Fixture.CreateAsync();
        var id = await f.SeedReplicateAsync(isDeleted: scenario != "active");
        var operation = await f.Db.StorageOperations.Include(o => o.StorageObjectGeneration).SingleAsync(o => o.Id == id);
        var payload = JsonSerializer.Deserialize<StorageOperationPayload>(operation.PayloadJson!)!;
        operation.OperationType = StorageOperationTypes.Delete;
        if (scenario == "wrongGeneration")
        {
            var other = new StorageObjectGeneration
            {
                StorageObject = new StorageObject { DataClass = "test", OwnerType = "test", OwnerId = "other", LogicalName = "other.txt", CurrentGeneration = 1 },
                Generation = 1,
                SizeBytes = f.Content.Length,
                Sha256 = f.Sha256,
            };
            f.Db.StorageObjectGenerations.Add(other);
            await f.Db.SaveChangesAsync();
            operation.StorageObjectGeneration = other;
            operation.StorageObjectGenerationId = other.Id;
        }
        f.Target.Objects[payload.TargetLocator] = f.Content;
        if (scenario == "changedBytes") f.Target.Objects[payload.TargetLocator] = [9, 9, 9, 9];
        if (scenario == "sameSizeChangedBytes") f.Target.Objects[payload.TargetLocator] = new byte[f.Content.Length];
        if (scenario == "missingBytes") f.Target.Objects.Remove(payload.TargetLocator);
        f.Target.HeadUnavailable = scenario == "unavailableBytes";
        f.Db.StorageReplicas.Add(new()
        {
            StorageObjectGenerationId = operation.StorageObjectGenerationId!.Value,
            ProviderKey = f.Target.Key,
            Locator = scenario == "wrongReplica" ? "target/not-authorized" : payload.TargetLocator,
            Status = StorageReplicaStatuses.DeletePending,
            SizeBytes = f.Content.Length,
            Sha256 = f.Sha256,
        });
        if (scenario != "noTombstone") f.Db.StorageTombstones.Add(new()
        {
            StorageObjectId = operation.StorageObjectId,
            Generation = 1,
            DeletedAt = f.Clock.UtcNow,
            LegalHold = scenario == "hold",
            RetainUntil = scenario == "retained" ? f.Clock.UtcNow.AddDays(2) : null,
        });
        await f.Db.SaveChangesAsync();
        var batch = await f.Processor.ProcessBatchAsync("delete-authority", CancellationToken.None);
        if (scenario is "expired" or "missingBytes")
        {
            Assert.Equal(1, batch.Succeeded);
            Assert.Empty(f.Target.Objects);
            Assert.Equal(scenario == "expired" ? 1 : 0, f.Target.DeleteCalls);
        }
        else
        {
            Assert.Single(f.Target.Objects);
            Assert.Equal(0, f.Target.DeleteCalls);
            var saved = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == id);
            if (scenario is "hold" or "retained")
            {
                Assert.Equal(StorageOperationStatuses.Pending, saved.Status);
                Assert.Equal(0, saved.AttemptCount);
                Assert.True(saved.NextAttemptAt > f.Clock.UtcNow);
            }
            else if (scenario is "changedBytes" or "sameSizeChangedBytes" or "unavailableBytes")
                Assert.Equal(StorageOperationStatuses.Failed, saved.Status);
            else Assert.Equal(StorageOperationStatuses.DeadLetter, saved.Status);
        }
        Assert.Single(f.Source.Objects);
    }

    [Fact]
    public async Task ProcessBatch_ReplicatesAndRecordsVerifiedReplica()
    {
        await using var fixture = await Fixture.CreateAsync();
        var operationId = await fixture.SeedReplicateAsync();

        var result = await fixture.Processor.ProcessBatchAsync("worker-a", CancellationToken.None);

        Assert.Equal(1, result.Succeeded);
        var operation = await fixture.Db.StorageOperations.SingleAsync(item => item.Id == operationId);
        Assert.Equal(StorageOperationStatuses.Succeeded, operation.Status);
        var replica = await fixture.Db.StorageReplicas.SingleAsync();
        Assert.Equal(StorageReplicaStatuses.Available, replica.Status);
        Assert.Equal(fixture.Sha256, replica.Sha256);
        Assert.Equal(1, fixture.Target.PutCalls);
    }

    [Fact]
    public async Task ProcessBatch_ReconcilesUnknownWriteWithoutWritingTwice()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Target.ReturnUnknownAfterPut = true;
        var operationId = await fixture.SeedReplicateAsync();

        var first = await fixture.Processor.ProcessBatchAsync("worker-a", CancellationToken.None);
        Assert.Equal(1, first.Unknown);

        fixture.Clock.Advance(TimeSpan.FromHours(1));
        fixture.Target.ReturnUnknownAfterPut = false;
        var second = await fixture.Processor.ProcessBatchAsync("worker-b", CancellationToken.None);

        Assert.Equal(1, second.Succeeded);
        Assert.Equal(1, fixture.Target.PutCalls);
        Assert.Equal(StorageOperationStatuses.Succeeded,
            (await fixture.Db.StorageOperations.SingleAsync(item => item.Id == operationId)).Status);
    }

    [Fact]
    public async Task ProcessBatch_ClaimsExpiredLeaseButNotLiveLease()
    {
        await using var fixture = await Fixture.CreateAsync();
        var expiredId = await fixture.SeedReplicateAsync(StorageOperationStatuses.Running, fixture.Clock.UtcNow.AddMinutes(-1));
        var liveId = await fixture.SeedReplicateAsync(StorageOperationStatuses.Running, fixture.Clock.UtcNow.AddMinutes(5), "live-lease");

        var result = await fixture.Processor.ProcessBatchAsync("worker-new", CancellationToken.None);

        Assert.Equal(1, result.Claimed);
        Assert.Equal(StorageOperationStatuses.Succeeded,
            (await fixture.Db.StorageOperations.SingleAsync(item => item.Id == expiredId)).Status);
        Assert.Equal(StorageOperationStatuses.Running,
            (await fixture.Db.StorageOperations.SingleAsync(item => item.Id == liveId)).Status);
    }

    [Fact]
    public async Task ProcessBatch_DoesNotResurrectDeletedObject()
    {
        await using var fixture = await Fixture.CreateAsync();
        var operationId = await fixture.SeedReplicateAsync(isDeleted: true);

        var result = await fixture.Processor.ProcessBatchAsync("worker-a", CancellationToken.None);

        Assert.Equal(1, result.DeadLettered);
        Assert.Equal(0, fixture.Target.PutCalls);
        var operation = await fixture.Db.StorageOperations.SingleAsync(item => item.Id == operationId);
        Assert.Equal(StorageOperationStatuses.DeadLetter, operation.Status);
        Assert.Contains("tombstone", operation.LastError, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection? _connection;
        private int _sequence;

        private Fixture(SqliteConnection? connection, PaymentContext db, FakeTimeProvider clock, FakeProvider source, FakeProvider target, int maxAttempts)
        {
            _connection = connection;
            Db = db;
            Clock = clock;
            Source = source;
            Target = target;
            var options = new StaticOptionsMonitor<StorageOrchestrationOptions>(new StorageOrchestrationOptions
            {
                WorkerBatchSize = 20,
                WorkerLeaseSeconds = 60,
                WorkerMaxAttempts = maxAttempts,
            });
            Processor = new StorageOperationProcessor(
                db,
                new StorageProviderRegistry([source, target]),
                options,
                clock,
                NullLogger<StorageOperationProcessor>.Instance);
        }

        public PaymentContext Db { get; }
        public FakeTimeProvider Clock { get; }
        public FakeProvider Source { get; }
        public FakeProvider Target { get; }
        public StorageOperationProcessor Processor { get; }
        public ReceiptSaveFailure SaveFailure { get; private set; } = null!;
        public ReceiptCommitFailure CommitFailure { get; private set; } = null!;
        public byte[] Content { get; } = Encoding.UTF8.GetBytes("durable storage payload");
        public string Sha256 => Convert.ToHexString(SHA256.HashData(Content)).ToLowerInvariant();

        public static async Task<Fixture> CreateAsync(bool connected = false, int maxAttempts = 3, bool legacySource = false, PaymentContext? database = null)
        {
            SqliteConnection? connection = null;
            var failure = new ReceiptSaveFailure();
            var commitFailure = new ReceiptCommitFailure();
            var db = database;
            if (db is null)
            {
                connection = new SqliteConnection("Data Source=:memory:");
                await connection.OpenAsync();
                var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).AddInterceptors(failure, commitFailure).Options;
                db = new PaymentContext(options);
                await db.Database.EnsureCreatedAsync();
            }
            return new Fixture(connection, db, new FakeTimeProvider(), connected ? new ConnectedFakeProvider("source") : new FakeProvider(legacySource ? "legacy-local" : "source"),
                connected ? new ConnectedFakeProvider("target") : new FakeProvider("target"), maxAttempts)
            { SaveFailure = failure, CommitFailure = commitFailure };
        }

        public async Task<Guid> SeedReplicateAsync(
            string status = StorageOperationStatuses.Pending,
            DateTime? leaseExpiresAt = null,
            string? leaseOwner = null,
            bool isDeleted = false)
        {
            var index = Interlocked.Increment(ref _sequence);
            var sourceLocator = $"source/object-{index}";
            var targetLocator = $"target/object-{index}";
            Source.Objects[sourceLocator] = Content;
            var storageObject = new StorageObject
            {
                DataClass = "test",
                OwnerType = "test",
                OwnerId = index.ToString(),
                LogicalName = $"object-{index}.txt",
                ContentType = "text/plain",
                CurrentGeneration = 1,
                IsDeleted = isDeleted,
            };
            var generation = new StorageObjectGeneration
            {
                StorageObject = storageObject,
                Generation = 1,
                SizeBytes = Content.Length,
                Sha256 = Sha256,
                PolicyVersion = "test-v1",
            };
            var operation = new StorageOperation
            {
                StorageObject = storageObject,
                StorageObjectGeneration = generation,
                OperationType = StorageOperationTypes.Replicate,
                Status = status,
                IdempotencyKey = $"replicate-{index}",
                ProviderKey = Target.Key,
                ConnectionId = Target is IStorageConnectedLocatorProvider ? "target" : null,
                PayloadJson = JsonSerializer.Serialize(new StorageOperationPayload(targetLocator, Source.Key, sourceLocator)),
                LeaseOwner = leaseOwner,
                LeaseExpiresAt = leaseExpiresAt,
                CreatedAt = Clock.UtcNow,
                UpdatedAt = Clock.UtcNow,
            };
            Db.StorageOperations.Add(operation);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return operation.Id;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (_connection is not null) await _connection.DisposeAsync();
        }
    }

    private sealed class ReceiptSaveFailure : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool PlanArmed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (PlanArmed && data.Context!.ChangeTracker.Entries<StorageReplica>().Any(e => e.State == EntityState.Added
                && e.Entity.Status == StorageReplicaStatuses.RepairPending))
            {
                PlanArmed = false;
                throw new DbUpdateException("Owned injected legacy plan save failure");
            }
            if (Armed && data.Context!.ChangeTracker.Entries<StorageOperation>().Any(e =>
                e.Entity.OperationType == StorageOperationTypes.Repair && e.Entity.Status == StorageOperationStatuses.Succeeded))
            {
                Armed = false;
                throw new DbUpdateException("Owned injected repair receipt save failure");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ReceiptCommitFailure : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool LostAck { get; set; }
        public bool PlanArmed { get; set; }
        public bool PlanLostAck { get; set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData data, InterceptionResult result, CancellationToken ct = default)
        {
            if (PlanArmed && data.Context!.ChangeTracker.Entries<StorageReplica>().Any(e => e.Entity.Status == StorageReplicaStatuses.RepairPending))
            {
                PlanArmed = false;
                throw new IOException("Owned injected legacy plan commit failure");
            }
            if (Armed && data.Context!.ChangeTracker.Entries<StorageOperation>().Any(e =>
                e.Entity.OperationType == StorageOperationTypes.Repair && e.Entity.Status == StorageOperationStatuses.Succeeded))
            {
                Armed = false;
                throw new IOException("Owned injected repair commit failure after SaveChanges");
            }
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default)
        {
            if (PlanLostAck && data.Context!.ChangeTracker.Entries<StorageReplica>().Any(e => e.Entity.Status == StorageReplicaStatuses.RepairPending))
            {
                PlanLostAck = false;
                throw new IOException("Owned injected legacy plan commit acknowledgement loss");
            }
            if (LostAck && data.Context!.ChangeTracker.Entries<StorageOperation>().Any(e =>
                e.Entity.OperationType == StorageOperationTypes.Repair && e.Entity.Status == StorageOperationStatuses.Succeeded))
            {
                LostAck = false;
                throw new IOException("Owned injected lost repair commit acknowledgement");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
        public DateTime UtcNow => _now.UtcDateTime;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private class FakeProvider(string key) : IStorageProvider, IStorageRepairDestinationProvider
    {
        public string Key => key;
        public virtual string CreateRepairLocator(string originalLocator, Guid generationId, string idempotencyKey)
            => $"owned-recovery/{generationId:N}/{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey))).ToLowerInvariant()}.bin";
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public bool ReturnUnknownAfterPut { get; set; }
        public bool HeadUnavailable { get; set; }
        public long? ReportedReadSize { get; set; }
        public Func<Task>? BeforeReadReturn { get; set; }
        public Func<Task>? AfterPut { get; set; }
        public bool NonSeeking { get; set; }
        public Stream? LastReadStream { get; private set; }
        public string? BadAcknowledgement { get; set; }
        public int PutCalls { get; private set; }
        public int HeadCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public StorageProviderCapabilities Capabilities { get; } = new(true, true, true, true, false, false, false, false, false);

        public async Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken cancellationToken)
        {
            PutCalls++;
            using var memory = new MemoryStream();
            await request.Content.CopyToAsync(memory, cancellationToken);
            var bytes = memory.ToArray();
            if (!request.Overwrite && Objects.ContainsKey(request.Locator))
                return new(StorageOperationStatus.Conflict, request.Locator, 0, null);
            Objects[request.Locator] = bytes;
            if (AfterPut is { } afterPut) await afterPut();
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return new(ReturnUnknownAfterPut ? StorageOperationStatus.Unknown : StorageOperationStatus.Succeeded,
                BadAcknowledgement == "badAckLocator" ? "different-target" : request.Locator,
                BadAcknowledgement == "badAckSize" ? bytes.Length + 1 : bytes.Length,
                BadAcknowledgement == "badAckHash" ? new string('0', 64) : hash, Error: ReturnUnknownAfterPut ? "response lost" : null);
        }

        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken cancellationToken)
        {
            HeadCalls++;
            if (HeadUnavailable)
                return Task.FromResult(new StorageObjectMetadata(StorageOperationStatus.Unavailable, locator, 0, null, null));
            if (!Objects.TryGetValue(locator, out var bytes))
                return Task.FromResult(new StorageObjectMetadata(StorageOperationStatus.NotFound, locator, 0, null, null));
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return Task.FromResult(new StorageObjectMetadata(StorageOperationStatus.Succeeded, locator, bytes.Length, hash, DateTime.UtcNow));
        }

        public async Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken cancellationToken)
        {
            ReadCalls++;
            if (BeforeReadReturn is { } callback) await callback();
            return Objects.TryGetValue(locator, out var bytes)
                ? new StorageReadResult(StorageOperationStatus.Succeeded, locator,
                    LastReadStream = NonSeeking ? new NonSeekingStream(bytes) : new MemoryStream(bytes, writable: false), ReportedReadSize ?? bytes.Length)
                : new StorageReadResult(StorageOperationStatus.NotFound, locator, null, 0);
        }

        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            return Task.FromResult(Objects.Remove(locator)
                ? new StorageDeleteResult(StorageOperationStatus.Succeeded, locator)
                : new StorageDeleteResult(StorageOperationStatus.NotFound, locator));
        }
    }

    private sealed class ConnectedFakeProvider(string key) : FakeProvider(key), IStorageConnectedLocatorProvider
    {
        public bool TryGetConnectionId(string locator, out string connectionId)
        {
            var separator = locator.IndexOf('/');
            connectionId = separator > 0 ? locator[..separator] : string.Empty;
            return separator > 0;
        }
        public override string CreateRepairLocator(string originalLocator, Guid generationId, string idempotencyKey)
            => Key + "/" + base.CreateRepairLocator(originalLocator, generationId, idempotencyKey);
    }

    private sealed class NonSeekingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    }
}
