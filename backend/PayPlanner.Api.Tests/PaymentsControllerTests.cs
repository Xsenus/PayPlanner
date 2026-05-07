using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class PaymentsControllerTests
{
    private const string InstallmentPaymentMarker = "[pp:installment-payment]";

    [Fact]
    public async Task Get_WithInstallmentFilter_ReturnsSerializableGraph_AndKeepsAuditMetadata()
    {
        await using var db = await CreateContextAsync();

        var clientStatus = new ClientStatusEntity
        {
            Name = "Активный",
            ColorHex = "#22C55E",
        };

        var client = new Client
        {
            Name = "Тестовый клиент",
            Company = "ООО Тест",
            ClientStatus = clientStatus,
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело по рассрочке",
        };

        var incomeType = new IncomeType
        {
            Name = "Оплата по делу",
            PaymentType = PaymentType.Income,
        };

        var invoice = new Invoice
        {
            Number = "INV-25",
            Date = new DateTime(2026, 3, 18),
        };

        var parentPayment = CreatePayment(new DateTime(2026, 3, 18), 25_000m, "Счёт с рассрочкой");
        parentPayment.Client = client;
        parentPayment.ClientCase = clientCase;
        parentPayment.IncomeType = incomeType;
        parentPayment.DocumentKind = DocumentKind.Invoice;
        parentPayment.Invoice = invoice;
        parentPayment.InvoiceId = invoice.Id;
        parentPayment.Account = invoice.Number;
        parentPayment.AccountDate = invoice.Date;

        var installmentPayment = CreatePayment(new DateTime(2026, 3, 18), 12_500m, "Платёж по рассрочке");
        installmentPayment.Client = client;
        installmentPayment.ClientCase = clientCase;
        installmentPayment.IncomeType = incomeType;
        installmentPayment.Invoice = invoice;
        installmentPayment.InvoiceId = invoice.Id;
        installmentPayment.SystemNotes = $"{InstallmentPaymentMarker}\nСоздан автоматически";

        var unrelatedPayment = CreatePayment(new DateTime(2026, 3, 20), 7_500m, "Обычный платёж");
        unrelatedPayment.Client = client;
        unrelatedPayment.ClientCase = clientCase;
        unrelatedPayment.IncomeType = incomeType;

        db.Payments.AddRange(parentPayment, installmentPayment, unrelatedPayment);
        await db.SaveChangesAsync();

        db.Entry(installmentPayment).Property("CreatedByUserName").CurrentValue = "Тестовый автор";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        var actionResult = await controller.Get(
            new DateTime(2026, 3, 1),
            new DateTime(2026, 3, 31),
            null,
            null,
            excludeInstallmentParents: true,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var payments = Assert.IsAssignableFrom<List<Payment>>(ok.Value);

        Assert.Equal(2, payments.Count);
        Assert.DoesNotContain(payments, payment => payment.Id == parentPayment.Id);

        var installmentFromResult = Assert.Single(payments, payment => payment.Id == installmentPayment.Id);
        Assert.Equal("Тестовый автор", installmentFromResult.AuditCreatedByUserName);

        var json = JsonSerializer.Serialize(payments);
        Assert.StartsWith("[", json);
    }

    [Fact]
    public async Task Get_FiltersOnlyByPaymentDateRange_AndIgnoresAccountDate()
    {
        await using var db = await CreateContextAsync();

        var marchPayment = CreatePayment(new DateTime(2026, 3, 31), 5_000m, "Платеж до периода");
        marchPayment.AccountDate = new DateTime(2026, 4, 10);

        var aprilPayment = CreatePayment(new DateTime(2026, 4, 15), 7_500m, "Платеж внутри периода");
        aprilPayment.AccountDate = new DateTime(2025, 12, 20);

        var mayPayment = CreatePayment(new DateTime(2026, 5, 1), 9_000m, "Платеж после периода");
        mayPayment.AccountDate = new DateTime(2025, 8, 13);

        db.Payments.AddRange(marchPayment, aprilPayment, mayPayment);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        var actionResult = await controller.Get(
            new DateTime(2026, 4, 1),
            new DateTime(2026, 4, 30),
            null,
            null,
            excludeInstallmentParents: false,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var payments = Assert.IsAssignableFrom<List<Payment>>(ok.Value);

        var paymentIds = payments.Select(payment => payment.Id).ToList();
        Assert.Equal(new[] { aprilPayment.Id }, paymentIds);
    }

    [Fact]
    public async Task ApplyPaymentFilters_FiltersOnlyByPaymentDateRange_AndIgnoresAccountDate()
    {
        await using var db = await CreateContextAsync();

        var marchPayment = CreatePayment(new DateTime(2026, 3, 31), 5_000m, "Платеж до периода");
        marchPayment.AccountDate = new DateTime(2026, 4, 10);

        var aprilPayment = CreatePayment(new DateTime(2026, 4, 15), 7_500m, "Платеж внутри периода");
        aprilPayment.AccountDate = new DateTime(2025, 12, 20);

        var mayPayment = CreatePayment(new DateTime(2026, 5, 1), 9_000m, "Платеж после периода");
        mayPayment.AccountDate = new DateTime(2025, 8, 13);

        db.Payments.AddRange(marchPayment, aprilPayment, mayPayment);
        await db.SaveChangesAsync();

        var paymentIds = await db.Payments
            .AsNoTracking()
            .ApplyPaymentFilters(
                new DateTime(2026, 4, 1),
                new DateTime(2026, 4, 30),
                null,
                null,
                null)
            .OrderBy(payment => payment.Date)
            .Select(payment => payment.Id)
            .ToListAsync();

        Assert.Equal(new[] { aprilPayment.Id }, paymentIds);
    }

    [Fact]
    public async Task Get_WithCaseId_IncludesPaymentsLinkedThroughInvoiceCases()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "Клиент по делу",
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с привязкой через счет",
        };

        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var invoice = new Invoice
        {
            Number = "30",
            Date = new DateTime(2026, 4, 8),
        };

        var invoicePayment = CreatePayment(new DateTime(2026, 4, 8), 18_000m, "Счет №30");
        invoicePayment.Client = client;
        invoicePayment.DocumentKind = DocumentKind.Invoice;
        invoicePayment.Invoice = invoice;
        invoicePayment.InvoiceId = invoice.Id;
        invoicePayment.Account = invoice.Number;
        invoicePayment.AccountDate = invoice.Date;

        var linkedPayment = CreatePayment(new DateTime(2026, 4, 9), 18_000m, "Рассрочка 1/3");
        linkedPayment.Client = client;
        linkedPayment.Invoice = invoice;
        linkedPayment.InvoiceId = invoice.Id;
        linkedPayment.ClientCaseId = null;
        linkedPayment.DocumentKind = DocumentKind.Payment;

        db.Payments.AddRange(invoicePayment, linkedPayment);
        await db.SaveChangesAsync();

        db.InvoiceClientCases.Add(new InvoiceClientCase
        {
            PaymentId = linkedPayment.Id,
            ClientCaseId = clientCase.Id,
        });
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        var actionResult = await controller.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            clientId: client.Id,
            caseId: clientCase.Id,
            excludeInstallmentParents: false,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var payments = Assert.IsAssignableFrom<List<Payment>>(ok.Value);

        Assert.Collection(
            payments,
            payment => Assert.Equal(linkedPayment.Id, payment.Id));
    }

    [Fact]
    public async Task PaymentsV2_ExcludesParentInvoice_WhenInstallmentScheduleExists()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "Тестовый клиент",
            Company = "ООО Тест",
        };

        var invoice = new Invoice
        {
            Number = "31",
            Date = new DateTime(2026, 4, 9),
        };

        var parentPayment = CreatePayment(new DateTime(2026, 4, 9), 60_000m, "Счет с рассрочкой");
        parentPayment.Client = client;
        parentPayment.DocumentKind = DocumentKind.Invoice;
        parentPayment.Invoice = invoice;
        parentPayment.InvoiceId = invoice.Id;
        parentPayment.Account = invoice.Number;
        parentPayment.AccountDate = invoice.Date;

        var installmentOne = CreatePayment(new DateTime(2026, 4, 10), 20_000m, "Платеж 1/3");
        installmentOne.Client = client;
        installmentOne.Invoice = invoice;
        installmentOne.InvoiceId = invoice.Id;
        installmentOne.SystemNotes = $"{InstallmentPaymentMarker} 1";

        var installmentTwo = CreatePayment(new DateTime(2026, 5, 10), 20_000m, "Платеж 2/3");
        installmentTwo.Client = client;
        installmentTwo.Invoice = invoice;
        installmentTwo.InvoiceId = invoice.Id;
        installmentTwo.SystemNotes = $"{InstallmentPaymentMarker} 2";

        db.Payments.AddRange(parentPayment, installmentOne, installmentTwo);
        await db.SaveChangesAsync();

        var controller = new PaymentsV2Controller(db);

        var actionResult = await controller.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 5, 31),
            clientId: client.Id,
            caseId: null,
            contractId: null,
            excludeInstallmentParents: true,
            excludeInvoiceDocuments: false,
            clientIds: null,
            categoryTokens: null,
            type: null,
            status: null,
            minAmount: null,
            maxAmount: null,
            search: null,
            sortBy: "date",
            sortDir: "asc",
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<PaymentsPageResponse>(ok.Value);

        Assert.Equal(2, response.Total);
        Assert.Equal(2, response.Items.Count);
        Assert.DoesNotContain(response.Items, payment => payment.Id == parentPayment.Id);
        Assert.Equal(40_000m, response.Summary.Income);
        Assert.Equal(2, response.Summary.IncomeCount);
    }

    [Fact]
    public async Task PaymentsV2_FiltersByContractId_UsingCanonicalAndLegacyLinks_AndExcludesInvoiceDocuments()
    {
        await using var db = await CreateContextAsync();

        var contract = await CreateContractAsync(db, "CTR-PAY-V2-1");
        var invoice = new Invoice
        {
            Number = "INV-PAY-V2-1",
            Date = new DateTime(2026, 4, 9),
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var canonicalPayment = CreatePayment(new DateTime(2026, 4, 10), 5_000m, "Канонический платеж");
        canonicalPayment.ContractId = contract.Id;

        var legacyPayment = CreatePayment(new DateTime(2026, 4, 11), 6_000m, "Legacy платеж");

        var invoiceDocument = CreatePayment(new DateTime(2026, 4, 12), 7_000m, "Счет-документ");
        invoiceDocument.DocumentKind = DocumentKind.Invoice;
        invoiceDocument.InvoiceId = invoice.Id;
        invoiceDocument.Account = invoice.Number;
        invoiceDocument.AccountDate = invoice.Date;
        invoiceDocument.ContractId = contract.Id;

        var unrelatedPayment = CreatePayment(new DateTime(2026, 4, 13), 8_000m, "Посторонний платеж");

        db.Payments.AddRange(canonicalPayment, legacyPayment, invoiceDocument, unrelatedPayment);
        await db.SaveChangesAsync();

        db.ContractPayments.Add(new ContractPayment
        {
            ContractId = contract.Id,
            PaymentId = legacyPayment.Id,
        });
        await db.SaveChangesAsync();

        var controller = new PaymentsV2Controller(db);

        var actionResult = await controller.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            clientId: null,
            caseId: null,
            contractId: contract.Id,
            excludeInstallmentParents: false,
            excludeInvoiceDocuments: true,
            clientIds: null,
            categoryTokens: null,
            type: null,
            status: null,
            minAmount: null,
            maxAmount: null,
            search: null,
            sortBy: "date",
            sortDir: "asc",
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<PaymentsPageResponse>(ok.Value);

        Assert.Equal(2, response.Total);
        Assert.Equal(new[] { canonicalPayment.Id, legacyPayment.Id }, response.Items.Select(item => item.Id).OrderBy(id => id).ToArray());
        Assert.Equal(11_000m, response.Summary.Income);
        Assert.DoesNotContain(response.Items, item => item.Id == invoiceDocument.Id);
        Assert.DoesNotContain(response.Items, item => item.Id == unrelatedPayment.Id);
    }

    [Fact]
    public async Task PaymentsV2_FiltersExpensePaymentsByClientRole()
    {
        await using var db = await CreateContextAsync();

        var supplier = new Client { Name = "Supplier", CreatedAt = DateTime.UtcNow };
        var expenseClient = new Client { Name = "Expense Client", CreatedAt = DateTime.UtcNow };
        var unrelated = new Client { Name = "Unrelated", CreatedAt = DateTime.UtcNow };
        db.Clients.AddRange(supplier, expenseClient, unrelated);
        await db.SaveChangesAsync();

        var supplierPayment = CreatePayment(new DateTime(2026, 4, 12), 5_000m, "Supplier payment");
        supplierPayment.Type = PaymentType.Expense;
        supplierPayment.ClientId = supplier.Id;
        supplierPayment.ExpenseClientId = expenseClient.Id;

        var expenseClientPayment = CreatePayment(new DateTime(2026, 4, 13), 7_000m, "Expense client payment");
        expenseClientPayment.Type = PaymentType.Expense;
        expenseClientPayment.ClientId = unrelated.Id;
        expenseClientPayment.ExpenseClientId = supplier.Id;

        db.Payments.AddRange(supplierPayment, expenseClientPayment);
        await db.SaveChangesAsync();

        var controller = new PaymentsV2Controller(db);

        var supplierResult = await controller.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            clientId: supplier.Id,
            caseId: null,
            contractId: null,
            excludeInstallmentParents: false,
            excludeInvoiceDocuments: false,
            clientIds: null,
            clientRole: "counterparty",
            categoryTokens: null,
            type: PaymentType.Expense,
            status: null,
            minAmount: null,
            maxAmount: null,
            search: null,
            sortBy: "date",
            sortDir: "asc",
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var supplierOk = Assert.IsType<OkObjectResult>(supplierResult.Result);
        var supplierResponse = Assert.IsType<PaymentsPageResponse>(supplierOk.Value);
        Assert.Collection(supplierResponse.Items, payment => Assert.Equal(supplierPayment.Id, payment.Id));

        var expenseClientResult = await controller.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            clientId: supplier.Id,
            caseId: null,
            contractId: null,
            excludeInstallmentParents: false,
            excludeInvoiceDocuments: false,
            clientIds: null,
            clientRole: "expenseClient",
            categoryTokens: null,
            type: PaymentType.Expense,
            status: null,
            minAmount: null,
            maxAmount: null,
            search: null,
            sortBy: "date",
            sortDir: "asc",
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var expenseClientOk = Assert.IsType<OkObjectResult>(expenseClientResult.Result);
        var expenseClientResponse = Assert.IsType<PaymentsPageResponse>(expenseClientOk.Value);
        Assert.Collection(expenseClientResponse.Items, payment => Assert.Equal(expenseClientPayment.Id, payment.Id));
    }

    [Fact]
    public async Task PaymentsV2_FiltersByLegalEntityAndRole()
    {
        await using var db = await CreateContextAsync();

        var supplier = new LegalEntity { ShortName = "Supplier Legal" };
        var expenseOwner = new LegalEntity { ShortName = "Expense Legal" };
        var unrelated = new LegalEntity { ShortName = "Unrelated Legal" };
        db.LegalEntities.AddRange(supplier, expenseOwner, unrelated);
        await db.SaveChangesAsync();

        var supplierPayment = CreatePayment(new DateTime(2026, 4, 12), 5_000m, "Legal supplier payment");
        supplierPayment.Type = PaymentType.Expense;
        supplierPayment.LegalEntityId = supplier.Id;
        supplierPayment.ExpenseLegalEntityId = expenseOwner.Id;

        var expenseOwnerPayment = CreatePayment(new DateTime(2026, 4, 13), 7_000m, "Legal expense owner payment");
        expenseOwnerPayment.Type = PaymentType.Expense;
        expenseOwnerPayment.LegalEntityId = unrelated.Id;
        expenseOwnerPayment.ExpenseLegalEntityId = supplier.Id;

        db.Payments.AddRange(supplierPayment, expenseOwnerPayment);
        await db.SaveChangesAsync();

        var controller = new PaymentsV2Controller(db);

        var allResult = await controller.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            clientId: null,
            caseId: null,
            legalEntityId: supplier.Id,
            contractId: null,
            excludeInstallmentParents: false,
            excludeInvoiceDocuments: false,
            clientIds: null,
            clientRole: null,
            categoryTokens: null,
            type: PaymentType.Expense,
            status: null,
            minAmount: null,
            maxAmount: null,
            search: null,
            sortBy: "date",
            sortDir: "asc",
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var allOk = Assert.IsType<OkObjectResult>(allResult.Result);
        var allResponse = Assert.IsType<PaymentsPageResponse>(allOk.Value);
        Assert.Equal(new[] { supplierPayment.Id, expenseOwnerPayment.Id }, allResponse.Items.Select(payment => payment.Id).ToArray());

        var supplierResult = await controller.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            clientId: null,
            caseId: null,
            legalEntityId: supplier.Id,
            contractId: null,
            excludeInstallmentParents: false,
            excludeInvoiceDocuments: false,
            clientIds: null,
            clientRole: "counterparty",
            categoryTokens: null,
            type: PaymentType.Expense,
            status: null,
            minAmount: null,
            maxAmount: null,
            search: null,
            sortBy: "date",
            sortDir: "asc",
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var supplierOk = Assert.IsType<OkObjectResult>(supplierResult.Result);
        var supplierResponse = Assert.IsType<PaymentsPageResponse>(supplierOk.Value);
        Assert.Collection(supplierResponse.Items, payment => Assert.Equal(supplierPayment.Id, payment.Id));

        var expenseResult = await controller.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            clientId: null,
            caseId: null,
            legalEntityId: supplier.Id,
            contractId: null,
            excludeInstallmentParents: false,
            excludeInvoiceDocuments: false,
            clientIds: null,
            clientRole: "expenseClient",
            categoryTokens: null,
            type: PaymentType.Expense,
            status: null,
            minAmount: null,
            maxAmount: null,
            search: null,
            sortBy: "date",
            sortDir: "asc",
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var expenseOk = Assert.IsType<OkObjectResult>(expenseResult.Result);
        var expenseResponse = Assert.IsType<PaymentsPageResponse>(expenseOk.Value);
        Assert.Collection(expenseResponse.Items, payment => Assert.Equal(expenseOwnerPayment.Id, payment.Id));
    }

    [Fact]
    public async Task Delete_RemovesOnlyStandalonePayment_WhenPaymentIsNotPartOfInvoiceAggregate()
    {
        await using var db = await CreateContextAsync();

        var standalonePayment = CreatePayment(new DateTime(2026, 4, 20), 4_500m, "Обычный платеж");
        var unrelatedPayment = CreatePayment(new DateTime(2026, 4, 21), 7_500m, "Последующий платеж");

        db.Payments.AddRange(standalonePayment, unrelatedPayment);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        var actionResult = await controller.Delete(standalonePayment.Id, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.IsType<NoContentResult>(actionResult);
        Assert.Null(await db.Payments.FindAsync(standalonePayment.Id));
        Assert.NotNull(await db.Payments.FindAsync(unrelatedPayment.Id));
    }

    [Fact]
    public async Task Delete_RejectsInvoiceDocument_WhenTargetIsInvoiceManagedPayment()
    {
        await using var db = await CreateContextAsync();
        var invoice = await CreateInvoiceAggregateAsync(db);

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        var actionResult = await controller.Delete(invoice.ParentPaymentId, CancellationToken.None);
        db.ChangeTracker.Clear();

        var conflict = Assert.IsType<ConflictObjectResult>(actionResult);
        Assert.Contains(InvoiceManagedPaymentRules.DeleteBlockedMessage, conflict.Value?.ToString());
        Assert.NotNull(await db.Payments.FindAsync(invoice.ParentPaymentId));
        Assert.Equal(3, await db.Payments.CountAsync(payment => payment.InvoiceId == invoice.InvoiceEntityId));
        Assert.NotNull(await db.Invoices.FindAsync(invoice.InvoiceEntityId));
        Assert.NotNull(await db.Payments.FindAsync(invoice.UnrelatedPaymentId));
    }

    [Fact]
    public async Task Delete_RejectsGeneratedInstallmentPayment_WhenTargetIsInvoiceManagedPayment()
    {
        await using var db = await CreateContextAsync();
        var invoice = await CreateInvoiceAggregateAsync(db);

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        var actionResult = await controller.Delete(invoice.InstallmentPaymentIds[0], CancellationToken.None);
        db.ChangeTracker.Clear();

        var conflict = Assert.IsType<ConflictObjectResult>(actionResult);
        Assert.Contains(InvoiceManagedPaymentRules.DeleteBlockedMessage, conflict.Value?.ToString());
        Assert.NotNull(await db.Payments.FindAsync(invoice.ParentPaymentId));
        Assert.Equal(3, await db.Payments.CountAsync(payment => payment.InvoiceId == invoice.InvoiceEntityId));
        Assert.NotNull(await db.Invoices.FindAsync(invoice.InvoiceEntityId));
        Assert.NotNull(await db.Payments.FindAsync(invoice.UnrelatedPaymentId));
    }

    [Fact]
    public async Task Update_RejectsGeneratedInstallmentPayment_WhenTargetIsInvoiceManagedPayment()
    {
        await using var db = await CreateContextAsync();
        var invoice = await CreateInvoiceAggregateAsync(db);
        var installmentPayment = await db.Payments.FindAsync(invoice.InstallmentPaymentIds[0]);
        Assert.NotNull(installmentPayment);

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        installmentPayment!.Description = "Updated";

        var actionResult = await controller.Update(
            installmentPayment.Id,
            installmentPayment,
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(actionResult);
        Assert.Contains(InvoiceManagedPaymentRules.UpdateBlockedMessage, conflict.Value?.ToString());
    }

    [Fact]
    public async Task UpdateInvoiceManaged_UpdatesGeneratedInstallmentPayment_AndPreservesInvoiceLink()
    {
        await using var db = await CreateContextAsync();
        var invoice = await CreateInvoiceAggregateAsync(db);
        var installmentPayment = await db.Payments.FindAsync(invoice.InstallmentPaymentIds[0]);
        Assert.NotNull(installmentPayment);

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var paidDate = new DateTime(2026, 4, 12);
        var model = CreatePayment(new DateTime(2026, 4, 12), installmentPayment!.Amount, "Updated installment payment");
        model.PlannedDate = new DateTime(2026, 4, 11);
        model.PaidAmount = installmentPayment.Amount;
        model.PaidDate = paidDate;
        model.LastPaymentDate = paidDate;
        model.Status = PaymentStatus.Completed;
        model.InvoiceId = null;
        model.Account = "CHANGED";
        model.AccountDate = new DateTime(2027, 1, 1);
        model.SystemNotes = installmentPayment.SystemNotes;

        var actionResult = await controller.UpdateInvoiceManaged(
            installmentPayment.Id,
            model,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var updated = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(invoice.InvoiceEntityId, updated.InvoiceId);
        Assert.Equal("30", updated.Account);
        Assert.Contains(InstallmentPaymentMarker, updated.SystemNotes);
        Assert.Equal("Updated installment payment", updated.Description);
        Assert.Equal(PaymentStatus.Completed, updated.Status);
        Assert.True(updated.IsPaid);
        Assert.Equal(paidDate, updated.PaidDate);
    }

    [Fact]
    public async Task UpdateInvoiceManaged_SyncsGeneratedInstallmentBindingsFromRepresentative()
    {
        await using var db = await CreateContextAsync();

        var invoiceClient = new Client { Name = "Correct invoice client" };
        var staleClient = new Client { Name = "Stale installment client" };
        var staleCase = new ClientCase { Client = staleClient, Title = "Stale installment case" };
        var invoice = new Invoice { Number = "166", Date = new DateTime(2025, 9, 16) };
        db.AddRange(invoiceClient, staleClient, staleCase, invoice);
        await db.SaveChangesAsync();

        var representative = CreatePayment(new DateTime(2025, 11, 24), 332_000m, "Invoice 166");
        representative.DocumentKind = DocumentKind.Invoice;
        representative.InvoiceId = invoice.Id;
        representative.Account = invoice.Number;
        representative.AccountDate = invoice.Date;
        representative.ClientId = invoiceClient.Id;
        representative.ClientCaseId = null;

        var installment = CreatePayment(new DateTime(2026, 4, 30), 20_000m, "Installment 1/17");
        installment.InvoiceId = invoice.Id;
        installment.Account = invoice.Number;
        installment.AccountDate = invoice.Date;
        installment.ClientId = staleClient.Id;
        installment.ClientCaseId = staleCase.Id;
        installment.SystemNotes = $"{InstallmentPaymentMarker} {{\"Sequence\":1}}";
        db.Payments.AddRange(representative, installment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var update = CreatePayment(new DateTime(2026, 5, 1), installment.Amount, "Updated installment");
        update.ClientId = staleClient.Id;
        update.ClientCaseId = staleCase.Id;
        update.InvoiceId = null;
        update.Account = "BROKEN";
        update.AccountDate = new DateTime(2030, 1, 1);
        update.SystemNotes = installment.SystemNotes;

        var actionResult = await controller.UpdateInvoiceManaged(installment.Id, update, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var updated = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(invoice.Id, updated.InvoiceId);
        Assert.Equal(invoice.Number, updated.Account);
        Assert.Equal(invoice.Date, updated.AccountDate);
        Assert.Equal(invoiceClient.Id, updated.ClientId);
        Assert.Null(updated.ClientCaseId);
        Assert.Equal("Updated installment", updated.Description);
        Assert.Equal(new DateTime(2026, 5, 1), updated.Date);

        db.ChangeTracker.Clear();
        var stored = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == installment.Id);
        Assert.Equal(invoiceClient.Id, stored.ClientId);
        Assert.Null(stored.ClientCaseId);
        Assert.Equal(invoice.Number, stored.Account);
        Assert.Equal(invoice.Date, stored.AccountDate);
    }

    [Fact]
    public async Task UpdateInvoiceManaged_UpdatesRepresentativeInvoicePayment_AndPreservesDocumentKind()
    {
        await using var db = await CreateContextAsync();
        var invoice = await CreateInvoiceAggregateAsync(db);
        var parentPayment = await db.Payments.FindAsync(invoice.ParentPaymentId);
        Assert.NotNull(parentPayment);

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var paidDate = new DateTime(2026, 4, 15);
        var model = CreatePayment(parentPayment!.Date, parentPayment.Amount, "Updated representative payment");
        model.PlannedDate = parentPayment.PlannedDate;
        model.PaidAmount = parentPayment.Amount;
        model.PaidDate = paidDate;
        model.LastPaymentDate = paidDate;
        model.Status = PaymentStatus.Completed;
        model.DocumentKind = DocumentKind.Payment;
        model.InvoiceId = null;
        model.Account = "BROKEN";
        model.AccountDate = new DateTime(2027, 2, 2);
        model.SystemNotes = parentPayment.SystemNotes;

        var actionResult = await controller.UpdateInvoiceManaged(
            parentPayment.Id,
            model,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var updated = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(DocumentKind.Invoice, updated.DocumentKind);
        Assert.Equal(invoice.InvoiceEntityId, updated.InvoiceId);
        Assert.Equal("30", updated.Account);
        Assert.Equal("Updated representative payment", updated.Description);
        Assert.Equal(PaymentStatus.Completed, updated.Status);
        Assert.True(updated.IsPaid);
        Assert.Equal(paidDate, updated.PaidDate);
    }

    [Fact]
    public async Task Create_ResolvesInvoiceLinkFromPaymentHistory_AndNormalizesFields()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "Клиент для привязки",
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело для привязки",
        };

        var incomeType = new IncomeType
        {
            Name = "Оплата по счёту",
            PaymentType = PaymentType.Income,
        };

        var invoice = new Invoice
        {
            Number = "INV-400",
            Date = new DateTime(2026, 4, 2),
        };

        db.AddRange(client, clientCase, incomeType, invoice);
        await db.SaveChangesAsync();

        var invoiceDocument = CreatePayment(invoice.Date, 20_000m, "Счёт INV-400");
        invoiceDocument.ClientId = client.Id;
        invoiceDocument.ClientCaseId = clientCase.Id;
        invoiceDocument.DocumentKind = DocumentKind.Invoice;
        invoiceDocument.InvoiceId = invoice.Id;
        invoiceDocument.Account = invoice.Number;
        invoiceDocument.AccountDate = invoice.Date;
        db.Payments.Add(invoiceDocument);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var model = CreatePayment(new DateTime(2026, 4, 3), 5_500m, "Доплата по счёту");
        model.ClientId = client.Id;
        model.ClientCaseId = clientCase.Id;
        model.IncomeTypeId = incomeType.Id;
        model.Account = "  INV-400  ";
        model.AccountDate = invoice.Date;
        model.PaidAmount = 1_500m;
        model.PaidDate = new DateTime(2026, 4, 4);
        model.LastPaymentDate = null;
        model.RescheduleCount = -5;
        model.SystemNotes = null!;
        model.DocumentKind = default;
        var expectedLastPaymentDate = model.PaidDate.Value.Date;

        var actionResult = await controller.Create(model, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(actionResult);
        var payment = Assert.IsType<Payment>(created.Value);
        Assert.Equal(DocumentKind.Payment, payment.DocumentKind);
        Assert.Equal(invoice.Id, payment.InvoiceId);
        Assert.Equal(invoice.Number, payment.Account);
        Assert.Equal(invoice.Date, payment.AccountDate);
        Assert.Equal(expectedLastPaymentDate, payment.LastPaymentDate);
        Assert.Equal(0, payment.RescheduleCount);
        Assert.Equal(incomeType.Id, payment.IncomeTypeId);

        var stored = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == payment.Id);
        Assert.Equal(invoice.Id, stored.InvoiceId);
        Assert.Equal(invoice.Number, stored.Account);
        Assert.Equal(invoice.Date, stored.AccountDate);
        Assert.Equal(expectedLastPaymentDate, stored.LastPaymentDate);
        Assert.Equal(0, stored.RescheduleCount);
    }

    [Fact]
    public async Task Update_StandalonePayment_RewritesInvoiceLink_AndAppendsSystemNotes()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "Клиент для обновления",
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело для обновления",
        };

        var incomeType = new IncomeType
        {
            Name = "Доход",
            PaymentType = PaymentType.Income,
        };

        var paymentSource = new PaymentSource
        {
            Name = "Банк",
        };

        var paymentStatus = new PaymentStatusEntity
        {
            Name = "В работе",
        };

        var dealType = new DealType
        {
            Name = "Сделка",
        };

        var invoice = new Invoice
        {
            Number = "INV-401",
            Date = new DateTime(2026, 4, 8),
        };

        db.AddRange(client, clientCase, incomeType, paymentSource, paymentStatus, dealType, invoice);
        await db.SaveChangesAsync();

        var invoiceDocument = CreatePayment(invoice.Date, 12_000m, "Счёт INV-401");
        invoiceDocument.ClientId = client.Id;
        invoiceDocument.ClientCaseId = clientCase.Id;
        invoiceDocument.DocumentKind = DocumentKind.Invoice;
        invoiceDocument.InvoiceId = invoice.Id;
        invoiceDocument.Account = invoice.Number;
        invoiceDocument.AccountDate = invoice.Date;
        db.Payments.Add(invoiceDocument);

        var standalonePayment = CreatePayment(new DateTime(2026, 4, 5), 4_500m, "Старый платёж");
        standalonePayment.ClientId = client.Id;
        standalonePayment.ClientCaseId = clientCase.Id;
        standalonePayment.IncomeTypeId = incomeType.Id;
        standalonePayment.SystemNotes = "Исходная заметка";
        standalonePayment.RescheduleCount = 2;
        db.Payments.Add(standalonePayment);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var updateModel = new Payment
        {
            Id = standalonePayment.Id,
            Amount = 4_800m,
            CounterpartyName = standalonePayment.CounterpartyName,
            Date = new DateTime(2026, 4, 10),
            PlannedDate = new DateTime(2026, 4, 12),
            Description = "Обновлённый платёж",
            DocumentKind = DocumentKind.Payment,
            Notes = "Новые заметки",
            Status = PaymentStatus.Completed,
            SystemNotes = "Исходная заметка\nРучное примечание",
            Type = PaymentType.Income,
            ClientId = client.Id,
            ClientCaseId = clientCase.Id,
            IncomeTypeId = incomeType.Id,
            PaymentSourceId = paymentSource.Id,
            PaymentStatusId = paymentStatus.Id,
            DealTypeId = dealType.Id,
            InvoiceId = invoice.Id,
            Account = "LEGACY-401",
            AccountDate = invoice.Date.AddDays(1),
            PaidAmount = 4_800m,
            PaidDate = new DateTime(2026, 4, 11),
            LastPaymentDate = null,
        };

        var actionResult = await controller.Update(standalonePayment.Id, updateModel, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var updated = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(invoice.Id, updated.InvoiceId);
        Assert.Equal(invoice.Number, updated.Account);
        Assert.Equal(invoice.Date, updated.AccountDate);
        Assert.Equal("LEGACY-401", updated.LegacyAccountNumber);
        Assert.Equal(invoice.Date.AddDays(1), updated.LegacyAccountDate);
        Assert.Equal(3, updated.RescheduleCount);
        Assert.Equal(paymentSource.Id, updated.PaymentSourceId);
        Assert.Equal(paymentStatus.Id, updated.PaymentStatusId);
        Assert.Equal(dealType.Id, updated.DealTypeId);
        Assert.Equal(updateModel.PaidDate!.Value.Date, updated.LastPaymentDate);
        Assert.Contains("Ручное примечание", updated.SystemNotes);
        Assert.Equal(1, updated.SystemNotes.Split('\n').Count(line => line.Contains("Исходная")));

        var stored = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == standalonePayment.Id);
        Assert.Equal(invoice.Id, stored.InvoiceId);
        Assert.Equal(invoice.Number, stored.Account);
        Assert.Equal("LEGACY-401", stored.LegacyAccountNumber);
        Assert.Equal(3, stored.RescheduleCount);
    }

    [Fact]
    public async Task Update_PersistsContractId_ForStandalonePayment()
    {
        await using var db = await CreateContextAsync();

        var contract = await CreateContractAsync(db, "CTR-PAY-UPD-1");
        var payment = CreatePayment(new DateTime(2026, 4, 6), 3_500m, "Платеж для договора");
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var updateModel = new Payment
        {
            Amount = payment.Amount,
            CounterpartyName = payment.CounterpartyName,
            Date = payment.Date,
            PlannedDate = payment.PlannedDate,
            Description = payment.Description,
            DocumentKind = DocumentKind.Payment,
            Notes = payment.Notes,
            Status = payment.Status,
            SystemNotes = payment.SystemNotes,
            Type = payment.Type,
            ContractId = contract.Id,
        };

        var actionResult = await controller.Update(payment.Id, updateModel, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var updated = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(contract.Id, updated.ContractId);

        var stored = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == payment.Id);
        Assert.Equal(contract.Id, stored.ContractId);
    }

    [Fact]
    public async Task PaymentsV2_Update_PersistsContractId_ForStandalonePayment()
    {
        await using var db = await CreateContextAsync();

        var contract = await CreateContractAsync(db, "CTR-PAY-UPD-2");
        var payment = CreatePayment(new DateTime(2026, 4, 7), 4_200m, "V2 платеж для договора");
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var controller = new PaymentsV2Controller(db);
        var updateModel = new Payment
        {
            Amount = payment.Amount,
            CounterpartyName = payment.CounterpartyName,
            Date = payment.Date,
            PlannedDate = payment.PlannedDate,
            Description = payment.Description,
            DocumentKind = DocumentKind.Payment,
            Notes = payment.Notes,
            Status = payment.Status,
            SystemNotes = payment.SystemNotes,
            Type = payment.Type,
            ContractId = contract.Id,
        };

        var actionResult = await controller.Update(payment.Id, updateModel);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var updated = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(contract.Id, updated.ContractId);

        var stored = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == payment.Id);
        Assert.Equal(contract.Id, stored.ContractId);
    }

    [Fact]
    public async Task PaymentsV2_Create_LinkedPayment_RefreshesParentInvoiceAsPartiallyPaid()
    {
        await using var db = await CreateContextAsync();

        var client = new Client { Name = "V2 invoice client", CreatedAt = DateTime.UtcNow };
        var invoice = new Invoice { Number = "V2-LINK-1", Date = DateTime.UtcNow.Date.AddDays(7) };
        db.Clients.Add(client);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var parentPayment = CreatePayment(invoice.Date, 10_000m, "V2 parent invoice");
        parentPayment.ClientId = client.Id;
        parentPayment.DocumentKind = DocumentKind.Invoice;
        parentPayment.InvoiceId = invoice.Id;
        parentPayment.Account = invoice.Number;
        parentPayment.AccountDate = invoice.Date;
        db.Payments.Add(parentPayment);
        await db.SaveChangesAsync();

        var controller = new PaymentsV2Controller(db);
        var linkedPayment = CreatePayment(invoice.Date.AddDays(1), 4_000m, "V2 linked payment");
        linkedPayment.ClientId = client.Id;
        linkedPayment.InvoiceId = invoice.Id;
        linkedPayment.Status = PaymentStatus.Completed;
        linkedPayment.PaidAmount = 4_000m;
        linkedPayment.PaidDate = linkedPayment.Date;
        linkedPayment.LastPaymentDate = linkedPayment.Date;

        var actionResult = await controller.Create(linkedPayment, CancellationToken.None);

        Assert.IsType<CreatedResult>(actionResult);
        db.ChangeTracker.Clear();

        var refreshedParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == parentPayment.Id);
        Assert.Equal(4_000m, refreshedParent.PaidAmount);
        Assert.Equal(PaymentStatus.Processing, refreshedParent.Status);
        Assert.Equal(InvoiceWorkflowStatus.PartiallyPaid, refreshedParent.InvoiceWorkflowStatus);
        Assert.False(refreshedParent.IsPaid);
    }

    [Fact]
    public async Task PaymentsV2_Update_LinkedPayment_RefreshesParentInvoiceAsPaid()
    {
        await using var db = await CreateContextAsync();

        var client = new Client { Name = "V2 paid invoice client", CreatedAt = DateTime.UtcNow };
        var invoice = new Invoice { Number = "V2-LINK-2", Date = DateTime.UtcNow.Date.AddDays(7) };
        db.Clients.Add(client);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var parentPayment = CreatePayment(invoice.Date, 10_000m, "V2 parent invoice to pay");
        parentPayment.ClientId = client.Id;
        parentPayment.DocumentKind = DocumentKind.Invoice;
        parentPayment.InvoiceId = invoice.Id;
        parentPayment.Account = invoice.Number;
        parentPayment.AccountDate = invoice.Date;

        var linkedPayment = CreatePayment(invoice.Date.AddDays(1), 10_000m, "V2 linked payment to update");
        linkedPayment.ClientId = client.Id;
        linkedPayment.InvoiceId = invoice.Id;
        linkedPayment.Account = invoice.Number;
        linkedPayment.AccountDate = invoice.Date;

        db.Payments.AddRange(parentPayment, linkedPayment);
        await db.SaveChangesAsync();

        var controller = new PaymentsV2Controller(db);
        var update = CreatePayment(linkedPayment.Date, linkedPayment.Amount, linkedPayment.Description);
        update.ClientId = client.Id;
        update.Status = PaymentStatus.Completed;
        update.PaidAmount = linkedPayment.Amount;
        update.PaidDate = linkedPayment.Date;
        update.LastPaymentDate = linkedPayment.Date;

        var actionResult = await controller.Update(linkedPayment.Id, update, CancellationToken.None);

        Assert.IsType<OkObjectResult>(actionResult);
        db.ChangeTracker.Clear();

        var refreshedParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == parentPayment.Id);
        Assert.Equal(10_000m, refreshedParent.PaidAmount);
        Assert.Equal(PaymentStatus.Completed, refreshedParent.Status);
        Assert.Equal(InvoiceWorkflowStatus.Paid, refreshedParent.InvoiceWorkflowStatus);
        Assert.True(refreshedParent.IsPaid);
    }

    [Fact]
    public async Task RepairInvoiceLinks_ReassignsLinkedPayments_AndDowngradesDuplicateInvoiceDocuments()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "Клиент для восстановления",
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело для repair",
        };

        var invoice = new Invoice
        {
            Number = "INV-402",
            Date = new DateTime(2026, 4, 15),
        };

        db.AddRange(client, clientCase, invoice);
        await db.SaveChangesAsync();

        var primaryInvoiceDocument = CreatePayment(invoice.Date, 18_000m, "Основной чек");
        primaryInvoiceDocument.ClientId = client.Id;
        primaryInvoiceDocument.ClientCaseId = clientCase.Id;
        primaryInvoiceDocument.DocumentKind = DocumentKind.Invoice;
        primaryInvoiceDocument.InvoiceId = invoice.Id;
        primaryInvoiceDocument.Account = invoice.Number;
        primaryInvoiceDocument.AccountDate = invoice.Date;
        primaryInvoiceDocument.CreatedAt = new DateTime(2026, 4, 15, 8, 0, 0, DateTimeKind.Utc);

        var duplicateInvoiceDocument = CreatePayment(invoice.Date, 18_000m, "Дубль чека");
        duplicateInvoiceDocument.ClientId = client.Id;
        duplicateInvoiceDocument.ClientCaseId = clientCase.Id;
        duplicateInvoiceDocument.DocumentKind = DocumentKind.Invoice;
        duplicateInvoiceDocument.InvoiceId = invoice.Id;
        duplicateInvoiceDocument.Account = invoice.Number;
        duplicateInvoiceDocument.AccountDate = invoice.Date;
        duplicateInvoiceDocument.CreatedAt = new DateTime(2026, 4, 15, 9, 0, 0, DateTimeKind.Utc);

        var orphanPayment = CreatePayment(new DateTime(2026, 4, 16), 6_000m, "Оплата без привязки");
        orphanPayment.ClientId = client.Id;
        orphanPayment.ClientCaseId = clientCase.Id;
        orphanPayment.DocumentKind = DocumentKind.Payment;
        orphanPayment.Account = invoice.Number;
        orphanPayment.AccountDate = invoice.Date;

        db.Payments.AddRange(primaryInvoiceDocument, duplicateInvoiceDocument, orphanPayment);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        var actionResult = await controller.RepairInvoiceLinks(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        Assert.NotNull(ok.Value);

        var repairedPayment = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == orphanPayment.Id);
        var correctedDuplicate = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == duplicateInvoiceDocument.Id);
        var keptPrimary = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == primaryInvoiceDocument.Id);

        Assert.Equal(invoice.Id, repairedPayment.InvoiceId);
        Assert.Equal(invoice.Number, repairedPayment.Account);
        Assert.Equal(invoice.Date, repairedPayment.AccountDate);
        Assert.Equal(DocumentKind.Payment, correctedDuplicate.DocumentKind);
        Assert.Equal(DocumentKind.Invoice, keptPrimary.DocumentKind);
    }

    [Fact]
    public async Task GetById_ReturnsNotFound_WhenPaymentMissing()
    {
        await using var db = await CreateContextAsync();
        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        var actionResult = await controller.GetById(99_001, CancellationToken.None);

        Assert.IsType<NotFoundResult>(actionResult);
    }

    [Fact]
    public async Task GetById_ReturnsProjectedPayment_WhenPaymentExists()
    {
        await using var db = await CreateContextAsync();

        var payment = CreatePayment(new DateTime(2026, 4, 28), 6_500m, "Платёж для GetById");
        payment.SystemNotes = "Системная заметка";
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        var actionResult = await controller.GetById(payment.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var result = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(payment.Id, result.Id);
        Assert.Equal("Платёж для GetById", result.Description);
        Assert.Equal(6_500m, result.Amount);
    }

    [Fact]
    public async Task Update_WhenStoredPlannedDateIsDefault_PreservesOriginalDueDateAsPlanned()
    {
        await using var db = await CreateContextAsync();

        var originalDate = new DateTime(2099, 5, 10);
        var payment = CreatePayment(originalDate, 8_000m, "Платеж без planned date");
        payment.PlannedDate = default;
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var updateModel = new Payment
        {
            Amount = 8_500m,
            CounterpartyName = payment.CounterpartyName,
            Date = originalDate.AddDays(3),
            PlannedDate = default,
            Description = "Обновленный платеж",
            DocumentKind = DocumentKind.Payment,
            Notes = string.Empty,
            Status = PaymentStatus.Pending,
            SystemNotes = string.Empty,
            Type = PaymentType.Income,
        };

        var actionResult = await controller.Update(payment.Id, updateModel, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var updated = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(originalDate, updated.PlannedDate);
        Assert.Equal(originalDate.AddDays(3), updated.Date);

        var stored = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == payment.Id);
        Assert.Equal(originalDate, stored.PlannedDate);
    }

    [Fact]
    public async Task Update_WithPositivePaidAmountAndNoPaidDates_LeavesLastPaymentDateEmpty()
    {
        await using var db = await CreateContextAsync();

        var payment = CreatePayment(new DateTime(2099, 6, 1), 10_000m, "Частично оплаченный платеж");
        payment.LastPaymentDate = null;
        payment.PaidDate = null;
        payment.PaidAmount = 0m;
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var updateModel = new Payment
        {
            Amount = 10_000m,
            CounterpartyName = payment.CounterpartyName,
            Date = payment.Date,
            PlannedDate = payment.PlannedDate,
            Description = payment.Description,
            DocumentKind = DocumentKind.Payment,
            Notes = string.Empty,
            Status = PaymentStatus.Pending,
            SystemNotes = string.Empty,
            Type = PaymentType.Income,
            PaidAmount = 2_500m,
            PaidDate = null,
            LastPaymentDate = null,
        };

        var actionResult = await controller.Update(payment.Id, updateModel, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var updated = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(2_500m, updated.PaidAmount);
        Assert.Null(updated.LastPaymentDate);

        var stored = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == payment.Id);
        Assert.Null(stored.LastPaymentDate);
    }

    [Fact]
    public async Task Update_WhenInvoiceIsFullyPaid_AutoCorrectsDate_AndAddsSystemNote()
    {
        await using var db = await CreateContextAsync();

        var originalDate = new DateTime(2099, 7, 10);
        var payment = CreatePayment(originalDate, 10_000m, "Полная оплата со сдвигом даты");
        payment.PaidAmount = 0m;
        payment.PaidDate = null;
        payment.LastPaymentDate = null;
        payment.SystemNotes = string.Empty;
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var updateModel = new Payment
        {
            Amount = 10_000m,
            CounterpartyName = payment.CounterpartyName,
            Date = originalDate,
            PlannedDate = payment.PlannedDate,
            Description = payment.Description,
            DocumentKind = DocumentKind.Payment,
            Notes = string.Empty,
            Status = PaymentStatus.Pending,
            SystemNotes = string.Empty,
            Type = PaymentType.Income,
            PaidAmount = 10_000m,
            PaidDate = new DateTime(2099, 7, 15),
            LastPaymentDate = null,
        };

        var actionResult = await controller.Update(payment.Id, updateModel, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var updated = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(new DateTime(2099, 7, 15), updated.Date);
        Assert.Equal(PaymentStatus.Completed, updated.Status);
        Assert.Equal(payment.RescheduleCount, updated.RescheduleCount);
        Assert.Contains("автоматически скорректирована", updated.SystemNotes, StringComparison.OrdinalIgnoreCase);

        var stored = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == payment.Id);
        Assert.Equal(new DateTime(2099, 7, 15), stored.Date);
        Assert.Equal(PaymentStatus.Completed, stored.Status);
        Assert.Equal(0, stored.RescheduleCount);
        Assert.Contains("автоматически скорректирована", stored.SystemNotes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepairInvoiceLinks_ClearsBrokenInvoiceReference_AndReportsClearedLinks()
    {
        await using var db = await CreateContextAsync();

        var invoice = new Invoice
        {
            Number = "INV-404",
            Date = new DateTime(2026, 4, 18),
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var brokenPayment = CreatePayment(new DateTime(2026, 4, 18), 3_000m, "Платеж с битой ссылкой");
        brokenPayment.InvoiceId = invoice.Id;
        brokenPayment.Account = null;
        brokenPayment.AccountDate = new DateTime(2026, 4, 18);
        brokenPayment.LegacyAccountNumber = "LEGACY-404";
        brokenPayment.LegacyAccountDate = new DateTime(2026, 4, 17);
        db.Payments.Add(brokenPayment);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Payments SET InvoiceId = {999_999} WHERE Id = {brokenPayment.Id}");
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        db.ChangeTracker.Clear();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        var actionResult = await controller.RepairInvoiceLinks(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var response = ok.Value;
        Assert.NotNull(response);
        Assert.Equal(1, Assert.IsType<int>(response.GetType().GetProperty("scanned")?.GetValue(response)));
        Assert.Equal(0, Assert.IsType<int>(response.GetType().GetProperty("repairedLinks")?.GetValue(response)));
        Assert.Equal(1, Assert.IsType<int>(response.GetType().GetProperty("clearedLinks")?.GetValue(response)));
        Assert.Equal(0, Assert.IsType<int>(response.GetType().GetProperty("correctedKinds")?.GetValue(response)));

        var stored = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == brokenPayment.Id);
        Assert.Null(stored.InvoiceId);
        Assert.Null(stored.Account);
        Assert.Null(stored.AccountDate);
        Assert.Null(stored.LegacyAccountNumber);
        Assert.Null(stored.LegacyAccountDate);
    }

    [Fact]
    public async Task Create_LeavesInvoiceUnlinked_WhenHistoryHasNoMatches()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "Клиент без истории",
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var model = CreatePayment(new DateTime(2026, 4, 19), 4_000m, "Платеж без найденного счета");
        model.ClientId = client.Id;
        model.Account = "INV-NOT-FOUND";
        model.AccountDate = new DateTime(2026, 4, 18);

        var actionResult = await controller.Create(model, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(actionResult);
        var payment = Assert.IsType<Payment>(created.Value);
        Assert.Null(payment.InvoiceId);
        Assert.Equal("INV-NOT-FOUND", payment.Account);
        Assert.Equal(new DateTime(2026, 4, 18), payment.AccountDate);
    }

    [Fact]
    public async Task Create_IgnoresHistoryCandidates_WithZeroInvoiceId()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "Клиент с поврежденной историей",
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var historyPayment = CreatePayment(new DateTime(2026, 4, 20), 6_000m, "Поврежденный исторический платеж");
        historyPayment.ClientId = client.Id;
        historyPayment.Account = "INV-ZERO";
        historyPayment.AccountDate = new DateTime(2026, 4, 20);
        historyPayment.InvoiceId = null;
        db.Payments.Add(historyPayment);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Payments SET InvoiceId = {0} WHERE Id = {historyPayment.Id}");
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        db.ChangeTracker.Clear();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var model = CreatePayment(new DateTime(2026, 4, 21), 2_000m, "Новый платеж");
        model.ClientId = client.Id;
        model.Account = "INV-ZERO";
        model.AccountDate = new DateTime(2026, 4, 20);

        var actionResult = await controller.Create(model, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(actionResult);
        var payment = Assert.IsType<Payment>(created.Value);
        Assert.Null(payment.InvoiceId);
        Assert.Equal("INV-ZERO", payment.Account);
    }

    [Fact]
    public async Task Create_DeduplicatesLegacyAndCurrentLookupCandidates()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "Клиент с дублем кандидатов",
        };

        var invoice = new Invoice
        {
            Number = "INV-DUP",
            Date = new DateTime(2026, 4, 25),
        };

        db.AddRange(client, invoice);
        await db.SaveChangesAsync();

        var invoiceDocument = CreatePayment(invoice.Date, 9_000m, "Счет INV-DUP");
        invoiceDocument.ClientId = client.Id;
        invoiceDocument.DocumentKind = DocumentKind.Invoice;
        invoiceDocument.InvoiceId = invoice.Id;
        invoiceDocument.Account = invoice.Number;
        invoiceDocument.AccountDate = invoice.Date;
        db.Payments.Add(invoiceDocument);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var model = CreatePayment(new DateTime(2026, 4, 26), 3_000m, "Платеж по найденному счету");
        model.ClientId = client.Id;
        model.Account = " INV-DUP ";
        model.AccountDate = invoice.Date;
        model.LegacyAccountNumber = "INV-DUP";
        model.LegacyAccountDate = invoice.Date;

        var actionResult = await controller.Create(model, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(actionResult);
        var payment = Assert.IsType<Payment>(created.Value);
        Assert.Equal(invoice.Id, payment.InvoiceId);
        Assert.Equal(invoice.Number, payment.Account);
        Assert.Equal(invoice.Date, payment.AccountDate);
    }

    [Fact]
    public async Task Create_WithAuditModelDisabled_SkipsAuditMetadataPopulation()
    {
        await using var db = await CreateContextAsync(useObjectAuditModel: false);
        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var model = CreatePayment(new DateTime(2026, 4, 27), 5_000m, "Платеж без объектного аудита");

        var actionResult = await controller.Create(model, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(actionResult);
        var payment = Assert.IsType<Payment>(created.Value);
        Assert.Null(payment.AuditCreatedByUserId);
        Assert.Null(payment.AuditCreatedByUserName);
        Assert.Null(payment.AuditUpdatedAt);
        Assert.Null(payment.AuditUpdatedByUserId);
        Assert.Null(payment.AuditUpdatedByUserName);
    }

    [Fact]
    public async Task Create_ExpensePayment_PersistsExpenseClientAndPaymentDocumentFields()
    {
        await using var db = await CreateContextAsync();

        var supplier = new Client { Name = "Поставщик", CreatedAt = DateTime.UtcNow };
        var expenseClient = new Client { Name = "Клиент расхода", CreatedAt = DateTime.UtcNow };
        db.Clients.AddRange(supplier, expenseClient);
        await db.SaveChangesAsync();

        var clientCase = new ClientCase
        {
            ClientId = expenseClient.Id,
            Title = "Расходное дело",
            Status = ClientCaseStatus.Open,
            CreatedAt = DateTime.UtcNow,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var model = CreatePayment(new DateTime(2026, 4, 29), 12_500m, "Расход по подрядчику");
        model.Type = PaymentType.Expense;
        model.ClientId = supplier.Id;
        model.ExpenseClientId = expenseClient.Id;
        model.ClientCaseId = clientCase.Id;
        model.PaymentOrderNumber = "  ПП-458  ";
        model.PaymentDocumentDate = new DateTime(2026, 4, 28, 13, 45, 0, DateTimeKind.Utc);

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        var actionResult = await controller.Create(model, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(actionResult);
        var payment = Assert.IsType<Payment>(created.Value);
        Assert.Equal(PaymentType.Expense, payment.Type);
        Assert.Equal(supplier.Id, payment.ClientId);
        Assert.Equal(expenseClient.Id, payment.ExpenseClientId);
        Assert.Equal(clientCase.Id, payment.ClientCaseId);
        Assert.Equal("ПП-458", payment.PaymentOrderNumber);
        Assert.Equal(new DateTime(2026, 4, 28), payment.PaymentDocumentDate);

        var getById = await controller.GetById(payment.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(getById);
        var projected = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(expenseClient.Id, projected.ExpenseClientId);
        Assert.NotNull(projected.ExpenseClient);
        Assert.Equal("Клиент расхода", projected.ExpenseClient!.Name);
        Assert.Equal("ПП-458", projected.PaymentOrderNumber);
    }

    [Fact]
    public async Task Create_RejectsExpenseClientCaseWithoutExpenseClientId()
    {
        await using var db = await CreateContextAsync();

        var supplier = new Client { Name = "Поставщик", CreatedAt = DateTime.UtcNow };
        var expenseClient = new Client { Name = "Клиент расхода", CreatedAt = DateTime.UtcNow };
        db.Clients.AddRange(supplier, expenseClient);
        await db.SaveChangesAsync();

        var clientCase = new ClientCase
        {
            ClientId = expenseClient.Id,
            Title = "Дело без привязки",
            Status = ClientCaseStatus.Open,
            CreatedAt = DateTime.UtcNow,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var model = CreatePayment(new DateTime(2026, 4, 30), 3_200m, "Расход без клиента расхода");
        model.Type = PaymentType.Expense;
        model.ClientId = supplier.Id;
        model.ClientCaseId = clientCase.Id;

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        var actionResult = await controller.Create(model, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
        Assert.Equal(
            "ExpenseClientId or expenseLegalEntityId is required when clientCaseId is specified for expense payments.",
            badRequest.Value);
    }

    [Fact]
    public async Task UpdateInvoiceManaged_WithOverpayment_AddsClientBalanceCredit()
    {
        await using var db = await CreateContextAsync();

        var client = new Client { Name = "Клиент с переплатой", CreatedAt = DateTime.UtcNow };
        var invoice = new Invoice { Number = "3", Date = new DateTime(2026, 1, 19) };
        db.Clients.Add(client);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var invoiceDocument = CreatePayment(invoice.Date, 16_250m, "Счет №3");
        invoiceDocument.ClientId = client.Id;
        invoiceDocument.InvoiceId = invoice.Id;
        invoiceDocument.DocumentKind = DocumentKind.Invoice;
        invoiceDocument.Account = invoice.Number;
        invoiceDocument.AccountDate = invoice.Date;

        var installment = CreatePayment(new DateTime(2026, 4, 25), 16_250m, "Рассрочка по счету №3");
        installment.ClientId = client.Id;
        installment.InvoiceId = invoice.Id;
        installment.SystemNotes = $"{InstallmentPaymentMarker}\nПлатеж 3/6";
        db.Payments.AddRange(invoiceDocument, installment);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var update = CreatePayment(installment.Date, 16_250m, installment.Description);
        update.ClientId = client.Id;
        update.InvoiceId = invoice.Id;
        update.PaidAmount = 17_000m;
        update.Status = PaymentStatus.Completed;
        update.PaidDate = new DateTime(2026, 4, 25);
        update.LastPaymentDate = new DateTime(2026, 4, 25);

        var actionResult = await controller.UpdateInvoiceManaged(installment.Id, update, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var payment = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(16_250m, payment.PaidAmount);

        var transaction = await db.ClientBalanceTransactions.SingleAsync();
        Assert.Equal(client.Id, transaction.ClientId);
        Assert.Equal(installment.Id, transaction.PaymentId);
        Assert.Equal(750m, transaction.Amount);
        Assert.Equal(ClientBalanceTransactionType.Overpayment, transaction.Type);
        Assert.Equal(ClientBalanceDirection.Credit, transaction.Direction);
    }

    [Fact]
    public async Task ApplyCredit_UsesClientBalanceAndCompletesPayment()
    {
        await using var db = await CreateContextAsync();

        var client = new Client { Name = "Клиент с балансом", CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 4, 28), 1_000m, "Следующий платеж");
        payment.ClientId = client.Id;
        payment.PaidAmount = 250m;
        db.Payments.Add(payment);
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = client.Id,
            Amount = 800m,
            Type = ClientBalanceTransactionType.Overpayment,
            Direction = ClientBalanceDirection.Credit,
            Description = "Переплата",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = new ClientBalancesController(db);

        var actionResult = await controller.ApplyCredit(
            client.Id,
            new ApplyClientBalanceRequest { PaymentId = payment.Id, Amount = 800m },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var updatedPayment = await db.Payments.FindAsync(payment.Id);
        Assert.NotNull(updatedPayment);
        Assert.Equal(1_000m, updatedPayment!.PaidAmount);
        Assert.Equal(PaymentStatus.Completed, updatedPayment.Status);

        var balance = await ClientBalanceService.GetBalanceAsync(db, client.Id);
        Assert.Equal(50m, balance);
        Assert.Equal(2, await db.ClientBalanceTransactions.CountAsync());
        Assert.Contains(await db.ClientBalanceTransactions.ToListAsync(), item =>
            item.Type == ClientBalanceTransactionType.CreditApplied
            && item.Direction == ClientBalanceDirection.Debit
            && item.Amount == 750m);
    }

    [Fact]
    public async Task Update_WithAppliedClientBalance_RejectsLowerPaidAmount()
    {
        await using var db = await CreateContextAsync();

        var client = new Client { Name = "Клиент с зачетом", CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 6, 25), 16_250m, "Следующий платеж");
        payment.ClientId = client.Id;
        payment.PaidAmount = 250m;
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = client.Id,
            PaymentId = payment.Id,
            Amount = 250m,
            Type = ClientBalanceTransactionType.CreditApplied,
            Direction = ClientBalanceDirection.Debit,
            Description = "Зачет баланса",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var update = CreatePayment(payment.Date, payment.Amount, payment.Description);
        update.ClientId = client.Id;
        update.PaidAmount = 100m;

        var actionResult = await controller.Update(payment.Id, update, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
        Assert.Contains("Оплата не может быть меньше зачтенной суммы", badRequest.Value?.ToString());
        Assert.Equal(250m, (await db.Payments.FindAsync(payment.Id))!.PaidAmount);
    }

    [Fact]
    public async Task UpdateInvoiceManaged_WithAppliedClientBalance_RejectsLowerPaidAmount()
    {
        await using var db = await CreateContextAsync();

        var client = new Client { Name = "Клиент с зачетом по счету", CreatedAt = DateTime.UtcNow };
        var invoice = new Invoice { Number = "3", Date = new DateTime(2026, 1, 19) };
        db.Clients.Add(client);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var installment = CreatePayment(new DateTime(2026, 6, 25), 16_250m, "Рассрочка по счету №3");
        installment.ClientId = client.Id;
        installment.InvoiceId = invoice.Id;
        installment.DocumentKind = DocumentKind.Payment;
        installment.Account = invoice.Number;
        installment.AccountDate = invoice.Date;
        installment.PaidAmount = 250m;
        installment.SystemNotes = $"{InstallmentPaymentMarker}\nЗачтено с баланса клиента: 250,00";
        db.Payments.Add(installment);
        await db.SaveChangesAsync();

        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = client.Id,
            InvoiceId = invoice.Id,
            PaymentId = installment.Id,
            Amount = 250m,
            Type = ClientBalanceTransactionType.CreditApplied,
            Direction = ClientBalanceDirection.Debit,
            Description = "Зачет баланса",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var update = CreatePayment(installment.Date, installment.Amount, installment.Description);
        update.ClientId = client.Id;
        update.InvoiceId = invoice.Id;
        update.PaidAmount = 100m;

        var actionResult = await controller.UpdateInvoiceManaged(installment.Id, update, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
        Assert.Contains("Оплата не может быть меньше зачтенной суммы", badRequest.Value?.ToString());
        Assert.Equal(250m, (await db.Payments.FindAsync(installment.Id))!.PaidAmount);
    }

    [Fact]
    public async Task UpdateInvoiceManaged_RefreshesParentInvoice_WhenInstallmentIsPaid()
    {
        await using var db = await CreateContextAsync();

        var client = new Client { Name = "Клиент рассрочки", CreatedAt = DateTime.UtcNow };
        var invoice = new Invoice { Number = "RS-1", Date = DateTime.UtcNow.Date.AddDays(10) };
        db.Clients.Add(client);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var parentPayment = CreatePayment(invoice.Date, 18_000m, "Счет RS-1");
        parentPayment.ClientId = client.Id;
        parentPayment.DocumentKind = DocumentKind.Invoice;
        parentPayment.InvoiceId = invoice.Id;
        parentPayment.Account = invoice.Number;
        parentPayment.AccountDate = invoice.Date;

        var firstInstallment = CreatePayment(invoice.Date.AddDays(5), 6_000m, "Рассрочка 1/3");
        firstInstallment.ClientId = client.Id;
        firstInstallment.InvoiceId = invoice.Id;
        firstInstallment.Account = invoice.Number;
        firstInstallment.AccountDate = invoice.Date;
        firstInstallment.SystemNotes = $"{InstallmentPaymentMarker}\nПлатёж 1/3";

        var secondInstallment = CreatePayment(invoice.Date.AddDays(35), 6_000m, "Рассрочка 2/3");
        secondInstallment.ClientId = client.Id;
        secondInstallment.InvoiceId = invoice.Id;
        secondInstallment.Account = invoice.Number;
        secondInstallment.AccountDate = invoice.Date;
        secondInstallment.SystemNotes = $"{InstallmentPaymentMarker}\nПлатёж 2/3";

        db.Payments.AddRange(parentPayment, firstInstallment, secondInstallment);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var update = CreatePayment(firstInstallment.Date, firstInstallment.Amount, firstInstallment.Description);
        update.ClientId = client.Id;
        update.InvoiceId = invoice.Id;
        update.Account = invoice.Number;
        update.AccountDate = invoice.Date;
        update.Status = PaymentStatus.Completed;
        update.PaidAmount = firstInstallment.Amount;
        update.PaidDate = firstInstallment.Date;
        update.LastPaymentDate = firstInstallment.Date;

        var actionResult = await controller.UpdateInvoiceManaged(firstInstallment.Id, update, CancellationToken.None);

        Assert.IsType<OkObjectResult>(actionResult);
        db.ChangeTracker.Clear();

        var refreshedParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == parentPayment.Id);
        Assert.Equal(6_000m, refreshedParent.PaidAmount);
        Assert.Equal(PaymentStatus.Processing, refreshedParent.Status);
        Assert.Equal(InvoiceWorkflowStatus.PartiallyPaid, refreshedParent.InvoiceWorkflowStatus);
        Assert.False(refreshedParent.IsPaid);
        Assert.Equal(firstInstallment.Date, refreshedParent.PaidDate!.Value);
        Assert.Equal(secondInstallment.Date, refreshedParent.Date);
    }

    [Fact]
    public async Task Create_LinkedPayment_RefreshesParentInvoiceAsPartiallyPaid()
    {
        await using var db = await CreateContextAsync();

        var client = new Client { Name = "Клиент счета", CreatedAt = DateTime.UtcNow };
        var invoice = new Invoice { Number = "LINK-1", Date = DateTime.UtcNow.Date.AddDays(7) };
        db.Clients.Add(client);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var parentPayment = CreatePayment(invoice.Date, 10_000m, "Счет LINK-1");
        parentPayment.ClientId = client.Id;
        parentPayment.DocumentKind = DocumentKind.Invoice;
        parentPayment.InvoiceId = invoice.Id;
        parentPayment.Account = invoice.Number;
        parentPayment.AccountDate = invoice.Date;
        db.Payments.Add(parentPayment);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var linkedPayment = CreatePayment(invoice.Date.AddDays(1), 4_000m, "Оплата по счету LINK-1");
        linkedPayment.ClientId = client.Id;
        linkedPayment.InvoiceId = invoice.Id;
        linkedPayment.Status = PaymentStatus.Completed;
        linkedPayment.PaidAmount = 4_000m;
        linkedPayment.PaidDate = linkedPayment.Date;
        linkedPayment.LastPaymentDate = linkedPayment.Date;

        var actionResult = await controller.Create(linkedPayment, CancellationToken.None);

        Assert.IsType<CreatedResult>(actionResult);
        db.ChangeTracker.Clear();

        var refreshedParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == parentPayment.Id);
        Assert.Equal(4_000m, refreshedParent.PaidAmount);
        Assert.Equal(PaymentStatus.Processing, refreshedParent.Status);
        Assert.Equal(InvoiceWorkflowStatus.PartiallyPaid, refreshedParent.InvoiceWorkflowStatus);
        Assert.False(refreshedParent.IsPaid);
    }

    private static async Task<PaymentContext> CreateContextAsync(bool useObjectAuditModel = true)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

        var db = new PaymentContext(options, useObjectAuditModel: useObjectAuditModel);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task<InvoiceAggregateIds> CreateInvoiceAggregateAsync(PaymentContext db)
    {
        var invoice = new Invoice
        {
            Number = "30",
            Date = new DateTime(2026, 4, 8),
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var invoiceDate = new DateTime(2026, 4, 8);
        var parentPayment = CreatePayment(invoiceDate, 18_000m, "Счет №30");
        parentPayment.DocumentKind = DocumentKind.Invoice;
        parentPayment.InvoiceId = invoice.Id;
        parentPayment.Account = invoice.Number;
        parentPayment.AccountDate = invoice.Date;

        var installmentOne = CreatePayment(invoiceDate, 6_000m, "Рассрочка 1/3");
        installmentOne.InvoiceId = invoice.Id;
        installmentOne.SystemNotes = $"{InstallmentPaymentMarker}\nПлатёж 1/3";

        var installmentTwo = CreatePayment(invoiceDate.AddMonths(1), 6_000m, "Рассрочка 2/3");
        installmentTwo.InvoiceId = invoice.Id;
        installmentTwo.SystemNotes = $"{InstallmentPaymentMarker}\nПлатёж 2/3";

        var unrelatedPayment = CreatePayment(invoiceDate, 2_000m, "Обычный платеж");

        db.Payments.AddRange(parentPayment, installmentOne, installmentTwo, unrelatedPayment);
        await db.SaveChangesAsync();

        return new InvoiceAggregateIds(
            invoice.Id,
            parentPayment.Id,
            new[] { installmentOne.Id, installmentTwo.Id },
            unrelatedPayment.Id);
    }

    private static async Task<Contract> CreateContractAsync(PaymentContext db, string number)
    {
        var contract = new Contract
        {
            Number = number,
            Date = new DateTime(2026, 4, 1),
            BillingMode = ContractBillingMode.Actual,
            IsOpenEnded = true,
        };

        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        return contract;
    }

    private static Payment CreatePayment(DateTime date, decimal amount, string description)
    {
        return new Payment
        {
            Amount = amount,
            CounterpartyName = "Тестовый клиент",
            Date = date,
            PlannedDate = date,
            Description = description,
            DocumentKind = DocumentKind.Payment,
            Notes = string.Empty,
            Status = PaymentStatus.Pending,
            SystemNotes = string.Empty,
            Type = PaymentType.Income,
        };
    }

    private sealed record InvoiceAggregateIds(
        int InvoiceEntityId,
        int ParentPaymentId,
        int[] InstallmentPaymentIds,
        int UnrelatedPaymentId);

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());

        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }
}
