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

public class InvoicesControllerCrudTests
{
    private const string InvoiceMaxClientCasesSettingKey = "invoices.rules.maxClientCasesPerInvoice";
    private const string InvoiceMaxActsSettingKey = "invoices.rules.maxActsPerInvoice";
    private const string InstallmentPaymentMarker = "[pp:installment-payment]";

    [Fact]
    public async Task Create_PersistsFullInvoiceAggregate_WithRelationsAndInstallments()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client Alpha");
        var contractId = await CreateContractAsync(db, clientId, "CTR-501");
        var caseId1 = await CreateClientCaseAsync(db, clientId, "Case Alpha");
        var caseId2 = await CreateClientCaseAsync(db, clientId, "Case Beta");
        var actId1 = await CreateActAsync(db, clientId, "ACT-501");
        var actId2 = await CreateActAsync(db, clientId, "ACT-502");
        var responsibleId1 = await CreateUserAsync(db, "Responsible One");
        var responsibleId2 = await CreateUserAsync(db, "Responsible Two");
        var invoiceServiceId = await CreateServiceAsync(db, "Primary Service");
        var lineServiceId = await CreateServiceAsync(db, "Line Service");
        var paymentSourceId = await CreatePaymentSourceAsync(db, "Bank");
        var incomeTypeId = await CreateIncomeTypeAsync(db, "Legal income");
        var dealTypeId = await CreateDealTypeAsync(db, "Consulting");
        var paymentStatusEntityId = await CreatePaymentStatusEntityAsync(db, "Awaiting signature");

        var controller = CreateController(db, isAdmin: true, userId: actorUserId);
        var request = CreateRequest("INV-501", clientId, new DateTime(2026, 4, 15), 18_000m);
        request.Status = PaymentStatus.Completed;
        request.PaidDate = new DateTime(2026, 4, 16);
        request.ContractId = contractId;
        request.ClientCaseId = caseId1;
        request.ClientCaseIds = [caseId1, caseId2];
        request.ClientCaseReference = " CASE-REF-501 ";
        request.Description = " Full invoice graph ";
        request.IsNotForAccounting = true;
        request.ActId = actId1;
        request.ActIds = [actId1, actId2];
        request.ActReference = " ACT-REF-501 ";
        request.CounterpartyName = "Legacy counterparty";
        request.PaymentSourceId = paymentSourceId;
        request.ServiceId = invoiceServiceId;
        request.IncomeTypeId = incomeTypeId;
        request.DealTypeId = dealTypeId;
        request.PaymentStatusEntityId = paymentStatusEntityId;
        request.ResponsibleIds = [responsibleId1, responsibleId1, responsibleId2];
        request.LineItems =
        [
            new InvoiceLineItemRequest
            {
                ServiceId = lineServiceId,
                Name = "Draft statement of claim",
                Quantity = 1m,
                Unit = "service",
                Price = 12_000m,
            },
            new InvoiceLineItemRequest
            {
                Name = "Court attendance",
                Quantity = 2m,
                Unit = "hour",
                Price = 3_000m,
            },
        ];
        request.InstallmentPlan = CreateInstallmentPlanRequest(
            total: 18_000m,
            startDate: new DateTime(2026, 4, 15),
            new DateTime(2026, 5, 15),
            new DateTime(2026, 6, 15));

        var action = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<InvoiceDto>(created.Value);

        Assert.Equal("INV-501", dto.Number);
        Assert.Equal(new DateTime(2026, 4, 15), dto.Date);
        Assert.Equal(new DateTime(2026, 6, 15), dto.DueDate);
        Assert.Equal(18_000m, dto.Amount);
        Assert.Equal(PaymentType.Income, dto.Type);
        Assert.Equal(PaymentStatus.Completed, dto.Status);
        Assert.Equal(InvoiceWorkflowStatus.Paid, dto.WorkflowStatus);
        Assert.True(dto.IsPaid);
        Assert.Equal(new DateTime(2026, 4, 16), dto.PaidDate);
        Assert.Equal(clientId, dto.ClientId);
        Assert.Equal(contractId, dto.ContractId);
        Assert.Equal(invoiceServiceId, dto.ServiceId);
        Assert.Equal(2, dto.ClientCaseIds.Count);
        Assert.Equal(new[] { caseId1, caseId2 }, dto.ClientCaseIds.OrderBy(id => id).ToArray());
        Assert.Equal(2, dto.ActIds.Count);
        Assert.Equal(new[] { actId1, actId2 }, dto.ActIds.OrderBy(id => id).ToArray());
        Assert.Equal(2, dto.Responsibles.Count);
        Assert.Equal(new[] { responsibleId1, responsibleId2 }, dto.Responsibles.Select(item => item.Id).OrderBy(id => id).ToArray());
        Assert.Equal(2, dto.LineItems.Count);

        db.ChangeTracker.Clear();

        var storedParent = await db.Payments
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == dto.Id);

        Assert.Equal("INV-501", storedParent.Account);
        Assert.Equal(new DateTime(2026, 4, 15), storedParent.AccountDate);
        Assert.Equal(new DateTime(2026, 6, 15), storedParent.Date);
        Assert.Equal(18_000m, storedParent.Amount);
        Assert.Equal(PaymentStatus.Completed, storedParent.Status);
        Assert.Equal(InvoiceWorkflowStatus.Paid, storedParent.InvoiceWorkflowStatus);
        Assert.Equal(18_000m, storedParent.PaidAmount);
        Assert.Equal(new DateTime(2026, 4, 16), storedParent.PaidDate);
        Assert.Equal("CASE-REF-501", storedParent.ClientCaseReference);
        Assert.Equal("ACT-REF-501", storedParent.Notes);
        Assert.Equal("Full invoice graph", storedParent.Description);
        Assert.True(storedParent.IsNotForAccounting);
        Assert.Equal(contractId, storedParent.ContractId);
        Assert.Equal(paymentSourceId, storedParent.PaymentSourceId);
        Assert.Equal(invoiceServiceId, storedParent.ServiceId);
        Assert.Equal(incomeTypeId, storedParent.IncomeTypeId);
        Assert.Equal(dealTypeId, storedParent.DealTypeId);
        Assert.Equal(paymentStatusEntityId, storedParent.PaymentStatusId);
        Assert.Null(storedParent.CounterpartyName);
        Assert.NotNull(storedParent.InvoiceId);
        Assert.Contains(storedParent.ClientCaseId ?? 0, new[] { caseId1, caseId2 });

        var storedResponsibleIds = await db.Set<PaymentResponsible>()
            .AsNoTracking()
            .Where(item => item.PaymentId == dto.Id)
            .Select(item => item.UserId)
            .OrderBy(id => id)
            .ToListAsync();
        Assert.Equal(new[] { responsibleId1, responsibleId2 }, storedResponsibleIds);

        var storedCaseIds = await db.Set<InvoiceClientCase>()
            .AsNoTracking()
            .Where(item => item.PaymentId == dto.Id)
            .Select(item => item.ClientCaseId)
            .OrderBy(id => id)
            .ToListAsync();
        Assert.Equal(new[] { caseId1, caseId2 }, storedCaseIds);

        var storedLineItems = await db.InvoiceLineItems
            .AsNoTracking()
            .Where(item => item.PaymentId == dto.Id)
            .OrderBy(item => item.SortOrder)
            .ToListAsync();
        Assert.Equal(2, storedLineItems.Count);
        Assert.Equal(lineServiceId, storedLineItems[0].ServiceId);
        Assert.Equal("Draft statement of claim", storedLineItems[0].Name);
        Assert.Equal(1m, storedLineItems[0].Quantity);
        Assert.Equal("service", storedLineItems[0].Unit);
        Assert.Equal(12_000m, storedLineItems[0].Price);
        Assert.Null(storedLineItems[1].ServiceId);
        Assert.Equal("Court attendance", storedLineItems[1].Name);
        Assert.Equal(2m, storedLineItems[1].Quantity);
        Assert.Equal("hour", storedLineItems[1].Unit);
        Assert.Equal(3_000m, storedLineItems[1].Price);

        var storedActs = await db.Acts
            .AsNoTracking()
            .Where(act => act.Id == actId1 || act.Id == actId2)
            .OrderBy(act => act.Id)
            .ToListAsync();
        Assert.All(storedActs, act =>
        {
            Assert.Equal(dto.Id, act.InvoiceId);
            Assert.Equal("INV-501", act.InvoiceNumber);
        });

        var storedInstallments = await LoadInstallmentPaymentsAsync(db, storedParent.InvoiceId!.Value, dto.Id);
        Assert.Equal(2, storedInstallments.Count);
        Assert.Equal(new[] { new DateTime(2026, 5, 15), new DateTime(2026, 6, 15) }, storedInstallments.Select(item => item.Date).ToArray());
        Assert.All(storedInstallments, installment =>
        {
            Assert.Equal(DocumentKind.Payment, installment.DocumentKind);
            Assert.Equal(storedParent.InvoiceId, installment.InvoiceId);
            Assert.Equal("INV-501", installment.Account);
            Assert.Equal(new DateTime(2026, 4, 15), installment.AccountDate);
            Assert.Equal(clientId, installment.ClientId);
            Assert.Equal(contractId, installment.ContractId);
            Assert.Equal(paymentSourceId, installment.PaymentSourceId);
            Assert.Equal(invoiceServiceId, installment.ServiceId);
            Assert.Equal(incomeTypeId, installment.IncomeTypeId);
            Assert.Equal(dealTypeId, installment.DealTypeId);
            Assert.Equal(paymentStatusEntityId, installment.PaymentStatusId);
            Assert.Contains(installment.ClientCaseId ?? 0, new[] { caseId1, caseId2 });
            Assert.False(installment.IsPaid);
        });

        var installmentResponsibleIds = await db.Set<PaymentResponsible>()
            .AsNoTracking()
            .Where(item => storedInstallments.Select(payment => payment.Id).Contains(item.PaymentId))
            .OrderBy(item => item.PaymentId)
            .ThenBy(item => item.UserId)
            .Select(item => item.UserId)
            .ToListAsync();
        Assert.Equal(new[] { responsibleId1, responsibleId2, responsibleId1, responsibleId2 }, installmentResponsibleIds);
    }

    [Fact]
    public async Task Get_FiltersByWorkflowStatus()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client Workflow");
        var controller = CreateController(db, isAdmin: true, userId: actorUserId);

        var paidRequest = CreateRequest("INV-WF-PAID", clientId, new DateTime(2026, 4, 15), 10_000m);
        paidRequest.Status = PaymentStatus.Completed;
        paidRequest.PaidDate = new DateTime(2026, 4, 16);
        await controller.Create(paidRequest, CancellationToken.None);

        var signingRequest = CreateRequest("INV-WF-SIGN", clientId, new DateTime(2026, 4, 16), 8_000m);
        signingRequest.WorkflowStatus = InvoiceWorkflowStatus.OnSigning;
        await controller.Create(signingRequest, CancellationToken.None);

        var action = await controller.Get(
            from: null,
            to: null,
            status: null,
            clientId: null,
            clientIds: null,
            contractId: null,
            caseId: null,
            responsibleId: null,
            search: null,
            type: null,
            sortBy: null,
            sortDir: null,
            clientRole: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None,
            workflowStatus: InvoiceWorkflowStatus.Paid);

        var ok = Assert.IsType<OkObjectResult>(action);
        var items = GetAnonymousProperty<List<InvoiceDto>>(ok.Value, "items");
        var item = Assert.Single(items);
        Assert.Equal("INV-WF-PAID", item.Number);
        Assert.Equal(InvoiceWorkflowStatus.Paid, item.WorkflowStatus);
    }

    [Fact]
    public async Task SigningWorkflowActions_UpdateStatusAndTimestamps()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client Signing");
        var controller = CreateController(db, isAdmin: true, userId: actorUserId);

        var createAction = await controller.Create(
            CreateRequest("INV-SIGN-1", clientId, new DateTime(2026, 4, 20), 12_000m),
            CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        var sendAction = await controller.SendToSigning(createdDto.Id, CancellationToken.None);

        var sendOk = Assert.IsType<OkObjectResult>(sendAction.Result);
        var sentDto = Assert.IsType<InvoiceDto>(sendOk.Value);
        Assert.Equal(InvoiceWorkflowStatus.OnSigning, sentDto.WorkflowStatus);
        Assert.NotNull(sentDto.SentForSigningAt);
        Assert.Null(sentDto.SignedAt);

        var signAction = await controller.MarkSigned(createdDto.Id, CancellationToken.None);

        var signOk = Assert.IsType<OkObjectResult>(signAction.Result);
        var signedDto = Assert.IsType<InvoiceDto>(signOk.Value);
        Assert.Equal(InvoiceWorkflowStatus.SignedByClient, signedDto.WorkflowStatus);
        Assert.NotNull(signedDto.SentForSigningAt);
        Assert.NotNull(signedDto.SignedAt);

        db.ChangeTracker.Clear();
        var stored = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);
        Assert.Equal(InvoiceWorkflowStatus.SignedByClient, stored.InvoiceWorkflowStatus);
        Assert.NotNull(stored.InvoiceSentForSigningAt);
        Assert.NotNull(stored.InvoiceSignedAt);
        Assert.Equal(PaymentStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task Cancel_StoresReasonAndCancelsGeneratedInstallments()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client Cancel");
        var controller = CreateController(db, isAdmin: true, userId: actorUserId);
        var request = CreateRequest("INV-CANCEL-1", clientId, new DateTime(2026, 4, 20), 12_000m);
        request.InstallmentPlan = CreateInstallmentPlanRequest(
            total: 12_000m,
            startDate: new DateTime(2026, 4, 20),
            new DateTime(2026, 5, 20),
            new DateTime(2026, 6, 20));

        var createAction = await controller.Create(request, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        var cancelAction = await controller.Cancel(
            createdDto.Id,
            new CancelInvoiceRequest { Reason = "Клиент отказался от услуги" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(cancelAction.Result);
        var dto = Assert.IsType<InvoiceDto>(ok.Value);
        Assert.Equal(PaymentStatus.Cancelled, dto.Status);
        Assert.Equal(InvoiceWorkflowStatus.Cancelled, dto.WorkflowStatus);
        Assert.NotNull(dto.CancelledAt);
        Assert.Equal(actorUserId, dto.CancelledByUserId);
        Assert.Equal("Клиент отказался от услуги", dto.CancelledReason);

        db.ChangeTracker.Clear();
        var storedParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);
        Assert.Equal(PaymentStatus.Cancelled, storedParent.Status);
        Assert.Equal(InvoiceWorkflowStatus.Cancelled, storedParent.InvoiceWorkflowStatus);
        Assert.NotNull(storedParent.InvoiceCancelledAt);
        Assert.Equal(actorUserId, storedParent.InvoiceCancelledByUserId);
        Assert.Equal("Клиент отказался от услуги", storedParent.InvoiceCancelledReason);
        Assert.Contains("Счёт аннулирован", storedParent.SystemNotes, StringComparison.Ordinal);

        var installments = await LoadInstallmentPaymentsAsync(db, storedParent.InvoiceId!.Value, storedParent.Id);
        Assert.Equal(2, installments.Count);
        Assert.All(installments, installment =>
        {
            Assert.Equal(PaymentStatus.Cancelled, installment.Status);
            Assert.False(installment.IsPaid);
            Assert.Equal(0m, installment.PaidAmount);
            Assert.Contains("Платёж рассрочки аннулирован", installment.SystemNotes, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Cancel_CancelsLinkedActs()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client With Act");
        var controller = CreateController(db, isAdmin: true, userId: actorUserId);
        var request = CreateRequest("INV-CANCEL-ACT", clientId, new DateTime(2026, 4, 20), 12_000m);
        request.InstallmentPlan = CreateInstallmentPlanRequest(
            total: 12_000m,
            startDate: new DateTime(2026, 4, 20),
            new DateTime(2026, 5, 20),
            new DateTime(2026, 6, 20));

        var createAction = await controller.Create(request, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        var linkedAct = new Act
        {
            Number = "ACT-CANCEL-LINKED",
            Title = "Linked act",
            Date = new DateTime(2026, 4, 21),
            Amount = 12_000m,
            ClientId = clientId,
            InvoiceId = createdDto.Id,
            InvoiceNumber = request.Number,
            Status = ActStatus.Signed,
            WorkflowStatus = ActWorkflowStatus.Signed,
            SignedAt = new DateTime(2026, 4, 22),
        };
        var unrelatedAct = new Act
        {
            Number = "ACT-CANCEL-OTHER",
            Title = "Unrelated act",
            Date = new DateTime(2026, 4, 21),
            Amount = 12_000m,
            ClientId = clientId,
            InvoiceNumber = request.Number,
            Status = ActStatus.Signed,
            WorkflowStatus = ActWorkflowStatus.Signed,
            SignedAt = new DateTime(2026, 4, 22),
        };
        db.Acts.AddRange(linkedAct, unrelatedAct);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var cancelAction = await controller.Cancel(
            createdDto.Id,
            new CancelInvoiceRequest { Reason = "Работа прекращена" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(cancelAction.Result);
        var dto = Assert.IsType<InvoiceDto>(ok.Value);
        Assert.Equal(PaymentStatus.Cancelled, dto.Status);

        db.ChangeTracker.Clear();
        var storedLinkedAct = await db.Acts.AsNoTracking().SingleAsync(act => act.Id == linkedAct.Id);
        Assert.Equal(ActStatus.Terminated, storedLinkedAct.Status);
        Assert.Equal(ActWorkflowStatus.Cancelled, storedLinkedAct.WorkflowStatus);
        Assert.NotNull(storedLinkedAct.CancelledAt);
        Assert.Equal(actorUserId, storedLinkedAct.CancelledByUserId);
        Assert.Equal("Работа прекращена", storedLinkedAct.CancelledReason);
        Assert.Contains("Акт аннулирован вместе со счётом №INV-CANCEL-ACT", storedLinkedAct.Comment, StringComparison.Ordinal);
        Assert.Contains("Работа прекращена", storedLinkedAct.Comment, StringComparison.Ordinal);

        var storedUnrelatedAct = await db.Acts.AsNoTracking().SingleAsync(act => act.Id == unrelatedAct.Id);
        Assert.Equal(ActStatus.Signed, storedUnrelatedAct.Status);
        Assert.Equal(ActWorkflowStatus.Signed, storedUnrelatedAct.WorkflowStatus);

        var storedParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);
        var installments = await LoadInstallmentPaymentsAsync(db, storedParent.InvoiceId!.Value, storedParent.Id);
        Assert.Equal(2, installments.Count);
        Assert.All(installments, installment => Assert.Equal(PaymentStatus.Cancelled, installment.Status));
    }

    [Fact]
    public async Task Cancel_RejectsInvoiceWithReceivedMoney()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client Paid Cancel");
        var controller = CreateController(db, isAdmin: true, userId: actorUserId);
        var request = CreateRequest("INV-CANCEL-PAID", clientId, new DateTime(2026, 4, 20), 12_000m);
        request.Status = PaymentStatus.Completed;
        request.PaidDate = new DateTime(2026, 4, 21);

        var createAction = await controller.Create(request, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        var cancelAction = await controller.Cancel(
            createdDto.Id,
            new CancelInvoiceRequest { Reason = "Ошибка в счёте" },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(cancelAction.Result);
        var message = Assert.IsType<string>(badRequest.Value);
        Assert.Contains("полученной оплатой", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancel_PaidInvoiceWithAdvanceAction_CreatesClientBalanceCredit()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client Paid Advance");
        var controller = CreateController(db, isAdmin: true, userId: actorUserId);
        var request = CreateRequest("INV-CANCEL-ADV", clientId, new DateTime(2026, 4, 20), 12_000m);
        request.Status = PaymentStatus.Completed;
        request.PaidDate = new DateTime(2026, 4, 21);

        var createAction = await controller.Create(request, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        var cancelAction = await controller.Cancel(
            createdDto.Id,
            new CancelInvoiceRequest
            {
                Reason = "Ошибочный счёт",
                ReceivedMoneyAction = InvoiceCancellationMoneyAction.LeaveAsAdvance,
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(cancelAction.Result);
        var dto = Assert.IsType<InvoiceDto>(ok.Value);
        Assert.Equal(PaymentStatus.Cancelled, dto.Status);
        Assert.Equal(InvoiceWorkflowStatus.Cancelled, dto.WorkflowStatus);
        Assert.Equal(0m, dto.PaidAmount);

        db.ChangeTracker.Clear();
        var storedPayment = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);
        Assert.Equal(PaymentStatus.Cancelled, storedPayment.Status);
        Assert.Equal(0m, storedPayment.PaidAmount);
        Assert.False(storedPayment.IsPaid);
        Assert.Null(storedPayment.PaidDate);
        Assert.Null(storedPayment.LastPaymentDate);
        Assert.Contains("аванс контрагента", storedPayment.SystemNotes, StringComparison.OrdinalIgnoreCase);

        var balanceTransaction = await db.ClientBalanceTransactions.AsNoTracking().SingleAsync();
        Assert.Equal(clientId, balanceTransaction.ClientId);
        Assert.Equal(storedPayment.InvoiceId, balanceTransaction.InvoiceId);
        Assert.Equal(createdDto.Id, balanceTransaction.PaymentId);
        Assert.Equal(12_000m, balanceTransaction.Amount);
        Assert.Equal(ClientBalanceTransactionType.AdvanceFromCancelledInvoice, balanceTransaction.Type);
        Assert.Equal(ClientBalanceDirection.Credit, balanceTransaction.Direction);
        Assert.Equal(ClientBalanceService.BuildCancelledInvoiceAdvanceKey(createdDto.Id), balanceTransaction.ExternalKey);
        Assert.Contains("Ошибочный счёт", balanceTransaction.Description, StringComparison.Ordinal);

        var balance = await ClientBalanceService.GetBalanceAsync(db, clientId);
        Assert.Equal(12_000m, balance);
    }

    [Fact]
    public async Task Cancel_PaidLegalEntityInvoiceWithAdvanceAction_CreatesLegalEntityBalanceCredit()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var legalEntityId = await CreateLegalEntityAsync(db, "Legal Entity Advance");
        var controller = CreateController(db, isAdmin: true, userId: actorUserId);
        var request = CreateLegalEntityRequest("INV-CANCEL-LEGAL-ADV", legalEntityId, new DateTime(2026, 4, 20), 12_000m);
        request.Status = PaymentStatus.Completed;
        request.PaidDate = new DateTime(2026, 4, 21);

        var createAction = await controller.Create(request, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        var cancelAction = await controller.Cancel(
            createdDto.Id,
            new CancelInvoiceRequest
            {
                Reason = "Legal entity advance",
                ReceivedMoneyAction = InvoiceCancellationMoneyAction.LeaveAsAdvance,
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(cancelAction.Result);
        var dto = Assert.IsType<InvoiceDto>(ok.Value);
        Assert.Equal(PaymentStatus.Cancelled, dto.Status);
        Assert.Equal(0m, dto.PaidAmount);

        db.ChangeTracker.Clear();
        var storedPayment = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);
        Assert.Null(storedPayment.ClientId);
        Assert.Equal(legalEntityId, storedPayment.LegalEntityId);
        Assert.Equal(PaymentStatus.Cancelled, storedPayment.Status);

        var balanceTransaction = await db.ClientBalanceTransactions.AsNoTracking().SingleAsync();
        Assert.Null(balanceTransaction.ClientId);
        Assert.Equal(legalEntityId, balanceTransaction.LegalEntityId);
        Assert.Equal(storedPayment.InvoiceId, balanceTransaction.InvoiceId);
        Assert.Equal(createdDto.Id, balanceTransaction.PaymentId);
        Assert.Equal(12_000m, balanceTransaction.Amount);
        Assert.Equal(ClientBalanceTransactionType.AdvanceFromCancelledInvoice, balanceTransaction.Type);
        Assert.Equal(ClientBalanceDirection.Credit, balanceTransaction.Direction);

        var balance = await ClientBalanceService.GetLegalEntityBalanceAsync(db, legalEntityId);
        Assert.Equal(12_000m, balance);
    }

    [Fact]
    public async Task Cancel_InstallmentInvoiceWithPaidGeneratedPayment_TransfersPaidAmountToAdvance()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client Installment Advance");
        var controller = CreateController(db, isAdmin: true, userId: actorUserId);
        var request = CreateRequest("INV-CANCEL-INST-ADV", clientId, new DateTime(2026, 4, 20), 12_000m);
        request.InstallmentPlan = CreateInstallmentPlanRequest(
            total: 12_000m,
            startDate: new DateTime(2026, 4, 20),
            new DateTime(2026, 5, 20),
            new DateTime(2026, 6, 20));

        var createAction = await controller.Create(request, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        var storedParent = await db.Payments.SingleAsync(payment => payment.Id == createdDto.Id);
        var generatedPayment = await db.Payments
            .Where(payment =>
                payment.InvoiceId == storedParent.InvoiceId
                && payment.Id != storedParent.Id
                && payment.SystemNotes != null
                && payment.SystemNotes.Contains(InstallmentPaymentMarker))
            .OrderBy(payment => payment.Id)
            .FirstAsync();
        generatedPayment.Status = PaymentStatus.Completed;
        generatedPayment.PaidAmount = generatedPayment.Amount;
        generatedPayment.IsPaid = true;
        generatedPayment.PaidDate = new DateTime(2026, 5, 21);
        generatedPayment.LastPaymentDate = new DateTime(2026, 5, 21);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var cancelAction = await controller.Cancel(
            createdDto.Id,
            new CancelInvoiceRequest
            {
                Reason = "Отмена рассрочки",
                ReceivedMoneyAction = InvoiceCancellationMoneyAction.LeaveAsAdvance,
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(cancelAction.Result);
        var dto = Assert.IsType<InvoiceDto>(ok.Value);
        Assert.Equal(PaymentStatus.Cancelled, dto.Status);

        db.ChangeTracker.Clear();
        var installments = await LoadInstallmentPaymentsAsync(db, storedParent.InvoiceId!.Value, storedParent.Id);
        Assert.All(installments, installment =>
        {
            Assert.Equal(PaymentStatus.Cancelled, installment.Status);
            Assert.False(installment.IsPaid);
            Assert.Equal(0m, installment.PaidAmount);
        });

        var balanceTransaction = await db.ClientBalanceTransactions.AsNoTracking().SingleAsync();
        Assert.Equal(generatedPayment.Amount, balanceTransaction.Amount);
        Assert.Equal(ClientBalanceTransactionType.AdvanceFromCancelledInvoice, balanceTransaction.Type);

        var balance = await ClientBalanceService.GetBalanceAsync(db, clientId);
        Assert.Equal(generatedPayment.Amount, balance);
    }

    [Fact]
    public async Task Cancel_UnpaidInstallmentsOnly_PreservesPaidGeneratedPayment()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client Keep Paid Installment");
        var controller = CreateController(db, isAdmin: true, userId: actorUserId);
        var invoiceDate = DateTime.UtcNow.Date.AddDays(5);
        var request = CreateRequest("INV-CANCEL-UNPAID-INST", clientId, invoiceDate, 12_000m);
        request.InstallmentPlan = CreateInstallmentPlanRequest(
            total: 12_000m,
            startDate: invoiceDate,
            invoiceDate.AddDays(15),
            invoiceDate.AddDays(45));

        var createAction = await controller.Create(request, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        var storedParent = await db.Payments.SingleAsync(payment => payment.Id == createdDto.Id);
        var generatedPayments = await db.Payments
            .Where(payment =>
                payment.InvoiceId == storedParent.InvoiceId
                && payment.Id != storedParent.Id
                && payment.SystemNotes != null
                && payment.SystemNotes.Contains(InstallmentPaymentMarker))
            .OrderBy(payment => payment.Date)
            .ToListAsync();
        var paidInstallment = generatedPayments[0];
        paidInstallment.Status = PaymentStatus.Completed;
        paidInstallment.PaidAmount = paidInstallment.Amount;
        paidInstallment.IsPaid = true;
        paidInstallment.PaidDate = paidInstallment.Date;
        paidInstallment.LastPaymentDate = paidInstallment.Date;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var cancelAction = await controller.Cancel(
            createdDto.Id,
            new CancelInvoiceRequest
            {
                Reason = "Оставить оплаченную часть",
                CancellationScope = InvoiceCancellationScope.UnpaidInstallmentsOnly,
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(cancelAction.Result);
        var dto = Assert.IsType<InvoiceDto>(ok.Value);
        Assert.Equal(PaymentStatus.Processing, dto.Status);
        Assert.Equal(InvoiceWorkflowStatus.PartiallyPaid, dto.WorkflowStatus);
        Assert.Equal(paidInstallment.Amount, dto.PaidAmount);
        Assert.Equal(0m, dto.OutstandingAmount);
        Assert.Null(dto.CancelledAt);
        Assert.Null(dto.CancelledReason);

        db.ChangeTracker.Clear();
        var storedUpdatedParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);
        Assert.Equal(PaymentStatus.Processing, storedUpdatedParent.Status);
        Assert.Equal(InvoiceWorkflowStatus.PartiallyPaid, storedUpdatedParent.InvoiceWorkflowStatus);
        Assert.Equal(paidInstallment.Amount, storedUpdatedParent.PaidAmount);
        Assert.Null(storedUpdatedParent.InvoiceCancelledAt);
        Assert.Contains("Оплаченные платежи сохранены", storedUpdatedParent.SystemNotes, StringComparison.Ordinal);

        var installments = await LoadInstallmentPaymentsAsync(db, storedUpdatedParent.InvoiceId!.Value, storedUpdatedParent.Id);
        Assert.Equal(2, installments.Count);
        Assert.Equal(PaymentStatus.Completed, installments[0].Status);
        Assert.True(installments[0].IsPaid);
        Assert.Equal(installments[0].Amount, installments[0].PaidAmount);
        Assert.Equal(PaymentStatus.Cancelled, installments[1].Status);
        Assert.False(installments[1].IsPaid);
        Assert.Equal(0m, installments[1].PaidAmount);
        Assert.Contains("Неоплаченный платёж рассрочки отменён", installments[1].SystemNotes, StringComparison.Ordinal);

        Assert.Empty(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());
        Assert.Empty(await db.MoneyOperations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Cancel_PaidInvoiceWithReallocateAction_AppliesCreditToTargetInvoice()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client Reallocate");
        var controller = CreateController(db, isAdmin: true, userId: actorUserId);

        var sourceRequest = CreateRequest("INV-CANCEL-REALLOC-SRC", clientId, new DateTime(2026, 4, 20), 12_000m);
        sourceRequest.Status = PaymentStatus.Completed;
        sourceRequest.PaidDate = new DateTime(2026, 4, 21);
        var sourceAction = await controller.Create(sourceRequest, CancellationToken.None);
        var sourceCreated = Assert.IsType<CreatedResult>(sourceAction.Result);
        var sourceDto = Assert.IsType<InvoiceDto>(sourceCreated.Value);

        var targetAction = await controller.Create(
            CreateRequest("INV-CANCEL-REALLOC-DST", clientId, new DateTime(2026, 4, 22), 12_000m),
            CancellationToken.None);
        var targetCreated = Assert.IsType<CreatedResult>(targetAction.Result);
        var targetDto = Assert.IsType<InvoiceDto>(targetCreated.Value);

        var cancelAction = await controller.Cancel(
            sourceDto.Id,
            new CancelInvoiceRequest
            {
                Reason = "Перевыставили счёт",
                ReceivedMoneyAction = InvoiceCancellationMoneyAction.Reallocate,
                TargetPaymentId = targetDto.Id,
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(cancelAction.Result);
        var dto = Assert.IsType<InvoiceDto>(ok.Value);
        Assert.Equal(PaymentStatus.Cancelled, dto.Status);

        db.ChangeTracker.Clear();
        var storedSource = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == sourceDto.Id);
        var storedTarget = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == targetDto.Id);
        Assert.Equal(PaymentStatus.Cancelled, storedSource.Status);
        Assert.Equal(0m, storedSource.PaidAmount);
        Assert.Equal(PaymentStatus.Completed, storedTarget.Status);
        Assert.Equal(12_000m, storedTarget.PaidAmount);
        Assert.Contains($"счёт #{targetDto.Id}", storedSource.SystemNotes, StringComparison.OrdinalIgnoreCase);

        var balanceTransactions = await db.ClientBalanceTransactions
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Collection(
            balanceTransactions,
            credit =>
            {
                Assert.Equal(ClientBalanceTransactionType.AdvanceFromCancelledInvoice, credit.Type);
                Assert.Equal(ClientBalanceDirection.Credit, credit.Direction);
                Assert.Equal(12_000m, credit.Amount);
                Assert.Equal(sourceDto.Id, credit.PaymentId);
            },
            debit =>
            {
                Assert.Equal(ClientBalanceTransactionType.CreditApplied, debit.Type);
                Assert.Equal(ClientBalanceDirection.Debit, debit.Direction);
                Assert.Equal(12_000m, debit.Amount);
                Assert.Equal(targetDto.Id, debit.PaymentId);
            });

        var balance = await ClientBalanceService.GetBalanceAsync(db, clientId);
        Assert.Equal(0m, balance);
    }

    [Fact]
    public async Task Cancel_PaidLegalEntityInvoiceWithReallocateAction_AppliesCreditToLegalEntityTarget()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var legalEntityId = await CreateLegalEntityAsync(db, "Legal Entity Reallocate");
        var controller = CreateController(db, isAdmin: true, userId: actorUserId);

        var sourceRequest = CreateLegalEntityRequest("INV-CANCEL-LEGAL-REALLOC-SRC", legalEntityId, new DateTime(2026, 4, 20), 12_000m);
        sourceRequest.Status = PaymentStatus.Completed;
        sourceRequest.PaidDate = new DateTime(2026, 4, 21);
        var sourceAction = await controller.Create(sourceRequest, CancellationToken.None);
        var sourceCreated = Assert.IsType<CreatedResult>(sourceAction.Result);
        var sourceDto = Assert.IsType<InvoiceDto>(sourceCreated.Value);

        var targetAction = await controller.Create(
            CreateLegalEntityRequest("INV-CANCEL-LEGAL-REALLOC-DST", legalEntityId, new DateTime(2026, 4, 22), 12_000m),
            CancellationToken.None);
        var targetCreated = Assert.IsType<CreatedResult>(targetAction.Result);
        var targetDto = Assert.IsType<InvoiceDto>(targetCreated.Value);

        var cancelAction = await controller.Cancel(
            sourceDto.Id,
            new CancelInvoiceRequest
            {
                Reason = "Legal entity reallocate",
                ReceivedMoneyAction = InvoiceCancellationMoneyAction.Reallocate,
                TargetPaymentId = targetDto.Id,
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(cancelAction.Result);
        var dto = Assert.IsType<InvoiceDto>(ok.Value);
        Assert.Equal(PaymentStatus.Cancelled, dto.Status);

        db.ChangeTracker.Clear();
        var storedSource = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == sourceDto.Id);
        var storedTarget = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == targetDto.Id);
        Assert.Null(storedSource.ClientId);
        Assert.Equal(legalEntityId, storedSource.LegalEntityId);
        Assert.Null(storedTarget.ClientId);
        Assert.Equal(legalEntityId, storedTarget.LegalEntityId);
        Assert.Equal(PaymentStatus.Cancelled, storedSource.Status);
        Assert.Equal(0m, storedSource.PaidAmount);
        Assert.Equal(PaymentStatus.Completed, storedTarget.Status);
        Assert.Equal(12_000m, storedTarget.PaidAmount);

        var balanceTransactions = await db.ClientBalanceTransactions
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Collection(
            balanceTransactions,
            credit =>
            {
                Assert.Null(credit.ClientId);
                Assert.Equal(legalEntityId, credit.LegalEntityId);
                Assert.Equal(ClientBalanceTransactionType.AdvanceFromCancelledInvoice, credit.Type);
                Assert.Equal(ClientBalanceDirection.Credit, credit.Direction);
                Assert.Equal(12_000m, credit.Amount);
                Assert.Equal(sourceDto.Id, credit.PaymentId);
            },
            debit =>
            {
                Assert.Null(debit.ClientId);
                Assert.Equal(legalEntityId, debit.LegalEntityId);
                Assert.Equal(ClientBalanceTransactionType.CreditApplied, debit.Type);
                Assert.Equal(ClientBalanceDirection.Debit, debit.Direction);
                Assert.Equal(12_000m, debit.Amount);
                Assert.Equal(targetDto.Id, debit.PaymentId);
            });

        var balance = await ClientBalanceService.GetLegalEntityBalanceAsync(db, legalEntityId);
        Assert.Equal(0m, balance);
    }

    [Fact]
    public async Task Cancel_PaidCashInvoiceWithRefundAction_CreatesExpenseOperationAndCashOrder()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client Refund");
        var cashSourceId = await CreatePaymentSourceAsync(db, "Касса");
        var controller = CreateController(db, isAdmin: true, userId: actorUserId);

        var request = CreateRequest("INV-CANCEL-REFUND", clientId, new DateTime(2026, 4, 20), 12_000m);
        request.Status = PaymentStatus.Completed;
        request.PaidDate = new DateTime(2026, 4, 21);
        request.PaymentSourceId = cashSourceId;

        var createAction = await controller.Create(request, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        var cancelAction = await controller.Cancel(
            createdDto.Id,
            new CancelInvoiceRequest
            {
                Reason = "Возврат клиенту",
                ReceivedMoneyAction = InvoiceCancellationMoneyAction.Refund,
                RefundDate = new DateTime(2026, 4, 25),
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(cancelAction.Result);
        var dto = Assert.IsType<InvoiceDto>(ok.Value);
        Assert.Equal(PaymentStatus.Cancelled, dto.Status);

        db.ChangeTracker.Clear();
        var storedPayment = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);
        Assert.Equal(PaymentStatus.Cancelled, storedPayment.Status);
        Assert.Equal(0m, storedPayment.PaidAmount);
        Assert.Contains("возвращена клиенту", storedPayment.SystemNotes, StringComparison.OrdinalIgnoreCase);

        var operation = await db.MoneyOperations.AsNoTracking().SingleAsync();
        Assert.Equal(MoneyOperationSource.Cash, operation.Source);
        Assert.Equal(MoneyOperationDirection.Expense, operation.Direction);
        Assert.Equal(MoneyOperationAccountingType.Fact, operation.AccountingType);
        Assert.Equal(MoneyOperationMatchStatus.Matched, operation.MatchStatus);
        Assert.Equal(12_000m, operation.Amount);
        Assert.Equal(new DateTime(2026, 4, 25), operation.Date);
        Assert.Equal(storedPayment.InvoiceId, operation.LinkedInvoiceId);
        Assert.Equal("Возврат клиенту", operation.CategoryName);
        Assert.Contains("Возврат клиенту", operation.Purpose, StringComparison.Ordinal);

        var cashOrder = await db.CashOrders.AsNoTracking().SingleAsync();
        Assert.Equal(CashOrderType.Outgoing, cashOrder.Type);
        Assert.Equal(CashOrderStatus.Posted, cashOrder.Status);
        Assert.Equal(12_000m, cashOrder.Amount);
        Assert.Equal(operation.Id, cashOrder.MoneyOperationId);
        Assert.Equal(storedPayment.InvoiceId, cashOrder.InvoiceId);
        Assert.Equal(clientId, cashOrder.ClientId);
        Assert.StartsWith("РКО-2026-", cashOrder.Number, StringComparison.Ordinal);

        var balance = await ClientBalanceService.GetBalanceAsync(db, clientId);
        Assert.Equal(0m, balance);
    }

    [Fact]
    public async Task Create_ExpenseInvoice_UsesExpenseClientAsCaseOwner()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var supplierId = await CreateClientAsync(db, "Supplier");
        var expenseClientId = await CreateClientAsync(db, "Expense Client");
        var expenseCaseId = await CreateClientCaseAsync(db, expenseClientId, "Expense Case");

        var controller = CreateController(db, isAdmin: true, userId: actorUserId);
        var request = CreateRequest("INV-EXP-1", supplierId, new DateTime(2026, 4, 18), 7_500m);
        request.Type = PaymentType.Expense;
        request.ExpenseClientId = expenseClientId;
        request.ClientCaseIds = [expenseCaseId];

        var action = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<InvoiceDto>(created.Value);
        Assert.Equal(PaymentType.Expense, dto.Type);
        Assert.Equal(supplierId, dto.ClientId);
        Assert.Equal(expenseClientId, dto.ExpenseClientId);
        Assert.Equal(new[] { expenseCaseId }, dto.ClientCaseIds);

        db.ChangeTracker.Clear();

        var storedParent = await db.Payments
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == dto.Id);
        Assert.Equal(supplierId, storedParent.ClientId);
        Assert.Equal(expenseClientId, storedParent.ExpenseClientId);
        Assert.Equal(expenseCaseId, storedParent.ClientCaseId);

        var storedCaseIds = await db.Set<InvoiceClientCase>()
            .AsNoTracking()
            .Where(item => item.PaymentId == dto.Id)
            .Select(item => item.ClientCaseId)
            .OrderBy(id => id)
            .ToListAsync();
        Assert.Equal(new[] { expenseCaseId }, storedCaseIds);
    }

    [Fact]
    public async Task Update_ReplacesInvoiceGraph_AndResyncsInstallmentsAndRelations()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client Beta");
        var contractId1 = await CreateContractAsync(db, clientId, "CTR-600-A");
        var contractId2 = await CreateContractAsync(db, clientId, "CTR-600-B");
        var caseId1 = await CreateClientCaseAsync(db, clientId, "Case One");
        var caseId2 = await CreateClientCaseAsync(db, clientId, "Case Two");
        var actId1 = await CreateActAsync(db, clientId, "ACT-600-A");
        var actId2 = await CreateActAsync(db, clientId, "ACT-600-B");
        var responsibleId1 = await CreateUserAsync(db, "Responsible A");
        var responsibleId2 = await CreateUserAsync(db, "Responsible B");
        var serviceId1 = await CreateServiceAsync(db, "Service A");
        var serviceId2 = await CreateServiceAsync(db, "Service B");
        var paymentSourceId1 = await CreatePaymentSourceAsync(db, "Cash desk");
        var paymentSourceId2 = await CreatePaymentSourceAsync(db, "Wire");
        var incomeTypeId1 = await CreateIncomeTypeAsync(db, "Income A");
        var incomeTypeId2 = await CreateIncomeTypeAsync(db, "Income B");
        var dealTypeId1 = await CreateDealTypeAsync(db, "Deal A");
        var dealTypeId2 = await CreateDealTypeAsync(db, "Deal B");
        var paymentStatusId1 = await CreatePaymentStatusEntityAsync(db, "Status A");
        var paymentStatusId2 = await CreatePaymentStatusEntityAsync(db, "Status B");

        var controller = CreateController(db, isAdmin: true, userId: actorUserId);
        var createRequest = CreateRequest("INV-600", clientId, new DateTime(2026, 4, 10), 12_000m);
        createRequest.Status = PaymentStatus.Completed;
        createRequest.PaidDate = new DateTime(2026, 4, 11);
        createRequest.ContractId = contractId1;
        createRequest.ClientCaseIds = [caseId1];
        createRequest.ActIds = [actId1];
        createRequest.ClientCaseReference = " old-case-ref ";
        createRequest.ActReference = " old-act-ref ";
        createRequest.Description = " old description ";
        createRequest.IsNotForAccounting = true;
        createRequest.PaymentSourceId = paymentSourceId1;
        createRequest.ServiceId = serviceId1;
        createRequest.IncomeTypeId = incomeTypeId1;
        createRequest.DealTypeId = dealTypeId1;
        createRequest.PaymentStatusEntityId = paymentStatusId1;
        createRequest.ResponsibleIds = [responsibleId1, responsibleId2];
        createRequest.LineItems =
        [
            new InvoiceLineItemRequest
            {
                ServiceId = serviceId1,
                Name = "Initial service",
                Quantity = 1m,
                Unit = "service",
                Price = 12_000m,
            },
        ];
        createRequest.InstallmentPlan = CreateInstallmentPlanRequest(
            total: 12_000m,
            startDate: new DateTime(2026, 4, 10),
            new DateTime(2026, 5, 10),
            new DateTime(2026, 6, 10));

        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        db.ChangeTracker.Clear();

        var originalParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);
        var originalInstallmentIds = (await LoadInstallmentPaymentsAsync(db, originalParent.InvoiceId!.Value, createdDto.Id))
            .Select(payment => payment.Id)
            .OrderBy(id => id)
            .ToArray();

        var updateRequest = CreateRequest("INV-601", clientId, new DateTime(2026, 4, 20), 15_000m);
        updateRequest.Status = PaymentStatus.Pending;
        updateRequest.PaidDate = null;
        updateRequest.ContractId = contractId2;
        updateRequest.ClientCaseIds = [caseId2];
        updateRequest.ActIds = [actId2];
        updateRequest.ClientCaseReference = " new-case-ref ";
        updateRequest.ActReference = " new-act-ref ";
        updateRequest.Description = " new description ";
        updateRequest.IsNotForAccounting = false;
        updateRequest.PaymentSourceId = paymentSourceId2;
        updateRequest.ServiceId = serviceId2;
        updateRequest.IncomeTypeId = incomeTypeId2;
        updateRequest.DealTypeId = dealTypeId2;
        updateRequest.PaymentStatusEntityId = paymentStatusId2;
        updateRequest.ResponsibleIds = [responsibleId2];
        updateRequest.LineItems =
        [
            new InvoiceLineItemRequest
            {
                ServiceId = serviceId2,
                Name = "Updated service",
                Quantity = 3m,
                Unit = "hour",
                Price = 5_000m,
            },
        ];
        updateRequest.InstallmentPlan = CreateInstallmentPlanRequest(
            total: 15_000m,
            startDate: new DateTime(2026, 4, 20),
            new DateTime(2026, 5, 15),
            new DateTime(2026, 6, 15),
            new DateTime(2026, 7, 15));

        var updateAction = await controller.Update(createdDto.Id, updateRequest, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(updateAction.Result);
        var updatedDto = Assert.IsType<InvoiceDto>(ok.Value);

        Assert.Equal("INV-601", updatedDto.Number);
        Assert.Equal(new DateTime(2026, 4, 20), updatedDto.Date);
        Assert.Equal(new DateTime(2026, 7, 15), updatedDto.DueDate);
        Assert.Equal(15_000m, updatedDto.Amount);
        Assert.Equal(PaymentStatus.Pending, updatedDto.Status);
        Assert.False(updatedDto.IsPaid);
        Assert.Null(updatedDto.PaidDate);
        Assert.Equal(new[] { caseId2 }, updatedDto.ClientCaseIds);
        Assert.Equal(new[] { actId2 }, updatedDto.ActIds);
        Assert.Equal(new[] { responsibleId2 }, updatedDto.Responsibles.Select(item => item.Id).ToArray());
        Assert.Single(updatedDto.LineItems);
        Assert.Equal("Updated service", updatedDto.LineItems[0].Name);

        db.ChangeTracker.Clear();

        var updatedParent = await db.Payments
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == createdDto.Id);

        Assert.Equal("INV-601", updatedParent.Account);
        Assert.Equal(new DateTime(2026, 4, 20), updatedParent.AccountDate);
        Assert.Equal(new DateTime(2026, 7, 15), updatedParent.Date);
        Assert.Equal(15_000m, updatedParent.Amount);
        Assert.Equal(PaymentStatus.Pending, updatedParent.Status);
        Assert.False(updatedParent.IsPaid);
        Assert.Equal(0m, updatedParent.PaidAmount);
        Assert.Null(updatedParent.PaidDate);
        Assert.Equal(contractId2, updatedParent.ContractId);
        Assert.Equal(caseId2, updatedParent.ClientCaseId);
        Assert.Equal("new-case-ref", updatedParent.ClientCaseReference);
        Assert.Equal("new-act-ref", updatedParent.Notes);
        Assert.Equal("new description", updatedParent.Description);
        Assert.False(updatedParent.IsNotForAccounting);
        Assert.Equal(paymentSourceId2, updatedParent.PaymentSourceId);
        Assert.Equal(serviceId2, updatedParent.ServiceId);
        Assert.Equal(incomeTypeId2, updatedParent.IncomeTypeId);
        Assert.Equal(dealTypeId2, updatedParent.DealTypeId);
        Assert.Equal(paymentStatusId2, updatedParent.PaymentStatusId);

        var updatedResponsibleIds = await db.Set<PaymentResponsible>()
            .AsNoTracking()
            .Where(item => item.PaymentId == createdDto.Id)
            .Select(item => item.UserId)
            .OrderBy(id => id)
            .ToListAsync();
        Assert.Equal(new[] { responsibleId2 }, updatedResponsibleIds);

        var updatedCaseIds = await db.Set<InvoiceClientCase>()
            .AsNoTracking()
            .Where(item => item.PaymentId == createdDto.Id)
            .Select(item => item.ClientCaseId)
            .OrderBy(id => id)
            .ToListAsync();
        Assert.Equal(new[] { caseId2 }, updatedCaseIds);

        var updatedLineItems = await db.InvoiceLineItems
            .AsNoTracking()
            .Where(item => item.PaymentId == createdDto.Id)
            .OrderBy(item => item.SortOrder)
            .ToListAsync();
        Assert.Single(updatedLineItems);
        Assert.Equal(serviceId2, updatedLineItems[0].ServiceId);
        Assert.Equal("Updated service", updatedLineItems[0].Name);
        Assert.Equal(3m, updatedLineItems[0].Quantity);
        Assert.Equal("hour", updatedLineItems[0].Unit);
        Assert.Equal(5_000m, updatedLineItems[0].Price);

        var detachedAct = await db.Acts.AsNoTracking().SingleAsync(act => act.Id == actId1);
        Assert.Null(detachedAct.InvoiceId);
        Assert.Null(detachedAct.InvoiceNumber);

        var attachedAct = await db.Acts.AsNoTracking().SingleAsync(act => act.Id == actId2);
        Assert.Equal(createdDto.Id, attachedAct.InvoiceId);
        Assert.Equal("INV-601", attachedAct.InvoiceNumber);

        var updatedInstallments = await LoadInstallmentPaymentsAsync(db, updatedParent.InvoiceId!.Value, createdDto.Id);
        Assert.Equal(3, updatedInstallments.Count);
        Assert.Equal(new[] { new DateTime(2026, 5, 15), new DateTime(2026, 6, 15), new DateTime(2026, 7, 15) }, updatedInstallments.Select(item => item.Date).ToArray());
        Assert.Contains(originalInstallmentIds[0], updatedInstallments.Select(item => item.Id));
        Assert.Contains(originalInstallmentIds[1], updatedInstallments.Select(item => item.Id));
        Assert.Single(updatedInstallments.Select(item => item.Id).Except(originalInstallmentIds));
        Assert.All(updatedInstallments, installment =>
        {
            Assert.Equal("INV-601", installment.Account);
            Assert.Equal(new DateTime(2026, 4, 20), installment.AccountDate);
            Assert.Equal(clientId, installment.ClientId);
            Assert.Equal(contractId2, installment.ContractId);
            Assert.Equal(caseId2, installment.ClientCaseId);
            Assert.Equal(paymentSourceId2, installment.PaymentSourceId);
            Assert.Equal(serviceId2, installment.ServiceId);
            Assert.Equal(incomeTypeId2, installment.IncomeTypeId);
            Assert.Equal(dealTypeId2, installment.DealTypeId);
            Assert.Equal(paymentStatusId2, installment.PaymentStatusId);
        });

        var updatedInstallmentResponsibleIds = await db.Set<PaymentResponsible>()
            .AsNoTracking()
            .Where(item => updatedInstallments.Select(payment => payment.Id).Contains(item.PaymentId))
            .Select(item => item.UserId)
            .Distinct()
            .ToListAsync();
        Assert.Equal(new[] { responsibleId2 }, updatedInstallmentResponsibleIds);
    }

    [Fact]
    public async Task Update_ChangesInvoiceClient_PropagatesToGeneratedInstallments()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var originalClientId = await CreateClientAsync(db, "Original Client");
        var newClientId = await CreateClientAsync(db, "New Client");
        var controller = CreateController(db, isAdmin: true, userId: actorUserId);

        var createRequest = CreateRequest("INV-CL-CHANGE-1", originalClientId, new DateTime(2026, 4, 10), 9_000m);
        createRequest.InstallmentPlan = CreateInstallmentPlanRequest(
            total: 9_000m,
            startDate: new DateTime(2026, 4, 10),
            new DateTime(2026, 5, 10),
            new DateTime(2026, 6, 10));

        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        db.ChangeTracker.Clear();
        var createdParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);
        var createdInstallments = await LoadInstallmentPaymentsAsync(db, createdParent.InvoiceId!.Value, createdParent.Id);
        Assert.Equal(2, createdInstallments.Count);
        Assert.All(createdInstallments, installment => Assert.Equal(originalClientId, installment.ClientId));

        var updateRequest = CreateRequest("INV-CL-CHANGE-1", newClientId, new DateTime(2026, 4, 10), 9_000m);

        var updateAction = await controller.Update(createdDto.Id, updateRequest, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(updateAction.Result);
        _ = Assert.IsType<InvoiceDto>(ok.Value);

        db.ChangeTracker.Clear();
        var updatedParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);
        Assert.Equal(newClientId, updatedParent.ClientId);
        var updatedInstallments = await LoadInstallmentPaymentsAsync(db, updatedParent.InvoiceId!.Value, updatedParent.Id);
        Assert.Equal(2, updatedInstallments.Count);
        Assert.All(updatedInstallments, installment =>
        {
            Assert.Equal(newClientId, installment.ClientId);
            Assert.Equal("INV-CL-CHANGE-1", installment.Account);
            Assert.Equal(new DateTime(2026, 4, 10), installment.AccountDate);
        });
    }

    [Fact]
    public async Task Update_ToLegalEntity_PropagatesToRegularLinkedPayments()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var originalClientId = await CreateClientAsync(db, "Original Client");
        var legalEntityId = await CreateLegalEntityAsync(db, "Lada Lux");
        var responsibleId1 = await CreateUserAsync(db, "Original Responsible");
        var responsibleId2 = await CreateUserAsync(db, "Updated Responsible");
        var paymentSourceId = await CreatePaymentSourceAsync(db, "Bank");
        var serviceId = await CreateServiceAsync(db, "Legal Service");
        var controller = CreateController(db, isAdmin: true, userId: actorUserId);

        var createRequest = CreateRequest("INV-LINKED-OWNER", originalClientId, new DateTime(2026, 4, 10), 50_000m);
        createRequest.ResponsibleIds = [responsibleId1];

        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        var createdParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);
        var firstLinkedPayment = new Payment
        {
            InvoiceId = createdParent.InvoiceId,
            Account = createdParent.Account,
            AccountDate = createdParent.AccountDate,
            Date = new DateTime(2026, 5, 10),
            PlannedDate = new DateTime(2026, 5, 10),
            Amount = 35_000m,
            PaidAmount = 0m,
            Status = PaymentStatus.Overdue,
            Type = PaymentType.Income,
            ClientId = originalClientId,
            Description = "First linked payment",
            DocumentKind = DocumentKind.Payment,
            CreatedAt = DateTime.UtcNow,
        };
        firstLinkedPayment.Responsibles.Add(new PaymentResponsible { UserId = responsibleId1 });

        var secondLinkedPayment = new Payment
        {
            InvoiceId = createdParent.InvoiceId,
            Account = createdParent.Account,
            AccountDate = createdParent.AccountDate,
            Date = new DateTime(2026, 6, 10),
            PlannedDate = new DateTime(2026, 6, 10),
            Amount = 50_000m,
            PaidAmount = 50_000m,
            PaidDate = new DateTime(2026, 5, 15),
            LastPaymentDate = new DateTime(2026, 5, 15),
            IsPaid = true,
            Status = PaymentStatus.Completed,
            Type = PaymentType.Income,
            ClientId = originalClientId,
            Description = "Second linked payment",
            DocumentKind = DocumentKind.Payment,
            CreatedAt = DateTime.UtcNow,
        };
        secondLinkedPayment.Responsibles.Add(new PaymentResponsible { UserId = responsibleId1 });

        db.Payments.AddRange(firstLinkedPayment, secondLinkedPayment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var updateRequest = CreateLegalEntityRequest("INV-LINKED-OWNER", legalEntityId, new DateTime(2026, 4, 10), 135_000m);
        updateRequest.ResponsibleIds = [responsibleId2];
        updateRequest.PaymentSourceId = paymentSourceId;
        updateRequest.ServiceId = serviceId;

        var updateAction = await controller.Update(createdDto.Id, updateRequest, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(updateAction.Result);
        _ = Assert.IsType<InvoiceDto>(ok.Value);

        db.ChangeTracker.Clear();
        var updatedParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);
        Assert.Null(updatedParent.ClientId);
        Assert.Equal(legalEntityId, updatedParent.LegalEntityId);
        Assert.Equal(135_000m, updatedParent.Amount);

        var linkedPayments = await db.Payments
            .AsNoTracking()
            .Where(payment => payment.InvoiceId == updatedParent.InvoiceId && payment.DocumentKind == DocumentKind.Payment)
            .ToListAsync();
        linkedPayments = linkedPayments.OrderBy(payment => payment.Amount).ToList();
        Assert.Equal(2, linkedPayments.Count);
        Assert.Equal(new[] { 35_000m, 50_000m }, linkedPayments.Select(payment => payment.Amount).ToArray());
        Assert.Equal(0m, linkedPayments[0].PaidAmount);
        Assert.Equal(50_000m, linkedPayments[1].PaidAmount);
        Assert.True(linkedPayments[1].IsPaid);
        Assert.Equal(PaymentStatus.Completed, linkedPayments[1].Status);
        Assert.Equal(new DateTime(2026, 5, 15), linkedPayments[1].PaidDate);
        Assert.Equal(new DateTime(2026, 5, 15), linkedPayments[1].LastPaymentDate);
        Assert.All(linkedPayments, payment =>
        {
            Assert.Null(payment.ClientId);
            Assert.Equal(legalEntityId, payment.LegalEntityId);
            Assert.Equal("INV-LINKED-OWNER", payment.Account);
            Assert.Equal(new DateTime(2026, 4, 10), payment.AccountDate);
            Assert.Equal(paymentSourceId, payment.PaymentSourceId);
            Assert.Equal(serviceId, payment.ServiceId);
        });

        var linkedResponsibleIds = await db.Set<PaymentResponsible>()
            .AsNoTracking()
            .Where(item => linkedPayments.Select(payment => payment.Id).Contains(item.PaymentId))
            .Select(item => item.UserId)
            .Distinct()
            .ToListAsync();
        Assert.Equal(new[] { responsibleId2 }, linkedResponsibleIds);
    }

    [Fact]
    public async Task Update_ForksProtectedRepresentativePayment_WhenInvoiceWouldOverwriteLegacyAdvance()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client Legacy");
        var contractId1 = await CreateContractAsync(db, clientId, "CTR-LEGACY-A");
        var contractId2 = await CreateContractAsync(db, clientId, "CTR-LEGACY-B");
        var caseId1 = await CreateClientCaseAsync(db, clientId, "Legacy Case");
        var caseId2 = await CreateClientCaseAsync(db, clientId, "Updated Case");
        var actId1 = await CreateActAsync(db, clientId, "ACT-LEGACY-A");
        var actId2 = await CreateActAsync(db, clientId, "ACT-LEGACY-B");
        var responsibleId1 = await CreateUserAsync(db, "Legacy Responsible");
        var responsibleId2 = await CreateUserAsync(db, "Updated Responsible");
        var serviceId1 = await CreateServiceAsync(db, "Legacy Service");
        var serviceId2 = await CreateServiceAsync(db, "Updated Service");
        var paymentSourceId = await CreatePaymentSourceAsync(db, "Bank");

        var invoice = new Invoice
        {
            Number = "INV-LEG-1",
            Date = new DateTime(2026, 4, 10),
            CreatedAt = DateTime.UtcNow,
        };

        var legacyRepresentative = new Payment
        {
            Invoice = invoice,
            Account = "INV-LEG-1",
            AccountDate = new DateTime(2026, 4, 10),
            Date = new DateTime(2026, 4, 10),
            PlannedDate = new DateTime(2026, 4, 15),
            Amount = 5_000m,
            PaidAmount = 5_000m,
            PaidDate = new DateTime(2026, 4, 10),
            LastPaymentDate = new DateTime(2026, 4, 10),
            IsPaid = true,
            Status = PaymentStatus.Completed,
            Type = PaymentType.Income,
            ClientId = clientId,
            ContractId = contractId1,
            ClientCaseId = caseId1,
            Description = "Legacy advance",
            DocumentKind = DocumentKind.Invoice,
            PaymentSourceId = paymentSourceId,
            ServiceId = serviceId1,
            CreatedAt = DateTime.UtcNow,
        };
        legacyRepresentative.Responsibles.Add(new PaymentResponsible { UserId = responsibleId1 });
        legacyRepresentative.InvoiceClientCases.Add(new InvoiceClientCase { ClientCaseId = caseId1 });
        legacyRepresentative.InvoiceLineItems.Add(new InvoiceLineItem
        {
            ServiceId = serviceId1,
            SortOrder = 0,
            Name = "Legacy advance line",
            Quantity = 1m,
            Unit = "service",
            Price = 5_000m,
            CreatedAt = DateTime.UtcNow,
        });

        db.Payments.Add(legacyRepresentative);
        await db.SaveChangesAsync();

        db.Payments.Add(new Payment
        {
            InvoiceId = legacyRepresentative.InvoiceId,
            Account = legacyRepresentative.Account,
            AccountDate = legacyRepresentative.AccountDate,
            Date = new DateTime(2026, 5, 15),
            PlannedDate = new DateTime(2026, 5, 15),
            Amount = 2_000m,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            ClientId = clientId,
            ContractId = contractId1,
            ClientCaseId = caseId1,
            Description = "Linked legacy payment",
            DocumentKind = DocumentKind.Payment,
            CreatedAt = DateTime.UtcNow,
        });

        var legacyAct = await db.Acts.SingleAsync(act => act.Id == actId1);
        legacyAct.InvoiceId = legacyRepresentative.Id;
        legacyAct.InvoiceNumber = legacyRepresentative.Account;

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = CreateController(db, isAdmin: true, userId: actorUserId);
        var updateRequest = CreateRequest("INV-LEG-1", clientId, new DateTime(2026, 4, 10), 25_000m);
        updateRequest.ContractId = contractId2;
        updateRequest.ClientCaseIds = [caseId2];
        updateRequest.ActIds = [actId2];
        updateRequest.ResponsibleIds = [responsibleId2];
        updateRequest.Description = "Updated invoice total";
        updateRequest.PaymentSourceId = paymentSourceId;
        updateRequest.ServiceId = serviceId2;
        updateRequest.LineItems =
        [
            new InvoiceLineItemRequest
            {
                ServiceId = serviceId2,
                Name = "Updated invoice line",
                Quantity = 1m,
                Unit = "service",
                Price = 25_000m,
            },
        ];

        var updateAction = await controller.Update(legacyRepresentative.Id, updateRequest, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(updateAction.Result);
        var updatedDto = Assert.IsType<InvoiceDto>(ok.Value);
        Assert.NotEqual(legacyRepresentative.Id, updatedDto.Id);

        db.ChangeTracker.Clear();

        var preservedAdvance = await db.Payments
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == legacyRepresentative.Id);
        Assert.Equal(DocumentKind.Payment, preservedAdvance.DocumentKind);
        Assert.Equal(5_000m, preservedAdvance.Amount);
        Assert.Equal(5_000m, preservedAdvance.PaidAmount);
        Assert.True(preservedAdvance.IsPaid);
        Assert.Equal(PaymentStatus.Completed, preservedAdvance.Status);
        Assert.Equal(new DateTime(2026, 4, 10), preservedAdvance.PaidDate);
        Assert.Equal(contractId1, preservedAdvance.ContractId);
        Assert.Equal(caseId1, preservedAdvance.ClientCaseId);
        Assert.Equal("Legacy advance", preservedAdvance.Description);

        var updatedRepresentative = await db.Payments
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == updatedDto.Id);
        Assert.Equal(DocumentKind.Invoice, updatedRepresentative.DocumentKind);
        Assert.Equal(25_000m, updatedRepresentative.Amount);
        Assert.Equal(contractId2, updatedRepresentative.ContractId);
        Assert.Equal(caseId2, updatedRepresentative.ClientCaseId);
        Assert.Equal(serviceId2, updatedRepresentative.ServiceId);
        Assert.Equal(paymentSourceId, updatedRepresentative.PaymentSourceId);
        Assert.Equal("Updated invoice total", updatedRepresentative.Description);

        var aggregatePayments = await db.Payments
            .AsNoTracking()
            .Where(payment => payment.InvoiceId == updatedRepresentative.InvoiceId)
            .OrderBy(payment => payment.Id)
            .ToListAsync();
        Assert.Equal(3, aggregatePayments.Count);
        Assert.Single(aggregatePayments, payment => payment.DocumentKind == DocumentKind.Invoice);
        Assert.Contains(aggregatePayments, payment => payment.Id == legacyRepresentative.Id && payment.DocumentKind == DocumentKind.Payment);

        var oldResponsibleIds = await db.Set<PaymentResponsible>()
            .AsNoTracking()
            .Where(item => item.PaymentId == legacyRepresentative.Id)
            .Select(item => item.UserId)
            .OrderBy(id => id)
            .ToListAsync();
        Assert.Equal(new[] { responsibleId1 }, oldResponsibleIds);

        var newResponsibleIds = await db.Set<PaymentResponsible>()
            .AsNoTracking()
            .Where(item => item.PaymentId == updatedDto.Id)
            .Select(item => item.UserId)
            .OrderBy(id => id)
            .ToListAsync();
        Assert.Equal(new[] { responsibleId2 }, newResponsibleIds);

        var oldCaseLinks = await db.Set<InvoiceClientCase>()
            .AsNoTracking()
            .Where(item => item.PaymentId == legacyRepresentative.Id)
            .Select(item => item.ClientCaseId)
            .ToListAsync();
        Assert.Empty(oldCaseLinks);

        var newCaseLinks = await db.Set<InvoiceClientCase>()
            .AsNoTracking()
            .Where(item => item.PaymentId == updatedDto.Id)
            .Select(item => item.ClientCaseId)
            .OrderBy(id => id)
            .ToListAsync();
        Assert.Equal(new[] { caseId2 }, newCaseLinks);

        var oldLineItems = await db.InvoiceLineItems
            .AsNoTracking()
            .Where(item => item.PaymentId == legacyRepresentative.Id)
            .ToListAsync();
        Assert.Empty(oldLineItems);

        var newLineItems = await db.InvoiceLineItems
            .AsNoTracking()
            .Where(item => item.PaymentId == updatedDto.Id)
            .OrderBy(item => item.SortOrder)
            .ToListAsync();
        Assert.Single(newLineItems);
        Assert.Equal(serviceId2, newLineItems[0].ServiceId);
        Assert.Equal("Updated invoice line", newLineItems[0].Name);

        var detachedLegacyAct = await db.Acts.AsNoTracking().SingleAsync(act => act.Id == actId1);
        Assert.Null(detachedLegacyAct.InvoiceId);
        Assert.Null(detachedLegacyAct.InvoiceNumber);

        var attachedUpdatedAct = await db.Acts.AsNoTracking().SingleAsync(act => act.Id == actId2);
        Assert.Equal(updatedDto.Id, attachedUpdatedAct.InvoiceId);
        Assert.Equal("INV-LEG-1", attachedUpdatedAct.InvoiceNumber);
    }

    [Fact]
    public async Task Update_ClearsClientCaseLinks_WhenCasesRemoved()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client Eta");
        var caseId = await CreateClientCaseAsync(db, clientId, "Case To Remove");

        var controller = CreateController(db, isAdmin: true, userId: actorUserId);
        var createRequest = CreateRequest("INV-CLR-1", clientId, new DateTime(2026, 4, 10), 9_000m);
        createRequest.ClientCaseIds = [caseId];

        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        var updateRequest = CreateRequest("INV-CLR-2", clientId, new DateTime(2026, 4, 11), 9_000m);
        updateRequest.ClientCaseId = null;
        updateRequest.ClientCaseIds = [];

        var updateAction = await controller.Update(createdDto.Id, updateRequest, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(updateAction.Result);
        var updatedDto = Assert.IsType<InvoiceDto>(ok.Value);
        Assert.Empty(updatedDto.ClientCaseIds);
        Assert.Null(updatedDto.ClientCaseId);

        db.ChangeTracker.Clear();

        var updatedParent = await db.Payments
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == createdDto.Id);
        Assert.Null(updatedParent.ClientCaseId);

        var updatedCaseIds = await db.Set<InvoiceClientCase>()
            .AsNoTracking()
            .Where(item => item.PaymentId == createdDto.Id)
            .Select(item => item.ClientCaseId)
            .ToListAsync();
        Assert.Empty(updatedCaseIds);
    }

    [Fact]
    public async Task Update_ToLegalEntity_CascadesOwnerToActsAndInstallments()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client Owner Before");
        var legalEntityId = await CreateLegalEntityAsync(db, "Legal Entity Owner After");
        var actId = await CreateActAsync(db, clientId, "ACT-LEGAL-CASCADE");
        var controller = CreateController(db, isAdmin: true, userId: actorUserId);

        var createRequest = CreateRequest("INV-LEGAL-CASCADE", clientId, new DateTime(2026, 4, 10), 18_000m);
        createRequest.ActIds = [actId];
        createRequest.InstallmentPlan = CreateInstallmentPlanRequest(
            total: 18_000m,
            startDate: new DateTime(2026, 4, 10),
            new DateTime(2026, 5, 10),
            new DateTime(2026, 6, 10));

        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        var updateRequest = CreateLegalEntityRequest("INV-LEGAL-CASCADE", legalEntityId, new DateTime(2026, 4, 10), 18_000m);
        updateRequest.ActIds = [actId];
        updateRequest.InstallmentPlan = CreateInstallmentPlanRequest(
            total: 18_000m,
            startDate: new DateTime(2026, 4, 10),
            new DateTime(2026, 5, 10),
            new DateTime(2026, 6, 10));

        var updateAction = await controller.Update(createdDto.Id, updateRequest, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(updateAction.Result);
        var updatedDto = Assert.IsType<InvoiceDto>(ok.Value);
        Assert.Null(updatedDto.ClientId);
        Assert.Equal(legalEntityId, updatedDto.LegalEntityId);

        db.ChangeTracker.Clear();
        var parent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);
        var aggregatePayments = await db.Payments
            .AsNoTracking()
            .Where(payment => payment.Id == createdDto.Id || payment.InvoiceId == parent.InvoiceId)
            .OrderBy(payment => payment.Id)
            .ToListAsync();
        Assert.Equal(3, aggregatePayments.Count);
        Assert.All(aggregatePayments, payment =>
        {
            Assert.Null(payment.ClientId);
            Assert.Equal(legalEntityId, payment.LegalEntityId);
        });

        var linkedAct = await db.Acts.AsNoTracking().SingleAsync(act => act.Id == actId);
        Assert.Null(linkedAct.ClientId);
        Assert.Equal(legalEntityId, linkedAct.LegalEntityId);
        Assert.Equal(createdDto.Id, linkedAct.InvoiceId);
        Assert.Equal("INV-LEGAL-CASCADE", linkedAct.InvoiceNumber);
    }

    [Fact]
    public async Task Update_RejectsChangingProtectedInstallmentPayment()
    {
        await using var db = await CreateContextAsync();
        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client Gamma");

        var controller = CreateController(db, isAdmin: true, userId: actorUserId);
        var createRequest = CreateRequest("INV-700", clientId, new DateTime(2026, 4, 10), 10_000m);
        createRequest.InstallmentPlan = CreateInstallmentPlanRequest(
            total: 10_000m,
            startDate: new DateTime(2026, 4, 10),
            new DateTime(2026, 5, 10),
            new DateTime(2026, 6, 10));

        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        db.ChangeTracker.Clear();

        var parent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);
        var installments = await LoadInstallmentPaymentsAsync(db, parent.InvoiceId!.Value, createdDto.Id);
        var protectedInstallment = installments[0];

        var trackedProtectedInstallment = await db.Payments.SingleAsync(payment => payment.Id == protectedInstallment.Id);
        trackedProtectedInstallment.Status = PaymentStatus.Completed;
        trackedProtectedInstallment.IsPaid = true;
        trackedProtectedInstallment.PaidAmount = trackedProtectedInstallment.Amount;
        trackedProtectedInstallment.PaidDate = trackedProtectedInstallment.Date;
        trackedProtectedInstallment.LastPaymentDate = trackedProtectedInstallment.Date;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var updateRequest = CreateRequest("INV-701", clientId, new DateTime(2026, 4, 12), 10_000m);
        updateRequest.InstallmentPlan = CreateInstallmentPlanRequest(
            total: 10_000m,
            startDate: new DateTime(2026, 4, 12),
            new DateTime(2026, 5, 12),
            new DateTime(2026, 6, 12));

        var updateAction = await controller.Update(createdDto.Id, updateRequest, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(updateAction.Result);
        var message = Assert.IsType<string>(badRequest.Value);
        Assert.Contains("Нельзя изменить платеж рассрочки", message);

        db.ChangeTracker.Clear();

        var unchangedParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);
        Assert.Equal("INV-700", unchangedParent.Account);
        Assert.Equal(new DateTime(2026, 6, 10), unchangedParent.Date);

        var unchangedInstallments = await LoadInstallmentPaymentsAsync(db, parent.InvoiceId.Value, createdDto.Id);
        Assert.Equal(new[] { new DateTime(2026, 5, 10), new DateTime(2026, 6, 10) }, unchangedInstallments.Select(item => item.Date).ToArray());
        Assert.Equal(new[] { protectedInstallment.Id, installments[1].Id }, unchangedInstallments.Select(item => item.Id).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task Delete_DetachesLinkedActs_AndRemovesInvoiceAggregate()
    {
        await using var db = await CreateContextAsync();
        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client Delta");
        var actId = await CreateActAsync(db, clientId, "ACT-DEL-1");

        var controller = CreateController(db, isAdmin: true, userId: actorUserId);
        var createRequest = CreateRequest("INV-800", clientId, new DateTime(2026, 4, 10), 8_000m);
        createRequest.ActIds = [actId];
        createRequest.InstallmentPlan = CreateInstallmentPlanRequest(
            total: 8_000m,
            startDate: new DateTime(2026, 4, 10),
            new DateTime(2026, 5, 10),
            new DateTime(2026, 6, 10));

        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var dto = Assert.IsType<InvoiceDto>(created.Value);

        db.ChangeTracker.Clear();

        var parent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == dto.Id);
        var invoiceEntityId = parent.InvoiceId!.Value;

        var deleteResult = await controller.Delete(dto.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteResult);

        db.ChangeTracker.Clear();

        Assert.Empty(await db.Payments.AsNoTracking().Where(payment => payment.InvoiceId == invoiceEntityId).ToListAsync());
        Assert.Null(await db.Invoices.AsNoTracking().SingleOrDefaultAsync(invoice => invoice.Id == invoiceEntityId));

        var detachedAct = await db.Acts.AsNoTracking().SingleAsync(act => act.Id == actId);
        Assert.Null(detachedAct.InvoiceId);
        Assert.Null(detachedAct.InvoiceNumber);
    }

    [Fact]
    public async Task Update_NormalizesInstallmentRowsThatStillUseInvoiceDocumentKind()
    {
        await using var db = await CreateContextAsync();
        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client Legacy Marker");

        var controller = CreateController(db, isAdmin: true, userId: actorUserId);
        var createRequest = CreateRequest("INV-FIX-30", clientId, new DateTime(2026, 4, 8), 18_000m);
        createRequest.InstallmentPlan = CreateInstallmentPlanRequest(
            total: 18_000m,
            startDate: new DateTime(2026, 4, 8),
            new DateTime(2026, 4, 8),
            new DateTime(2026, 4, 30),
            new DateTime(2026, 5, 7));

        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        db.ChangeTracker.Clear();

        var parent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);
        var installments = await LoadInstallmentPaymentsAsync(db, parent.InvoiceId!.Value, createdDto.Id);
        Assert.Equal(3, installments.Count);

        var brokenFirstInstallment = await db.Payments.SingleAsync(payment => payment.Id == installments[0].Id);
        brokenFirstInstallment.DocumentKind = DocumentKind.Invoice;
        brokenFirstInstallment.Amount = 18_000m;
        brokenFirstInstallment.Date = new DateTime(2026, 4, 9);
        brokenFirstInstallment.PlannedDate = new DateTime(2026, 4, 9);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var updateRequest = CreateRequest("INV-FIX-30", clientId, new DateTime(2026, 4, 8), 18_000m);
        updateRequest.InstallmentPlan = CreateInstallmentPlanRequest(
            total: 18_000m,
            startDate: new DateTime(2026, 4, 9),
            new DateTime(2026, 4, 9),
            new DateTime(2026, 4, 30),
            new DateTime(2026, 5, 7));

        var updateAction = await controller.Update(createdDto.Id, updateRequest, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(updateAction.Result);
        _ = Assert.IsType<InvoiceDto>(ok.Value);

        db.ChangeTracker.Clear();

        var aggregatePayments = await db.Payments
            .AsNoTracking()
            .Where(payment => payment.InvoiceId == parent.InvoiceId)
            .OrderBy(payment => payment.Date)
            .ThenBy(payment => payment.Id)
            .ToListAsync();

        Assert.Single(aggregatePayments, payment => payment.DocumentKind == DocumentKind.Invoice);

        var normalizedInstallments = aggregatePayments
            .Where(payment => payment.DocumentKind == DocumentKind.Payment)
            .OrderBy(payment => payment.Date)
            .ThenBy(payment => payment.Id)
            .ToList();

        Assert.Equal(3, normalizedInstallments.Count);
        Assert.Equal(brokenFirstInstallment.Id, normalizedInstallments[0].Id);
        Assert.Equal(6_000m, normalizedInstallments[0].Amount);
        Assert.Equal(new DateTime(2026, 4, 9), normalizedInstallments[0].Date);
        Assert.Contains(InstallmentPaymentMarker, normalizedInstallments[0].SystemNotes, StringComparison.Ordinal);
        Assert.All(normalizedInstallments, payment => Assert.Equal(DocumentKind.Payment, payment.DocumentKind));
    }

    [Fact]
    public async Task BulkDelete_RemovesSelectedInvoiceAggregates_AndDetachesActs()
    {
        await using var db = await CreateContextAsync();
        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client Epsilon");
        var actId1 = await CreateActAsync(db, clientId, "ACT-BULK-1");
        var actId2 = await CreateActAsync(db, clientId, "ACT-BULK-2");

        var controller = CreateController(db, isAdmin: true, userId: actorUserId);

        var firstRequest = CreateRequest("INV-900-A", clientId, new DateTime(2026, 4, 1), 9_000m);
        firstRequest.ActIds = [actId1];
        firstRequest.InstallmentPlan = CreateInstallmentPlanRequest(
            total: 9_000m,
            startDate: new DateTime(2026, 4, 1),
            new DateTime(2026, 5, 1),
            new DateTime(2026, 6, 1));

        var firstCreate = await controller.Create(firstRequest, CancellationToken.None);
        var firstCreated = Assert.IsType<CreatedResult>(firstCreate.Result);
        var firstDto = Assert.IsType<InvoiceDto>(firstCreated.Value);

        var secondRequest = CreateRequest("INV-900-B", clientId, new DateTime(2026, 4, 2), 4_000m);
        secondRequest.ActIds = [actId2];

        var secondCreate = await controller.Create(secondRequest, CancellationToken.None);
        var secondCreated = Assert.IsType<CreatedResult>(secondCreate.Result);
        var secondDto = Assert.IsType<InvoiceDto>(secondCreated.Value);

        db.ChangeTracker.Clear();

        var firstParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == firstDto.Id);
        var secondParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == secondDto.Id);

        var bulkDeleteResult = await controller.BulkDelete(
            new BulkIdsRequest { Ids = [firstDto.Id, secondDto.Id] },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(bulkDeleteResult);
        Assert.Equal(4, GetAnonymousProperty<int>(ok.Value, "deleted"));

        db.ChangeTracker.Clear();

        Assert.Empty(await db.Payments.AsNoTracking().Where(payment =>
            payment.InvoiceId == firstParent.InvoiceId || payment.InvoiceId == secondParent.InvoiceId).ToListAsync());
        Assert.Null(await db.Invoices.AsNoTracking().SingleOrDefaultAsync(invoice => invoice.Id == firstParent.InvoiceId));
        Assert.Null(await db.Invoices.AsNoTracking().SingleOrDefaultAsync(invoice => invoice.Id == secondParent.InvoiceId));

        var detachedActs = await db.Acts
            .AsNoTracking()
            .Where(act => act.Id == actId1 || act.Id == actId2)
            .OrderBy(act => act.Id)
            .ToListAsync();
        Assert.All(detachedActs, act =>
        {
            Assert.Null(act.InvoiceId);
            Assert.Null(act.InvoiceNumber);
        });
    }

    [Fact]
    public async Task ChangeResponsible_PropagatesToInstallmentPayments()
    {
        await using var db = await CreateContextAsync();
        var actorUserId = await CreateUserAsync(db, "Admin User", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Client Zeta");
        var responsibleId1 = await CreateUserAsync(db, "Responsible Legacy");
        var responsibleId2 = await CreateUserAsync(db, "Responsible New");

        var controller = CreateController(db, isAdmin: true, userId: actorUserId);
        var createRequest = CreateRequest("INV-RESP-1", clientId, new DateTime(2026, 4, 5), 11_000m);
        createRequest.ResponsibleIds = [responsibleId1];
        createRequest.InstallmentPlan = CreateInstallmentPlanRequest(
            total: 11_000m,
            startDate: new DateTime(2026, 4, 5),
            new DateTime(2026, 5, 5),
            new DateTime(2026, 6, 5));

        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var dto = Assert.IsType<InvoiceDto>(created.Value);

        db.ChangeTracker.Clear();

        var changeResult = await controller.ChangeResponsible(
            new BulkAssignResponsibleRequest
            {
                Ids = [dto.Id],
                ResponsibleUserId = responsibleId2,
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(changeResult);
        Assert.Equal(1, GetAnonymousProperty<int>(ok.Value, "updated"));

        db.ChangeTracker.Clear();

        var parent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == dto.Id);
        var aggregatePayments = await db.Payments
            .AsNoTracking()
            .Where(payment => payment.Id == dto.Id || payment.InvoiceId == parent.InvoiceId)
            .OrderBy(payment => payment.Id)
            .ToListAsync();

        Assert.Equal(3, aggregatePayments.Count);

        var responsibleRows = await db.Set<PaymentResponsible>()
            .AsNoTracking()
            .Where(item => aggregatePayments.Select(payment => payment.Id).Contains(item.PaymentId))
            .OrderBy(item => item.PaymentId)
            .ThenBy(item => item.UserId)
            .ToListAsync();

        Assert.Equal(3, responsibleRows.Count);
        Assert.All(responsibleRows, row => Assert.Equal(responsibleId2, row.UserId));
    }

    private static UpsertInvoiceRequest CreateRequest(string number, int clientId, DateTime date, decimal amount)
    {
        return new UpsertInvoiceRequest
        {
            Number = number,
            Date = date,
            DueDate = date.AddDays(5),
            Amount = amount,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            ClientId = clientId,
        };
    }

    private static UpsertInvoiceRequest CreateLegalEntityRequest(string number, int legalEntityId, DateTime date, decimal amount)
    {
        var request = CreateRequest(number, 0, date, amount);
        request.ClientId = null;
        request.LegalEntityId = legalEntityId;
        return request;
    }

    private static InvoiceInstallmentPlanRequest CreateInstallmentPlanRequest(decimal total, DateTime startDate, params DateTime[] dates)
    {
        var paymentAmount = dates.Length > 0 ? decimal.Round(total / dates.Length, 2) : total;

        return new InvoiceInstallmentPlanRequest
        {
            Total = total,
            DownPayment = 0m,
            AnnualRate = 0m,
            Months = Math.Max(1, dates.Length),
            StartDate = startDate,
            RoundingMode = InstallmentRoundingMode.None,
            ToPay = total,
            Overpay = 0m,
            LoanAmount = total,
            BaseMonthlyPayment = paymentAmount,
            RoundedMonthlyPayment = paymentAmount,
            Items = dates.Select((itemDate, index) =>
            {
                var isLast = index == dates.Length - 1;
                var payment = isLast ? total - paymentAmount * (dates.Length - 1) : paymentAmount;
                var balance = isLast ? 0m : Math.Max(0m, total - paymentAmount * (index + 1));

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

    private static async Task ConfigureInvoiceRulesAsync(PaymentContext db, int maxClientCases, int maxActs)
    {
        var now = DateTime.UtcNow;
        db.SystemSettings.AddRange(
            new SystemSetting
            {
                Key = InvoiceMaxClientCasesSettingKey,
                Value = maxClientCases.ToString(),
                CreatedAt = now,
                UpdatedAt = now,
            },
            new SystemSetting
            {
                Key = InvoiceMaxActsSettingKey,
                Value = maxActs.ToString(),
                CreatedAt = now,
                UpdatedAt = now,
            });
        await db.SaveChangesAsync();
    }

    private static async Task<List<Payment>> LoadInstallmentPaymentsAsync(PaymentContext db, int invoiceEntityId, int parentPaymentId)
    {
        return await db.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.InvoiceId == invoiceEntityId
                && payment.Id != parentPaymentId
                && payment.SystemNotes != null
                && payment.SystemNotes.Contains(InstallmentPaymentMarker))
            .OrderBy(payment => payment.Date)
            .ThenBy(payment => payment.Id)
            .ToListAsync();
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

    private static async Task<int> CreateLegalEntityAsync(PaymentContext db, string shortName)
    {
        var legalEntity = new LegalEntity
        {
            ShortName = shortName,
            FullName = shortName,
            CreatedAt = DateTime.UtcNow,
        };

        db.LegalEntities.Add(legalEntity);
        await db.SaveChangesAsync();
        return legalEntity.Id;
    }

    private static async Task<int> CreateContractAsync(PaymentContext db, int clientId, string number)
    {
        var contract = new Contract
        {
            Number = number,
            Title = $"Contract {number}",
            Date = new DateTime(2026, 4, 1),
            BillingMode = ContractBillingMode.Fixed,
            Amount = 100_000m,
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
            Amount = 5_000m,
            ClientId = clientId,
            Status = ActStatus.Created,
        };

        db.Acts.Add(act);
        await db.SaveChangesAsync();
        return act.Id;
    }

    private static async Task<int> CreateServiceAsync(PaymentContext db, string name)
    {
        var service = new ServiceDictionaryItem
        {
            Name = name,
            Description = string.Empty,
            Unit = "service",
            DefaultPrice = 0m,
            ColorHex = "#111827",
            IsActive = true,
        };

        db.Services.Add(service);
        await db.SaveChangesAsync();
        return service.Id;
    }

    private static async Task<int> CreatePaymentSourceAsync(PaymentContext db, string name)
    {
        var entity = new PaymentSource
        {
            Name = name,
            Description = name,
            ColorHex = "#2563EB",
            IsActive = true,
        };

        db.PaymentSources.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private static async Task<int> CreateIncomeTypeAsync(PaymentContext db, string name)
    {
        var entity = new IncomeType
        {
            Name = name,
            Description = name,
            ColorHex = "#10B981",
            PaymentType = PaymentType.Income,
            IsActive = true,
        };

        db.IncomeTypes.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private static async Task<int> CreateDealTypeAsync(PaymentContext db, string name)
    {
        var entity = new DealType
        {
            Name = name,
            Description = name,
            ColorHex = "#3B82F6",
            IsActive = true,
        };

        db.DealTypes.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private static async Task<int> CreatePaymentStatusEntityAsync(PaymentContext db, string name)
    {
        var entity = new PaymentStatusEntity
        {
            Name = name,
            Description = name,
            ColorHex = "#6B7280",
            IsActive = true,
        };

        db.PaymentStatuses.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private static async Task<int> CreateUserAsync(PaymentContext db, string fullName, string roleName = "user")
    {
        var role = new Role
        {
            Name = $"{roleName}-{Guid.NewGuid():N}",
            Description = roleName,
            CreatedAt = DateTime.UtcNow,
        };

        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@example.com",
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
            claims.Add(new Claim(ClaimTypes.Name, $"User {userId.Value}"));
        }

        var controller = new InvoicesController(db, new NoopEmailService(), new NoopFormRequirementsService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
                },
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
