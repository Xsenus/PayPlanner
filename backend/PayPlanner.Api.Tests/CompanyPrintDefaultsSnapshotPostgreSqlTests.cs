using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CompanyPrintDefaultsSnapshotPostgreSqlTests
{
    [Fact]
    public async Task PostgreSql_CaptureSharesCompanyLeaseAndPreservesCommittedDefaults()
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
        var database = "pp_print_snapshot_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            connection.Database = database;
            await using var first = Context(connection.ConnectionString);
            await using var second = Context(connection.ConnectionString);
            await first.Database.EnsureCreatedAsync();
            first.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
            first.LegalEntities.Add(new LegalEntity { Id = 7, ShortName = "Synthetic own", ProfileStatus = CompanyProfileStatuses.Active });
            first.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = "[7]" });
            await first.SaveChangesAsync();
            var company = await second.LegalEntities.AsNoTracking().SingleAsync();
            var store = new CompanyDocumentImagePrintDefaultsService(first);
            var original = (await store.ReadAsync(7)).Version!;
            var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var edit = store.EditAsync(7, original, async () =>
            {
                await store.StageAsync(7, CompanyPrintDefaultsSnapshotTests.Value(5), true);
                await first.SaveChangesAsync(); admitted.SetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(15)); return true;
            }, default);
            var document = new Act { Number = "BUSY", SellerCompanyId = 7 };
            var service = new DocumentSellerService(second);
            try
            {
                await admitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => service.ResolveAsync(document, company, true, default));
                Assert.Null(document.SellerSnapshotJson);
                Assert.Empty(await second.CompanyBrandImageVersions.ToArrayAsync());
            }
            finally { release.TrySetResult(); await edit; }
            second.Acts.Add(document); await second.SaveChangesAsync();
            var stale = await second.Acts.AsNoTracking().SingleAsync();
            await service.ResolveAsync(document, company, true, default, _ => Task.FromResult(true));
            var captured = document.SellerSnapshotJson;
            Assert.Equal(CompanyPrintDefaultsSnapshotTests.Value(5), SellerLegalEntityComposer.ReadMetadata(captured)!.ImagePrintDefaults);
            await store.StageAsync(7, CompanyPrintDefaultsSnapshotTests.Value(2), true); await first.SaveChangesAsync();
            await service.ResolveAsync(stale, company, true, default, _ => Task.FromResult(true));
            Assert.Equal(captured, stale.SellerSnapshotJson);
            Assert.Equal(captured, await second.Acts.AsNoTracking().Select(row => row.SellerSnapshotJson).SingleAsync());
            var invalid = await first.SystemSettings.SingleAsync(row => row.Key == CompanyDocumentImagePrintDefaultsService.Key(7));
            invalid.Value = new string('x', CompanyDocumentImagePrintDefaultsService.MaximumLength + 1); await first.SaveChangesAsync();
            var state = await new CompanyDocumentImagePrintDefaultsService(second).ReadForSnapshotAsync(7);
            Assert.True(state.Invalid); Assert.Null(state.Value); Assert.Null(state.Version);
            Assert.Empty(await second.Payments.ToArrayAsync());
            Assert.Empty(await second.ClientCases.ToArrayAsync());
            Assert.Empty(await second.MoneyOperations.ToArrayAsync());
        }
        finally
        {
            Assert.Matches("^pp_print_snapshot_[a-f0-9]{32}$", database);
            await using var owner = new NpgsqlCommand("SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = @name", admin);
            owner.Parameters.AddWithValue("name", database);
            Assert.Equal(role, await owner.ExecuteScalarAsync());
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\"", admin); await drop.ExecuteNonQueryAsync();
        }
    }

    private static PostgreSqlPaymentContext Context(string connection) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
        .UseNpgsql(connection, options => options.MigrationsAssembly(typeof(PaymentContext).Assembly.GetName().Name)).Options);
}
