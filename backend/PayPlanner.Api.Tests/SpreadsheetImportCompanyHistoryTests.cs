using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class SpreadsheetImportServiceTests
{
    public static IEnumerable<object[]> CompanyHistoryReferences()
    {
        string[] references = ["none", "unrelated", "money-account", "inactive-account", "bank-import-batch",
            "bank-connection", "own-registry", "legacy-registry", "seller-payment", "archived-payment",
            "supplier-payment", "expense-party", "seller-act", "act-party", "seller-contract", "branch", "contract-party", "archived-other-client"];
        foreach (var postgres in new[] { false, true })
            foreach (var updatedClient in new[] { false, true })
                foreach (var reference in references)
                    yield return [postgres, updatedClient, reference];
    }

    [Theory]
    [MemberData(nameof(CompanyHistoryReferences))]
    public async Task RollbackAsync_PreservesCompanyUsedByFinancialHistory(bool postgres, bool updatedClient, string reference)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateFromModelTemplateAsync(postgres, bankModelTemplate);
        var db = fixture.Db;
        const string name = "ООО Синтетический импорт";
        if (updatedClient)
        {
            db.Clients.Add(new Client { Name = name });
            await db.SaveChangesAsync();
        }
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var service = new SpreadsheetImportService(new CompanyHistoryContextFactory(db, postgres, accessor), accessor,
            NullLogger<SpreadsheetImportService>.Instance);
        var files = new[] { CreateCounterpartiesFile(
            ["Название", "ИНН", "Контакт", "Телефон", "Email", "Договор", "Отсрочка дн.", "Статья приходов", "Ответственный"],
            [name, "1234567890", "", "", "", "", "", "", ""]) };
        var preview = await service.PreviewAsync(files);
        var row = Assert.Single(Assert.Single(preview.Sections, section => section.Key == "counterparties").Rows);
        var imported = await service.ApplyAsync(files, new SpreadsheetImportApplyRequest
        {
            SelectedPreviewIds = [row.PreviewId],
            CounterpartyOptions = [new() { PreviewId = row.PreviewId, ImportMode = "LegalEntityWithClient" }],
            AllowUpdates = true,
        });
        Assert.Equal(SpreadsheetImportBatchStatus.Applied, imported.Status);
        var importedOperation = Assert.Single(imported.Operations);
        Assert.Equal(updatedClient ? SpreadsheetImportOperationAction.Updated : SpreadsheetImportOperationAction.Created, importedOperation.Action);
        db.ChangeTracker.Clear();
        var client = await db.Clients.AsNoTracking().SingleAsync();
        var company = await db.LegalEntities.AsNoTracking().SingleAsync();
        Assert.Equal(company.Id, client.LegalEntityId);

        var referenceCompanyId = company.Id;
        if (reference == "unrelated")
        {
            var other = new LegalEntity { ShortName = "Другая синтетическая компания" };
            db.LegalEntities.Add(other);
            await db.SaveChangesAsync();
            referenceCompanyId = other.Id;
        }
        if (reference is "money-account" or "inactive-account" or "unrelated")
            db.MoneyAccounts.Add(new MoneyAccount
            {
                Name = "Синтетический денежный счёт",
                Source = MoneyOperationSource.Bank,
                CompanyId = referenceCompanyId,
                IsActive = reference != "inactive-account"
            });
        else if (reference == "bank-import-batch")
            db.BankStatementImportBatches.Add(new BankStatementImportBatch { CompanyId = company.Id, FileName = "synthetic.csv", FileHash = "synthetic-history" });
        else if (reference == "bank-connection")
            db.BankConnections.Add(new BankConnection { LegalEntityId = company.Id, Provider = BankProviderKeys.Tochka });
        else if (reference is "own-registry" or "legacy-registry")
            db.SystemSettings.Add(new SystemSetting
            {
                Key = reference == "own-registry" ? "legalEntities.mainCompanyIds" : "legalEntities.mainCompanyId",
                Value = reference == "own-registry" ? JsonSerializer.Serialize(new[] { company.Id }) : company.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        else if (reference is "seller-payment" or "archived-payment" or "supplier-payment" or "expense-party")
        {
            var payment = new Payment
            {
                Amount = 37m,
                Date = new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc),
                SellerCompanyId = reference is "seller-payment" or "archived-payment" ? company.Id : null,
                LegalEntityId = reference == "supplier-payment" ? company.Id : null,
                ExpenseLegalEntityId = reference == "expense-party" ? company.Id : null
            };
            db.Payments.Add(payment);
        }
        else if (reference is "seller-act" or "act-party")
            db.Acts.Add(new Act
            {
                Number = "SYNTHETIC-HISTORY",
                Amount = 37m,
                Date = new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc),
                SellerCompanyId = reference == "seller-act" ? company.Id : null,
                LegalEntityId = reference == "act-party" ? company.Id : null
            });
        else if (reference == "seller-contract")
            db.Contracts.Add(new Contract { Number = "SYNTHETIC-HISTORY", SellerCompanyId = company.Id });
        else if (reference == "branch")
            db.CompanyBranches.Add(new CompanyBranch { LegalEntityId = company.Id, Name = "Синтетический филиал" });
        else if (reference == "contract-party")
            db.Contracts.Add(new Contract { Number = "SYNTHETIC-PARTY", LegalEntities = [await db.LegalEntities.SingleAsync(item => item.Id == company.Id)] });
        else if (reference == "archived-other-client")
            db.Clients.Add(new Client { Name = "Другой синтетический клиент", LegalEntityId = company.Id });
        await db.SaveChangesAsync();
        if (reference == "archived-payment")
        {
            await db.Payments.Where(item => item.SellerCompanyId == company.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => EF.Property<bool>(item, "IsDeleted"), true));
            Assert.False(await db.Payments.AnyAsync());
        }
        if (reference == "archived-other-client")
        {
            await db.Clients.Where(item => item.Id != client.Id && item.LegalEntityId == company.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => EF.Property<bool>(item, "IsDeleted"), true));
            Assert.Single(await db.Clients.ToArrayAsync());
            Assert.Equal(2, await db.Clients.IgnoreQueryFilters().CountAsync());
        }
        db.ChangeTracker.Clear();
        var financialBefore = await CaptureCompanyHistoryReferencesAsync(db);
        var result = await service.RollbackAsync(imported.Id);
        var operation = Assert.Single(result.Operations);
        db.ChangeTracker.Clear();
        Assert.Equal(financialBefore, await CaptureCompanyHistoryReferencesAsync(db));

        if (reference is "none" or "unrelated")
        {
            Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, result.Status);
            Assert.Equal(1, result.RollbackRows);
            Assert.Equal(updatedClient ? SpreadsheetImportRollbackAction.Restored : SpreadsheetImportRollbackAction.Deleted, operation.RollbackAction);
            Assert.False(await db.LegalEntities.AnyAsync(item => item.Id == company.Id));
            if (updatedClient)
            {
                var restored = await db.Clients.SingleAsync(item => item.Id == client.Id);
                Assert.Equal(name, restored.Name);
                Assert.Null(restored.LegalEntityId);
            }
            else Assert.False(await db.Clients.AnyAsync(item => item.Id == client.Id));
        }
        else
        {
            Assert.Equal(SpreadsheetImportBatchStatus.RolledBackWithConflicts, result.Status);
            Assert.Equal(0, result.RollbackRows);
            Assert.Equal(SpreadsheetImportRollbackAction.Skipped, operation.RollbackAction);
            Assert.Contains("юридическое лицо уже используется", operation.RollbackMessage ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.True(await db.LegalEntities.AnyAsync(item => item.Id == company.Id));
            Assert.Equal(company.Id, (await db.Clients.SingleAsync(item => item.Id == client.Id)).LegalEntityId);
            if (reference == "archived-other-client")
                Assert.Equal(1, await db.Clients.IgnoreQueryFilters().CountAsync(item => item.Id != client.Id
                    && item.LegalEntityId == company.Id && EF.Property<bool>(item, "IsDeleted")));
        }
    }

    private static async Task<string> CaptureCompanyHistoryReferencesAsync(PaymentContext db)
        => JsonSerializer.Serialize(new
        {
            Accounts = await db.MoneyAccounts.AsNoTracking().OrderBy(item => item.Id).Select(item => new { item.Id, item.CompanyId, item.IsActive, item.Currency }).ToArrayAsync(),
            BankImports = await db.BankStatementImportBatches.AsNoTracking().OrderBy(item => item.Id).Select(item => new { item.Id, item.CompanyId, item.FileHash }).ToArrayAsync(),
            Connections = await db.BankConnections.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id).Select(item => new { item.Id, item.LegalEntityId }).ToArrayAsync(),
            Branches = await db.CompanyBranches.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id).Select(item => new { item.Id, item.LegalEntityId, item.Name }).ToArrayAsync(),
            Payments = await db.Payments.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id).Select(item => new { item.Id, item.SellerCompanyId, item.LegalEntityId, item.ExpenseLegalEntityId, item.Amount, Deleted = EF.Property<bool>(item, "IsDeleted") }).ToArrayAsync(),
            Acts = await db.Acts.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id).Select(item => new { item.Id, item.SellerCompanyId, item.LegalEntityId, item.Amount }).ToArrayAsync(),
            Contracts = await db.Contracts.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id).Select(item => new { item.Id, item.SellerCompanyId, Parties = item.LegalEntities.Select(party => party.Id).OrderBy(id => id).ToArray() }).ToArrayAsync(),
            OwnCompanies = await db.SystemSettings.AsNoTracking().Where(item => item.Key == "legalEntities.mainCompanyIds" || item.Key == "legalEntities.mainCompanyId").OrderBy(item => item.Key).Select(item => new { item.Key, item.Value }).ToArrayAsync(),
        });

    private sealed class CompanyHistoryContextFactory(PaymentContext db, bool postgres, IHttpContextAccessor accessor) : IDbContextFactory<PaymentContext>
    {
        public PaymentContext CreateDbContext()
            => postgres
                ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(db.Database.GetConnectionString()).Options, accessor)
                : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(db.Database.GetDbConnection()).Options, accessor);

        public Task<PaymentContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
