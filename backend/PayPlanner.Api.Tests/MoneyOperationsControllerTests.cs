using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
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
}
