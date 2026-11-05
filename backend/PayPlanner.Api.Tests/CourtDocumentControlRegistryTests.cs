using System.Data.Common;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class CourtDocumentSourceRegistryTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 17)]
    [InlineData(true, 17)]
    public async Task AcceptedControlsUseOneBatchedRead(bool postgres, int count)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var inputs = Enumerable.Range(0, count).Select(index => Input() with
        {
            DocumentKey = "sudrf-act-" + Guid.NewGuid().ToString("N"),
            SourceUrl = "https://owned-court.test/event/" + index,
        }).ToArray();
        var deadlines = inputs.Select(input => new ClientCaseProceduralDeadline
        {
            ClientCaseId = f.CaseId,
            Title = "Принятый контроль",
            DeadlineType = "appeal",
            DueAt = CheckedAt.AddDays(10),
            SourceDocumentKey = input.DocumentKey,
            SourceType = "sudrf",
            SourceUrl = input.SourceUrl,
        }).ToArray();
        f.Db.ClientCaseProceduralDeadlines.AddRange(deadlines);
        await f.Db.SaveChangesAsync();
        var counter = new CourtControlReadCounter();
        await using var capture = f.NewContext(counter);
        var accepted = await new CourtDocumentSourceRegistry(capture).RecordAcceptedBatchAsync(f.CaseId, f.ActorId,
            "Первый автор", Binding, CheckedAt, inputs, default, deadlines.Select(CourtDocumentControlState.Capture).ToArray());
        Assert.Equal("recorded", accepted.Status);
        Assert.Equal(count, accepted.Sources.Count);
        Assert.Equal(1, counter.Reads);
        Assert.Equal(postgres ? 1 : 0, counter.Pins);
        Assert.Equal(count, await f.Db.CourtDocumentSources.CountAsync(row => row.AcceptedControlsJson != null));
    }

    [Fact]
    public async Task AcceptedControlsRejectBusyPostgresRowAndRetryAfterRollback()
    {
        await using var f = await CreateFixtureAsync(true);
        var input = Input();
        var deadline = new ClientCaseProceduralDeadline
        {
            ClientCaseId = f.CaseId,
            Title = "Занятый контроль",
            DeadlineType = "appeal",
            DueAt = CheckedAt.AddDays(10),
            SourceDocumentKey = input.DocumentKey,
            SourceType = "sudrf",
            SourceUrl = input.SourceUrl,
        };
        f.Db.ClientCaseProceduralDeadlines.Add(deadline);
        await f.Db.SaveChangesAsync();
        var expected = CourtDocumentControlState.Capture(deadline);
        await using var blocker = f.NewContext();
        await blocker.Database.BeginTransactionAsync();
        await blocker.ClientCaseProceduralDeadlines.Where(row => row.Id == deadline.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Notes, "Временная блокировка"));
        await using var capture = f.NewContext();
        var registry = new CourtDocumentSourceRegistry(capture);
        var error = await Assert.ThrowsAsync<PostgresException>(() => registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId,
            "Первый автор", Binding, CheckedAt, [input], default, [expected]));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
        Assert.Empty(await f.Db.CourtDocumentSourceScopes.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.CourtDocumentSourceAttempts.AsNoTracking().ToArrayAsync());
        Assert.Null(capture.Database.CurrentTransaction);
        Assert.False(capture.ChangeTracker.HasChanges());
        await blocker.Database.RollbackTransactionAsync();
        Assert.Equal("recorded", (await registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId,
            "Первый автор", Binding, CheckedAt, [input], default, [expected])).Status);
        Assert.NotNull((await f.Db.CourtDocumentSources.AsNoTracking().SingleAsync()).AcceptedControlsJson);
        Assert.Equal("", (await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).Notes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlProofNeedsNewAcceptedObservationAndPreservesFrozenAttempt(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var input = Input();
        var deadline = new ClientCaseProceduralDeadline
        {
            ClientCaseId = f.CaseId,
            Title = "Контроль принятой сводки",
            DeadlineType = "appeal",
            DueAt = CheckedAt.AddDays(10),
            SourceDocumentKey = input.DocumentKey,
            SourceType = "sudrf",
            SourceUrl = input.SourceUrl,
        };
        f.Db.ClientCaseProceduralDeadlines.Add(deadline);
        await f.Db.SaveChangesAsync();
        var expected = CourtDocumentControlState.Capture(deadline);
        var registry = new CourtDocumentSourceRegistry(f.Db);
        var first = Assert.Single((await registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId, "Первый автор",
            Binding, CheckedAt, [input], default, [expected])).Sources);
        var originalBytes = await registry.ReadContentAsync(Publication(first), default);
        Assert.NotNull((await f.Db.CourtDocumentSources.AsNoTracking().SingleAsync()).AcceptedControlsJson);
        await f.Db.CourtDocumentSources.ExecuteUpdateAsync(set => set.SetProperty(row => row.AcceptedControlsJson, (string?)null));
        Assert.Equal(first, Assert.Single((await registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId, "Первый автор",
            Binding, CheckedAt, [input], default, [expected])).Sources));
        Assert.Null((await f.Db.CourtDocumentSources.AsNoTracking().SingleAsync()).AcceptedControlsJson);
        Assert.Equal(first, Assert.Single((await registry.RecordAcceptedBatchAsync(f.CaseId, f.OtherActorId, "Второй автор",
            Binding, CheckedAt.AddSeconds(1), [input], default, [expected])).Sources));
        var source = await f.Db.CourtDocumentSources.AsNoTracking().SingleAsync();
        var proof = JsonNode.Parse(source.AcceptedControlsJson!)!;
        Assert.Equal(2, proof["scopeVersion"]!.GetValue<long>());
        Assert.Equal(f.OtherActorId, proof["actorId"]!.GetValue<int>());
        Assert.Equal(first.Snapshot.AttemptId, proof["attemptId"]!.GetValue<Guid>());
        Assert.Equal(first.Generation.FrozenDomainJson, (await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync()).FrozenSnapshotJson);
        Assert.Equal(originalBytes, await registry.ReadContentAsync(Publication(first), default));
        await f.Db.ClientCaseProceduralDeadlines.ExecuteUpdateAsync(set => set.SetProperty(row => row.SourceUrl, "https://owned-court.test/manual"));
        var rejected = await registry.RecordAcceptedBatchAsync(f.CaseId, f.ActorId, "Первый автор",
            Binding, CheckedAt.AddSeconds(2), [input], default, [expected]);
        Assert.Equal("conflict", rejected.Status);
        Assert.Equal(source.AcceptedControlsJson, (await f.Db.CourtDocumentSources.AsNoTracking().SingleAsync()).AcceptedControlsJson);
        Assert.Equal(2, (await f.Db.CourtDocumentSourceScopes.AsNoTracking().SingleAsync()).Version);
        Assert.Equal("https://owned-court.test/manual", (await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).SourceUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedControlMigrationPreservesExistingAttemptAndLeavesLegacyProofUnknown(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, migrateFromEmpty: true);
        Assert.False(f.Db.Database.HasPendingModelChanges());
        var receipt = Assert.Single((await new CourtDocumentSourceRegistry(f.Db).RecordAcceptedBatchAsync(f.CaseId,
            f.ActorId, "Первый автор", Binding, CheckedAt, [Input()], default)).Sources);
        var original = await f.Db.CourtDocumentSourceContents.AsNoTracking().SingleAsync();
        var scope = await f.Db.CourtDocumentSourceScopes.AsNoTracking().SingleAsync();
        var source = await f.Db.CourtDocumentSources.AsNoTracking().SingleAsync();
        var attempt = await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync();
        var previous = postgres ? "20261101000834_IndexCompanyBankBindingHistoryPostgreSql" : "20261101000824_IndexCompanyBankBindingHistory";
        f.Db.ChangeTracker.Clear();
        await f.Db.CourtDocumentSourceScopes.ExecuteDeleteAsync();
        await f.Db.Database.MigrateAsync(previous);
        await using (var restore = await f.Db.Database.BeginTransactionAsync())
        {
            object acceptedAt = postgres
                ? new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Timestamp, Value = scope.LastAcceptedAt }
                : scope.LastAcceptedAt;
            await f.Db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "CourtDocumentSourceScopes"
                    ("Id", "ClientCaseId", "BindingUrl", "BindingSha256", "Version", "InputSha256", "LastAcceptedAt", "LastActorId", "LastActorName")
                VALUES ({scope.Id}, {scope.ClientCaseId}, {scope.BindingUrl}, {scope.BindingSha256}, {scope.Version},
                    {scope.InputSha256}, {acceptedAt}, {scope.LastActorId}, {scope.LastActorName})
                """);
            await f.Db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "CourtDocumentSources"
                    ("Id", "ScopeId", "ClientCaseId", "DocumentKey", "AcceptedScopeVersion", "IsActive", "CurrentAttemptVersion")
                VALUES ({source.Id}, {source.ScopeId}, {source.ClientCaseId}, {source.DocumentKey},
                    {source.AcceptedScopeVersion}, {source.IsActive}, {source.CurrentAttemptVersion})
                """);
            await f.Db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "CourtDocumentSourceAttempts"
                    ("Id", "SourceId", "Version", "ActorId", "ActorName", "FrozenSnapshotJson", "FrozenSnapshotSha256", "RequestSha256")
                VALUES ({attempt.Id}, {attempt.SourceId}, {attempt.Version}, {attempt.ActorId}, {attempt.ActorName},
                    {attempt.FrozenSnapshotJson}, {attempt.FrozenSnapshotSha256}, {attempt.RequestSha256})
                """);
            await f.Db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "CourtDocumentSourceContents" ("AttemptId", "Bytes", "SizeBytes", "Sha256")
                VALUES ({original.AttemptId}, {original.Bytes}, {original.SizeBytes}, {original.Sha256})
                """);
            await restore.CommitAsync();
        }
        Assert.Equal(1, await f.Db.CourtDocumentSourceAttempts.CountAsync());
        await f.Db.Database.MigrateAsync();
        Assert.False(f.Db.Database.HasPendingModelChanges());
        Assert.Empty(await f.Db.Database.GetPendingMigrationsAsync());
        Assert.Null((await f.Db.CourtDocumentSources.AsNoTracking().SingleAsync()).AcceptedControlsJson);
        Assert.Equal(receipt.Generation.FrozenDomainJson, (await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync()).FrozenSnapshotJson);
        Assert.Equal(original.Bytes, (await f.Db.CourtDocumentSourceContents.AsNoTracking().SingleAsync()).Bytes);
        Assert.True(await new CourtDocumentSourceRegistry(f.Db).CanPublishAsync(Publication(receipt), default));
        if (postgres)
        {
            Assert.Equal(scope.LastAcceptedAt, (await f.Db.CourtDocumentSourceScopes.AsNoTracking().SingleAsync()).LastAcceptedAt);
            var error = await Assert.ThrowsAsync<PostgresException>(() => f.Db.Database.MigrateAsync(previous));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        else
        {
            var error = await Assert.ThrowsAsync<SqliteException>(() => f.Db.Database.MigrateAsync(previous));
            Assert.Equal(19, error.SqliteErrorCode);
        }
        Assert.Null((await f.Db.CourtDocumentSources.AsNoTracking().SingleAsync()).AcceptedControlsJson);
        Assert.Equal(original.Bytes, (await f.Db.CourtDocumentSourceContents.AsNoTracking().SingleAsync()).Bytes);
        Assert.True(await new CourtDocumentSourceRegistry(f.Db).CanPublishAsync(Publication(receipt), default));
    }

    private sealed class CourtControlReadCounter : DbCommandInterceptor
    {
        public int Reads { get; private set; }
        public int Pins { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("ClientCaseProceduralDeadlines", StringComparison.Ordinal)) Reads++;
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("ClientCaseProceduralDeadlines", StringComparison.Ordinal)) Pins++;
            return ValueTask.FromResult(result);
        }
    }
}
