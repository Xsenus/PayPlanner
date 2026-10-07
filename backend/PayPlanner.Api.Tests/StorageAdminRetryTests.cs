using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageAdminRetryTests
{
    [Theory]
    [InlineData(StorageOperationStatuses.Pending)]
    [InlineData(StorageOperationStatuses.Failed)]
    [InlineData(StorageOperationStatuses.Unknown)]
    [InlineData(StorageOperationStatuses.DeadLetter)]
    public async Task RetryResetsOnlyRetryableOperation(string status)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = NewContext(connection);
        await db.Database.EnsureCreatedAsync();
        var operation = await SeedAsync(db, status);
        var controller = NewController(db);

        var result = await controller.RetryOperation(operation.Id, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        db.ChangeTracker.Clear();
        var saved = await db.StorageOperations.AsNoTracking().SingleAsync(item => item.Id == operation.Id);
        Assert.Equal(StorageOperationStatuses.Pending, saved.Status);
        Assert.Equal(0, saved.AttemptCount);
        Assert.Null(saved.LeaseOwner);
        Assert.Null(saved.LeaseExpiresAt);
        Assert.Null(saved.LastError);
        Assert.Null(saved.CompletedAt);
        Assert.True(saved.NextAttemptAt.HasValue);
    }

    [Theory]
    [InlineData(StorageOperationStatuses.Running)]
    [InlineData(StorageOperationStatuses.Succeeded)]
    public async Task RetryDoesNotReopenRunningOrCompletedOperation(string status)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = NewContext(connection);
        await db.Database.EnsureCreatedAsync();
        var operation = await SeedAsync(db, status);

        Assert.IsType<ConflictObjectResult>(await NewController(db).RetryOperation(operation.Id, CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.Equal(status, (await db.StorageOperations.AsNoTracking().SingleAsync(item => item.Id == operation.Id)).Status);
        Assert.IsType<NotFoundResult>(await NewController(db).RetryOperation(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task WorkerClaimBetweenRetryReadAndUpdateCannotBeReset()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var claim = new ClaimBeforeRetryUpdate();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).AddInterceptors(claim).Options);
        await db.Database.EnsureCreatedAsync();
        var operation = await SeedAsync(db, StorageOperationStatuses.Failed);
        claim.Enabled = true;

        Assert.IsType<ConflictObjectResult>(await NewController(db).RetryOperation(operation.Id, CancellationToken.None));

        Assert.True(claim.Claimed);
        db.ChangeTracker.Clear();
        var saved = await db.StorageOperations.AsNoTracking().SingleAsync(item => item.Id == operation.Id);
        Assert.Equal(StorageOperationStatuses.Running, saved.Status);
        Assert.Equal("competing-worker", saved.LeaseOwner);
        Assert.True(saved.LeaseExpiresAt > DateTime.UtcNow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryRespectsAnUnsettledLeaseEvenWhenStatusIsFailed(bool expired)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = NewContext(connection);
        await db.Database.EnsureCreatedAsync();
        var operation = await SeedAsync(db, StorageOperationStatuses.Failed);
        operation.LeaseOwner = "worker-with-stale-status";
        operation.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(expired ? -2 : 2);
        db.StorageOperations.Update(operation);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await NewController(db).RetryOperation(operation.Id, CancellationToken.None);

        if (expired) Assert.IsType<AcceptedResult>(result);
        else Assert.IsType<ConflictObjectResult>(result);
        var saved = await db.StorageOperations.AsNoTracking().SingleAsync(item => item.Id == operation.Id);
        Assert.Equal(expired ? StorageOperationStatuses.Pending : StorageOperationStatuses.Failed, saved.Status);
        Assert.Equal(expired ? null : "worker-with-stale-status", saved.LeaseOwner);
    }

    [Fact]
    public async Task PostgreSqlRetryKeepsActiveLeaseAndResetsFailedOperation()
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        var name = "pp_admin_retry_" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            { await create.ExecuteNonQueryAsync(); created = true; }
            settings.Database = name;
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(settings.ConnectionString).Options);
            await db.Database.MigrateAsync();
            var failed = await SeedAsync(db, StorageOperationStatuses.Failed);
            var running = await SeedAsync(db, StorageOperationStatuses.Running);
            running.LeaseOwner = "live-worker";
            running.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(2);
            db.StorageOperations.Update(running);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            Assert.IsType<AcceptedResult>(await NewController(db).RetryOperation(failed.Id, CancellationToken.None));
            Assert.IsType<ConflictObjectResult>(await NewController(db).RetryOperation(running.Id, CancellationToken.None));

            db.ChangeTracker.Clear();
            Assert.Equal(StorageOperationStatuses.Pending,
                (await db.StorageOperations.AsNoTracking().SingleAsync(item => item.Id == failed.Id)).Status);
            var preserved = await db.StorageOperations.AsNoTracking().SingleAsync(item => item.Id == running.Id);
            Assert.Equal(StorageOperationStatuses.Running, preserved.Status);
            Assert.Equal("live-worker", preserved.LeaseOwner);
        }
        finally
        {
            Assert.Matches("^pp_admin_retry_[a-f0-9]{32}$", name);
            if (created)
            {
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private static PaymentContext NewContext(SqliteConnection connection)
        => new(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);

    private static StorageAdminController NewController(PaymentContext db)
        => new(null!, null!, null!, null!, null!, null!, null!, db, null!);

    private static async Task<StorageOperation> SeedAsync(PaymentContext db, string status)
    {
        var storageObject = new StorageObject { DataClass = "task-file", OwnerType = "task-file", OwnerId = "1" };
        var operation = new StorageOperation
        {
            StorageObject = storageObject,
            OperationType = StorageOperationTypes.Replicate,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            Status = status,
            AttemptCount = 4,
            LastError = "previous failure",
            CompletedAt = DateTime.UtcNow.AddMinutes(-1)
        };
        db.StorageOperations.Add(operation);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return operation;
    }

    private sealed class ClaimBeforeRetryUpdate : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public bool Claimed { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await ClaimAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await ClaimAsync(command, cancellationToken);
            return result;
        }

        private async Task ClaimAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (!Enabled || Claimed || !command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                || !command.CommandText.Contains("StorageOperations", StringComparison.Ordinal)) return;
            Claimed = true;
            await using var competing = command.Connection!.CreateCommand();
            competing.Transaction = command.Transaction;
            competing.CommandText = "UPDATE \"StorageOperations\" SET \"Status\" = 'running', \"LeaseOwner\" = 'competing-worker', \"LeaseExpiresAt\" = $expires WHERE \"Status\" = 'failed'";
            var expires = competing.CreateParameter();
            expires.ParameterName = "$expires";
            expires.Value = DateTime.UtcNow.AddMinutes(2);
            competing.Parameters.Add(expires);
            Assert.Equal(1, await competing.ExecuteNonQueryAsync(cancellationToken));
        }
    }
}
