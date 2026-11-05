using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class InvoicePaymentConsistencyTests
{
    [Fact]
    public async Task InvoiceEndpoints_ReturnSameRepresentativePayment_ForClientInvoiceWithSingleOperation()
    {
        await using var db = await CreateContextAsync();
        var aggregate = await CreateSingleOperationInvoiceAsync(db);

        var invoicesController = CreateInvoicesController(db);
        var accountsController = new AccountsController(db);
        var paymentsController = new PaymentsController(db, new NoopFormRequirementsService());

        var invoicesResult = await invoicesController.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            status: null,
            clientId: aggregate.ClientId,
            clientIds: null,
            contractId: null,
            caseId: aggregate.ClientCaseId,
            responsibleId: null,
            search: null,
            type: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var invoicesOk = Assert.IsType<OkObjectResult>(invoicesResult);
        var invoiceItems = ExtractInvoiceItems(invoicesOk);
        var invoiceRow = Assert.Single(invoiceItems);

        Assert.Equal(aggregate.InvoicePaymentId, invoiceRow.Id);
        Assert.Equal(aggregate.InvoiceEntityId, invoiceRow.InvoiceEntityId);
        Assert.Equal("24", invoiceRow.Number);

        var accountsResult = await accountsController.GetRegistry(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            status: null,
            search: null,
            type: PaymentType.Income,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var accountsOk = Assert.IsType<OkObjectResult>(accountsResult.Result);
        var accountsPayload = Assert.IsType<AccountsPageResponse>(accountsOk.Value);
        var registryRow = Assert.Single(accountsPayload.Items);

        Assert.Equal("24", registryRow.AccountNumber);
        Assert.Equal(aggregate.InvoicePaymentId, registryRow.Invoice?.Id);
        Assert.Collection(
            registryRow.Payments,
            payment => Assert.Equal(aggregate.InvoicePaymentId, payment.Id));

        var paymentsResult = await paymentsController.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            clientId: aggregate.ClientId,
            caseId: aggregate.ClientCaseId,
            excludeInstallmentParents: false,
            ct: CancellationToken.None);

        var paymentsOk = Assert.IsType<OkObjectResult>(paymentsResult);
        var paymentItems = Assert.IsAssignableFrom<List<Payment>>(paymentsOk.Value);

        Assert.Collection(
            paymentItems,
            payment => Assert.Equal(aggregate.InvoicePaymentId, payment.Id));
    }

    [Fact]
    public async Task InvoiceEndpoints_KeepAllSiblingOperations_ForClientInvoiceWithSeveralPayments()
    {
        await using var db = await CreateContextAsync();
        var aggregate = await CreateInvoiceWithSiblingPaymentAsync(db);

        var invoicesController = CreateInvoicesController(db);
        var accountsController = new AccountsController(db);
        var paymentsController = new PaymentsController(db, new NoopFormRequirementsService());

        var invoicesResult = await invoicesController.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            status: null,
            clientId: aggregate.ClientId,
            clientIds: null,
            contractId: null,
            caseId: aggregate.ClientCaseId,
            responsibleId: null,
            search: null,
            type: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var invoicesOk = Assert.IsType<OkObjectResult>(invoicesResult);
        var invoiceItems = ExtractInvoiceItems(invoicesOk);
        var invoiceRow = Assert.Single(invoiceItems);

        Assert.Equal(aggregate.InvoicePaymentId, invoiceRow.Id);
        Assert.Equal(aggregate.InvoiceEntityId, invoiceRow.InvoiceEntityId);
        Assert.Equal("25", invoiceRow.Number);

        var accountsResult = await accountsController.GetRegistry(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            status: null,
            search: null,
            type: PaymentType.Income,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var accountsOk = Assert.IsType<OkObjectResult>(accountsResult.Result);
        var accountsPayload = Assert.IsType<AccountsPageResponse>(accountsOk.Value);
        var registryRow = Assert.Single(accountsPayload.Items);

        Assert.Equal("25", registryRow.AccountNumber);
        Assert.Equal(aggregate.InvoicePaymentId, registryRow.Invoice?.Id);
        var registryPayment = Assert.Single(registryRow.Payments);
        Assert.Equal(aggregate.SiblingPaymentId, registryPayment.Id);

        var paymentsResult = await paymentsController.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            clientId: aggregate.ClientId,
            caseId: aggregate.ClientCaseId,
            excludeInstallmentParents: false,
            ct: CancellationToken.None);

        var paymentsOk = Assert.IsType<OkObjectResult>(paymentsResult);
        var paymentItems = Assert.IsAssignableFrom<List<Payment>>(paymentsOk.Value);

        Assert.Equal(
            new[] { aggregate.InvoicePaymentId, aggregate.SiblingPaymentId },
            paymentItems.Select(payment => payment.Id).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task AccountsRegistry_ReturnsOpenableParentInvoiceId_ForInstallmentInvoice()
    {
        await using var db = await CreateContextAsync();
        var aggregate = await CreateInstallmentInvoiceAggregateAsync(db);

        var invoicesController = CreateInvoicesController(db);
        var accountsController = new AccountsController(db);

        var accountsResult = await accountsController.GetRegistry(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            status: null,
            search: null,
            type: PaymentType.Income,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var accountsOk = Assert.IsType<OkObjectResult>(accountsResult.Result);
        var accountsPayload = Assert.IsType<AccountsPageResponse>(accountsOk.Value);
        var registryRow = Assert.Single(accountsPayload.Items);

        Assert.Equal("27", registryRow.AccountNumber);
        Assert.Equal(aggregate.InvoicePaymentId, registryRow.Invoice?.Id);
        Assert.Equal(aggregate.InvoiceEntityId, registryRow.Invoice?.InvoiceEntityId);
        Assert.Equal(PaymentStatus.Processing, registryRow.Invoice?.Status);
        Assert.False(registryRow.Invoice?.IsPaid ?? true);
        Assert.Equal(
            aggregate.InstallmentPaymentIds.OrderBy(id => id).ToArray(),
            registryRow.Payments.Select(payment => payment.Id).OrderBy(id => id).ToArray());

        var openedInvoiceResult = await invoicesController.GetById(registryRow.Invoice!.Id, CancellationToken.None);
        var openedInvoiceOk = Assert.IsType<OkObjectResult>(openedInvoiceResult.Result);
        var openedInvoice = Assert.IsType<InvoiceDto>(openedInvoiceOk.Value);

        Assert.Equal(aggregate.InvoicePaymentId, openedInvoice.Id);
        Assert.Equal(aggregate.InvoiceEntityId, openedInvoice.InvoiceEntityId);
    }

    [Fact]
    public async Task InvoiceEndpoints_UseAggregateStatus_ForInstallmentInvoice()
    {
        await using var db = await CreateContextAsync();
        var aggregate = await CreateInstallmentInvoiceAggregateAsync(db);

        var controller = CreateInvoicesController(db);

        var listResult = await controller.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 5, 31),
            status: null,
            clientId: aggregate.ClientId,
            clientIds: null,
            contractId: null,
            caseId: aggregate.ClientCaseId,
            responsibleId: null,
            search: null,
            type: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var listOk = Assert.IsType<OkObjectResult>(listResult);
        var listInvoice = Assert.Single(ExtractInvoiceItems(listOk));

        Assert.Equal(PaymentStatus.Processing, listInvoice.Status);
        Assert.False(listInvoice.IsPaid);
        Assert.Equal(new DateTime(2026, 5, 10), listInvoice.DueDate);
        Assert.Equal(new DateTime(2026, 4, 10), listInvoice.PaidDate);

        var openedInvoiceResult = await controller.GetById(aggregate.InvoicePaymentId, CancellationToken.None);
        var openedInvoiceOk = Assert.IsType<OkObjectResult>(openedInvoiceResult.Result);
        var openedInvoice = Assert.IsType<InvoiceDto>(openedInvoiceOk.Value);

        Assert.Equal(PaymentStatus.Processing, openedInvoice.Status);
        Assert.False(openedInvoice.IsPaid);
        Assert.Equal(new DateTime(2026, 5, 10), openedInvoice.DueDate);
        Assert.Equal(new DateTime(2026, 4, 10), openedInvoice.PaidDate);
    }

    [Fact]
    public async Task InvoiceFilters_AndSummary_UseAggregateStatus_ForLegacyBrokenInstallmentInvoice()
    {
        await using var db = await CreateContextAsync();
        var aggregate = await CreateLegacyBrokenInstallmentAggregateAsync(db);

        var controller = CreateInvoicesController(db);

        var processingResult = await controller.Get(
            from: new DateTime(2025, 10, 1),
            to: new DateTime(2026, 8, 31),
            status: PaymentStatus.Processing,
            clientId: aggregate.ClientId,
            clientIds: null,
            contractId: null,
            caseId: aggregate.ClientCaseId,
            responsibleId: null,
            search: null,
            type: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var processingOk = Assert.IsType<OkObjectResult>(processingResult);
        var processingItems = ExtractInvoiceItems(processingOk);
        var processingInvoice = Assert.Single(processingItems);

        Assert.Equal(aggregate.InvoicePaymentId, processingInvoice.Id);
        Assert.Equal(PaymentStatus.Processing, processingInvoice.Status);
        Assert.False(processingInvoice.IsPaid);
        Assert.Equal(new DateTime(2026, 4, 27), processingInvoice.DueDate);
        Assert.Equal(new DateTime(2026, 1, 30), processingInvoice.PaidDate);
        Assert.Equal(36_000m, processingInvoice.PaidAmount);
        Assert.Equal(18_000m, processingInvoice.OutstandingAmount);

        var pendingResult = await controller.Get(
            from: new DateTime(2025, 10, 1),
            to: new DateTime(2026, 8, 31),
            status: PaymentStatus.Pending,
            clientId: aggregate.ClientId,
            clientIds: null,
            contractId: null,
            caseId: aggregate.ClientCaseId,
            responsibleId: null,
            search: null,
            type: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var pendingOk = Assert.IsType<OkObjectResult>(pendingResult);
        Assert.Empty(ExtractInvoiceItems(pendingOk));

        var summaryAction = await controller.GetSummary(
            from: new DateTime(2025, 10, 1),
            to: new DateTime(2026, 8, 31),
            status: PaymentStatus.Processing,
            clientId: aggregate.ClientId,
            caseId: aggregate.ClientCaseId,
            responsibleId: null,
            search: null,
            type: null,
            ct: CancellationToken.None);

        var summaryOk = Assert.IsType<OkObjectResult>(summaryAction.Result);
        var summary = Assert.IsType<InvoiceSummaryDto>(summaryOk.Value);

        Assert.Equal(1, summary.Total.Count);
        Assert.Equal(205_000m, summary.Total.Amount);
        Assert.Equal(1, summary.Pending.Count);
        Assert.Equal(18_000m, summary.Pending.Amount);
        Assert.Equal(0, summary.Paid.Count);
        Assert.Equal(0, summary.Overdue.Count);
    }

    [Fact]
    public async Task RepairPaymentState_DryRunReportsStaleInstallmentParent_WithoutSaving()
    {
        await using var db = await CreateContextAsync();
        var aggregate = await CreateInstallmentInvoiceAggregateAsync(db);
        var controller = CreateInvoicesController(db);

        var action = await controller.RepairPaymentState(
            new RepairInvoicePaymentStateRequest
            {
                DryRun = true,
                PaymentIds = [aggregate.InvoicePaymentId],
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var response = Assert.IsType<RepairInvoicePaymentStateResponse>(ok.Value);
        var item = Assert.Single(response.Items);

        Assert.True(response.DryRun);
        Assert.Equal(1, response.Scanned);
        Assert.Equal(1, response.Changed);
        Assert.Equal(0, response.Applied);
        Assert.Equal(aggregate.InvoiceEntityId, item.InvoiceId);
        Assert.Equal(aggregate.InvoicePaymentId, item.RepresentativePaymentId);
        Assert.Equal(2, item.SourcePaymentCount);
        Assert.Equal(30_000m, item.Current.PaidAmount);
        Assert.Equal(PaymentStatus.Completed, item.Current.Status);
        Assert.Equal(15_000m, item.Suggested.PaidAmount);
        Assert.Equal(PaymentStatus.Processing, item.Suggested.Status);
        Assert.Equal(InvoiceWorkflowStatus.Draft, item.Suggested.WorkflowStatus);

        var storedParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == aggregate.InvoicePaymentId);
        Assert.Equal(30_000m, storedParent.PaidAmount);
        Assert.Equal(PaymentStatus.Completed, storedParent.Status);
    }

    [Fact]
    public async Task RepairPaymentState_AppliesSuggestedState_WhenDryRunFalse()
    {
        await using var db = await CreateContextAsync();
        var aggregate = await CreateInstallmentInvoiceAggregateAsync(db);
        var controller = CreateInvoicesController(db);

        var action = await controller.RepairPaymentState(
            new RepairInvoicePaymentStateRequest
            {
                DryRun = false,
                PaymentIds = [aggregate.InvoicePaymentId],
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var response = Assert.IsType<RepairInvoicePaymentStateResponse>(ok.Value);

        Assert.False(response.DryRun);
        Assert.Equal(1, response.Scanned);
        Assert.Equal(1, response.Changed);
        Assert.Equal(1, response.Applied);

        db.ChangeTracker.Clear();
        var storedParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == aggregate.InvoicePaymentId);

        Assert.Equal(15_000m, storedParent.PaidAmount);
        Assert.Equal(PaymentStatus.Processing, storedParent.Status);
        Assert.Equal(InvoiceWorkflowStatus.Draft, storedParent.InvoiceWorkflowStatus);
        Assert.False(storedParent.IsPaid);
        Assert.Equal(new DateTime(2026, 5, 10), storedParent.Date);
        Assert.Equal(new DateTime(2026, 5, 10), storedParent.PlannedDate);
        Assert.Equal(new DateTime(2026, 4, 10), storedParent.PaidDate);
        Assert.Equal(new DateTime(2026, 4, 10), storedParent.LastPaymentDate);
    }

    [Fact]
    public async Task RepairPaymentState_KeepsIsPaidTrue_WhenAggregateRemainsCompleted()
    {
        await using var db = await CreateContextAsync();
        var aggregate = await CreateCompletedInstallmentInvoiceWithStaleDatesAsync(db);
        var controller = CreateInvoicesController(db);

        var action = await controller.RepairPaymentState(
            new RepairInvoicePaymentStateRequest
            {
                DryRun = true,
                PaymentIds = [aggregate.InvoicePaymentId],
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var response = Assert.IsType<RepairInvoicePaymentStateResponse>(ok.Value);
        var item = Assert.Single(response.Items);

        Assert.Equal(PaymentStatus.Completed, item.Suggested.Status);
        Assert.Equal(InvoiceWorkflowStatus.Draft, item.Suggested.WorkflowStatus);
        Assert.True(item.Suggested.IsPaid);
        Assert.Equal(10_000m, item.Suggested.PaidAmount);
        Assert.Equal(new DateTime(2026, 6, 10), item.Suggested.Date);
        Assert.Equal(new DateTime(2026, 6, 10), item.Suggested.PlannedDate);
    }

    [Fact]
    public void CalculateRepresentativeState_PreservesClientSignatureRequirement_WhenPaymentStateChanges()
    {
        var representative = new Payment
        {
            Id = 10,
            InvoiceId = 100,
            DocumentKind = DocumentKind.Invoice,
            Amount = 10_000m,
            Status = PaymentStatus.Pending,
            InvoiceWorkflowStatus = InvoiceWorkflowStatus.RequiresClientSignature,
            Date = new DateTime(2026, 5, 10),
            PlannedDate = new DateTime(2026, 5, 10),
            Type = PaymentType.Income,
        };
        var linkedPayment = new Payment
        {
            Id = 11,
            InvoiceId = 100,
            DocumentKind = DocumentKind.Payment,
            Amount = 10_000m,
            PaidAmount = 10_000m,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Date = new DateTime(2026, 5, 11),
            PlannedDate = new DateTime(2026, 5, 11),
            PaidDate = new DateTime(2026, 5, 11),
            LastPaymentDate = new DateTime(2026, 5, 11),
            Type = PaymentType.Income,
        };

        var snapshot = InvoicePaymentStateService.CalculateRepresentativeState(
            representative,
            [linkedPayment],
            new DateTime(2026, 5, 12));

        Assert.Equal(10_000m, snapshot.PaidAmount);
        Assert.Equal(PaymentStatus.Completed, snapshot.Status);
        Assert.Equal(InvoiceWorkflowStatus.RequiresClientSignature, snapshot.WorkflowStatus);
        Assert.True(snapshot.IsPaid);
    }

    [Fact]
    public async Task RepairPaymentState_RemovesStalePartialStatus_FromUnpaidInvoice()
    {
        await using var db = await CreateContextAsync();
        var invoice = await CreateUnpaidInvoiceWithStalePartialStateAsync(db);
        var controller = CreateInvoicesController(db);

        var action = await controller.RepairPaymentState(
            new RepairInvoicePaymentStateRequest
            {
                DryRun = false,
                PaymentIds = [invoice.PaymentId],
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var response = Assert.IsType<RepairInvoicePaymentStateResponse>(ok.Value);
        var item = Assert.Single(response.Items);

        Assert.Equal(1, response.Applied);
        Assert.Equal(PaymentStatus.Processing, item.Current.Status);
        Assert.Equal(InvoiceWorkflowStatus.PartiallyPaid, item.Current.WorkflowStatus);
        Assert.Equal(0m, item.Suggested.PaidAmount);
        Assert.Equal(PaymentStatus.Pending, item.Suggested.Status);
        Assert.Equal(InvoiceWorkflowStatus.Draft, item.Suggested.WorkflowStatus);

        db.ChangeTracker.Clear();
        var storedPayment = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == invoice.PaymentId);

        Assert.Equal(0m, storedPayment.PaidAmount);
        Assert.Equal(PaymentStatus.Pending, storedPayment.Status);
        Assert.Equal(InvoiceWorkflowStatus.Draft, storedPayment.InvoiceWorkflowStatus);
        Assert.False(storedPayment.IsPaid);
        Assert.Null(storedPayment.PaidDate);
        Assert.Null(storedPayment.LastPaymentDate);
    }

    [Fact]
    public async Task InvoicePaymentStateReconciliationService_RepairsStaleInstallmentParent()
    {
        await using var db = await CreateContextAsync();
        var aggregate = await CreateInstallmentInvoiceAggregateAsync(db);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InvoicePaymentStateReconciliation:BatchSize"] = "2",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton(db);
        await using var provider = services.BuildServiceProvider();
        var service = new InvoicePaymentStateReconciliationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            NullLogger<InvoicePaymentStateReconciliationService>.Instance);

        var result = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Changed);
        Assert.Equal(1, result.Applied);

        db.ChangeTracker.Clear();
        var storedParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == aggregate.InvoicePaymentId);

        Assert.Equal(15_000m, storedParent.PaidAmount);
        Assert.Equal(PaymentStatus.Processing, storedParent.Status);
        Assert.Equal(InvoiceWorkflowStatus.Draft, storedParent.InvoiceWorkflowStatus);
        Assert.False(storedParent.IsPaid);
        Assert.Equal(new DateTime(2026, 5, 10), storedParent.Date);
        Assert.Equal(new DateTime(2026, 5, 10), storedParent.PlannedDate);
        Assert.Equal(new DateTime(2026, 4, 10), storedParent.PaidDate);
        Assert.Equal(new DateTime(2026, 4, 10), storedParent.LastPaymentDate);
    }

    [Fact]
    public async Task PaymentUpdate_NormalizesInvoiceLink_AndKeepsInvoiceReadsConsistent()
    {
        await using var db = await CreateContextAsync();
        var aggregate = await CreateInvoiceWithDetachedSiblingPaymentAsync(db);

        var invoicesController = CreateInvoicesController(db);
        var accountsController = new AccountsController(db);
        var paymentsController = await CreatePaymentsControllerAsync(db);
        var updateModel = new Payment
        {
            Amount = 5_000m,
            CounterpartyName = "Test Client",
            Date = new DateTime(2026, 4, 4),
            PlannedDate = new DateTime(2026, 4, 4),
            PaidDate = new DateTime(2026, 4, 5),
            LastPaymentDate = new DateTime(2026, 4, 5),
            PaidAmount = 5_000m,
            Description = "Supplement for invoice 26",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            ClientId = aggregate.ClientId,
            ClientCaseId = aggregate.ClientCaseId,
            InvoiceId = aggregate.InvoiceEntityId,
            Account = " OLD-26 ",
            AccountDate = new DateTime(2025, 1, 15),
            Notes = string.Empty,
            SystemNotes = string.Empty,
        };

        var updateResult = await paymentsController.Update(
            aggregate.SiblingPaymentId,
            updateModel,
            CancellationToken.None);

        var updateOk = Assert.IsType<OkObjectResult>(updateResult);
        var updatedPayment = Assert.IsType<Payment>(updateOk.Value);

        Assert.Equal(aggregate.InvoiceEntityId, updatedPayment.InvoiceId);
        Assert.Equal("26", updatedPayment.Account);
        Assert.Equal(new DateTime(2026, 4, 2), updatedPayment.AccountDate);
        Assert.Equal("OLD-26", updatedPayment.LegacyAccountNumber);
        Assert.Equal(new DateTime(2025, 1, 15), updatedPayment.LegacyAccountDate);

        var paymentByIdResult = await paymentsController.GetById(aggregate.SiblingPaymentId, CancellationToken.None);
        var paymentByIdOk = Assert.IsType<OkObjectResult>(paymentByIdResult);
        var paymentById = Assert.IsType<Payment>(paymentByIdOk.Value);

        Assert.Equal(aggregate.InvoiceEntityId, paymentById.InvoiceId);
        Assert.Equal("26", paymentById.Account);
        Assert.Equal(new DateTime(2026, 4, 2), paymentById.AccountDate);
        Assert.Equal("OLD-26", paymentById.LegacyAccountNumber);

        var invoicesResult = await invoicesController.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            status: null,
            clientId: aggregate.ClientId,
            clientIds: null,
            contractId: null,
            caseId: aggregate.ClientCaseId,
            responsibleId: null,
            search: null,
            type: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var invoicesOk = Assert.IsType<OkObjectResult>(invoicesResult);
        var invoiceItems = ExtractInvoiceItems(invoicesOk);
        var invoiceRow = Assert.Single(invoiceItems);

        Assert.Equal(aggregate.InvoicePaymentId, invoiceRow.Id);
        Assert.Equal(aggregate.InvoiceEntityId, invoiceRow.InvoiceEntityId);
        Assert.Equal("26", invoiceRow.Number);

        var accountsResult = await accountsController.GetRegistry(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            status: null,
            search: null,
            type: PaymentType.Income,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var accountsOk = Assert.IsType<OkObjectResult>(accountsResult.Result);
        var accountsPayload = Assert.IsType<AccountsPageResponse>(accountsOk.Value);
        var registryRow = Assert.Single(accountsPayload.Items);

        Assert.Equal("26", registryRow.AccountNumber);
        Assert.Equal(aggregate.InvoicePaymentId, registryRow.Invoice?.Id);
        Assert.Equal(aggregate.InvoiceEntityId, registryRow.Invoice?.InvoiceEntityId);
        var registryPayment = Assert.Single(registryRow.Payments);
        Assert.Equal(aggregate.SiblingPaymentId, registryPayment.Id);

        var paymentsResult = await paymentsController.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            clientId: aggregate.ClientId,
            caseId: aggregate.ClientCaseId,
            excludeInstallmentParents: false,
            ct: CancellationToken.None);

        var paymentsOk = Assert.IsType<OkObjectResult>(paymentsResult);
        var paymentItems = Assert.IsAssignableFrom<List<Payment>>(paymentsOk.Value);

        Assert.Equal(
            new[] { aggregate.InvoicePaymentId, aggregate.SiblingPaymentId },
            paymentItems.Select(payment => payment.Id).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task PaymentCreate_NormalizesInvoiceLink_AndKeepsInvoiceReadsConsistent()
    {
        await using var db = await CreateContextAsync();
        var aggregate = await CreateSingleOperationInvoiceAsync(db);

        var invoicesController = CreateInvoicesController(db);
        var accountsController = new AccountsController(db);
        var paymentsController = await CreatePaymentsControllerAsync(db);

        // This is an authenticated financial-writer scenario, not an ACL bypass.
        var adminRole = await db.Roles.SingleOrDefaultAsync(role => role.Name == "admin") ?? new Role { Name = "admin" };
        var actor = new User
        {
            Role = adminRole,
            Email = "invoice-create-writer@test.local",
            FullName = "Invoice create administrator",
            PasswordHash = "unused",
            IsActive = true,
            IsApproved = true,
            IsEmployee = true,
        };
        db.Users.Add(actor); await db.SaveChangesAsync();
        ((ClaimsIdentity)paymentsController.User.Identity!).AddClaim(new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()));

        var createModel = new Payment
        {
            Amount = 3_000m,
            CounterpartyName = "Supplement payment",
            Date = new DateTime(2026, 4, 4),
            PlannedDate = new DateTime(2026, 4, 4),
            PaidDate = new DateTime(2026, 4, 5),
            LastPaymentDate = new DateTime(2026, 4, 5),
            PaidAmount = 3_000m,
            Description = "Supplement for invoice 24",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            ClientId = aggregate.ClientId,
            ClientCaseId = aggregate.ClientCaseId,
            InvoiceId = aggregate.InvoiceEntityId,
            Account = " LEGACY-24 ",
            AccountDate = new DateTime(2025, 12, 31),
            Notes = string.Empty,
            SystemNotes = string.Empty,
        };

        var createResult = await PaymentOperationKeyTestSetup.CreateAsync(paymentsController, db, createModel, CancellationToken.None);

        var createCreated = Assert.IsType<CreatedResult>(createResult);
        var createdPayment = Assert.IsType<Payment>(createCreated.Value);

        Assert.NotEqual(aggregate.InvoicePaymentId, createdPayment.Id);
        Assert.Equal(aggregate.InvoiceEntityId, createdPayment.InvoiceId);
        Assert.Equal("24", createdPayment.Account);
        Assert.Equal(new DateTime(2026, 4, 2), createdPayment.AccountDate);
        Assert.Equal("LEGACY-24", createdPayment.LegacyAccountNumber);
        Assert.Equal(new DateTime(2025, 12, 31), createdPayment.LegacyAccountDate);

        var paymentByIdResult = await paymentsController.GetById(createdPayment.Id, CancellationToken.None);
        var paymentByIdOk = Assert.IsType<OkObjectResult>(paymentByIdResult);
        var paymentById = Assert.IsType<Payment>(paymentByIdOk.Value);

        Assert.Equal(aggregate.InvoiceEntityId, paymentById.InvoiceId);
        Assert.Equal("24", paymentById.Account);
        Assert.Equal(new DateTime(2026, 4, 2), paymentById.AccountDate);
        Assert.Equal("LEGACY-24", paymentById.LegacyAccountNumber);

        var invoicesResult = await invoicesController.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            status: null,
            clientId: aggregate.ClientId,
            clientIds: null,
            contractId: null,
            caseId: aggregate.ClientCaseId,
            responsibleId: null,
            search: null,
            type: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var invoicesOk = Assert.IsType<OkObjectResult>(invoicesResult);
        var invoiceItems = ExtractInvoiceItems(invoicesOk);
        var invoiceRow = Assert.Single(invoiceItems);

        Assert.Equal(aggregate.InvoicePaymentId, invoiceRow.Id);
        Assert.Equal(aggregate.InvoiceEntityId, invoiceRow.InvoiceEntityId);
        Assert.Equal("24", invoiceRow.Number);

        var accountsResult = await accountsController.GetRegistry(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            status: null,
            search: null,
            type: PaymentType.Income,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var accountsOk = Assert.IsType<OkObjectResult>(accountsResult.Result);
        var accountsPayload = Assert.IsType<AccountsPageResponse>(accountsOk.Value);
        var registryRow = Assert.Single(accountsPayload.Items);

        Assert.Equal("24", registryRow.AccountNumber);
        Assert.Equal(aggregate.InvoicePaymentId, registryRow.Invoice?.Id);
        Assert.Equal(aggregate.InvoiceEntityId, registryRow.Invoice?.InvoiceEntityId);
        var registryPayment = Assert.Single(registryRow.Payments);
        Assert.Equal(createdPayment.Id, registryPayment.Id);

        var paymentsResult = await paymentsController.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            clientId: aggregate.ClientId,
            caseId: aggregate.ClientCaseId,
            excludeInstallmentParents: false,
            ct: CancellationToken.None);

        var paymentsOk = Assert.IsType<OkObjectResult>(paymentsResult);
        var paymentItems = Assert.IsAssignableFrom<List<Payment>>(paymentsOk.Value);

        Assert.Equal(
            new[] { aggregate.InvoicePaymentId, createdPayment.Id },
            paymentItems.Select(payment => payment.Id).OrderBy(id => id).ToArray());
    }

    private static List<InvoiceDto> ExtractInvoiceItems(OkObjectResult ok)
    {
        var itemsProperty = ok.Value?.GetType().GetProperty("items");
        Assert.NotNull(itemsProperty);

        var rawItems = itemsProperty!.GetValue(ok.Value);
        var items = Assert.IsAssignableFrom<IEnumerable<InvoiceDto>>(rawItems);
        return items.ToList();
    }

    private static async Task<(int InvoicePaymentId, int InvoiceEntityId, int ClientId, int ClientCaseId)> CreateSingleOperationInvoiceAsync(PaymentContext db)
    {
        var client = new Client
        {
            Name = "Байгузина Екатерина Евгеньевна",
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "БАЙГУЗИНА - ОТМЕНА ПРОТОКОЛОВ ОБЩЕГО СОБРАНИЯ",
        };

        var invoice = new Invoice
        {
            Number = "24",
            Date = new DateTime(2026, 4, 2),
        };

        var representativePayment = new Payment
        {
            Amount = 10_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 4, 2),
            PlannedDate = new DateTime(2026, 4, 2),
            PaidDate = new DateTime(2026, 4, 3),
            LastPaymentDate = new DateTime(2026, 4, 3),
            PaidAmount = 10_000m,
            Description = "Оплата по счету №24",
            DocumentKind = DocumentKind.Invoice,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            Client = client,
            ClientCase = clientCase,
            Invoice = invoice,
            Account = invoice.Number,
            AccountDate = invoice.Date,
        };

        db.Payments.Add(representativePayment);
        await db.SaveChangesAsync();

        return (representativePayment.Id, invoice.Id, client.Id, clientCase.Id);
    }

    private static async Task<(int InvoicePaymentId, int SiblingPaymentId, int InvoiceEntityId, int ClientId, int ClientCaseId)> CreateInvoiceWithSiblingPaymentAsync(PaymentContext db)
    {
        var client = new Client
        {
            Name = "Байгузина Екатерина Евгеньевна",
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "БАЙГУЗИНА - ОТМЕНА ПРОТОКОЛОВ ОБЩЕГО СОБРАНИЯ",
        };

        var invoice = new Invoice
        {
            Number = "25",
            Date = new DateTime(2026, 4, 2),
        };

        var representativePayment = new Payment
        {
            Amount = 10_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 4, 2),
            PlannedDate = new DateTime(2026, 4, 2),
            PaidDate = new DateTime(2026, 4, 3),
            LastPaymentDate = new DateTime(2026, 4, 3),
            PaidAmount = 10_000m,
            Description = "Оплата по счету №25",
            DocumentKind = DocumentKind.Invoice,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            Client = client,
            ClientCase = clientCase,
            Invoice = invoice,
            Account = invoice.Number,
            AccountDate = invoice.Date,
        };

        var siblingPayment = new Payment
        {
            Amount = 5_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 4, 4),
            PlannedDate = new DateTime(2026, 4, 4),
            PaidDate = new DateTime(2026, 4, 5),
            LastPaymentDate = new DateTime(2026, 4, 5),
            PaidAmount = 5_000m,
            Description = "Доплата по счету №25",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            Client = client,
            ClientCase = clientCase,
            Invoice = invoice,
            InvoiceId = null,
            Account = invoice.Number,
            AccountDate = invoice.Date,
        };

        db.Payments.AddRange(representativePayment, siblingPayment);
        await db.SaveChangesAsync();

        return (representativePayment.Id, siblingPayment.Id, invoice.Id, client.Id, clientCase.Id);
    }

    private static async Task<(int InvoicePaymentId, int SiblingPaymentId, int InvoiceEntityId, int ClientId, int ClientCaseId)> CreateInvoiceWithDetachedSiblingPaymentAsync(PaymentContext db)
    {
        var client = new Client
        {
            Name = "Test Client",
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Test Case",
        };

        var invoice = new Invoice
        {
            Number = "26",
            Date = new DateTime(2026, 4, 2),
        };

        var representativePayment = new Payment
        {
            Amount = 10_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 4, 2),
            PlannedDate = new DateTime(2026, 4, 2),
            PaidDate = new DateTime(2026, 4, 3),
            LastPaymentDate = new DateTime(2026, 4, 3),
            PaidAmount = 10_000m,
            Description = "Invoice 26",
            DocumentKind = DocumentKind.Invoice,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            Client = client,
            ClientCase = clientCase,
            Invoice = invoice,
            Account = invoice.Number,
            AccountDate = invoice.Date,
        };

        var siblingPayment = new Payment
        {
            Amount = 5_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 4, 4),
            PlannedDate = new DateTime(2026, 4, 4),
            PaidDate = new DateTime(2026, 4, 5),
            LastPaymentDate = new DateTime(2026, 4, 5),
            PaidAmount = 5_000m,
            Description = "Detached supplement",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            Client = client,
            ClientCase = clientCase,
            InvoiceId = null,
            Account = "OLD-26",
            AccountDate = new DateTime(2025, 1, 15),
        };

        db.Payments.AddRange(representativePayment, siblingPayment);
        await db.SaveChangesAsync();

        return (representativePayment.Id, siblingPayment.Id, invoice.Id, client.Id, clientCase.Id);
    }

    private static async Task<(int PaymentId, int InvoiceEntityId)> CreateUnpaidInvoiceWithStalePartialStateAsync(PaymentContext db)
    {
        var dueDate = DateTime.UtcNow.Date.AddDays(7);
        var invoice = new Invoice
        {
            Number = "STALE-2500",
            Date = dueDate,
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var payment = new Payment
        {
            Amount = 2_500m,
            CounterpartyName = "Unpaid invoice client",
            Date = dueDate,
            PlannedDate = dueDate,
            PaidAmount = 0m,
            Description = "Unpaid invoice with stale partial status",
            DocumentKind = DocumentKind.Invoice,
            Status = PaymentStatus.Processing,
            InvoiceWorkflowStatus = InvoiceWorkflowStatus.PartiallyPaid,
            IsPaid = false,
            Type = PaymentType.Income,
            InvoiceId = invoice.Id,
            Account = invoice.Number,
            AccountDate = invoice.Date,
        };

        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        return (payment.Id, invoice.Id);
    }

    private static async Task<(int InvoicePaymentId, int InvoiceEntityId)> CreateCompletedInstallmentInvoiceWithStaleDatesAsync(PaymentContext db)
    {
        var client = new Client
        {
            Name = "Completed aggregate client",
        };
        var invoice = new Invoice
        {
            Number = "PAID-PLAN",
            Date = new DateTime(2026, 5, 1),
        };

        db.Clients.Add(client);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var representativePayment = new Payment
        {
            Amount = 10_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 5, 1),
            PlannedDate = new DateTime(2026, 5, 1),
            PaidDate = new DateTime(2026, 5, 1),
            LastPaymentDate = new DateTime(2026, 5, 1),
            PaidAmount = 10_000m,
            Description = "Paid installment invoice",
            DocumentKind = DocumentKind.Invoice,
            Status = PaymentStatus.Completed,
            InvoiceWorkflowStatus = InvoiceWorkflowStatus.Paid,
            IsPaid = true,
            Type = PaymentType.Income,
            Client = client,
            InvoiceId = invoice.Id,
            Account = invoice.Number,
            AccountDate = invoice.Date,
        };

        var firstInstallment = new Payment
        {
            Amount = 5_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 5, 10),
            PlannedDate = new DateTime(2026, 5, 10),
            PaidDate = new DateTime(2026, 5, 10),
            LastPaymentDate = new DateTime(2026, 5, 10),
            PaidAmount = 5_000m,
            Description = "Paid installment 1/2",
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            Client = client,
            InvoiceId = invoice.Id,
            Account = invoice.Number,
            AccountDate = invoice.Date,
            SystemNotes = $"{InvoiceManagedPaymentRules.InstallmentPaymentMarker} 1",
        };

        var secondInstallment = new Payment
        {
            Amount = 5_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 6, 10),
            PlannedDate = new DateTime(2026, 6, 10),
            PaidDate = new DateTime(2026, 6, 10),
            LastPaymentDate = new DateTime(2026, 6, 10),
            PaidAmount = 5_000m,
            Description = "Paid installment 2/2",
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            Client = client,
            InvoiceId = invoice.Id,
            Account = invoice.Number,
            AccountDate = invoice.Date,
            SystemNotes = $"{InvoiceManagedPaymentRules.InstallmentPaymentMarker} 2",
        };

        db.Payments.AddRange(representativePayment, firstInstallment, secondInstallment);
        await db.SaveChangesAsync();

        return (representativePayment.Id, invoice.Id);
    }

    private static async Task<(int InvoicePaymentId, int[] InstallmentPaymentIds, int InvoiceEntityId, int ClientId, int ClientCaseId)> CreateInstallmentInvoiceAggregateAsync(PaymentContext db)
    {
        var client = new Client
        {
            Name = "Артамонов Александр Александрович",
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "АРТАМОНОВ - ОФОРМЛЕНИЕ ГАРАЖА",
        };

        var invoice = new Invoice
        {
            Number = "27",
            Date = new DateTime(2026, 4, 2),
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var representativePayment = new Payment
        {
            Amount = 30_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 4, 2),
            PlannedDate = new DateTime(2026, 4, 2),
            PaidDate = new DateTime(2026, 4, 2),
            LastPaymentDate = new DateTime(2026, 4, 2),
            PaidAmount = 30_000m,
            Description = "Счет №27",
            DocumentKind = DocumentKind.Invoice,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            Client = client,
            ClientCase = clientCase,
            InvoiceId = invoice.Id,
            Account = invoice.Number,
            AccountDate = invoice.Date,
        };

        var firstInstallment = new Payment
        {
            Amount = 15_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 4, 10),
            PlannedDate = new DateTime(2026, 4, 10),
            PaidDate = new DateTime(2026, 4, 10),
            LastPaymentDate = new DateTime(2026, 4, 10),
            PaidAmount = 15_000m,
            Description = "Платеж 1/2 по счету №27",
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            Client = client,
            ClientCase = clientCase,
            InvoiceId = invoice.Id,
            Account = invoice.Number,
            AccountDate = invoice.Date,
            SystemNotes = $"{InvoiceManagedPaymentRules.InstallmentPaymentMarker} 1",
        };

        var secondInstallment = new Payment
        {
            Amount = 15_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 5, 10),
            PlannedDate = new DateTime(2026, 5, 10),
            Description = "Платеж 2/2 по счету №27",
            Status = PaymentStatus.Pending,
            IsPaid = false,
            Type = PaymentType.Income,
            Client = client,
            ClientCase = clientCase,
            InvoiceId = invoice.Id,
            Account = invoice.Number,
            AccountDate = invoice.Date,
            SystemNotes = $"{InvoiceManagedPaymentRules.InstallmentPaymentMarker} 2",
        };

        db.Payments.AddRange(representativePayment, firstInstallment, secondInstallment);
        await db.SaveChangesAsync();

        return (
            representativePayment.Id,
            new[] { firstInstallment.Id, secondInstallment.Id },
            invoice.Id,
            client.Id,
            clientCase.Id);
    }

    private static async Task<(int InvoicePaymentId, int InvoiceEntityId, int ClientId, int ClientCaseId)> CreateLegacyBrokenInstallmentAggregateAsync(PaymentContext db)
    {
        var client = new Client
        {
            Name = "Безняков Данил Андреевич",
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "БЕЗНЯКОВ - РАССРОЧКА",
        };

        var invoice = new Invoice
        {
            Number = "155",
            Date = new DateTime(2025, 10, 24),
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var representativePayment = new Payment
        {
            Amount = 205_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 8, 25),
            PlannedDate = new DateTime(2026, 8, 25),
            Description = "Счет №155",
            DocumentKind = DocumentKind.Invoice,
            Status = PaymentStatus.Pending,
            IsPaid = false,
            Type = PaymentType.Income,
            Client = client,
            ClientCase = clientCase,
            InvoiceId = invoice.Id,
            Account = invoice.Number,
            AccountDate = invoice.Date,
        };

        var firstInstallment = new Payment
        {
            Amount = 18_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2025, 11, 27),
            PlannedDate = new DateTime(2025, 11, 27),
            PaidDate = new DateTime(2025, 11, 27),
            LastPaymentDate = new DateTime(2025, 11, 27),
            PaidAmount = 18_000m,
            Description = "Платеж 1",
            DocumentKind = DocumentKind.Invoice,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            Client = client,
            ClientCase = clientCase,
            InvoiceId = invoice.Id,
            Account = invoice.Number,
            AccountDate = invoice.Date,
        };

        var secondInstallment = new Payment
        {
            Amount = 18_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 1, 30),
            PlannedDate = new DateTime(2026, 1, 30),
            PaidDate = new DateTime(2026, 1, 30),
            LastPaymentDate = new DateTime(2026, 1, 30),
            PaidAmount = 18_000m,
            Description = "Платеж 2",
            DocumentKind = DocumentKind.Invoice,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            Client = client,
            ClientCase = clientCase,
            InvoiceId = invoice.Id,
            Account = invoice.Number,
            AccountDate = invoice.Date,
        };

        var thirdInstallment = new Payment
        {
            Amount = 18_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 4, 27),
            PlannedDate = new DateTime(2026, 4, 27),
            Description = "Платеж 3",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Pending,
            IsPaid = false,
            Type = PaymentType.Income,
            Client = client,
            ClientCase = clientCase,
            InvoiceId = invoice.Id,
            Account = invoice.Number,
            AccountDate = invoice.Date,
        };

        db.Payments.AddRange(representativePayment, firstInstallment, secondInstallment, thirdInstallment);
        await db.SaveChangesAsync();

        return (representativePayment.Id, invoice.Id, client.Id, clientCase.Id);
    }

    private static InvoicesController CreateInvoicesController(PaymentContext db)
    {
        var controller = new InvoicesController(db, new NoopEmailService(), new NoopFormRequirementsService());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim(ClaimTypes.Role, "admin"),
                    },
                    authenticationType: "Test")),
            },
        };

        return controller;
    }

    private static async Task<PaymentsController> CreatePaymentsControllerAsync(PaymentContext db)
    {
        var adminRole = await db.Roles.SingleOrDefaultAsync(role => role.Name == "admin") ?? new Role { Name = "admin" };
        var actor = new User
        {
            Role = adminRole,
            Email = "invoice-link-writer@test.local",
            FullName = "Invoice link administrator",
            PasswordHash = "unused",
            IsActive = true,
            IsApproved = true,
            IsEmployee = true,
        };
        db.Users.Add(actor);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()),
                        new Claim(ClaimTypes.Name, "Test Admin"),
                        new Claim(ClaimTypes.Role, "admin"),
                    },
                    authenticationType: "Test")),
            },
        };

        return controller;
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

    private sealed class NoopEmailService : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;

        public Task SendEmailWithAttachmentAsync(
            string toEmail,
            string subject,
            string body,
            string attachmentFileName,
            byte[] attachmentBytes,
            string attachmentContentType) => Task.CompletedTask;
    }

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }
}
