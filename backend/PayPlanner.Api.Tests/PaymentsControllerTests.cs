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
            excludeInstallmentParents: true,
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
    public async Task Create_ResolvesInvoiceLinkFromPaymentHistory_AndNormalizesFields()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "РљР»РёРµРЅС‚ РґР»СЏ РїСЂРёРІСЏР·РєРё",
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Р”РµР»Рѕ РґР»СЏ РїСЂРёРІСЏР·РєРё",
        };

        var incomeType = new IncomeType
        {
            Name = "РћРїР»Р°С‚Р° РїРѕ СЃС‡С‘С‚Сѓ",
            PaymentType = PaymentType.Income,
        };

        var invoice = new Invoice
        {
            Number = "INV-400",
            Date = new DateTime(2026, 4, 2),
        };

        db.AddRange(client, clientCase, incomeType, invoice);
        await db.SaveChangesAsync();

        var invoiceDocument = CreatePayment(invoice.Date, 20_000m, "РЎС‡С‘С‚ INV-400");
        invoiceDocument.ClientId = client.Id;
        invoiceDocument.ClientCaseId = clientCase.Id;
        invoiceDocument.DocumentKind = DocumentKind.Invoice;
        invoiceDocument.InvoiceId = invoice.Id;
        invoiceDocument.Account = invoice.Number;
        invoiceDocument.AccountDate = invoice.Date;
        db.Payments.Add(invoiceDocument);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var model = CreatePayment(new DateTime(2026, 4, 3), 5_500m, "Р”РѕРїР»Р°С‚Р° РїРѕ СЃС‡С‘С‚Сѓ");
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
            Name = "РљР»РёРµРЅС‚ РґР»СЏ РѕР±РЅРѕРІР»РµРЅРёСЏ",
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Р”РµР»Рѕ РґР»СЏ РѕР±РЅРѕРІР»РµРЅРёСЏ",
        };

        var incomeType = new IncomeType
        {
            Name = "Р”РѕС…РѕРґ",
            PaymentType = PaymentType.Income,
        };

        var paymentSource = new PaymentSource
        {
            Name = "Р‘Р°РЅРє",
        };

        var paymentStatus = new PaymentStatusEntity
        {
            Name = "Р’ СЂР°Р±РѕС‚Рµ",
        };

        var dealType = new DealType
        {
            Name = "РЎРґРµР»РєР°",
        };

        var invoice = new Invoice
        {
            Number = "INV-401",
            Date = new DateTime(2026, 4, 8),
        };

        db.AddRange(client, clientCase, incomeType, paymentSource, paymentStatus, dealType, invoice);
        await db.SaveChangesAsync();

        var invoiceDocument = CreatePayment(invoice.Date, 12_000m, "РЎС‡С‘С‚ INV-401");
        invoiceDocument.ClientId = client.Id;
        invoiceDocument.ClientCaseId = clientCase.Id;
        invoiceDocument.DocumentKind = DocumentKind.Invoice;
        invoiceDocument.InvoiceId = invoice.Id;
        invoiceDocument.Account = invoice.Number;
        invoiceDocument.AccountDate = invoice.Date;
        db.Payments.Add(invoiceDocument);

        var standalonePayment = CreatePayment(new DateTime(2026, 4, 5), 4_500m, "РЎС‚Р°СЂС‹Р№ РїР»Р°С‚С‘Р¶");
        standalonePayment.ClientId = client.Id;
        standalonePayment.ClientCaseId = clientCase.Id;
        standalonePayment.IncomeTypeId = incomeType.Id;
        standalonePayment.SystemNotes = "РСЃС…РѕРґРЅР°СЏ Р·Р°РјРµС‚РєР°";
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
            Description = "РћР±РЅРѕРІР»С‘РЅРЅС‹Р№ РїР»Р°С‚С‘Р¶",
            DocumentKind = DocumentKind.Payment,
            Notes = "РќРѕРІС‹Рµ Р·Р°РјРµС‚РєРё",
            Status = PaymentStatus.Completed,
            SystemNotes = "РСЃС…РѕРґРЅР°СЏ Р·Р°РјРµС‚РєР°\nР СѓС‡РЅРѕРµ РїСЂРёРјРµС‡Р°РЅРёРµ",
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
        Assert.Contains("Р СѓС‡РЅРѕРµ РїСЂРёРјРµС‡Р°РЅРёРµ", updated.SystemNotes);
        Assert.Equal(1, updated.SystemNotes.Split('\n').Count(line => line.Contains("РСЃС…РѕРґРЅР°СЏ")));

        var stored = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == standalonePayment.Id);
        Assert.Equal(invoice.Id, stored.InvoiceId);
        Assert.Equal(invoice.Number, stored.Account);
        Assert.Equal("LEGACY-401", stored.LegacyAccountNumber);
        Assert.Equal(3, stored.RescheduleCount);
    }

    [Fact]
    public async Task RepairInvoiceLinks_ReassignsLinkedPayments_AndDowngradesDuplicateInvoiceDocuments()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "РљР»РёРµРЅС‚ РґР»СЏ РІРѕСЃСЃС‚Р°РЅРѕРІР»РµРЅРёСЏ",
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Р”РµР»Рѕ РґР»СЏ repair",
        };

        var invoice = new Invoice
        {
            Number = "INV-402",
            Date = new DateTime(2026, 4, 15),
        };

        db.AddRange(client, clientCase, invoice);
        await db.SaveChangesAsync();

        var primaryInvoiceDocument = CreatePayment(invoice.Date, 18_000m, "РћСЃРЅРѕРІРЅРѕР№ С‡РµРє");
        primaryInvoiceDocument.ClientId = client.Id;
        primaryInvoiceDocument.ClientCaseId = clientCase.Id;
        primaryInvoiceDocument.DocumentKind = DocumentKind.Invoice;
        primaryInvoiceDocument.InvoiceId = invoice.Id;
        primaryInvoiceDocument.Account = invoice.Number;
        primaryInvoiceDocument.AccountDate = invoice.Date;
        primaryInvoiceDocument.CreatedAt = new DateTime(2026, 4, 15, 8, 0, 0, DateTimeKind.Utc);

        var duplicateInvoiceDocument = CreatePayment(invoice.Date, 18_000m, "Р”СѓР±Р»СЊ С‡РµРєР°");
        duplicateInvoiceDocument.ClientId = client.Id;
        duplicateInvoiceDocument.ClientCaseId = clientCase.Id;
        duplicateInvoiceDocument.DocumentKind = DocumentKind.Invoice;
        duplicateInvoiceDocument.InvoiceId = invoice.Id;
        duplicateInvoiceDocument.Account = invoice.Number;
        duplicateInvoiceDocument.AccountDate = invoice.Date;
        duplicateInvoiceDocument.CreatedAt = new DateTime(2026, 4, 15, 9, 0, 0, DateTimeKind.Utc);

        var orphanPayment = CreatePayment(new DateTime(2026, 4, 16), 6_000m, "РћРїР»Р°С‚Р° Р±РµР· РїСЂРёРІСЏР·РєРё");
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
