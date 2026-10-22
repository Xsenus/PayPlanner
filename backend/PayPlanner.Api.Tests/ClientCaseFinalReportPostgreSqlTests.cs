using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ClientCaseFinalReportPostgreSqlTests
{
    private const string DatabasePrefix = "pp_case_report_";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSql_ClosedCourtCaseKeepsFinalReportOpenAfterDeadlineSync(bool upgrade)
    {
        var rawConnection = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(rawConnection))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }

        var baseBuilder = new NpgsqlConnectionStringBuilder(rawConnection);
        Assert.Contains(baseBuilder.Host, new[] { "127.0.0.1", "localhost" });
        var databaseName = DatabasePrefix + Guid.NewGuid().ToString("N");
        Assert.StartsWith(DatabasePrefix, databaseName, StringComparison.Ordinal);
        var adminBuilder = new NpgsqlConnectionStringBuilder(rawConnection) { Database = "postgres", Pooling = false };
        await using var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await adminConnection.OpenAsync();
        try
        {
            await using (var create = adminConnection.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                await create.ExecuteNonQueryAsync();
            }

            var testBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                Database = databaseName,
                Pooling = false,
            };
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(testBuilder.ConnectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            await using var db = new PostgreSqlPaymentContext(options);
            if (upgrade) await db.Database.MigrateAsync("20260925140001_AddCasePositionTemplatesPostgres");
            await db.Database.MigrateAsync();

            var role = new Role
            {
                Name = "court-final-report-manager",
                Permissions =
                [
                    new RolePermission
                    {
                        Section = RolePermissionCatalog.Cases,
                        CanView = true,
                        ViewScope = DataVisibilityScope.All,
                    },
                ],
            };
            var assignee = new User
            {
                Email = "court-final-report-pg@example.com",
                FullName = "Ответственный за отчёт",
                PasswordHash = "test",
                IsActive = true,
                IsApproved = true,
                Role = role,
            };
            var clientCase = new ClientCase
            {
                Client = new Client { Name = "Клиент", IsActive = true },
                Title = "Судебное дело",
                CourtName = "Районный суд",
                Status = ClientCaseStatus.Open,
            };
            db.Users.Add(assignee);
            db.ClientCases.Add(clientCase);
            await db.SaveChangesAsync();
            db.SystemSettings.Add(new SystemSetting
            {
                Key = ClientCaseFinalReportTaskService.AssigneeUserIdSettingKey,
                Value = assignee.Id.ToString(),
            });
            await db.SaveChangesAsync();

            Assert.Contains(clientCase.Id,
                await ClientCaseFinalReportTaskService.FilterCourtCaseIdsAsync(db, [clientCase], CancellationToken.None));
            var configuredAssignee = await ClientCaseFinalReportTaskService.ResolveAssigneeAsync(db, CancellationToken.None);
            Assert.Equal(assignee.Id, configuredAssignee?.Id);

            clientCase.Status = ClientCaseStatus.Closed;
            await ClientCaseTaskSyncService.CloseActiveTasksForClosedCaseAsync(db, clientCase.Id, assignee.Id, assignee.FullName, CancellationToken.None);
            Assert.Equal(1, await ClientCaseFinalReportTaskService.CreateForClosedCasesAsync(
                db, [clientCase.Id], configuredAssignee!, assignee.Id, assignee.FullName, CancellationToken.None));
            await db.SaveChangesAsync();
            await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, assignee.Id, assignee.FullName, CancellationToken.None);

            var task = Assert.Single(await db.TaskJournalItems.AsNoTracking().ToListAsync());
            Assert.Equal("new", task.Status);
            Assert.Equal(assignee.Id, task.AssigneeUserId);
            Assert.Contains(ClientCaseFinalReportTaskService.TaskTag, task.TagsJson);
        }
        finally
        {
            await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(adminConnection, databaseName);
            NpgsqlConnection.ClearAllPools();
        }
    }
}
