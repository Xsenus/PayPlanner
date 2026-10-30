using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class DatabaseImportServiceTests
{
    private sealed class HistoryPgFactory(string connection, IInterceptor? interceptor = null) : IDbContextFactory<PaymentContext>
    {
        public PaymentContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection);
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            return new PostgreSqlPaymentContext(builder.Options);
        }
        public Task<PaymentContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private static async Task WithHistoryPgAsync(Func<string, string, Task> action)
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Existing isolated PostgreSQL test role is not configured."));
        Assert.Equal("127.0.0.1", connection.Host); Assert.Equal(5433, connection.Port); Assert.Equal("payplanner_ci", connection.Username);
        connection.Database = "postgres"; connection.Pooling = false;
        await using var admin = new NpgsqlConnection(connection.ConnectionString); await admin.OpenAsync();
        await using (var check = new NpgsqlCommand("SELECT current_setting('server_version_num')::int/10000=17 AND rolcreatedb AND NOT rolsuper AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls FROM pg_roles WHERE rolname=current_user", admin))
            Assert.Equal(true, await check.ExecuteScalarAsync());
        var database = "pp_seller_history_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        var path = Path.Combine(Path.GetTempPath(), $"pp-history-pg-{Guid.NewGuid():N}.sqlite");
        try { connection.Database = database; await action(path, connection.ConnectionString); }
        finally
        {
            TryDeleteFile(path);
            Assert.Matches("^pp_seller_history_[a-f0-9]{32}$", database);
            await using var owner = new NpgsqlCommand("SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname=@name", admin); owner.Parameters.AddWithValue("name", database);
            Assert.Equal(connection.Username, await owner.ExecuteScalarAsync());
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\"", admin); await drop.ExecuteNonQueryAsync();
        }
    }

    private static DatabaseImportService HistoryPgService(string connection, IInterceptor? interceptor = null)
        => new(new HistoryPgFactory(connection, interceptor), new DatabaseProviderInfo(DatabaseProviderKind.PostgreSql, connection), NullLogger<DatabaseImportService>.Instance);

    [Theory]
    [InlineData("first-file")]
    [InlineData("on-create")]
    public async Task HistoryPg_ImportsMappedImagesBranchesAndKeepsIssuedDocuments(string mode)
        => await WithHistoryPgAsync(async (path, connection) =>
        {
            await SeedHistorySourceAsync(path, mode); var bytes = await File.ReadAllBytesAsync(path);
            var factory = new HistoryPgFactory(connection);
            await using (var setup = factory.CreateDbContext()) await SeedHistoryTargetAsync(setup);
            Assert.True((await HistoryPgService(connection).ImportSqliteAsync(path)).Success);
            await using var verify = factory.CreateDbContext(); await AssertHistoryResultAsync(path, verify, mode);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        });

    [Fact]
    public async Task HistoryPg_FailureAfterMaterializationRollsBackVersionsBranchesAndDocuments()
        => await WithHistoryPgAsync(async (path, connection) =>
        {
            await SeedHistorySourceAsync(path); var factory = new HistoryPgFactory(connection);
            await using (var setup = factory.CreateDbContext()) await SeedHistoryTargetAsync(setup);
            var fail = new FailHistoryDocuments(); Assert.False((await HistoryPgService(connection, fail).ImportSqliteAsync(path)).Success);
            Assert.True(fail.SawMaterializedReferences);
            await using var verify = factory.CreateDbContext();
            Assert.Equal(1, await verify.CompanyBrandImageVersions.CountAsync()); Assert.Equal(1, await verify.CompanyBranches.CountAsync()); Assert.Equal(1, await verify.Payments.CountAsync());
            Assert.Empty(await verify.Roles.ToArrayAsync());
        });

    [Fact]
    public async Task HistoryPg_BusyCompanyRefusesBeforeWritesAndCanBeRetriedAfterRelease()
        => await WithHistoryPgAsync(async (path, connection) =>
        {
            await SeedHistorySourceAsync(path); var factory = new HistoryPgFactory(connection);
            await using (var setup = factory.CreateDbContext()) await SeedHistoryTargetAsync(setup);
            await using (var holder = factory.CreateDbContext())
            {
                await using var transaction = await holder.Database.BeginTransactionAsync();
                await holder.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"LegalEntities\" WHERE \"Id\"=2 FOR UPDATE NOWAIT").ToArrayAsync();
                await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => HistoryPgService(connection).ImportSqliteAsync(path));
                await transaction.RollbackAsync();
            }
            await using (var verify = factory.CreateDbContext()) Assert.Equal(1, await verify.CompanyBrandImageVersions.CountAsync());
            Assert.True((await HistoryPgService(connection).ImportSqliteAsync(path)).Success);
        });

    private sealed class DuringHistorySave(Func<Task> action) : SaveChangesInterceptor
    {
        private bool called;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!called && eventData.Context!.ChangeTracker.Entries<Payment>().Any(e => e.State == EntityState.Added && e.Entity.Description == "Historical invoice"))
            { called = true; await action(); }
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task HistoryPg_ParallelFirstFileCannotCaptureWhileHistoryOwnsTheCompany()
        => await WithHistoryPgAsync(async (path, connection) =>
        {
            await SeedHistorySourceAsync(path); var factory = new HistoryPgFactory(connection);
            await using (var setup = factory.CreateDbContext())
            {
                await SeedHistoryTargetAsync(setup);
                (await setup.Payments.SingleAsync()).SellerSnapshotJson = null; await setup.SaveChangesAsync();
            }
            var blocked = false;
            var interceptor = new DuringHistorySave(async () =>
            {
                await using var other = factory.CreateDbContext();
                var document = await other.Payments.SingleAsync(p => p.Description == "Keep issued invoice");
                var company = await other.LegalEntities.SingleAsync(c => c.Inn == HistoryInn);
                await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => new DocumentSellerService(other).ResolveAsync(document, company, true, default));
                Assert.Null(await other.Payments.Where(p => p.Id == document.Id).Select(p => p.SellerSnapshotJson).SingleAsync());
                blocked = true;
            });
            Assert.True((await HistoryPgService(connection, interceptor).ImportSqliteAsync(path)).Success); Assert.True(blocked);
            await using var after = factory.CreateDbContext();
            var current = await after.Payments.SingleAsync(p => p.Description == "Keep issued invoice");
            Assert.Null(current.SellerSnapshotJson);
            var profile = await after.LegalEntities.SingleAsync(c => c.Inn == HistoryInn);
            var seller = await new DocumentSellerService(after).ResolveAsync(current, profile, true, default);
            Assert.NotNull(seller); Assert.Equal("Target current profile", seller.ShortName); Assert.Equal("target current logo", seller.LogoDataUrl);
        });
}
