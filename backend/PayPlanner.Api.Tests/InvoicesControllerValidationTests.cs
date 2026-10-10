using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class InvoicesControllerValidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_RejectsNullLineItemWithoutChangingFinancialData(bool update)
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Null line client");
        var date = new DateTime(2026, 10, 10);
        if (update) await CreateActiveInvoiceAsync(db, "NULL-LINE", date, clientId);
        var existingId = update ? (await db.Payments.SingleAsync()).Id : 0;
        var request = CreateRequest("NULL-LINE", clientId, date);
        request.Amount = 10m;
        request.LineItems = [null!, new InvoiceLineItemRequest { Name = "Service", Quantity = 1m, Price = 10m, VatRate = "20" }];
        var controller = CreateController(db, true, null);
        var action = update ? await controller.Update(existingId, request, default) : await controller.Create(request, default);
        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("Позиция счёта не может быть пустой", System.Text.Json.JsonSerializer.Serialize(badRequest.Value,
            new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        db.ChangeTracker.Clear();
        Assert.Equal(update ? 1 : 0, await db.Payments.CountAsync());
        if (update) Assert.Equal(10_000m, (await db.Payments.SingleAsync()).Amount);
        Assert.Empty(await db.InvoiceLineItems.ToListAsync());
    }

    [Theory]
    [InlineData("1.5", "10.075")]
    [InlineData("1.50001", "10")]
    public async Task Create_RejectsLinePrecisionThatWouldChangeAfterStorage(string quantity, string price)
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Precision client");
        var request = CreateRequest("PRECISION", clientId, new DateTime(2026, 10, 10));
        var q = decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture);
        var p = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture);
        request.Amount = Math.Round(q * p, 2, MidpointRounding.AwayFromZero);
        request.LineItems = [new InvoiceLineItemRequest { Name = "Service", Quantity = q, Price = p, VatRate = "20" }];
        var action = await CreateController(db, true, null).Create(request, default);
        Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Empty(await db.Payments.ToListAsync());
        Assert.Empty(await db.InvoiceLineItems.ToListAsync());
    }

    private const string LongInvoiceLineItemName =
        "Юридические услуги / ОКАЗАНИЕ ЮРИДИЧЕСКИХ УСЛУГ В РАМКАХ ДОГОВОРА № 1-20/03-2026 ОТ 20.03.2026 Г. по вопросу оказания услуг реализации транспортного средства, организации подачи объявлений на площадках в сети интернет, организация просмотра транспортного средства, подготовки договора для продажи транспортного средства LADA VESTA GFL110; идентификационный номер (vin): XTAGFL110NY628724; год выпуска: октябрь 2021; государственный регистрационный номер: Е506ОХ763; номер кузова: XTAGFL110NY628724";

    [Theory]
    [InlineData("1.5", "10.075")]
    [InlineData("1.50001", "10")]
    public async Task Update_RejectsLinePrecisionWithoutChangingExistingInvoice(string quantity, string price)
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Precision client");
        var date = new DateTime(2026, 10, 10);
        await CreateActiveInvoiceAsync(db, "PRECISION-UPDATE", date, clientId);
        var invoice = await db.Payments.SingleAsync();
        var request = CreateRequest("PRECISION-UPDATE", clientId, date);
        var q = decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture);
        var p = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture);
        request.Amount = Math.Round(q * p, 2, MidpointRounding.AwayFromZero);
        request.LineItems = [new InvoiceLineItemRequest { Name = "Service", Quantity = q, Price = p, VatRate = "20" }];
        Assert.IsType<BadRequestObjectResult>((await CreateController(db, true, null).Update(invoice.Id, request, default)).Result);
        db.ChangeTracker.Clear();
        Assert.Equal(10_000m, (await db.Payments.SingleAsync()).Amount);
        Assert.Empty(await db.InvoiceLineItems.ToListAsync());
    }

    [Fact]
    public async Task Create_IgnoresOrphanInvoiceRecord_WhenCheckingDuplicateNumber()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");
        await CreateOrphanInvoiceAsync(db, "25", new DateTime(2026, 4, 7));

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("25", clientId, new DateTime(2026, 4, 8));

        var action = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        Assert.NotNull(created.Value);
    }

    [Fact]
    public async Task Create_ReturnsShortDuplicateMessage_WhenActiveInvoiceExists()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");
        await CreateActiveInvoiceAsync(db, "25", new DateTime(2026, 4, 7), clientId);

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("25", clientId, new DateTime(2026, 4, 8));

        var action = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        var message = Assert.IsType<string>(badRequest.Value);
        Assert.Equal("Номер счета '25' уже занят в 2026 году.", message);
    }

    [Fact]
    public async Task Create_AllowsDuplicateNumberOnImport_ByIncrementingImportSuffix()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Import Duplicate Client");
        await CreateActiveInvoiceAsync(db, "IMP-42", new DateTime(2026, 4, 7), clientId);
        await CreateActiveInvoiceAsync(db, "IMP-42-IMP1", new DateTime(2026, 4, 7), clientId);

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("IMP-42", clientId, new DateTime(2026, 4, 8));
        request.AllowDuplicateNumberOnImport = true;

        var action = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<InvoiceDto>(created.Value);
        Assert.Equal("IMP-42-IMP2", dto.Number);

        var storedPayment = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == dto.Id);
        Assert.Equal("IMP-42-IMP2", storedPayment.Account);
        Assert.Equal(new DateTime(2026, 4, 8), storedPayment.AccountDate);
    }

    [Fact]
    public async Task GetNextNumber_IgnoresOrphanInvoiceRecord()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");
        await CreateActiveInvoiceAsync(db, "24", new DateTime(2026, 4, 6), clientId);
        await CreateOrphanInvoiceAsync(db, "25", new DateTime(2026, 4, 7));

        var controller = CreateController(db, isAdmin: true, userId: 1);

        var action = await controller.GetNextNumber(new DateTime(2026, 4, 9), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var nextNumber = GetAnonymousProperty<string>(ok.Value, "nextNumber");
        Assert.Equal("25", nextNumber);
    }

    [Fact]
    public async Task CleanupOrphanInvoices_DeletesOnlyInvoicesWithoutLivePayments()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");
        await CreateActiveInvoiceAsync(db, "24", new DateTime(2026, 4, 6), clientId);
        await CreateOrphanInvoiceAsync(db, "25", new DateTime(2026, 4, 7));

        var controller = CreateController(db, isAdmin: true, userId: null);

        var action = await controller.CleanupOrphanInvoices(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var deleted = GetAnonymousProperty<int>(ok.Value, "deleted");
        Assert.Equal(1, deleted);

        var remainingNumbers = await db.Invoices.AsNoTracking().OrderBy(x => x.Number).Select(x => x.Number).ToListAsync();
        Assert.Equal(new[] { "24" }, remainingNumbers);
    }

    [Fact]
    public async Task Create_AllowsLongInvoiceLineItemName()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("31", clientId, new DateTime(2026, 4, 10));
        request.LineItems =
        [
            new InvoiceLineItemRequest
            {
                Name = LongInvoiceLineItemName,
                Quantity = 1,
                Unit = "шт",
                Price = 22_500m,
            },
        ];
        request.Amount = 22_500m;

        var action = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        Assert.NotNull(created.Value);

        var storedName = await db.InvoiceLineItems.AsNoTracking().Select(x => x.Name).SingleAsync();
        Assert.Equal(LongInvoiceLineItemName, storedName);
    }

    [Fact]
    public async Task Create_PersistsInvoiceLineItemServiceId()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");
        var serviceId = await CreateServiceAsync(db, "Составление иска");

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("31A", clientId, new DateTime(2026, 4, 10));
        request.LineItems =
        [
            new InvoiceLineItemRequest
            {
                ServiceId = serviceId,
                Name = "Составление иска по делу №42",
                Quantity = 1,
                Unit = "усл. ед.",
                Price = 22_500m,
            },
        ];
        request.Amount = 22_500m;

        var action = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<InvoiceDto>(created.Value);
        var lineItem = Assert.Single(dto.LineItems);
        Assert.Equal(serviceId, lineItem.ServiceId);

        var storedServiceId = await db.InvoiceLineItems.AsNoTracking().Select(x => x.ServiceId).SingleAsync();
        Assert.Equal(serviceId, storedServiceId);
    }

    [Fact]
    public async Task Create_RejectsUnknownInvoiceLineItemServiceId()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("31B", clientId, new DateTime(2026, 4, 10));
        request.LineItems =
        [
            new InvoiceLineItemRequest
            {
                ServiceId = 9999,
                Name = "Нестандартная услуга",
                Quantity = 1,
                Unit = "шт",
                Price = 1_000m,
            },
        ];

        var action = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal("Unknown invoice line item serviceId 9999", badRequest.Value);
    }

    [Fact]
    public async Task Create_ExpenseInvoice_AcceptsClientCaseLinkedToSelectedExpenseClient()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Клиент");
        var expenseClientId = await CreateClientAsync(db, "Поставщик");
        var clientCaseId = await CreateClientCaseAsync(db, clientId, "Дело клиента");

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("31C", expenseClientId, new DateTime(2026, 4, 10));
        request.Type = PaymentType.Expense;
        request.ExpenseClientId = clientId;
        request.ClientCaseIds = [clientCaseId];

        var action = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<InvoiceDto>(created.Value);
        Assert.Contains(clientCaseId, dto.ClientCaseIds);
    }

    [Fact]
    public async Task Create_RejectsUnknownClientCaseIds()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Unknown Case Client");

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-CASE-404", clientId, new DateTime(2026, 4, 10));
        request.ClientCaseIds = [999];

        var action = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        var message = Assert.IsType<string>(badRequest.Value);
        Assert.Equal("Unknown clientCaseIds: 999", message);
    }

    [Fact]
    public async Task Create_RejectsClientCasesFromAnotherClient()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Primary Case Client");
        var foreignClientId = await CreateClientAsync(db, "Foreign Case Client");
        var foreignCaseId = await CreateClientCaseAsync(db, foreignClientId, "Foreign Case");

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-CASE-FOREIGN", clientId, new DateTime(2026, 4, 10));
        request.ClientCaseIds = [foreignCaseId];

        var action = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        var message = Assert.IsType<string>(badRequest.Value);
        Assert.Equal($"Дела не связаны с выбранным клиентом или юрлицом: {foreignCaseId}", message);
    }

    [Fact]
    public async Task Create_RejectsUnknownActIds()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Unknown Act Client");

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-ACT-404", clientId, new DateTime(2026, 4, 10));
        request.ActIds = [999];

        var action = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        var message = Assert.IsType<string>(badRequest.Value);
        Assert.Equal("Unknown actIds: 999", message);
    }

    [Fact]
    public async Task Create_RejectsActsFromAnotherClient()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Primary Act Client");
        var foreignClientId = await CreateClientAsync(db, "Foreign Act Client");
        var foreignActId = await CreateActAsync(db, foreignClientId, "ACT-FOREIGN");

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-ACT-FOREIGN", clientId, new DateTime(2026, 4, 10));
        request.ActIds = [foreignActId];

        var action = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        var message = Assert.IsType<string>(badRequest.Value);
        Assert.Equal($"Акты не связаны с выбранным клиентом или юрлицом: {foreignActId}", message);
    }

    [Fact]
    public async Task Create_RejectsActsLinkedToAnotherInvoice()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Linked Act Client");
        var linkedInvoiceId = await CreateInvoiceAsync(db, clientId, "INV-ACT-LINKED");
        var actId = await CreateActAsync(db, clientId, "ACT-LINKED");

        var trackedAct = await db.Acts.SingleAsync(act => act.Id == actId);
        trackedAct.InvoiceId = linkedInvoiceId;
        trackedAct.InvoiceNumber = "INV-ACT-LINKED";
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-ACT-NEW", clientId, new DateTime(2026, 4, 10));
        request.ActIds = [actId];

        var action = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        var message = Assert.IsType<string>(badRequest.Value);
        Assert.Equal($"Акты уже привязаны к другому счету: {actId}", message);
    }

    [Fact]
    public async Task Create_AlignsDueDateWithLastInstallmentPaymentDate()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("32", clientId, new DateTime(2026, 4, 10));
        request.DueDate = new DateTime(2026, 4, 12);
        request.InstallmentPlan = CreateInstallmentPlanRequest(
            new DateTime(2026, 4, 10),
            new DateTime(2026, 5, 10),
            new DateTime(2026, 6, 10));

        var action = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<InvoiceDto>(created.Value);
        Assert.Equal(new DateTime(2026, 6, 10), dto.DueDate);

        var storedInvoicePayment = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == dto.Id);
        Assert.Equal(new DateTime(2026, 6, 10), storedInvoicePayment.Date);
    }

    [Fact]
    public async Task Create_ProcessingStatusWithoutPaidAmount_NormalizesToPending()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Processing Client");
        var invoiceDate = DateTime.UtcNow.Date.AddDays(5);

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-PROC-1", clientId, invoiceDate);
        request.Status = PaymentStatus.Processing;
        request.PaidDate = invoiceDate.AddDays(1);

        var action = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<InvoiceDto>(created.Value);

        var storedPayment = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == dto.Id);
        Assert.Equal(PaymentStatus.Pending, storedPayment.Status);
        Assert.Equal(0m, storedPayment.PaidAmount);
        Assert.False(storedPayment.IsPaid);
        Assert.Null(storedPayment.PaidDate);
        Assert.Null(storedPayment.LastPaymentDate);
    }

    [Fact]
    public async Task Update_AlignsDueDateWithLastInstallmentPaymentDate()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");
        var controller = CreateController(db, isAdmin: true, userId: null);

        var createRequest = CreateRequest("33", clientId, new DateTime(2026, 4, 10));
        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        var updateRequest = CreateRequest("33", clientId, new DateTime(2026, 4, 10));
        updateRequest.DueDate = new DateTime(2026, 4, 12);
        updateRequest.InstallmentPlan = CreateInstallmentPlanRequest(
            new DateTime(2026, 4, 10),
            new DateTime(2026, 5, 10),
            new DateTime(2026, 6, 10));

        var updateAction = await controller.Update(createdDto.Id, updateRequest, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(updateAction.Result);
        var dto = Assert.IsType<InvoiceDto>(ok.Value);
        Assert.Equal(new DateTime(2026, 6, 10), dto.DueDate);

        var storedInvoicePayment = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);
        Assert.Equal(new DateTime(2026, 6, 10), storedInvoicePayment.Date);
    }

    [Fact]
    public async Task Update_PreservesExistingInstallmentSchedule_WhenInstallmentPlanIsOmitted()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");
        var controller = CreateController(db, isAdmin: true, userId: null);

        var createRequest = CreateRequest("34", clientId, new DateTime(2026, 4, 10));
        createRequest.InstallmentPlan = CreateInstallmentPlanRequest(
            new DateTime(2026, 4, 10),
            new DateTime(2026, 5, 10),
            new DateTime(2026, 6, 10));

        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        var invoicePayment = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);

        var updateRequest = CreateRequest("34", clientId, new DateTime(2026, 4, 10));
        updateRequest.DueDate = new DateTime(2026, 6, 10);

        var updateAction = await controller.Update(createdDto.Id, updateRequest, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(updateAction.Result);
        Assert.IsType<InvoiceDto>(ok.Value);

        var storedInstallments = await db.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.InvoiceId == invoicePayment.InvoiceId
                && payment.Id != invoicePayment.Id
                && payment.SystemNotes != null
                && payment.SystemNotes.Contains("[pp:installment-payment]"))
            .OrderBy(payment => payment.Date)
            .ToListAsync();

        Assert.Equal(2, storedInstallments.Count);
        Assert.Equal(new DateTime(2026, 5, 10), storedInstallments[0].Date);
        Assert.Equal(new DateTime(2026, 6, 10), storedInstallments[1].Date);
    }

    [Fact]
    public async Task Update_RemovesExistingInstallmentSchedule_WhenReplaceInstallmentPlanRequestedWithNullPlan()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");
        var controller = CreateController(db, isAdmin: true, userId: null);

        var createRequest = CreateRequest("35", clientId, new DateTime(2026, 4, 10));
        createRequest.InstallmentPlan = CreateInstallmentPlanRequest(
            new DateTime(2026, 4, 10),
            new DateTime(2026, 5, 10),
            new DateTime(2026, 6, 10));

        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        var invoicePayment = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);

        var updateRequest = CreateRequest("35", clientId, new DateTime(2026, 4, 10));
        updateRequest.DueDate = new DateTime(2026, 6, 10);
        updateRequest.ReplaceInstallmentPlan = true;
        updateRequest.InstallmentPlan = null;

        var updateAction = await controller.Update(createdDto.Id, updateRequest, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(updateAction.Result);
        Assert.IsType<InvoiceDto>(ok.Value);

        var storedInstallmentCount = await db.Payments
            .AsNoTracking()
            .CountAsync(payment =>
                payment.InvoiceId == invoicePayment.InvoiceId
                && payment.Id != invoicePayment.Id
                && payment.SystemNotes != null
                && payment.SystemNotes.Contains("[pp:installment-payment]"));

        Assert.Equal(0, storedInstallmentCount);
    }

    [Fact]
    public async Task Create_ReturnsValidationProblem_WhenModelStateIsInvalid()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Validation Client");
        var controller = CreateController(db, isAdmin: true, userId: null);
        controller.ModelState.AddModelError("Number", "Required");

        var action = await controller.Create(CreateRequest("INV-VAL-1", clientId, new DateTime(2026, 4, 10)), CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        var details = Assert.IsType<ValidationProblemDetails>(result.Value);
        Assert.Contains("Number", details.Errors.Keys);
    }

    [Fact]
    public async Task Create_RejectsUnknownClient()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);

        var action = await controller.Create(CreateRequest("INV-VAL-2", 999, new DateTime(2026, 4, 10)), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal("Unknown clientId 999", badRequest.Value);
    }

    [Fact]
    public async Task Create_RejectsUnknownExpenseClient()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Client");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-VAL-3", clientId, new DateTime(2026, 4, 10));
        request.Type = PaymentType.Expense;
        request.ExpenseClientId = 999;

        var action = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal("Unknown expenseClientId 999", badRequest.Value);
    }

    [Fact]
    public async Task Create_RejectsUnknownContract()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Contract Client");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-VAL-4", clientId, new DateTime(2026, 4, 10));
        request.ContractId = 999;

        var action = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal("Unknown contractId 999", badRequest.Value);
    }

    [Fact]
    public async Task Create_RejectsContractFromAnotherClient()
    {
        await using var db = await CreateContextAsync();
        var requestClientId = await CreateClientAsync(db, "Request Client");
        var contractOwnerId = await CreateClientAsync(db, "Contract Owner");
        var contractId = await CreateContractAsync(db, contractOwnerId, "CTR-VAL-1");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-VAL-5", requestClientId, new DateTime(2026, 4, 10));
        request.ContractId = contractId;

        var action = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal("Client is not linked to selected contract.", badRequest.Value);
    }

    [Fact]
    public async Task Create_RejectsBlankInvoiceNumber()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Blank Number Client");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-VAL-6", clientId, new DateTime(2026, 4, 10));
        request.Number = "   ";

        var action = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal("Invoice number is required.", badRequest.Value);
    }

    [Fact]
    public async Task Create_RejectsUnknownInvoiceServiceId()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Service Client");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-VAL-7", clientId, new DateTime(2026, 4, 10));
        request.ServiceId = 999;

        var action = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal("Unknown serviceId 999", badRequest.Value);
    }

    [Fact]
    public async Task Create_RejectsTooManyClientCases()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 1, maxActs: 5);
        var clientId = await CreateClientAsync(db, "Case Limit Client");
        var caseId1 = await CreateClientCaseAsync(db, clientId, "Case 1");
        var caseId2 = await CreateClientCaseAsync(db, clientId, "Case 2");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-VAL-8", clientId, new DateTime(2026, 4, 10));
        request.ClientCaseIds = [caseId1, caseId2];

        var action = await controller.Create(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    [Fact]
    public async Task Create_RejectsTooManyActs()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 5, maxActs: 1);
        var clientId = await CreateClientAsync(db, "Act Limit Client");
        var actId1 = await CreateActAsync(db, clientId, "ACT-VAL-1");
        var actId2 = await CreateActAsync(db, clientId, "ACT-VAL-2");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-VAL-9", clientId, new DateTime(2026, 4, 10));
        request.ActIds = [actId1, actId2];

        var action = await controller.Create(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    [Fact]
    public async Task Create_RejectsUnknownResponsibleUser()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Responsible Client");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-VAL-10", clientId, new DateTime(2026, 4, 10));
        request.ResponsibleIds = [999];

        var action = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal("One or more responsible users were not found.", badRequest.Value);
    }

    [Fact]
    public async Task Create_RejectsInstallmentPlanWithoutValidItems()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Installment Client");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-VAL-11", clientId, new DateTime(2026, 4, 10));
        request.InstallmentPlan = CreateInstallmentPlanRequest(new DateTime(2026, 4, 10), new DateTime(2026, 5, 10));
        request.InstallmentPlan.Items = [null!];

        var action = await controller.Create(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    [Fact]
    public async Task Update_ReturnsValidationProblem_WhenModelStateIsInvalid()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Update Validation Client");
        var invoiceId = await CreateInvoiceAsync(db, clientId, "INV-UPD-VAL-1");
        var controller = CreateController(db, isAdmin: true, userId: null);
        controller.ModelState.AddModelError("Number", "Required");

        var action = await controller.Update(invoiceId, CreateRequest("INV-UPD-VAL-1", clientId, new DateTime(2026, 4, 10)), CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        var details = Assert.IsType<ValidationProblemDetails>(result.Value);
        Assert.Contains("Number", details.Errors.Keys);
    }

    [Fact]
    public async Task Update_ReturnsNotFound_WhenInvoiceDoesNotExist()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Missing Invoice Client");
        var controller = CreateController(db, isAdmin: true, userId: null);

        var action = await controller.Update(999, CreateRequest("INV-UPD-VAL-2", clientId, new DateTime(2026, 4, 10)), CancellationToken.None);

        Assert.IsType<NotFoundResult>(action.Result);
    }

    [Fact]
    public async Task Update_RejectsUnknownClient()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Known Client");
        var invoiceId = await CreateInvoiceAsync(db, clientId, "INV-UPD-VAL-3");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-UPD-VAL-3", 999, new DateTime(2026, 4, 10));

        var action = await controller.Update(invoiceId, request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal("Unknown clientId 999", badRequest.Value);
    }

    [Fact]
    public async Task Update_RejectsUnknownExpenseClient()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Expense Client");
        var invoiceId = await CreateInvoiceAsync(db, clientId, "INV-UPD-VAL-4");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-UPD-VAL-4", clientId, new DateTime(2026, 4, 10));
        request.Type = PaymentType.Expense;
        request.ExpenseClientId = 999;

        var action = await controller.Update(invoiceId, request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal("Unknown expenseClientId 999", badRequest.Value);
    }

    [Fact]
    public async Task Update_RejectsUnknownContract()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Unknown Contract Client");
        var invoiceId = await CreateInvoiceAsync(db, clientId, "INV-UPD-VAL-5");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-UPD-VAL-5", clientId, new DateTime(2026, 4, 10));
        request.ContractId = 999;

        var action = await controller.Update(invoiceId, request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal("Unknown contractId 999", badRequest.Value);
    }

    [Fact]
    public async Task Update_RejectsBlankInvoiceNumber()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Update Blank Number Client");
        var invoiceId = await CreateInvoiceAsync(db, clientId, "INV-UPD-VAL-6");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-UPD-VAL-6", clientId, new DateTime(2026, 4, 10));
        request.Number = "   ";

        var action = await controller.Update(invoiceId, request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal("Invoice number is required.", badRequest.Value);
    }

    [Fact]
    public async Task Update_RejectsUnknownInvoiceServiceId()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Update Service Client");
        var invoiceId = await CreateInvoiceAsync(db, clientId, "INV-UPD-VAL-7");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-UPD-VAL-7", clientId, new DateTime(2026, 4, 10));
        request.ServiceId = 999;

        var action = await controller.Update(invoiceId, request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal("Unknown serviceId 999", badRequest.Value);
    }

    [Fact]
    public async Task Update_RejectsUnknownLineItemServiceId()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Update Line Item Client");
        var invoiceId = await CreateInvoiceAsync(db, clientId, "INV-UPD-VAL-7B");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-UPD-VAL-7B", clientId, new DateTime(2026, 4, 10));
        request.LineItems =
        [
            new InvoiceLineItemRequest
            {
                ServiceId = 999,
                Name = "Unknown line service",
                Quantity = 1m,
                Unit = "service",
                Price = 1_000m,
            },
        ];

        var action = await controller.Update(invoiceId, request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal("Unknown invoice line item serviceId 999", badRequest.Value);
    }

    [Fact]
    public async Task Update_RejectsTooManyClientCases()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 1, maxActs: 5);
        var clientId = await CreateClientAsync(db, "Update Case Limit Client");
        var invoiceId = await CreateInvoiceAsync(db, clientId, "INV-UPD-VAL-8");
        var caseId1 = await CreateClientCaseAsync(db, clientId, "Case A");
        var caseId2 = await CreateClientCaseAsync(db, clientId, "Case B");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-UPD-VAL-8", clientId, new DateTime(2026, 4, 10));
        request.ClientCaseIds = [caseId1, caseId2];

        var action = await controller.Update(invoiceId, request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    [Fact]
    public async Task Update_RejectsTooManyActs()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 5, maxActs: 1);
        var clientId = await CreateClientAsync(db, "Update Act Limit Client");
        var invoiceId = await CreateInvoiceAsync(db, clientId, "INV-UPD-VAL-9");
        var actId1 = await CreateActAsync(db, clientId, "ACT-UPD-VAL-1");
        var actId2 = await CreateActAsync(db, clientId, "ACT-UPD-VAL-2");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-UPD-VAL-9", clientId, new DateTime(2026, 4, 10));
        request.ActIds = [actId1, actId2];

        var action = await controller.Update(invoiceId, request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    [Fact]
    public async Task Update_AllowsActLinkedToSiblingPaymentOfSameInvoice_AndSavesEditedAmount()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Update Act Same Aggregate Client");
        var invoiceId = await CreateInvoiceAsync(db, clientId, "INV-UPD-ACT-SAME");
        var invoice = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == invoiceId);
        Assert.NotNull(invoice.InvoiceId);

        var siblingPayment = new Payment
        {
            Account = "INV-UPD-ACT-SAME",
            AccountDate = new DateTime(2026, 4, 10),
            InvoiceId = invoice.InvoiceId,
            Amount = 10_000m,
            ClientId = clientId,
            Date = new DateTime(2026, 4, 12),
            PlannedDate = new DateTime(2026, 4, 12),
            Description = "Linked invoice payment",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Completed,
            Type = PaymentType.Income,
            CreatedAt = DateTime.UtcNow,
        };
        db.Payments.Add(siblingPayment);
        await db.SaveChangesAsync();

        var actId = await CreateActAsync(db, clientId, "ACT-UPD-SAME-AGGREGATE");
        var linkedAct = await db.Acts.SingleAsync(act => act.Id == actId);
        linkedAct.InvoiceId = siblingPayment.Id;
        linkedAct.InvoiceNumber = "INV-UPD-ACT-SAME";
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-UPD-ACT-SAME", clientId, new DateTime(2026, 4, 10));
        request.Amount = 165_000m;
        request.ActIds = [actId];
        request.LineItems =
        [
            new InvoiceLineItemRequest
            {
                Name = "Corrected invoice line",
                Quantity = 1m,
                Unit = "шт",
                Price = 165_000m,
            },
        ];

        var action = await controller.Update(invoiceId, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        Assert.IsType<InvoiceDto>(ok.Value);

        var storedInvoice = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == invoiceId);
        Assert.Equal(165_000m, storedInvoice.Amount);

        var storedAct = await db.Acts.AsNoTracking().SingleAsync(act => act.Id == actId);
        Assert.Equal(invoiceId, storedAct.InvoiceId);
        Assert.Equal("INV-UPD-ACT-SAME", storedAct.InvoiceNumber);
    }

    [Fact]
    public async Task Update_RejectsUnknownResponsibleUser()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Update Responsible Client");
        var invoiceId = await CreateInvoiceAsync(db, clientId, "INV-UPD-VAL-10");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-UPD-VAL-10", clientId, new DateTime(2026, 4, 10));
        request.ResponsibleIds = [999];

        var action = await controller.Update(invoiceId, request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal("One or more responsible users were not found.", badRequest.Value);
    }

    [Fact]
    public async Task Update_AddsNewResponsible_WhenInvoicePreviouslyHadNone()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Update Responsible Add Client");
        var invoiceId = await CreateInvoiceAsync(db, clientId, "INV-UPD-VAL-11");
        var responsibleId = await CreateUserAsync(db, "Fresh Responsible");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-UPD-VAL-11", clientId, new DateTime(2026, 4, 10));
        request.ResponsibleIds = [responsibleId];

        var action = await controller.Update(invoiceId, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        Assert.IsType<InvoiceDto>(ok.Value);

        var storedResponsibleIds = await db.Set<PaymentResponsible>()
            .AsNoTracking()
            .Where(item => item.PaymentId == invoiceId)
            .Select(item => item.UserId)
            .ToListAsync();
        Assert.Equal([responsibleId], storedResponsibleIds);
    }

    [Fact]
    public async Task Update_ProcessingStatus_PreservesExistingPaidState_FromCompletedInvoice()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Processing Update Client");
        var invoiceId = await CreateInvoiceAsync(
            db,
            clientId,
            "INV-UPD-PROC-1",
            request =>
            {
                request.Status = PaymentStatus.Completed;
                request.PaidDate = new DateTime(2026, 4, 11);
            });

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-UPD-PROC-1", clientId, new DateTime(2026, 4, 10));
        request.Status = PaymentStatus.Processing;

        var action = await controller.Update(invoiceId, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        Assert.IsType<InvoiceDto>(ok.Value);

        var storedPayment = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == invoiceId);
        Assert.Equal(PaymentStatus.Processing, storedPayment.Status);
        Assert.Equal(11_000m, storedPayment.PaidAmount);
        Assert.Equal(new DateTime(2026, 4, 11), storedPayment.PaidDate);
        Assert.Equal(new DateTime(2026, 4, 11), storedPayment.LastPaymentDate);
        Assert.False(storedPayment.IsPaid);
    }

    [Fact]
    public async Task Update_ProcessingStatusWithoutPaidAmount_NormalizesToPending()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Unpaid Processing Update Client");
        var invoiceDate = DateTime.UtcNow.Date.AddDays(5);
        var invoiceId = await CreateInvoiceAsync(db, clientId, "INV-UPD-PROC-0", request =>
        {
            request.Date = invoiceDate;
            request.DueDate = invoiceDate;
        });

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-UPD-PROC-0", clientId, invoiceDate);
        request.DueDate = invoiceDate;
        request.Status = PaymentStatus.Processing;
        request.PaidDate = invoiceDate.AddDays(1);

        var action = await controller.Update(invoiceId, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        Assert.IsType<InvoiceDto>(ok.Value);

        var storedPayment = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == invoiceId);
        Assert.Equal(PaymentStatus.Pending, storedPayment.Status);
        Assert.Equal(0m, storedPayment.PaidAmount);
        Assert.Null(storedPayment.PaidDate);
        Assert.Null(storedPayment.LastPaymentDate);
        Assert.False(storedPayment.IsPaid);
    }

    [Fact]
    public async Task Update_ReturnsShortDuplicateMessage_WhenAnotherActiveInvoiceExists()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Duplicate Update Client");
        await CreateActiveInvoiceAsync(db, "INV-UPD-DUP", new DateTime(2026, 4, 7), clientId);
        var invoiceId = await CreateInvoiceAsync(db, clientId, "INV-UPD-UNIQUE");
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-UPD-DUP", clientId, new DateTime(2026, 4, 8));

        var action = await controller.Update(invoiceId, request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        var message = Assert.IsType<string>(badRequest.Value);
        Assert.Equal("Номер счета 'INV-UPD-DUP' уже занят в 2026 году.", message);
    }

    [Fact]
    public async Task Update_RejectsMutatingProtectedInstallmentPayment()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Protected Installment Client");
        var invoiceId = await CreateInvoiceAsync(
            db,
            clientId,
            "INV-UPD-VAL-12",
            request =>
            {
                request.InstallmentPlan = CreateInstallmentPlanRequest(
                    new DateTime(2026, 4, 10),
                    new DateTime(2026, 5, 10),
                    new DateTime(2026, 6, 10));
            });

        var invoicePayment = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == invoiceId);
        var protectedInstallment = await db.Payments
            .Where(item =>
                item.InvoiceId == invoicePayment.InvoiceId
                && item.Id != invoiceId
                && item.SystemNotes != null
                && item.SystemNotes.Contains("[pp:installment-payment]"))
            .OrderBy(item => item.Date)
            .FirstAsync();
        protectedInstallment.Status = PaymentStatus.Completed;
        protectedInstallment.PaidAmount = protectedInstallment.Amount;
        protectedInstallment.PaidDate = protectedInstallment.Date;
        protectedInstallment.LastPaymentDate = protectedInstallment.Date;
        protectedInstallment.IsPaid = true;
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("INV-UPD-VAL-12", clientId, new DateTime(2026, 4, 10));
        request.InstallmentPlan = CreateInstallmentPlanRequest(
            new DateTime(2026, 4, 10),
            new DateTime(2026, 5, 15),
            new DateTime(2026, 6, 10));

        var action = await controller.Update(invoiceId, request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    private static UpsertInvoiceRequest CreateRequest(string number, int clientId, DateTime date)
    {
        return new UpsertInvoiceRequest
        {
            Number = number,
            Date = date,
            DueDate = date.AddDays(2),
            Amount = 11_000m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            ClientId = clientId,
        };
    }

    private static InvoiceInstallmentPlanRequest CreateInstallmentPlanRequest(DateTime startDate, params DateTime[] dates)
    {
        var paymentAmount = dates.Length > 0 ? decimal.Round(11_000m / dates.Length, 2) : 11_000m;

        return new InvoiceInstallmentPlanRequest
        {
            Total = 11_000m,
            DownPayment = 0m,
            AnnualRate = 0m,
            Months = Math.Max(1, dates.Length),
            StartDate = startDate,
            RoundingMode = InstallmentRoundingMode.None,
            ToPay = 11_000m,
            Overpay = 0m,
            LoanAmount = 11_000m,
            BaseMonthlyPayment = paymentAmount,
            RoundedMonthlyPayment = paymentAmount,
            Items = dates.Select((itemDate, index) =>
            {
                var isLast = index == dates.Length - 1;
                var payment = isLast ? 11_000m - paymentAmount * (dates.Length - 1) : paymentAmount;
                var balance = isLast ? 0m : Math.Max(0m, 11_000m - paymentAmount * (index + 1));

                return new InvoiceInstallmentPlanItemRequest
                {
                    Sequence = index + 1,
                    Date = itemDate,
                    Principal = payment,
                    Interest = 0m,
                    Payment = payment,
                    Balance = balance,
                };
            }).ToList(),
        };
    }

    private static T GetAnonymousProperty<T>(object? source, string name)
    {
        Assert.NotNull(source);
        var property = source!.GetType().GetProperty(name);
        Assert.NotNull(property);
        return Assert.IsType<T>(property!.GetValue(source));
    }

    private static async Task<int> CreateClientAsync(PaymentContext db, string name)
    {
        var client = new Client
        {
            Name = name,
            IsActive = true,
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    private static async Task<int> CreateServiceAsync(PaymentContext db, string name)
    {
        var service = new ServiceDictionaryItem
        {
            Name = name,
            Description = string.Empty,
            Unit = "усл. ед.",
            DefaultPrice = 0m,
            ColorHex = "#111827",
            IsActive = true,
        };

        db.Services.Add(service);
        await db.SaveChangesAsync();
        return service.Id;
    }

    private static async Task<int> CreateClientCaseAsync(PaymentContext db, int clientId, string title)
    {
        var clientCase = new ClientCase
        {
            ClientId = clientId,
            Title = title,
            Status = ClientCaseStatus.Open,
        };

        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        return clientCase.Id;
    }

    private static async Task<int> CreateActAsync(PaymentContext db, int clientId, string number)
    {
        var act = new Act
        {
            Number = number,
            Title = $"Act {number}",
            Date = new DateTime(2026, 4, 1),
            Amount = 3_000m,
            ClientId = clientId,
            Status = ActStatus.Created,
        };

        db.Acts.Add(act);
        await db.SaveChangesAsync();
        return act.Id;
    }

    private static async Task<int> CreateContractAsync(PaymentContext db, int clientId, string number)
    {
        var contract = new Contract
        {
            Number = number,
            Title = $"Contract {number}",
            Date = new DateTime(2026, 4, 1),
            BillingMode = ContractBillingMode.Fixed,
            Amount = 25_000m,
        };

        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        db.Set<ClientContract>().Add(new ClientContract
        {
            ClientId = clientId,
            ContractId = contract.Id,
        });
        await db.SaveChangesAsync();

        return contract.Id;
    }

    private static async Task<int> CreateUserAsync(PaymentContext db, string fullName, string roleName = "user")
    {
        var role = await db.Roles.FirstOrDefaultAsync(item => item.Name == roleName);
        if (role is null)
        {
            role = new Role
            {
                Name = roleName,
                Description = roleName,
                CreatedAt = DateTime.UtcNow,
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
        }

        var user = new User
        {
            Email = $"invoice-validation-{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            FullName = fullName,
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task ConfigureInvoiceRulesAsync(PaymentContext db, int maxClientCases, int maxActs)
    {
        var now = DateTime.UtcNow;
        db.SystemSettings.AddRange(
            new SystemSetting
            {
                Key = "invoices.rules.maxClientCasesPerInvoice",
                Value = maxClientCases.ToString(),
                CreatedAt = now,
                UpdatedAt = now,
            },
            new SystemSetting
            {
                Key = "invoices.rules.maxActsPerInvoice",
                Value = maxActs.ToString(),
                CreatedAt = now,
                UpdatedAt = now,
            });
        await db.SaveChangesAsync();
    }

    private static async Task<int> CreateInvoiceAsync(
        PaymentContext db,
        int clientId,
        string number,
        Action<UpsertInvoiceRequest>? configure = null)
    {
        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest(number, clientId, new DateTime(2026, 4, 10));
        configure?.Invoke(request);

        var action = await controller.Create(request, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<InvoiceDto>(created.Value);
        return dto.Id;
    }

    private static async Task CreateOrphanInvoiceAsync(PaymentContext db, string number, DateTime date)
    {
        db.Invoices.Add(new Invoice
        {
            Number = number,
            Date = date,
        });

        await db.SaveChangesAsync();
    }

    private static async Task CreateActiveInvoiceAsync(PaymentContext db, string number, DateTime date, int clientId)
    {
        var invoice = new Invoice
        {
            Number = number,
            Date = date,
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        db.Payments.Add(new Payment
        {
            Account = number,
            AccountDate = date,
            InvoiceId = invoice.Id,
            ClientId = clientId,
            DocumentKind = DocumentKind.Invoice,
            Date = date,
            PlannedDate = date,
            Amount = 10_000m,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            Description = "Тестовый счет",
        });

        await db.SaveChangesAsync();
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

    private static InvoicesController CreateController(PaymentContext db, bool isAdmin, int? userId)
    {
        var claims = new List<Claim>();
        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        if (userId.HasValue)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));
        }

        var controller = new InvoicesController(db, new NoopEmailService(), new NoopFormRequirementsService());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
            },
        };

        return controller;
    }

    private sealed class NoopEmailService : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType) => Task.CompletedTask;
    }

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }
}
