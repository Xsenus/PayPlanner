using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageAdminHealthTests
{
    [Theory]
    [InlineData(StorageOperationStatuses.Pending, StorageReplicaStatuses.Available, "healthy", "degraded")]
    [InlineData(StorageOperationStatuses.Running, StorageReplicaStatuses.Available, "healthy", "degraded")]
    [InlineData(StorageOperationStatuses.Failed, StorageReplicaStatuses.Available, "healthy", "degraded")]
    [InlineData(StorageOperationStatuses.Unknown, StorageReplicaStatuses.Available, "healthy", "degraded")]
    [InlineData(StorageOperationStatuses.DeadLetter, StorageReplicaStatuses.Available, "healthy", "degraded")]
    [InlineData(StorageOperationStatuses.Succeeded, StorageReplicaStatuses.Pending, "healthy", "degraded")]
    [InlineData(StorageOperationStatuses.Succeeded, StorageReplicaStatuses.Missing, "healthy", "degraded")]
    [InlineData(StorageOperationStatuses.Succeeded, StorageReplicaStatuses.Suspect, "healthy", "degraded")]
    [InlineData(StorageOperationStatuses.Succeeded, StorageReplicaStatuses.Corrupt, "healthy", "degraded")]
    [InlineData(StorageOperationStatuses.Succeeded, StorageReplicaStatuses.RepairPending, "healthy", "degraded")]
    [InlineData(StorageOperationStatuses.Succeeded, StorageReplicaStatuses.DeletePending, "healthy", "degraded")]
    [InlineData(StorageOperationStatuses.Succeeded, StorageReplicaStatuses.RetainedCorrupt, "healthy", "degraded")]
    [InlineData(StorageOperationStatuses.Succeeded, StorageReplicaStatuses.Available, "open", "degraded")]
    [InlineData(StorageOperationStatuses.Succeeded, StorageReplicaStatuses.Available, "halfOpen", "degraded")]
    [InlineData("unexpected", StorageReplicaStatuses.Available, "healthy", "degraded")]
    [InlineData(StorageOperationStatuses.Succeeded, "unexpected", "healthy", "degraded")]
    [InlineData(StorageOperationStatuses.Succeeded, StorageReplicaStatuses.Available, "unexpected", "degraded")]
    [InlineData(StorageOperationStatuses.Succeeded, StorageReplicaStatuses.Available, "healthy", "healthy")]
    [InlineData(StorageOperationStatuses.Succeeded, StorageReplicaStatuses.Deleted, "healthy", "healthy")]
    public async Task HealthDoesNotHideOutstandingWorkOrUnhealthyProviders(
        string operationStatus, string replicaStatus, string providerState, string expected)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var commandCount = 0;
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection)
            .LogTo(_ => commandCount++, [RelationalEventId.CommandExecuted]).Options);
        await db.Database.EnsureCreatedAsync();
        var entry = new StorageObject
        {
            DataClass = "task-file",
            OwnerType = "task-file",
            OwnerId = "42",
            CurrentGeneration = 1,
            IsDeleted = replicaStatus == StorageReplicaStatuses.Deleted
        };
        var generation = new StorageObjectGeneration { StorageObject = entry, Generation = 1, SizeBytes = 1, Sha256 = new string('a', 64) };
        entry.Generations.Add(generation);
        generation.Replicas.Add(new()
        {
            ProviderKey = "local",
            Locator = "owned-health-probe",
            Status = replicaStatus,
            SizeBytes = 1,
            Sha256 = generation.Sha256
        });
        db.StorageObjects.Add(entry);
        db.StorageOperations.Add(new()
        {
            StorageObject = entry,
            StorageObjectGeneration = generation,
            OperationType = StorageOperationTypes.Replicate,
            ProviderKey = "local",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            Status = operationStatus
        });
        await db.SaveChangesAsync();
        commandCount = 0;
        var controller = new StorageAdminController(null!, new StorageProviderRegistry([new UnusedProvider()]),
            new HealthSnapshot(providerState), null!, null!, null!, null!, db, null!);
        var response = Assert.IsType<OkObjectResult>((await controller.GetHealth(CancellationToken.None)).Result);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(response.Value));
        Assert.Equal(expected, body.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("operationCounts").GetProperty(operationStatus).GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("replicaCounts").GetProperty(replicaStatus).GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("pendingCapacityReservations").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("pendingStageCapacityReservations").GetInt32());
        Assert.Equal(4, commandCount);
    }

    [Fact]
    public async Task HealthFlagsOldPendingCapacityReservationButNotRecentUpload()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        var reservation = new StorageCapacityReservation
        {
            IdempotencyKey = "stage:" + new string('a', 64),
            ResourceKey = "[\"local\",null]",
            GroupId = "local-test",
            BindingHash = new string('b', 64),
            Bytes = 4,
            ObjectId = Guid.NewGuid(),
            LocalLocator = ".task-uploads/recent.tmp",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.StorageCapacityReservations.Add(reservation);
        await db.SaveChangesAsync();
        var controller = new StorageAdminController(null!, new StorageProviderRegistry([new UnusedProvider()]),
            new HealthSnapshot("healthy"), null!, null!, null!, null!, db, null!);
        async Task<JsonDocument> ReadAsync()
        {
            var response = Assert.IsType<OkObjectResult>((await controller.GetHealth(CancellationToken.None)).Result);
            return JsonDocument.Parse(JsonSerializer.Serialize(response.Value));
        }
        using (var recent = await ReadAsync())
        {
            Assert.Equal("healthy", recent.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, recent.RootElement.GetProperty("pendingCapacityReservations").GetInt32());
            Assert.Equal(1, recent.RootElement.GetProperty("pendingStageCapacityReservations").GetInt32());
        }
        db.StorageCapacityReservations.Add(new StorageCapacityReservation
        {
            IdempotencyKey = "upload:" + new string('c', 64),
            ResourceKey = "[\"local\",null]",
            GroupId = "local-test",
            BindingHash = new string('d', 64),
            Bytes = 8,
            ObjectId = Guid.NewGuid(),
            LocalLocator = "ordinary.tmp",
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        using (var mixed = await ReadAsync())
        {
            Assert.Equal(2, mixed.RootElement.GetProperty("pendingCapacityReservations").GetInt32());
            Assert.Equal(1, mixed.RootElement.GetProperty("pendingStageCapacityReservations").GetInt32());
        }
        var old = now.AddHours(-1);
        reservation.CreatedAt = old;
        reservation.UpdatedAt = old;
        await db.SaveChangesAsync();
        using var stale = await ReadAsync();
        Assert.Equal("degraded", stale.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, stale.RootElement.GetProperty("pendingCapacityReservations").GetInt32());
        Assert.Equal(1, stale.RootElement.GetProperty("pendingStageCapacityReservations").GetInt32());
        Assert.Equal(old, stale.RootElement.GetProperty("oldestStageCapacityReservationAt").GetDateTime());
    }

    [Fact]
    public async Task StageRecoveryEndpointDefaultsToPreviewAndRejectsUnsafeBatchLimits()
    {
        var stages = new RecoveryStub();
        var controller = new StorageAdminController(null!, new StorageProviderRegistry([new UnusedProvider()]),
            new HealthSnapshot("healthy"), null!, null!, null!, stages, null!, null!);
        Assert.IsType<BadRequestObjectResult>((await controller.ReconcileStages(true, 0, 10)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.ReconcileStages(true, 100, 9)).Result);
        Assert.Equal(0, stages.Calls);
        var preview = Assert.IsType<OkObjectResult>((await controller.ReconcileStages()).Result);
        Assert.IsType<StorageStageRecoveryReport>(preview.Value);
        Assert.False(stages.Apply);
        Assert.Equal(100, stages.Limit);
        Assert.Equal(TimeSpan.FromMinutes(10), stages.MinimumAge);
        Assert.Equal(1, stages.Calls);
    }

    [Fact]
    public void HealthEndpointRemainsAdministratorOnly()
    {
        var authorization = Assert.Single(typeof(StorageAdminController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>());
        Assert.Equal("Admin", authorization.Policy);
    }

    private sealed class HealthSnapshot(string state) : IStorageProviderHealthTracker
    {
        public bool CanAttempt(string providerKey) => throw new NotSupportedException();
        public void Record(string providerKey, StorageOperationStatus status, string? error = null) => throw new NotSupportedException();
        public IReadOnlyList<StorageProviderHealthSnapshot> Snapshot(IEnumerable<string> providerKeys)
        {
            Assert.Equal("local", Assert.Single(providerKeys));
            return [new("local", state, state == "healthy" ? 0 : 3, null, null, null, null)];
        }
    }

    private sealed class RecoveryStub : IStorageStageCapacityService
    {
        public Task<StorageStageCapacityTicket> ConfirmPhysicalSizeAsync(StorageStageCapacityTicket ticket, long actualSizeBytes, CancellationToken ct)
            => throw new NotSupportedException("Written-size confirmation is outside this fixture scope.");

        public int Calls { get; private set; }
        public bool Apply { get; private set; }
        public int Limit { get; private set; }
        public TimeSpan MinimumAge { get; private set; }
        public Task<StorageStageRecoveryReport> ReconcilePendingAsync(bool apply, int limit,
            TimeSpan minimumAge, CancellationToken ct, string? afterKey = null)
        {
            Calls++;
            Apply = apply;
            Limit = limit;
            MinimumAge = minimumAge;
            return Task.FromResult(new StorageStageRecoveryReport(0, 0, 0, 0, false, []));
        }
        public Task<(StorageStageCapacityTicket? Ticket, string? Error)> ReserveAsync(
            string uploadIdentity, string absolutePath, long sizeBytes, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<(StorageStageCapacityTicket? Ticket, string? Error)> ReserveCompatibilityAsync(
            string uploadIdentity, string absolutePath, long sizeBytes, string rootAlias, CancellationToken ct)
            => throw new NotSupportedException();
        public Task SettlePhysicalAsync(StorageStageCapacityTicket ticket, CancellationToken ct)
            => throw new NotSupportedException();
        public Task ReleaseAsync(StorageStageCapacityTicket ticket, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class UnusedProvider : IStorageProvider
    {
        public string Key => "local";
        public StorageProviderCapabilities Capabilities { get; } = new(true, true, true, true, false, false, false, false, false);
        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
