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

    [Fact]
    public async Task Counterparties_GroupsFactOperationsByCounterparty()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 10_000m, new DateTime(2026, 5, 1), "Основной банк", counterpartyName: "ООО Ромашка"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 2), "Основной банк", counterpartyName: "  ооо ромашка  "),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 3), "Основной банк", counterpartyName: "ООО Ромашка"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 5, 4), "Касса", counterpartyName: "ИП Петров"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 5, 5), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 6), "Основной банк", counterpartyName: "ООО Ромашка"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Counterparties(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CounterpartyReportDto>(ok.Value);
        Assert.Equal(19_000m, report.IncomeTotal);
        Assert.Equal(6_000m, report.ExpenseTotal);
        Assert.Equal(13_000m, report.NetTotal);

        Assert.Collection(
            report.Counterparties,
            item =>
            {
                Assert.Equal("ООО Ромашка", item.CounterpartyName);
                Assert.Equal(15_000m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(13_000m, item.NetTotal);
                Assert.Equal(17_000m, item.TotalAmount);
                Assert.Equal(2, item.IncomeCount);
                Assert.Equal(1, item.ExpenseCount);
                Assert.Equal(68.0m, item.Share);
            },
            item =>
            {
                Assert.Equal("Без контрагента", item.CounterpartyName);
                Assert.Equal(4_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(16.0m, item.Share);
            },
            item =>
            {
                Assert.Equal("ИП Петров", item.CounterpartyName);
                Assert.Equal(0m, item.Income);
                Assert.Equal(4_000m, item.Expense);
                Assert.Equal(16.0m, item.Share);
            });
    }

    [Fact]
    public async Task Projects_GroupsFactOperationsByProjectName()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 12_000m, new DateTime(2026, 5, 1), "Основной банк", projectName: "Проект Альфа"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 2), "Касса", projectName: " проект альфа "),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 3), "Основной банк", projectName: "Проект Бета"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", projectName: "Проект Альфа"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Projects(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<ProjectReportDto>(ok.Value);
        Assert.Equal(17_000m, report.IncomeTotal);
        Assert.Equal(5_000m, report.ExpenseTotal);
        Assert.Equal(12_000m, report.NetTotal);

        Assert.Collection(
            report.Projects,
            item =>
            {
                Assert.Equal("Проект Альфа", item.ProjectName);
                Assert.Equal(12_000m, item.Income);
                Assert.Equal(3_000m, item.Expense);
                Assert.Equal(9_000m, item.NetTotal);
                Assert.Equal(15_000m, item.TotalAmount);
                Assert.Equal(68.2m, item.Share);
            },
            item =>
            {
                Assert.Equal("Проект Бета", item.ProjectName);
                Assert.Equal(5_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(22.7m, item.Share);
            },
            item =>
            {
                Assert.Equal("Без проекта", item.ProjectName);
                Assert.Equal(0m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(9.1m, item.Share);
            });
    }

    [Fact]
    public async Task Directions_GroupsFactOperationsByDirectionName()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 9_000m, new DateTime(2026, 5, 1), "Основной банк", directionName: "Консалтинг"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Касса", directionName: " консалтинг "),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 5, 3), "Основной банк", directionName: "Поддержка"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 4), "Основной банк", directionName: "Консалтинг"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", directionName: "Консалтинг"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Directions(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<DirectionReportDto>(ok.Value);
        Assert.Equal(13_000m, report.IncomeTotal);
        Assert.Equal(3_000m, report.ExpenseTotal);
        Assert.Equal(10_000m, report.NetTotal);

        Assert.Collection(
            report.Directions,
            item =>
            {
                Assert.Equal("Консалтинг", item.DirectionName);
                Assert.Equal(9_000m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(7_000m, item.NetTotal);
                Assert.Equal(11_000m, item.TotalAmount);
                Assert.Equal(68.8m, item.Share);
            },
            item =>
            {
                Assert.Equal("Поддержка", item.DirectionName);
                Assert.Equal(4_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(25.0m, item.Share);
            },
            item =>
            {
                Assert.Equal("Без направления", item.DirectionName);
                Assert.Equal(0m, item.Income);
                Assert.Equal(1_000m, item.Expense);
                Assert.Equal(6.3m, item.Share);
            });
    }

    [Fact]
    public async Task SalesPoints_GroupsFactOperationsBySalesPointName()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 8_000m, new DateTime(2026, 5, 1), "Основной банк", salesPointName: "Офис"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_500m, new DateTime(2026, 5, 2), "Касса", salesPointName: " офис "),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_500m, new DateTime(2026, 5, 3), "Основной банк", salesPointName: "Онлайн"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", salesPointName: "Офис"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.SalesPoints(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<SalesPointReportDto>(ok.Value);
        Assert.Equal(11_500m, report.IncomeTotal);
        Assert.Equal(2_500m, report.ExpenseTotal);
        Assert.Equal(9_000m, report.NetTotal);

        Assert.Collection(
            report.SalesPoints,
            item =>
            {
                Assert.Equal("Офис", item.SalesPointName);
                Assert.Equal(8_000m, item.Income);
                Assert.Equal(1_500m, item.Expense);
                Assert.Equal(6_500m, item.NetTotal);
                Assert.Equal(9_500m, item.TotalAmount);
                Assert.Equal(67.9m, item.Share);
            },
            item =>
            {
                Assert.Equal("Онлайн", item.SalesPointName);
                Assert.Equal(3_500m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(25.0m, item.Share);
            },
            item =>
            {
                Assert.Equal("Без точки продаж", item.SalesPointName);
                Assert.Equal(0m, item.Income);
                Assert.Equal(1_000m, item.Expense);
                Assert.Equal(7.1m, item.Share);
            });
    }

    [Fact]
    public async Task Vat_GroupsFactOperationsByVatRate()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 12_000m, new DateTime(2026, 5, 1), "Основной банк", vatRate: 20m, vatAmount: 2_000m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 5_500m, new DateTime(2026, 5, 2), "Основной банк", vatRate: 10m, vatAmount: 500m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 6_000m, new DateTime(2026, 5, 3), "Основной банк", vatRate: 20m, vatAmount: 1_000m),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 4), "Касса"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", vatRate: 20m, vatAmount: 16_666.50m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", vatRate: 20m, vatAmount: 8_333.33m));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Vat(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<VatReportDto>(ok.Value);
        Assert.Equal(17_500m, report.IncomeAmountTotal);
        Assert.Equal(2_500m, report.IncomeVatTotal);
        Assert.Equal(9_000m, report.ExpenseAmountTotal);
        Assert.Equal(1_000m, report.ExpenseVatTotal);
        Assert.Equal(1_500m, report.VatBalance);

        Assert.Collection(
            report.Rates,
            item =>
            {
                Assert.Equal(20m, item.VatRate);
                Assert.Equal("20%", item.VatRateLabel);
                Assert.Equal(10_000m, item.IncomeBase);
                Assert.Equal(2_000m, item.IncomeVat);
                Assert.Equal(12_000m, item.IncomeGross);
                Assert.Equal(5_000m, item.ExpenseBase);
                Assert.Equal(1_000m, item.ExpenseVat);
                Assert.Equal(6_000m, item.ExpenseGross);
                Assert.Equal(1_000m, item.NetVat);
            },
            item =>
            {
                Assert.Equal(10m, item.VatRate);
                Assert.Equal("10%", item.VatRateLabel);
                Assert.Equal(5_000m, item.IncomeBase);
                Assert.Equal(500m, item.IncomeVat);
                Assert.Equal(5_500m, item.IncomeGross);
                Assert.Equal(500m, item.NetVat);
            },
            item =>
            {
                Assert.Null(item.VatRate);
                Assert.Equal("Без НДС", item.VatRateLabel);
                Assert.Equal(3_000m, item.ExpenseBase);
                Assert.Equal(0m, item.ExpenseVat);
                Assert.Equal(-0m, item.NetVat);
            });
    }

    [Fact]
    public async Task PlanFact_ComparesPlanAndFactOperations()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 10_000m, new DateTime(2026, 5, 1), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 8_000m, new DateTime(2026, 5, 1), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Plan, 4_000m, new DateTime(2026, 5, 2), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 2), "Основной банк"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 3), "Касса"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 3), "Основной банк"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.PlanFact(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            MoneyOperationSource.Bank,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<PlanFactReportDto>(ok.Value);
        Assert.Equal(MoneyOperationSource.Bank, report.Source);
        Assert.Equal(10_000m, report.PlanIncome);
        Assert.Equal(8_000m, report.FactIncome);
        Assert.Equal(-2_000m, report.IncomeVariance);
        Assert.Equal(80.0m, report.IncomeCompletionPercent);
        Assert.Equal(4_000m, report.PlanExpense);
        Assert.Equal(5_000m, report.FactExpense);
        Assert.Equal(1_000m, report.ExpenseVariance);
        Assert.Equal(125.0m, report.ExpenseExecutionPercent);
        Assert.Equal(6_000m, report.PlanNetTotal);
        Assert.Equal(3_000m, report.FactNetTotal);
        Assert.Equal(-3_000m, report.NetVariance);

        Assert.Collection(
            report.Days,
            day =>
            {
                Assert.Equal(new DateTime(2026, 5, 1), day.Date);
                Assert.Equal(10_000m, day.PlanIncome);
                Assert.Equal(8_000m, day.FactIncome);
                Assert.Equal(-2_000m, day.IncomeVariance);
                Assert.Equal(10_000m, day.PlanNetTotal);
                Assert.Equal(8_000m, day.FactNetTotal);
            },
            day =>
            {
                Assert.Equal(new DateTime(2026, 5, 2), day.Date);
                Assert.Equal(4_000m, day.PlanExpense);
                Assert.Equal(5_000m, day.FactExpense);
                Assert.Equal(1_000m, day.ExpenseVariance);
                Assert.Equal(-4_000m, day.PlanNetTotal);
                Assert.Equal(-5_000m, day.FactNetTotal);
            });
    }

    [Fact]
    public async Task Advances_GroupsAdvanceFactOperationsByCounterpartyAndRecognition()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 10_000m, new DateTime(2026, 5, 1), "Основной банк", counterpartyName: "ООО Ромашка", isAdvancePayment: true, recognitionDate: new DateTime(2026, 5, 10)),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 6_000m, new DateTime(2026, 5, 2), "Основной банк", counterpartyName: "ООО Ромашка", isAdvancePayment: true),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 4_000m, new DateTime(2026, 5, 3), "Основной банк", counterpartyName: "ИП Петров", isAdvancePayment: true, recognitionDate: new DateTime(2026, 6, 1)),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 4), "Касса", isAdvancePayment: true),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", counterpartyName: "ООО Ромашка", isAdvancePayment: true),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 7_000m, new DateTime(2026, 5, 6), "Основной банк", counterpartyName: "ООО Ромашка"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Advances(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<AdvanceReportDto>(ok.Value);
        Assert.Equal(19_000m, report.IncomeTotal);
        Assert.Equal(4_000m, report.ExpenseTotal);
        Assert.Equal(10_000m, report.RecognizedTotal);
        Assert.Equal(13_000m, report.UnrecognizedTotal);
        Assert.Equal(1, report.RecognizedCount);
        Assert.Equal(3, report.UnrecognizedCount);

        Assert.Collection(
            report.Counterparties,
            item =>
            {
                Assert.Equal("ООО Ромашка", item.CounterpartyName);
                Assert.Equal(16_000m, item.IncomeTotal);
                Assert.Equal(0m, item.ExpenseTotal);
                Assert.Equal(10_000m, item.RecognizedTotal);
                Assert.Equal(6_000m, item.UnrecognizedTotal);
                Assert.Equal(2, item.IncomeCount);
            },
            item =>
            {
                Assert.Equal("ИП Петров", item.CounterpartyName);
                Assert.Equal(0m, item.IncomeTotal);
                Assert.Equal(4_000m, item.ExpenseTotal);
                Assert.Equal(0m, item.RecognizedTotal);
                Assert.Equal(4_000m, item.UnrecognizedTotal);
            },
            item =>
            {
                Assert.Equal("Без контрагента", item.CounterpartyName);
                Assert.Equal(3_000m, item.IncomeTotal);
                Assert.Equal(3_000m, item.UnrecognizedTotal);
            });
    }

    [Fact]
    public async Task PaymentPriorities_GroupsFactOperationsByPriority()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 9_000m, new DateTime(2026, 5, 1), "Основной банк", paymentPriority: 1),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 2), "Основной банк", paymentPriority: 1),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 3), "Основной банк", paymentPriority: 2),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Касса"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", paymentPriority: 1),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", paymentPriority: 1));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.PaymentPriorities(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<PaymentPriorityReportDto>(ok.Value);
        Assert.Equal(10_000m, report.IncomeTotal);
        Assert.Equal(5_000m, report.ExpenseTotal);
        Assert.Equal(5_000m, report.NetTotal);

        Assert.Collection(
            report.Priorities,
            item =>
            {
                Assert.Equal(1, item.Priority);
                Assert.Equal("Высокий приоритет", item.PriorityLabel);
                Assert.Equal(9_000m, item.Income);
                Assert.Equal(3_000m, item.Expense);
                Assert.Equal(6_000m, item.NetTotal);
                Assert.Equal(12_000m, item.TotalAmount);
                Assert.Equal(80.0m, item.Share);
            },
            item =>
            {
                Assert.Equal(2, item.Priority);
                Assert.Equal("Средний приоритет", item.PriorityLabel);
                Assert.Equal(0m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(13.3m, item.Share);
            },
            item =>
            {
                Assert.Null(item.Priority);
                Assert.Equal("Без приоритета", item.PriorityLabel);
                Assert.Equal(1_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(6.7m, item.Share);
            });
    }

    [Fact]
    public async Task PaidAccounts_GroupsFactOperationsByPaidAccountName()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 7_000m, new DateTime(2026, 5, 1), "Основной банк", paidAccountName: "Расчетный счет"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", paidAccountName: " расчетный счет "),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Касса", paidAccountName: "Касса"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", paidAccountName: "Расчетный счет"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", paidAccountName: "Расчетный счет"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.PaidAccounts(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<PaidAccountReportDto>(ok.Value);
        Assert.Equal(8_000m, report.IncomeTotal);
        Assert.Equal(5_000m, report.ExpenseTotal);
        Assert.Equal(3_000m, report.NetTotal);

        Assert.Collection(
            report.Accounts,
            item =>
            {
                Assert.Equal("Расчетный счет", item.PaidAccountName);
                Assert.Equal(7_000m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(5_000m, item.NetTotal);
                Assert.Equal(9_000m, item.TotalAmount);
                Assert.Equal(69.2m, item.Share);
            },
            item =>
            {
                Assert.Equal("Касса", item.PaidAccountName);
                Assert.Equal(0m, item.Income);
                Assert.Equal(3_000m, item.Expense);
                Assert.Equal(23.1m, item.Share);
            },
            item =>
            {
                Assert.Equal("Без счета оплаты", item.PaidAccountName);
                Assert.Equal(1_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(7.7m, item.Share);
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
        string? categoryName = null,
        string? counterpartyName = null,
        string? projectName = null,
        string? directionName = null,
        string? salesPointName = null,
        decimal? vatRate = null,
        decimal? vatAmount = null,
        bool isAdvancePayment = false,
        DateTime? recognitionDate = null,
        int? paymentPriority = null,
        string? paidAccountName = null)
        => new()
        {
            Source = source,
            Direction = direction,
            AccountingType = accountingType,
            AccountName = accountName,
            CounterpartyName = counterpartyName,
            IncomeTypeId = incomeTypeId,
            CategoryName = categoryName,
            ProjectName = projectName,
            DirectionName = directionName,
            SalesPointName = salesPointName,
            VatRate = vatRate,
            VatAmount = vatAmount,
            Amount = amount,
            Date = date,
            RecognitionDate = recognitionDate,
            IsAdvancePayment = isAdvancePayment,
            PaymentPriority = paymentPriority,
            PaidAccountName = paidAccountName,
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
