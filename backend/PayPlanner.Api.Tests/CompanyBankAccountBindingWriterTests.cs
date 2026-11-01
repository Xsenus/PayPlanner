using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Companies;
using Xunit;
using Cmd = PayPlanner.Api.Tests.CompanyBankAccountBindingCommandTests;

namespace PayPlanner.Api.Tests;

public sealed class CompanyBankAccountBindingWriterTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    public static IEnumerable<object[]> ProfileChanges()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var mode in new[] { "number", "bik", "currency", "id", "remove", "duplicate-id", "invalid-json", "rename", "primary", "archive", "forged-field", "unrelated-incomplete" })
                yield return [postgres, mode];
    }

    [Theory]
    [MemberData(nameof(ProfileChanges))]
    public Task CommonGuardProtectsConfirmedIdentityAndAllowsUnrelatedProfileEdits(bool postgres, string mode)
        => RunAsync(postgres, async db =>
        {
            var f = await Cmd.SeedAsync(db); var bind = await new CompanyBankAccountBindingService(db).BindAsync(Cmd.Principal(f.Actor), f.Connection, f.Bank, "local-id", await Cmd.VersionAsync(db, f), default);
            var json = (await db.LegalEntities.AsNoTracking().SingleAsync()).BankAccountsJson!;
            json = mode switch
            {
                "number" => json.Replace(Cmd.Number, "40702810200000000002"),
                "bik" => json.Replace(Cmd.Bik, "044525104"),
                "currency" => json.Replace("RUB", "USD"),
                "id" => json.Replace("local-id", "replacement"),
                "remove" => "[]",
                "duplicate-id" => json[..^1] + "," + json[1..],
                "invalid-json" => "not-json",
                "rename" => json.Replace("Profile account", "New label"),
                "primary" => json.Replace("\"isPrimary\":true", "\"isPrimary\":false"),
                "archive" => json.Replace("\"isArchived\":false", "\"isArchived\":true"),
                "forged-field" => json.Replace("\"id\":\"local-id\"", "\"id\":\"local-id\",\"bankConnectionAccountId\":999"),
                "unrelated-incomplete" => json[..^1] + ",{\"accountNumber\":\"other\"}]",
                _ => json
            };
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            if (mode is "rename" or "primary" or "archive" or "forged-field" or "unrelated-incomplete")
                await CompanyBankAccountBindingProfileGuard.ValidateAsync(db, f.Company, json, default);
            else Assert.Equal("ProfileBindingProtected", (await Assert.ThrowsAsync<CompanyBankBindingException>(() => CompanyBankAccountBindingProfileGuard.ValidateAsync(db, f.Company, json, default))).Code);
            Assert.Equal(bind.BindingId, (await db.CompanyBankAccountBindings.AsNoTracking().SingleAsync()).Id);
            await tx.RollbackAsync();
        });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task ProfileControllerProtectsBindingAndRollsBackOtherFields(bool postgres, bool allowed)
        => RunAsync(postgres, async db =>
        {
            var f = await Cmd.SeedAsync(db);
            await new CompanyBankAccountBindingService(db).BindAsync(Cmd.Principal(f.Actor), f.Connection, f.Bank, "local-id", await Cmd.VersionAsync(db, f), default);
            var controller = new LegalEntitiesController(db, null!, new FormRequirementsService(db))
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Cmd.Principal(f.Actor) } } };
            var detail = Assert.IsType<LegalEntityDetailDto>(Assert.IsType<OkObjectResult>((await controller.GetById(f.Company, default)).Result).Value);
            var json = detail.BankAccountsJson!;
            json = allowed ? json.Replace("Profile account", "Renamed") : json.Replace(Cmd.Number, "40702810200000000002");
            var request = new LegalEntityRequest { ShortName = detail.ShortName, BankAccountsJson = json, Phone = "Changed with bank form", ProfileEditVersion = detail.ProfileEditVersion };
            var result = await controller.Update(f.Company, request, default);
            if (allowed) Assert.IsType<OkObjectResult>(result.Result);
            else Assert.IsType<ConflictObjectResult>(result.Result);
            db.ChangeTracker.Clear(); var company = await db.LegalEntities.AsNoTracking().SingleAsync();
            Assert.Equal(allowed ? "Changed with bank form" : null, company.Phone);
            Assert.Contains(Cmd.Number, company.BankAccountsJson!); Assert.Null((await db.CompanyBankAccountBindings.AsNoTracking().SingleAsync()).RevokedAtUtc);
        });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task DatabaseMergePreservesAuthorityAndRejectsChangingConfirmedProfile(bool postgres, bool allowed)
        => RunAsync(postgres, async db =>
        {
            var f = await Cmd.SeedAsync(db);
            var bound = await new CompanyBankAccountBindingService(db).BindAsync(Cmd.Principal(f.Actor), f.Connection, f.Bank, "local-id", await Cmd.VersionAsync(db, f), default);
            var original = await db.LegalEntities.AsNoTracking().SingleAsync();
            var path = Path.Combine(Path.GetTempPath(), "pp-binding-merge-" + Guid.NewGuid().ToString("N") + ".sqlite");
            try
            {
                Console.WriteLine($"Owned binding merge source: {path}");
                await using (var connection = new SqliteConnection($"Data Source={path};Pooling=false"))
                {
                    await connection.OpenAsync();
                    await using var source = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
                    await source.Database.EnsureCreatedAsync();
                    source.LegalEntities.Add(new LegalEntity { ShortName = original.ShortName, Phone = "Incoming source", BankAccountsJson = allowed ? original.BankAccountsJson!.Replace("Profile account", "Imported label") : Cmd.Profile("replacement") });
                    await source.SaveChangesAsync();
                }
                var provider = new DatabaseProviderInfo(postgres ? DatabaseProviderKind.PostgreSql : DatabaseProviderKind.Sqlite, db.Database.GetConnectionString()!);
                var service = new DatabaseImportService(new WriterFactory(db), provider, NullLogger<DatabaseImportService>.Instance);
                var result = await service.ImportSqliteAsync(path);
                var companies = Assert.Single(result.Tables, x => x.Table == "LegalEntities");
                if (allowed) Assert.Equal(0, companies.Errors); else Assert.True(companies.Errors > 0);
                db.ChangeTracker.Clear(); var after = await db.LegalEntities.AsNoTracking().SingleAsync();
                Assert.Equal(allowed ? "Incoming source" : original.Phone, after.Phone);
                Assert.Contains("local-id", after.BankAccountsJson!);
                var binding = await db.CompanyBankAccountBindings.AsNoTracking().SingleAsync();
                Assert.Equal(bound.BindingId, binding.Id); Assert.Equal(f.Bank, binding.BankConnectionAccountId); Assert.Null(binding.RevokedAtUtc);
                Assert.Equal(123.45m, (await db.MoneyOperations.AsNoTracking().SingleAsync()).Amount);
            }
            finally
            {
                File.Delete(path); Assert.False(File.Exists(path));
            }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DatabaseMergeGuardBatchesReadsForManyChangedCompanies(bool postgres)
        => RunAsync(postgres, async db =>
        {
            var f = await Cmd.SeedAsync(db);
            var companies = Enumerable.Range(0, 32).Select(i => new LegalEntity { ShortName = "Bulk binding " + i, BankAccountsJson = Cmd.Profile() }).ToArray();
            db.LegalEntities.AddRange(companies); await db.SaveChangesAsync();
            foreach (var company in companies)
            {
                var connection = new BankConnection { LegalEntityId = company.Id, Provider = BankProviderKeys.Sber };
                db.BankConnections.Add(connection); await db.SaveChangesAsync();
                var account = new BankConnectionAccount { BankConnectionId = connection.Id, ExternalAccountId = "bulk-source", AccountNumber = Cmd.Number, Bik = Cmd.Bik, Currency = "RUB" };
                db.BankConnectionAccounts.Add(account); await db.SaveChangesAsync();
                db.CompanyBankAccountBindings.Add(new CompanyBankAccountBinding
                {
                    LegalEntityId = company.Id,
                    ProfileAccountId = "local-id",
                    ProfileAccountKey = CompanyBankAccountBindingIdentity.ProfileKey("local-id"),
                    BankConnectionAccountId = account.Id,
                    Provider = BankProviderKeys.Sber,
                    ExternalAccountId = "bulk-source",
                    AccountNumber = Cmd.Number,
                    Bik = Cmd.Bik,
                    Currency = "RUB",
                    ConfirmedMatchVersion = "synthetic-storage-fixture",
                    CreatedByUserId = f.Actor,
                    CreatedAtUtc = DateTime.UtcNow
                });
            }
            await db.SaveChangesAsync();
            var reads = new BindingReads();
            await using PaymentContext writer = postgres
                ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(db.Database.GetConnectionString()).AddInterceptors(reads).Options)
                : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(db.Database.GetDbConnection()).AddInterceptors(reads).Options);
            await using var tx = await writer.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            var ids = companies.Select(x => x.Id).ToArray();
            foreach (var company in await writer.LegalEntities.Where(x => ids.Contains(x.Id)).ToArrayAsync())
                company.BankAccountsJson = company.BankAccountsJson!.Replace("Profile account", "Bulk renamed");
            await CompanyBankAccountBindingProfileGuard.ValidateTrackedAsync(writer, default);
            Assert.Equal(2, reads.Count); await tx.RollbackAsync();
        });

    private sealed class BindingReads : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("FROM \"CompanyBankAccountBindings\"", StringComparison.Ordinal)) Count++;
            return ValueTask.FromResult(result);
        }
    }

    private async Task RunAsync(bool postgres, Func<PaymentContext, Task> test)
    {
        if (postgres) { await using var database = await template.CreateDatabaseAsync(); await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options); await test(db); }
        else { await using var db = await SqliteTestContextFactory.CreateAsync(); await test(db); }
    }

    internal sealed class WriterFactory(PaymentContext observer, IHttpContextAccessor? accessor = null) : IDbContextFactory<PaymentContext>
    {
        public PaymentContext CreateDbContext()
            => observer.Database.IsNpgsql()
                ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(observer.Database.GetConnectionString()).Options, accessor)
                : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(observer.Database.GetDbConnection()).Options, accessor);
        public Task<PaymentContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
