using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Data;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class DatabaseImportServiceTests
{
    [Fact]
    public async Task ImportOwnership_DatabaseImportAlsoPreservesIdentityOnPostgreSql()
    {
        var path = Path.Combine(Path.GetTempPath(), $"payplanner-invoice-ownership-{Guid.NewGuid():N}.sqlite");
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres: true);
        try
        {
            await SeedOwnershipDatabaseAsync(path, sharedHeader: false, differentDirection: false);
            await using (var source = new SqliteConnection($"Data Source={path}"))
            {
                await source.OpenAsync();
                await using var db = CreateContext(source);
                foreach (var payment in await db.Payments.ToListAsync()) payment.Notes = $"UID 1С: pg-owned-{payment.Id}";
                await db.SaveChangesAsync();
            }
            var connection = fixture.Db.Database.GetConnectionString()!;
            var factory = new InvoiceImportPostgresFactory(connection);
            var service = new DatabaseImportService(factory, new DatabaseProviderInfo(DatabaseProviderKind.PostgreSql, connection),
                NullLogger<DatabaseImportService>.Instance);
            Assert.True((await service.ImportSqliteAsync(path)).Success);
            Assert.True((await service.ImportSqliteAsync(path)).Success);
            await using var verify = factory.CreateDbContext();
            Assert.Equal(2, await verify.Payments.CountAsync());
            Assert.Equal(2, await verify.Invoices.CountAsync());
            var act = await verify.Acts.SingleAsync();
            Assert.Equal(act.ClientId, (await verify.Payments.SingleAsync(payment => payment.Id == act.InvoiceId)).ClientId);
        }
        finally { TryDeleteFile(path); }
    }

    private sealed class InvoiceImportPostgresFactory(string connection) : IDbContextFactory<PaymentContext>
    {
        public PaymentContext CreateDbContext() => new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(connection).Options, new HttpContextAccessor());
        public Task<PaymentContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    [Fact]
    public async Task ImportOwnership_DatabaseKnownUidWithChangedNumberRequiresReviewInsteadOfCreatingDuplicate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"payplanner-invoice-ownership-{Guid.NewGuid():N}.sqlite");
        await using var target = await CreateOpenConnectionAsync();
        try
        {
            await SeedOwnershipDatabaseAsync(path, sharedHeader: false, differentDirection: false);
            await using (var source = new SqliteConnection($"Data Source={path}"))
            {
                await source.OpenAsync();
                await using var db = CreateContext(source);
                var payment = await db.Payments.OrderBy(payment => payment.Id).FirstAsync();
                payment.Notes = "UID 1С: database-stable-uid";
                await db.SaveChangesAsync();
            }
            await using (var db = CreateContext(target)) await db.Database.EnsureCreatedAsync();
            var service = CreateService(target);
            Assert.True((await service.ImportSqliteAsync(path)).Success);
            await using (var source = new SqliteConnection($"Data Source={path}"))
            {
                await source.OpenAsync();
                await using var db = CreateContext(source);
                var payment = await db.Payments.Include(payment => payment.Invoice).OrderBy(payment => payment.Id).FirstAsync();
                payment.Account = "CHANGED-NUMBER";
                payment.Invoice!.Number = payment.Account;
                await db.SaveChangesAsync();
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportSqliteAsync(path));
            await using var verify = CreateContext(target);
            Assert.Equal(2, await verify.Payments.CountAsync());
            Assert.Equal(2, await verify.Invoices.CountAsync());
            Assert.DoesNotContain(await verify.Invoices.ToListAsync(), invoice => invoice.Number == "CHANGED-NUMBER");
        }
        finally { TryDeleteFile(path); }
    }

    [Fact]
    public async Task ImportOwnership_DatabaseOldCopyCannotReplaceChangedFinancialState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"payplanner-invoice-ownership-{Guid.NewGuid():N}.sqlite");
        await using var target = await CreateOpenConnectionAsync();
        try
        {
            await SeedOwnershipDatabaseAsync(path, sharedHeader: false, differentDirection: false);
            await using (var db = CreateContext(target)) await db.Database.EnsureCreatedAsync();
            var service = CreateService(target);
            Assert.True((await service.ImportSqliteAsync(path)).Success);
            await using (var db = CreateContext(target))
            {
                var changed = await db.Payments.OrderBy(payment => payment.Id).FirstAsync();
                changed.PaidAmount = 50;
                changed.Status = PaymentStatus.Processing;
                await db.SaveChangesAsync();
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportSqliteAsync(path));
            await using var verify = CreateContext(target);
            var payment = await verify.Payments.OrderBy(item => item.Id).FirstAsync();
            Assert.Equal(50m, payment.PaidAmount);
            Assert.Equal(PaymentStatus.Processing, payment.Status);
            Assert.Equal(100m, payment.Amount);
            Assert.Equal(2, await verify.Payments.CountAsync());
            Assert.Equal(2, await verify.Invoices.CountAsync());
        }
        finally { TryDeleteFile(path); }
    }

    [Fact]
    public async Task ImportOwnership_DatabaseLateWriteFailureRollsBackAllEarlierTables()
    {
        var path = Path.Combine(Path.GetTempPath(), $"payplanner-invoice-ownership-{Guid.NewGuid():N}.sqlite");
        await using var target = await CreateOpenConnectionAsync();
        try
        {
            await SeedOwnershipDatabaseAsync(path, sharedHeader: false, differentDirection: false);
            await using (var db = CreateContext(target)) await db.Database.EnsureCreatedAsync();
            var options = new DbContextOptionsBuilder<PayPlanner.Api.Data.PaymentContext>().UseSqlite(target)
                .AddInterceptors(new FailInvoicePaymentInsert()).Options;
            var service = new DatabaseImportService(new TestPaymentContextFactory(options, new HttpContextAccessor()),
                new DatabaseProviderInfo(DatabaseProviderKind.Sqlite, target.ConnectionString), NullLogger<DatabaseImportService>.Instance);
            var result = await service.ImportSqliteAsync(path);
            Assert.False(result.Success);
            Assert.Contains(result.Tables, table => table.Errors > 0);
            Assert.All(result.Tables, table => { Assert.Equal(0, table.Inserted); Assert.Equal(0, table.Updated); });
            await using var verify = CreateContext(target);
            Assert.Empty(await verify.Clients.ToListAsync());
            Assert.Empty(await verify.Invoices.ToListAsync());
            Assert.Empty(await verify.Payments.ToListAsync());
            Assert.Empty(await verify.Acts.ToListAsync());
            Assert.Empty(await verify.Roles.ToListAsync());
            Assert.Empty(await verify.SystemSettings.ToListAsync());
        }
        finally { TryDeleteFile(path); }
    }

    private sealed class FailInvoicePaymentInsert : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO \"Payments\"", StringComparison.Ordinal))
                throw new InvalidOperationException("Synthetic payment write failure");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportOwnership_DatabasePreservesIndependentHeadersAndActLinksOnRepeat(bool differentDirection)
    {
        var path = Path.Combine(Path.GetTempPath(), $"payplanner-invoice-ownership-{Guid.NewGuid():N}.sqlite");
        await using var target = await CreateOpenConnectionAsync();
        try
        {
            await SeedOwnershipDatabaseAsync(path, sharedHeader: false, differentDirection);
            await using (var db = CreateContext(target))
            {
                await db.Database.EnsureCreatedAsync();
                // Shift target IDs independently of source IDs.
                db.Payments.Add(CreatePayment(new DateTime(2026, 1, 1), 1, "Unrelated target"));
                await db.SaveChangesAsync();
            }
            var service = CreateService(target);
            Assert.True((await service.ImportSqliteAsync(path)).Success);
            Assert.True((await service.ImportSqliteAsync(path)).Success);
            await using var verify = CreateContext(target);
            var docs = await verify.Payments.Where(p => p.DocumentKind == DocumentKind.Invoice).OrderBy(p => p.Id).ToListAsync();
            Assert.Equal(2, docs.Count);
            Assert.NotEqual(docs[0].InvoiceId, docs[1].InvoiceId);
            Assert.Equal(2, await verify.Invoices.CountAsync());
            Assert.Equal(new[] { 100m, 200m }, docs.Select(p => p.Amount));
            var act = await verify.Acts.SingleAsync();
            Assert.Equal(docs[1].Id, act.InvoiceId);
            Assert.Equal(docs[1].ClientId, act.ClientId);
        }
        finally { TryDeleteFile(path); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportOwnership_DatabaseRejectsConflictOrAmbiguousIdentityBeforeAnyTargetWrites(bool sharedHeader)
    {
        var path = Path.Combine(Path.GetTempPath(), $"payplanner-invoice-ownership-{Guid.NewGuid():N}.sqlite");
        await using var target = await CreateOpenConnectionAsync();
        try
        {
            await SeedOwnershipDatabaseAsync(path, sharedHeader, differentDirection: false, sameOwner: !sharedHeader);
            await using (var db = CreateContext(target)) await db.Database.EnsureCreatedAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(target).ImportSqliteAsync(path));
            await using var verify = CreateContext(target);
            Assert.Empty(await verify.Clients.ToListAsync());
            Assert.Empty(await verify.Payments.ToListAsync());
            Assert.Empty(await verify.Invoices.ToListAsync());
            Assert.Empty(await verify.Roles.ToListAsync());
            Assert.Empty(await verify.SystemSettings.ToListAsync());
        }
        finally { TryDeleteFile(path); }
    }

    private static async Task SeedOwnershipDatabaseAsync(string path, bool sharedHeader, bool differentDirection, bool sameOwner = false)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var firstClient = new Client { Name = "First import owner" };
        var secondClient = sameOwner || differentDirection ? firstClient : new Client { Name = "Second import owner" };
        var firstHeader = new Invoice { Number = "SAME-DB", Date = new DateTime(2026, 6, 1) };
        var secondHeader = sharedHeader ? firstHeader : new Invoice { Number = firstHeader.Number, Date = firstHeader.Date };
        var first = CreateInvoicePayment(firstHeader, firstClient, null, "First import document");
        var second = CreateInvoicePayment(secondHeader, secondClient, null, "Second import document");
        first.Amount = 100;
        second.Amount = 200;
        if (differentDirection) second.Type = PaymentType.Expense;
        db.Payments.AddRange(first, second);
        db.Roles.Add(new Role { Name = "Source-only role", Description = "Must not be inserted on refusal" });
        db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
        await db.SaveChangesAsync();
        db.Acts.Add(new Act
        {
            Number = "SECOND-ACT",
            InvoiceId = second.Id,
            ClientId = second.ClientId,
            Date = firstHeader.Date,
            Amount = 200
        });
        await db.SaveChangesAsync();
    }
}
