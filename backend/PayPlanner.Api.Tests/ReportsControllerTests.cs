using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ReportsControllerTests
{
    [Fact]
    public async Task CashFlow_ReturnsOpeningPeriodAndDailyBalances()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 10_000m, new DateTime(2026, 4, 25), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 4, 28), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 7_000m, new DateTime(2026, 5, 1), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_500m, new DateTime(2026, 5, 2), "Основной банк"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 500m, new DateTime(2026, 5, 2), "Касса"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 3), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Основной банк"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.CashFlow(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CashFlowReportDto>(ok.Value);
        Assert.Equal(new DateTime(2026, 5, 1), report.From);
        Assert.Equal(new DateTime(2026, 5, 31), report.To);
        Assert.Equal(8_000m, report.OpeningBalance);
        Assert.Equal(7_000m, report.Income);
        Assert.Equal(2_000m, report.Expense);
        Assert.Equal(5_000m, report.NetCashFlow);
        Assert.Equal(13_000m, report.ClosingBalance);
        Assert.Equal(1, report.IncomeCount);
        Assert.Equal(2, report.ExpenseCount);

        Assert.Collection(
            report.Days,
            day =>
            {
                Assert.Equal(new DateTime(2026, 5, 1), day.Date);
                Assert.Equal(8_000m, day.OpeningBalance);
                Assert.Equal(7_000m, day.Income);
                Assert.Equal(0m, day.Expense);
                Assert.Equal(15_000m, day.ClosingBalance);
            },
            day =>
            {
                Assert.Equal(new DateTime(2026, 5, 2), day.Date);
                Assert.Equal(15_000m, day.OpeningBalance);
                Assert.Equal(0m, day.Income);
                Assert.Equal(2_000m, day.Expense);
                Assert.Equal(13_000m, day.ClosingBalance);
            });

        var bank = Assert.Single(report.BySource, item => item.Source == MoneyOperationSource.Bank);
        Assert.Equal(8_000m, bank.OpeningBalance);
        Assert.Equal(7_000m, bank.Income);
        Assert.Equal(1_500m, bank.Expense);
        Assert.Equal(13_500m, bank.ClosingBalance);

        var cash = Assert.Single(report.BySource, item => item.Source == MoneyOperationSource.Cash);
        Assert.Equal(0m, cash.OpeningBalance);
        Assert.Equal(500m, cash.Expense);
        Assert.Equal(-500m, cash.ClosingBalance);
    }

    [Fact]
    public async Task CashFlow_FiltersBySourceAndAccountName()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 4, 30), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 6_000m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 5), "Резервный банк"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 4), "Основной банк"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.CashFlow(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            MoneyOperationSource.Bank,
            "  Основной банк  ",
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CashFlowReportDto>(ok.Value);
        Assert.Equal(MoneyOperationSource.Bank, report.Source);
        Assert.Equal("Основной банк", report.AccountName);
        Assert.Equal(4_000m, report.OpeningBalance);
        Assert.Equal(6_000m, report.Income);
        Assert.Equal(0m, report.Expense);
        Assert.Equal(10_000m, report.ClosingBalance);
        Assert.Single(report.BySource);
        var account = Assert.Single(report.ByAccount);
        Assert.Equal("Основной банк", account.AccountName);
    }

    [Fact]
    public async Task Categories_GroupsFactOperationsByIncomeTypeAndCategoryName()
    {
        await using var db = await CreateContextAsync();
        var services = new IncomeType
        {
            Name = "Услуги",
            PaymentType = PaymentType.Income,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var taxes = new IncomeType
        {
            Name = "Налоги",
            PaymentType = PaymentType.Expense,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.IncomeTypes.AddRange(services, taxes);
        await db.SaveChangesAsync();

        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 10_000m, new DateTime(2026, 5, 1), "Основной банк", services.Id),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 2), "Основной банк", services.Id),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Касса", categoryName: "Розница"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 5, 4), "Основной банк", taxes.Id),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 5), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", services.Id));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Categories(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<MoneyCategoryReportDto>(ok.Value);
        Assert.Equal(18_000m, report.IncomeTotal);
        Assert.Equal(5_000m, report.ExpenseTotal);
        Assert.Equal(13_000m, report.NetTotal);

        Assert.Collection(
            report.Income,
            item =>
            {
                Assert.Equal("Услуги", item.CategoryName);
                Assert.Equal(15_000m, item.Amount);
                Assert.Equal(2, item.Count);
                Assert.Equal(83.3m, item.Share);
            },
            item =>
            {
                Assert.Equal("Розница", item.CategoryName);
                Assert.Equal(3_000m, item.Amount);
                Assert.Equal(1, item.Count);
                Assert.Equal(16.7m, item.Share);
            });

        Assert.Collection(
            report.Expense,
            item =>
            {
                Assert.Equal("Налоги", item.CategoryName);
                Assert.Equal(4_000m, item.Amount);
                Assert.Equal(80.0m, item.Share);
            },
            item =>
            {
                Assert.Equal("Без статьи", item.CategoryName);
                Assert.Equal(1_000m, item.Amount);
                Assert.Equal(20.0m, item.Share);
            });
    }

    private static MoneyOperation Operation(
        MoneyOperationSource source,
        MoneyOperationDirection direction,
        MoneyOperationAccountingType accountingType,
        decimal amount,
        DateTime date,
        string accountName,
        int? incomeTypeId = null,
        string? categoryName = null)
        => new()
        {
            Source = source,
            Direction = direction,
            AccountingType = accountingType,
            AccountName = accountName,
            IncomeTypeId = incomeTypeId,
            CategoryName = categoryName,
            Amount = amount,
            Date = date,
            Currency = "RUB",
            CreatedAt = DateTime.UtcNow,
        };

    private static ReportsController CreateController(PaymentContext db)
        => new(db, NullLogger<ReportsController>.Instance);

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
