using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Invoices;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using Xunit;

namespace PayPlanner.Api.Tests;

public class MoneyOperationsControllerTests
{
    [Fact]
    public async Task CreateTransfer_CreatesPairedOperations_AndCashOrderForCashSide()
    {
        await using var db = await CreateContextAsync();
        var bankAccount = new MoneyAccount
        {
            Name = "Основной банк",
            Source = MoneyOperationSource.Bank,
            Currency = "RUB",
            IsActive = true,
        };
        var cashAccount = new MoneyAccount
        {
            Name = "Основная касса",
            Source = MoneyOperationSource.Cash,
            Currency = "RUB",
            IsActive = true,
        };
        db.MoneyAccounts.AddRange(bankAccount, cashAccount);
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var action = await controller.CreateTransfer(new MoneyOperationTransferRequest
        {
            SourceMoneyAccountId = bankAccount.Id,
            TargetMoneyAccountId = cashAccount.Id,
            Amount = 12_000m,
            Currency = "RUB",
            Date = new DateTime(2026, 5, 22),
            Purpose = "Инкассация в кассу",
            PaymentOrderNumber = "TR-1",
        });

        var created = Assert.IsType<CreatedResult>(action.Result);
        var response = Assert.IsType<MoneyOperationTransferResponse>(created.Value);

        Assert.Equal(MoneyOperationDirection.Expense, response.SourceOperation.Direction);
        Assert.Equal(MoneyOperationSource.Bank, response.SourceOperation.Source);
        Assert.Equal(bankAccount.Id, response.SourceOperation.MoneyAccountId);
        Assert.Equal(cashAccount.Name, response.SourceOperation.PaidAccountName);
        Assert.Equal(MoneyOperationDirection.Income, response.TargetOperation.Direction);
        Assert.Equal(MoneyOperationSource.Cash, response.TargetOperation.Source);
        Assert.Equal(cashAccount.Id, response.TargetOperation.MoneyAccountId);
        Assert.Equal(bankAccount.Name, response.TargetOperation.PaidAccountName);
        Assert.Equal(MoneyOperationMatchStatus.Ignored, response.SourceOperation.MatchStatus);
        Assert.Equal(MoneyOperationMatchStatus.Ignored, response.TargetOperation.MatchStatus);

        var operations = await db.MoneyOperations
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, operations.Count);

        var cashOrder = Assert.Single(await db.CashOrders.ToListAsync());
        Assert.Equal(response.TargetOperation.Id, cashOrder.MoneyOperationId);
        Assert.Equal(CashOrderType.Incoming, cashOrder.Type);
        Assert.Equal(CashOrderStatus.Posted, cashOrder.Status);
        Assert.Equal(12_000m, cashOrder.Amount);
    }

    [Fact]
    public async Task CreateUpdateDeleteMoneyOperation_RoundTripsSuccessfully()
    {
        await using var db = await CreateContextAsync();
        var primaryResponsible = await CreateUserAsync(db, "money-primary@example.test", "Мария Финансист");
        var nextResponsible = await CreateUserAsync(db, "money-next@example.test", "Олег Бухгалтер");
        var controller = CreateController(db);

        var createAction = await controller.Create(new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Expense,
            AccountingType = MoneyOperationAccountingType.Fact,
            AccountName = "  Расчётный счёт  ",
            CounterpartyName = "  ООО Ромашка  ",
            ResponsibleUserId = primaryResponsible.Id,
            CategoryName = "  Аренда  ",
            Amount = 15_500m,
            VatRate = 20m,
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
        Assert.Equal(primaryResponsible.Id, createdOperation.ResponsibleUserId);
        Assert.Equal(20m, createdOperation.VatRate);
        Assert.Equal(2_583.33m, createdOperation.VatAmount);
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
            ResponsibleUserId = nextResponsible.Id,
            CategoryName = "  Возврат  ",
            Amount = 9_900m,
            VatRate = 10m,
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
        Assert.Equal(nextResponsible.Id, updatedOperation.ResponsibleUserId);
        Assert.Equal(10m, updatedOperation.VatRate);
        Assert.Equal(900m, updatedOperation.VatAmount);
        Assert.Equal("USD", updatedOperation.Currency);
        Assert.Equal(new DateTime(2026, 4, 23), updatedOperation.Date);
        Assert.Null(updatedOperation.RecognitionDate);
        Assert.NotNull(updatedOperation.UpdatedAt);

        var deleteAction = await controller.Delete(createdOperation.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(deleteAction);
        Assert.False(await db.MoneyOperations.AnyAsync());
    }

    [Fact]
    public async Task CreateMoneyOperation_ReturnsBadRequest_WhenResponsibleUserIsInactive()
    {
        await using var db = await CreateContextAsync();
        var inactiveResponsible = await CreateUserAsync(db, "inactive-money@example.test", "Неактивный", isActive: false);
        var controller = CreateController(db);

        var action = await controller.Create(new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Expense,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 1_000m,
            Date = new DateTime(2026, 4, 21),
            ResponsibleUserId = inactiveResponsible.Id,
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Equal("Ответственный за денежную операцию не найден или отключён.", badRequest.Value);
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

        var action = await controller.Delete(operation.Id, CancellationToken.None);

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
        Assert.Equal("Сумма денежной операции не может быть отрицательной.", badRequest.Value);
    }

    [Fact]
    public async Task GetPaged_ReturnsComputedJournalStatuses()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                Amount = 10_000m,
                Date = new DateTime(2026, 5, 1),
                MatchStatus = MoneyOperationMatchStatus.ClarificationRequired,
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                Amount = 5_000m,
                Date = new DateTime(2026, 5, 2),
                MatchStatus = MoneyOperationMatchStatus.None,
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Expense,
                AccountingType = MoneyOperationAccountingType.Fact,
                Amount = 2_000m,
                Date = new DateTime(2026, 5, 3),
                MatchStatus = MoneyOperationMatchStatus.None,
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Cash,
                Direction = MoneyOperationDirection.Expense,
                AccountingType = MoneyOperationAccountingType.Plan,
                Amount = 1_000m,
                Date = new DateTime(2026, 5, 4),
                MatchStatus = MoneyOperationMatchStatus.None,
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var action = await controller.GetPaged(
            from: new DateTime(2026, 5, 1),
            to: new DateTime(2026, 5, 31),
            source: null,
            direction: null,
            accountingType: null,
            moneyAccountId: null,
            accountName: null,
            categoryName: null,
            linkedInvoiceId: null,
            search: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var page = Assert.IsType<MoneyOperationsPageResponse>(ok.Value);
        var statuses = page.Items.ToDictionary(item => item.Amount, item => item.JournalStatus);
        Assert.Equal(MoneyOperationJournalStatus.Error, statuses[10_000m]);
        Assert.Equal(MoneyOperationJournalStatus.Processing, statuses[5_000m]);
        Assert.Equal(MoneyOperationJournalStatus.Posted, statuses[2_000m]);
        Assert.Equal(MoneyOperationJournalStatus.Processing, statuses[1_000m]);
    }

    [Fact]
    public async Task GetPaged_ReturnsResponsibleFromLinkedInvoiceOnEditableOperation()
    {
        await using var db = await CreateContextAsync();
        var role = new Role
        {
            Name = "Lawyer",
            Description = "Responsible user role",
            CreatedAt = DateTime.UtcNow,
        };
        var responsible = new User
        {
            Email = "anna@example.test",
            PasswordHash = "hash",
            FullName = "Анна Иванова",
            Role = role,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var invoice = new Invoice
        {
            Number = "INV-RESP",
            Date = new DateTime(2026, 5, 5),
            CreatedAt = DateTime.UtcNow,
        };
        var contract = new Contract
        {
            Number = "ДОГ-55/26",
            Date = new DateTime(2026, 5, 1),
            Status = ContractStatus.Concluded,
            CreatedAt = DateTime.UtcNow,
        };
        db.AddRange(role, responsible, invoice, contract);
        await db.SaveChangesAsync();

        var payment = new Payment
        {
            Account = invoice.Number,
            AccountDate = invoice.Date,
            InvoiceId = invoice.Id,
            ContractId = contract.Id,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Amount = 15_000m,
            PaidAmount = 15_000m,
            Date = new DateTime(2026, 5, 6),
            PlannedDate = new DateTime(2026, 5, 6),
            PaidDate = new DateTime(2026, 5, 6),
            LastPaymentDate = new DateTime(2026, 5, 6),
            CounterpartyName = "ООО Ромашка",
            Description = "Оплата по счету",
            CreatedAt = DateTime.UtcNow,
        };
        payment.Responsibles.Add(new PaymentResponsible
        {
            UserId = responsible.Id,
        });
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 9_000m,
            Date = new DateTime(2026, 5, 7),
            LinkedInvoiceId = invoice.Id,
            MatchStatus = MoneyOperationMatchStatus.Matched,
            CreatedAt = DateTime.UtcNow,
        };
        var act = new Act
        {
            Number = "АКТ-55/26",
            Date = new DateTime(2026, 5, 8),
            Amount = 15_000m,
            Invoice = payment,
            Status = ActStatus.Signed,
            WorkflowStatus = ActWorkflowStatus.Signed,
            CreatedAt = DateTime.UtcNow,
        };
        db.AddRange(payment, operation, act);
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.GetPaged(
            from: new DateTime(2026, 5, 1),
            to: new DateTime(2026, 5, 31),
            source: MoneyOperationSource.Bank,
            direction: null,
            accountingType: null,
            moneyAccountId: null,
            accountName: null,
            categoryName: null,
            linkedInvoiceId: null,
            search: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var page = Assert.IsType<MoneyOperationsPageResponse>(ok.Value);
        var manualRow = Assert.Single(page.Items, item => item.Id == operation.Id);
        Assert.DoesNotContain(page.Items, item => item.RelatedPaymentId == payment.Id);
        Assert.Equal(responsible.Id, manualRow.ResponsibleUserId);
        Assert.Equal("Анна Иванова", manualRow.ResponsibleName);
        Assert.Equal(contract.Id, manualRow.LinkedContractId);
        Assert.Equal("ДОГ-55/26", manualRow.LinkedContractNumber);
        Assert.Equal(act.Id, manualRow.LinkedActId);
        Assert.Equal("АКТ-55/26", manualRow.LinkedActNumber);
        Assert.Equal(new DateTime(2026, 5, 8), manualRow.LinkedActDate);
    }

    [Fact]
    public async Task GetPaged_ReturnsAttachmentSummaryForImportedStatementOperation()
    {
        await using var db = await CreateContextAsync();
        var batch = new BankStatementImportBatch
        {
            FileName = "statement-may.csv",
            FileHash = "hash-may",
            FileSize = 128,
            Status = BankStatementImportBatchStatus.Applied,
            RowsRead = 1,
            CreatedRows = 1,
            CreatedAt = DateTime.UtcNow,
        };
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 7_000m,
            Date = new DateTime(2026, 5, 8),
            BankStatementImportBatch = batch,
            MatchStatus = MoneyOperationMatchStatus.ClarificationRequired,
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.GetPaged(
            from: new DateTime(2026, 5, 1),
            to: new DateTime(2026, 5, 31),
            source: MoneyOperationSource.Bank,
            direction: null,
            accountingType: null,
            moneyAccountId: null,
            accountName: null,
            categoryName: null,
            linkedInvoiceId: null,
            search: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var page = Assert.IsType<MoneyOperationsPageResponse>(ok.Value);
        var row = Assert.Single(page.Items);
        Assert.Equal(1, row.AttachmentCount);
        Assert.Equal("statement-may.csv", row.AttachmentSummary);
    }

    [Fact]
    public async Task ExportCsv_ReturnsFilteredJournalRowsWithRussianLabels()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Основной банк",
                CounterpartyName = "ООО Ромашка",
                CategoryName = "Оплата услуг",
                Amount = 10_000m,
                Currency = "RUB",
                PaymentOrderNumber = "101",
                Date = new DateTime(2026, 5, 1),
                Purpose = "Оплата по счету; май",
                PaidInvoiceNumber = "INV-1",
                MatchStatus = MoneyOperationMatchStatus.None,
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Cash,
                Direction = MoneyOperationDirection.Expense,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Касса",
                CounterpartyName = "Поставщик",
                Amount = 500m,
                Date = new DateTime(2026, 5, 1),
                MatchStatus = MoneyOperationMatchStatus.None,
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.ExportCsv(
            from: new DateTime(2026, 5, 1),
            to: new DateTime(2026, 5, 31),
            source: MoneyOperationSource.Bank,
            direction: null,
            accountingType: null,
            moneyAccountId: null,
            accountName: null,
            categoryName: null,
            linkedInvoiceId: null,
            search: null,
            ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(action);
        Assert.Equal("text/csv; charset=utf-8", file.ContentType);
        Assert.StartsWith("money-operations-", file.FileDownloadName);
        Assert.EndsWith(".csv", file.FileDownloadName);

        var preamble = Encoding.UTF8.GetPreamble();
        Assert.True(file.FileContents.Take(preamble.Length).SequenceEqual(preamble));
        var csv = Encoding.UTF8.GetString(file.FileContents[preamble.Length..]);

        Assert.Contains("Дата;Источник;Статус;Движение;Учет;Счет;Контрагент;Ответственный;Сумма;Ставка НДС;Сумма НДС;Валюта", csv);
        Assert.Contains("Связанный счет;Договор;Акт;Статус сверки;Комментарий сверки;Вложения", csv);
        Assert.Contains("Банк;В обработке;Приход;Факт;Основной банк;ООО Ромашка;;10000.00;;;RUB", csv);
        Assert.Contains("\"Оплата по счету; май\"", csv);
        Assert.Contains("Не сверено", csv);
        Assert.DoesNotContain("Поставщик", csv);
    }

    [Fact]
    public async Task ExportCsv_ReturnsForbidWhenPaymentExportIsDenied()
    {
        await using var db = await CreateContextAsync();
        var role = new Role
        {
            Name = $"payments-view-only-{Guid.NewGuid():N}",
            Description = "Деньги без выгрузки",
            Permissions =
            [
                new RolePermission
                {
                    Section = RolePermissionCatalog.Payments,
                    CanView = true,
                    CanExport = false,
                    ViewScope = DataVisibilityScope.All,
                },
            ],
        };
        var user = new User
        {
            Email = "money-export-denied@example.com",
            FullName = "Пользователь без выгрузки",
            PasswordHash = "test",
            IsActive = true,
            IsApproved = true,
            Role = role,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = CreateController(db, user.Id, isAdminClaim: false);

        var action = await controller.ExportCsv(
            from: null,
            to: null,
            source: null,
            direction: null,
            accountingType: null,
            moneyAccountId: null,
            accountName: null,
            categoryName: null,
            linkedInvoiceId: null,
            search: null,
            ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task Print_ReturnsFilteredJournalHtmlWithEscapedRowsAndTotals()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Основной банк",
                CounterpartyName = "<ООО Ромашка>",
                Amount = 10_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 5, 1),
                Purpose = "Оплата по счету",
                MatchStatus = MoneyOperationMatchStatus.None,
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Cash,
                Direction = MoneyOperationDirection.Expense,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Касса",
                CounterpartyName = "Поставщик",
                Amount = 500m,
                Date = new DateTime(2026, 5, 1),
                MatchStatus = MoneyOperationMatchStatus.None,
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Print(
            from: new DateTime(2026, 5, 1),
            to: new DateTime(2026, 5, 31),
            source: MoneyOperationSource.Bank,
            direction: null,
            accountingType: null,
            moneyAccountId: null,
            accountName: null,
            categoryName: null,
            linkedInvoiceId: null,
            search: null,
            ct: CancellationToken.None);

        var content = Assert.IsType<ContentResult>(action);
        Assert.Equal("text/html; charset=utf-8", content.ContentType);
        Assert.Contains("<h1>Денежный журнал</h1>", content.Content);
        Assert.Contains("Источник: Банк", content.Content);
        Assert.Contains("<th>Ответственный</th>", content.Content);
        Assert.Contains("<th>Договор</th>", content.Content);
        Assert.Contains("<th>Акт</th>", content.Content);
        Assert.Contains("<th>Вложения</th>", content.Content);
        Assert.Contains("В обработке", content.Content);
        Assert.Contains("&lt;ООО Ромашка&gt;", content.Content);
        Assert.DoesNotContain("<ООО Ромашка>", content.Content);
        Assert.DoesNotContain("Поставщик", content.Content);
    }

    [Fact]
    public async Task ExportPdf_ReturnsFilteredJournalPdfFile()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Main bank",
                CounterpartyName = "Bank Client LLC",
                Amount = 10_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 5, 1),
                Purpose = "Оплата по счету",
                MatchStatus = MoneyOperationMatchStatus.None,
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Cash,
                Direction = MoneyOperationDirection.Expense,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Cash desk",
                CounterpartyName = "Cash Supplier",
                Amount = 500m,
                Date = new DateTime(2026, 5, 1),
                MatchStatus = MoneyOperationMatchStatus.None,
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        var controller = new MoneyOperationsController(
            db,
            NullLogger<MoneyOperationsController>.Instance,
            new FakeMoneyJournalPdfService(
                "%PDF-money-journal%"u8.ToArray(),
                html =>
                {
                    Assert.Contains("Денежный журнал", html);
                    Assert.Contains("Bank Client LLC", html);
                    Assert.DoesNotContain("Cash Supplier", html);
                }));

        var action = await controller.ExportPdf(
            from: new DateTime(2026, 5, 1),
            to: new DateTime(2026, 5, 31),
            source: MoneyOperationSource.Bank,
            direction: null,
            accountingType: null,
            moneyAccountId: null,
            accountName: null,
            categoryName: null,
            linkedInvoiceId: null,
            search: null,
            ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(action);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("%PDF-money-journal%"u8.ToArray(), file.FileContents);
        Assert.StartsWith("money-operations-", file.FileDownloadName);
        Assert.EndsWith(".pdf", file.FileDownloadName);
    }

    [Fact]
    public async Task ExportExcel_ReturnsFilteredJournalWorkbook()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Main bank",
                CounterpartyName = "Bank Client LLC",
                Amount = 10_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 5, 1),
                Purpose = "Invoice payment",
                MatchStatus = MoneyOperationMatchStatus.None,
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Cash,
                Direction = MoneyOperationDirection.Expense,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Cash desk",
                CounterpartyName = "Cash Supplier",
                Amount = 500m,
                Date = new DateTime(2026, 5, 1),
                MatchStatus = MoneyOperationMatchStatus.None,
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.ExportExcel(
            from: new DateTime(2026, 5, 1),
            to: new DateTime(2026, 5, 31),
            source: MoneyOperationSource.Bank,
            direction: null,
            accountingType: null,
            moneyAccountId: null,
            accountName: null,
            categoryName: null,
            linkedInvoiceId: null,
            search: null,
            ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(action);
        Assert.Equal("application/vnd.ms-excel; charset=utf-8", file.ContentType);
        Assert.StartsWith("money-operations-", file.FileDownloadName);
        Assert.EndsWith(".xls", file.FileDownloadName);
        var workbook = Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("Excel.Sheet", workbook);
        Assert.Contains("Money journal", workbook);
        Assert.Contains("Bank Client LLC", workbook);
        Assert.Contains("""ss:Type="Number">10000.00""", workbook);
        Assert.DoesNotContain("Cash Supplier", workbook);
    }

    [Fact]
    public async Task CreateMoneyOperation_LinksIncomeTypeWithoutDuplicatingCategoryName()
    {
        await using var db = await CreateContextAsync();
        var incomeType = new IncomeType
        {
            Name = "Юридические услуги",
            PaymentType = PaymentType.Income,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.IncomeTypes.Add(incomeType);
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Create(new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            IncomeTypeId = incomeType.Id,
            CategoryName = "Ручной текст не должен победить справочник",
            Amount = 25_000m,
            Date = new DateTime(2026, 4, 30),
        });

        var created = Assert.IsType<CreatedResult>(action);
        var operation = Assert.IsType<MoneyOperation>(created.Value);
        Assert.Equal(incomeType.Id, operation.IncomeTypeId);
        Assert.Null(operation.CategoryName);
    }

    [Fact]
    public async Task CreateMoneyOperation_ReturnsBadRequest_WhenIncomeTypeDirectionDoesNotMatch()
    {
        await using var db = await CreateContextAsync();
        var incomeType = new IncomeType
        {
            Name = "Доход от услуг",
            PaymentType = PaymentType.Income,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.IncomeTypes.Add(incomeType);
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.Create(new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Expense,
            AccountingType = MoneyOperationAccountingType.Fact,
            IncomeTypeId = incomeType.Id,
            Amount = 15_000m,
            Date = new DateTime(2026, 4, 30),
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Equal("Для расхода нужно выбрать статью расхода.", badRequest.Value);
        Assert.False(await db.MoneyOperations.AnyAsync());
    }

    [Fact]
    public async Task CreateMoneyOperation_ReturnsBadRequest_WhenLinkedInvoiceAlreadyPaid()
    {
        await using var db = await CreateContextAsync();
        var invoice = new Invoice
        {
            Number = "33",
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        };
        db.Payments.Add(new Payment
        {
            Invoice = invoice,
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

        var action = await controller.Create(new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 25_000m,
            Date = new DateTime(2026, 4, 29),
            LinkedInvoiceId = invoice.Id,
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Contains("полностью оплачен", Assert.IsType<string>(badRequest.Value));
        Assert.False(await db.MoneyOperations.AnyAsync());
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
        Assert.Equal(order.Date, afterItem.CashOrderDate);
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
    public async Task MoneyJournal_SearchFindsResponsibleContractAndStatementFile()
    {
        await using var db = await CreateContextAsync();
        var role = new Role
        {
            Name = "Manager",
            Description = "Responsible role",
            CreatedAt = DateTime.UtcNow,
        };
        var responsible = new User
        {
            Email = "anna.search@example.test",
            PasswordHash = "hash",
            FullName = "Анна Поискова",
            Role = role,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var invoice = new Invoice
        {
            Number = "INV-SEARCH",
            Date = new DateTime(2026, 4, 10),
            CreatedAt = DateTime.UtcNow,
        };
        var contract = new Contract
        {
            Number = "ДОГ-77/26",
            Title = "Абонентское обслуживание",
            Date = new DateTime(2026, 4, 1),
            Status = ContractStatus.Concluded,
            CreatedAt = DateTime.UtcNow,
        };
        var batch = new BankStatementImportBatch
        {
            FileName = "statement-june.csv",
            FileHash = "hash-search",
            FileSize = 512,
            Status = BankStatementImportBatchStatus.Applied,
            RowsRead = 1,
            CreatedRows = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.AddRange(role, responsible, invoice, contract, batch);
        await db.SaveChangesAsync();

        var payment = new Payment
        {
            Account = invoice.Number,
            AccountDate = invoice.Date,
            InvoiceId = invoice.Id,
            ContractId = contract.Id,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Amount = 12_000m,
            PaidAmount = 12_000m,
            Date = new DateTime(2026, 4, 15),
            PlannedDate = new DateTime(2026, 4, 15),
            PaidDate = new DateTime(2026, 4, 15),
            CounterpartyName = "ООО Ромашка",
            CreatedAt = DateTime.UtcNow,
        };
        payment.Responsibles.Add(new PaymentResponsible { UserId = responsible.Id });
        var linkedOperation = new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 12_000m,
            Date = new DateTime(2026, 4, 16),
            LinkedInvoiceId = invoice.Id,
            MatchStatus = MoneyOperationMatchStatus.Matched,
            CreatedAt = DateTime.UtcNow,
        };
        var importedOperation = new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Expense,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 800m,
            Date = new DateTime(2026, 4, 17),
            BankStatementImportBatchId = batch.Id,
            CreatedAt = DateTime.UtcNow,
        };
        db.AddRange(payment, linkedOperation, importedOperation);
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var byResponsible = await SearchMoneyJournalAsync(controller, "анна");
        Assert.Equal(1, byResponsible.Total);
        Assert.All(byResponsible.Items, item => Assert.Equal("Анна Поискова", item.ResponsibleName));
        Assert.Contains(byResponsible.Items, item => item.Id == linkedOperation.Id);

        var byContract = await SearchMoneyJournalAsync(controller, "ДОГ-77");
        Assert.Equal(1, byContract.Total);
        Assert.All(byContract.Items, item => Assert.Equal("ДОГ-77/26", item.LinkedContractNumber));
        Assert.Contains(byContract.Items, item => item.Id == linkedOperation.Id);

        var byStatementFile = await SearchMoneyJournalAsync(controller, "statement-june.csv");
        var importedItem = Assert.Single(byStatementFile.Items);
        Assert.Equal(importedOperation.Id, importedItem.Id);
        Assert.Equal("statement-june.csv", importedItem.AttachmentSummary);
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
    public async Task GetDailyBalances_ReturnsOpeningAndClosingByAccountDay()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Main bank",
                CounterpartyName = "Opening",
                Amount = 1_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, 30),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Main bank",
                CounterpartyName = "Client",
                Amount = 500m,
                Currency = "RUB",
                Date = new DateTime(2026, 5, 1, 10, 15, 0),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Expense,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Main bank",
                CounterpartyName = "Supplier",
                Amount = 200m,
                Currency = "RUB",
                Date = new DateTime(2026, 5, 1, 16, 30, 0),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Expense,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Main bank",
                CounterpartyName = "Bank fee",
                Amount = 100m,
                Currency = "RUB",
                Date = new DateTime(2026, 5, 2),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Cash,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Cashbox",
                Amount = 9_999m,
                Currency = "RUB",
                Date = new DateTime(2026, 5, 1),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Plan,
                AccountName = "Main bank",
                Amount = 9_999m,
                Currency = "RUB",
                Date = new DateTime(2026, 5, 1),
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var action = await controller.GetDailyBalances(
            from: new DateTime(2026, 5, 1),
            to: new DateTime(2026, 5, 2),
            source: MoneyOperationSource.Bank,
            accountName: "Main bank");

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var balances = Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationDailyBalanceDto>>(ok.Value);
        Assert.Collection(
            balances,
            first =>
            {
                Assert.Equal(new DateTime(2026, 5, 1), first.Date);
                Assert.Equal(MoneyOperationSource.Bank, first.Source);
                Assert.Equal("Main bank", first.AccountName);
                Assert.Equal(1_000m, first.OpeningBalance);
                Assert.Equal(500m, first.Income);
                Assert.Equal(200m, first.Expense);
                Assert.Equal(1_300m, first.ClosingBalance);
                Assert.Equal(1, first.IncomeCount);
                Assert.Equal(1, first.ExpenseCount);
                Assert.True(first.IsBalanced);
            },
            second =>
            {
                Assert.Equal(new DateTime(2026, 5, 2), second.Date);
                Assert.Equal(1_300m, second.OpeningBalance);
                Assert.Equal(0m, second.Income);
                Assert.Equal(100m, second.Expense);
                Assert.Equal(1_200m, second.ClosingBalance);
                Assert.Equal(0, second.IncomeCount);
                Assert.Equal(1, second.ExpenseCount);
                Assert.True(second.IsBalanced);
            });
    }

    [Fact]
    public async Task RunBalanceReconciliation_PersistsAndUpdatesDailyResults()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.AddRange(
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Main bank",
                Amount = 1_000m,
                Currency = "RUB",
                Date = new DateTime(2026, 4, 30),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Main bank",
                Amount = 500m,
                Currency = "RUB",
                Date = new DateTime(2026, 5, 1),
                CreatedAt = DateTime.UtcNow,
            },
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Expense,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Main bank",
                Amount = 200m,
                Currency = "RUB",
                Date = new DateTime(2026, 5, 1),
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId: 7);
        var firstAction = await controller.RunBalanceReconciliation(
            from: new DateTime(2026, 5, 1),
            to: new DateTime(2026, 5, 1),
            source: MoneyOperationSource.Bank,
            accountName: "Main bank");

        var firstOk = Assert.IsType<OkObjectResult>(firstAction.Result);
        var firstResult = Assert.IsType<MoneyBalanceReconciliationRunResultDto>(firstOk.Value);
        Assert.Equal(1, firstResult.Created);
        Assert.Equal(0, firstResult.Updated);
        Assert.Equal(0, firstResult.IssueCount);

        var saved = Assert.Single(await db.MoneyBalanceReconciliationResults.ToListAsync());
        Assert.Equal(new DateTime(2026, 5, 1), saved.Date);
        Assert.Equal(MoneyOperationSource.Bank, saved.Source);
        Assert.Equal("Main bank", saved.AccountName);
        Assert.Equal(1_000m, saved.OpeningBalance);
        Assert.Equal(500m, saved.Income);
        Assert.Equal(200m, saved.Expense);
        Assert.Equal(1_300m, saved.ClosingBalance);
        Assert.Equal(7, saved.CheckedByUserId);

        db.MoneyOperations.Add(new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            AccountName = "Main bank",
            Amount = 100m,
            Currency = "RUB",
            Date = new DateTime(2026, 5, 1),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var secondAction = await controller.RunBalanceReconciliation(
            from: new DateTime(2026, 5, 1),
            to: new DateTime(2026, 5, 1),
            source: MoneyOperationSource.Bank,
            accountName: "Main bank");

        var secondOk = Assert.IsType<OkObjectResult>(secondAction.Result);
        var secondResult = Assert.IsType<MoneyBalanceReconciliationRunResultDto>(secondOk.Value);
        Assert.Equal(0, secondResult.Created);
        Assert.Equal(1, secondResult.Updated);
        var updated = Assert.Single(await db.MoneyBalanceReconciliationResults.ToListAsync());
        Assert.Equal(600m, updated.Income);
        Assert.Equal(1_400m, updated.ClosingBalance);

        var listAction = await controller.GetBalanceReconciliationResults(
            from: new DateTime(2026, 5, 1),
            to: new DateTime(2026, 5, 1),
            source: MoneyOperationSource.Bank,
            accountName: "Main bank");
        var listOk = Assert.IsType<OkObjectResult>(listAction.Result);
        var rows = Assert.IsAssignableFrom<IReadOnlyList<MoneyBalanceReconciliationResultDto>>(listOk.Value);
        var row = Assert.Single(rows);
        Assert.Equal(updated.Id, row.Id);
        Assert.Equal(1_400m, row.ClosingBalance);
    }

    [Fact]
    public async Task MoneyBalanceReconciliationSchedulerService_RunsDailyReconciliationForDate()
    {
        await using var db = await CreateContextAsync();
        db.MoneyOperations.Add(new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            AccountName = "Main bank",
            Amount = 750m,
            Currency = "RUB",
            Date = new DateTime(2026, 5, 1),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        await using var provider = services.BuildServiceProvider();
        var scheduler = new MoneyBalanceReconciliationSchedulerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MoneyBalanceReconciliationSchedulerService>.Instance);

        var result = await scheduler.RunForDateAsync(new DateTime(2026, 5, 1), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result.Created);
        var saved = Assert.Single(await db.MoneyBalanceReconciliationResults.ToListAsync());
        Assert.Equal(new DateTime(2026, 5, 1), saved.Date);
        Assert.Equal(MoneyOperationSource.Bank, saved.Source);
        Assert.Equal("Main bank", saved.AccountName);
        Assert.Equal(750m, saved.Income);
        Assert.Equal(750m, saved.ClosingBalance);
        Assert.Null(saved.CheckedByUserId);
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
    public async Task MoneyJournal_FiltersByIncomeTypeName()
    {
        await using var db = await CreateContextAsync();
        var incomeType = new IncomeType
        {
            Name = "Юридические услуги",
            PaymentType = PaymentType.Income,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.IncomeTypes.Add(incomeType);
        await db.SaveChangesAsync();

        db.MoneyOperations.AddRange(
            new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                AccountName = "Основной банк",
                CounterpartyName = "Клиент",
                IncomeTypeId = incomeType.Id,
                CategoryName = null,
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
            from: null,
            to: null,
            source: MoneyOperationSource.Bank,
            direction: null,
            accountingType: null,
            categoryName: "юридические",
            search: null);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var page = Assert.IsType<MoneyOperationsPageResponse>(ok.Value);
        var item = Assert.Single(page.Items);
        Assert.Equal(incomeType.Id, item.IncomeTypeId);
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
        Assert.Equal(createdOrder.Date, item.CashOrderDate);

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
    public async Task CashOrder_Create_GeneratesNumberAndNormalizesDraft()
    {
        await using var db = await CreateContextAsync();
        var controller = new CashOrdersController(db);

        var action = await controller.Create(new CashOrder
        {
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Draft,
            Date = new DateTime(2026, 4, 29),
            Amount = 25_000m,
            Currency = "",
            Account = "  Основная касса  ",
            PayerName = "  Ахмедова Айтан Рафик Кызы  ",
            ReceiverName = "  Касса  ",
            Basis = "  Оплата по счёту №33  ",
            Appendix = "",
            Comment = "  ручной ордер  ",
        }, CancellationToken.None);

        var createdResult = Assert.IsType<CreatedResult>(action);
        var created = Assert.IsType<CashOrderDto>(createdResult.Value);
        Assert.StartsWith("ПКО-2026-", created.Number);
        Assert.Equal(CashOrderStatus.Draft, created.Status);
        Assert.Equal("RUB", created.Currency);
        Assert.Equal("Основная касса", created.Account);
        Assert.Equal("Ахмедова Айтан Рафик Кызы", created.PayerName);
        Assert.Equal("Касса", created.ReceiverName);
        Assert.Equal("Оплата по счёту №33", created.Basis);
        Assert.Null(created.Appendix);
        Assert.Equal("ручной ордер", created.Comment);

        var saved = await db.CashOrders.SingleAsync();
        Assert.Equal(created.Id, saved.Id);
        Assert.Equal(CashOrderStatus.Draft, saved.Status);
    }

    [Fact]
    public async Task CashOrder_Post_ChangesDraftToPosted()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.Add(new CashOrder
        {
            Number = "ПКО-2026-0001",
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Draft,
            Date = new DateTime(2026, 4, 29),
            Amount = 25_000m,
            Currency = "RUB",
            Account = "Основная касса",
            PayerName = "Ахмедова Айтан Рафик Кызы",
            Basis = "Оплата по счёту №33",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = new CashOrdersController(db);
        var order = await db.CashOrders.SingleAsync();

        var action = await controller.Post(order.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var posted = Assert.IsType<CashOrderDto>(ok.Value);
        Assert.Equal(CashOrderStatus.Posted, posted.Status);
        Assert.Equal(CashOrderStatus.Posted, (await db.CashOrders.SingleAsync()).Status);
        Assert.NotNull((await db.CashOrders.SingleAsync()).UpdatedAt);
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
        Assert.Contains("Унифицированная форма № КО-1", content.Content);
        Assert.Contains("0310001", content.Content);
        Assert.DoesNotContain("0310002", content.Content);
        Assert.Contains("Квитанция", content.Content);
        Assert.Contains("Линия отреза", content.Content);
        Assert.Contains("Форма по ОКУД", content.Content);
        Assert.Contains("25 001-25", content.Content);
        Assert.Contains("25 001", content.Content);
        Assert.Contains("25</div><span>коп.", content.Content);
        Assert.Contains("двадцать пять тысяч один", content.Content);
        Assert.Contains("апреля", content.Content);
        Assert.Contains("Принято от", content.Content);
        Assert.Contains("Ахмедова Айтан Рафик Кызы", content.Content);
        Assert.Contains("Основная касса", content.Content);
        Assert.Contains("Договор и счёт", content.Content);
        Assert.Contains("Главный бухгалтер", content.Content);
        Assert.Contains("Получил кассир", content.Content);
        Assert.Contains("(подпись)", content.Content);
        Assert.Contains("(расшифровка подписи)", content.Content);
        AssertPrintHtmlHasNoGeneratedTimestamp(content.Content);
    }

    [Fact]
    public async Task CashOrder_PrintOutgoing_ReturnsKo2WithoutReceipt()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.Add(new CashOrder
        {
            Number = "РКО-2026-0001",
            Type = CashOrderType.Outgoing,
            Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 4, 29),
            Amount = 1500m,
            Currency = "RUB",
            PayerName = "Основная касса",
            ReceiverName = "Поставщик",
            Basis = "Выдача наличных",
            Account = "Основная касса",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = new CashOrdersController(db);
        var order = await db.CashOrders.SingleAsync();

        var action = await controller.Print(order.Id, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(action);
        Assert.Equal("text/html; charset=utf-8", content.ContentType);
        Assert.Contains("Расходный кассовый ордер", content.Content);
        Assert.Contains("Унифицированная форма № КО-2", content.Content);
        Assert.Contains("0310002", content.Content);
        Assert.Contains("Выдать", content.Content);
        Assert.Contains("Поставщик", content.Content);
        Assert.Contains("Руководитель", content.Content);
        Assert.Contains("Главный бухгалтер", content.Content);
        Assert.DoesNotContain("Квитанция к", content.Content);
        AssertPrintHtmlHasNoGeneratedTimestamp(content.Content);
        AssertPrintHtmlUsesSharedDocumentStyle(content.Content);
    }

    [Fact]
    public async Task CashOrder_Delete_RemovesDraftOnly()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-0001",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Draft,
                Date = new DateTime(2026, 4, 29),
                Amount = 1000m,
                Currency = "RUB",
                CreatedAt = DateTime.UtcNow,
            },
            new CashOrder
            {
                Number = "ПКО-2026-0002",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 29),
                Amount = 2000m,
                Currency = "RUB",
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = new CashOrdersController(db);
        var draft = await db.CashOrders.SingleAsync(order => order.Status == CashOrderStatus.Draft);
        var posted = await db.CashOrders.SingleAsync(order => order.Status == CashOrderStatus.Posted);

        var deleted = await controller.Delete(draft.Id, CancellationToken.None);
        var postedDelete = await controller.Delete(posted.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleted);
        var badRequest = Assert.IsType<BadRequestObjectResult>(postedDelete);
        Assert.Contains("черновик", Assert.IsType<string>(badRequest.Value));
        Assert.False(await db.CashOrders.AnyAsync(order => order.Id == draft.Id));
        Assert.True(await db.CashOrders.AnyAsync(order => order.Id == posted.Id));
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
        var action = await controller.GetCashBook(new DateTime(2026, 4, 29), new DateTime(2026, 4, 30), ct: CancellationToken.None);

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
    public async Task CashOrder_CashBook_IncludesDaysWithoutOrders()
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
                Number = "РКО-2026-0001",
                Type = CashOrderType.Outgoing,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 3),
                Amount = 3_000m,
                Currency = "RUB",
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = new CashOrdersController(db);

        var action = await controller.GetCashBook(new DateTime(2026, 4, 1), new DateTime(2026, 4, 3), ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var book = Assert.IsType<CashBookResponseDto>(ok.Value);
        Assert.Collection(
            book.Days,
            day =>
            {
                Assert.Equal(new DateTime(2026, 4, 1), day.Date);
                Assert.Equal(0m, day.OpeningBalance);
                Assert.Equal(10_000m, day.Income);
                Assert.Equal(10_000m, day.ClosingBalance);
            },
            day =>
            {
                Assert.Equal(new DateTime(2026, 4, 2), day.Date);
                Assert.Equal(10_000m, day.OpeningBalance);
                Assert.Equal(0m, day.Income);
                Assert.Equal(0m, day.Expense);
                Assert.Equal(10_000m, day.ClosingBalance);
            },
            day =>
            {
                Assert.Equal(new DateTime(2026, 4, 3), day.Date);
                Assert.Equal(10_000m, day.OpeningBalance);
                Assert.Equal(3_000m, day.Expense);
                Assert.Equal(7_000m, day.ClosingBalance);
            });
    }

    [Fact]
    public async Task CashOrder_CashBookAndRegister_FilterByAccount()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "PKO-2026-0001",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 1),
                Amount = 1_000m,
                Currency = "RUB",
                Account = "Cashbox A",
                CreatedAt = DateTime.UtcNow,
            },
            new CashOrder
            {
                Number = "PKO-2026-0002",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 2),
                Amount = 5_000m,
                Currency = "RUB",
                Account = "Cashbox B",
                CreatedAt = DateTime.UtcNow,
            },
            new CashOrder
            {
                Number = "RKO-2026-0001",
                Type = CashOrderType.Outgoing,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 3),
                Amount = 300m,
                Currency = "RUB",
                Account = "Cashbox A",
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = new CashOrdersController(db);
        var bookAction = await controller.GetCashBook(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            accountName: " Cashbox A ",
            ct: CancellationToken.None);
        var bookOk = Assert.IsType<OkObjectResult>(bookAction.Result);
        var book = Assert.IsType<CashBookResponseDto>(bookOk.Value);
        Assert.Equal("Cashbox A", book.AccountName);
        Assert.Equal(1_000m, book.Income);
        Assert.Equal(300m, book.Expense);
        Assert.Equal(700m, book.ClosingBalance);

        var registerAction = await controller.GetRegister(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            type: null,
            status: null,
            search: null,
            page: 1,
            pageSize: 10,
            accountName: "Cashbox A",
            ct: CancellationToken.None);
        var registerOk = Assert.IsType<OkObjectResult>(registerAction.Result);
        var register = Assert.IsType<CashOrderRegisterResponseDto>(registerOk.Value);
        Assert.Equal(2, register.Total);
        Assert.Equal(1_000m, register.IncomingAmount);
        Assert.Equal(300m, register.OutgoingAmount);
        Assert.DoesNotContain(register.Items, item => item.Account == "Cashbox B");

        var printBookAction = await controller.PrintCashBook(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            accountName: "Cashbox A",
            ct: CancellationToken.None);
        Assert.Contains("Cashbox A", Assert.IsType<ContentResult>(printBookAction).Content);

        var printRegisterAction = await controller.PrintRegister(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            type: null,
            status: null,
            search: null,
            accountName: "Cashbox A",
            ct: CancellationToken.None);
        Assert.Contains("Cashbox A", Assert.IsType<ContentResult>(printRegisterAction).Content);
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
            },
            new CashOrder
            {
                Number = "ПКО-2026-0003",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Draft,
                Date = new DateTime(2026, 4, 28),
                Amount = 2_000m,
                Currency = "RUB",
                PayerName = "Ахмедова Айтан Рафик Кызы",
                Basis = "Черновик оплаты",
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
        Assert.Equal(3, register.Total);
        Assert.Equal(3, register.IncomingCount);
        Assert.Equal(0, register.OutgoingCount);
        Assert.Equal(1, register.DraftCount);
        Assert.Equal(1, register.PostedCount);
        Assert.Equal(1, register.CancelledCount);
        Assert.Equal(25_000m, register.IncomingAmount);
        Assert.Equal(0m, register.OutgoingAmount);
        Assert.Equal(["ПКО-2026-0002", "ПКО-2026-0001", "ПКО-2026-0003"], register.Items.Select(item => item.Number).ToArray());
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
        var action = await controller.PrintCashBook(new DateTime(2026, 4, 29), new DateTime(2026, 4, 29), ct: CancellationToken.None);

        var content = Assert.IsType<ContentResult>(action);
        Assert.Equal("text/html; charset=utf-8", content.ContentType);
        Assert.Contains("Кассовая книга", content.Content);
        Assert.Contains("25", content.Content);
        AssertPrintHtmlHasNoGeneratedTimestamp(content.Content);
        AssertPrintHtmlUsesSharedDocumentStyle(content.Content);
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
        Assert.Contains("Черновиков", content.Content);
        AssertPrintHtmlHasNoGeneratedTimestamp(content.Content);
        AssertPrintHtmlUsesSharedDocumentStyle(content.Content);
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
    public async Task BankStatementAutoImportRun_ImportsFilesFromConfiguredFolder()
    {
        var folderPath = Path.Combine(Path.GetTempPath(), $"payplanner-auto-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folderPath);
        try
        {
            await using var db = await CreateContextAsync();
            var role = new Role
            {
                Name = "Auto import role",
                Description = "Role for auto import test",
                CreatedAt = DateTime.UtcNow,
            };
            var user = new User
            {
                Email = "auto-import@example.test",
                PasswordHash = "hash",
                FullName = "Auto Import User",
                Role = role,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.UserSettings.Add(new UserSetting
            {
                User = user,
                Key = "money.bankStatement.autoImport",
                Value = $$"""
                {"enabled":true,"folderPath":"{{folderPath.Replace("\\", "\\\\")}}","fileMask":"*.csv","lastCheckedAt":null}
                """,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            await File.WriteAllTextAsync(
                Path.Combine(folderPath, "statement.csv"),
                "Дата;Сумма;Счёт;Контрагент;Назначение;Номер\n" +
                "29.04.2026;25000;Main bank;Client;Invoice 33;101\n",
                Encoding.UTF8);
            await File.WriteAllTextAsync(Path.Combine(folderPath, "ignore.pdf"), "ignored", Encoding.UTF8);

            var controller = CreateController(db, user.Id);

            var action = await controller.RunBankStatementAutoImport(CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(action.Result);
            var result = Assert.IsType<BankStatementAutoImportRunResultDto>(ok.Value);
            Assert.True(result.Enabled);
            Assert.Equal(1, result.FilesFound);
            Assert.Equal(1, result.FilesImported);
            Assert.Equal(1, result.RowsRead);
            Assert.Equal(1, result.Created);
            Assert.Empty(result.Errors);
            var file = Assert.Single(result.Files);
            Assert.Equal("statement.csv", file.FileName);
            Assert.NotNull(file.BatchId);

            var operation = await db.MoneyOperations.SingleAsync();
            Assert.Equal(MoneyOperationSource.Bank, operation.Source);
            Assert.Equal("Main bank", operation.AccountName);
            Assert.Equal(25_000m, operation.Amount);

            var savedSetting = await db.UserSettings.SingleAsync(item =>
                item.UserId == user.Id && item.Key == "money.bankStatement.autoImport");
            Assert.Contains("lastCheckedAt", savedSetting.Value);
        }
        finally
        {
            Directory.Delete(folderPath, recursive: true);
        }
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
    public async Task ReprocessBankStatementImport_RecreatesRolledBackRows()
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

        await controller.RollbackBankStatementImport(importResult.BatchId!.Value, CancellationToken.None);

        var reprocessAction = await controller.ReprocessBankStatementImport(importResult.BatchId.Value, CancellationToken.None);

        var reprocessOk = Assert.IsType<OkObjectResult>(reprocessAction.Result);
        var reprocessResult = Assert.IsType<BankStatementImportResultDto>(reprocessOk.Value);
        Assert.NotEqual(importResult.BatchId, reprocessResult.BatchId);
        Assert.Equal(1, reprocessResult.RowsRead);
        Assert.Equal(1, reprocessResult.Created);
        Assert.Equal(0, reprocessResult.Skipped);

        var operation = await db.MoneyOperations.SingleAsync();
        Assert.Equal("Ахмедова Айтан Рафик Кызы", operation.CounterpartyName);

        var batches = await db.BankStatementImportBatches
            .Include(batch => batch.Rows)
            .OrderBy(batch => batch.Id)
            .ToListAsync();
        Assert.Equal(2, batches.Count);
        Assert.Equal(BankStatementImportBatchStatus.RolledBack, batches[0].Status);
        Assert.Equal(BankStatementImportBatchStatus.Applied, batches[1].Status);
        Assert.Equal(BankStatementImportRowStatus.Created, Assert.Single(batches[1].Rows).Status);
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
        Assert.Equal(InvoiceWorkflowStatus.Draft, payment.InvoiceWorkflowStatus);
        Assert.Equal(25_000m, payment.PaidAmount);
        Assert.True(payment.IsPaid);
        Assert.Contains("Деньги", payment.SystemNotes);

        var row = await db.BankStatementImportRows.SingleAsync();
        Assert.Equal(BankStatementImportRowStatus.Created, row.Status);
        Assert.Contains("Автоматически сверено", row.Message);
    }

    [Fact]
    public async Task ImportBankStatement_AutoMatchesInvoiceByContractNumberInPurpose()
    {
        await using var db = await CreateContextAsync();
        var invoice = new Invoice
        {
            Number = "88",
            Date = new DateTime(2026, 4, 29),
            CreatedAt = DateTime.UtcNow,
        };
        var contract = new Contract
        {
            Number = "ДОГ-88/26",
            Date = new DateTime(2026, 4, 1),
            Status = ContractStatus.Concluded,
            CreatedAt = DateTime.UtcNow,
        };
        db.Payments.Add(new Payment
        {
            Invoice = invoice,
            Contract = contract,
            Account = "88",
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
                "29.04.2026;25000;Ахмедова Айтан Рафик Кызы;Оплата по договору №ДОГ-88/26;101\n",
                "statement.csv"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<BankStatementImportResultDto>(ok.Value);
        var operationDto = Assert.Single(result.Operations);
        Assert.Equal(MoneyOperationMatchStatus.Matched, operationDto.MatchStatus);

        var operation = await db.MoneyOperations.SingleAsync();
        Assert.Equal(invoice.Id, operation.LinkedInvoiceId);
        Assert.Equal(MoneyOperationMatchStatus.Matched, operation.MatchStatus);
        Assert.Equal("88", operation.PaidInvoiceNumber);
        Assert.Contains("номеру договора", operation.MatchComment);
    }

    [Fact]
    public async Task ImportBankStatement_AutoMatchesExpenseInvoiceByNumberInPurpose()
    {
        await using var db = await CreateContextAsync();
        var supplier = new Client
        {
            Name = "Поставщик услуг",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var invoice = new Invoice
        {
            Number = "44",
            Date = new DateTime(2026, 4, 29),
            CreatedAt = DateTime.UtcNow,
        };
        db.Payments.Add(new Payment
        {
            Invoice = invoice,
            ExpenseClient = supplier,
            Account = "44",
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Expense,
            Status = PaymentStatus.Pending,
            Amount = 15_000m,
            PaidAmount = 0m,
            Date = new DateTime(2026, 4, 29),
            CounterpartyName = "Поставщик услуг",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.ImportBankStatement(
            CreateFormFile(
                "Дата;Сумма;Контрагент;Назначение;Номер\n" +
                "29.04.2026;-15000;Поставщик услуг;Оплата поставщику по счету №44;202\n",
                "statement.csv"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<BankStatementImportResultDto>(ok.Value);
        var operationDto = Assert.Single(result.Operations);
        Assert.Equal(MoneyOperationDirection.Expense, operationDto.Direction);
        Assert.Equal(MoneyOperationMatchStatus.Matched, operationDto.MatchStatus);

        var operation = await db.MoneyOperations.SingleAsync();
        Assert.Equal(invoice.Id, operation.LinkedInvoiceId);
        Assert.Equal(MoneyOperationMatchStatus.Matched, operation.MatchStatus);
        Assert.Equal("44", operation.PaidInvoiceNumber);

        var payment = await db.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(15_000m, payment.PaidAmount);

        var row = await db.BankStatementImportRows.SingleAsync();
        Assert.Equal(BankStatementImportRowStatus.Created, row.Status);
        Assert.Contains("Автоматически сверено", row.Message);
    }

    [Fact]
    public async Task ImportBankStatement_AutoMatchesInstallmentInvoice_UpdatesInstallmentAndParent()
    {
        await using var db = await CreateContextAsync();

        var baseDate = DateTime.UtcNow.Date.AddDays(10);
        var operationDate = baseDate.AddDays(5);
        var invoice = new Invoice
        {
            Number = "55",
            Date = baseDate,
            CreatedAt = DateTime.UtcNow,
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var parentPayment = new Payment
        {
            InvoiceId = invoice.Id,
            Account = invoice.Number,
            AccountDate = invoice.Date,
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 12_000m,
            PaidAmount = 0m,
            Date = baseDate,
            PlannedDate = baseDate,
            CounterpartyName = "Клиент рассрочки",
            Description = "Счет №55",
            CreatedAt = DateTime.UtcNow,
        };
        var firstInstallment = new Payment
        {
            InvoiceId = invoice.Id,
            Account = invoice.Number,
            AccountDate = invoice.Date,
            DocumentKind = DocumentKind.Payment,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 6_000m,
            PaidAmount = 0m,
            Date = operationDate,
            PlannedDate = operationDate,
            CounterpartyName = "Клиент рассрочки",
            Description = "Рассрочка 1/2",
            SystemNotes = $"{InvoiceManagedPaymentRules.InstallmentPaymentMarker}\nПлатеж 1/2",
            CreatedAt = DateTime.UtcNow,
        };
        var secondInstallment = new Payment
        {
            InvoiceId = invoice.Id,
            Account = invoice.Number,
            AccountDate = invoice.Date,
            DocumentKind = DocumentKind.Payment,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 6_000m,
            PaidAmount = 0m,
            Date = operationDate.AddMonths(1),
            PlannedDate = operationDate.AddMonths(1),
            CounterpartyName = "Клиент рассрочки",
            Description = "Рассрочка 2/2",
            SystemNotes = $"{InvoiceManagedPaymentRules.InstallmentPaymentMarker}\nПлатеж 2/2",
            CreatedAt = DateTime.UtcNow,
        };
        db.Payments.AddRange(parentPayment, firstInstallment, secondInstallment);
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var action = await controller.ImportBankStatement(
            CreateFormFile(
                "Дата;Сумма;Контрагент;Назначение;Номер\n" +
                $"{operationDate:dd.MM.yyyy};6000;Клиент рассрочки;Оплата по счету №55;501\n",
                "statement.csv"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<BankStatementImportResultDto>(ok.Value);
        var operationDto = Assert.Single(result.Operations);
        Assert.Equal(MoneyOperationMatchStatus.Matched, operationDto.MatchStatus);

        db.ChangeTracker.Clear();
        var refreshedParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == parentPayment.Id);
        var refreshedFirst = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == firstInstallment.Id);
        var refreshedSecond = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == secondInstallment.Id);

        Assert.Equal(PaymentStatus.Completed, refreshedFirst.Status);
        Assert.Equal(6_000m, refreshedFirst.PaidAmount);
        Assert.True(refreshedFirst.IsPaid);
        Assert.Equal(PaymentStatus.Pending, refreshedSecond.Status);
        Assert.Equal(6_000m, refreshedParent.PaidAmount);
        Assert.Equal(PaymentStatus.Processing, refreshedParent.Status);
        Assert.Equal(InvoiceWorkflowStatus.Draft, refreshedParent.InvoiceWorkflowStatus);
        Assert.False(refreshedParent.IsPaid);
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
    public async Task GetInvoiceCandidates_ReturnsCandidateMatchedByContractNumber()
    {
        await using var db = await CreateContextAsync();
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 25_000m,
            Date = new DateTime(2026, 4, 29),
            CounterpartyName = "Ахмедова Айтан Рафик Кызы",
            Purpose = "Оплата по договору №ДОГ-88/26",
            MatchStatus = MoneyOperationMatchStatus.ClarificationRequired,
            CreatedAt = DateTime.UtcNow,
        };
        var invoice = new Invoice
        {
            Number = "88",
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        };
        var contract = new Contract
        {
            Number = "ДОГ-88/26",
            Date = new DateTime(2026, 4, 1),
            Status = ContractStatus.Concluded,
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyOperations.Add(operation);
        db.Payments.Add(new Payment
        {
            Invoice = invoice,
            Contract = contract,
            Account = "88",
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
        Assert.Equal("88", candidate.InvoiceNumber);
        Assert.Contains("номер договора", candidate.MatchReason);
    }

    [Fact]
    public async Task GetInvoiceCandidates_ReturnsExpenseCandidatesForBankOperation()
    {
        await using var db = await CreateContextAsync();
        var supplier = new Client
        {
            Name = "Поставщик услуг",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var operation = new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Expense,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 15_000m,
            Date = new DateTime(2026, 4, 29),
            CounterpartyName = "Поставщик услуг",
            Purpose = "Оплата поставщику по счету №44",
            MatchStatus = MoneyOperationMatchStatus.ClarificationRequired,
            CreatedAt = DateTime.UtcNow,
        };
        var invoice = new Invoice
        {
            Number = "44",
            Date = new DateTime(2026, 4, 20),
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyOperations.Add(operation);
        db.Payments.Add(new Payment
        {
            Invoice = invoice,
            ExpenseClient = supplier,
            Account = "44",
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Expense,
            Status = PaymentStatus.Pending,
            Amount = 15_000m,
            PaidAmount = 0m,
            Date = new DateTime(2026, 4, 20),
            CounterpartyName = "Поставщик услуг",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.GetInvoiceCandidates(operation.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var candidate = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationInvoiceCandidateDto>>(ok.Value));
        Assert.Equal(invoice.Id, candidate.InvoiceId);
        Assert.Equal("44", candidate.InvoiceNumber);
        Assert.Equal(15_000m, candidate.OutstandingAmount);
        Assert.Equal("Поставщик услуг", candidate.ClientName);
        Assert.Contains("номер счёта", candidate.MatchReason);
        Assert.Contains("похожий поставщик", candidate.MatchReason);
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
    public async Task LinkInvoice_PreservesClientSignatureRequirement_WhenPaymentClosesInvoice()
    {
        await using var db = await CreateContextAsync();
        var invoice = new Invoice
        {
            Number = "SIG-33",
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
            Account = "SIG-33",
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            InvoiceWorkflowStatus = InvoiceWorkflowStatus.RequiresClientSignature,
            Amount = 25_000m,
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

        Assert.IsType<OkObjectResult>(action);

        var payment = await db.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(25_000m, payment.PaidAmount);
        Assert.True(payment.IsPaid);
        Assert.Equal(InvoiceWorkflowStatus.RequiresClientSignature, payment.InvoiceWorkflowStatus);
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
        Assert.Contains("Зачтено с баланса контрагента", refreshedSecondPayment.SystemNotes);

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
    public async Task ImportBankStatement_ReadsClientBankExchangeTxtAndOneCExtension()
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

        var oneCExtensionAction = await controller.ImportBankStatement(
            CreateFormFile(
                content
                    .Replace("Номер=401", "Номер=403", StringComparison.Ordinal)
                    .Replace("Номер=402", "Номер=404", StringComparison.Ordinal),
                "client-bank.1c"),
            CancellationToken.None);
        var oneCExtensionOk = Assert.IsType<OkObjectResult>(oneCExtensionAction.Result);
        var oneCExtensionResult = Assert.IsType<BankStatementImportResultDto>(oneCExtensionOk.Value);
        Assert.Equal(2, oneCExtensionResult.RowsRead);
        Assert.Equal(2, oneCExtensionResult.Created);
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
    public async Task GetPaged_WithLinkedInvoiceId_ReturnsOnlyEditableInvoiceMoneyOperations()
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

        var representativeInvoicePayment = new Payment
        {
            InvoiceId = targetInvoice.Id,
            Account = "33",
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Amount = 100_000m,
            PaidAmount = 100_000m,
            Date = new DateTime(2026, 4, 29),
            PaidDate = new DateTime(2026, 4, 29),
            Description = "Служебная запись счёта №33",
            CreatedAt = DateTime.UtcNow,
        };
        var actualLinkedPayment = new Payment
        {
            InvoiceId = targetInvoice.Id,
            Account = "33",
            DocumentKind = DocumentKind.Payment,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Amount = 5_000m,
            PaidAmount = 5_000m,
            Date = new DateTime(2026, 4, 29),
            PaidDate = new DateTime(2026, 4, 29),
            Description = "Наличный платёж по счёту №33",
            CreatedAt = DateTime.UtcNow,
        };
        db.Payments.AddRange(representativeInvoicePayment, actualLinkedPayment);
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

        Assert.Equal(1, page.Total);
        Assert.All(page.Items, item => Assert.Equal(targetInvoice.Id, item.LinkedInvoiceId));
        Assert.DoesNotContain(page.Items, item => item.RelatedPaymentId == actualLinkedPayment.Id);
        Assert.DoesNotContain(page.Items, item => item.RelatedPaymentId == representativeInvoicePayment.Id);
        Assert.DoesNotContain(page.Items, item => item.Amount == 100_000m);
        Assert.DoesNotContain(page.Items, item => item.Amount == 5_000m);
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
    public async Task ImportBankStatement_AutoDetectsIncomeTypeFromPurpose()
    {
        await using var db = await CreateContextAsync();
        var incomeType = new IncomeType
        {
            Name = "Юридические услуги",
            PaymentType = PaymentType.Income,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var expenseType = new IncomeType
        {
            Name = "Банковская комиссия",
            PaymentType = PaymentType.Expense,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.IncomeTypes.AddRange(incomeType, expenseType);
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.ImportBankStatement(
            CreateFormFile(
                "Дата;Поступило;Списано;Контрагент;Назначение;Номер\n" +
                "29.04.2026;25000;;ООО Ромашка;Оплата за юридические услуги по договору;401\n" +
                "30.04.2026;;1200;Банк;Банковская комиссия за обслуживание счета;402\n",
                "statement.csv"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<BankStatementImportResultDto>(ok.Value);
        Assert.Equal(2, result.Created);

        var operations = await db.MoneyOperations.OrderBy(item => item.Date).ToListAsync();
        Assert.Collection(
            operations,
            income =>
            {
                Assert.Equal(incomeType.Id, income.IncomeTypeId);
                Assert.Null(income.CategoryName);
            },
            expense =>
            {
                Assert.Equal(expenseType.Id, expense.IncomeTypeId);
                Assert.Null(expense.CategoryName);
            });

        var page = await GetMoneyPageAsync(controller, MoneyOperationSource.Bank);
        Assert.Contains(page.Items, item => item.CategoryName == "Юридические услуги");
        Assert.Contains(page.Items, item => item.CategoryName == "Банковская комиссия");
    }

    [Fact]
    public async Task ImportBankStatement_InfersCategoriesFromRealBankPurposes()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db);

        var action = await controller.ImportBankStatement(
            CreateFormFile(
                "Дата;Поступило;Списано;Контрагент;Назначение;Номер\n" +
                "29.04.2026;12500;;ООО Клиент;Оплата по счету №15 за услуги по договору;501\n" +
                "29.04.2026;;590;АО Банк;Комиссия за РКО и обслуживание счета;502\n" +
                "30.04.2026;;8500;ИФНС №10;Уплата налога УСН за 1 квартал;503\n" +
                "30.04.2026;;40000;Иванов И.И.;Заработная плата за апрель по ведомости;504\n",
                "statement.csv"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<BankStatementImportResultDto>(ok.Value);
        Assert.Equal(4, result.Created);

        var operations = await db.MoneyOperations
            .OrderBy(item => item.PaymentOrderNumber)
            .ToDictionaryAsync(item => item.PaymentOrderNumber!);

        Assert.Equal("Оплата от клиентов", operations["501"].CategoryName);
        Assert.Equal("Банковская комиссия", operations["502"].CategoryName);
        Assert.Equal("Налоги и сборы", operations["503"].CategoryName);
        Assert.Equal("Зарплата", operations["504"].CategoryName);
        Assert.All(operations.Values, operation => Assert.Null(operation.IncomeTypeId));
    }

    [Fact]
    public async Task ImportBankStatement_UsesPurposeRulesToMatchIncomeType()
    {
        await using var db = await CreateContextAsync();
        var bankCommissionType = new IncomeType
        {
            Name = "Банковская комиссия",
            PaymentType = PaymentType.Expense,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.IncomeTypes.Add(bankCommissionType);
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var action = await controller.ImportBankStatement(
            CreateFormFile(
                "Дата;Поступило;Списано;Контрагент;Назначение;Номер\n" +
                "29.04.2026;;590;АО Банк;Комиссия за РКО и обслуживание счета;601\n",
                "statement.csv"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<BankStatementImportResultDto>(ok.Value);
        Assert.Equal(1, result.Created);

        var operation = await db.MoneyOperations.SingleAsync();
        Assert.Equal(bankCommissionType.Id, operation.IncomeTypeId);
        Assert.Null(operation.CategoryName);
    }

    [Fact]
    public async Task ImportBankStatement_UsesManualUserRulesForCategoryAndIncomeType()
    {
        await using var db = await CreateContextAsync();
        var role = new Role
        {
            Name = "Bank rules role",
            Description = "Role for bank rules test",
            CreatedAt = DateTime.UtcNow,
        };
        var user = new User
        {
            Email = "bank-rules@example.test",
            PasswordHash = "hash",
            FullName = "Настройщик банка",
            Role = role,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var hostingType = new IncomeType
        {
            Name = "Облачная инфраструктура",
            PaymentType = PaymentType.Expense,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(role);
        db.Users.Add(user);
        db.IncomeTypes.Add(hostingType);
        await db.SaveChangesAsync();
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = "money.bankStatement.categoryRules",
            Value = $$"""
            {"rules":[{"enabled":true,"categoryName":"Облачная инфраструктура","paymentType":"Expense","keywords":"хостинг; cloud; vps","incomeTypeId":{{hostingType.Id}}}]}
            """,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, user.Id);

        var action = await controller.ImportBankStatement(
            CreateFormFile(
                "Дата;Поступило;Списано;Контрагент;Назначение;Номер\n" +
                "29.04.2026;;3900;ООО Датацентр;Оплата cloud VPS и хостинг за май;701\n",
                "statement.csv"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<BankStatementImportResultDto>(ok.Value);
        Assert.Equal(1, result.Created);

        var operation = await db.MoneyOperations.SingleAsync();
        Assert.Equal(hostingType.Id, operation.IncomeTypeId);
        Assert.Null(operation.CategoryName);
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

    [Fact]
    public async Task MoneyAccountsController_UpdateInactiveDefault_PromotesFallbackDefault()
    {
        await using var db = await CreateContextAsync();
        var primary = new MoneyAccount
        {
            Name = "Основной банк",
            Source = MoneyOperationSource.Bank,
            Currency = "RUB",
            IsDefault = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var reserve = new MoneyAccount
        {
            Name = "Резервный банк",
            Source = MoneyOperationSource.Bank,
            Currency = "RUB",
            IsDefault = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyAccounts.AddRange(primary, reserve);
        await db.SaveChangesAsync();

        var controller = new MoneyAccountsController(db);
        var action = await controller.Update(primary.Id, new MoneyAccount
        {
            Name = primary.Name,
            Source = MoneyOperationSource.Bank,
            Currency = "RUB",
            IsDefault = true,
            IsActive = false,
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.False(await db.MoneyAccounts.Where(item => item.Id == primary.Id).Select(item => item.IsDefault).SingleAsync());
        Assert.False(await db.MoneyAccounts.Where(item => item.Id == primary.Id).Select(item => item.IsActive).SingleAsync());
        Assert.True(await db.MoneyAccounts.Where(item => item.Id == reserve.Id).Select(item => item.IsDefault).SingleAsync());
    }

    [Fact]
    public async Task MoneyAccountsController_DeleteUsedDefault_PromotesFallbackDefault()
    {
        await using var db = await CreateContextAsync();
        var primary = new MoneyAccount
        {
            Name = "Основная касса",
            Source = MoneyOperationSource.Cash,
            Currency = "RUB",
            IsDefault = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var reserve = new MoneyAccount
        {
            Name = "Резервная касса",
            Source = MoneyOperationSource.Cash,
            Currency = "RUB",
            IsDefault = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.MoneyAccounts.AddRange(primary, reserve);
        await db.SaveChangesAsync();
        db.MoneyOperations.Add(new MoneyOperation
        {
            Source = MoneyOperationSource.Cash,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            MoneyAccountId = primary.Id,
            AccountName = primary.Name,
            Amount = 1_000m,
            Currency = "RUB",
            Date = new DateTime(2026, 5, 1),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = new MoneyAccountsController(db);
        var action = await controller.Delete(primary.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(action);
        Assert.False(await db.MoneyAccounts.Where(item => item.Id == primary.Id).Select(item => item.IsDefault).SingleAsync());
        Assert.False(await db.MoneyAccounts.Where(item => item.Id == primary.Id).Select(item => item.IsActive).SingleAsync());
        Assert.True(await db.MoneyAccounts.Where(item => item.Id == reserve.Id).Select(item => item.IsDefault).SingleAsync());
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

    private static async Task<User> CreateUserAsync(
        PaymentContext db,
        string email,
        string fullName,
        bool isActive = true)
    {
        var role = await db.Roles.FirstOrDefaultAsync(item => item.Name == "money-user");
        if (role is null)
        {
            role = new Role
            {
                Name = "money-user",
                Description = "Money operation test role",
                CreatedAt = DateTime.UtcNow,
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
        }

        var user = new User
        {
            Email = email,
            PasswordHash = "hash",
            FullName = fullName,
            RoleId = role.Id,
            IsActive = isActive,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static MoneyOperationsController CreateController(PaymentContext db, int? userId = null, bool isAdminClaim = true)
    {
        var controller = new MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance);
        if (userId.HasValue)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId.Value.ToString()),
            };
            if (isAdminClaim)
            {
                claims.Add(new Claim(ClaimTypes.Role, "admin"));
            }

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
                },
            };
        }

        return controller;
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

    private static async Task<MoneyOperationsPageResponse> SearchMoneyJournalAsync(
        MoneyOperationsController controller,
        string search)
    {
        var action = await controller.GetPaged(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            source: MoneyOperationSource.Bank,
            direction: null,
            accountingType: null,
            moneyAccountId: null,
            accountName: null,
            categoryName: null,
            linkedInvoiceId: null,
            search: search,
            page: 1,
            pageSize: 25,
            ct: CancellationToken.None);

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

    private static void AssertPrintHtmlHasNoGeneratedTimestamp(string? html)
    {
        Assert.NotNull(html);
        Assert.DoesNotContain("Дата печати", html);
        Assert.DoesNotContain("Сформировано:", html);
        Assert.DoesNotContain("Generated", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DateTime", html);
        Assert.DoesNotContain("new Date", html, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertPrintHtmlUsesSharedDocumentStyle(string? html)
    {
        Assert.NotNull(html);
        Assert.Contains("* { box-sizing: border-box; }", html);
        Assert.Contains("font-family: Arial, \"DejaVu Sans\", sans-serif;", html);
        Assert.Contains("-webkit-print-color-adjust: exact !important;", html);
        Assert.Contains("print-color-adjust: exact !important;", html);
    }

    private sealed class FakeMoneyJournalPdfService : IInvoiceDocumentPdfService
    {
        private readonly byte[] _bytes;
        private readonly Action<string> _assertHtml;

        public FakeMoneyJournalPdfService(byte[] bytes, Action<string> assertHtml)
        {
            _bytes = bytes;
            _assertHtml = assertHtml;
        }

        public Task<byte[]> RenderAsync(string html, CancellationToken ct = default)
        {
            _assertHtml(html);
            return Task.FromResult(_bytes);
        }
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
