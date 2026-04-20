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
