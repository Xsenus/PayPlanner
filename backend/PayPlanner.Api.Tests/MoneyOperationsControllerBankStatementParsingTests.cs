using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    private const string ParsingOwnAccount = "40702810900000000001";
    private const string ParsingForeignAccount = "40702810900000000002";
    private const string ParsingCounterpartyAccount = "40817810000000000003";

    [Theory]
    [InlineData(false, "ambiguous-amount")]
    [InlineData(true, "ambiguous-amount")]
    [InlineData(false, "excess-precision")]
    [InlineData(true, "excess-precision")]
    [InlineData(false, "foreign-header")]
    [InlineData(true, "foreign-header")]
    [InlineData(false, "foreign-row")]
    [InlineData(true, "foreign-row")]
    public async Task BankStatementParsing_HttpRejectsAmbiguousAmountOrUnownedRowBeforeMoney(bool postgres, string scenario)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var actor = await SeedStatementParsingAsync(db);
        var before = await CaptureStatementParsingMoneyAsync(db);
        using var server = CreateStatementParsingServer(db);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", actor.ToString(CultureInfo.InvariantCulture));
        var content = scenario switch
        {
            "ambiguous-amount" => StatementParsingCsv("1,234"),
            "excess-precision" => StatementParsingCsv("1234.567"),
            "foreign-header" => StatementParsingOneC(ParsingForeignAccount, ParsingForeignAccount, ParsingCounterpartyAccount),
            "foreign-row" => StatementParsingOneC(ParsingOwnAccount, ParsingForeignAccount, ParsingCounterpartyAccount),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        var result = await UploadStatementParsingAsync(client, db, content, scenario.StartsWith("foreign-", StringComparison.Ordinal));

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.RowsRead);
        Assert.Equal(1, result.Skipped);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(before, await CaptureStatementParsingMoneyAsync(db));
        Assert.Empty(await db.MoneyOperations.IgnoreQueryFilters().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BankStatementParsing_HttpMixedSeparatorsPreserveTheActualAmount(bool postgres)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var actor = await SeedStatementParsingAsync(db);
        using var server = CreateStatementParsingServer(db);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", actor.ToString(CultureInfo.InvariantCulture));

        var result = await UploadStatementParsingAsync(client, db, StatementParsingCsv("1,234.56"), false);

        Assert.Equal(1, result.Created);
        Assert.Empty(result.Errors);
        db.ChangeTracker.Clear();
        var operation = await db.MoneyOperations.SingleAsync();
        Assert.Equal(1234.56m, operation.Amount);
        Assert.Equal(MoneyOperationDirection.Income, operation.Direction);
        Assert.Equal((await db.MoneyAccounts.SingleAsync()).Id, operation.MoneyAccountId);
    }

    private static string StatementParsingCsv(string amount)
        => "Дата;Сумма;Контрагент;Назначение;Номер\n" +
           $"18.10.2026;{amount};Синтетический плательщик;Синтетическая операция;18\n";

    public static IEnumerable<object[]> StatementParsingInvalidCases()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var scenario in new[] { "csv-foreign", "csv-number-account", "csv-payer", "csv-recipient", "csv-self",
                     "csv-negative-income", "csv-both-sides", "csv-bad-total-fallback", "csv-bad-income", "csv-conflict",
                     "csv-direction", "csv-empty-account", "csv-duplicate-account", "csv-duplicate-amount",
                     "csv-duplicate-income", "csv-unquoted-comma", "csv-display-name", "date", "missing-selected",
                     "onec-missing-header", "onec-self", "onec-no-parties", "onec-balance-only", "onec-duplicate", "onec-truncated" })
                yield return new object[] { postgres, scenario };
    }

    [Theory]
    [MemberData(nameof(StatementParsingInvalidCases))]
    public Task BankStatementParsing_HttpRetainsRejectedSourceAndCannotReprocessIt(bool postgres, string scenario)
        => AssertStatementParsingRejectedAsync(postgres, scenario);

    [Theory]
    [InlineData(false, "csv-embedded-quotes")]
    [InlineData(true, "csv-embedded-quotes")]
    [InlineData(false, "csv-unclosed-quote")]
    [InlineData(true, "csv-unclosed-quote")]
    [InlineData(false, "csv-after-quote")]
    [InlineData(true, "csv-after-quote")]
    public Task BankStatementParsing_HttpRejectsMalformedCsvQuotes(bool postgres, string scenario)
        => AssertStatementParsingRejectedAsync(postgres, scenario);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BankStatementParsing_HttpMalformedCsvHeaderNeverWritesMoney(bool postgres)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var actor = await SeedStatementParsingAsync(db);
        var before = await CaptureStatementParsingMoneyAsync(db);
        using var server = CreateStatementParsingServer(db);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", actor.ToString(CultureInfo.InvariantCulture));
        using var upload = new MultipartFormDataContent();
        var account = await db.MoneyAccounts.SingleAsync();
        upload.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("\"Дата\"x;Сумма\n18.10.2026;123.45\n")), "file", "quotes.csv");
        upload.Add(new StringContent(account.Id.ToString(CultureInfo.InvariantCulture)), "moneyAccountId");
        upload.Add(new StringContent(account.CompanyId!.Value.ToString(CultureInfo.InvariantCulture)), "companyId");
        using var response = await client.PostAsync("/api/money-operations/bank-statement/import", upload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("кавычк", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await CaptureStatementParsingMoneyAsync(db));
        Assert.Empty(await db.BankStatementImportBatches.ToArrayAsync());
        Assert.Empty(await db.BankStatementImportRows.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BankStatementParsing_HttpCsvQuoteErrorsStayOnTheirOwnRows(bool postgres)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var actor = await SeedStatementParsingAsync(db);
        using var server = CreateStatementParsingServer(db);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", actor.ToString(CultureInfo.InvariantCulture));
        const string content = "Дата;Сумма;Номер;Контрагент\n"
            + "18.10.2026;\"100.01\";Q-1;\"Синтетический \"\"плательщик\"\"\"\n"
            + "18.10.2026;1\"2\"3.45;Q-2;Ошибочная строка\n"
            + "18.10.2026;100.02;Q-3;\"Две\nстроки\"\n";
        var result = await UploadStatementParsingAsync(client, db, content, false);
        Assert.Equal(3, result.RowsRead);
        Assert.Equal(2, result.Created);
        Assert.Equal(1, result.Skipped);
        Assert.Single(result.Errors);
        db.ChangeTracker.Clear();
        var money = await db.MoneyOperations.OrderBy(operation => operation.PaymentOrderNumber).ToArrayAsync();
        Assert.Equal(new[] { 100.01m, 100.02m }, money.Select(operation => operation.Amount));
        Assert.Equal("Синтетический \"плательщик\"", money[0].CounterpartyName);
        Assert.Equal("Две\nстроки", money[1].CounterpartyName);
        var rows = await db.BankStatementImportRows.OrderBy(row => row.RowNumber).ToArrayAsync();
        Assert.Equal(3, rows.Length);
        Assert.Equal(BankStatementImportRowStatus.ClarificationRequired, rows[1].Status);
        Assert.Null(rows[1].MoneyOperationId);
        Assert.Contains("кавычк", rows[1].Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, (await db.BankStatementImportBatches.SingleAsync()).ErrorRows);
        var after = await CaptureStatementParsingMoneyAsync(db);
        var replay = await UploadStatementParsingAsync(client, db, content, false);
        Assert.Equal(result.BatchId, replay.BatchId);
        using var response = await client.PostAsync($"/api/money-operations/bank-statement/imports/{result.BatchId}/reprocess", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<BankStatementImportResultDto>())!.Created);
        Assert.Equal(after, await CaptureStatementParsingMoneyAsync(db));
        Assert.Equal(1, await db.BankStatementImportBatches.CountAsync());
        Assert.Equal(3, await db.BankStatementImportRows.CountAsync());
    }

    private async Task AssertStatementParsingRejectedAsync(bool postgres, string scenario)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var actor = await SeedStatementParsingAsync(db);
        var oneC = scenario.StartsWith("onec-", StringComparison.Ordinal) || scenario == "missing-selected";
        var validOneC = StatementParsingOneC(ParsingOwnAccount, ParsingCounterpartyAccount, ParsingOwnAccount);
        var content = scenario switch
        {
            "csv-foreign" => Csv("Сумма;Расчетный счет", $"123.45;{ParsingForeignAccount}"),
            "csv-number-account" => Csv("Сумма;Номер счета", $"123.45;{ParsingForeignAccount}"),
            "csv-payer" => Csv("Сумма;Счет плательщика;Счет получателя", $"123.45;{ParsingForeignAccount};{ParsingCounterpartyAccount}"),
            "csv-recipient" => Csv("Сумма;Счет получателя", $"123.45;{ParsingForeignAccount}"),
            "csv-self" => Csv("Сумма;Счет плательщика;Счет получателя", $"123.45;{ParsingOwnAccount};{ParsingOwnAccount}"),
            "csv-negative-income" => Csv("Приход;Расход", "-123.45;0"),
            "csv-both-sides" => Csv("Приход;Расход", "123.45;10"),
            "csv-bad-total-fallback" => Csv("Сумма;Приход;Расход", "1,234;123.45;0"),
            "csv-bad-income" => Csv("Приход;Расход", "1,234;123.45"),
            "csv-conflict" => Csv("Сумма;Приход;Расход", "123.45;10;0"),
            "csv-direction" => Csv("Приход;Расход;Счет плательщика;Счет получателя", $"123.45;0;{ParsingOwnAccount};{ParsingForeignAccount}"),
            "csv-empty-account" => Csv("Сумма;Расчетный счет", "123.45;"),
            "csv-duplicate-account" => Csv("Сумма;Счет;Расчетный счет", $"123.45;{ParsingForeignAccount};{ParsingOwnAccount}"),
            "csv-duplicate-amount" => Csv("Сумма;Сумма", "123.45;999.99"),
            "csv-duplicate-income" => Csv("Приход;Приход;Расход", "123.45;999.99;0"),
            "csv-unquoted-comma" => "Дата,Сумма,Контрагент\n18.10.2026,1,234.56,Синтетический плательщик\n",
            "csv-embedded-quotes" => Csv("Сумма", "1\"2\"3.45"),
            "csv-unclosed-quote" => Csv("Сумма", "\"123.45"),
            "csv-after-quote" => Csv("Сумма", "\"123\".45"),
            "csv-display-name" => Csv("Сумма;Расчетный счет", "123.45;Основной банк синтетической компании"),
            "date" => StatementParsingCsv("123.45").Replace("18.10.2026", "не дата", StringComparison.Ordinal),
            "missing-selected" => validOneC,
            "onec-missing-header" => validOneC.Replace($"РасчСчет={ParsingOwnAccount}\n", string.Empty, StringComparison.Ordinal),
            "onec-self" => StatementParsingOneC(ParsingOwnAccount, ParsingOwnAccount, ParsingOwnAccount),
            "onec-no-parties" => StatementParsingOneC(ParsingOwnAccount, "", ""),
            "onec-balance-only" => validOneC.Replace($"РасчСчет={ParsingOwnAccount}", $"РасчСчет={ParsingForeignAccount}\nСекцияРасчСчет\nРасчСчет={ParsingOwnAccount}\nКонецРасчСчета", StringComparison.Ordinal),
            "onec-duplicate" => validOneC.Replace("Сумма=123.45", "Сумма=123.45\nСумма=42.00", StringComparison.Ordinal),
            "onec-truncated" => validOneC.Replace("КонецДокумента\n", string.Empty, StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        if (scenario == "missing-selected")
        {
            var account = await db.MoneyAccounts.SingleAsync();
            account.BankAccountNumber = null;
            account.Name = ParsingOwnAccount;
            await db.SaveChangesAsync();
        }
        var before = await CaptureStatementParsingMoneyAsync(db);
        using var server = CreateStatementParsingServer(db);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", actor.ToString(CultureInfo.InvariantCulture));
        var result = await UploadStatementParsingAsync(client, db, content, oneC);
        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.RowsRead);
        Assert.Equal(1, result.Skipped);
        Assert.NotEmpty(result.Errors);
        db.ChangeTracker.Clear();
        var row = await db.BankStatementImportRows.SingleAsync();
        Assert.Equal(BankStatementImportRowStatus.ClarificationRequired, row.Status);
        Assert.Null(row.MoneyOperationId);
        Assert.NotEmpty(row.Message!);
        Assert.Contains("заново", row.Message);
        Assert.Equal(1, (await db.BankStatementImportBatches.SingleAsync()).ErrorRows);
        var replay = await UploadStatementParsingAsync(client, db, content, oneC);
        Assert.Equal(result.BatchId, replay.BatchId);
        using var response = await client.PostAsync($"/api/money-operations/bank-statement/imports/{result.BatchId}/reprocess", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var retry = (await response.Content.ReadFromJsonAsync<BankStatementImportResultDto>())!;
        Assert.Equal(0, retry.Created);
        Assert.Equal(0, retry.RowsRead);
        Assert.Equal(before, await CaptureStatementParsingMoneyAsync(db));
        Assert.Empty(await db.MoneyOperations.IgnoreQueryFilters().ToArrayAsync());
        Assert.Equal(1, await db.BankStatementImportBatches.CountAsync());
        Assert.Equal(1, await db.BankStatementImportRows.CountAsync());

        static string Csv(string columns, string values) => $"Дата;{columns}\n18.10.2026;{values}\n";
    }

    public static IEnumerable<object[]> StatementParsingValidCases()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var scenario in new[] { "ru", "nbsp", "large-exact", "negative", "expense-columns", "blank-total",
                     "generic-account", "payer-out", "recipient-in", "counterparty-payer", "counterparty-recipient",
                     "counterparty-bank", "payer-correspondent", "quoted-comma", "onec-multiple", "onec-out" })
                yield return new object[] { postgres, scenario };
    }

    [Theory]
    [MemberData(nameof(StatementParsingValidCases))]
    public async Task BankStatementParsing_HttpValidFormatsAndAccountSidesPreserveExactMoney(bool postgres, string scenario)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var actor = await SeedStatementParsingAsync(db);
        using var server = CreateStatementParsingServer(db);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", actor.ToString(CultureInfo.InvariantCulture));
        var content = scenario switch
        {
            "ru" => StatementParsingCsv("1.234,56"),
            "nbsp" => StatementParsingCsv("1\u202F234,56 ₽"),
            "large-exact" => StatementParsingCsv("999999999999.99"),
            "negative" => StatementParsingCsv("-123.45"),
            "expense-columns" => Csv("Сумма;Приход;Расход", "123.45;0;123.45"),
            "blank-total" => Csv("Сумма;Приход;Расход", ";123.45;0"),
            "generic-account" => Csv("Сумма;Номер счета", $"123.45;{ParsingOwnAccount}"),
            "payer-out" => Csv("Сумма;Счет плательщика;Счет получателя", $"123.45;{ParsingOwnAccount};{ParsingForeignAccount}"),
            "recipient-in" => Csv("Сумма;Счет плательщика;Счет получателя", $"123.45;{ParsingForeignAccount};{ParsingOwnAccount}"),
            "counterparty-payer" => Csv("Сумма;Счет плательщика", $"123.45;{ParsingForeignAccount}"),
            "counterparty-recipient" => Csv("Сумма;Счет получателя", $"-123.45;{ParsingForeignAccount}"),
            "counterparty-bank" => Csv("Сумма;Счет контрагента", $"123.45;{ParsingCounterpartyAccount}"),
            "payer-correspondent" => Csv("Сумма;Корреспондентский счет банка плательщика", $"123.45;{ParsingCounterpartyAccount}"),
            "quoted-comma" => "Дата,Сумма,Контрагент\n18.10.2026,\"1,234.56\",Синтетический плательщик\n",
            "onec-multiple" => StatementParsingOneC(ParsingForeignAccount, ParsingCounterpartyAccount, ParsingOwnAccount)
                .Replace($"РасчСчет={ParsingForeignAccount}", $"РасчСчет={ParsingForeignAccount}\nРасчСчет={ParsingOwnAccount}", StringComparison.Ordinal),
            "onec-out" => StatementParsingOneC(ParsingOwnAccount, ParsingOwnAccount, ParsingCounterpartyAccount),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var result = await UploadStatementParsingAsync(client, db, content, scenario.StartsWith("onec-", StringComparison.Ordinal));
        Assert.Equal(1, result.Created);
        Assert.Empty(result.Errors);
        db.ChangeTracker.Clear();
        var operation = await db.MoneyOperations.SingleAsync();
        var expectedAmount = scenario switch { "ru" or "nbsp" or "quoted-comma" => 1234.56m, "large-exact" => 999999999999.99m, _ => 123.45m };
        var expectedDirection = scenario is "negative" or "expense-columns" or "payer-out" or "counterparty-recipient" or "onec-out"
            ? MoneyOperationDirection.Expense : MoneyOperationDirection.Income;
        Assert.Equal(expectedAmount, operation.Amount);
        Assert.Equal(expectedDirection, operation.Direction);
        Assert.Equal((await db.MoneyAccounts.SingleAsync()).Id, operation.MoneyAccountId);
        var row = await db.BankStatementImportRows.SingleAsync();
        Assert.Equal(operation.Id, row.MoneyOperationId);
        Assert.Equal(expectedAmount, row.Amount);
        Assert.Equal(BankStatementImportRowStatus.Created, row.Status);

        static string Csv(string columns, string values) => $"Дата;{columns}\n18.10.2026;{values}\n";
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BankStatementParsing_HttpStorageBoundaryPreservesKopecksOrRejectsBeforeMoney(bool postgres)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var actor = await SeedStatementParsingAsync(db);
        var before = await CaptureStatementParsingMoneyAsync(db);
        using var server = CreateStatementParsingServer(db);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", actor.ToString(CultureInfo.InvariantCulture));
        var result = await UploadStatementParsingAsync(client, db, StatementParsingCsv("9999999999999999.99"), false);
        db.ChangeTracker.Clear();
        if (postgres)
        {
            Assert.Equal(1, result.Created);
            Assert.Empty(result.Errors);
            Assert.Equal(9999999999999999.99m, (await db.MoneyOperations.SingleAsync()).Amount);
            Assert.Equal(9999999999999999.99m, (await db.BankStatementImportRows.SingleAsync()).Amount);
        }
        else
        {
            Assert.Equal(0, result.Created);
            Assert.Equal(1, result.Skipped);
            Assert.Contains(result.Errors, error => error.Contains("9999999999999999.99", StringComparison.Ordinal));
            Assert.Equal(before, await CaptureStatementParsingMoneyAsync(db));
            Assert.Empty(await db.MoneyOperations.IgnoreQueryFilters().ToArrayAsync());
            var row = await db.BankStatementImportRows.SingleAsync();
            Assert.Equal(BankStatementImportRowStatus.ClarificationRequired, row.Status);
            Assert.Equal(0m, row.Amount);
            Assert.Contains("точности", row.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BankStatementParsing_HttpMixedBatchKeepsEveryErrorBeyondResponseLimitAndReplaysSafely(bool postgres)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var actor = await SeedStatementParsingAsync(db);
        using var server = CreateStatementParsingServer(db);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", actor.ToString(CultureInfo.InvariantCulture));
        var content = "Дата;Сумма;Номер\n" + string.Concat(Enumerable.Range(1, 60).Select(index => $"18.10.2026;1,234;ERR-{index}\n"))
            + "18.10.2026;23.45;OK-61\n";
        var result = await UploadStatementParsingAsync(client, db, content, false);
        Assert.Equal(61, result.RowsRead);
        Assert.Equal(1, result.Created);
        Assert.Equal(60, result.Skipped);
        Assert.Equal(50, result.Errors.Count);
        db.ChangeTracker.Clear();
        var batch = await db.BankStatementImportBatches.Include(item => item.Rows).SingleAsync();
        Assert.Equal(60, batch.ErrorRows);
        Assert.Equal(61, batch.Rows.Count);
        Assert.True(batch.ErrorSummary!.Length <= 4000);
        var badRows = batch.Rows.Where(row => row.Status == BankStatementImportRowStatus.ClarificationRequired).ToArray();
        Assert.Equal(60, badRows.Length);
        Assert.All(badRows, row => { Assert.Contains("1,234", row.Message); Assert.Null(row.MoneyOperationId); });
        Assert.Equal(23.45m, (await db.MoneyOperations.SingleAsync()).Amount);
        var after = await CaptureStatementParsingMoneyAsync(db);
        var replay = await UploadStatementParsingAsync(client, db, content, false);
        Assert.Equal(result.BatchId, replay.BatchId);
        Assert.InRange(replay.Errors.Count, 2, 50);
        using var response = await client.PostAsync($"/api/money-operations/bank-statement/imports/{result.BatchId}/reprocess", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<BankStatementImportResultDto>())!.Created);
        Assert.Equal(after, await CaptureStatementParsingMoneyAsync(db));
        Assert.Equal(1, await db.BankStatementImportBatches.CountAsync());
        Assert.Equal(61, await db.BankStatementImportRows.CountAsync());
    }

    [Fact]
    public async Task BankStatementParsing_HttpSqliteStoragePreflightHandlesFullDistinctChunk()
    {
        await using var fixture = await CreateModelDatabaseAsync(false);
        var db = fixture.Db;
        var actor = await SeedStatementParsingAsync(db);
        using var server = CreateStatementParsingServer(db);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", actor.ToString(CultureInfo.InvariantCulture));
        var amounts = Enumerable.Range(1, 501).Select(index => index + 0.01m).ToArray();
        var content = "Дата;Сумма;Номер\n" + string.Concat(amounts.Select((amount, index) =>
            $"18.10.2026;{amount.ToString(CultureInfo.InvariantCulture)};CHUNK-{index}\n"));
        var result = await UploadStatementParsingAsync(client, db, content, false);
        Assert.Equal(501, result.RowsRead);
        Assert.Equal(501, result.Created);
        Assert.Empty(result.Errors);
        Assert.Equal(100, result.Operations.Count);
        db.ChangeTracker.Clear();
        var saved = await db.BankStatementImportRows.OrderBy(row => row.RowNumber)
            .Select(row => new { row.Amount, MoneyAmount = row.MoneyOperation!.Amount }).ToArrayAsync();
        Assert.Equal(amounts, saved.Select(row => row.Amount));
        Assert.Equal(amounts, saved.Select(row => row.MoneyAmount));
    }

    private static string StatementParsingOneC(string header, string payer, string recipient)
        => "1CClientBankExchange\nВерсияФормата=1.03\n" +
           $"РасчСчет={header}\nСекцияДокумент=Платежное поручение\n" +
           "Номер=18\nДата=18.10.2026\nСумма=123.45\n" +
           $"ПлательщикСчет={payer}\nПолучательСчет={recipient}\n" +
           "Плательщик=Синтетический плательщик\nПолучатель=Синтетический получатель\n" +
           "НазначениеПлатежа=Синтетическая операция\nКонецДокумента\nКонецФайла\n";

    private static async Task<int> SeedStatementParsingAsync(PaymentContext db)
    {
        var controller = await CreateBankImportControllerAsync(db, selectedBankAccountNumber: ParsingOwnAccount);
        var actor = int.Parse(controller.User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);
        var account = await db.MoneyAccounts.SingleAsync();
        account.Name = "Основной банк синтетической компании";
        db.Payments.Add(new Payment
        {
            Invoice = new Invoice { Number = "PARSER-18", Date = new DateTime(2026, 10, 18) },
            SellerCompanyId = account.CompanyId,
            Account = "PARSER-18",
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Amount = 500m,
            Status = PaymentStatus.Pending,
            Date = new DateTime(2026, 10, 18),
        });
        await db.SaveChangesAsync();
        return actor;
    }

    private static TestServer CreateStatementParsingServer(PaymentContext db)
        => new(new WebHostBuilder()
            .UseEnvironment("Testing")
            .UseSetting("BackgroundServices:Enabled", "false")
            .UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton(db);
                services.AddAuthentication("bank-import-fixture")
                    .AddScheme<AuthenticationSchemeOptions, BankImportFixtureAuthentication>("bank-import-fixture", _ => { });
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(MoneyOperationsController).Assembly);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            }));

    private static async Task<BankStatementImportResultDto> UploadStatementParsingAsync(
        HttpClient client, PaymentContext db, string content, bool oneC)
    {
        var account = await db.MoneyAccounts.AsNoTracking().SingleAsync();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", oneC ? "synthetic.1c" : "synthetic.csv");
        form.Add(new StringContent(account.CompanyId!.Value.ToString(CultureInfo.InvariantCulture)), "companyId");
        form.Add(new StringContent(account.Id.ToString(CultureInfo.InvariantCulture)), "moneyAccountId");
        using var response = await client.PostAsync("/api/money-operations/bank-statement/import", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BankStatementImportResultDto>())!;
    }

    private static async Task<string> CaptureStatementParsingMoneyAsync(PaymentContext db)
    {
        using var state = JsonDocument.Parse(await CaptureBankImportStateAsync(db));
        return string.Join("|", new[] { "Operations", "Payments", "InvoiceCount", "CashOrderCount" }
            .Select(name => state.RootElement.GetProperty(name).GetRawText()));
    }
}
