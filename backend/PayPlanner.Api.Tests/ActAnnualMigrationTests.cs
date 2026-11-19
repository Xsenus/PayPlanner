using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Invoices;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ActAnnualMigrationTests(PostgreSqlMigratedTemplate postgresTemplate, SqliteMigratedTemplate sqliteTemplate)
    : IClassFixture<PostgreSqlMigratedTemplate>, IClassFixture<SqliteMigratedTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_MetadataMigrationSupportsCleanUpgradeAndRoundTripWithHistoricalDuplicates(bool postgres)
    {
        if (postgres) Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES")));
        await using PostgreSqlMigratedTemplate.DatabaseLease? pgLease = postgres ? await postgresTemplate.CreateDatabaseAsync("pp_act_upgrade_") : null;
        await using SqliteMigratedTemplate.DatabaseLease? sqliteLease = postgres ? null : await sqliteTemplate.CreateDatabaseAsync();
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(pgLease!.ConnectionString).Options)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(sqliteLease!.Connection).UsePayPlannerSqliteMigrations().Options);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.ActAnnualNumberCounters.ToArrayAsync());
        Assert.Empty(await db.ActCreateReceipts.ToArrayAsync());
        var migrations = db.Database.GetMigrations().ToArray();
        var annual = Assert.Single(migrations, name => name.Contains("AddActAnnualNumbering", StringComparison.Ordinal));
        var previous = migrations.TakeWhile(name => name != annual).Last();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);
        var date = new DateTime(2048, 1, 2);
        db.Acts.AddRange(new Act { Number = "7", Date = date }, new Act { Number = "7", Date = date }, new Act { Number = "0007", Date = date });
        await db.SaveChangesAsync();
        var original = await db.Acts.AsNoTracking().OrderBy(item => item.Id).Select(item => new { item.Id, item.Number, item.Date }).ToArrayAsync();
        await migrator.MigrateAsync();

        Assert.Equal(original, await db.Acts.AsNoTracking().OrderBy(item => item.Id).Select(item => new { item.Id, item.Number, item.Date }).ToArrayAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        var counter = db.Model.FindEntityType(typeof(ActAnnualNumberCounter))!;
        Assert.Equal(new[] { nameof(ActAnnualNumberCounter.Year) }, counter.FindPrimaryKey()!.Properties.Select(item => item.Name));
        var receipt = db.Model.FindEntityType(typeof(ActCreateReceipt))!;
        Assert.Equal(new[] { nameof(ActCreateReceipt.ActorUserId), nameof(ActCreateReceipt.UploadKey) }, receipt.FindPrimaryKey()!.Properties.Select(item => item.Name));
        Assert.DoesNotContain(receipt.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(Act));
        Assert.All(db.Model.FindEntityType(typeof(Act))!.GetIndexes().Where(index => index.Properties.Any(property => property.Name == nameof(Act.Number))), index => Assert.False(index.IsUnique));

        await migrator.MigrateAsync(previous);
        await migrator.MigrateAsync();
        Assert.Equal(original, await db.Acts.AsNoTracking().OrderBy(item => item.Id).Select(item => new { item.Id, item.Number, item.Date }).ToArrayAsync());
        Assert.Empty(await db.ActAnnualNumberCounters.ToArrayAsync());
        Assert.Empty(await db.ActCreateReceipts.ToArrayAsync());
        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.Equal("8", await new ActAnnualNumberingService(db).NextAsync(date, default));
        await transaction.CommitAsync();
    }
}
