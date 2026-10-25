using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    public static IEnumerable<object[]> MoneyReportLinkedSourceCases()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var report in new[] { "cash-orders", "bank-statement-imports" })
                foreach (var filtered in new[] { false, true })
                    foreach (var csv in new[] { false, true })
                        yield return new object[] { postgres, report, filtered, csv };
    }

    [Theory]
    [MemberData(nameof(MoneyReportLinkedSourceCases))]
    public async Task MoneyReportLinkedScope_ViewAllDoesNotGrantAnotherCompany(
        bool postgres, string report, bool filtered, bool csv)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: false);
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        const string name = "SYNTHETIC-SAME-REPORT-ACCOUNT-84872";
        await db.RolePermissions.Where(item => item.RoleId == seed.Actor.RoleId && item.Section == RolePermissionCatalog.Payments)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.CanView, true).SetProperty(item => item.CanExport, true)
                .SetProperty(item => item.ViewScope, DataVisibilityScope.All));
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = seed.Actor.RoleId,
            Section = RolePermissionCatalog.Reports,
            CanView = true,
            CanExport = true,
            ViewScope = DataVisibilityScope.All,
        });
        if (report == "cash-orders")
        {
            MoneyAccount Account(int companyId) => new()
            {
                CompanyId = companyId,
                Name = name,
                Source = MoneyOperationSource.Cash,
                Currency = "RUB",
                IsActive = true,
            };
            var own = Account(seed.Company.Id);
            var foreign = Account(seed.Other.Id);
            db.MoneyAccounts.AddRange(own, foreign);
            await db.SaveChangesAsync();
            CashOrder Order(string number, MoneyAccount account, decimal amount) => new()
            {
                Number = number,
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = BankScopeDate,
                Amount = amount,
                Currency = "RUB",
                Account = name,
                CreatedByUserId = seed.Actor.Id,
                MoneyOperation = new MoneyOperation
                {
                    MoneyAccountId = account.Id,
                    AccountName = name,
                    Source = MoneyOperationSource.Cash,
                    Direction = MoneyOperationDirection.Income,
                    AccountingType = MoneyOperationAccountingType.Fact,
                    Date = BankScopeDate,
                    Amount = amount,
                    Currency = "RUB",
                    ResponsibleUserId = seed.Actor.Id,
                },
            };
            db.CashOrders.AddRange(Order("SYNTHETIC-OWN-84872", own, 10m), Order("SYNTHETIC-FOREIGN-84872", foreign, 20m));
        }
        else
        {
            await db.MoneyAccounts.Where(item => item.Id == seed.Own.Id || item.Id == seed.Foreign.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.Name, name));
            // Use fresh accounts so the persisted labels and their stable IDs agree.
            var own = await db.MoneyAccounts.AsNoTracking().SingleAsync(item => item.Id == seed.Own.Id);
            var foreign = await db.MoneyAccounts.AsNoTracking().SingleAsync(item => item.Id == seed.Foreign.Id);
            var ownBatch = BuildScopeBatch("report-own-84872", own, seed.Actor.Id);
            var foreignBatch = BuildScopeBatch("report-foreign-84872", foreign, seed.Actor.Id);
            ownBatch.CreatedAt = foreignBatch.CreatedAt = BankScopeDate.AddHours(12);
            db.BankStatementImportBatches.AddRange(ownBatch, foreignBatch);
        }
        await db.SaveChangesAsync();
        var before = await CaptureLinkedReportStateAsync(db);
        using var server = fixture.HttpServer();
        using var client = fixture.Client(server, claim: false);
        var date = Uri.EscapeDataString(BankScopeDate.ToString("O", CultureInfo.InvariantCulture));
        var url = csv ? "/api/reports/management.csv?report=" + report : "/api/reports/" + report;
        url += (csv ? "&" : "?") + $"from={date}&to={date}";
        if (filtered) url += "&accountName=" + Uri.EscapeDataString(name);
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var errors = new List<string>();
        if (csv)
        {
            void Metric(string label, decimal expected)
            {
                var prefix = label + ";";
                var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                    .Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                var value = lines.Length == 1 ? lines[0][prefix.Length..].Replace("\u00a0", "").Replace("\u202f", "")
                    .Replace(" ", "").Replace(',', '.') : "";
                if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var actual) || actual != expected)
                    errors.Add($"CSV metric {label} includes another company or lost permitted rows.");
            }
            if (report == "cash-orders")
            {
                Metric("Всего ордеров", 1m);
                Metric("Приход проведен", 10m);
            }
            else
            {
                Metric("Импортов", 1m);
                Metric("Прочитано строк", 1m);
                Metric("Создано операций", 1m);
            }
        }
        else
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (report == "cash-orders")
            {
                if (root.GetProperty("orderCount").GetInt32() != 1) errors.Add("Cash order count includes another company.");
                if (root.GetProperty("postedIncomingTotal").GetDecimal() != 10m) errors.Add("Cash order amount includes another company.");
            }
            else
            {
                if (root.GetProperty("batchCount").GetInt32() != 1) errors.Add("Batch count includes another company.");
                if (root.GetProperty("rowsReadTotal").GetInt32() != 1) errors.Add("Row count includes another company.");
                if (root.GetProperty("createdRowsTotal").GetInt32() != 1) errors.Add("Created count includes another company.");
            }
        }
        await using var verify = fixture.Open();
        Assert.Equal(before, await CaptureLinkedReportStateAsync(verify));
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static async Task<string> CaptureLinkedReportStateAsync(PayPlanner.Api.Data.PaymentContext db)
        => JsonSerializer.Serialize(new
        {
            Bank = await CaptureBankScopeStateAsync(db),
            Money = await CaptureHistoricalInvoiceMoneyAsync(db),
            Orders = await db.CashOrders.AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new
                {
                    item.Id,
                    item.Number,
                    item.Date,
                    item.Amount,
                    item.Currency,
                    item.Status,
                    item.MoneyOperationId,
                    item.PaymentId,
                    item.InvoiceId,
                    item.Account
                }).ToListAsync(),
        });
}
