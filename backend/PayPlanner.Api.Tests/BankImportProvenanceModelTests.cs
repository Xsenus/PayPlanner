using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class BankImportProvenanceModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyStyleRecordsRemainUnattributedOnFreshModel(bool postgres)
    {
        await using var fixture = await ModelDatabase.CreateAsync(postgres);
        var db = fixture.Db;
        db.MoneyAccounts.Add(new MoneyAccount { Name = "Прежний счёт", Source = MoneyOperationSource.Bank });
        db.BankStatementImportBatches.Add(new BankStatementImportBatch { FileName = "legacy.csv", FileHash = "legacy" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var account = await db.MoneyAccounts.SingleAsync();
        var batch = await db.BankStatementImportBatches.SingleAsync();
        Assert.Null(account.CompanyId);
        Assert.Null(account.BankConnectionAccountId);
        Assert.Null(batch.CompanyId);
        Assert.Null(batch.MoneyAccountId);
        Assert.Null(batch.BankConnectionAccountId);
        Assert.Null(batch.ImportedByUserId);
        Assert.Null(batch.ResponsibleUserId);
        Assert.Null(batch.Source);
        Assert.Null(batch.Trigger);
        Assert.Null(batch.ParentBatchId);
        Assert.Null(batch.PackageKey);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EmptyAndErrorOnlyPackagesRetainOriginWithoutMoney(bool postgres, bool errorRow)
    {
        await using var fixture = await ModelDatabase.CreateAsync(postgres);
        var db = fixture.Db;
        var account = await AddAccountAsync(db);
        var batch = new BankStatementImportBatch
        {
            CompanyId = account.CompanyId,
            MoneyAccountId = account.Id,
            BankConnectionAccountId = account.BankConnectionAccountId,
            ImportedByUserId = 42,
            ResponsibleUserId = 43,
            Source = BankStatementImportSources.ManualBank,
            Trigger = BankStatementImportTriggers.Manual,
            PackageKey = new string('a', 64),
            PeriodFrom = Date,
            PeriodTo = Date,
            FileName = "synthetic-bank-result",
            FileHash = "synthetic",
            ErrorRows = errorRow ? 1 : 0,
        };
        if (errorRow)
        {
            batch.Rows.Add(new BankStatementImportRow
            {
                RowNumber = 1,
                ImportKey = "unresolved-source-row",
                Status = BankStatementImportRowStatus.Error,
                Date = Date,
                Amount = 123m,
                Currency = "RUB",
                IsBooked = false,
                Message = "Синтетическая ошибка распознавания",
            });
        }
        db.BankStatementImportBatches.Add(batch);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var saved = await db.BankStatementImportBatches.Include(item => item.Rows).SingleAsync();
        Assert.Equal(account.CompanyId, saved.CompanyId);
        Assert.Equal(account.Id, saved.MoneyAccountId);
        Assert.Equal(account.BankConnectionAccountId, saved.BankConnectionAccountId);
        Assert.Equal(42, saved.ImportedByUserId);
        Assert.Equal(43, saved.ResponsibleUserId);
        Assert.Equal(BankStatementImportSources.ManualBank, saved.Source);
        Assert.Equal(BankStatementImportTriggers.Manual, saved.Trigger);
        Assert.Equal(Date.Date, saved.PeriodFrom!.Value.Date);
        Assert.Equal(errorRow ? 1 : 0, saved.Rows.Count);
        Assert.Empty(await db.MoneyOperations.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountNamesAreUniqueWithinCompanyAndPreserveLegacyConstraint(bool postgres)
    {
        await using var fixture = await ModelDatabase.CreateAsync(postgres);
        var db = fixture.Db;
        var firstCompany = new LegalEntity { ShortName = "Первая" };
        var secondCompany = new LegalEntity { ShortName = "Вторая" };
        db.LegalEntities.AddRange(firstCompany, secondCompany);
        await db.SaveChangesAsync();
        db.MoneyAccounts.AddRange(
            new MoneyAccount { CompanyId = firstCompany.Id, Name = "Основной", Source = MoneyOperationSource.Bank },
            new MoneyAccount { CompanyId = secondCompany.Id, Name = "Основной", Source = MoneyOperationSource.Bank });
        await db.SaveChangesAsync();
        var duplicate = new MoneyAccount { CompanyId = firstCompany.Id, Name = "Основной", Source = MoneyOperationSource.Bank };
        db.MoneyAccounts.Add(duplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.Entry(duplicate).State = EntityState.Detached;
        Assert.Equal(2, await db.MoneyAccounts.CountAsync());

        db.MoneyAccounts.Add(new MoneyAccount { Name = "Прежний", Source = MoneyOperationSource.Bank });
        await db.SaveChangesAsync();
        var legacyDuplicate = new MoneyAccount { Name = "Прежний", Source = MoneyOperationSource.Bank };
        db.MoneyAccounts.Add(legacyDuplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.Entry(legacyDuplicate).State = EntityState.Detached;
        Assert.Equal(3, await db.MoneyAccounts.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StableMovementAndPackageKeysRejectDuplicateInserts(bool postgres)
    {
        await using var fixture = await ModelDatabase.CreateAsync(postgres);
        var db = fixture.Db;
        var account = await AddAccountAsync(db);
        var original = NewMovement(account, new string('b', 64));
        db.MoneyOperations.Add(original);
        db.BankStatementImportBatches.Add(new BankStatementImportBatch
        {
            FileName = "original.csv",
            FileHash = "hash",
            PackageKey = new string('c', 64),
            CompanyId = account.CompanyId,
            MoneyAccountId = account.Id,
        });
        await db.SaveChangesAsync();

        var duplicate = NewMovement(account, original.SourceMovementKey!);
        db.MoneyOperations.Add(duplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.Entry(duplicate).State = EntityState.Detached;
        var duplicateBatch = new BankStatementImportBatch { FileName = "retry.csv", FileHash = "hash", PackageKey = new string('c', 64) };
        db.BankStatementImportBatches.Add(duplicateBatch);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.Entry(duplicateBatch).State = EntityState.Detached;
        Assert.Equal(1, await db.MoneyOperations.CountAsync());
        Assert.Equal(1, await db.BankStatementImportBatches.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetriedRowsRetainRawBankOperationAfterMoneyIsRemoved(bool postgres)
    {
        await using var fixture = await ModelDatabase.CreateAsync(postgres);
        var db = fixture.Db;
        var account = await AddAccountAsync(db);
        var raw = new BankStatementOperation
        {
            BankConnectionAccountId = account.BankConnectionAccountId!.Value,
            ExternalOperationId = "native-1",
            OperationDate = Date,
            Amount = 123m,
            IsIncome = false,
        };
        var operation = NewMovement(account, new string('d', 64));
        var root = new BankStatementImportBatch { FileName = "root", FileHash = "root", CompanyId = account.CompanyId, MoneyAccountId = account.Id };
        operation.BankStatementImportBatch = root;
        var rootRow = new BankStatementImportRow
        {
            RowNumber = 1,
            ImportKey = "root-row",
            Status = BankStatementImportRowStatus.Created,
            Date = Date,
            Amount = 123m,
            MoneyOperation = operation,
            BankStatementOperation = raw,
            MovementKey = operation.SourceMovementKey,
        };
        root.Rows.Add(rootRow);
        db.BankStatementImportBatches.Add(root);
        await db.SaveChangesAsync();
        var rawId = raw.Id;
        db.MoneyOperations.Remove(operation);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var preserved = await db.BankStatementImportRows.SingleAsync();
        Assert.Null(preserved.MoneyOperationId);
        Assert.Equal(rawId, preserved.BankStatementOperationId);
        var archived = await db.MoneyOperations.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(operation.Id, archived.Id);
        Assert.True(db.Entry(archived).Property<bool>("IsDeleted").CurrentValue);
        // A rollback retains the original financial identity. A retry must not
        // bypass the uniqueness barrier by inserting a second archived/live row.
        var secondIdentity = NewMovement(account, operation.SourceMovementKey!);
        db.MoneyOperations.Add(secondIdentity);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.Entry(secondIdentity).State = EntityState.Detached;
        db.Entry(archived).Property("IsDeleted").CurrentValue = false;
        db.Entry(archived).Property("DeletedAt").CurrentValue = null;
        db.Entry(archived).Property("DeletedByUserId").CurrentValue = null;
        db.Entry(archived).Property("DeletedByUserName").CurrentValue = null;

        var retry = new BankStatementImportBatch
        {
            ParentBatchId = root.Id,
            CompanyId = account.CompanyId,
            MoneyAccountId = account.Id,
            FileName = "retry",
            FileHash = "retry",
        };
        retry.Rows.Add(new BankStatementImportRow
        {
            RowNumber = 1,
            ImportKey = "retry-row",
            Status = BankStatementImportRowStatus.Created,
            Date = Date,
            Amount = 123m,
            BankStatementOperationId = rawId,
            OriginRowId = rootRow.Id,
            MovementKey = operation.SourceMovementKey,
            MoneyOperation = archived,
        });
        db.BankStatementImportBatches.Add(retry);
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.BankStatementOperations.CountAsync());
        Assert.Equal(1, await db.MoneyOperations.CountAsync());
        Assert.Equal(1, await db.MoneyOperations.IgnoreQueryFilters().CountAsync());
        Assert.Equal(operation.Id, (await db.MoneyOperations.SingleAsync()).Id);
        Assert.Equal(rawId, await db.BankStatementImportRows.Where(item => item.BatchId == retry.Id).Select(item => item.BankStatementOperationId).SingleAsync());
        db.BankStatementOperations.Remove(await db.BankStatementOperations.SingleAsync());
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(rawId, (await db.BankStatementOperations.SingleAsync()).Id);
    }

    [Fact]
    public void PublicJsonDoesNotBindInternalIdentityOrExposeBankConnectionNavigation()
    {
        var incoming = JsonSerializer.Deserialize<MoneyOperation>("{\"SourceMovementKey\":\"injected-key\"}");
        Assert.Null(incoming!.SourceMovementKey);
        Assert.DoesNotContain("SourceMovementKey", JsonSerializer.Serialize(new MoneyOperation { SourceMovementKey = "internal-key" }));
        var account = new MoneyAccount
        {
            CompanyId = 12,
            Company = new LegalEntity { ShortName = "Private navigation" },
            BankConnectionAccount = new BankConnectionAccount
            {
                BankConnection = new BankConnection { AccessTokenProtected = "synthetic-protected-secret" },
            },
        };
        var json = JsonSerializer.Serialize(account);
        Assert.Contains("\"CompanyId\":12", json);
        Assert.DoesNotContain("Private navigation", json);
        Assert.DoesNotContain("synthetic-protected-secret", json);
        Assert.DoesNotContain("AccessTokenProtected", json);
    }

    private static readonly DateTime Date = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    public static IEnumerable<object[]> ProvenanceDeletionCases
    {
        get
        {
            foreach (var postgres in new[] { false, true })
                foreach (var tracked in new[] { false, true })
                    foreach (var relation in new[]
                    {
                        "account-company", "account-bank", "batch-company", "batch-account",
                        "batch-bank", "batch-parent", "row-bank", "row-origin",
                    })
                        yield return [postgres, tracked, relation];
        }
    }

    [Theory]
    [MemberData(nameof(ProvenanceDeletionCases))]
    public async Task ProvenanceLinksSurvivePrincipalDeletion(bool postgres, bool tracked, string relation)
    {
        await using var fixture = await ModelDatabase.CreateAsync(postgres);
        var db = fixture.Db;
        var company = new LegalEntity { ShortName = "Компания для проверки происхождения" };
        db.LegalEntities.Add(company);
        var account = new MoneyAccount { Name = "Счёт", Source = MoneyOperationSource.Bank };
        var batch = new BankStatementImportBatch { FileName = "root", FileHash = "root" };
        var child = new BankStatementImportBatch { FileName = "child", FileHash = "child" };
        var rootRow = new BankStatementImportRow { ImportKey = "root", RowNumber = 1, Date = Date, Amount = 123m };
        var bankAccount = new BankConnectionAccount
        {
            BankConnection = new BankConnection { Provider = BankProviderKeys.Tochka },
            ExternalAccountId = "native-account",
            AccountNumber = "synthetic-account",
        };
        var raw = new BankStatementOperation { BankConnectionAccount = bankAccount, ExternalOperationId = "native-operation", OperationDate = Date, Amount = 123m };
        await db.SaveChangesAsync();
        bankAccount.BankConnection.LegalEntityId = company.Id;

        object principal;
        switch (relation)
        {
            case "account-company":
                account.CompanyId = company.Id;
                db.MoneyAccounts.Add(account);
                principal = company;
                break;
            case "account-bank":
                account.BankConnectionAccount = bankAccount;
                db.MoneyAccounts.Add(account);
                principal = bankAccount;
                break;
            case "batch-company":
                batch.CompanyId = company.Id;
                db.BankStatementImportBatches.Add(batch);
                principal = company;
                break;
            case "batch-account":
                batch.MoneyAccount = account;
                db.BankStatementImportBatches.Add(batch);
                principal = account;
                break;
            case "batch-bank":
                batch.BankConnectionAccount = bankAccount;
                db.BankStatementImportBatches.Add(batch);
                principal = bankAccount;
                break;
            case "batch-parent":
                child.ParentBatch = batch;
                db.BankStatementImportBatches.Add(child);
                principal = batch;
                break;
            case "row-bank":
                rootRow.BankStatementOperation = raw;
                batch.Rows.Add(rootRow);
                db.BankStatementImportBatches.Add(batch);
                principal = raw;
                break;
            case "row-origin":
                batch.Rows.Add(rootRow);
                child.Rows.Add(new BankStatementImportRow { ImportKey = "child", RowNumber = 1, Date = Date, OriginRow = rootRow });
                db.BankStatementImportBatches.AddRange(batch, child);
                principal = rootRow;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(relation));
        }
        await db.SaveChangesAsync();
        var principalType = principal.GetType();
        var principalId = (int)db.Entry(principal).Property("Id").CurrentValue!;
        var before = await CaptureProvenanceLinksAsync(db);
        if (!tracked) db.ChangeTracker.Clear();
        var loaded = await db.FindAsync(principalType, principalId);
        Assert.NotNull(loaded);
        db.Remove(loaded);
        if (principalType == typeof(LegalEntity))
        {
            await db.SaveChangesAsync();
            Assert.True((bool)db.Entry(loaded).Property("IsDeleted").CurrentValue!);
        }
        else
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        db.ChangeTracker.Clear();
        Assert.Equal(before, await CaptureProvenanceLinksAsync(db));
    }

    private static async Task<string> CaptureProvenanceLinksAsync(PaymentContext db)
        => JsonSerializer.Serialize(new
        {
            Accounts = await db.MoneyAccounts.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.CompanyId, item.BankConnectionAccountId }).ToListAsync(),
            Batches = await db.BankStatementImportBatches.AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.CompanyId, item.MoneyAccountId, item.BankConnectionAccountId, item.ParentBatchId }).ToListAsync(),
            Rows = await db.BankStatementImportRows.AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.BatchId, item.OriginRowId, item.BankStatementOperationId }).ToListAsync(),
            BankAccounts = await db.BankConnectionAccounts.AsNoTracking().OrderBy(item => item.Id).Select(item => item.Id).ToListAsync(),
            BankOperations = await db.BankStatementOperations.AsNoTracking().OrderBy(item => item.Id).Select(item => item.Id).ToListAsync(),
        });

    private static MoneyOperation NewMovement(MoneyAccount account, string key)
        => new()
        {
            MoneyAccountId = account.Id,
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Expense,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 123m,
            Currency = "RUB",
            Date = Date,
            SourceMovementKey = key,
        };

    private static async Task<MoneyAccount> AddAccountAsync(PaymentContext db)
    {
        var company = new LegalEntity { ShortName = "Синтетическая собственная компания" };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        var connection = new BankConnection { LegalEntityId = company.Id, Provider = BankProviderKeys.Tochka };
        var bankAccount = new BankConnectionAccount
        {
            BankConnection = connection,
            ExternalAccountId = "account-1",
            AccountNumber = "synthetic-account",
            Bik = "synthetic-bik",
            Currency = "RUB",
        };
        var account = new MoneyAccount
        {
            CompanyId = company.Id,
            BankConnectionAccount = bankAccount,
            Name = "Банк",
            Source = MoneyOperationSource.Bank,
            BankAccountNumber = bankAccount.AccountNumber,
            BankBik = bankAccount.Bik,
            Currency = "RUB",
        };
        db.MoneyAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    internal sealed class ModelDatabase(PaymentContext db, string? databaseName = null, string? adminConnection = null,
        PostgreSqlModelTemplate.DatabaseLease? modelLease = null) : IAsyncDisposable
    {
        public PaymentContext Db { get; } = db;

        public static async Task<ModelDatabase> CreateFromModelTemplateAsync(bool postgres, PostgreSqlModelTemplate modelTemplate)
        {
            ArgumentNullException.ThrowIfNull(modelTemplate);
            if (!postgres) return await CreateAsync(false);
            var lease = await modelTemplate.CreateDatabaseAsync();
            PostgreSqlPaymentContext? db = null;
            try
            {
                db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                    .UseNpgsql(lease.ConnectionString).Options);
                Assert.Equal("false", await db.SystemSettings.Where(row => row.Key == "notifications.email.enabled")
                    .Select(row => row.Value).SingleAsync());
                return new ModelDatabase(db, modelLease: lease);
            }
            catch (Exception error)
            {
                try
                {
                    try { if (db is not null) await db.DisposeAsync(); }
                    finally { await lease.DisposeAsync(); }
                }
                catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
                throw;
            }
        }

        public static async Task<ModelDatabase> CreateAsync(bool postgres)
        {
            if (!postgres) return new(await SqliteTestContextFactory.CreateAsync());
            var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Provenance model tests require an owned PostgreSQL endpoint.");
            var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
            if (settings.Host is not ("127.0.0.1" or "localhost" or "::1")) throw new InvalidOperationException("Only loopback PostgreSQL is allowed.");
            var adminConnection = settings.ConnectionString;
            var name = "pp_bank_model_" + Guid.NewGuid().ToString("N");
            await using (var admin = new NpgsqlConnection(adminConnection))
            {
                await admin.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
                await create.ExecuteNonQueryAsync();
                Console.WriteLine($"Owned bank-model database created: {name}");
            }
            settings.Database = name;
            var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString).Options);
            var fixture = new ModelDatabase(db, name, adminConnection);
            try
            {
                await db.Database.EnsureCreatedAsync();
                db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
                await db.SaveChangesAsync();
                return fixture;
            }
            catch (Exception error)
            {
                try { await fixture.DisposeAsync(); }
                catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (modelLease is not null)
            {
                try { await Db.DisposeAsync(); }
                finally { await modelLease.DisposeAsync(); }
                return;
            }
            await Db.DisposeAsync();
            if (databaseName is null) return;
            Assert.Matches("^pp_bank_model_[a-f0-9]{32}$", databaseName);
            await using var admin = new NpgsqlConnection(adminConnection!);
            await admin.OpenAsync();
            var started = Stopwatch.GetTimestamp();
            while (true)
            {
                await using var sessions = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @name", admin);
                sessions.Parameters.AddWithValue("name", databaseName);
                if ((long)(await sessions.ExecuteScalarAsync())! == 0) break;
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5))
                    throw new InvalidOperationException($"Owned bank-model database still has active sessions; preserved: {databaseName}");
                await Task.Delay(100);
            }
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\"", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
