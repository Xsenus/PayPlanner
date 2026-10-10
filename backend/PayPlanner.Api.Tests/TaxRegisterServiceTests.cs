using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Tax;
using Xunit;

namespace PayPlanner.Api.Tests;

public class TaxRegisterServiceTests
{
    private static async Task<(PaymentContext Db, TaxRegisterService Service, int CompanyId)> CreateAsync()
    {
        var sqlite = new SqliteConnection("Data Source=:memory:");
        await sqlite.OpenAsync();
        var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(sqlite).Options);
        await db.Database.EnsureCreatedAsync();
        var company = new LegalEntity { ShortName = "Своя", Inn = "7707083893" };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        return (db, new TaxRegisterService(db), company.Id);
    }

    private static TaxRegisterEntryInput Input(int companyId, string type = "Income", decimal amount = 1000m, string? doc = "Счёт № 1", int year = 2025, int? bankOp = null)
        => new(companyId, type, new DateTime(year, 3, 15), amount, doc, null, "Оплата", null, null, false, bankOp);

    [Fact]
    public async Task Approve_RequiresDocument_AndMovesToApprovedWithHistory()
    {
        var (db, service, company) = await CreateAsync();
        await using var _ = db;
        var created = (await service.CreateAsync(Input(company, doc: null), 1, default)).Value!;

        var denied = await service.ApproveAsync(created.Id, 1, default);
        Assert.False(denied.Ok);

        await service.UpdateDraftAsync(created.Id, Input(company), 1, default);
        var approved = await service.ApproveAsync(created.Id, 7, default);

        Assert.True(approved.Ok);
        Assert.Equal(TaxRegisterStatuses.Approved, approved.Value!.Status);
        Assert.Equal(7, approved.Value.ApprovedByUserId);
        Assert.Equal(["Created", "Approved"], db.TaxRegisterEvents.OrderBy(e => e.Id).Select(e => e.Action).ToList());
    }

    [Fact]
    public async Task ApprovedEntry_CannotBeEditedOrArchived_ButCanBeCorrectedOnce()
    {
        var (db, service, company) = await CreateAsync();
        await using var _ = db;
        var entry = (await service.CreateAsync(Input(company), 1, default)).Value!;
        await service.ApproveAsync(entry.Id, 1, default);

        Assert.Equal(409, (await service.UpdateDraftAsync(entry.Id, Input(company, amount: 5m), 1, default)).Status);
        Assert.Equal(409, (await service.ArchiveAsync(entry.Id, 1, null, default)).Status);
        Assert.False((await service.CorrectAsync(entry.Id, " ", 1, default)).Ok);

        var correction = (await service.CorrectAsync(entry.Id, "Ошибка суммы", 1, default)).Value!;
        Assert.Equal(-1, correction.Sign);
        Assert.Equal(entry.Id, correction.CorrectsEntryId);
        Assert.Equal(TaxRegisterStatuses.Approved, correction.Status);
        Assert.Equal(409, (await service.CorrectAsync(entry.Id, "ещё раз", 1, default)).Status);
        Assert.Equal(409, (await service.CorrectAsync(correction.Id, "корректировку не исправить", 1, default)).Status);

        var book = await service.BuildBookAsync(company, 2025, default);
        Assert.Equal(0m, book.Totals.Income);
        Assert.Equal(2, book.Totals.Entries);
        Assert.Equal(1000m, (await db.TaxRegisterEntries.FindAsync(entry.Id))!.Amount);
    }

    [Fact]
    public async Task Book_CountsOnlyApprovedEntriesOfYear_WithSignedTotals()
    {
        var (db, service, company) = await CreateAsync();
        await using var _ = db;
        var income = (await service.CreateAsync(Input(company, "Income", 1000m), 1, default)).Value!;
        var expense = (await service.CreateAsync(Input(company, "Expense", 300.555m), 1, default)).Value!;
        await service.CreateAsync(Input(company, "Income", 999m), 1, default); // черновик
        await service.CreateAsync(Input(company, "Income", 777m, year: 2024), 1, default);
        await service.ApproveAsync(income.Id, 1, default);
        await service.ApproveAsync(expense.Id, 1, default);

        var book = await service.BuildBookAsync(company, 2025, default);

        Assert.Equal(1000m, book.Totals.Income);
        Assert.Equal(300.56m, book.Totals.Expense);
        Assert.Equal(699.44m, book.Totals.TaxBase);
        Assert.Equal(2, book.Entries.Count);
        var csv = TaxRegisterService.ToCsv(book);
        Assert.Contains("Итого доходов;1000.00", csv);
    }

    [Fact]
    public async Task BankOperation_GivesAtMostOneEntry_AndTakesValuesFromStatement()
    {
        var (db, service, company) = await CreateAsync();
        await using var _ = db;
        var connection = new BankConnection { LegalEntityId = company, Provider = "sber" };
        var account = new BankConnectionAccount { BankConnection = connection, ExternalAccountId = "a", AccountNumber = "40702810200000000001" };
        var operation = new BankStatementOperation { BankConnectionAccount = account, ExternalOperationId = "op1", OperationDate = new DateTime(2025, 5, 2), Amount = 250m, IsIncome = false };
        var pending = new BankStatementOperation { BankConnectionAccount = account, ExternalOperationId = "op2", OperationDate = new DateTime(2025, 5, 3), Amount = 10m, IsIncome = true, IsBooked = false };
        db.BankStatementOperations.AddRange(operation, pending);
        await db.SaveChangesAsync();

        var first = await service.CreateAsync(Input(company, "Income", 1m, bankOp: operation.Id), 1, default);
        var second = await service.CreateAsync(Input(company, bankOp: operation.Id), 1, default);
        var notBooked = await service.CreateAsync(Input(company, bankOp: pending.Id), 1, default);

        Assert.True(first.Ok);
        Assert.Equal("Expense", first.Value!.Type);
        Assert.Equal(250m, first.Value.Amount);
        Assert.Equal(new DateTime(2025, 5, 2), first.Value.OperationDate);
        Assert.Equal(409, second.Status);
        Assert.Equal(409, notBooked.Status);
    }

    [Fact]
    public async Task ClosedYear_BlocksApprovalCreationAndCorrection_AndRequiresNoDrafts()
    {
        var (db, service, company) = await CreateAsync();
        await using var _ = db;
        var approved = (await service.CreateAsync(Input(company), 1, default)).Value!;
        await service.ApproveAsync(approved.Id, 1, default);
        var draft = (await service.CreateAsync(Input(company), 1, default)).Value!;

        Assert.Equal(409, (await service.CloseYearAsync(company, 2025, 1, default)).Status);
        await service.ArchiveAsync(draft.Id, 1, "лишняя", default);
        var closed = await service.CloseYearAsync(company, 2025, 1, default);

        Assert.True(closed.Ok);
        Assert.Equal(64, closed.Value!.DataHash.Length);
        Assert.Equal(409, (await service.CreateAsync(Input(company), 1, default)).Status);
        Assert.Equal(409, (await service.CorrectAsync(approved.Id, "поздно", 1, default)).Status);
        Assert.Equal(409, (await service.CloseYearAsync(company, 2025, 1, default)).Status);
        Assert.True((await service.BuildBookAsync(company, 2025, default)).IsClosed);
    }

    [Fact]
    public async Task Create_RejectsBadInput()
    {
        var (db, service, company) = await CreateAsync();
        await using var _ = db;

        Assert.False((await service.CreateAsync(Input(company, "Transfer"), 1, default)).Ok);
        Assert.False((await service.CreateAsync(Input(company, amount: 0m), 1, default)).Ok);
        Assert.Equal(404, (await service.CreateAsync(Input(999), 1, default)).Status);
    }

    [Fact]
    public void Csv_NeutralisesFormulaInjection()
    {
        var entry = new TaxRegisterEntry { Id = 1, OperationDate = new DateTime(2025, 1, 1), Direction = "Income", Amount = 1m, Sign = 1, DocumentRef = "=HYPERLINK(\"x\")", Description = "a;b" };
        var csv = TaxRegisterService.ToCsv(new TaxRegisterBook(1, 2025, false, [entry], new TaxRegisterBookTotals(1m, 0m, 1m, 1)));

        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv);
        Assert.Contains("\"a;b\"", csv);
    }

    [Fact]
    public async Task Journal_ListsEventsOfCompanyWithFiltersAndPaging()
    {
        var (db, service, company) = await CreateAsync();
        await using var _ = db;
        var first = (await service.CreateAsync(Input(company), 1, default)).Value!;
        await service.ApproveAsync(first.Id, 1, default);
        await service.CreateAsync(Input(company, "Expense", 50m), 1, default);

        var all = await service.GetJournalAsync(company, null, null, null, 1, 50, default);
        var approvals = await service.GetJournalAsync(company, null, null, "Approved", 1, 50, default);
        var paged = await service.GetJournalAsync(company, null, null, null, 2, 2, default);
        var foreign = await service.GetJournalAsync(company + 1, null, null, null, 1, 50, default);

        Assert.Equal(3, all.Total);
        Assert.Single(approvals.Items);
        Assert.Equal(1000m, approvals.Items[0].SignedAmount);
        Assert.Single(paged.Items);
        Assert.Equal(3, paged.Total);
        Assert.Equal(0, foreign.Total);
    }

    [Fact]
    public async Task DatabaseRefusesSecondCorrectionOfSameEntry()
    {
        var (db, service, company) = await CreateAsync();
        await using var _ = db;
        var entry = (await service.CreateAsync(Input(company), 1, default)).Value!;
        await service.ApproveAsync(entry.Id, 1, default);
        await service.CorrectAsync(entry.Id, "первая", 1, default);

        db.TaxRegisterEntries.Add(new TaxRegisterEntry { LegalEntityId = company, OperationDate = new DateTime(2025, 3, 15), Amount = 1m, Type = "Correction", Direction = "Income", Sign = -1, Status = "Approved", CorrectsEntryId = entry.Id });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void Xlsx_ContainsRowsTotalsAndEscapesText()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var entry = new TaxRegisterEntry { Id = 1, OperationDate = new DateTime(2025, 1, 1), Direction = "Income", Amount = 12.5m, Sign = 1, DocumentRef = "=A1 & <b>", Description = "x\u0001y" };
        var bytes = TaxRegisterXlsxWriter.Build(new TaxRegisterBook(1, 2025, false, [entry], new TaxRegisterBookTotals(12.5m, 0m, 12.5m, 1)));

        using var reader = ExcelDataReader.ExcelReaderFactory.CreateReader(new MemoryStream(bytes));
        var rows = new List<string?[]>();
        while (reader.Read())
        {
            rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i)?.ToString()).ToArray());
        }

        Assert.Equal("Книга доходов и расходов, 2025 год", rows[0][0]);
        Assert.Equal("12.5", rows[2][4]);
        Assert.Equal("=A1 & <b>", rows[2][5]);
        Assert.Equal("xy", rows[2][6]);
        Assert.Equal("Разность", rows[^1][3]);
    }
}
