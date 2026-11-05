using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtRecovery_BatchCapturesRecipientsOnceAndDoesNotSerializeNavigationGraph(bool postgres)
    {
        var counter = new CourtRecoveryReadCounter();
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(counter).Options)
            : await CreateContextAsync(counter);
        var seed = await SeedCourtRecoveryAsync(db);
        var clientCase = await db.ClientCases.SingleAsync(row => row.Id == seed.Base.Base.Cases[0]);
        var sources = Enumerable.Range(0, 17).Select(i => new ClientCaseCourtEvent
        { ClientCaseId = clientCase.Id, Title = "Пакет " + i, ScheduledAt = DateTime.UnixEpoch.AddDays(i) }).ToArray();
        db.AddRange(sources); await db.SaveChangesAsync();
        var queue = new CaseCourtNotificationQueue(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueBatchAsync(clientCase, 0, "",
            [new("event", "created", sources[0], null, false)], default));
        await using var transaction = await db.Database.BeginTransactionAsync();
        counter.Reads = 0;
        var first = await queue.EnqueueBatchAsync(clientCase, seed.Base.Base.User.Id, "Автор",
            [new("event", "created", sources[0], null, false)], default);
        var oneReads = counter.Reads;
        counter.Reads = 0;
        var batch = await queue.EnqueueBatchAsync(clientCase, seed.Base.Base.User.Id, "Автор",
            sources.Select(row => new CaseCourtNotificationRequest("event", "updated", row, null, true)).ToArray(), default);
        Assert.Equal(oneReads, counter.Reads);
        Assert.InRange(oneReads, 1, 4);
        Assert.Equal(17, batch.Count);
        Assert.True(batch.Min() > Assert.Single(first));
        var deduplicated = await queue.EnqueueBatchAsync(clientCase, 0, "Автор",
            [new("event", "updated", sources[0], null, false), new("event", "updated", sources[0], null, false)], default);
        Assert.Single(deduplicated);
        await transaction.CommitAsync();
        var rows = await db.CaseCourtNotificationOutboxes.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        Assert.Equal(19, rows.Length);
        Assert.Equal(rows.Length, rows.Select(row => row.EventKey).Distinct().Count());
        foreach (var row in rows)
        {
            var payload = JsonSerializer.Deserialize<CaseCourtNotificationPayload>(row.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Null(payload.CourtEvent!.ClientCase);
            Assert.Null(payload.CourtEvent.Court);
            Assert.Null(payload.CourtEvent.ProceduralDeadline);
            Assert.Empty(payload.CourtEvent.History);
            Assert.NotEmpty(payload.RecipientUserIds);
        }
        Console.WriteLine($"Court capture SELECT counts: one={oneReads},17={oneReads};rows=19");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtRecovery_InvalidLaterRequestDoesNotLeavePartialAcceptedIntent(bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtRecoveryAsync(db);
        var clientCase = await db.ClientCases.SingleAsync(row => row.Id == seed.Base.Base.Cases[0]);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var queue = new CaseCourtNotificationQueue(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueBatchAsync(clientCase, 0, "Автор",
            [new("event", "created", seed.SecondEvent, null, false), new("event", "created", seed.Base.OtherEvent, null, false)], default));
        Assert.Empty(db.ChangeTracker.Entries<CaseCourtNotificationOutbox>());
        Assert.Empty(await db.CaseCourtNotificationOutboxes.ToArrayAsync());
        await transaction.CommitAsync();
    }

    private sealed class CourtRecoveryReadCounter : DbCommandInterceptor
    {
        public int Reads { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) Reads++;
            return ValueTask.FromResult(result);
        }
    }
}
