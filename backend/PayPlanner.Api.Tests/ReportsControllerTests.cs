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

    [Fact]
    public async Task PaidInvoices_GroupsFactOperationsByPaidInvoiceNumber()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 5_000m, new DateTime(2026, 5, 1), "Основной банк", paidInvoiceNumber: "INV-10", paidAmount: 4_500m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", paidInvoiceNumber: " inv-10 ", paidAmount: 1_900m),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Касса", paidInvoiceNumber: "INV-11"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Основной банк", paidAmount: 800m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", paidInvoiceNumber: "INV-10", paidAmount: 99_999m),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", paidInvoiceNumber: "INV-10", paidAmount: 50_000m));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.PaidInvoices(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<PaidInvoiceReportDto>(ok.Value);
        Assert.Equal(9_000m, report.IncomeTotal);
        Assert.Equal(2_000m, report.ExpenseTotal);
        Assert.Equal(7_000m, report.NetTotal);
        Assert.Equal(10_200m, report.PaidAmountTotal);

        Assert.Collection(
            report.Invoices,
            item =>
            {
                Assert.Equal("INV-10", item.PaidInvoiceNumber);
                Assert.Equal(5_000m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(3_000m, item.NetTotal);
                Assert.Equal(7_000m, item.OperationTotal);
                Assert.Equal(6_400m, item.PaidAmountTotal);
                Assert.Equal(63.6m, item.Share);
            },
            item =>
            {
                Assert.Equal("INV-11", item.PaidInvoiceNumber);
                Assert.Equal(3_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(3_000m, item.PaidAmountTotal);
                Assert.Equal(27.3m, item.Share);
            },
            item =>
            {
                Assert.Equal("Без номера счета", item.PaidInvoiceNumber);
                Assert.Equal(1_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(800m, item.PaidAmountTotal);
                Assert.Equal(9.1m, item.Share);
            });
    }

    [Fact]
    public async Task InvoiceStatuses_GroupsInvoicesByWorkflowStatus()
    {
        await using var db = await CreateContextAsync();
        db.Payments.AddRange(
            InvoicePayment("INV-1", PaymentType.Income, PaymentStatus.Pending, InvoiceWorkflowStatus.Draft, 1_000m, 0m, new DateTime(2026, 5, 1)),
            InvoicePayment("INV-2", PaymentType.Expense, PaymentStatus.Processing, InvoiceWorkflowStatus.OnSigning, 2_000m, 500m, new DateTime(2026, 5, 2)),
            InvoicePayment("INV-3", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 3_000m, 3_000m, new DateTime(2026, 5, 3)),
            InvoicePayment("INV-4", PaymentType.Income, PaymentStatus.Pending, InvoiceWorkflowStatus.PartiallyPaid, 4_000m, 1_500m, new DateTime(2026, 5, 4)),
            InvoicePayment("INV-5", PaymentType.Income, PaymentStatus.Overdue, InvoiceWorkflowStatus.Overdue, 5_000m, 1_000m, new DateTime(2026, 5, 5)),
            InvoicePayment("INV-6", PaymentType.Expense, PaymentStatus.Cancelled, InvoiceWorkflowStatus.Cancelled, 6_000m, 100m, new DateTime(2026, 5, 6)),
            InvoicePayment("INV-7", PaymentType.Income, PaymentStatus.Pending, InvoiceWorkflowStatus.NotSigned, 7_000m, 0m, new DateTime(2026, 5, 7)),
            InvoicePayment("INV-OLD", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 99_999m, 99_999m, new DateTime(2026, 4, 30)),
            InvoicePayment("PAY-1", PaymentType.Income, PaymentStatus.Completed, InvoiceWorkflowStatus.Paid, 88_888m, 88_888m, new DateTime(2026, 5, 7), DocumentKind.Payment));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.InvoiceStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<InvoiceStatusReportDto>(ok.Value);
        Assert.Equal(7, report.InvoiceCount);
        Assert.Equal(5, report.IncomeCount);
        Assert.Equal(2, report.ExpenseCount);
        Assert.Equal(1, report.PaidCount);
        Assert.Equal(1, report.OverdueCount);
        Assert.Equal(28_000m, report.TotalAmount);
        Assert.Equal(6_100m, report.PaidAmount);
        Assert.Equal(21_900m, report.OutstandingAmount);

        Assert.Collection(
            report.Statuses,
            item =>
            {
                Assert.Equal(InvoiceWorkflowStatus.Overdue, item.Status);
                Assert.Equal("Просрочен", item.StatusLabel);
                Assert.Equal(5_000m, item.TotalAmount);
                Assert.Equal(4_000m, item.OutstandingAmount);
                Assert.Equal(17.9m, item.Share);
            },
            item =>
            {
                Assert.Equal(InvoiceWorkflowStatus.OnSigning, item.Status);
                Assert.Equal("На подписании", item.StatusLabel);
                Assert.Equal(2_000m, item.TotalAmount);
                Assert.Equal(500m, item.PaidAmount);
                Assert.Equal(7.1m, item.Share);
            },
            item =>
            {
                Assert.Equal(InvoiceWorkflowStatus.NotSigned, item.Status);
                Assert.Equal("Не подписан", item.StatusLabel);
                Assert.Equal(7_000m, item.TotalAmount);
                Assert.Equal(7_000m, item.OutstandingAmount);
                Assert.Equal(25m, item.Share);
            },
            item =>
            {
                Assert.Equal(InvoiceWorkflowStatus.PartiallyPaid, item.Status);
                Assert.Equal("Частично оплачен", item.StatusLabel);
                Assert.Equal(4_000m, item.TotalAmount);
                Assert.Equal(2_500m, item.OutstandingAmount);
                Assert.Equal(14.3m, item.Share);
            },
            item =>
            {
                Assert.Equal(InvoiceWorkflowStatus.Draft, item.Status);
                Assert.Equal("Черновик", item.StatusLabel);
                Assert.Equal(1_000m, item.TotalAmount);
                Assert.Equal(3.6m, item.Share);
            },
            item =>
            {
                Assert.Equal(InvoiceWorkflowStatus.Paid, item.Status);
                Assert.Equal("Оплачен", item.StatusLabel);
                Assert.Equal(3_000m, item.TotalAmount);
                Assert.Equal(0m, item.OutstandingAmount);
                Assert.Equal(10.7m, item.Share);
            },
            item =>
            {
                Assert.Equal(InvoiceWorkflowStatus.Cancelled, item.Status);
                Assert.Equal("Аннулирован", item.StatusLabel);
                Assert.Equal(6_000m, item.TotalAmount);
                Assert.Equal(5_900m, item.OutstandingAmount);
                Assert.Equal(21.4m, item.Share);
            });
    }

    [Fact]
    public async Task ActStatuses_GroupsActsByWorkflowStatus()
    {
        await using var db = await CreateContextAsync();
        db.Acts.AddRange(
            Act("ACT-1", ActStatus.Created, ActWorkflowStatus.Draft, 1_000m, new DateTime(2026, 5, 1)),
            Act("ACT-2", ActStatus.Transferred, ActWorkflowStatus.OnSigning, 2_000m, new DateTime(2026, 5, 2)),
            Act("ACT-3", ActStatus.Signed, ActWorkflowStatus.Signed, 3_000m, new DateTime(2026, 5, 3)),
            Act("ACT-4", ActStatus.Terminated, ActWorkflowStatus.Cancelled, 4_000m, new DateTime(2026, 5, 4)),
            Act("ACT-5", ActStatus.Created, ActWorkflowStatus.NotSigned, 5_000m, new DateTime(2026, 5, 5)),
            Act("ACT-OLD", ActStatus.Signed, ActWorkflowStatus.Signed, 99_999m, new DateTime(2026, 4, 30)));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.ActStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<ActStatusReportDto>(ok.Value);
        Assert.Equal(5, report.ActCount);
        Assert.Equal(2, report.CreatedCount);
        Assert.Equal(1, report.TransferredCount);
        Assert.Equal(1, report.SignedCount);
        Assert.Equal(1, report.TerminatedCount);
        Assert.Equal(15_000m, report.TotalAmount);
        Assert.Equal(3_000m, report.SignedAmount);
        Assert.Equal(12_000m, report.PendingAmount);

        Assert.Collection(
            report.Statuses,
            item =>
            {
                Assert.Equal(ActWorkflowStatus.OnSigning, item.WorkflowStatus);
                Assert.Equal("На подписании", item.WorkflowStatusLabel);
                Assert.Equal(2_000m, item.TotalAmount);
                Assert.Equal(13.3m, item.Share);
            },
            item =>
            {
                Assert.Equal(ActWorkflowStatus.NotSigned, item.WorkflowStatus);
                Assert.Equal("Не подписан", item.WorkflowStatusLabel);
                Assert.Equal(5_000m, item.TotalAmount);
                Assert.Equal(5_000m, item.PendingAmount);
                Assert.Equal(33.3m, item.Share);
            },
            item =>
            {
                Assert.Equal(ActWorkflowStatus.Draft, item.WorkflowStatus);
                Assert.Equal("Черновик", item.WorkflowStatusLabel);
                Assert.Equal(1_000m, item.TotalAmount);
                Assert.Equal(6.7m, item.Share);
            },
            item =>
            {
                Assert.Equal(ActWorkflowStatus.Signed, item.WorkflowStatus);
                Assert.Equal("Подписан", item.WorkflowStatusLabel);
                Assert.Equal(3_000m, item.SignedAmount);
                Assert.Equal(20m, item.Share);
            },
            item =>
            {
                Assert.Equal(ActWorkflowStatus.Cancelled, item.WorkflowStatus);
                Assert.Equal("Отменен", item.WorkflowStatusLabel);
                Assert.Equal(4_000m, item.PendingAmount);
                Assert.Equal(26.7m, item.Share);
            });
    }

    [Fact]
    public async Task ContractStatuses_GroupsContractsByStatus()
    {
        await using var db = await CreateContextAsync();
        db.Contracts.AddRange(
            Contract("C-1", ContractStatus.Draft, 1_000m, new DateTime(2026, 5, 1), false, new DateTime(2026, 5, 20)),
            Contract("C-2", ContractStatus.OnSigning, 2_000m, new DateTime(2026, 5, 2), false, new DateTime(2026, 6, 20)),
            Contract("C-3", ContractStatus.Concluded, 3_000m, new DateTime(2026, 5, 3), true, null),
            Contract("C-4", ContractStatus.Terminated, 4_000m, new DateTime(2026, 5, 4), false, new DateTime(2026, 5, 25)),
            Contract("C-5", ContractStatus.Cancelled, 5_000m, new DateTime(2026, 5, 5), false, null),
            Contract("C-6", ContractStatus.NotSigned, 6_000m, new DateTime(2026, 5, 6), false, new DateTime(2026, 5, 28)),
            Contract("C-OLD", ContractStatus.Concluded, 99_999m, new DateTime(2026, 4, 30), false, null));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.ContractStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<ContractStatusReportDto>(ok.Value);
        Assert.Equal(6, report.ContractCount);
        Assert.Equal(1, report.DraftCount);
        Assert.Equal(1, report.NotSignedCount);
        Assert.Equal(1, report.OnSigningCount);
        Assert.Equal(1, report.ConcludedCount);
        Assert.Equal(1, report.TerminatedCount);
        Assert.Equal(1, report.CancelledCount);
        Assert.Equal(1, report.OpenEndedCount);
        Assert.Equal(21_000m, report.TotalAmount);
        Assert.Equal(12_000m, report.ActiveAmount);
        Assert.Equal(9_000m, report.ClosedAmount);

        Assert.Collection(
            report.Statuses,
            item =>
            {
                Assert.Equal(ContractStatus.OnSigning, item.Status);
                Assert.Equal("На подписании", item.StatusLabel);
                Assert.Equal(2_000m, item.TotalAmount);
                Assert.Equal(0, item.ExpiringCount);
                Assert.Equal(9.5m, item.Share);
            },
            item =>
            {
                Assert.Equal(ContractStatus.NotSigned, item.Status);
                Assert.Equal("Не подписан", item.StatusLabel);
                Assert.Equal(6_000m, item.TotalAmount);
                Assert.Equal(1, item.ExpiringCount);
                Assert.Equal(28.6m, item.Share);
            },
            item =>
            {
                Assert.Equal(ContractStatus.Draft, item.Status);
                Assert.Equal("Черновик", item.StatusLabel);
                Assert.Equal(1_000m, item.TotalAmount);
                Assert.Equal(1, item.ExpiringCount);
                Assert.Equal(4.8m, item.Share);
            },
            item =>
            {
                Assert.Equal(ContractStatus.Concluded, item.Status);
                Assert.Equal("Подписан", item.StatusLabel);
                Assert.Equal(3_000m, item.TotalAmount);
                Assert.Equal(1, item.OpenEndedCount);
                Assert.Equal(14.3m, item.Share);
            },
            item =>
            {
                Assert.Equal(ContractStatus.Terminated, item.Status);
                Assert.Equal("Расторгнут", item.StatusLabel);
                Assert.Equal(4_000m, item.TotalAmount);
                Assert.Equal(1, item.ExpiringCount);
                Assert.Equal(19.0m, item.Share);
            },
            item =>
            {
                Assert.Equal(ContractStatus.Cancelled, item.Status);
                Assert.Equal("Аннулирован", item.StatusLabel);
                Assert.Equal(5_000m, item.TotalAmount);
                Assert.Equal(23.8m, item.Share);
            });
    }

    [Fact]
    public async Task PaymentOrders_GroupsFactOperationsByPaymentOrderNumber()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 6_000m, new DateTime(2026, 5, 1), "Основной банк", paymentOrderNumber: "PO-1"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", paymentOrderNumber: " po-1 "),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_500m, new DateTime(2026, 5, 3), "Касса", paymentOrderNumber: "PO-2"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 500m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", paymentOrderNumber: "PO-1"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", paymentOrderNumber: "PO-1"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.PaymentOrders(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<PaymentOrderReportDto>(ok.Value);
        Assert.Equal(6_500m, report.IncomeTotal);
        Assert.Equal(3_500m, report.ExpenseTotal);
        Assert.Equal(3_000m, report.NetTotal);

        Assert.Collection(
            report.Orders,
            item =>
            {
                Assert.Equal("PO-1", item.PaymentOrderNumber);
                Assert.Equal(6_000m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(4_000m, item.NetTotal);
                Assert.Equal(8_000m, item.TotalAmount);
                Assert.Equal(80m, item.Share);
            },
            item =>
            {
                Assert.Equal("PO-2", item.PaymentOrderNumber);
                Assert.Equal(0m, item.Income);
                Assert.Equal(1_500m, item.Expense);
                Assert.Equal(15m, item.Share);
            },
            item =>
            {
                Assert.Equal("Без номера платежки", item.PaymentOrderNumber);
                Assert.Equal(500m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(5m, item.Share);
            });
    }

    [Fact]
    public async Task PaymentPurposes_GroupsFactOperationsByPurpose()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 8_000m, new DateTime(2026, 5, 1), "Основной банк", purpose: "Оплата услуг"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", purpose: " оплата услуг "),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_500m, new DateTime(2026, 5, 3), "Касса", purpose: "Возврат"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 500m, new DateTime(2026, 5, 4), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", purpose: "Оплата услуг"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", purpose: "Оплата услуг"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.PaymentPurposes(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<PaymentPurposeReportDto>(ok.Value);
        Assert.Equal(8_500m, report.IncomeTotal);
        Assert.Equal(3_500m, report.ExpenseTotal);
        Assert.Equal(5_000m, report.NetTotal);

        Assert.Collection(
            report.Purposes,
            item =>
            {
                Assert.Equal("Оплата услуг", item.Purpose);
                Assert.Equal(8_000m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(6_000m, item.NetTotal);
                Assert.Equal(10_000m, item.TotalAmount);
                Assert.Equal(83.3m, item.Share);
            },
            item =>
            {
                Assert.Equal("Возврат", item.Purpose);
                Assert.Equal(0m, item.Income);
                Assert.Equal(1_500m, item.Expense);
                Assert.Equal(12.5m, item.Share);
            },
            item =>
            {
                Assert.Equal("Без назначения", item.Purpose);
                Assert.Equal(500m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(4.2m, item.Share);
            });
    }

    [Fact]
    public async Task MatchStatuses_GroupsFactOperationsByMatchStatus()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 6_000m, new DateTime(2026, 5, 1), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Основной банк", matchStatus: MoneyOperationMatchStatus.ClarificationRequired),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Касса"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 500m, new DateTime(2026, 5, 5), "Касса", matchStatus: MoneyOperationMatchStatus.Ignored),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 6), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 7), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.MatchStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<MatchStatusReportDto>(ok.Value);
        Assert.Equal(7_000m, report.IncomeTotal);
        Assert.Equal(5_500m, report.ExpenseTotal);
        Assert.Equal(1_500m, report.NetTotal);
        Assert.Equal(5, report.OperationCount);
        Assert.Equal(2, report.MatchedCount);
        Assert.Equal(2, report.NeedsAttentionCount);

        Assert.Collection(
            report.Statuses,
            item =>
            {
                Assert.Equal(MoneyOperationMatchStatus.ClarificationRequired, item.Status);
                Assert.Equal("Требует уточнения", item.StatusLabel);
                Assert.Equal(0m, item.Income);
                Assert.Equal(3_000m, item.Expense);
                Assert.Equal(24m, item.Share);
            },
            item =>
            {
                Assert.Equal(MoneyOperationMatchStatus.None, item.Status);
                Assert.Equal("Не сверено", item.StatusLabel);
                Assert.Equal(1_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(8m, item.Share);
            },
            item =>
            {
                Assert.Equal(MoneyOperationMatchStatus.Matched, item.Status);
                Assert.Equal("Сверено", item.StatusLabel);
                Assert.Equal(6_000m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(8_000m, item.TotalAmount);
                Assert.Equal(64m, item.Share);
            },
            item =>
            {
                Assert.Equal(MoneyOperationMatchStatus.Ignored, item.Status);
                Assert.Equal("Исключено", item.StatusLabel);
                Assert.Equal(0m, item.Income);
                Assert.Equal(500m, item.Expense);
                Assert.Equal(4m, item.Share);
            });
    }

    [Fact]
    public async Task JournalStatuses_GroupsFactOperationsByComputedJournalStatus()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 6_000m, new DateTime(2026, 5, 1), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Основной банк", matchStatus: MoneyOperationMatchStatus.ClarificationRequired),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 500m, new DateTime(2026, 5, 5), "Касса"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 6), "Основной банк"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 7), "Основной банк", matchStatus: MoneyOperationMatchStatus.Matched));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.JournalStatuses(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<JournalStatusReportDto>(ok.Value);
        Assert.Equal(9_500m, report.IncomeTotal);
        Assert.Equal(3_000m, report.ExpenseTotal);
        Assert.Equal(6_500m, report.NetTotal);
        Assert.Equal(5, report.OperationCount);
        Assert.Equal(3, report.PostedCount);
        Assert.Equal(1, report.ProcessingCount);
        Assert.Equal(1, report.ErrorCount);

        Assert.Collection(
            report.Statuses,
            item =>
            {
                Assert.Equal(MoneyOperationJournalStatus.Error, item.Status);
                Assert.Equal("Ошибка", item.StatusLabel);
                Assert.Equal(0m, item.Income);
                Assert.Equal(1_000m, item.Expense);
                Assert.Equal(8m, item.Share);
            },
            item =>
            {
                Assert.Equal(MoneyOperationJournalStatus.Processing, item.Status);
                Assert.Equal("В обработке", item.StatusLabel);
                Assert.Equal(3_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(24m, item.Share);
            },
            item =>
            {
                Assert.Equal(MoneyOperationJournalStatus.Posted, item.Status);
                Assert.Equal("Проведено", item.StatusLabel);
                Assert.Equal(6_500m, item.Income);
                Assert.Equal(2_000m, item.Expense);
                Assert.Equal(8_500m, item.TotalAmount);
                Assert.Equal(68m, item.Share);
            });
    }

    [Fact]
    public async Task Currencies_GroupsFactOperationsByCurrency()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 7_000m, new DateTime(2026, 5, 1), "Основной банк", currency: "RUB"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 2_000m, new DateTime(2026, 5, 2), "Основной банк", currency: " rub "),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Fact, 3_000m, new DateTime(2026, 5, 3), "Основной банк", currency: "usd"),
            Operation(MoneyOperationSource.Cash, MoneyOperationDirection.Expense, MoneyOperationAccountingType.Fact, 1_000m, new DateTime(2026, 5, 4), "Касса", currency: null),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Income, MoneyOperationAccountingType.Plan, 99_999m, new DateTime(2026, 5, 5), "Основной банк", currency: "USD"),
            Operation(MoneyOperationSource.Bank, MoneyOperationDirection.Transfer, MoneyOperationAccountingType.Fact, 50_000m, new DateTime(2026, 5, 6), "Основной банк", currency: "RUB"));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Currencies(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CurrencyReportDto>(ok.Value);
        Assert.Equal(10_000m, report.IncomeTotal);
        Assert.Equal(3_000m, report.ExpenseTotal);
        Assert.Equal(7_000m, report.NetTotal);

        Assert.Collection(
            report.Currencies,
            item =>
            {
                Assert.Equal("RUB", item.Currency);
                Assert.Equal(7_000m, item.Income);
                Assert.Equal(3_000m, item.Expense);
                Assert.Equal(4_000m, item.NetTotal);
                Assert.Equal(10_000m, item.TotalAmount);
                Assert.Equal(76.9m, item.Share);
            },
            item =>
            {
                Assert.Equal("USD", item.Currency);
                Assert.Equal(3_000m, item.Income);
                Assert.Equal(0m, item.Expense);
                Assert.Equal(23.1m, item.Share);
            });
    }

    [Fact]
    public async Task CashOrders_GroupsOrdersByStatus()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.AddRange(
            CashOrder("PKO-1", CashOrderType.Incoming, CashOrderStatus.Posted, 5_000m, new DateTime(2026, 5, 1)),
            CashOrder("RKO-1", CashOrderType.Outgoing, CashOrderStatus.Posted, 2_000m, new DateTime(2026, 5, 2)),
            CashOrder("PKO-2", CashOrderType.Incoming, CashOrderStatus.Draft, 1_000m, new DateTime(2026, 5, 3)),
            CashOrder("RKO-2", CashOrderType.Outgoing, CashOrderStatus.Cancelled, 500m, new DateTime(2026, 5, 4)),
            CashOrder("PKO-OLD", CashOrderType.Incoming, CashOrderStatus.Posted, 99_999m, new DateTime(2026, 4, 30)));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.CashOrders(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<CashOrderReportDto>(ok.Value);
        Assert.Equal(5_000m, report.PostedIncomingTotal);
        Assert.Equal(2_000m, report.PostedOutgoingTotal);
        Assert.Equal(3_000m, report.PostedNetTotal);
        Assert.Equal(4, report.OrderCount);
        Assert.Equal(1, report.DraftCount);
        Assert.Equal(2, report.PostedCount);
        Assert.Equal(1, report.CancelledCount);

        Assert.Collection(
            report.Statuses,
            item =>
            {
                Assert.Equal(CashOrderStatus.Draft, item.Status);
                Assert.Equal("Черновик", item.StatusLabel);
                Assert.Equal(1_000m, item.IncomingTotal);
                Assert.Equal(0m, item.OutgoingTotal);
                Assert.Equal(11.8m, item.Share);
            },
            item =>
            {
                Assert.Equal(CashOrderStatus.Posted, item.Status);
                Assert.Equal("Проведен", item.StatusLabel);
                Assert.Equal(5_000m, item.IncomingTotal);
                Assert.Equal(2_000m, item.OutgoingTotal);
                Assert.Equal(7_000m, item.TotalAmount);
                Assert.Equal(82.4m, item.Share);
            },
            item =>
            {
                Assert.Equal(CashOrderStatus.Cancelled, item.Status);
                Assert.Equal("Отменен", item.StatusLabel);
                Assert.Equal(0m, item.IncomingTotal);
                Assert.Equal(500m, item.OutgoingTotal);
                Assert.Equal(5.9m, item.Share);
            });
    }

    [Fact]
    public async Task BankStatementImports_GroupsBatchesByStatus()
    {
        await using var db = await CreateContextAsync();
        db.BankStatementImportBatches.AddRange(
            BankStatementImportBatch("may-ok.txt", BankStatementImportBatchStatus.Applied, 10, 8, 2, 0, 0, new DateTime(2026, 5, 1, 10, 0, 0)),
            BankStatementImportBatch("may-errors.txt", BankStatementImportBatchStatus.AppliedWithErrors, 12, 7, 2, 3, 0, new DateTime(2026, 5, 2, 11, 0, 0)),
            BankStatementImportBatch("may-rolled.txt", BankStatementImportBatchStatus.RolledBack, 5, 5, 0, 0, 5, new DateTime(2026, 5, 3, 12, 0, 0)),
            BankStatementImportBatch("may-blocked.txt", BankStatementImportBatchStatus.RollbackBlocked, 4, 3, 0, 1, 0, new DateTime(2026, 5, 4, 13, 0, 0)),
            BankStatementImportBatch("old.txt", BankStatementImportBatchStatus.Applied, 99, 99, 0, 0, 0, new DateTime(2026, 4, 30, 23, 59, 59)));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.BankStatementImports(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<BankStatementImportReportDto>(ok.Value);
        Assert.Equal(4, report.BatchCount);
        Assert.Equal(31, report.RowsReadTotal);
        Assert.Equal(23, report.CreatedRowsTotal);
        Assert.Equal(4, report.SkippedRowsTotal);
        Assert.Equal(4, report.ErrorRowsTotal);
        Assert.Equal(5, report.RollbackRowsTotal);
        Assert.Equal(1, report.AppliedCount);
        Assert.Equal(1, report.AppliedWithErrorsCount);
        Assert.Equal(1, report.RolledBackCount);
        Assert.Equal(1, report.RollbackBlockedCount);

        Assert.Collection(
            report.Statuses,
            item =>
            {
                Assert.Equal(BankStatementImportBatchStatus.AppliedWithErrors, item.Status);
                Assert.Equal("Применен с ошибками", item.StatusLabel);
                Assert.Equal(12, item.RowsRead);
                Assert.Equal(3, item.ErrorRows);
                Assert.Equal(25m, item.Share);
            },
            item =>
            {
                Assert.Equal(BankStatementImportBatchStatus.RollbackBlocked, item.Status);
                Assert.Equal("Откат заблокирован", item.StatusLabel);
                Assert.Equal(4, item.RowsRead);
                Assert.Equal(1, item.ErrorRows);
                Assert.Equal(25m, item.Share);
            },
            item =>
            {
                Assert.Equal(BankStatementImportBatchStatus.Applied, item.Status);
                Assert.Equal("Применен", item.StatusLabel);
                Assert.Equal(10, item.RowsRead);
                Assert.Equal(0, item.ErrorRows);
                Assert.Equal(25m, item.Share);
            },
            item =>
            {
                Assert.Equal(BankStatementImportBatchStatus.RolledBack, item.Status);
                Assert.Equal("Откатан", item.StatusLabel);
                Assert.Equal(5, item.RollbackRows);
                Assert.Equal(25m, item.Share);
            });
    }

    [Fact]
    public async Task SpreadsheetImports_GroupsBatchesByStatus()
    {
        await using var db = await CreateContextAsync();
        db.SpreadsheetImportBatches.AddRange(
            SpreadsheetImportBatch("May OK", SpreadsheetImportBatchStatus.Applied, 2, 20, 10, 4, 5, 1, 0, new DateTime(2026, 5, 1, 10, 0, 0)),
            SpreadsheetImportBatch("May Errors", SpreadsheetImportBatchStatus.AppliedWithErrors, 1, 12, 5, 2, 2, 3, 0, new DateTime(2026, 5, 2, 11, 0, 0)),
            SpreadsheetImportBatch("May Rolled", SpreadsheetImportBatchStatus.RolledBack, 1, 8, 4, 1, 2, 1, 4, new DateTime(2026, 5, 3, 12, 0, 0)),
            SpreadsheetImportBatch("May Conflicts", SpreadsheetImportBatchStatus.RolledBackWithConflicts, 3, 6, 2, 1, 1, 2, 2, new DateTime(2026, 5, 4, 13, 0, 0)),
            SpreadsheetImportBatch("Old", SpreadsheetImportBatchStatus.Applied, 5, 99, 99, 0, 0, 0, 0, new DateTime(2026, 4, 30, 23, 59, 59)));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.SpreadsheetImports(
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var report = Assert.IsType<SpreadsheetImportReportDto>(ok.Value);
        Assert.Equal(4, report.BatchCount);
        Assert.Equal(7, report.FileCount);
        Assert.Equal(46, report.TotalRows);
        Assert.Equal(21, report.CreatedRows);
        Assert.Equal(8, report.UpdatedRows);
        Assert.Equal(10, report.SkippedRows);
        Assert.Equal(7, report.ErrorRows);
        Assert.Equal(6, report.RollbackRows);
        Assert.Equal(1, report.AppliedCount);
        Assert.Equal(1, report.AppliedWithErrorsCount);
        Assert.Equal(1, report.RolledBackCount);
        Assert.Equal(1, report.RolledBackWithConflictsCount);

        Assert.Collection(
            report.Statuses,
            item =>
            {
                Assert.Equal(SpreadsheetImportBatchStatus.AppliedWithErrors, item.Status);
                Assert.Equal("Применен с ошибками", item.StatusLabel);
                Assert.Equal(1, item.FileCount);
                Assert.Equal(12, item.TotalRows);
                Assert.Equal(3, item.ErrorRows);
                Assert.Equal(25m, item.Share);
            },
            item =>
            {
                Assert.Equal(SpreadsheetImportBatchStatus.RolledBackWithConflicts, item.Status);
                Assert.Equal("Откатан с конфликтами", item.StatusLabel);
                Assert.Equal(3, item.FileCount);
                Assert.Equal(6, item.TotalRows);
                Assert.Equal(2, item.RollbackRows);
                Assert.Equal(25m, item.Share);
            },
            item =>
            {
                Assert.Equal(SpreadsheetImportBatchStatus.Applied, item.Status);
                Assert.Equal("Применен", item.StatusLabel);
                Assert.Equal(2, item.FileCount);
                Assert.Equal(20, item.TotalRows);
                Assert.Equal(25m, item.Share);
            },
            item =>
            {
                Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, item.Status);
                Assert.Equal("Откатан", item.StatusLabel);
                Assert.Equal(1, item.FileCount);
                Assert.Equal(4, item.RollbackRows);
                Assert.Equal(25m, item.Share);
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
        string? paidAccountName = null,
        string? paidInvoiceNumber = null,
        decimal? paidAmount = null,
        string? paymentOrderNumber = null,
        string? purpose = null,
        MoneyOperationMatchStatus matchStatus = MoneyOperationMatchStatus.None,
        string? currency = "RUB")
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
            PaidInvoiceNumber = paidInvoiceNumber,
            PaidAmount = paidAmount,
            PaymentOrderNumber = paymentOrderNumber,
            Purpose = purpose,
            MatchStatus = matchStatus,
            Currency = currency,
            CreatedAt = DateTime.UtcNow,
        };

    private static Payment InvoicePayment(
        string account,
        PaymentType type,
        PaymentStatus status,
        string workflowStatus,
        decimal amount,
        decimal paidAmount,
        DateTime date,
        DocumentKind documentKind = DocumentKind.Invoice)
        => new()
        {
            Account = account,
            AccountDate = date,
            Amount = amount,
            PaidAmount = paidAmount,
            Date = date,
            PlannedDate = date,
            Status = status,
            Type = type,
            DocumentKind = documentKind,
            InvoiceWorkflowStatus = workflowStatus,
            IsPaid = status == PaymentStatus.Completed,
            CreatedAt = DateTime.UtcNow,
        };

    private static Act Act(
        string number,
        ActStatus status,
        string workflowStatus,
        decimal amount,
        DateTime date)
        => new()
        {
            Number = number,
            Status = status,
            WorkflowStatus = workflowStatus,
            Amount = amount,
            Date = date,
            CreatedAt = DateTime.UtcNow,
        };

    private static Contract Contract(
        string number,
        string status,
        decimal amount,
        DateTime date,
        bool isOpenEnded,
        DateTime? validUntil)
        => new()
        {
            Number = number,
            Status = status,
            Amount = amount,
            Date = date,
            IsOpenEnded = isOpenEnded,
            ValidUntil = validUntil,
            BillingMode = ContractBillingMode.Fixed,
            CreatedAt = DateTime.UtcNow,
        };

    private static BankStatementImportBatch BankStatementImportBatch(
        string fileName,
        string status,
        int rowsRead,
        int createdRows,
        int skippedRows,
        int errorRows,
        int rollbackRows,
        DateTime createdAt)
        => new()
        {
            FileName = fileName,
            FileHash = $"{fileName}-hash",
            FileSize = rowsRead * 100,
            Status = status,
            RowsRead = rowsRead,
            CreatedRows = createdRows,
            SkippedRows = skippedRows,
            ErrorRows = errorRows,
            RollbackRows = rollbackRows,
            CreatedAt = createdAt,
        };

    private static SpreadsheetImportBatch SpreadsheetImportBatch(
        string title,
        string status,
        int fileCount,
        int totalRows,
        int createdRows,
        int updatedRows,
        int skippedRows,
        int errorRows,
        int rollbackRows,
        DateTime createdAt)
        => new()
        {
            Title = title,
            Status = status,
            TotalRows = totalRows,
            CreatedRows = createdRows,
            UpdatedRows = updatedRows,
            SkippedRows = skippedRows,
            ErrorRows = errorRows,
            RollbackRows = rollbackRows,
            CreatedAt = createdAt,
            Files = Enumerable.Range(1, fileCount)
                .Select(index => new SpreadsheetImportBatchFile
                {
                    FileName = $"{title}-{index}.xlsx",
                    FileHash = $"{title}-{index}-hash",
                    DetectedKind = "mixed",
                    RowCount = Math.Max(1, totalRows / Math.Max(1, fileCount)),
                    CreatedAt = createdAt,
                })
                .ToList(),
        };

    private static CashOrder CashOrder(
        string number,
        CashOrderType type,
        CashOrderStatus status,
        decimal amount,
        DateTime date)
        => new()
        {
            Number = number,
            Type = type,
            Status = status,
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
