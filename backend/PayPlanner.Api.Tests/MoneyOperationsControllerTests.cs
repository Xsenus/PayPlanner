using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Invoices;
using System.Reflection;
using System.Text;
using Xunit;

namespace PayPlanner.Api.Tests;

public class MoneyOperationsControllerTests
{
    [Fact]
    public async Task CreateUpdateDeleteMoneyOperation_RoundTripsSuccessfully()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db);

        var createAction = await controller.Create(new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Expense,
            AccountingType = MoneyOperationAccountingType.Fact,
            AccountName = "  Расчётный счёт  ",
            CounterpartyName = "  ООО Ромашка  ",
            CategoryName = "  Аренда  ",
            Amount = 15_500m,
            Currency = "  RUB  ",
            PaymentOrderNumber = "  873864  ",
            Date = new DateTime(2026, 4, 21),
            RecognitionDate = new DateTime(2026, 4, 22),
            IsAdvancePayment = true,
            Purpose = "  Оплата аренды  ",
            PaidInvoiceNumber = "  INV-51  ",
            PaidAmount = 15_500m,
            ProjectName = "  Проект Альфа  ",
            DirectionName = "  Корпоративное право  ",
            SalesPointName = "  Центральный офис  ",
            PaymentPriority = 2,
            PaidAccountName = "  Основной счёт  ",
        });

        var created = Assert.IsType<CreatedResult>(createAction);
        var createdOperation = Assert.IsType<MoneyOperation>(created.Value);
        Assert.Equal("Расчётный счёт", createdOperation.AccountName);
        Assert.Equal("ООО Ромашка", createdOperation.CounterpartyName);
        Assert.Equal("RUB", createdOperation.Currency);
        Assert.Equal(new DateTime(2026, 4, 21), createdOperation.Date);
        Assert.Equal(new DateTime(2026, 4, 22), createdOperation.RecognitionDate);

        var updateAction = await controller.Update(createdOperation.Id, new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Plan,
            AccountName = "  Основной счёт  ",
            CounterpartyName = "  ООО Василёк  ",
            CategoryName = "  Возврат  ",
            Amount = 9_900m,
            Currency = "  USD  ",
            PaymentOrderNumber = "  445566  ",
            Date = new DateTime(2026, 4, 23),
            RecognitionDate = null,
            IsAdvancePayment = false,
            Purpose = "  Возврат аванса  ",
            PaidInvoiceNumber = "  INV-77  ",
            PaidAmount = 9_900m,
            ProjectName = "  Проект Бета  ",
            DirectionName = "  Арбитраж  ",
            SalesPointName = "  Филиал  ",
            PaymentPriority = 1,
            PaidAccountName = "  Дополнительный счёт  ",
        });

        var updatedOk = Assert.IsType<OkObjectResult>(updateAction);
        var updatedOperation = Assert.IsType<MoneyOperation>(updatedOk.Value);
        Assert.Equal(MoneyOperationDirection.Income, updatedOperation.Direction);
        Assert.Equal(MoneyOperationAccountingType.Plan, updatedOperation.AccountingType);
        Assert.Equal("ООО Василёк", updatedOperation.CounterpartyName);
        Assert.Equal("USD", updatedOperation.Currency);
        Assert.Equal(new DateTime(2026, 4, 23), updatedOperation.Date);
        Assert.Null(updatedOperation.RecognitionDate);
        Assert.NotNull(updatedOperation.UpdatedAt);

        var deleteAction = await controller.Delete(createdOperation.Id);
        Assert.IsType<NoContentResult>(deleteAction);
        Assert.False(await db.MoneyOperations.AnyAsync());
    }

    [Fact]
    public async Task DeleteMoneyOperation_ReturnsBadRequest_WhenOperationAlreadyAppliedToInvoice()
    {
        await using var db = await CreateContextAsync();
        var invoice = new Invoice
        {
            Number = "33",
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 25_000m,
            Date = new DateTime(2026, 4, 29),
            LinkedInvoiceId = invoice.Id,
            PaidInvoiceNumber = "33",
            PaidAccountName = "33",
            MatchStatus = MoneyOperationMatchStatus.Matched,
            MatchedAt = DateTime.UtcNow,
            MatchComment = "Сверено",
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Delete(operation.Id);

        Assert.IsType<BadRequestObjectResult>(action);
        Assert.True(await db.MoneyOperations.AnyAsync(item => item.Id == operation.Id));
    }

    [Fact]
    public async Task CreateMoneyOperation_ReturnsBadRequest_WhenAmountIsNegative()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db);

        var action = await controller.Create(new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Expense,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = -1m,
            Date = new DateTime(2026, 4, 21),
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Equal("Amount must be non-negative.", badRequest.Value);
    }

    [Fact]
    public async Task CashPayment_CanCreateCashOrder_ThenReusesExistingOrder()
    {
        await using var db = await CreateContextAsync();
        var cashSource = new PaymentSource
        {
            Name = "Наличные",
            Description = "Оплата наличными",
            ColorHex = "#111827",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var client = new Client
        {
            Name = "Ахмедова Айтан Рафик Кызы",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var payment = new Payment
        {
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            DocumentKind = DocumentKind.Payment,
            Amount = 25_000m,
            PaidAmount = 25_000m,
            Account = "33",
            AccountDate = new DateTime(2026, 4, 3),
            Date = new DateTime(2026, 4, 20),
            PlannedDate = new DateTime(2026, 4, 20),
            PaidDate = new DateTime(2026, 4, 20),
            LastPaymentDate = new DateTime(2026, 4, 20),
            Description = "Рассрочка по счету №33 · платеж 1/8",
            CounterpartyName = "Ахмедова Айтан Рафик Кызы",
            PaymentSource = cashSource,
            Client = client,
            CreatedAt = DateTime.UtcNow,
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var moneyController = CreateController(db);
        var beforeAction = await moneyController.GetPaged(
            from: new DateTime(2026, 1, 1),
            to: new DateTime(2026, 12, 31),
            source: MoneyOperationSource.Cash,
            direction: null,
            accountingType: null,
            search: null);
        var beforeOk = Assert.IsType<OkObjectResult>(beforeAction.Result);
        var beforePage = Assert.IsType<MoneyOperationsPageResponse>(beforeOk.Value);
        var beforeItem = Assert.Single(beforePage.Items);
        Assert.True(beforeItem.CanCreateCashOrder);
        Assert.Null(beforeItem.CashOrderId);

        var cashController = new CashOrdersController(db);
        var createAction = await cashController.CreateFromPayment(payment.Id, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction);
        var order = Assert.IsType<CashOrderDto>(created.Value);
        Assert.Equal(CashOrderType.Incoming, order.Type);
        Assert.Equal(CashOrderStatus.Posted, order.Status);
        Assert.Equal(25_000m, order.Amount);
        Assert.Equal(payment.Id, order.PaymentId);
        Assert.StartsWith("ПКО-2026-", order.Number);

        var duplicateAction = await cashController.CreateFromPayment(payment.Id, CancellationToken.None);
        var duplicateOk = Assert.IsType<OkObjectResult>(duplicateAction);
        var duplicateOrder = Assert.IsType<CashOrderDto>(duplicateOk.Value);
        Assert.Equal(order.Id, duplicateOrder.Id);
        Assert.Equal(1, await db.CashOrders.CountAsync());

        var afterAction = await moneyController.GetPaged(
            from: new DateTime(2026, 1, 1),
            to: new DateTime(2026, 12, 31),
            source: MoneyOperationSource.Cash,
            direction: null,
            accountingType: null,
            search: null);
        var afterOk = Assert.IsType<OkObjectResult>(afterAction.Result);
        var afterPage = Assert.IsType<MoneyOperationsPageResponse>(afterOk.Value);
        var afterItem = Assert.Single(afterPage.Items);
        Assert.False(afterItem.CanCreateCashOrder);
        Assert.Equal(order.Id, afterItem.CashOrderId);
        Assert.Equal(order.Number, afterItem.CashOrderNumber);
    }

    [Fact]
    public async Task CompletedPayment_UsesActualPaymentDateForMoneyJournalPeriod()
    {
        await using var db = await CreateContextAsync();
        var cashSource = new PaymentSource
        {
            Name = "Наличные",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.Payments.Add(new Payment
        {
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            DocumentKind = DocumentKind.Payment,
            Amount = 25_000m,
            PaidAmount = 25_000m,
            Account = "33",
            Date = new DateTime(2026, 1, 15),
            PaidDate = new DateTime(2026, 4, 29),
            LastPaymentDate = new DateTime(2026, 4, 29),
            CounterpartyName = "Ахмедова Айтан Рафик Кызы",
            PaymentSource = cashSource,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var action = await controller.GetPaged(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            source: MoneyOperationSource.Cash,
            direction: null,
            accountingType: null,
            search: null);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var page = Assert.IsType<MoneyOperationsPageResponse>(ok.Value);
        var item = Assert.Single(page.Items);
        Assert.Equal(new DateTime(2026, 4, 29), item.Date);
        Assert.Equal(25_000m, item.Amount);
    }

    [Fact]
    public async Task MoneyJournal_SearchFindsCyrillicCounterpartyIgnoringFirstLetterCase()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.Add(new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            AccountName = "Расчетный счет",
            CounterpartyName = "Ахмедова Айтан Рафик Кызы",
            Amount = 25_000m,
            Currency = "RUB",
            Date = new DateTime(2026, 4, 29),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var action = await controller.GetPaged(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            source: MoneyOperationSource.Bank,
            direction: null,
            accountingType: null,
            search: "ахмедова");

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var page = Assert.IsType<MoneyOperationsPageResponse>(ok.Value);
        var item = Assert.Single(page.Items);
        Assert.Equal("Ахмедова Айтан Рафик Кызы", item.CounterpartyName);
    }

    [Fact]
    public async Task MoneyJournal_FiltersByAccountAndCalculatesOpeningBalance()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Основной банк",
                CounterpartyName = "Стартовый остаток",
                Amount = 10_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 3, 31),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Основной банк",
                CounterpartyName = "Клиент",
                Amount = 5_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, 10),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Expense,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Резервный банк",
                CounterpartyName = "Поставщик",
                Amount = 1_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, 11),
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var accountsAction = await controller.GetAccounts(MoneyOperationSource.Bank);
        var accountsOk = Assert.IsType<OkObjectResult>(accountsAction.Result);
        var accounts = Assert.IsAssignableFrom<IReadOnlyList<string>>(accountsOk.Value);
        Assert.Contains("Основной банк", accounts);
        Assert.Contains("Резервный банк", accounts);

        var accountSummariesAction = await controller.GetAccountSummaries(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            source: MoneyOperationSource.Bank);
        var accountSummariesOk = Assert.IsType<OkObjectResult>(accountSummariesAction.Result);
        var accountSummaries = Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationAccountSummaryDto>>(accountSummariesOk.Value);
        var mainAccountSummary = Assert.Single(accountSummaries, item => item.AccountName == "Основной банк");
        Assert.Equal(10_000m, mainAccountSummary.StartBalance);
        Assert.Equal(5_000m, mainAccountSummary.Income);
        Assert.Equal(15_000m, mainAccountSummary.EndBalance);

        var action = await controller.GetPaged(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            source: MoneyOperationSource.Bank,
            direction: null,
            accountingType: null,
            accountName: "Основной банк",
            search: null);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var page = Assert.IsType<MoneyOperationsPageResponse>(ok.Value);
        var item = Assert.Single(page.Items);
        Assert.Equal("Основной банк", item.AccountName);
        Assert.Equal(10_000m, page.Summary.StartBalance);
        Assert.Equal(5_000m, page.Summary.Income);
        Assert.Equal(15_000m, page.Summary.EndBalance);
    }

    [Fact]
    public async Task MoneyJournal_FiltersByCategoryName()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Основной банк",
                CounterpartyName = "Клиент",
                CategoryName = "Юридические услуги",
                Amount = 5_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, 10),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Основной банк",
                CounterpartyName = "Другой клиент",
                CategoryName = "Офисные расходы",
                Amount = 2_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, 11),
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var action = await controller.GetPaged(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            source: MoneyOperationSource.Bank,
            direction: null,
            accountingType: null,
            categoryName: "юридические",
            search: null);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var page = Assert.IsType<MoneyOperationsPageResponse>(ok.Value);
        var item = Assert.Single(page.Items);
        Assert.Equal("Юридические услуги", item.CategoryName);
        Assert.Equal(5_000m, page.Summary.Income);
    }

    [Fact]
    public async Task MoneyJournal_TotalSummaryIncludesEMoneySource()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            new MoneyOperation
            {
                Source = MoneyOperationSource.EMoney,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "ЮMoney",
                CounterpartyName = "Онлайн клиент",
                Amount = 3_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, 10),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Expense,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Основной банк",
                CounterpartyName = "Поставщик",
                Amount = 1_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, 11),
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var action = await controller.GetPaged(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            source: null,
            direction: null,
            accountingType: null,
            accountName: null,
            search: null);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var page = Assert.IsType<MoneyOperationsPageResponse>(ok.Value);
        var eMoney = Assert.Single(page.Summary.BySource, item => item.Source == MoneyOperationSource.EMoney);

        Assert.Equal(3_000m, eMoney.Income);
        Assert.Equal(3_000m, eMoney.EndBalance);
        Assert.Equal(2_000m, page.Summary.EndBalance);
    }

    [Fact]
    public async Task MoneyJournal_TotalSummaryCalculatesOpeningBalanceBySourceAndIgnoresPlan()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Основной банк",
                CounterpartyName = "Стартовый остаток",
                Amount = 10_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 3, 31),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Cash,
                Direction = MoneyOperationDirection.Expense,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Главная касса",
                CounterpartyName = "Расход до периода",
                Amount = 2_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 3, 30),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Основной банк",
                CounterpartyName = "Клиент",
                Amount = 5_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, 10),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Cash,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Главная касса",
                CounterpartyName = "Наличный клиент",
                Amount = 3_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, 11),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Plan,
                AccountName = "Основной банк",
                CounterpartyName = "План не должен попасть в факт",
                Amount = 99_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, 12),
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var action = await controller.GetPaged(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            source: null,
            direction: null,
            accountingType: null,
            accountName: null,
            search: null);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var page = Assert.IsType<MoneyOperationsPageResponse>(ok.Value);

        Assert.Equal(8_000m, page.Summary.StartBalance);
        Assert.Equal(8_000m, page.Summary.Income);
        Assert.Equal(16_000m, page.Summary.EndBalance);
        Assert.Equal(2, page.Summary.IncomeCount);

        var bank = Assert.Single(page.Summary.BySource, item => item.Source == MoneyOperationSource.Bank);
        Assert.Equal(10_000m, bank.StartBalance);
        Assert.Equal(5_000m, bank.Income);
        Assert.Equal(15_000m, bank.EndBalance);

        var cash = Assert.Single(page.Summary.BySource, item => item.Source == MoneyOperationSource.Cash);
        Assert.Equal(-2_000m, cash.StartBalance);
        Assert.Equal(3_000m, cash.Income);
        Assert.Equal(1_000m, cash.EndBalance);
    }

    [Fact]
    public void MoneyJournal_PostgreSqlQueryUsesTextValuesForUnionEnums()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql("Host=localhost;Database=payplanner_test;Username=test;Password=test")
            .Options;
        using var db = new PostgreSqlPaymentContext(options);
        var controller = CreateController(db);

        var method = typeof(MoneyOperationsController).GetMethod(
            "BuildMoneyOperationsQuery",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var query = Assert.IsAssignableFrom<IQueryable>(method.Invoke(controller, new object?[]
        {
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
        }));
        var sql = query.ToQueryString();

        Assert.Contains("'Cash'", sql);
        Assert.Contains("'Bank'", sql);
        Assert.Contains("'Income'", sql);
        Assert.Contains("'Fact'", sql);
        Assert.DoesNotContain("ELSE 0", sql);
        Assert.DoesNotContain("THEN 0", sql);
    }

    [Fact]
    public async Task MoneyJournal_TotalViewCombinesManualOperationsAndCompletedPayments()
    {
        await using var db = await CreateContextAsync();
        var cashSource = new PaymentSource
        {
            Name = "\u041A\u0430\u0441\u0441\u0430",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var cardSource = new PaymentSource
        {
            Name = "\u041A\u0430\u0440\u0442\u0430",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var eMoneySource = new PaymentSource
        {
            Name = "\u042EMoney",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };

        db.MoneyOperations.AddRange(
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Bank",
                CounterpartyName = "Manual income",
                Amount = 1_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, 10),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Card,
                Direction = MoneyOperationDirection.Expense,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Card",
                CounterpartyName = "Manual card expense",
                Amount = 300m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, 11),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.EMoney,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Online wallet",
                CounterpartyName = "Manual e-money income",
                Amount = 700m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, 12),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Plan,
                AccountName = "Bank",
                CounterpartyName = "Planned income",
                Amount = 999m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, 13),
                CreatedAt = DateTime.UtcNow,
            });
        db.Payments.AddRange(
            new Payment
            {
                Type = PaymentType.Income,
                Status = PaymentStatus.Completed,
                Amount = 2_500m,
                PaidAmount = 2_500m,
                Account = "Cashbox",
                Date = new DateTime(2026, 4, 14),
                PaidDate = new DateTime(2026, 4, 14),
                LastPaymentDate = new DateTime(2026, 4, 14),
                CounterpartyName = "Cash client",
                PaymentSource = cashSource,
                CreatedAt = DateTime.UtcNow,
            },
            new Payment
            {
                Type = PaymentType.Expense,
                Status = PaymentStatus.Completed,
                Amount = 400m,
                PaidAmount = 400m,
                Account = "Card",
                Date = new DateTime(2026, 4, 15),
                PaidDate = new DateTime(2026, 4, 15),
                LastPaymentDate = new DateTime(2026, 4, 15),
                CounterpartyName = "Card supplier",
                PaymentSource = cardSource,
                CreatedAt = DateTime.UtcNow,
            },
            new Payment
            {
                Type = PaymentType.Income,
                Status = PaymentStatus.Completed,
                Amount = 700m,
                PaidAmount = 700m,
                Account = "Wallet",
                Date = new DateTime(2026, 4, 14),
                PaidDate = new DateTime(2026, 4, 14),
                LastPaymentDate = new DateTime(2026, 4, 14),
                CounterpartyName = "Wallet payment",
                PaymentSource = eMoneySource,
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var action = await controller.GetPaged(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            source: null,
            direction: null,
            accountingType: null,
            accountName: null,
            search: null,
            page: 1,
            pageSize: 25);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var page = Assert.IsType<MoneyOperationsPageResponse>(ok.Value);
        Assert.Equal(7, page.Total);
        Assert.Contains(page.Items, item => item.Source == MoneyOperationSource.Cash && item.IsReadonly && item.RelatedPaymentId.HasValue);
        Assert.Contains(page.Items, item => item.Source == MoneyOperationSource.Card && item.Direction == MoneyOperationDirection.Expense && item.IsReadonly);
        Assert.Contains(page.Items, item => item.Source == MoneyOperationSource.EMoney && !item.IsReadonly);
        Assert.Contains(page.Items, item => item.AccountingType == MoneyOperationAccountingType.Plan);

        Assert.Equal(4_900m, page.Summary.Income);
        Assert.Equal(700m, page.Summary.Expense);
        Assert.Equal(4_200m, page.Summary.EndBalance);
        Assert.Equal(4, page.Summary.IncomeCount);
        Assert.Equal(2, page.Summary.ExpenseCount);
        Assert.Equal(4, page.Summary.BySource.Count);
        Assert.Equal(2_500m, page.Summary.BySource.Single(item => item.Source == MoneyOperationSource.Cash).Income);
        Assert.Equal(1_000m, page.Summary.BySource.Single(item => item.Source == MoneyOperationSource.Bank).Income);
        Assert.Equal(700m, page.Summary.BySource.Single(item => item.Source == MoneyOperationSource.Card).Expense);
        Assert.Equal(1_400m, page.Summary.BySource.Single(item => item.Source == MoneyOperationSource.EMoney).Income);
    }

    [Fact]
    public async Task MoneyJournal_SourceFiltersSeparateCashBankCardAndEMoney()
    {
        await using var db = await CreateContextAsync();
        var cashSource = new PaymentSource
        {
            Name = "\u041D\u0430\u043B\u0438\u0447\u043D\u044B\u0435",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var cardSource = new PaymentSource
        {
            Name = "\u041A\u0430\u0440\u0442\u0430",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var eMoneySource = new PaymentSource
        {
            Name = "\u042EMoney",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };

        db.MoneyOperations.AddRange(
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Bank",
                CounterpartyName = "Bank operation",
                Amount = 1_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, 10),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.EMoney,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Wallet",
                CounterpartyName = "Wallet operation",
                Amount = 2_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, 11),
                CreatedAt = DateTime.UtcNow,
            });
        db.Payments.AddRange(
            new Payment
            {
                Type = PaymentType.Income,
                Status = PaymentStatus.Completed,
                Amount = 3_000m,
                PaidAmount = 3_000m,
                Account = "Cashbox",
                Date = new DateTime(2026, 4, 12),
                PaidDate = new DateTime(2026, 4, 12),
                LastPaymentDate = new DateTime(2026, 4, 12),
                CounterpartyName = "Cash client",
                PaymentSource = cashSource,
                CreatedAt = DateTime.UtcNow,
            },
            new Payment
            {
                Type = PaymentType.Expense,
                Status = PaymentStatus.Completed,
                Amount = 400m,
                PaidAmount = 400m,
                Account = "Card",
                Date = new DateTime(2026, 4, 13),
                PaidDate = new DateTime(2026, 4, 13),
                LastPaymentDate = new DateTime(2026, 4, 13),
                CounterpartyName = "Card supplier",
                PaymentSource = cardSource,
                CreatedAt = DateTime.UtcNow,
            },
            new Payment
            {
                Type = PaymentType.Income,
                Status = PaymentStatus.Completed,
                Amount = 700m,
                PaidAmount = 700m,
                Account = "Wallet",
                Date = new DateTime(2026, 4, 14),
                PaidDate = new DateTime(2026, 4, 14),
                LastPaymentDate = new DateTime(2026, 4, 14),
                CounterpartyName = "Wallet payment",
                PaymentSource = eMoneySource,
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var cashPage = await GetMoneyPageAsync(controller, MoneyOperationSource.Cash);
        var bankPage = await GetMoneyPageAsync(controller, MoneyOperationSource.Bank);
        var cardPage = await GetMoneyPageAsync(controller, MoneyOperationSource.Card);
        var eMoneyPage = await GetMoneyPageAsync(controller, MoneyOperationSource.EMoney);

        Assert.Single(cashPage.Items);
        Assert.Equal(MoneyOperationSource.Cash, cashPage.Items[0].Source);
        Assert.Equal(3_000m, cashPage.Summary.Income);

        Assert.Single(bankPage.Items);
        Assert.Equal(MoneyOperationSource.Bank, bankPage.Items[0].Source);
        Assert.Equal(1_000m, bankPage.Summary.Income);

        Assert.Single(cardPage.Items);
        Assert.Equal(MoneyOperationSource.Card, cardPage.Items[0].Source);
        Assert.Equal(400m, cardPage.Summary.Expense);

        Assert.Equal(2, eMoneyPage.Items.Count);
        Assert.All(eMoneyPage.Items, item => Assert.Equal(MoneyOperationSource.EMoney, item.Source));
        Assert.Equal(2_700m, eMoneyPage.Summary.Income);
    }

    [Fact]
    public async Task MoneyJournal_PaginationClampsPageSizeAndKeepsStableDateOrder()
    {
        await using var db = await CreateContextAsync();
        for (var day = 1; day <= 8; day++)
        {
            db.MoneyOperations.Add(new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Bank",
                CounterpartyName = $"Operation {day}",
                Amount = day * 100m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, day),
                CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var action = await controller.GetPaged(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            source: MoneyOperationSource.Bank,
            direction: null,
            accountingType: null,
            accountName: null,
            search: null,
            page: 2,
            pageSize: 1);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var page = Assert.IsType<MoneyOperationsPageResponse>(ok.Value);
        Assert.Equal(8, page.Total);
        Assert.Equal(2, page.Page);
        Assert.Equal(5, page.PageSize);
        Assert.Equal(3, page.Items.Count);
        Assert.Equal(
            new[] { new DateTime(2026, 4, 3), new DateTime(2026, 4, 2), new DateTime(2026, 4, 1) },
            page.Items.Select(item => item.Date).ToArray());
    }

    [Fact]
    public async Task MoneyJournal_AccountsEndpointIncludesManualAndPaymentAccountsDistinctly()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Bank main",
                Amount = 1_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, 10),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Expense,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Bank main",
                Amount = 500m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, 11),
                CreatedAt = DateTime.UtcNow,
            });
        db.Payments.Add(new Payment
        {
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Amount = 2_500m,
            PaidAmount = 2_500m,
            Account = "Bank payment account",
            Date = new DateTime(2026, 4, 12),
            PaidDate = new DateTime(2026, 4, 12),
            LastPaymentDate = new DateTime(2026, 4, 12),
            CounterpartyName = "Payment client",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var action = await controller.GetAccounts(MoneyOperationSource.Bank);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var accounts = Assert.IsAssignableFrom<IReadOnlyList<string>>(ok.Value);
        Assert.Equal(new[] { "Bank main", "Bank payment account" }, accounts);
    }

    [Fact]
    public async Task ManualCashOperation_CreatesAndSyncsCashOrder()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db);

        var createAction = await controller.Create(new MoneyOperation
        {
            Source = MoneyOperationSource.Cash,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            AccountName = "Касса",
            CounterpartyName = "Ахмедова Айтан Рафик Кызы",
            CategoryName = "Оплата наличными",
            Amount = 25_000m,
            Currency = "RUB",
            Date = new DateTime(2026, 4, 29),
            Purpose = "Оплата по счету №33",
        });

        var created = Assert.IsType<CreatedResult>(createAction);
        var operation = Assert.IsType<MoneyOperation>(created.Value);
        var createdOrder = await db.CashOrders.SingleAsync();
        Assert.Equal(operation.Id, createdOrder.MoneyOperationId);
        Assert.Equal(CashOrderType.Incoming, createdOrder.Type);
        Assert.Equal(CashOrderStatus.Posted, createdOrder.Status);
        Assert.Equal(25_000m, createdOrder.Amount);
        Assert.StartsWith("ПКО-2026-", createdOrder.Number);

        var pageAction = await controller.GetPaged(
            from: new DateTime(2026, 1, 1),
            to: new DateTime(2026, 12, 31),
            source: MoneyOperationSource.Cash,
            direction: null,
            accountingType: null,
            search: null);
        var pageOk = Assert.IsType<OkObjectResult>(pageAction.Result);
        var page = Assert.IsType<MoneyOperationsPageResponse>(pageOk.Value);
        var item = Assert.Single(page.Items);
        Assert.False(item.CanCreateCashOrder);
        Assert.Equal(createdOrder.Id, item.CashOrderId);

        var updateAction = await controller.Update(operation.Id, new MoneyOperation
        {
            Source = MoneyOperationSource.Cash,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            AccountName = "Основная касса",
            CounterpartyName = "Ахмедова Айтан Рафик Кызы",
            CategoryName = "Оплата наличными",
            Amount = 30_000m,
            Currency = "RUB",
            Date = new DateTime(2026, 4, 30),
            Purpose = "Оплата по счету №33",
        });
        Assert.IsType<OkObjectResult>(updateAction);

        var syncedOrder = await db.CashOrders.SingleAsync();
        Assert.Equal(30_000m, syncedOrder.Amount);
        Assert.Equal(new DateTime(2026, 4, 30), syncedOrder.Date);
        Assert.Equal("Основная касса", syncedOrder.Account);
    }

    [Fact]
    public async Task CashOrdersPaged_SearchFindsCyrillicNameIgnoringFirstLetterCase()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.Add(new CashOrder
        {
            Number = "ПКО-2026-0001",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 4, 29),
            Amount = 25_000m,
            Currency = "RUB",
            PayerName = "Ахмедова Айтан Рафик Кызы",
            Basis = "Оплата от клиента. счет №33",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = new CashOrdersController(db);
        var action = await controller.GetPaged(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            type: null,
            status: null,
            search: "ахмедова",
            page: 1,
            pageSize: 25,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var page = Assert.IsType<PagedResponse<CashOrderDto>>(ok.Value);
        var order = Assert.Single(page.Items);
        Assert.Equal("ПКО-2026-0001", order.Number);
    }

    [Fact]
    public async Task CashOrdersPaged_FiltersByStatus()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-0001",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 29),
                Amount = 25_000m,
                Currency = "RUB",
                PayerName = "Ахмедова Айтан Рафик Кызы",
                Basis = "Оплата от клиента. счет №33",
                CreatedAt = DateTime.UtcNow,
            },
            new CashOrder
            {
                Number = "ПКО-2026-0002",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Cancelled,
                Date = new DateTime(2026, 4, 30),
                Amount = 5_000m,
                Currency = "RUB",
                PayerName = "Ахмедова Айтан Рафик Кызы",
                Basis = "Ошибочный ордер",
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = new CashOrdersController(db);
        var action = await controller.GetPaged(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            type: null,
            status: CashOrderStatus.Cancelled,
            search: null,
            page: 1,
            pageSize: 25,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var page = Assert.IsType<PagedResponse<CashOrderDto>>(ok.Value);
        var order = Assert.Single(page.Items);
        Assert.Equal("ПКО-2026-0002", order.Number);
        Assert.Equal(CashOrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public async Task CashOrder_UpdateCancelAndCopy_Workflow()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.Add(new CashOrder
        {
            Number = "ПКО-2026-0001",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 4, 29),
            Amount = 25_000m,
            Currency = "RUB",
            PayerName = "Ахмедова Айтан Рафик Кызы",
            ReceiverName = "Касса",
            Basis = "Оплата по счёту №33",
            Appendix = "Квитанция",
            Account = "Основная касса",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = new CashOrdersController(db);
        var source = await db.CashOrders.SingleAsync();
        var updateAction = await controller.Update(source.Id, new CashOrder
        {
            Type = CashOrderType.Incoming,
            Date = new DateTime(2026, 4, 30),
            Amount = 30_000m,
            Currency = "RUB",
            PayerName = "Ахмедова Айтан Рафик Кызы",
            ReceiverName = "Основная касса",
            Basis = "Оплата и доплата",
            Account = "Касса",
            Appendix = "Скан заявления",
            Comment = "Проверено",
        }, CancellationToken.None);

        var updateOk = Assert.IsType<OkObjectResult>(updateAction);
        var updated = Assert.IsType<CashOrderDto>(updateOk.Value);
        Assert.Equal(30_000m, updated.Amount);
        Assert.Equal(new DateTime(2026, 4, 30), updated.Date);
        Assert.Equal("Основная касса", updated.ReceiverName);
        Assert.Equal("Скан заявления", updated.Appendix);
        Assert.Equal("Проверено", updated.Comment);

        var copyAction = await controller.Copy(source.Id, CancellationToken.None);
        var copyCreated = Assert.IsType<CreatedResult>(copyAction);
        var copied = Assert.IsType<CashOrderDto>(copyCreated.Value);
        Assert.NotEqual(source.Id, copied.Id);
        Assert.StartsWith("ПКО-", copied.Number);
        Assert.Equal(30_000m, copied.Amount);
        Assert.Equal("Основная касса", copied.ReceiverName);
        Assert.Equal("Скан заявления", copied.Appendix);
        Assert.Null(copied.PaymentId);
        Assert.Null(copied.MoneyOperationId);
        Assert.Null(copied.CancelledAt);
        Assert.Null(copied.CancelledReason);

        var cancelAction = await controller.Cancel(source.Id, new CashOrderCancelRequest { Reason = "Ошибочный документ" }, CancellationToken.None);
        var cancelOk = Assert.IsType<OkObjectResult>(cancelAction);
        var cancelled = Assert.IsType<CashOrderDto>(cancelOk.Value);
        Assert.Equal(CashOrderStatus.Cancelled, cancelled.Status);
        Assert.NotNull(cancelled.CancelledAt);
        Assert.Equal("Ошибочный документ", cancelled.CancelledReason);

        var cancelledUpdateAction = await controller.Update(source.Id, new CashOrder
        {
            Type = CashOrderType.Incoming,
            Date = new DateTime(2026, 5, 1),
            Amount = 1_000m,
        }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(cancelledUpdateAction);
    }

    [Fact]
    public async Task CashOrder_DownloadPdf_ReturnsPdfFile()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.Add(new CashOrder
        {
            Number = "ПКО-2026-0001",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 4, 29),
            Amount = 25_000m,
            Currency = "RUB",
            PayerName = "Ахмедова Айтан Рафик Кызы",
            Basis = "Оплата по счёту №33",
            Account = "Основная касса",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = new CashOrdersController(db, new FakeInvoiceDocumentPdfService("%PDF-cash%"u8.ToArray()));
        var order = await db.CashOrders.SingleAsync();

        var action = await controller.DownloadPdf(order.Id, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(action);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("%PDF-cash%"u8.ToArray(), file.FileContents);
        Assert.Equal("cash-order-ПКО-2026-0001.pdf", file.FileDownloadName);
    }

    [Fact]
    public async Task CashOrder_Print_ReturnsAccountableFormWithAmountInWords()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.Add(new CashOrder
        {
            Number = "ПКО-2026-0001",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 4, 29),
            Amount = 25_001.25m,
            Currency = "RUB",
            PayerName = "Ахмедова Айтан Рафик Кызы",
            ReceiverName = "Основная касса",
            Basis = "Оплата по счёту №33",
            Account = "Основная касса",
            Appendix = "Договор и счёт",
            Comment = "Без замечаний",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = new CashOrdersController(db);
        var order = await db.CashOrders.SingleAsync();

        var action = await controller.Print(order.Id, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(action);
        Assert.Equal("text/html; charset=utf-8", content.ContentType);
        Assert.Contains("Приходный кассовый ордер", content.Content);
        Assert.Contains("Квитанция к приходный кассовый ордер", content.Content);
        Assert.Contains("Двадцать пять тысяч один рубль 25 копеек", content.Content);
        Assert.Contains("Принято от", content.Content);
        Assert.Contains("Ахмедова Айтан Рафик Кызы", content.Content);
        Assert.Contains("Основная касса", content.Content);
        Assert.Contains("Договор и счёт", content.Content);
        Assert.Contains("Без замечаний", content.Content);
    }

    [Fact]
    public async Task CashOrder_CashBook_ReturnsPeriodBalancesByDay()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-0001",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 1),
                Amount = 10_000m,
                Currency = "RUB",
                CreatedAt = DateTime.UtcNow,
            },
            new CashOrder
            {
                Number = "ПКО-2026-0002",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 29),
                Amount = 25_000m,
                Currency = "RUB",
                CreatedAt = DateTime.UtcNow,
            },
            new CashOrder
            {
                Number = "РКО-2026-0001",
                Type = CashOrderType.Outgoing,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 30),
                Amount = 3_000m,
                Currency = "RUB",
                CreatedAt = DateTime.UtcNow,
            },
            new CashOrder
            {
                Number = "ПКО-2026-0003",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Cancelled,
                Date = new DateTime(2026, 4, 29),
                Amount = 99_000m,
                Currency = "RUB",
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = new CashOrdersController(db);
        var action = await controller.GetCashBook(new DateTime(2026, 4, 29), new DateTime(2026, 4, 30), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var book = Assert.IsType<CashBookResponseDto>(ok.Value);
        Assert.Equal(10_000m, book.OpeningBalance);
        Assert.Equal(25_000m, book.Income);
        Assert.Equal(3_000m, book.Expense);
        Assert.Equal(32_000m, book.ClosingBalance);
        Assert.Equal(1, book.IncomingCount);
        Assert.Equal(1, book.OutgoingCount);
        Assert.Collection(
            book.Days,
            day =>
            {
                Assert.Equal(new DateTime(2026, 4, 29), day.Date);
                Assert.Equal(10_000m, day.OpeningBalance);
                Assert.Equal(35_000m, day.ClosingBalance);
            },
            day =>
            {
                Assert.Equal(new DateTime(2026, 4, 30), day.Date);
                Assert.Equal(35_000m, day.OpeningBalance);
                Assert.Equal(32_000m, day.ClosingBalance);
            });
    }

    [Fact]
    public async Task CashOrder_Register_ReturnsPagedRowsAndSummary()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-0001",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 29),
                Amount = 25_000m,
                Currency = "RUB",
                PayerName = "Ахмедова Айтан Рафик Кызы",
                Basis = "Оплата по счёту №33",
                CreatedAt = DateTime.UtcNow,
            },
            new CashOrder
            {
                Number = "РКО-2026-0001",
                Type = CashOrderType.Outgoing,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 30),
                Amount = 3_000m,
                Currency = "RUB",
                PayerName = "Касса",
                Basis = "Выдача из кассы",
                CreatedAt = DateTime.UtcNow,
            },
            new CashOrder
            {
                Number = "ПКО-2026-0002",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Cancelled,
                Date = new DateTime(2026, 4, 30),
                Amount = 1_000m,
                Currency = "RUB",
                PayerName = "Ахмедова Айтан Рафик Кызы",
                Basis = "Ошибочный документ",
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = new CashOrdersController(db);
        var action = await controller.GetRegister(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            type: null,
            status: null,
            search: "Ахмедова",
            page: 1,
            pageSize: 10,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var register = Assert.IsType<CashOrderRegisterResponseDto>(ok.Value);
        Assert.Equal(2, register.Total);
        Assert.Equal(2, register.IncomingCount);
        Assert.Equal(0, register.OutgoingCount);
        Assert.Equal(1, register.PostedCount);
        Assert.Equal(1, register.CancelledCount);
        Assert.Equal(25_000m, register.IncomingAmount);
        Assert.Equal(0m, register.OutgoingAmount);
        Assert.Equal(["ПКО-2026-0002", "ПКО-2026-0001"], register.Items.Select(item => item.Number).ToArray());
    }

    [Fact]
    public async Task CashOrder_PrintCashBook_ReturnsHtml()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.Add(new CashOrder
        {
            Number = "ПКО-2026-0001",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 4, 29),
            Amount = 25_000m,
            Currency = "RUB",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = new CashOrdersController(db);
        var action = await controller.PrintCashBook(new DateTime(2026, 4, 29), new DateTime(2026, 4, 29), CancellationToken.None);

        var content = Assert.IsType<ContentResult>(action);
        Assert.Equal("text/html; charset=utf-8", content.ContentType);
        Assert.Contains("Кассовая книга", content.Content);
        Assert.Contains("25", content.Content);
    }

    [Fact]
    public async Task CashOrder_PrintRegister_ReturnsHtml()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.Add(new CashOrder
        {
            Number = "ПКО-2026-0001",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 4, 29),
            Amount = 25_000m,
            Currency = "RUB",
            PayerName = "Ахмедова Айтан Рафик Кызы",
            Basis = "Оплата по счёту №33",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = new CashOrdersController(db);
        var action = await controller.PrintRegister(
            from: new DateTime(2026, 4, 29),
            to: new DateTime(2026, 4, 29),
            type: null,
            status: null,
            search: null,
            ct: CancellationToken.None);

        var content = Assert.IsType<ContentResult>(action);
        Assert.Equal("text/html; charset=utf-8", content.ContentType);
        Assert.Contains("Журнал регистрации кассовых документов", content.Content);
        Assert.Contains("ПКО-2026-0001", content.Content);
        Assert.Contains("Ахмедова Айтан Рафик Кызы", content.Content);
    }

    [Fact]
    public async Task ImportBankStatement_CreatesBankFactOperationsAndSkipsDuplicates()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db);
        var file = CreateFormFile(
            "Дата;Сумма;Контрагент;Назначение;Номер\n" +
            "29.04.2026;25000;Ахмедова Айтан Рафик Кызы;Оплата по счету №33;101\n" +
            "29.04.2026;25000;Ахмедова Айтан Рафик Кызы;Оплата по счету №33;101\n" +
            "30.04.2026;-1200;Поставщик;Комиссия банка;102\n",
            "statement.csv");

        var action = await controller.ImportBankStatement(file, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<BankStatementImportResultDto>(ok.Value);
        Assert.Equal(3, result.RowsRead);
        Assert.Equal(2, result.Created);
        Assert.Equal(1, result.Skipped);
        Assert.NotNull(result.BatchId);

        var operations = await db.MoneyOperations.OrderBy(item => item.Date).ToListAsync();
        Assert.Collection(
            operations,
            income =>
            {
                Assert.Equal(MoneyOperationSource.Bank, income.Source);
                Assert.Equal(MoneyOperationDirection.Income, income.Direction);
                Assert.Equal(25_000m, income.Amount);
                Assert.Equal("Ахмедова Айтан Рафик Кызы", income.CounterpartyName);
                Assert.Equal("101", income.PaymentOrderNumber);
            },
            expense =>
            {
                Assert.Equal(MoneyOperationDirection.Expense, expense.Direction);
                Assert.Equal(1_200m, expense.Amount);
                Assert.Equal("Поставщик", expense.CounterpartyName);
            });

        var batch = await db.BankStatementImportBatches
            .Include(item => item.Rows)
            .SingleAsync(item => item.Id == result.BatchId);
        Assert.Equal("statement.csv", batch.FileName);
        Assert.Equal(BankStatementImportBatchStatus.Applied, batch.Status);
        Assert.Equal(3, batch.RowsRead);
        Assert.Equal(2, batch.CreatedRows);
        Assert.Equal(1, batch.SkippedRows);
        Assert.Equal(3, batch.Rows.Count);
        Assert.Equal(2, batch.Rows.Count(item => item.Status == BankStatementImportRowStatus.Created));
        Assert.Equal(1, batch.Rows.Count(item => item.Status == BankStatementImportRowStatus.Skipped));
        Assert.All(operations, operation => Assert.Equal(batch.Id, operation.BankStatementImportBatchId));

        var duplicateAction = await controller.ImportBankStatement(
            CreateFormFile(
                "Дата;Сумма;Контрагент;Назначение;Номер\n" +
                "29.04.2026;25000;Ахмедова Айтан Рафик Кызы;Оплата по счету №33;101\n",
                "statement.csv"),
            CancellationToken.None);
        var duplicateOk = Assert.IsType<OkObjectResult>(duplicateAction.Result);
        var duplicateResult = Assert.IsType<BankStatementImportResultDto>(duplicateOk.Value);
        Assert.Equal(0, duplicateResult.Created);
        Assert.Equal(1, duplicateResult.Skipped);
    }

    [Fact]
    public async Task RollbackBankStatementImport_RemovesCreatedOperationsAndMarksRows()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db);
        var importAction = await controller.ImportBankStatement(
            CreateFormFile(
                "Дата;Сумма;Контрагент;Назначение;Номер\n" +
                "29.04.2026;25000;Ахмедова Айтан Рафик Кызы;Оплата по счету №33;101\n",
                "statement.csv"),
            CancellationToken.None);
        var importOk = Assert.IsType<OkObjectResult>(importAction.Result);
        var importResult = Assert.IsType<BankStatementImportResultDto>(importOk.Value);
        Assert.NotNull(importResult.BatchId);

        var rollbackAction = await controller.RollbackBankStatementImport(importResult.BatchId.Value, CancellationToken.None);

        var rollbackOk = Assert.IsType<OkObjectResult>(rollbackAction.Result);
        var rollbackResult = Assert.IsType<BankStatementImportRollbackResultDto>(rollbackOk.Value);
        Assert.Equal(BankStatementImportBatchStatus.RolledBack, rollbackResult.Status);
        Assert.Equal(1, rollbackResult.DeletedOperations);
        Assert.Equal(0, rollbackResult.SkippedRows);
        Assert.Empty(await db.MoneyOperations.ToListAsync());

        var batch = await db.BankStatementImportBatches.Include(item => item.Rows).SingleAsync();
        Assert.Equal(BankStatementImportBatchStatus.RolledBack, batch.Status);
        Assert.Equal(1, batch.RollbackRows);
        var row = Assert.Single(batch.Rows);
        Assert.Equal(BankStatementImportRowStatus.RolledBack, row.Status);
        Assert.NotNull(row.RolledBackAt);
    }

    [Fact]
    public async Task RollbackBankStatementImport_SkipsMatchedOperations()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db);
        var importAction = await controller.ImportBankStatement(
            CreateFormFile(
                "Дата;Сумма;Контрагент;Назначение;Номер\n" +
                "29.04.2026;25000;Ахмедова Айтан Рафик Кызы;Оплата по счету №33;101\n",
                "statement.csv"),
            CancellationToken.None);
        var importOk = Assert.IsType<OkObjectResult>(importAction.Result);
        var importResult = Assert.IsType<BankStatementImportResultDto>(importOk.Value);
        var operation = await db.MoneyOperations.SingleAsync();
        operation.MatchStatus = MoneyOperationMatchStatus.Matched;
        await db.SaveChangesAsync();

        var rollbackAction = await controller.RollbackBankStatementImport(importResult.BatchId!.Value, CancellationToken.None);

        var rollbackOk = Assert.IsType<OkObjectResult>(rollbackAction.Result);
        var rollbackResult = Assert.IsType<BankStatementImportRollbackResultDto>(rollbackOk.Value);
        Assert.Equal(BankStatementImportBatchStatus.RollbackBlocked, rollbackResult.Status);
        Assert.Equal(0, rollbackResult.DeletedOperations);
        Assert.Equal(1, rollbackResult.SkippedRows);
        Assert.Single(await db.MoneyOperations.ToListAsync());

        var row = await db.BankStatementImportRows.SingleAsync();
        Assert.Equal(BankStatementImportRowStatus.RollbackSkipped, row.Status);
        Assert.Contains("сверена", row.Message);
    }

    [Fact]
    public async Task ImportBankStatement_AutoMatchesSingleInvoiceByNumberInPurpose()
    {
        await using var db = await CreateContextAsync();
        var invoice = new Invoice
        {
            Number = "33",
            Date = new DateTime(2026, 4, 29),
            CreatedAt = DateTime.UtcNow,
        };
        db.Payments.Add(new Payment
        {
            Invoice = invoice,
            Account = "33",
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 25_000m,
            PaidAmount = 0m,
            Date = new DateTime(2026, 4, 29),
            CounterpartyName = "Ахмедова Айтан Рафик Кызы",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.ImportBankStatement(
            CreateFormFile(
                "Дата;Сумма;Контрагент;Назначение;Номер\n" +
                "29.04.2026;25000;Ахмедова Айтан Рафик Кызы;Оплата по счету №33;101\n",
                "statement.csv"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<BankStatementImportResultDto>(ok.Value);
        var operationDto = Assert.Single(result.Operations);
        Assert.Equal(MoneyOperationMatchStatus.Matched, operationDto.MatchStatus);

        var operation = await db.MoneyOperations.SingleAsync();
        Assert.Equal(invoice.Id, operation.LinkedInvoiceId);
        Assert.Equal(MoneyOperationMatchStatus.Matched, operation.MatchStatus);
        Assert.Equal("33", operation.PaidInvoiceNumber);
        Assert.Contains("Автоматически сверено", operation.MatchComment);

        var payment = await db.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(25_000m, payment.PaidAmount);
        Assert.True(payment.IsPaid);
        Assert.Contains("Деньги", payment.SystemNotes);

        var row = await db.BankStatementImportRows.SingleAsync();
        Assert.Equal(BankStatementImportRowStatus.Created, row.Status);
        Assert.Contains("Автоматически сверено", row.Message);
    }

    [Fact]
    public async Task ImportBankStatement_AutoMatchesSingleInvoiceByExactAmountAndClient()
    {
        await using var db = await CreateContextAsync();
        var client = new Client
        {
            Name = "Ахмедова Айтан Рафик Кызы",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var invoice = new Invoice
        {
            Number = "77",
            Date = new DateTime(2026, 4, 29),
            CreatedAt = DateTime.UtcNow,
        };
        db.Payments.Add(new Payment
        {
            Invoice = invoice,
            Client = client,
            Account = "77",
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 25_000m,
            PaidAmount = 0m,
            Date = new DateTime(2026, 4, 29),
            CounterpartyName = "Ахмедова Айтан Рафик Кызы",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.ImportBankStatement(
            CreateFormFile(
                "Дата;Сумма;Контрагент;Назначение;Номер\n" +
                "29.04.2026;25000;Ахмедова Айтан Рафик Кызы;Поступление от клиента;101\n",
                "statement.csv"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<BankStatementImportResultDto>(ok.Value);
        var operationDto = Assert.Single(result.Operations);
        Assert.Equal(MoneyOperationMatchStatus.Matched, operationDto.MatchStatus);

        var operation = await db.MoneyOperations.SingleAsync();
        Assert.Equal(invoice.Id, operation.LinkedInvoiceId);
        Assert.Equal(MoneyOperationMatchStatus.Matched, operation.MatchStatus);
        Assert.Equal("77", operation.PaidInvoiceNumber);
        Assert.Contains("по сумме", operation.MatchComment);
    }

    [Fact]
    public async Task ImportBankStatement_DoesNotAutoMatchAmbiguousAmountAndClient()
    {
        await using var db = await CreateContextAsync();
        var client = new Client
        {
            Name = "Ахмедова Айтан Рафик Кызы",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.Payments.AddRange(
            new Payment
            {
                Invoice = new Invoice { Number = "77", Date = new DateTime(2026, 4, 1), CreatedAt = DateTime.UtcNow },
                Client = client,
                Account = "77",
                DocumentKind = DocumentKind.Invoice,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Amount = 25_000m,
                PaidAmount = 0m,
                Date = new DateTime(2026, 4, 1),
                CounterpartyName = "Ахмедова Айтан Рафик Кызы",
                CreatedAt = DateTime.UtcNow,
            },
            new Payment
            {
                Invoice = new Invoice { Number = "78", Date = new DateTime(2026, 4, 2), CreatedAt = DateTime.UtcNow },
                Client = client,
                Account = "78",
                DocumentKind = DocumentKind.Invoice,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Amount = 25_000m,
                PaidAmount = 0m,
                Date = new DateTime(2026, 4, 2),
                CounterpartyName = "Ахмедова Айтан Рафик Кызы",
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.ImportBankStatement(
            CreateFormFile(
                "Дата;Сумма;Контрагент;Назначение;Номер\n" +
                "29.04.2026;25000;Ахмедова Айтан Рафик Кызы;Поступление от клиента;101\n",
                "statement.csv"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<BankStatementImportResultDto>(ok.Value);
        var operationDto = Assert.Single(result.Operations);
        Assert.Equal(MoneyOperationMatchStatus.ClarificationRequired, operationDto.MatchStatus);

        var operation = await db.MoneyOperations.SingleAsync();
        Assert.Null(operation.LinkedInvoiceId);
        Assert.Equal(MoneyOperationMatchStatus.ClarificationRequired, operation.MatchStatus);
    }

    [Fact]
    public async Task ImportBankStatement_LeavesAmbiguousInvoiceNumberForClarification()
    {
        await using var db = await CreateContextAsync();
        db.Payments.AddRange(
            new Payment
            {
                Invoice = new Invoice { Number = "33", Date = new DateTime(2026, 4, 1), CreatedAt = DateTime.UtcNow },
                Account = "33",
                DocumentKind = DocumentKind.Invoice,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Amount = 25_000m,
                PaidAmount = 0m,
                Date = new DateTime(2026, 4, 1),
                CounterpartyName = "Первый клиент",
                CreatedAt = DateTime.UtcNow,
            },
            new Payment
            {
                Invoice = new Invoice { Number = "33", Date = new DateTime(2026, 4, 2), CreatedAt = DateTime.UtcNow },
                Account = "33",
                DocumentKind = DocumentKind.Invoice,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Amount = 25_000m,
                PaidAmount = 0m,
                Date = new DateTime(2026, 4, 2),
                CounterpartyName = "Второй клиент",
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.ImportBankStatement(
            CreateFormFile(
                "Дата;Сумма;Контрагент;Назначение;Номер\n" +
                "29.04.2026;25000;Ахмедова Айтан Рафик Кызы;Оплата по счету №33;101\n",
                "statement.csv"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<BankStatementImportResultDto>(ok.Value);
        var operationDto = Assert.Single(result.Operations);
        Assert.Equal(MoneyOperationMatchStatus.ClarificationRequired, operationDto.MatchStatus);

        var operation = await db.MoneyOperations.SingleAsync();
        Assert.Null(operation.LinkedInvoiceId);
        Assert.Equal(MoneyOperationMatchStatus.ClarificationRequired, operation.MatchStatus);
    }

    [Fact]
    public async Task GetInvoiceCandidates_ReturnsScoredCandidatesForBankOperation()
    {
        await using var db = await CreateContextAsync();
        var client = new Client
        {
            Name = "Ахмедова Айтан Рафик Кызы",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 25_000m,
            Date = new DateTime(2026, 4, 29),
            CounterpartyName = "Ахмедова Айтан Рафик Кызы",
            Purpose = "Оплата по счету №33",
            MatchStatus = MoneyOperationMatchStatus.ClarificationRequired,
            CreatedAt = DateTime.UtcNow,
        };
        var invoice = new Invoice
        {
            Number = "33",
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyOperations.Add(operation);
        db.Payments.Add(new Payment
        {
            Invoice = invoice,
            Client = client,
            Account = "33",
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 25_000m,
            PaidAmount = 0m,
            Date = new DateTime(2026, 4, 20),
            CounterpartyName = "Ахмедова Айтан Рафик Кызы",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.GetInvoiceCandidates(operation.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var candidate = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationInvoiceCandidateDto>>(ok.Value));
        Assert.Equal(invoice.Id, candidate.InvoiceId);
        Assert.Equal("33", candidate.InvoiceNumber);
        Assert.Equal(25_000m, candidate.OutstandingAmount);
        Assert.True(candidate.Score >= 160);
        Assert.Contains("номер счёта", candidate.MatchReason);
    }

    [Fact]
    public async Task GetInvoiceCandidates_ReturnsCandidatesForEMoneyIncomeOperation()
    {
        await using var db = await CreateContextAsync();
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.EMoney,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 7_500m,
            Date = new DateTime(2026, 4, 29),
            CounterpartyName = "Ахмедова Айтан Рафик Кызы",
            Purpose = "ЮMoney, оплата по счету №44",
            MatchStatus = MoneyOperationMatchStatus.ClarificationRequired,
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyOperations.Add(operation);
        db.Payments.Add(new Payment
        {
            Invoice = new Invoice { Number = "44", Date = new DateTime(2026, 4, 20), CreatedAt = DateTime.UtcNow },
            Account = "44",
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 7_500m,
            PaidAmount = 0m,
            Date = new DateTime(2026, 4, 20),
            CounterpartyName = "Ахмедова Айтан Рафик Кызы",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.GetInvoiceCandidates(operation.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var candidate = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationInvoiceCandidateDto>>(ok.Value));
        Assert.Equal("44", candidate.InvoiceNumber);
        Assert.Equal(7_500m, candidate.OutstandingAmount);
    }

    [Fact]
    public async Task GetInvoiceCandidates_ExcludesFullyPaidInvoices()
    {
        await using var db = await CreateContextAsync();
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 25_000m,
            Date = new DateTime(2026, 4, 29),
            Purpose = "Оплата по счету №33",
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyOperations.Add(operation);
        db.Payments.Add(new Payment
        {
            Invoice = new Invoice { Number = "33", Date = new DateTime(2026, 4, 20), CreatedAt = DateTime.UtcNow },
            Account = "33",
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Amount = 25_000m,
            PaidAmount = 25_000m,
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.GetInvoiceCandidates(operation.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationInvoiceCandidateDto>>(ok.Value));
    }

    [Fact]
    public async Task IgnoreMoneyOperation_ClearsInvoiceLinkAndMarksAsIgnored()
    {
        await using var db = await CreateContextAsync();
        var invoice = new Invoice
        {
            Number = "33",
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 25_000m,
            Date = new DateTime(2026, 4, 29),
            LinkedInvoiceId = invoice.Id,
            PaidInvoiceNumber = "33",
            PaidAccountName = "33",
            MatchStatus = MoneyOperationMatchStatus.ClarificationRequired,
            MatchedAt = null,
            MatchComment = "Связано ошибочно",
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Ignore(operation.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var result = Assert.IsType<MoneyOperation>(ok.Value);
        Assert.Equal(MoneyOperationMatchStatus.Ignored, result.MatchStatus);
        Assert.Null(result.LinkedInvoiceId);
        Assert.Null(result.PaidInvoiceNumber);
        Assert.Null(result.PaidAccountName);
        Assert.Null(result.MatchedAt);
        Assert.NotNull(result.UpdatedAt);
        Assert.Equal(MoneyOperationMatchStatus.Ignored, (await db.MoneyOperations.SingleAsync()).MatchStatus);
    }

    [Fact]
    public async Task IgnoreMoneyOperation_ReturnsBadRequest_WhenOperationAlreadyAppliedToInvoice()
    {
        await using var db = await CreateContextAsync();
        var invoice = new Invoice
        {
            Number = "33",
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 25_000m,
            Date = new DateTime(2026, 4, 29),
            LinkedInvoiceId = invoice.Id,
            PaidInvoiceNumber = "33",
            PaidAccountName = "33",
            MatchStatus = MoneyOperationMatchStatus.Matched,
            MatchedAt = DateTime.UtcNow,
            MatchComment = "Сверено",
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Ignore(operation.Id, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action);
        var saved = await db.MoneyOperations.SingleAsync();
        Assert.Equal(invoice.Id, saved.LinkedInvoiceId);
        Assert.Equal(MoneyOperationMatchStatus.Matched, saved.MatchStatus);
    }

    [Fact]
    public async Task LinkInvoice_RejectsOperationAmountGreaterThanInvoiceOutstandingAmount()
    {
        await using var db = await CreateContextAsync();
        var invoice = new Invoice
        {
            Number = "33",
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        };
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 25_000m,
            Date = new DateTime(2026, 4, 29),
            MatchStatus = MoneyOperationMatchStatus.ClarificationRequired,
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyOperations.Add(operation);
        db.Payments.Add(new Payment
        {
            Invoice = invoice,
            Account = "33",
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 25_000m,
            PaidAmount = 10_000m,
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.LinkInvoice(
            operation.Id,
            new MoneyOperationsController.LinkMoneyOperationInvoiceRequest { InvoiceId = invoice.Id },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Contains("Остаток", Assert.IsType<string>(badRequest.Value));
        var unchanged = await db.MoneyOperations.SingleAsync();
        Assert.Null(unchanged.LinkedInvoiceId);
        Assert.Equal(MoneyOperationMatchStatus.ClarificationRequired, unchanged.MatchStatus);
    }

    [Fact]
    public async Task LinkInvoice_WithOverpayment_ClosesInvoiceAndCreditsClientBalance()
    {
        await using var db = await CreateContextAsync();
        var client = new Client
        {
            Name = "Ахмедова Айтан Рафик Кызы",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var invoice = new Invoice
        {
            Number = "33",
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        };
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 17_000m,
            Date = new DateTime(2026, 4, 29),
            MatchStatus = MoneyOperationMatchStatus.ClarificationRequired,
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyOperations.Add(operation);
        db.Payments.Add(new Payment
        {
            Invoice = invoice,
            Client = client,
            Account = "33",
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 25_000m,
            PaidAmount = 10_000m,
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.LinkInvoice(
            operation.Id,
            new MoneyOperationsController.LinkMoneyOperationInvoiceRequest { InvoiceId = invoice.Id },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var result = Assert.IsType<MoneyOperation>(ok.Value);
        Assert.Equal(invoice.Id, result.LinkedInvoiceId);
        Assert.Equal(MoneyOperationMatchStatus.Matched, result.MatchStatus);

        var payment = await db.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(25_000m, payment.PaidAmount);
        Assert.True(payment.IsPaid);

        var balanceTransaction = await db.ClientBalanceTransactions.SingleAsync();
        Assert.Equal(client.Id, balanceTransaction.ClientId);
        Assert.Equal(invoice.Id, balanceTransaction.InvoiceId);
        Assert.Equal(payment.Id, balanceTransaction.PaymentId);
        Assert.Equal(2_000m, balanceTransaction.Amount);
        Assert.Equal(ClientBalanceTransactionType.Overpayment, balanceTransaction.Type);
        Assert.Equal(ClientBalanceDirection.Credit, balanceTransaction.Direction);
        Assert.Equal($"money-operation-overpayment:{operation.Id}", balanceTransaction.ExternalKey);
    }

    [Fact]
    public async Task ApplyClientBalanceCreditAfterMoneyOperationOverpayment_DoesNotCreateNewMoneyOperation()
    {
        await using var db = await CreateContextAsync();
        var client = new Client
        {
            Name = "Ахмедова Айтан Рафик Кызы",
            CreatedAt = DateTime.UtcNow,
        };
        var firstInvoice = new Invoice
        {
            Number = "33",
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        };
        var secondInvoice = new Invoice
        {
            Number = "34",
            Date = new DateTime(2026, 4, 21),
            CreatedAt = DateTime.UtcNow,
        };
        var firstPayment = new Payment
        {
            Client = client,
            Invoice = firstInvoice,
            Account = "33",
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 15_000m,
            PaidAmount = 0m,
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        };
        var secondPayment = new Payment
        {
            Client = client,
            Invoice = secondInvoice,
            Account = "34",
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 10_000m,
            PaidAmount = 0m,
            Date = new DateTime(2026, 4, 21),
            CreatedAt = DateTime.UtcNow,
        };
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 17_000m,
            Date = new DateTime(2026, 4, 29),
            MatchStatus = MoneyOperationMatchStatus.ClarificationRequired,
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyOperations.Add(operation);
        db.Payments.AddRange(firstPayment, secondPayment);
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var linkAction = await controller.LinkInvoice(
            operation.Id,
            new MoneyOperationsController.LinkMoneyOperationInvoiceRequest { InvoiceId = firstInvoice.Id },
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(linkAction);

        var applyResult = await ClientBalanceService.ApplyCreditAsync(
            db,
            client.Id,
            secondPayment.Id,
            1_500m,
            new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc),
            httpContext: null,
            CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.True(applyResult.Success);
        Assert.Equal(1_500m, applyResult.AppliedAmount);
        Assert.Equal(1, await db.MoneyOperations.CountAsync());
        Assert.Equal(500m, await ClientBalanceService.GetBalanceAsync(db, client.Id));

        var refreshedSecondPayment = await db.Payments.SingleAsync(item => item.Id == secondPayment.Id);
        Assert.Equal(1_500m, refreshedSecondPayment.PaidAmount);
        Assert.Equal(PaymentStatus.Overdue, refreshedSecondPayment.Status);
        Assert.Contains("Зачтено с баланса клиента", refreshedSecondPayment.SystemNotes);

        var balanceTransactions = await db.ClientBalanceTransactions
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, balanceTransactions.Count);
        Assert.Equal(ClientBalanceTransactionType.Overpayment, balanceTransactions[0].Type);
        Assert.Equal(ClientBalanceDirection.Credit, balanceTransactions[0].Direction);
        Assert.Equal(ClientBalanceTransactionType.CreditApplied, balanceTransactions[1].Type);
        Assert.Equal(ClientBalanceDirection.Debit, balanceTransactions[1].Direction);
    }

    [Fact]
    public async Task LinkInvoice_MarksBankOperationAsMatched_WhenAmountFitsOutstanding()
    {
        await using var db = await CreateContextAsync();
        var invoice = new Invoice
        {
            Number = "33",
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        };
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 15_000m,
            Date = new DateTime(2026, 4, 29),
            MatchStatus = MoneyOperationMatchStatus.ClarificationRequired,
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyOperations.Add(operation);
        db.Payments.Add(new Payment
        {
            Invoice = invoice,
            Account = "33",
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 25_000m,
            PaidAmount = 10_000m,
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.LinkInvoice(
            operation.Id,
            new MoneyOperationsController.LinkMoneyOperationInvoiceRequest
            {
                InvoiceId = invoice.Id,
                Comment = "Проверено оператором",
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var result = Assert.IsType<MoneyOperation>(ok.Value);
        Assert.Equal(invoice.Id, result.LinkedInvoiceId);
        Assert.Equal(MoneyOperationMatchStatus.Matched, result.MatchStatus);
        Assert.Equal("Проверено оператором", result.MatchComment);

        var payment = await db.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(25_000m, payment.PaidAmount);
        Assert.True(payment.IsPaid);
        Assert.Contains("Деньги", payment.SystemNotes);
    }

    [Fact]
    public async Task LinkInvoice_AppliesPartialMoneyOperationToInvoicePayment()
    {
        await using var db = await CreateContextAsync();
        var invoice = new Invoice
        {
            Number = "33",
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        };
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 5_000m,
            Date = new DateTime(2026, 4, 29),
            MatchStatus = MoneyOperationMatchStatus.ClarificationRequired,
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyOperations.Add(operation);
        db.Payments.Add(new Payment
        {
            Invoice = invoice,
            Account = "33",
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 25_000m,
            PaidAmount = 10_000m,
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.LinkInvoice(
            operation.Id,
            new MoneyOperationsController.LinkMoneyOperationInvoiceRequest { InvoiceId = invoice.Id },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.IsType<MoneyOperation>(ok.Value);

        var payment = await db.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.Processing, payment.Status);
        Assert.Equal(15_000m, payment.PaidAmount);
        Assert.False(payment.IsPaid);
        Assert.Equal(new DateTime(2026, 4, 29), payment.LastPaymentDate);
    }

    [Fact]
    public async Task LinkInvoice_MarksEMoneyOperationAsMatched_WhenAmountFitsOutstanding()
    {
        await using var db = await CreateContextAsync();
        var invoice = new Invoice
        {
            Number = "44",
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        };
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.EMoney,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 7_500m,
            Date = new DateTime(2026, 4, 29),
            MatchStatus = MoneyOperationMatchStatus.ClarificationRequired,
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyOperations.Add(operation);
        db.Payments.Add(new Payment
        {
            Invoice = invoice,
            Account = "44",
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 7_500m,
            PaidAmount = 0m,
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.LinkInvoice(
            operation.Id,
            new MoneyOperationsController.LinkMoneyOperationInvoiceRequest { InvoiceId = invoice.Id },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var result = Assert.IsType<MoneyOperation>(ok.Value);
        Assert.Equal(invoice.Id, result.LinkedInvoiceId);
        Assert.Equal(MoneyOperationMatchStatus.Matched, result.MatchStatus);
        Assert.Equal("44", result.PaidInvoiceNumber);
        Assert.Contains("Денежная операция", result.MatchComment);
    }

    [Fact]
    public async Task ImportBankStatement_ReadsWindows1251AndIncomeExpenseColumns()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1251).GetBytes(
            "Дата операции;Поступило;Списано;Плательщик;Назначение платежа;Документ\n" +
            "29.04.2026;25 000,50;;Ахмедова Айтан Рафик Кызы;Оплата по счету №33;201\n" +
            "30.04.2026;;1 200,25;Банк;Комиссия;202\n");

        var action = await controller.ImportBankStatement(CreateFormFile(bytes, "statement.txt"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<BankStatementImportResultDto>(ok.Value);
        Assert.Equal(2, result.Created);

        var operations = await db.MoneyOperations.OrderBy(item => item.Date).ToListAsync();
        Assert.Collection(
            operations,
            income =>
            {
                Assert.Equal(MoneyOperationDirection.Income, income.Direction);
                Assert.Equal(25_000.50m, income.Amount);
                Assert.Equal("Ахмедова Айтан Рафик Кызы", income.CounterpartyName);
            },
            expense =>
            {
                Assert.Equal(MoneyOperationDirection.Expense, expense.Direction);
                Assert.Equal(1_200.25m, expense.Amount);
                Assert.Equal("Банк", expense.CounterpartyName);
            });
    }

    [Fact]
    public async Task ImportBankStatement_ReadsClientBankExchangeTxt()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db);
        var content =
            "1CClientBankExchange\n" +
            "ВерсияФормата=1.03\n" +
            "РасчСчет=40702810900000000001\n" +
            "СекцияДокумент=Платежное поручение\n" +
            "Номер=401\n" +
            "Дата=29.04.2026\n" +
            "Сумма=25000\n" +
            "Плательщик=Ахмедова Айтан Рафик Кызы\n" +
            "ПлательщикСчет=40817810000000000001\n" +
            "Получатель=ООО ЭлПит\n" +
            "ПолучательСчет=40702810900000000001\n" +
            "НазначениеПлатежа=Оплата по счету №33\n" +
            "КонецДокумента\n" +
            "СекцияДокумент=Платежное поручение\n" +
            "Номер=402\n" +
            "Дата=30.04.2026\n" +
            "Сумма=1200\n" +
            "Плательщик=ООО ЭлПит\n" +
            "ПлательщикСчет=40702810900000000001\n" +
            "Получатель=Банк\n" +
            "ПолучательСчет=30101810000000000001\n" +
            "НазначениеПлатежа=Комиссия банка\n" +
            "КонецДокумента\n" +
            "КонецФайла\n";

        var action = await controller.ImportBankStatement(CreateFormFile(content, "1c-bank.txt"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<BankStatementImportResultDto>(ok.Value);
        Assert.Equal(2, result.RowsRead);
        Assert.Equal(2, result.Created);

        var operations = await db.MoneyOperations.OrderBy(item => item.Date).ToListAsync();
        Assert.Collection(
            operations,
            income =>
            {
                Assert.Equal(MoneyOperationDirection.Income, income.Direction);
                Assert.Equal(25_000m, income.Amount);
                Assert.Equal("Ахмедова Айтан Рафик Кызы", income.CounterpartyName);
                Assert.Equal("401", income.PaymentOrderNumber);
                Assert.Equal("40702810900000000001", income.AccountName);
            },
            expense =>
            {
                Assert.Equal(MoneyOperationDirection.Expense, expense.Direction);
                Assert.Equal(1_200m, expense.Amount);
                Assert.Equal("Банк", expense.CounterpartyName);
                Assert.Equal("402", expense.PaymentOrderNumber);
            });
    }

    [Fact]
    public async Task ImportBankStatement_SkipsExistingCompletedBankPayments()
    {
        await using var db = await CreateContextAsync();
        db.Payments.Add(new Payment
        {
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Amount = 25_000m,
            PaidAmount = 25_000m,
            Date = new DateTime(2026, 4, 29),
            PlannedDate = new DateTime(2026, 4, 29),
            PaidDate = new DateTime(2026, 4, 29),
            LastPaymentDate = new DateTime(2026, 4, 29),
            CounterpartyName = "Ахмедова Айтан Рафик Кызы",
            PaymentOrderNumber = "301",
            Description = "Оплата по счету №33",
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var action = await controller.ImportBankStatement(
            CreateFormFile(
                "Дата;Сумма;Контрагент;Назначение;Номер\n" +
                "29.04.2026;25000;Ахмедова Айтан Рафик Кызы;Оплата по счету №33;301\n",
                "statement.csv"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<BankStatementImportResultDto>(ok.Value);
        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(await db.MoneyOperations.ToListAsync());
    }

    [Fact]
    public async Task UpdateMoneyOperation_WithLinkedInvoice_MarksOperationAsMatched()
    {
        await using var db = await CreateContextAsync();
        var invoice = new Invoice
        {
            Number = "33",
            Date = new DateTime(2026, 4, 29),
            CreatedAt = DateTime.UtcNow,
        };
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            AccountName = "Основной счёт",
            CounterpartyName = "Ахмедова Айтан Рафик Кызы",
            Amount = 25_000m,
            Currency = "RUB",
            Date = new DateTime(2026, 4, 29),
            RecognitionDate = new DateTime(2026, 4, 29),
            MatchStatus = MoneyOperationMatchStatus.ClarificationRequired,
            CreatedAt = DateTime.UtcNow,
        };
        db.Invoices.Add(invoice);
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var updateAction = await controller.Update(operation.Id, new MoneyOperation
        {
            Source = operation.Source,
            Direction = operation.Direction,
            AccountingType = operation.AccountingType,
            AccountName = operation.AccountName,
            CounterpartyName = operation.CounterpartyName,
            Amount = operation.Amount,
            Currency = operation.Currency,
            Date = operation.Date,
            RecognitionDate = operation.RecognitionDate,
            LinkedInvoiceId = invoice.Id,
            MatchComment = "Оплата по выписке",
        });

        var ok = Assert.IsType<OkObjectResult>(updateAction);
        var updated = Assert.IsType<MoneyOperation>(ok.Value);
        Assert.Equal(invoice.Id, updated.LinkedInvoiceId);
        Assert.Equal("33", updated.PaidInvoiceNumber);
        Assert.Equal("33", updated.PaidAccountName);
        Assert.Equal(MoneyOperationMatchStatus.Matched, updated.MatchStatus);
        Assert.NotNull(updated.MatchedAt);

        var page = await GetMoneyPageAsync(controller, MoneyOperationSource.Bank);
        var item = Assert.Single(page.Items);
        Assert.Equal(invoice.Id, item.LinkedInvoiceId);
        Assert.Equal("33", item.LinkedInvoiceNumber);
        Assert.Equal(MoneyOperationMatchStatus.Matched, item.MatchStatus);
        Assert.Equal("Оплата по выписке", item.MatchComment);
    }

    [Fact]
    public async Task GetPaged_WithLinkedInvoiceId_ReturnsOnlyInvoiceMoneyOperations()
    {
        await using var db = await CreateContextAsync();
        var targetInvoice = new Invoice
        {
            Number = "33",
            Date = new DateTime(2026, 4, 29),
            CreatedAt = DateTime.UtcNow,
        };
        var otherInvoice = new Invoice
        {
            Number = "34",
            Date = new DateTime(2026, 4, 30),
            CreatedAt = DateTime.UtcNow,
        };

        db.Invoices.AddRange(targetInvoice, otherInvoice);
        await db.SaveChangesAsync();

        db.MoneyOperations.AddRange(
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                Amount = 25_000m,
                Date = new DateTime(2026, 4, 29),
                Purpose = "Оплата по счёту №33",
                LinkedInvoiceId = targetInvoice.Id,
                MatchStatus = MoneyOperationMatchStatus.Matched,
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                Amount = 10_000m,
                Date = new DateTime(2026, 4, 30),
                Purpose = "Оплата по счёту №34",
                LinkedInvoiceId = otherInvoice.Id,
                MatchStatus = MoneyOperationMatchStatus.Matched,
                CreatedAt = DateTime.UtcNow,
            });

        db.Payments.Add(new Payment
        {
            InvoiceId = targetInvoice.Id,
            Account = "33",
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Amount = 5_000m,
            PaidAmount = 5_000m,
            Date = new DateTime(2026, 4, 29),
            PaidDate = new DateTime(2026, 4, 29),
            Description = "Наличный платёж по счёту №33",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var action = await controller.GetPaged(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            source: null,
            direction: null,
            accountingType: null,
            accountName: null,
            linkedInvoiceId: targetInvoice.Id,
            search: null,
            page: 1,
            pageSize: 25);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var page = Assert.IsType<MoneyOperationsPageResponse>(ok.Value);

        Assert.Equal(2, page.Total);
        Assert.All(page.Items, item => Assert.Equal(targetInvoice.Id, item.LinkedInvoiceId));
        Assert.Contains(page.Items, item => item.IsReadonly && item.RelatedPaymentId.HasValue);
        Assert.Contains(page.Items, item => !item.IsReadonly && item.Purpose == "Оплата по счёту №33");
        Assert.DoesNotContain(page.Items, item => item.LinkedInvoiceId == otherInvoice.Id);
    }

    [Fact]
    public async Task ImportBankStatement_CreatesOperationsAsClarificationRequired()
    {
        await using var db = await CreateContextAsync();
        db.MoneyAccounts.Add(new MoneyAccount
        {
            Name = "Основной банк",
            Source = MoneyOperationSource.Bank,
            Currency = "RUB",
            IsDefault = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.ImportBankStatement(
            CreateFormFile(
                "Дата;Сумма;Счёт;Контрагент;Назначение;Номер\n" +
                "29.04.2026;25000;Основной банк;Ахмедова Айтан Рафик Кызы;Оплата по счёту №33;301\n",
                "statement.csv"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<BankStatementImportResultDto>(ok.Value);
        var operationDto = Assert.Single(result.Operations);
        Assert.Equal(MoneyOperationMatchStatus.ClarificationRequired, operationDto.MatchStatus);

        var operation = await db.MoneyOperations.SingleAsync();
        Assert.Equal(MoneyOperationMatchStatus.ClarificationRequired, operation.MatchStatus);
        Assert.NotNull(operation.MoneyAccountId);
        Assert.Equal("Основной банк", operation.AccountName);

        var page = await GetMoneyPageAsync(controller, MoneyOperationSource.Bank);
        var item = Assert.Single(page.Items);
        Assert.Equal(MoneyOperationMatchStatus.ClarificationRequired, item.MatchStatus);
    }

    [Fact]
    public async Task MoneyAccount_CreateOperation_BindsAccountAndFiltersById()
    {
        await using var db = await CreateContextAsync();
        var account = new MoneyAccount
        {
            Name = "Основной банк",
            Source = MoneyOperationSource.Bank,
            Currency = "RUB",
            IsDefault = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var otherAccount = new MoneyAccount
        {
            Name = "Резервный банк",
            Source = MoneyOperationSource.Bank,
            Currency = "RUB",
            IsDefault = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyAccounts.AddRange(account, otherAccount);
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var createAction = await controller.Create(new MoneyOperation
        {
            Source = MoneyOperationSource.Cash,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            MoneyAccountId = account.Id,
            AccountName = "Будет заменено",
            Amount = 25_000m,
            Currency = "",
            Date = new DateTime(2026, 4, 29),
            Purpose = "Оплата по счету №33",
        });

        var created = Assert.IsType<CreatedResult>(createAction);
        var operation = Assert.IsType<MoneyOperation>(created.Value);
        Assert.Equal(MoneyOperationSource.Bank, operation.Source);
        Assert.Equal(account.Id, operation.MoneyAccountId);
        Assert.Equal("Основной банк", operation.AccountName);
        Assert.Equal("RUB", operation.Currency);

        db.MoneyOperations.Add(new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            MoneyAccountId = otherAccount.Id,
            AccountName = otherAccount.Name,
            Amount = 1_000m,
            Currency = "RUB",
            Date = new DateTime(2026, 4, 29),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var pageAction = await controller.GetPaged(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            source: MoneyOperationSource.Bank,
            direction: null,
            accountingType: null,
            moneyAccountId: account.Id,
            accountName: null,
            search: null,
            page: 1,
            pageSize: 25);

        var ok = Assert.IsType<OkObjectResult>(pageAction.Result);
        var page = Assert.IsType<MoneyOperationsPageResponse>(ok.Value);
        var item = Assert.Single(page.Items);
        Assert.Equal(account.Id, item.MoneyAccountId);
        Assert.Equal("Основной банк", item.MoneyAccountName);
    }

    [Fact]
    public async Task MoneyAccountsController_CreatesSingleDefaultPerSource()
    {
        await using var db = await CreateContextAsync();
        var controller = new MoneyAccountsController(db);

        var firstAction = await controller.Create(new MoneyAccount
        {
            Name = "Основной счет",
            Source = MoneyOperationSource.Bank,
            Currency = "rub",
            IsActive = true,
        }, CancellationToken.None);
        var firstCreated = Assert.IsType<CreatedAtActionResult>(firstAction.Result);
        var first = Assert.IsType<MoneyAccount>(firstCreated.Value);
        Assert.True(first.IsDefault);
        Assert.Equal("RUB", first.Currency);

        var secondAction = await controller.Create(new MoneyAccount
        {
            Name = "Дополнительный счет",
            Source = MoneyOperationSource.Bank,
            Currency = "RUB",
            IsDefault = true,
            IsActive = true,
        }, CancellationToken.None);
        var secondCreated = Assert.IsType<CreatedAtActionResult>(secondAction.Result);
        var second = Assert.IsType<MoneyAccount>(secondCreated.Value);

        Assert.True(second.IsDefault);
        Assert.False(await db.MoneyAccounts.Where(item => item.Id == first.Id).Select(item => item.IsDefault).SingleAsync());
        Assert.True(await db.MoneyAccounts.Where(item => item.Id == second.Id).Select(item => item.IsDefault).SingleAsync());
    }

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

    private static MoneyOperationsController CreateController(PaymentContext db)
    {
        return new MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance);
    }

    private static async Task<MoneyOperationsPageResponse> GetMoneyPageAsync(
        MoneyOperationsController controller,
        MoneyOperationSource source)
    {
        var action = await controller.GetPaged(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            source: source,
            direction: null,
            accountingType: null,
            accountName: null,
            search: null,
            page: 1,
            pageSize: 25);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        return Assert.IsType<MoneyOperationsPageResponse>(ok.Value);
    }

    private static IFormFile CreateFormFile(string content, string fileName)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return CreateFormFile(bytes, fileName);
    }

    private static IFormFile CreateFormFile(byte[] bytes, string fileName)
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/csv",
        };
    }

    private sealed class FakeInvoiceDocumentPdfService : IInvoiceDocumentPdfService
    {
        private readonly byte[] _bytes;

        public FakeInvoiceDocumentPdfService(byte[] bytes)
        {
            _bytes = bytes;
        }

        public Task<byte[]> RenderAsync(string html, CancellationToken ct = default)
        {
            Assert.Contains("ПКО-2026-0001", html);
            Assert.Contains("Ахмедова Айтан Рафик Кызы", html);
            return Task.FromResult(_bytes);
        }
    }
}
