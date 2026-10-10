using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Banking;
using PayPlanner.Api.Services.Requests;
using PayPlanner.Api.Services.Tax;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ClaudeBranchRegressionTests
{
    [Fact]
    public async Task MoneyLineValuesAndTotalsSurviveRealPostgreSqlStorage()
    {
        await using var database = await Database.CreateAsync(true);
        if (database is null) return;
        await using var db = database.Open();
        await db.Database.MigrateAsync();
        var lines = new[] { new VatLine(1.0075m, 10m, "20"), new VatLine(1.5m, 0.01m, null) };
        var expected = VatCalculator.DocumentTotal(lines);
        var payment = new Payment
        {
            Account = "PRECISION",
            CounterpartyName = "Test",
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Amount = expected,
            Date = DateTime.UtcNow,
            PlannedDate = DateTime.UtcNow,
            InvoiceLineItems = lines.Select((line, index) => new InvoiceLineItem
            {
                Name = "Service",
                SortOrder = index,
                Quantity = line.Quantity,
                Price = line.Price,
                VatRate = line.VatRate
            }).ToList()
        };
        var act = new Act
        {
            Number = "PRECISION",
            Date = DateTime.UtcNow,
            Amount = expected,
            LineItems = lines.Select((line, index) => new ActLineItem
            {
                Name = "Service",
                SourceKey = $"manual:{index}",
                SortOrder = index,
                Quantity = line.Quantity,
                Price = line.Price,
                VatRate = line.VatRate
            }).ToList()
        };
        db.AddRange(payment, act);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var storedInvoice = await db.Payments.Include(item => item.InvoiceLineItems).SingleAsync();
        var storedAct = await db.Acts.Include(item => item.LineItems).SingleAsync();
        Assert.Equal(10.10m, expected);
        Assert.Equal(expected, storedInvoice.Amount);
        Assert.Equal(expected, storedAct.Amount);
        Assert.Equal(expected, VatCalculator.DocumentTotal(storedInvoice.InvoiceLineItems.Select(item => new VatLine(item.Quantity, item.Price, item.VatRate))));
        Assert.Equal(expected, VatCalculator.DocumentTotal(storedAct.LineItems.Select(item => new VatLine(item.Quantity, item.Price, item.VatRate))));
        Assert.Equal(lines.Select(item => (item.Quantity, item.Price)), storedInvoice.InvoiceLineItems.OrderBy(item => item.SortOrder).Select(item => (item.Quantity, item.Price)));
        Assert.Equal(lines.Select(item => (item.Quantity, item.Price)), storedAct.LineItems.OrderBy(item => item.SortOrder).Select(item => (item.Quantity, item.Price)));
    }

    private sealed class Database : IAsyncDisposable
    {
        public required Func<PaymentContext> Open { get; init; }
        public string? File { get; init; }
        public NpgsqlConnection? Admin { get; init; }
        public string? Name { get; init; }

        public static async Task<Database?> CreateAsync(bool postgres)
        {
            if (!postgres)
            {
                var file = Path.Combine(Path.GetTempPath(), $"pp_claude_review_{Guid.NewGuid():N}.db");
                return new Database
                {
                    File = file,
                    Open = () => new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                    .UseSqlite($"Data Source={file};Pooling=False;Default Timeout=30").UsePayPlannerSqliteMigrations().Options)
                };
            }
            var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(raw))
            {
                Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
                return null;
            }
            var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
            Assert.Contains(settings.Host, new[] { "127.0.0.1", "localhost", "::1" });
            var admin = new NpgsqlConnection(settings.ConnectionString);
            await admin.OpenAsync();
            var name = $"pp_claude_review_{Guid.NewGuid():N}";
            await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
                await command.ExecuteNonQueryAsync();
            settings.Database = name;
            return new Database
            {
                Admin = admin,
                Name = name,
                Open = () => new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(settings.ConnectionString).Options)
            };
        }

        public async ValueTask DisposeAsync()
        {
            if (Admin is not null)
            {
                await using var command = new NpgsqlCommand($"DROP DATABASE \"{Name}\" WITH (FORCE)", Admin);
                await command.ExecuteNonQueryAsync();
                await Admin.DisposeAsync();
            }
            if (File is not null) System.IO.File.Delete(File);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Migrations_CleanAndUpgradePreserveDataAndMatchModel(bool postgres, bool upgrade)
    {
        await using var database = await Database.CreateAsync(postgres);
        if (database is null) return;
        await using var db = database.Open();
        if (upgrade)
        {
            var migrations = db.Database.GetMigrations().ToArray();
            var firstBranch = Array.FindIndex(migrations, id => id.Contains("AddCompanyBranches", StringComparison.Ordinal));
            Assert.True(firstBranch > 0);
            await db.GetService<IMigrator>().MigrateAsync(migrations[firstBranch - 1]);
            // Client schema is unchanged by this branch; preserve a real baseline object across its migrations.
            db.Clients.Add(new Client { Name = "Upgrade survivor", Phone = "+79000000000" });
            await db.SaveChangesAsync();
        }
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        if (upgrade) Assert.True(await db.Clients.AnyAsync(item => item.Name == "Upgrade survivor"));
        var service = new SalesRequestService(db);
        await service.EnsureDefaultsAsync(default);
        Assert.Equal(10, await db.SalesRequestStatuses.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentNewClientRequests_DoNotDuplicatePhone(bool postgres)
    {
        await using var database = await Database.CreateAsync(postgres);
        if (database is null) return;
        int actor;
        await using (var seed = database.Open())
        {
            await seed.Database.EnsureCreatedAsync();
            var user = new User
            {
                Email = "manager@x.test",
                FullName = "Manager",
                PasswordHash = "x",
                IsActive = true,
                IsApproved = true,
                Role = new Role { Name = "admin", Description = "admin" }
            };
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
            actor = user.Id;
            await new SalesRequestService(seed).EnsureDefaultsAsync(default);
        }
        await using var first = database.Open();
        await using var second = database.Open();
        var input = new SalesRequestCreateInput("Call", null, "Test client", "8 900 555 44 33", null, null, null, "Test request", null, null, actor);
        var results = await Task.WhenAll(Task.Run(() => new SalesRequestService(first).CreateAsync(input, actor, default)),
            Task.Run(() => new SalesRequestService(second).CreateAsync(input with { NewClientPhone = "+7 (900) 555-44-33" }, actor, default)));
        Assert.Single(results, item => item.Ok);
        Assert.Single(results, item => item.Status == 409);
        await using var verify = database.Open();
        Assert.Equal(1, await verify.Clients.CountAsync());
        Assert.Equal(1, await verify.SalesRequests.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentWrites_SerializeRegisterClosureCorrectionAndAccountDefaults(bool postgres)
    {
        await using var database = await Database.CreateAsync(postgres);
        if (database is null) return;
        int company, entryId, firstAccount, secondAccount;
        await using (var seed = database.Open())
        {
            await seed.Database.EnsureCreatedAsync();
            var entity = new LegalEntity { ShortName = "Test company", Inn = "7707083893" };
            seed.LegalEntities.Add(entity);
            await seed.SaveChangesAsync();
            company = entity.Id;
            var service = new TaxRegisterService(seed);
            var entry = (await service.CreateAsync(new TaxRegisterEntryInput(company, "Income", new DateTime(2025, 1, 1), 100m,
                "Test document", null, null, null, null, false, null), 1, default)).Value!;
            await service.ApproveAsync(entry.Id, 1, default);
            entryId = entry.Id;
            var first = new BankConnectionAccount { BankConnection = new BankConnection { LegalEntityId = company, Provider = "Tochka" }, ExternalAccountId = "first" };
            var second = new BankConnectionAccount { BankConnection = new BankConnection { LegalEntityId = company, Provider = "Sber" }, ExternalAccountId = "second" };
            seed.AddRange(first, second);
            await seed.SaveChangesAsync();
            firstAccount = first.Id;
            secondAccount = second.Id;
        }
        // Keep a stale tracked draft in a second context: approval must win over a late edit.
        await using (var first = database.Open())
        await using (var second = database.Open())
        {
            var created = (await new TaxRegisterService(first).CreateAsync(new TaxRegisterEntryInput(company, "Income", new DateTime(2025, 2, 1), 50m,
                "Draft", null, null, null, null, false, null), 1, default)).Value!;
            await second.TaxRegisterEntries.SingleAsync(item => item.Id == created.Id);
            Assert.True((await new TaxRegisterService(first).ApproveAsync(created.Id, 1, default)).Ok);
            Assert.Equal(409, (await new TaxRegisterService(second).UpdateDraftAsync(created.Id,
                new TaxRegisterEntryInput(company, "Income", created.OperationDate, 999m, "Changed", null, null, null, null, false, null), 1, default)).Status);
        }
        await using (var first = database.Open())
        await using (var second = database.Open())
        {
            var results = await Task.WhenAll(Task.Run(() => new TaxRegisterService(first).CorrectAsync(entryId, "first", 1, default)),
                Task.Run(() => new TaxRegisterService(second).CorrectAsync(entryId, "second", 1, default)));
            Assert.Single(results, item => item.Ok);
            Assert.Single(results, item => item.Status == 409);
        }
        await using (var first = database.Open())
        await using (var second = database.Open())
        {
            var results = await Task.WhenAll(Task.Run(() => new TaxRegisterService(first).CloseYearAsync(company, 2025, 1, default)),
                Task.Run(() => new TaxRegisterService(second).CloseYearAsync(company, 2025, 1, default)));
            Assert.Single(results, item => item.Ok);
            Assert.Single(results, item => item.Status == 409);
        }
        await using (var first = database.Open())
        await using (var second = database.Open())
        {
            var results = await Task.WhenAll(
                Task.Run(() => new BankSyncService(first, [], new EphemeralDataProtectionProvider()).UpdateAccountSettingsAsync(firstAccount, true, null, null, default)),
                Task.Run(() => new BankSyncService(second, [], new EphemeralDataProtectionProvider()).UpdateAccountSettingsAsync(secondAccount, true, null, null, default)));
            Assert.All(results, item => Assert.True(item.Success));
        }
        await using var verify = database.Open();
        Assert.Equal(1, await verify.BankConnectionAccounts.CountAsync(item => item.IsDefault));
        Assert.Single(await verify.TaxRegisterEntries.Where(item => item.CorrectsEntryId == entryId).ToListAsync());
        Assert.NotNull((await verify.TaxRegisterEntries.SingleAsync(item => item.Id == entryId)).CorrectedByEntryId);
        Assert.Equal(409, (await new TaxRegisterService(verify).CreateAsync(new TaxRegisterEntryInput(company, "Income", new DateTime(2025, 3, 1), 1m,
            "Late", null, null, null, null, false, null), 1, default)).Status);
    }
}
