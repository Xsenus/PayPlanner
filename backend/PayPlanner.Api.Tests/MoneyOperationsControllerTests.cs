using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
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
            Basis = "Оплата по счёту №33",
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
            Basis = "Оплата и доплата",
            Account = "Касса",
            Comment = "Проверено",
        }, CancellationToken.None);

        var updateOk = Assert.IsType<OkObjectResult>(updateAction);
        var updated = Assert.IsType<CashOrderDto>(updateOk.Value);
        Assert.Equal(30_000m, updated.Amount);
        Assert.Equal(new DateTime(2026, 4, 30), updated.Date);
        Assert.Equal("Проверено", updated.Comment);

        var copyAction = await controller.Copy(source.Id, CancellationToken.None);
        var copyCreated = Assert.IsType<CreatedResult>(copyAction);
        var copied = Assert.IsType<CashOrderDto>(copyCreated.Value);
        Assert.NotEqual(source.Id, copied.Id);
        Assert.StartsWith("ПКО-", copied.Number);
        Assert.Equal(30_000m, copied.Amount);
        Assert.Null(copied.PaymentId);
        Assert.Null(copied.MoneyOperationId);

        var cancelAction = await controller.Cancel(source.Id, CancellationToken.None);
        var cancelOk = Assert.IsType<OkObjectResult>(cancelAction);
        var cancelled = Assert.IsType<CashOrderDto>(cancelOk.Value);
        Assert.Equal(CashOrderStatus.Cancelled, cancelled.Status);

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
            Basis = "Оплата по счёту №33",
            Account = "Основная касса",
            Comment = "Без приложения",
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
