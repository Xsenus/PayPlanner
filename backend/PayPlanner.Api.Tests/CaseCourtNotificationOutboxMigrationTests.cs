using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CaseCourtNotificationOutboxMigrationTests(PostgreSqlMigratedTemplate template) : IClassFixture<PostgreSqlMigratedTemplate>
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CleanAndUpgradePreserveHistoricalGraphAndEnforceQueueIdentity(bool postgres, bool upgrade)
    {
        await using var database = postgres ? await template.CreateDatabaseAsync("pp_court_notice_migration_") : null;
        await using var sqlite = postgres ? null : new SqliteConnection("Data Source=:memory:");
        if (sqlite is not null) await sqlite.OpenAsync();
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(sqlite!).UsePayPlannerSqliteMigrations().Options);
        var previous = postgres ? "20261105040010_AddCourtDocumentAcceptedControlsPostgreSql" : "20261105040000_AddCourtDocumentAcceptedControls";
        if (upgrade) await db.Database.MigrateAsync(previous);
        else await db.Database.MigrateAsync();
        var clientCase = new ClientCase { Title = "Сохранённое дело", Client = new Client { Name = "Синтетический клиент" } };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var deadline = new ClientCaseProceduralDeadline { ClientCaseId = clientCase.Id, Title = "Прежний срок", DueAt = DateTime.UnixEpoch, Notes = "Не менять" };
        var courtEvent = new ClientCaseCourtEvent { ClientCaseId = clientCase.Id, Title = "Прежнее заседание", ScheduledAt = DateTime.UnixEpoch, ResultSummary = "Прежний результат" };
        db.AddRange(deadline, courtEvent);
        await db.SaveChangesAsync();
        var historical = JsonSerializer.Serialize(new
        {
            deadline = await db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync(),
            courtEvent = await db.ClientCaseCourtEvents.AsNoTracking().SingleAsync()
        });
        db.ChangeTracker.Clear();
        if (upgrade) await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.CaseCourtNotificationOutboxes.ToArrayAsync());
        Assert.Equal(historical, JsonSerializer.Serialize(new
        {
            deadline = await db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync(),
            courtEvent = await db.ClientCaseCourtEvents.AsNoTracking().SingleAsync()
        }));
        var first = Intent(clientCase.Id, "first");
        var second = Intent(clientCase.Id, "second");
        db.AddRange(first, second);
        await db.SaveChangesAsync();
        Assert.True(first.Id > 0);
        Assert.True(second.Id > first.Id);
        var snapshot = JsonSerializer.Serialize(await db.CaseCourtNotificationOutboxes.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync());
        if (postgres)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync(previous));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        else
        {
            var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync(previous));
            Assert.Equal(19, error.SqliteErrorCode);
        }
        Assert.Equal(snapshot, JsonSerializer.Serialize(await db.CaseCourtNotificationOutboxes.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()));
        Assert.False(db.Database.HasPendingModelChanges());
        db.ChangeTracker.Clear();
        db.Add(Intent(clientCase.Id, "first"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        await db.CaseCourtNotificationOutboxes.ExecuteDeleteAsync();
        await db.Database.MigrateAsync(previous);
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.CaseCourtNotificationOutboxes.ToArrayAsync());
        Assert.Equal("Не менять", (await db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).Notes);
        Assert.Equal("Прежний результат", (await db.ClientCaseCourtEvents.AsNoTracking().SingleAsync()).ResultSummary);
    }

    private static CaseCourtNotificationOutbox Intent(int caseId, string key) => new()
    {
        ClientCaseId = caseId,
        Kind = "journal",
        EventKey = "migration-" + key,
        PayloadJson = "{}",
        CreatedAt = DateTime.UnixEpoch,
        NextAttemptAt = DateTime.UnixEpoch,
        JournalChanged = true
    };
}
