using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ClientCaseFinalReportMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteMigration_AddsSystemOwnedTaskMarkerOnCleanAndUpgradedDatabase(bool upgrade)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).UsePayPlannerSqliteMigrations().Options;
        await using var db = new PaymentContext(options);
        if (upgrade) await db.Database.MigrateAsync("20260925140000_AddCasePositionTemplates");
        await db.Database.MigrateAsync();

        Assert.Contains("20260925150000_AddCaseFinalReportMarker", await db.Database.GetAppliedMigrationsAsync());
        var role = new Role { Name = "final-report-migration-role" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        var user = new User
        {
            Email = "final-report-migration@example.com",
            FullName = "Тестовый сотрудник",
            PasswordHash = "test",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var task = new TaskJournalItem
        {
            ObjectName = "Тестовое дело",
            Title = "Отчёт",
            AssigneeUserId = user.Id,
            AssigneeName = user.FullName,
            AuthorUserId = user.Id,
            AuthorName = user.FullName,
            DueAt = DateTime.UtcNow.AddDays(10),
            IsCaseFinalReport = true,
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        Assert.True((await db.TaskJournalItems.AsNoTracking().SingleAsync()).IsCaseFinalReport);
    }

    [Fact]
    public void PostgreSqlMigrationScript_AddsTaskMarkerAndIndex()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql("Host=localhost;Database=payplanner_migration_script;Username=test;Password=test").Options;
        using var db = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
        var script = db.GetService<IMigrator>().GenerateScript(
            "20260925140001_AddCasePositionTemplatesPostgres",
            "20260925150001_AddCaseFinalReportMarkerPostgres");
        Assert.Contains("IsCaseFinalReport", script, StringComparison.Ordinal);
        Assert.Contains("IX_TaskJournalItems_ClientCaseId_IsCaseFinalReport", script, StringComparison.Ordinal);
    }
}
