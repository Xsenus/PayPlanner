using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CancelledInvoiceBillingMigrationTests
{
    private const string SqlitePrevious = "20260924222618_ReclassifySudrfCourtSummaries";
    private const string SqliteCurrent = "20260925113000_ReleaseTasksFromCancelledInvoices";
    private const string PostgresPrevious = "20260924222802_ReclassifySudrfCourtSummariesPostgres";
    private const string PostgresCurrent = "20260925113001_ReleaseTasksFromCancelledInvoicesPostgres";

    [Fact]
    public async Task SqliteUpgrade_ReleasesOnlyTasksLinkedToCancelledInvoices()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).UsePayPlannerSqliteMigrations().Options;
        await using var context = new PaymentContext(options);
        await context.Database.MigrateAsync(SqlitePrevious);
        var ids = await SeedLinkedTasksAsync(context);

        await context.Database.MigrateAsync();

        Assert.Contains(SqliteCurrent, await context.Database.GetAppliedMigrationsAsync());
        await AssertRepairedAsync(context, ids);
    }

    [Fact]
    public async Task SqliteCleanDatabase_AppliesRepairMigration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).UsePayPlannerSqliteMigrations().Options;
        await using var context = new PaymentContext(options);

        await context.Database.MigrateAsync();

        Assert.Contains(SqliteCurrent, await context.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public void PostgreSqlMigrationScript_TargetsOnlyCancelledInvoiceLinks()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql("Host=localhost;Database=payplanner_migration_script;Username=test;Password=test")
            .Options;
        using var context = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);

        var script = context.GetService<IMigrator>().GenerateScript(PostgresPrevious, PostgresCurrent);

        Assert.Contains("INSERT INTO \"TaskJournalComments\"", script, StringComparison.Ordinal);
        Assert.Contains("UPDATE \"TaskJournalItems\" AS task", script, StringComparison.Ordinal);
        Assert.Contains("payment.\"Status\" = 'Cancelled' OR payment.\"InvoiceWorkflowStatus\" = 'Cancelled'", script, StringComparison.Ordinal);
        Assert.Contains("payment.\"DocumentKind\" = 'Invoice'", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSqlMigration_AppliesToDisposableLocalDatabase(bool upgrade)
    {
        var rawConnection = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(rawConnection)) return;
        var baseConnection = new NpgsqlConnectionStringBuilder(rawConnection);
        Assert.Contains(baseConnection.Host, new[] { "127.0.0.1", "localhost" });
        var databaseName = "pp_cancelbill_" + Guid.NewGuid().ToString("N");
        var adminConnection = new NpgsqlConnectionStringBuilder(rawConnection)
        {
            Database = "postgres",
            Pooling = false,
        };
        await using var admin = new NpgsqlConnection(adminConnection.ConnectionString);
        await admin.OpenAsync();
        try
        {
            await using (var create = admin.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                await create.ExecuteNonQueryAsync();
            }

            var testConnection = new NpgsqlConnectionStringBuilder(rawConnection)
            {
                Database = databaseName,
                Pooling = false,
            };
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(testConnection.ConnectionString).Options;
            await using var context = new PostgreSqlPaymentContext(options);
            (int CancelledTaskId, int CancelledWorkflowTaskId, int ActiveTaskId, int ActiveInvoiceId)? ids = null;
            if (upgrade)
            {
                await context.Database.MigrateAsync(PostgresPrevious);
                ids = await SeedLinkedTasksAsync(context);
            }

            await context.Database.MigrateAsync();
            Assert.Contains(PostgresCurrent, await context.Database.GetAppliedMigrationsAsync());
            if (ids.HasValue)
            {
                await AssertRepairedAsync(context, ids.Value);
            }
        }
        finally
        {
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<(int CancelledTaskId, int CancelledWorkflowTaskId, int ActiveTaskId, int ActiveInvoiceId)>
        SeedLinkedTasksAsync(PaymentContext context)
    {
        var date = new DateTime(2026, 9, 25);
        var cancelled = CreateInvoice("INV-OLD-CANCELLED", date, PaymentStatus.Cancelled);
        var cancelledWorkflow = CreateInvoice("INV-OLD-WORKFLOW", date, PaymentStatus.Pending);
        cancelledWorkflow.InvoiceWorkflowStatus = InvoiceWorkflowStatus.Cancelled;
        var active = CreateInvoice("INV-ACTIVE", date, PaymentStatus.Pending);
        context.Payments.AddRange(cancelled, cancelledWorkflow, active);
        await context.SaveChangesAsync();

        // Seed against the migrated historical schema, not the current EF model: newer
        // task columns do not exist yet at this point in an upgrade-path test.
        var tasks = new[]
        {
            (Id: 1001, InvoiceId: cancelled.Id, Title: "Работа по отменённому счёту"),
            (Id: 1002, InvoiceId: cancelledWorkflow.Id, Title: "Работа по аннулированному документу"),
            (Id: 1003, InvoiceId: active.Id, Title: "Работа по действующему счёту"),
        };
        foreach (var task in tasks)
        {
            await context.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""TaskJournalItems""
                    (""Id"", ""ObjectName"", ""Title"", ""AssigneeName"", ""AuthorName"", ""ControlMarkedByUserName"", ""DueAt"", ""Status"", ""ClientBillingClassification"", ""BillingInvoiceId"")
                VALUES ({task.Id}, {"Тестовое дело"}, {task.Title}, {"Исполнитель"}, {"Постановщик"}, {string.Empty}, {date}, {"done"}, {TaskClientBillingClassification.Billable}, {task.InvoiceId})");
        }
        context.ChangeTracker.Clear();
        return (tasks[0].Id, tasks[1].Id, tasks[2].Id, active.Id);
    }

    private static Payment CreateInvoice(string number, DateTime date, PaymentStatus status)
        => new()
        {
            Account = number,
            AccountDate = date,
            Invoice = new Invoice { Number = number, Date = date },
            Date = date,
            PlannedDate = date,
            Status = status,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
        };

    private static async Task AssertRepairedAsync(
        PaymentContext context,
        (int CancelledTaskId, int CancelledWorkflowTaskId, int ActiveTaskId, int ActiveInvoiceId) ids)
    {
        context.ChangeTracker.Clear();
        var tasks = await context.TaskJournalItems
            .AsNoTracking()
            .Include(task => task.Comments)
            .Where(task => task.Id == ids.CancelledTaskId
                || task.Id == ids.CancelledWorkflowTaskId
                || task.Id == ids.ActiveTaskId)
            .ToDictionaryAsync(task => task.Id);

        Assert.Null(tasks[ids.CancelledTaskId].BillingInvoiceId);
        Assert.Null(tasks[ids.CancelledWorkflowTaskId].BillingInvoiceId);
        Assert.Equal(ids.ActiveInvoiceId, tasks[ids.ActiveTaskId].BillingInvoiceId);
        Assert.All(new[] { ids.CancelledTaskId, ids.CancelledWorkflowTaskId }, id =>
        {
            Assert.Equal("done", tasks[id].Status);
            Assert.Contains(tasks[id].Comments, comment =>
                comment.Kind == "history" && comment.Text.Contains("снова ожидает выставления счёта"));
        });
        Assert.Empty(tasks[ids.ActiveTaskId].Comments);
    }
}
