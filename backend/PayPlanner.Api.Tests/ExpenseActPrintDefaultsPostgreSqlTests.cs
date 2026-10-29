using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ExpenseActPrintDefaultsPostgreSqlTests
{
    [Fact]
    public async Task PostgreSql_ActCaptureSerializesLinkedSideAndDoesNotReclassifySavedSnapshot()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Required isolated PostgreSQL test role is not configured."));
        Assert.Contains(connection.Host, new[] { "127.0.0.1", "localhost", "::1" });
        Assert.False(string.IsNullOrWhiteSpace(connection.Username));
        var role = connection.Username;
        connection.Database = "postgres"; connection.Pooling = false;
        await using var admin = new NpgsqlConnection(connection.ConnectionString); await admin.OpenAsync();
        await using (var roleCheck = new NpgsqlCommand("SELECT rolcreatedb AND NOT rolsuper AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls FROM pg_roles WHERE rolname = current_user", admin))
            Assert.Equal(true, await roleCheck.ExecuteScalarAsync());
        var database = "pp_expense_snapshot_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            connection.Database = database;
            await using var writer = Context(connection.ConnectionString);
            await using var reader = Context(connection.ConnectionString);
            await writer.Database.EnsureCreatedAsync();
            var company = new LegalEntity { ShortName = "Own buyer", ProfileStatus = CompanyProfileStatuses.Active };
            var payment = new Payment { Account = "LINKED", Type = PaymentType.Income, Amount = 100m };
            var act = new Act { Number = "LINKED", Invoice = payment, Amount = 100m, SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.FirstFile };
            writer.AddRange(company, act); await writer.SaveChangesAsync();
            writer.SystemSettings.AddRange(new SystemSetting { Key = "notifications.email.enabled", Value = "false" },
                new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" },
                new SystemSetting { Key = CompanyDocumentImagePrintDefaultsService.Key(company.Id), Value = "broken" });
            await writer.SaveChangesAsync();
            var stale = await reader.Acts.AsNoTracking().SingleAsync();
            stale.Invoice = new Payment { Id = payment.Id, Type = PaymentType.Income };
            var service = new DocumentSellerService(reader);
            await using (var edit = await writer.Database.BeginTransactionAsync())
            {
                payment.Type = PaymentType.Expense; await writer.SaveChangesAsync();
                var busy = await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => service.ResolveAsync(stale, company, true, default, _ => Task.FromResult(true)));
                var cause = busy.InnerException;
                while (cause is not PostgresException && cause?.InnerException is not null) cause = cause.InnerException;
                Assert.Equal(PostgresErrorCodes.LockNotAvailable, Assert.IsType<PostgresException>(cause).SqlState);
                Assert.Null(stale.SellerSnapshotJson);
                Assert.Null(await reader.Acts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
                await using (var create = await reader.Database.BeginTransactionAsync())
                {
                    var pending = new Act { Number = "BUSY-CREATE", InvoiceId = payment.Id, SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.OnCreate };
                    await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => service.InitializeForCreationAsync(pending, company, default));
                    Assert.Null(pending.SellerSnapshotJson);
                    await create.RollbackAsync();
                }
                await edit.CommitAsync();
            }
            await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => service.ResolveAsync(stale, company, true, default, _ => Task.FromResult(true)));
            Assert.Null(stale.SellerSnapshotJson);
            stale = await reader.Acts.AsNoTracking().Include(row => row.Invoice).SingleAsync();
            await service.ResolveAsync(stale, company, true, default, _ => Task.FromResult(true));
            var saved = stale.SellerSnapshotJson;
            Assert.Equal(company.ShortName, SellerLegalEntityComposer.ReadMetadata(saved)!.CompanyName);
            Assert.Null(SellerLegalEntityComposer.ReadMetadata(saved)!.ImagePrintDefaults);
            await using (var create = await reader.Database.BeginTransactionAsync())
            {
                var created = new Act { Number = "ON-CREATE", InvoiceId = payment.Id, SellerSnapshotPolicy = DocumentSellerSnapshotPolicy.OnCreate };
                await service.InitializeForCreationAsync(created, company, default);
                Assert.Equal(DocumentSellerSnapshotPolicy.OnCreate, SellerLegalEntityComposer.ReadMetadata(created.SellerSnapshotJson)!.FreezeReason);
                Assert.Null(SellerLegalEntityComposer.ReadMetadata(created.SellerSnapshotJson)!.ImagePrintDefaults);
                reader.Acts.Add(created); await reader.SaveChangesAsync(); await create.RollbackAsync();
            }
            reader.ChangeTracker.Clear();
            await using (var edit = await writer.Database.BeginTransactionAsync())
            {
                payment.Type = PaymentType.Income; await writer.SaveChangesAsync();
                var repeat = await reader.Acts.AsNoTracking().SingleAsync();
                await service.ResolveAsync(repeat, company, true, default, _ => Task.FromResult(true));
                Assert.Equal(saved, repeat.SellerSnapshotJson);
                await edit.RollbackAsync();
            }
            Assert.Single(await reader.Acts.ToArrayAsync());
            Assert.Equal(saved, await reader.Acts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
            Assert.Equal(payment.Id, await reader.Acts.AsNoTracking().Select(row => row.InvoiceId).SingleAsync());
            Assert.Equal(PaymentType.Expense, await reader.Payments.AsNoTracking().Select(row => row.Type).SingleAsync());
            Assert.Equal(100m, await reader.Payments.AsNoTracking().Select(row => row.Amount).SingleAsync());
            Assert.Empty(await reader.MoneyOperations.ToArrayAsync());
            Assert.Empty(await reader.TaskJournalItems.ToArrayAsync());
            Assert.Empty(await reader.CompanyBrandImageVersions.ToArrayAsync());
        }
        finally
        {
            Assert.Matches("^pp_expense_snapshot_[a-f0-9]{32}$", database);
            await using var owner = new NpgsqlCommand("SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = @name", admin);
            owner.Parameters.AddWithValue("name", database); Assert.Equal(role, await owner.ExecuteScalarAsync());
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\"", admin); await drop.ExecuteNonQueryAsync();
        }
    }

    private static PostgreSqlPaymentContext Context(string connection) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
        .UseNpgsql(connection, options => options.MigrationsAssembly(typeof(PaymentContext).Assembly.GetName().Name)).Options);
}
