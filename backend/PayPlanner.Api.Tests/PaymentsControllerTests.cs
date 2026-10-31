using System.Text.Json;
using System.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Controllers.V1;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
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
    public async Task PaymentsV2_Get_ReturnsCompactReferences_AndKeepsDetailEndpointComplete()
    {
        await using var db = await CreateContextAsync();

        const string privateAddress = "Полный адрес не должен попадать в журнал платежей";
        var client = new Client
        {
            Name = "Клиент компактного журнала",
            Address = privateAddress,
            Email = "compact-list@example.test",
            CreatedAt = DateTime.UtcNow,
        };
        var dealType = new DealType { Name = "Судебная работа", Description = "Служебное описание" };
        db.Clients.Add(client);
        db.DealTypes.Add(dealType);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 7, 17), 12_500m, "Компактный платеж");
        payment.ClientId = client.Id;
        payment.DealTypeId = dealType.Id;
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new PaymentsV2Controller(db);
        var listResult = await controller.Get(
            from: null,
            to: null,
            clientId: null,
            caseId: null,
            excludeInstallmentParents: false,
            sortBy: "date",
            sortDir: "desc",
            ct: CancellationToken.None);

        var listOk = Assert.IsType<OkObjectResult>(listResult.Result);
        var response = Assert.IsType<PaymentsPageResponse>(listOk.Value);
        var item = Assert.Single(response.Items);
        Assert.Equal(client.Id, item.Client?.Id);
        Assert.Equal(client.Name, item.Client?.Name);
        Assert.Equal(dealType.Name, item.DealType?.Name);

        var listJson = JsonSerializer.Serialize(response);
        Assert.DoesNotContain(privateAddress, listJson, StringComparison.Ordinal);
        Assert.DoesNotContain("compact-list@example.test", listJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Служебное описание", listJson, StringComparison.Ordinal);

        var detailResult = await controller.GetById(payment.Id, CancellationToken.None);
        var detailOk = Assert.IsType<OkObjectResult>(detailResult);
        var detailedPayment = Assert.IsType<Payment>(detailOk.Value);
        Assert.Equal(privateAddress, detailedPayment.Client?.Address);
        Assert.Equal("compact-list@example.test", detailedPayment.Client?.Email);
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
    public async Task Get_AttachesContractorUserFromTaskPayment()
    {
        await using var db = await CreateContextAsync();

        var role = new Role { Name = "contractor" };
        var contractor = new User
        {
            Email = "gordik@example.test",
            FullName = "Гордик Сергей",
            IsExternalContractor = true,
            IsApproved = true,
            Role = role,
        };
        var payment = CreatePayment(new DateTime(2026, 6, 29), 2_000m, "Оплата исполнителю");
        payment.Type = PaymentType.Expense;
        payment.CounterpartyName = contractor.FullName;

        db.Users.Add(contractor);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        db.TaskJournalItems.Add(new TaskJournalItem
        {
            Title = "Артамонов - подготовка решения суда",
            ObjectName = "Артамонов",
            AssigneeUserId = contractor.Id,
            AssigneeName = contractor.FullName,
            AuthorName = "Администратор",
            DueAt = new DateTime(2026, 6, 29, 10, 0, 0, DateTimeKind.Utc),
            ContractorPaymentEnabled = true,
            ContractorPaymentAmount = payment.Amount,
            ContractorPaymentId = payment.Id,
        });
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        var actionResult = await controller.Get(
            from: new DateTime(2026, 6, 1),
            to: new DateTime(2026, 6, 30),
            clientId: null,
            caseId: null,
            excludeInstallmentParents: false,
            sortBy: "date",
            sortDir: "asc",
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var payments = Assert.IsAssignableFrom<List<Payment>>(ok.Value);
        var item = Assert.Single(payments);

        Assert.Equal(contractor.Id, item.ContractorUserId);
        Assert.Equal(contractor.FullName, item.ContractorUserName);
    }

    [Fact]
    public async Task PaymentsV2_SelectsContractorWithUserAndFallsBackToCounterpartyName()
    {
        await using var db = await CreateContextAsync();

        var role = new Role { Name = "contractor-v2" };
        var contractor = new User
        {
            Email = "contractor-v2@example.test",
            FullName = "Выбранный исполнитель",
            IsExternalContractor = true,
            IsApproved = true,
            Role = role,
        };
        var assignedPayment = CreatePayment(new DateTime(2026, 7, 1), 2_000m, "Оплата выбранному исполнителю");
        assignedPayment.Type = PaymentType.Expense;
        assignedPayment.CounterpartyName = "Резервное имя исполнителя";
        var fallbackPayment = CreatePayment(new DateTime(2026, 7, 2), 3_000m, "Оплата исполнителю без пользователя");
        fallbackPayment.Type = PaymentType.Expense;
        fallbackPayment.CounterpartyName = "Исполнитель из платежа";

        db.Users.Add(contractor);
        db.Payments.AddRange(assignedPayment, fallbackPayment);
        await db.SaveChangesAsync();

        db.TaskJournalItems.AddRange(
            new TaskJournalItem
            {
                Title = "Задача без привязанного пользователя",
                ObjectName = "Объект 1",
                AssigneeName = "Исполнитель без пользователя",
                AuthorName = "Администратор",
                DueAt = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc),
                ContractorPaymentEnabled = true,
                ContractorPaymentAmount = assignedPayment.Amount,
                ContractorPaymentId = assignedPayment.Id,
            },
            new TaskJournalItem
            {
                Title = "Задача с привязанным пользователем",
                ObjectName = "Объект 1",
                AssigneeUserId = contractor.Id,
                AssigneeName = "  Выбранный исполнитель  ",
                AuthorName = "Администратор",
                DueAt = new DateTime(2026, 7, 1, 11, 0, 0, DateTimeKind.Utc),
                ContractorPaymentEnabled = true,
                ContractorPaymentAmount = assignedPayment.Amount,
                ContractorPaymentId = assignedPayment.Id,
            },
            new TaskJournalItem
            {
                Title = "Задача с пустым именем исполнителя",
                ObjectName = "Объект 2",
                AssigneeName = "   ",
                AuthorName = "Администратор",
                DueAt = new DateTime(2026, 7, 2, 10, 0, 0, DateTimeKind.Utc),
                ContractorPaymentEnabled = true,
                ContractorPaymentAmount = fallbackPayment.Amount,
                ContractorPaymentId = fallbackPayment.Id,
            });
        await db.SaveChangesAsync();

        var controller = new PaymentsV2Controller(db);
        var actionResult = await controller.Get(
            from: new DateTime(2026, 7, 1),
            to: new DateTime(2026, 7, 31),
            clientId: null,
            caseId: null,
            excludeInstallmentParents: false,
            sortBy: "date",
            sortDir: "asc",
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<PaymentsPageResponse>(ok.Value);
        Assert.Collection(
            response.Items,
            item =>
            {
                Assert.Equal(assignedPayment.Id, item.Id);
                Assert.Equal(contractor.Id, item.ContractorUserId);
                Assert.Equal("Выбранный исполнитель", item.ContractorUserName);
            },
            item =>
            {
                Assert.Equal(fallbackPayment.Id, item.Id);
                Assert.Null(item.ContractorUserId);
                Assert.Equal(fallbackPayment.CounterpartyName, item.ContractorUserName);
            });
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

        var paymentByInvoiceContract = CreatePayment(new DateTime(2026, 4, 14), 9_000m, "Платеж по счету с договором");
        paymentByInvoiceContract.InvoiceId = invoice.Id;
        paymentByInvoiceContract.Account = invoice.Number;
        paymentByInvoiceContract.AccountDate = invoice.Date;

        var unrelatedPayment = CreatePayment(new DateTime(2026, 4, 13), 8_000m, "Посторонний платеж");

        db.Payments.AddRange(canonicalPayment, legacyPayment, invoiceDocument, paymentByInvoiceContract, unrelatedPayment);
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

        Assert.Equal(3, response.Total);
        Assert.Equal(
            new[] { canonicalPayment.Id, legacyPayment.Id, paymentByInvoiceContract.Id },
            response.Items.Select(item => item.Id).OrderBy(id => id).ToArray());
        Assert.Equal(20_000m, response.Summary.Income);
        Assert.DoesNotContain(response.Items, item => item.Id == invoiceDocument.Id);
        Assert.DoesNotContain(response.Items, item => item.Id == unrelatedPayment.Id);
    }

    [Fact]
    public async Task PaymentsV2_FiltersByInvoiceIdAndActId()
    {
        await using var db = await CreateContextAsync();

        var invoice = new Invoice
        {
            Number = "INV-PAY-V2-REL",
            Date = new DateTime(2026, 4, 20),
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var invoiceDocument = CreatePayment(invoice.Date, 12_000m, "Счет-документ для связанных платежей");
        invoiceDocument.DocumentKind = DocumentKind.Invoice;
        invoiceDocument.InvoiceId = invoice.Id;
        invoiceDocument.Account = invoice.Number;
        invoiceDocument.AccountDate = invoice.Date;

        var linkedPayment = CreatePayment(invoice.Date.AddDays(1), 5_000m, "Платеж по связанному счету");
        linkedPayment.InvoiceId = invoice.Id;
        linkedPayment.Account = invoice.Number;
        linkedPayment.AccountDate = invoice.Date;

        var unrelatedPayment = CreatePayment(invoice.Date.AddDays(2), 6_000m, "Посторонний платеж");
        db.Payments.AddRange(invoiceDocument, linkedPayment, unrelatedPayment);
        await db.SaveChangesAsync();

        var act = new Act
        {
            Number = "ACT-PAY-V2-REL",
            Date = invoice.Date.AddDays(3),
            Amount = 12_000m,
            CreationMode = ActCreationMode.Manual,
            InvoiceId = invoiceDocument.Id,
            Status = ActStatus.Created,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Acts.Add(act);
        await db.SaveChangesAsync();

        var controller = new PaymentsV2Controller(db);

        var invoiceResult = await controller.Get(
            from: null,
            to: null,
            clientId: null,
            caseId: null,
            invoiceId: invoiceDocument.Id,
            excludeInstallmentParents: false,
            excludeInvoiceDocuments: true,
            sortBy: "date",
            sortDir: "asc",
            ct: CancellationToken.None);

        var invoiceOk = Assert.IsType<OkObjectResult>(invoiceResult.Result);
        var invoiceResponse = Assert.IsType<PaymentsPageResponse>(invoiceOk.Value);
        Assert.Collection(invoiceResponse.Items, item => Assert.Equal(linkedPayment.Id, item.Id));

        var actResult = await controller.Get(
            from: null,
            to: null,
            clientId: null,
            caseId: null,
            actId: act.Id,
            excludeInstallmentParents: false,
            excludeInvoiceDocuments: true,
            sortBy: "date",
            sortDir: "asc",
            ct: CancellationToken.None);

        var actOk = Assert.IsType<OkObjectResult>(actResult.Result);
        var actResponse = Assert.IsType<PaymentsPageResponse>(actOk.Value);
        Assert.Collection(actResponse.Items, item => Assert.Equal(linkedPayment.Id, item.Id));
        Assert.DoesNotContain(actResponse.Items, item => item.Id == unrelatedPayment.Id);
    }

    [Fact]
    public async Task PaymentsV2_FiltersClientPaymentsThroughInvoiceDocument()
    {
        await using var db = await CreateContextAsync();

        var client = new Client { Name = "Client with invoice-linked payments", CreatedAt = DateTime.UtcNow };
        var invoice = new Invoice
        {
            Number = "INV-PAY-V2-CLIENT",
            Date = new DateTime(2026, 4, 24),
        };
        db.Clients.Add(client);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var invoiceDocument = CreatePayment(invoice.Date, 18_000m, "Invoice document");
        invoiceDocument.DocumentKind = DocumentKind.Invoice;
        invoiceDocument.ClientId = client.Id;
        invoiceDocument.InvoiceId = invoice.Id;
        invoiceDocument.Account = invoice.Number;
        invoiceDocument.AccountDate = invoice.Date;

        var linkedPayment = CreatePayment(invoice.Date.AddDays(1), 18_000m, "Payment linked only by invoice");
        linkedPayment.InvoiceId = invoice.Id;
        linkedPayment.Account = invoice.Number;
        linkedPayment.AccountDate = invoice.Date;

        var unrelatedPayment = CreatePayment(invoice.Date.AddDays(2), 7_000m, "Unrelated payment");
        db.Payments.AddRange(invoiceDocument, linkedPayment, unrelatedPayment);
        await db.SaveChangesAsync();

        var controller = new PaymentsV2Controller(db);

        var result = await controller.Get(
            from: null,
            to: null,
            clientId: client.Id,
            caseId: null,
            excludeInstallmentParents: false,
            excludeInvoiceDocuments: true,
            sortBy: "date",
            sortDir: "asc",
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<PaymentsPageResponse>(ok.Value);

        Assert.Collection(response.Items, item => Assert.Equal(linkedPayment.Id, item.Id));
        Assert.DoesNotContain(response.Items, item => item.Id == invoiceDocument.Id);
        Assert.DoesNotContain(response.Items, item => item.Id == unrelatedPayment.Id);
    }

    [Fact]
    public async Task PaymentsV2_AllowsResponsibleToSeePaymentLinkedThroughInvoiceDocument()
    {
        await using var db = await CreateContextAsync();

        var role = new Role
        {
            Name = $"payments-user-{Guid.NewGuid():N}",
            Description = "Payments user",
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Payments,
            CanView = true,
            ViewScope = DataVisibilityScope.Own,
        });

        var user = new User
        {
            Email = $"elena-{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            FullName = "Elena",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            IsEmployee = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var visibleClient = new Client
        {
            Name = "Beznyakov",
            ResponsibleUserId = user.Id,
            CreatedAt = DateTime.UtcNow,
        };
        var hiddenClient = new Client { Name = "Other client", CreatedAt = DateTime.UtcNow };
        var invoice = new Invoice
        {
            Number = "155",
            Date = new DateTime(2025, 10, 24),
        };
        var hiddenInvoice = new Invoice
        {
            Number = "999",
            Date = new DateTime(2025, 10, 24),
        };
        db.Clients.AddRange(visibleClient, hiddenClient);
        db.Invoices.AddRange(invoice, hiddenInvoice);
        await db.SaveChangesAsync();

        var invoiceDocument = CreatePayment(invoice.Date, 205_000m, "Invoice 155");
        invoiceDocument.DocumentKind = DocumentKind.Invoice;
        invoiceDocument.ClientId = visibleClient.Id;
        invoiceDocument.InvoiceId = invoice.Id;
        invoiceDocument.Account = invoice.Number;
        invoiceDocument.AccountDate = invoice.Date;

        var linkedPayment = CreatePayment(invoice.Date.AddDays(1), 25_000m, "Linked payment for invoice 155");
        linkedPayment.InvoiceId = invoice.Id;
        linkedPayment.Account = invoice.Number;
        linkedPayment.AccountDate = invoice.Date;

        var hiddenInvoiceDocument = CreatePayment(hiddenInvoice.Date, 12_000m, "Hidden invoice");
        hiddenInvoiceDocument.DocumentKind = DocumentKind.Invoice;
        hiddenInvoiceDocument.ClientId = hiddenClient.Id;
        hiddenInvoiceDocument.InvoiceId = hiddenInvoice.Id;
        hiddenInvoiceDocument.Account = hiddenInvoice.Number;
        hiddenInvoiceDocument.AccountDate = hiddenInvoice.Date;

        var hiddenLinkedPayment = CreatePayment(hiddenInvoice.Date.AddDays(1), 12_000m, "Hidden linked payment");
        hiddenLinkedPayment.InvoiceId = hiddenInvoice.Id;
        hiddenLinkedPayment.Account = hiddenInvoice.Number;
        hiddenLinkedPayment.AccountDate = hiddenInvoice.Date;

        db.Payments.AddRange(invoiceDocument, linkedPayment, hiddenInvoiceDocument, hiddenLinkedPayment);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsV2Controller(db, user.Id);

        var result = await controller.Get(
            from: null,
            to: null,
            clientId: null,
            caseId: null,
            invoiceId: invoiceDocument.Id,
            excludeInstallmentParents: false,
            excludeInvoiceDocuments: true,
            sortBy: "date",
            sortDir: "asc",
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<PaymentsPageResponse>(ok.Value);

        Assert.Collection(response.Items, item => Assert.Equal(linkedPayment.Id, item.Id));
        Assert.DoesNotContain(response.Items, item => item.Id == hiddenLinkedPayment.Id);
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
    public async Task PaymentsV2_ExportCsv_ReturnsFilteredRowsWithProfileTimeZone()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: true);
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Samara",
        });

        var client = new Client { Name = "Экспортный клиент", CreatedAt = DateTime.UtcNow };
        var clientCase = new ClientCase { Title = "Экспортное дело", Client = client };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var exported = CreatePayment(new DateTime(2026, 4, 15), 12_345.67m, "Экспортируемый платеж");
        exported.ClientId = client.Id;
        exported.ClientCaseId = clientCase.Id;
        exported.PaidAmount = 345.67m;
        exported.CreatedAt = new DateTime(2026, 6, 4, 20, 30, 0, DateTimeKind.Utc);

        var hidden = CreatePayment(new DateTime(2026, 4, 16), 9_000m, "Другой платеж");
        hidden.Status = PaymentStatus.Completed;
        db.Payments.AddRange(exported, hidden);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsV2Controller(db, user.Id);

        var result = await controller.ExportCsv(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            clientId: null,
            caseId: null,
            status: PaymentStatus.Pending,
            sortBy: "date",
            sortDir: "asc",
            ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv; charset=utf-8", file.ContentType);
        Assert.StartsWith("payments-", file.FileDownloadName);
        Assert.EndsWith(".csv", file.FileDownloadName);

        var csv = Encoding.UTF8.GetString(file.FileContents).TrimStart('\uFEFF');
        Assert.Contains("Экспортируемый платеж", csv);
        Assert.Contains("Экспортный клиент", csv);
        Assert.Contains("Экспортное дело", csv);
        Assert.Contains("15.04.2026", csv);
        Assert.Contains("05.06.2026 00:30", csv);
        Assert.Contains("Europe/Samara", csv);
        Assert.DoesNotContain("Другой платеж", csv);
    }

    [Fact]
    public async Task PaymentsV2_ExportExcel_ReturnsWorkbookWithFilteredRows()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: true);

        var exported = CreatePayment(new DateTime(2026, 5, 1), 1_000m, "Excel export payment");
        var hidden = CreatePayment(new DateTime(2026, 5, 2), 2_000m, "Hidden Excel payment");
        hidden.Status = PaymentStatus.Completed;
        db.Payments.AddRange(exported, hidden);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsV2Controller(db, user.Id);

        var result = await controller.ExportExcel(
            from: new DateTime(2026, 5, 1),
            to: new DateTime(2026, 5, 31),
            clientId: null,
            caseId: null,
            status: PaymentStatus.Pending,
            sortBy: "date",
            sortDir: "asc",
            ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/vnd.ms-excel; charset=utf-8", file.ContentType);
        Assert.EndsWith(".xls", file.FileDownloadName);

        var workbook = Encoding.UTF8.GetString(file.FileContents).TrimStart('\uFEFF');
        Assert.Contains("<Workbook", workbook);
        Assert.Contains("Excel export payment", workbook);
        Assert.DoesNotContain("Hidden Excel payment", workbook);
    }

    [Fact]
    public async Task PaymentsV2_ExportCsv_ForbidsUserWithoutExportPermission()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false);
        var controller = CreatePaymentsV2Controller(db, user.Id);

        var result = await controller.ExportCsv(
            from: null,
            to: null,
            clientId: null,
            caseId: null,
            ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task PaymentsV2_ExportCsv_ForbidsAdminClaimWhenDatabaseRoleIsNotAdmin()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false);
        var controller = CreatePaymentsV2Controller(db, user.Id, includeAdminRoleClaim: true);

        var result = await controller.ExportCsv(
            from: null,
            to: null,
            clientId: null,
            caseId: null,
            ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task PaymentsV1AndV2_List_ForbidAuthenticatedUserWithoutPaymentsView()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, canView: false);
        var v1 = CreatePaymentsV1Controller(db, user.Id);
        var v2 = CreatePaymentsV2Controller(db, user.Id);

        var v1Result = await v1.Get(null, null, null, null, ct: CancellationToken.None);
        var v2Result = await v2.Get(
            from: null,
            to: null,
            clientId: null,
            caseId: null,
            ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(v1Result);
        Assert.IsType<ForbidResult>(v2Result.Result);
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

        await AuthenticatePaymentWriterAsync(db, controller);
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

        await AuthenticatePaymentWriterAsync(db, controller);
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
    public async Task Delete_RemovesLegacyInvoiceDocument_WhenPaymentHasNoInvoiceLink()
    {
        await using var db = await CreateContextAsync();

        var legacyPayment = CreatePayment(new DateTime(2026, 1, 30), 15_000m, "Legacy invoice-marked expense");
        legacyPayment.Type = PaymentType.Expense;
        legacyPayment.DocumentKind = DocumentKind.Invoice;
        legacyPayment.InvoiceId = null;

        db.Payments.Add(legacyPayment);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.Delete(legacyPayment.Id, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.IsType<NoContentResult>(actionResult);
        Assert.Null(await db.Payments.FindAsync(legacyPayment.Id));
    }

    [Fact]
    public async Task Delete_RemovesGeneratedInstallmentPayment_AndKeepsInvoiceAggregate()
    {
        await using var db = await CreateContextAsync();
        var invoice = await CreateInvoiceAggregateAsync(db);

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.Delete(invoice.InstallmentPaymentIds[0], CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.IsType<NoContentResult>(actionResult);
        Assert.Null(await db.Payments.FindAsync(invoice.InstallmentPaymentIds[0]));
        Assert.NotNull(await db.Payments.FindAsync(invoice.ParentPaymentId));
        Assert.Equal(2, await db.Payments.CountAsync(payment => payment.InvoiceId == invoice.InvoiceEntityId));
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
        db.Entry(installmentPayment!).State = EntityState.Detached;
        installmentPayment!.Description = "Updated";

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.Update(
            installmentPayment.Id,
            installmentPayment,
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(actionResult);
        Assert.Contains(InvoiceManagedPaymentRules.UpdateBlockedMessage, conflict.Value?.ToString());
    }

    [Fact]
    public async Task Update_AllowsLegacyInvoiceDocumentWithoutInvoiceLink_AndInfersExpenseClient()
    {
        await using var db = await CreateContextAsync();

        var client = new Client { Name = "Безняков", CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var clientCase = new ClientCase
        {
            ClientId = client.Id,
            Title = "Банкротство",
            Status = ClientCaseStatus.Open,
            CreatedAt = DateTime.UtcNow,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 1, 30), 15_000m, "Legacy expense");
        payment.Type = PaymentType.Expense;
        payment.DocumentKind = DocumentKind.Invoice;
        payment.ClientId = client.Id;
        payment.ClientCaseId = clientCase.Id;
        payment.ExpenseClientId = null;
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var model = CreatePayment(payment.Date, payment.Amount, payment.Description);
        model.Type = PaymentType.Expense;
        model.Status = PaymentStatus.Cancelled;
        model.DocumentKind = DocumentKind.Invoice;
        model.ClientId = client.Id;
        model.ClientCaseId = clientCase.Id;
        model.ExpenseClientId = null;

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.Update(payment.Id, model, CancellationToken.None);
        db.ChangeTracker.Clear();

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var updated = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(PaymentStatus.Cancelled, updated.Status);
        Assert.Equal(client.Id, updated.ExpenseClientId);

        var stored = await db.Payments.FindAsync(payment.Id);
        Assert.NotNull(stored);
        Assert.Equal(client.Id, stored!.ExpenseClientId);
    }

    [Fact]
    public async Task Update_AppendsSystemNoteTimestampInProfileTimeZone()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "admin", Description = "Admin" };
        var user = new User
        {
            Email = "payment-timezone@example.test",
            IsApproved = true,
            FullName = "Payment Timezone User",
            IsActive = true,
            Role = role,
        };
        db.Roles.Add(role);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Samara",
        });

        var payment = CreatePayment(new DateTime(2026, 1, 30), 15_000m, "Timezone payment");
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var model = CreatePayment(payment.Date, 16_000m, payment.Description);
        model.Status = payment.Status;
        var controller = new PaymentsController(db, new NoopFormRequirementsService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                            new Claim(ClaimTypes.Name, user.FullName),
                            new Claim(ClaimTypes.Role, "admin"),
                        ],
                        authenticationType: "Test")),
                },
            },
        };

        var expectedTimeZone = CalendarTimeZoneHelper.ResolveTimeZone("Europe/Samara");
        var before = CalendarTimeZoneHelper.FormatUserDateTime(
            DateTime.UtcNow,
            expectedTimeZone,
            "dd.MM.yyyy (HH:mm:ss)",
            System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.Update(payment.Id, model, CancellationToken.None);

        var after = CalendarTimeZoneHelper.FormatUserDateTime(
            DateTime.UtcNow,
            expectedTimeZone,
            "dd.MM.yyyy (HH:mm:ss)",
            System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        Assert.IsType<OkObjectResult>(actionResult);
        db.ChangeTracker.Clear();

        var stored = await db.Payments.FindAsync(payment.Id);
        Assert.NotNull(stored);
        Assert.True(
            stored!.SystemNotes.Contains(before, StringComparison.Ordinal) ||
            stored.SystemNotes.Contains(after, StringComparison.Ordinal),
            $"System notes must contain timestamp in Europe/Samara. Expected {before} or {after}. Notes: {stored.SystemNotes}");
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

        await AuthenticatePaymentWriterAsync(db, controller);
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
    public async Task UpdateInvoiceManaged_WhenDueDateChanges_SyncsPlannedDateAndInstallmentScheduleDate()
    {
        await using var db = await CreateContextAsync();
        var invoice = await CreateInvoiceAggregateAsync(db);
        var installmentPayment = await db.Payments.FindAsync(invoice.InstallmentPaymentIds[0]);
        Assert.NotNull(installmentPayment);

        installmentPayment!.Date = new DateTime(2026, 4, 8);
        installmentPayment.PlannedDate = new DateTime(2026, 4, 8);
        installmentPayment.SystemNotes = string.Join(
            "\n",
            $"{InstallmentPaymentMarker} {{\"sequence\":1,\"startDate\":\"2026-04-08\",\"item\":{{\"sequence\":1,\"date\":\"2026-04-08\",\"principal\":6000,\"interest\":0,\"payment\":6000,\"balance\":12000}}}}",
            "Платеж 1/3");
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var model = CreatePayment(new DateTime(2026, 5, 20), installmentPayment.Amount, "Moved installment payment");
        model.PlannedDate = new DateTime(2026, 4, 8);
        model.InvoiceId = null;
        model.SystemNotes = installmentPayment.SystemNotes;

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.UpdateInvoiceManaged(
            installmentPayment.Id,
            model,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var updated = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(new DateTime(2026, 5, 20), updated.Date);
        Assert.Equal(new DateTime(2026, 5, 20), updated.PlannedDate);

        db.ChangeTracker.Clear();
        var stored = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == installmentPayment.Id);
        Assert.Equal(new DateTime(2026, 5, 20), stored.PlannedDate);

        var markerLine = stored.SystemNotes
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(line => line.StartsWith(InstallmentPaymentMarker, StringComparison.Ordinal));
        using var document = JsonDocument.Parse(markerLine[InstallmentPaymentMarker.Length..].Trim());
        Assert.Equal("2026-05-20", document.RootElement.GetProperty("startDate").GetString());
        Assert.Equal("2026-05-20", document.RootElement.GetProperty("item").GetProperty("date").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateInvoiceManaged_SyncsAllowedBindingsButPreservesConflictingHistoricalOwner(bool foreignOwner)
    {
        await using var db = await CreateContextAsync();

        var invoiceClient = new Client { Name = "Correct invoice client" };
        var staleClient = new Client { Name = "Stale installment client" };
        var staleCase = new ClientCase { Client = foreignOwner ? staleClient : invoiceClient, Title = "Stale installment case" };
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
        installment.ClientId = foreignOwner ? staleClient.Id : invoiceClient.Id;
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

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.UpdateInvoiceManaged(installment.Id, update, CancellationToken.None);

        if (foreignOwner)
        {
            Assert.IsType<NotFoundResult>(actionResult);
            db.ChangeTracker.Clear();
            var unchanged = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == installment.Id);
            Assert.Equal(staleClient.Id, unchanged.ClientId);
            Assert.Equal(staleCase.Id, unchanged.ClientCaseId);
            Assert.Equal(invoice.Id, unchanged.InvoiceId);
            Assert.Equal(20_000m, unchanged.Amount);
            Assert.Equal(new DateTime(2026, 4, 30), unchanged.Date);
            Assert.Equal("Installment 1/17", unchanged.Description);
            return;
        }
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
    public async Task UpdateInvoiceManaged_PreservesSubmittedPaymentClassificationForInstallment()
    {
        await using var db = await CreateContextAsync();

        var client = new Client { Name = "Invoice client" };
        var invoice = new Invoice { Number = "33", Date = new DateTime(2026, 4, 3) };
        var bankSource = new PaymentSource { Name = "Bank" };
        var cashSource = new PaymentSource { Name = "Наличные" };
        var parentIncomeType = new IncomeType { Name = "Parent income", PaymentType = PaymentType.Income };
        var serviceIncomeType = new IncomeType { Name = "Service income", PaymentType = PaymentType.Income };
        var parentDealType = new DealType { Name = "Parent deal" };
        var legalDealType = new DealType { Name = "Legal services" };
        var parentService = new ServiceDictionaryItem { Name = "Parent service" };
        var legalService = new ServiceDictionaryItem { Name = "Юридическая услуга" };
        var draftPaymentStatus = new PaymentStatusEntity { Name = "Draft" };
        var completedPaymentStatus = new PaymentStatusEntity { Name = "Completed" };
        db.AddRange(
            client,
            invoice,
            bankSource,
            cashSource,
            parentIncomeType,
            serviceIncomeType,
            parentDealType,
            legalDealType,
            parentService,
            legalService,
            draftPaymentStatus,
            completedPaymentStatus);
        await db.SaveChangesAsync();

        var representative = CreatePayment(invoice.Date, 200_000m, "Invoice 33");
        representative.DocumentKind = DocumentKind.Invoice;
        representative.InvoiceId = invoice.Id;
        representative.Account = invoice.Number;
        representative.AccountDate = invoice.Date;
        representative.ClientId = client.Id;
        representative.PaymentSourceId = bankSource.Id;
        representative.IncomeTypeId = parentIncomeType.Id;
        representative.DealTypeId = parentDealType.Id;
        representative.ServiceId = parentService.Id;
        representative.PaymentStatusId = draftPaymentStatus.Id;

        var installment = CreatePayment(new DateTime(2026, 4, 20), 25_000m, "Installment 1/8");
        installment.InvoiceId = invoice.Id;
        installment.Account = invoice.Number;
        installment.AccountDate = invoice.Date;
        installment.ClientId = client.Id;
        installment.SystemNotes = $"{InstallmentPaymentMarker} {{\"Sequence\":1}}";
        db.Payments.AddRange(representative, installment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var paidDate = new DateTime(2026, 4, 20);
        var update = CreatePayment(installment.Date, installment.Amount, "Cash installment 1/8");
        update.InvoiceId = null;
        update.Account = "BROKEN";
        update.AccountDate = new DateTime(2030, 1, 1);
        update.SystemNotes = installment.SystemNotes;
        update.PaymentSourceId = cashSource.Id;
        update.IncomeTypeId = serviceIncomeType.Id;
        update.DealTypeId = legalDealType.Id;
        update.ServiceId = legalService.Id;
        update.PaymentStatusId = completedPaymentStatus.Id;
        update.Status = PaymentStatus.Completed;
        update.PaidAmount = installment.Amount;
        update.PaidDate = paidDate;
        update.LastPaymentDate = paidDate;

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.UpdateInvoiceManaged(installment.Id, update, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var updated = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(invoice.Id, updated.InvoiceId);
        Assert.Equal(invoice.Number, updated.Account);
        Assert.Equal(cashSource.Id, updated.PaymentSourceId);
        Assert.Equal(serviceIncomeType.Id, updated.IncomeTypeId);
        Assert.Equal(legalDealType.Id, updated.DealTypeId);
        Assert.Equal(legalService.Id, updated.ServiceId);
        Assert.Equal(completedPaymentStatus.Id, updated.PaymentStatusId);
        Assert.Equal(PaymentStatus.Completed, updated.Status);
        Assert.True(updated.IsPaid);
        Assert.StartsWith("ПКО-2026-", updated.PaymentOrderNumber);
        Assert.Equal(paidDate, updated.PaymentDocumentDate);

        db.ChangeTracker.Clear();
        var stored = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == installment.Id);
        Assert.Equal(cashSource.Id, stored.PaymentSourceId);
        Assert.Equal(serviceIncomeType.Id, stored.IncomeTypeId);
        Assert.Equal(legalDealType.Id, stored.DealTypeId);
        Assert.Equal(legalService.Id, stored.ServiceId);
        Assert.Equal(completedPaymentStatus.Id, stored.PaymentStatusId);
        Assert.Equal(PaymentStatus.Completed, stored.Status);
        Assert.StartsWith("ПКО-2026-", stored.PaymentOrderNumber);
        Assert.Equal(paidDate, stored.PaymentDocumentDate);

        var cashOrder = await db.CashOrders.AsNoTracking().SingleAsync(order => order.PaymentId == installment.Id);
        Assert.Equal(stored.PaymentOrderNumber, cashOrder.Number);
        Assert.Equal(CashOrderType.Incoming, cashOrder.Type);
        Assert.Equal(CashOrderStatus.Posted, cashOrder.Status);
        Assert.Equal(installment.Amount, cashOrder.Amount);
        Assert.Equal(paidDate, cashOrder.Date);

        db.ChangeTracker.Clear();
        var edited = CreatePayment(installment.Date, installment.Amount, "Cash installment 1/8");
        edited.InvoiceId = null;
        edited.Account = "BROKEN";
        edited.AccountDate = new DateTime(2030, 1, 1);
        edited.SystemNotes = stored.SystemNotes;
        edited.PaymentSourceId = cashSource.Id;
        edited.IncomeTypeId = serviceIncomeType.Id;
        edited.DealTypeId = legalDealType.Id;
        edited.ServiceId = legalService.Id;
        edited.PaymentStatusId = completedPaymentStatus.Id;
        edited.Status = PaymentStatus.Completed;
        edited.PaidAmount = installment.Amount;
        edited.PaidDate = paidDate;
        edited.LastPaymentDate = paidDate;
        edited.PaymentOrderNumber = "ПКО-2026-0099";
        edited.PaymentDocumentDate = paidDate.AddDays(1);

        await AuthenticatePaymentWriterAsync(db, controller);
        var editedActionResult = await controller.UpdateInvoiceManaged(installment.Id, edited, CancellationToken.None);

        var editedOk = Assert.IsType<OkObjectResult>(editedActionResult);
        var editedPayment = Assert.IsType<Payment>(editedOk.Value);
        Assert.Equal("ПКО-2026-0099", editedPayment.PaymentOrderNumber);
        Assert.Equal(paidDate.AddDays(1), editedPayment.PaymentDocumentDate);
        Assert.Equal(legalService.Id, editedPayment.ServiceId);

        db.ChangeTracker.Clear();
        var editedOrder = await db.CashOrders.AsNoTracking().SingleAsync(order => order.PaymentId == installment.Id);
        Assert.Equal("ПКО-2026-0099", editedOrder.Number);
        Assert.Equal(paidDate.AddDays(1), editedOrder.Date);
        Assert.Equal(installment.Amount, editedOrder.Amount);
    }

    [Fact]
    public async Task UpdateInvoiceManaged_WhenCashOrderNumberIsAlreadyUsed_GeneratesFreeNumber()
    {
        await using var db = await CreateContextAsync();

        var occupiedNumber = "\u041f\u041a\u041e-2026-0001";
        var client = new Client { Name = "Invoice client" };
        var invoice = new Invoice { Number = "34", Date = new DateTime(2026, 4, 3) };
        var cashSource = new PaymentSource { Name = "cash" };
        db.AddRange(
            client,
            invoice,
            cashSource,
            new CashOrder
            {
                Number = occupiedNumber,
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 20),
                Amount = 10_000m,
                Currency = "RUB",
                Account = "Main cash",
            });
        await db.SaveChangesAsync();

        var representative = CreatePayment(invoice.Date, 200_000m, "Invoice 34");
        representative.DocumentKind = DocumentKind.Invoice;
        representative.InvoiceId = invoice.Id;
        representative.Account = invoice.Number;
        representative.AccountDate = invoice.Date;
        representative.ClientId = client.Id;

        var installment = CreatePayment(new DateTime(2026, 4, 20), 25_000m, "Installment 1/8");
        installment.InvoiceId = invoice.Id;
        installment.Account = invoice.Number;
        installment.AccountDate = invoice.Date;
        installment.ClientId = client.Id;
        installment.SystemNotes = $"{InstallmentPaymentMarker} {{\"Sequence\":1}}";
        db.Payments.AddRange(representative, installment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var update = CreatePayment(installment.Date, installment.Amount, "Cash installment 1/8");
        update.InvoiceId = null;
        update.Account = "BROKEN";
        update.AccountDate = new DateTime(2030, 1, 1);
        update.SystemNotes = installment.SystemNotes;
        update.PaymentSourceId = cashSource.Id;
        update.Status = PaymentStatus.Completed;
        update.PaidAmount = installment.Amount;
        update.PaidDate = installment.Date;
        update.LastPaymentDate = installment.Date;
        update.PaymentOrderNumber = occupiedNumber;
        update.PaymentDocumentDate = installment.Date;

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.UpdateInvoiceManaged(installment.Id, update, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var updated = Assert.IsType<Payment>(ok.Value);
        Assert.NotEqual(occupiedNumber, updated.PaymentOrderNumber);
        Assert.StartsWith("\u041f\u041a\u041e-2026-", updated.PaymentOrderNumber);

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.CashOrders.CountAsync(order => order.Number == occupiedNumber));
        var linkedOrder = await db.CashOrders.AsNoTracking().SingleAsync(order => order.PaymentId == installment.Id);
        Assert.Equal(updated.PaymentOrderNumber, linkedOrder.Number);
        Assert.Equal(CashOrderType.Incoming, linkedOrder.Type);
        Assert.Equal(CashOrderStatus.Posted, linkedOrder.Status);
    }

    [Fact]
    public async Task UpdateInvoiceManaged_UpdatesRepresentativeInvoicePayment_AndSavesDescription()
    {
        await using var db = await CreateContextAsync();
        var invoice = await CreateInvoiceAggregateAsync(db);
        var parentPayment = await db.Payments.FindAsync(invoice.ParentPaymentId);
        Assert.NotNull(parentPayment);

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var paidDate = new DateTime(2026, 4, 15);
        parentPayment!.Description = "Комментарий для клиента в счете";
        parentPayment.Notes = "Акт №67 от 21.05.2026";
        await db.SaveChangesAsync();

        var model = CreatePayment(parentPayment.Date, parentPayment.Amount, "Комментарий по платежке");
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

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.UpdateInvoiceManaged(
            parentPayment.Id,
            model,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var updated = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(DocumentKind.Invoice, updated.DocumentKind);
        Assert.Equal(invoice.InvoiceEntityId, updated.InvoiceId);
        Assert.Equal("30", updated.Account);
        Assert.Equal("Комментарий по платежке", updated.Description);
        Assert.Equal("Акт №67 от 21.05.2026", updated.Notes);
        Assert.Equal(PaymentStatus.Completed, updated.Status);
        Assert.True(updated.IsPaid);
        Assert.Equal(paidDate, updated.PaidDate);

        db.ChangeTracker.Clear();
        var stored = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == parentPayment.Id);
        Assert.Equal("Комментарий по платежке", stored.Description);
        Assert.Equal("Акт №67 от 21.05.2026", stored.Notes);
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

        var service = new ServiceDictionaryItem
        {
            Name = "Юридическая услуга",
        };

        var invoice = new Invoice
        {
            Number = "INV-401",
            Date = new DateTime(2026, 4, 8),
        };

        db.AddRange(client, clientCase, incomeType, paymentSource, paymentStatus, dealType, service, invoice);
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
            ServiceId = service.Id,
            InvoiceId = invoice.Id,
            Account = "LEGACY-401",
            AccountDate = invoice.Date.AddDays(1),
            PaidAmount = 4_800m,
            PaidDate = new DateTime(2026, 4, 11),
            LastPaymentDate = null,
        };

        await AuthenticatePaymentWriterAsync(db, controller);
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
        Assert.Equal(service.Id, updated.ServiceId);
        Assert.Equal(updateModel.PaidDate!.Value.Date, updated.LastPaymentDate);
        Assert.Contains("Ручное примечание", updated.SystemNotes);
        Assert.Equal(1, updated.SystemNotes.Split('\n').Count(line => line.Contains("Исходная")));

        var stored = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == standalonePayment.Id);
        Assert.Equal(invoice.Id, stored.InvoiceId);
        Assert.Equal(invoice.Number, stored.Account);
        Assert.Equal("LEGACY-401", stored.LegacyAccountNumber);
        Assert.Equal(service.Id, stored.ServiceId);
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

        await AuthenticatePaymentWriterAsync(db, controller);
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
        Assert.Equal(InvoiceWorkflowStatus.Draft, refreshedParent.InvoiceWorkflowStatus);
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
        Assert.Equal(InvoiceWorkflowStatus.Draft, refreshedParent.InvoiceWorkflowStatus);
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

        var controller = await CreateAdminRepairControllerAsync(db);

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
    public async Task Update_WhenDueDateChangesWithoutExplicitPlannedDate_SyncsPlannedDate()
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

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.Update(payment.Id, updateModel, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var updated = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(originalDate.AddDays(3), updated.PlannedDate);
        Assert.Equal(originalDate.AddDays(3), updated.Date);

        var stored = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == payment.Id);
        Assert.Equal(originalDate.AddDays(3), stored.PlannedDate);
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

        await AuthenticatePaymentWriterAsync(db, controller);
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

        await AuthenticatePaymentWriterAsync(db, controller);
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

        var controller = await CreateAdminRepairControllerAsync(db);

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
    public async Task Create_InfersExpenseClientFromCaseWhenExpenseClientIsMissing()
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

        var created = Assert.IsType<CreatedResult>(actionResult);
        var payment = Assert.IsType<Payment>(created.Value);
        Assert.Equal(expenseClient.Id, payment.ExpenseClientId);
        Assert.Equal(clientCase.Id, payment.ClientCaseId);
    }

    [Fact]
    public async Task Create_WithHiddenClientCase_ReturnsBadRequestAndDoesNotCreatePayment()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.All);
        await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Clients, DataVisibilityScope.All);
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.Cases,
            CanView = true,
            ViewScope = DataVisibilityScope.Own,
        });

        var visibleClient = new Client
        {
            Name = "Visible payment attribution client",
            ResponsibleUserId = user.Id,
        };
        db.Clients.Add(visibleClient);
        await db.SaveChangesAsync();

        var hiddenCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            Title = "Hidden payment attribution case",
            ResponsibleUserId = user.Id,
            IsHiddenFromExecutor = true,
            Status = ClientCaseStatus.Open,
        };
        db.ClientCases.Add(hiddenCase);
        await db.SaveChangesAsync();

        var model = CreatePayment(new DateTime(2026, 6, 24), 4_200m, "Hidden case payment");
        model.ClientId = visibleClient.Id;
        model.ClientCaseId = hiddenCase.Id;

        var controller = CreatePaymentsController(db, user.Id);

        var actionResult = await controller.Create(model, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
        Assert.Contains("clientCaseId", badRequest.Value?.ToString());
        Assert.False(await db.Payments.AnyAsync(payment => payment.Description == "Hidden case payment"));
    }

    [Fact]
    public async Task Update_WithHiddenClientCase_ReturnsBadRequestAndKeepsOriginalBinding()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.All);
        await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Clients, DataVisibilityScope.All);
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.Cases,
            CanView = true,
            ViewScope = DataVisibilityScope.Own,
        });

        var visibleClient = new Client
        {
            Name = "Visible payment attribution client",
            ResponsibleUserId = user.Id,
        };
        db.Clients.Add(visibleClient);
        await db.SaveChangesAsync();

        var visibleCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            Title = "Visible payment attribution case",
            ResponsibleUserId = user.Id,
            Status = ClientCaseStatus.Open,
        };
        var hiddenCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            Title = "Hidden payment attribution update case",
            ResponsibleUserId = user.Id,
            IsHiddenFromExecutor = true,
            Status = ClientCaseStatus.Open,
        };
        db.ClientCases.AddRange(visibleCase, hiddenCase);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 6, 24), 5_100m, "Visible payment before hidden case update");
        payment.ClientId = visibleClient.Id;
        payment.ClientCaseId = visibleCase.Id;
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var update = CreatePayment(payment.Date, payment.Amount, "Attempt hidden case update");
        update.ClientId = visibleClient.Id;
        update.ClientCaseId = hiddenCase.Id;

        var controller = CreatePaymentsController(db, user.Id);

        var actionResult = await controller.Update(payment.Id, update, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
        Assert.Contains("clientCaseId", badRequest.Value?.ToString());

        var stored = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == payment.Id);
        Assert.Equal(visibleClient.Id, stored.ClientId);
        Assert.Equal(visibleCase.Id, stored.ClientCaseId);
        Assert.Equal("Visible payment before hidden case update", stored.Description);
    }

    [Fact]
    public async Task Create_WithHiddenClient_ReturnsBadRequestAndDoesNotCreatePayment()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.All);
        var otherUser = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.All);
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.Clients,
            CanView = true,
            ViewScope = DataVisibilityScope.Own,
        });

        var hiddenClient = new Client
        {
            Name = "Hidden direct payment client",
            ResponsibleUserId = otherUser.Id,
        };
        db.Clients.Add(hiddenClient);
        await db.SaveChangesAsync();

        var model = CreatePayment(new DateTime(2026, 6, 24), 3_700m, "Hidden client payment");
        model.ClientId = hiddenClient.Id;

        var controller = CreatePaymentsController(db, user.Id);

        var actionResult = await controller.Create(model, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
        Assert.Contains("clientId", badRequest.Value?.ToString());
        Assert.False(await db.Payments.AnyAsync(payment => payment.Description == "Hidden client payment"));
    }

    [Fact]
    public async Task Create_WithHiddenLegalEntity_ReturnsBadRequestAndDoesNotCreatePayment()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.All);
        var otherUser = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.All);
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.LegalEntities,
            CanView = true,
            ViewScope = DataVisibilityScope.Own,
        });

        var hiddenLegalEntity = new LegalEntity
        {
            ShortName = "Hidden direct payment legal",
            ResponsibleUserId = otherUser.Id,
        };
        db.LegalEntities.Add(hiddenLegalEntity);
        await db.SaveChangesAsync();

        var model = CreatePayment(new DateTime(2026, 6, 24), 6_300m, "Hidden legal payment");
        model.LegalEntityId = hiddenLegalEntity.Id;

        var controller = CreatePaymentsController(db, user.Id);

        var actionResult = await controller.Create(model, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
        Assert.Contains("legalEntityId", badRequest.Value?.ToString());
        Assert.False(await db.Payments.AnyAsync(payment => payment.Description == "Hidden legal payment"));
    }

    [Fact]
    public async Task PaymentsV2_Create_WithHiddenClient_ReturnsBadRequestAndDoesNotCreatePayment()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.All);
        var otherUser = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.All);
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.Clients,
            CanView = true,
            ViewScope = DataVisibilityScope.Own,
        });

        var hiddenClient = new Client
        {
            Name = "Hidden v2 direct payment client",
            ResponsibleUserId = otherUser.Id,
        };
        db.Clients.Add(hiddenClient);
        await db.SaveChangesAsync();

        var model = CreatePayment(new DateTime(2026, 6, 24), 3_900m, "Hidden v2 client payment");
        model.ClientId = hiddenClient.Id;

        var controller = CreatePaymentsV2Controller(db, user.Id);

        var actionResult = await controller.Create(model, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
        Assert.Contains("clientId", badRequest.Value?.ToString());
        Assert.False(await db.Payments.AnyAsync(payment => payment.Description == "Hidden v2 client payment"));
    }

    [Fact]
    public async Task PaymentsV2_Create_WithHiddenLegalEntity_ReturnsBadRequestAndDoesNotCreatePayment()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.All);
        var otherUser = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.All);
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.LegalEntities,
            CanView = true,
            ViewScope = DataVisibilityScope.Own,
        });

        var hiddenLegalEntity = new LegalEntity
        {
            ShortName = "Hidden v2 direct payment legal",
            ResponsibleUserId = otherUser.Id,
        };
        db.LegalEntities.Add(hiddenLegalEntity);
        await db.SaveChangesAsync();

        var model = CreatePayment(new DateTime(2026, 6, 24), 6_700m, "Hidden v2 legal payment");
        model.LegalEntityId = hiddenLegalEntity.Id;

        var controller = CreatePaymentsV2Controller(db, user.Id);

        var actionResult = await controller.Create(model, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
        Assert.Contains("legalEntityId", badRequest.Value?.ToString());
        Assert.False(await db.Payments.AnyAsync(payment => payment.Description == "Hidden v2 legal payment"));
    }

    [Fact]
    public async Task PaymentsV2_Create_WithHiddenClientCase_ReturnsBadRequestAndDoesNotCreatePayment()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.All);
        await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Clients, DataVisibilityScope.All);
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.Cases,
            CanView = true,
            ViewScope = DataVisibilityScope.Own,
        });

        var visibleClient = new Client
        {
            Name = "Visible v2 payment attribution client",
            ResponsibleUserId = user.Id,
        };
        db.Clients.Add(visibleClient);
        await db.SaveChangesAsync();

        var hiddenCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            Title = "Hidden v2 payment attribution case",
            ResponsibleUserId = user.Id,
            IsHiddenFromExecutor = true,
            Status = ClientCaseStatus.Open,
        };
        db.ClientCases.Add(hiddenCase);
        await db.SaveChangesAsync();

        var model = CreatePayment(new DateTime(2026, 6, 24), 4_600m, "Hidden v2 case payment");
        model.ClientId = visibleClient.Id;
        model.ClientCaseId = hiddenCase.Id;

        var controller = CreatePaymentsV2Controller(db, user.Id);

        var actionResult = await controller.Create(model, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
        Assert.Contains("clientCaseId", badRequest.Value?.ToString());
        Assert.False(await db.Payments.AnyAsync(payment => payment.Description == "Hidden v2 case payment"));
    }

    [Fact]
    public async Task PaymentsV1_Create_WithHiddenClient_ReturnsBadRequestAndDoesNotCreatePayment()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.All);
        var otherUser = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.All);
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.Clients,
            CanView = true,
            ViewScope = DataVisibilityScope.Own,
        });

        var hiddenClient = new Client
        {
            Name = "Hidden v1 direct payment client",
            ResponsibleUserId = otherUser.Id,
        };
        db.Clients.Add(hiddenClient);
        await db.SaveChangesAsync();

        var model = CreatePayment(new DateTime(2026, 6, 24), 3_400m, "Hidden v1 client payment");
        model.ClientId = hiddenClient.Id;

        var controller = CreatePaymentsV1Controller(db, user.Id);

        var actionResult = await controller.Create(model, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
        Assert.Contains("clientId", badRequest.Value?.ToString());
        Assert.False(await db.Payments.AnyAsync(payment => payment.Description == "Hidden v1 client payment"));
    }

    [Fact]
    public async Task PaymentsV1_Create_WithHiddenLegalEntity_ReturnsBadRequestAndDoesNotCreatePayment()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.All);
        var otherUser = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.All);
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.LegalEntities,
            CanView = true,
            ViewScope = DataVisibilityScope.Own,
        });

        var hiddenLegalEntity = new LegalEntity
        {
            ShortName = "Hidden v1 direct payment legal",
            ResponsibleUserId = otherUser.Id,
        };
        db.LegalEntities.Add(hiddenLegalEntity);
        await db.SaveChangesAsync();

        var model = CreatePayment(new DateTime(2026, 6, 24), 6_100m, "Hidden v1 legal payment");
        model.LegalEntityId = hiddenLegalEntity.Id;

        var controller = CreatePaymentsV1Controller(db, user.Id);

        var actionResult = await controller.Create(model, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
        Assert.Contains("legalEntityId", badRequest.Value?.ToString());
        Assert.False(await db.Payments.AnyAsync(payment => payment.Description == "Hidden v1 legal payment"));
    }

    [Fact]
    public async Task PaymentsV1_Create_WithHiddenClientCase_ReturnsBadRequestAndDoesNotCreatePayment()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.All);
        await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Clients, DataVisibilityScope.All);
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.Cases,
            CanView = true,
            ViewScope = DataVisibilityScope.Own,
        });

        var visibleClient = new Client
        {
            Name = "Visible v1 payment attribution client",
            ResponsibleUserId = user.Id,
        };
        db.Clients.Add(visibleClient);
        await db.SaveChangesAsync();

        var hiddenCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            Title = "Hidden v1 payment attribution case",
            ResponsibleUserId = user.Id,
            IsHiddenFromExecutor = true,
            Status = ClientCaseStatus.Open,
        };
        db.ClientCases.Add(hiddenCase);
        await db.SaveChangesAsync();

        var model = CreatePayment(new DateTime(2026, 6, 24), 4_300m, "Hidden v1 case payment");
        model.ClientId = visibleClient.Id;
        model.ClientCaseId = hiddenCase.Id;

        var controller = CreatePaymentsV1Controller(db, user.Id);

        var actionResult = await controller.Create(model, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
        Assert.Contains("clientCaseId", badRequest.Value?.ToString());
        Assert.False(await db.Payments.AnyAsync(payment => payment.Description == "Hidden v1 case payment"));
    }

    [Fact]
    public async Task PaymentsV1_Update_WithHiddenClientCase_ReturnsBadRequestAndKeepsOriginalBinding()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.All);
        await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Clients, DataVisibilityScope.All);
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.Cases,
            CanView = true,
            ViewScope = DataVisibilityScope.Own,
        });

        var visibleClient = new Client
        {
            Name = "Visible v1 payment update client",
            ResponsibleUserId = user.Id,
        };
        db.Clients.Add(visibleClient);
        await db.SaveChangesAsync();

        var visibleCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            Title = "Visible v1 payment attribution case",
            ResponsibleUserId = user.Id,
            Status = ClientCaseStatus.Open,
        };
        var hiddenCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            Title = "Hidden v1 payment attribution update case",
            ResponsibleUserId = user.Id,
            IsHiddenFromExecutor = true,
            Status = ClientCaseStatus.Open,
        };
        db.ClientCases.AddRange(visibleCase, hiddenCase);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 6, 24), 5_400m, "Visible v1 payment before hidden case update");
        payment.ClientId = visibleClient.Id;
        payment.ClientCaseId = visibleCase.Id;
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var update = CreatePayment(payment.Date, payment.Amount, "Attempt v1 hidden case update");
        update.ClientId = visibleClient.Id;
        update.ClientCaseId = hiddenCase.Id;

        var controller = CreatePaymentsV1Controller(db, user.Id);

        var actionResult = await controller.Update(payment.Id, update, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
        Assert.Contains("clientCaseId", badRequest.Value?.ToString());

        db.ChangeTracker.Clear();
        var stored = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == payment.Id);
        Assert.Equal(visibleClient.Id, stored.ClientId);
        Assert.Equal(visibleCase.Id, stored.ClientCaseId);
        Assert.Equal("Visible v1 payment before hidden case update", stored.Description);
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
        update.OverpaymentHandling = PaymentOverpaymentHandling.Advance;
        update.OverpaymentComment = "Аванс по делу";

        await AuthenticatePaymentWriterAsync(db, controller);
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
        Assert.Contains("Аванс по делу", payment.SystemNotes);
    }

    [Fact]
    public async Task UpdateInvoiceManaged_WithOverpaymentWithoutDecision_ReturnsConflict()
    {
        await using var db = await CreateContextAsync();

        var client = new Client { Name = "Клиент без решения по переплате", CreatedAt = DateTime.UtcNow };
        var invoice = new Invoice { Number = "OP-1", Date = new DateTime(2026, 4, 25) };
        db.Clients.Add(client);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var installment = CreatePayment(invoice.Date, 10_000m, "Рассрочка OP-1");
        installment.ClientId = client.Id;
        installment.InvoiceId = invoice.Id;
        installment.Account = invoice.Number;
        installment.AccountDate = invoice.Date;
        installment.SystemNotes = $"{InstallmentPaymentMarker}\nПлатеж 1/2";
        db.Payments.Add(installment);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var update = CreatePayment(installment.Date, installment.Amount, installment.Description);
        update.ClientId = client.Id;
        update.InvoiceId = invoice.Id;
        update.Account = invoice.Number;
        update.AccountDate = invoice.Date;
        update.PaidAmount = 10_500m;
        update.Status = PaymentStatus.Completed;
        update.PaidDate = installment.Date;
        update.LastPaymentDate = installment.Date;

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.UpdateInvoiceManaged(installment.Id, update, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(actionResult);
        Assert.Contains("payment_overpayment_decision_required", JsonSerializer.Serialize(conflict.Value));
        Assert.Empty(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task UpdateInvoiceManaged_WithOverpaymentToNextInstallment_PaysNextPayment()
    {
        await using var db = await CreateContextAsync();

        var invoiceDate = DateTime.UtcNow.Date.AddDays(5);
        var client = new Client { Name = "Клиент с зачетом в следующий платеж", CreatedAt = DateTime.UtcNow };
        var invoice = new Invoice { Number = "OP-NEXT", Date = invoiceDate };
        db.Clients.Add(client);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var parentPayment = CreatePayment(invoice.Date, 20_000m, "Счет OP-NEXT");
        parentPayment.ClientId = client.Id;
        parentPayment.DocumentKind = DocumentKind.Invoice;
        parentPayment.InvoiceId = invoice.Id;
        parentPayment.Account = invoice.Number;
        parentPayment.AccountDate = invoice.Date;

        var firstInstallment = CreatePayment(invoice.Date.AddDays(1), 10_000m, "Рассрочка OP-NEXT 1/2");
        firstInstallment.ClientId = client.Id;
        firstInstallment.InvoiceId = invoice.Id;
        firstInstallment.Account = invoice.Number;
        firstInstallment.AccountDate = invoice.Date;
        firstInstallment.SystemNotes = $"{InstallmentPaymentMarker}\nПлатеж 1/2";

        var secondInstallment = CreatePayment(invoice.Date.AddDays(31), 10_000m, "Рассрочка OP-NEXT 2/2");
        secondInstallment.ClientId = client.Id;
        secondInstallment.InvoiceId = invoice.Id;
        secondInstallment.Account = invoice.Number;
        secondInstallment.AccountDate = invoice.Date;
        secondInstallment.SystemNotes = $"{InstallmentPaymentMarker}\nПлатеж 2/2";

        db.Payments.AddRange(parentPayment, firstInstallment, secondInstallment);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var update = CreatePayment(firstInstallment.Date, firstInstallment.Amount, firstInstallment.Description);
        update.ClientId = client.Id;
        update.InvoiceId = invoice.Id;
        update.Account = invoice.Number;
        update.AccountDate = invoice.Date;
        update.PaidAmount = 12_500m;
        update.Status = PaymentStatus.Completed;
        update.PaidDate = firstInstallment.Date;
        update.LastPaymentDate = firstInstallment.Date;
        update.OverpaymentHandling = PaymentOverpaymentHandling.NextInstallment;

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.UpdateInvoiceManaged(firstInstallment.Id, update, CancellationToken.None);

        Assert.IsType<OkObjectResult>(actionResult);
        db.ChangeTracker.Clear();

        var storedFirst = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == firstInstallment.Id);
        var storedSecond = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == secondInstallment.Id);
        var refreshedParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == parentPayment.Id);

        Assert.Equal(10_000m, storedFirst.PaidAmount);
        Assert.Equal(PaymentStatus.Completed, storedFirst.Status);
        Assert.Equal(2_500m, storedSecond.PaidAmount);
        Assert.Equal(PaymentStatus.Processing, storedSecond.Status);
        Assert.Empty(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());
        Assert.Equal(12_500m, refreshedParent.PaidAmount);
        Assert.Equal(PaymentStatus.Processing, refreshedParent.Status);
    }

    [Fact]
    public async Task UpdateInvoiceManaged_WithOverpaymentTarget_PaysSelectedInstallment()
    {
        await using var db = await CreateContextAsync();

        var invoiceDate = DateTime.UtcNow.Date.AddDays(5);
        var client = new Client { Name = "Client with selected installment", CreatedAt = DateTime.UtcNow };
        var invoice = new Invoice { Number = "OP-TARGET", Date = invoiceDate };
        db.Clients.Add(client);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var parentPayment = CreatePayment(invoice.Date, 30_000m, "Invoice OP-TARGET");
        parentPayment.ClientId = client.Id;
        parentPayment.DocumentKind = DocumentKind.Invoice;
        parentPayment.InvoiceId = invoice.Id;
        parentPayment.Account = invoice.Number;
        parentPayment.AccountDate = invoice.Date;

        var firstInstallment = CreatePayment(invoice.Date.AddDays(1), 10_000m, "Installment OP-TARGET 1/3");
        firstInstallment.ClientId = client.Id;
        firstInstallment.InvoiceId = invoice.Id;
        firstInstallment.Account = invoice.Number;
        firstInstallment.AccountDate = invoice.Date;
        firstInstallment.SystemNotes = $"{InstallmentPaymentMarker} {{\"sequence\":1}}";

        var secondInstallment = CreatePayment(invoice.Date.AddDays(31), 10_000m, "Installment OP-TARGET 2/3");
        secondInstallment.ClientId = client.Id;
        secondInstallment.InvoiceId = invoice.Id;
        secondInstallment.Account = invoice.Number;
        secondInstallment.AccountDate = invoice.Date;
        secondInstallment.SystemNotes = $"{InstallmentPaymentMarker} {{\"sequence\":2}}";

        var thirdInstallment = CreatePayment(invoice.Date.AddDays(61), 10_000m, "Installment OP-TARGET 3/3");
        thirdInstallment.ClientId = client.Id;
        thirdInstallment.InvoiceId = invoice.Id;
        thirdInstallment.Account = invoice.Number;
        thirdInstallment.AccountDate = invoice.Date;
        thirdInstallment.SystemNotes = $"{InstallmentPaymentMarker} {{\"sequence\":3}}";

        db.Payments.AddRange(parentPayment, firstInstallment, secondInstallment, thirdInstallment);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var update = CreatePayment(firstInstallment.Date, firstInstallment.Amount, firstInstallment.Description);
        update.ClientId = client.Id;
        update.InvoiceId = invoice.Id;
        update.Account = invoice.Number;
        update.AccountDate = invoice.Date;
        update.PaidAmount = 12_500m;
        update.Status = PaymentStatus.Completed;
        update.PaidDate = firstInstallment.Date;
        update.LastPaymentDate = firstInstallment.Date;
        update.OverpaymentHandling = PaymentOverpaymentHandling.NextInstallment;
        update.OverpaymentTargetPaymentId = thirdInstallment.Id;

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.UpdateInvoiceManaged(firstInstallment.Id, update, CancellationToken.None);

        Assert.IsType<OkObjectResult>(actionResult);
        db.ChangeTracker.Clear();

        var storedFirst = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == firstInstallment.Id);
        var storedSecond = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == secondInstallment.Id);
        var storedThird = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == thirdInstallment.Id);

        Assert.Equal(10_000m, storedFirst.PaidAmount);
        Assert.Equal(0m, storedSecond.PaidAmount);
        Assert.Equal(PaymentStatus.Pending, storedSecond.Status);
        Assert.Equal(2_500m, storedThird.PaidAmount);
        Assert.Equal(PaymentStatus.Processing, storedThird.Status);
        Assert.Empty(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task UpdateInvoiceManaged_WithLegacyInvoiceGroupInstallmentTarget_PaysSelectedInstallment()
    {
        await using var db = await CreateContextAsync();

        var invoiceDate = DateTime.UtcNow.Date.AddDays(5);
        var client = new Client { Name = "Client with invoice-kind installment target", CreatedAt = DateTime.UtcNow };
        var invoice = new Invoice { Number = "OP-INVOICE-KIND", Date = invoiceDate };
        db.Clients.Add(client);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var parentPayment = CreatePayment(invoice.Date, 30_000m, "Invoice OP-INVOICE-KIND");
        parentPayment.ClientId = client.Id;
        parentPayment.DocumentKind = DocumentKind.Invoice;
        parentPayment.InvoiceId = invoice.Id;
        parentPayment.Account = invoice.Number;
        parentPayment.AccountDate = invoice.Date;

        var firstInstallment = CreatePayment(invoice.Date.AddDays(1), 15_000m, "Invoice OP-INVOICE-KIND");
        firstInstallment.ClientId = client.Id;
        firstInstallment.DocumentKind = DocumentKind.Invoice;
        firstInstallment.InvoiceId = invoice.Id;
        firstInstallment.Account = invoice.Number;
        firstInstallment.AccountDate = invoice.Date;

        var secondInstallment = CreatePayment(invoice.Date.AddDays(31), 15_000m, "Invoice OP-INVOICE-KIND");
        secondInstallment.ClientId = client.Id;
        secondInstallment.DocumentKind = DocumentKind.Invoice;
        secondInstallment.InvoiceId = invoice.Id;
        secondInstallment.Account = invoice.Number;
        secondInstallment.AccountDate = invoice.Date;

        db.Payments.AddRange(parentPayment, firstInstallment, secondInstallment);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var update = CreatePayment(firstInstallment.Date, firstInstallment.Amount, firstInstallment.Description);
        update.ClientId = client.Id;
        update.DocumentKind = DocumentKind.Invoice;
        update.InvoiceId = invoice.Id;
        update.Account = invoice.Number;
        update.AccountDate = invoice.Date;
        update.PaidAmount = 30_000m;
        update.Status = PaymentStatus.Completed;
        update.PaidDate = firstInstallment.Date;
        update.LastPaymentDate = firstInstallment.Date;
        update.OverpaymentHandling = PaymentOverpaymentHandling.NextInstallment;
        update.OverpaymentTargetPaymentId = secondInstallment.Id;

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.UpdateInvoiceManaged(firstInstallment.Id, update, CancellationToken.None);

        Assert.IsType<OkObjectResult>(actionResult);
        db.ChangeTracker.Clear();

        var storedFirst = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == firstInstallment.Id);
        var storedSecond = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == secondInstallment.Id);

        Assert.Equal(15_000m, storedFirst.PaidAmount);
        Assert.Equal(PaymentStatus.Completed, storedFirst.Status);
        Assert.Equal(15_000m, storedSecond.PaidAmount);
        Assert.Equal(PaymentStatus.Completed, storedSecond.Status);
        Assert.Empty(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task UpdateInvoiceManaged_WithRefundOverpayment_CreatesRefundOperationAndCashOrder()
    {
        await using var db = await CreateContextAsync();

        var client = new Client { Name = "Клиент с возвратом переплаты", CreatedAt = DateTime.UtcNow };
        var invoice = new Invoice { Number = "OP-REFUND", Date = new DateTime(2026, 4, 25) };
        var paymentSource = new PaymentSource { Name = "Касса" };
        db.Clients.Add(client);
        db.Invoices.Add(invoice);
        db.PaymentSources.Add(paymentSource);
        await db.SaveChangesAsync();

        var invoiceDocument = CreatePayment(invoice.Date, 16_250m, "Счет OP-REFUND");
        invoiceDocument.ClientId = client.Id;
        invoiceDocument.InvoiceId = invoice.Id;
        invoiceDocument.DocumentKind = DocumentKind.Invoice;
        invoiceDocument.Account = invoice.Number;
        invoiceDocument.AccountDate = invoice.Date;
        invoiceDocument.PaymentSourceId = paymentSource.Id;

        var installment = CreatePayment(invoice.Date.AddDays(1), 16_250m, "Рассрочка OP-REFUND");
        installment.ClientId = client.Id;
        installment.InvoiceId = invoice.Id;
        installment.Account = invoice.Number;
        installment.AccountDate = invoice.Date;
        installment.PaymentSourceId = paymentSource.Id;
        installment.SystemNotes = $"{InstallmentPaymentMarker}\nПлатеж 1/1";
        db.Payments.AddRange(invoiceDocument, installment);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var update = CreatePayment(installment.Date, installment.Amount, installment.Description);
        update.ClientId = client.Id;
        update.InvoiceId = invoice.Id;
        update.Account = invoice.Number;
        update.AccountDate = invoice.Date;
        update.PaymentSourceId = paymentSource.Id;
        update.PaidAmount = 17_000m;
        update.Status = PaymentStatus.Completed;
        update.PaidDate = installment.Date;
        update.LastPaymentDate = installment.Date;
        update.OverpaymentHandling = PaymentOverpaymentHandling.Refund;
        update.OverpaymentComment = "Вернуть клиенту";

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.UpdateInvoiceManaged(installment.Id, update, CancellationToken.None);

        Assert.IsType<OkObjectResult>(actionResult);
        Assert.Empty(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());

        var refund = await db.MoneyOperations
            .Include(operation => operation.CashOrders)
            .SingleAsync();
        Assert.Equal(MoneyOperationDirection.Expense, refund.Direction);
        Assert.Equal(MoneyOperationSource.Cash, refund.Source);
        Assert.Equal(750m, refund.Amount);
        Assert.Contains("Возврат переплаты", refund.Purpose);

        var cashOrder = Assert.Single(refund.CashOrders);
        Assert.Equal(CashOrderType.Outgoing, cashOrder.Type);
        Assert.Equal(750m, cashOrder.Amount);
        Assert.Equal(installment.Id, cashOrder.PaymentId);
        Assert.Equal("Вернуть клиенту", cashOrder.Comment);
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

        await AuthenticatePaymentWriterAsync(db, controller);
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
    public async Task ClientBalanceApplyCredit_WithoutPaymentEditPermission_ReturnsForbidAndDoesNotChangePayment()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateBalanceUserAsync(db, canEditPayments: false, viewScope: DataVisibilityScope.All);

        var client = new Client { Name = "Balance apply no edit client", CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 4, 28), 1_000m, "Debt without edit permission");
        payment.ClientId = client.Id;
        payment.PaidAmount = 0m;
        payment.Status = PaymentStatus.Pending;
        db.Payments.Add(payment);
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = client.Id,
            Amount = 800m,
            Type = ClientBalanceTransactionType.Overpayment,
            Direction = ClientBalanceDirection.Credit,
            Description = "Balance credit",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = CreateClientBalancesController(db, user.Id);
        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.ApplyCredit(
            client.Id,
            new ApplyClientBalanceRequest { PaymentId = payment.Id, Amount = 800m },
            CancellationToken.None);

        Assert.IsType<ForbidResult>(actionResult);
        db.ChangeTracker.Clear();
        var unchangedPayment = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == payment.Id);
        Assert.Equal(0m, unchangedPayment.PaidAmount);
        Assert.Equal(PaymentStatus.Pending, unchangedPayment.Status);
        Assert.Single(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ClientBalanceSummary_WithOwnScope_ForHiddenClientReturnsNotFound()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateBalanceUserAsync(db, canEditPayments: true, viewScope: DataVisibilityScope.Own);
        var otherUser = await CreateBalanceUserAsync(db, canEditPayments: true, viewScope: DataVisibilityScope.Own);

        var hiddenClient = new Client
        {
            Name = "Hidden balance client",
            ResponsibleUserId = otherUser.Id,
            CreatedAt = DateTime.UtcNow,
        };
        db.Clients.Add(hiddenClient);
        await db.SaveChangesAsync();

        var controller = CreateClientBalancesController(db, user.Id);
        var actionResult = await controller.GetSummary(hiddenClient.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(actionResult);
    }

    [Fact]
    public async Task ClientBalanceRead_WithoutPaymentViewPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateBalanceUserAsync(
            db,
            canEditPayments: true,
            viewScope: DataVisibilityScope.All,
            canViewPayments: false);

        var client = new Client { Name = "Hidden payment balance client", CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            Client = client,
            Amount = 1_000m,
            Type = ClientBalanceTransactionType.Overpayment,
            Direction = ClientBalanceDirection.Credit,
            Description = "Hidden balance",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = CreateClientBalancesController(db, user.Id);

        var summaryResult = await controller.GetSummary(client.Id, CancellationToken.None);
        var transactionsResult = await controller.GetTransactions(client.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(summaryResult);
        Assert.IsType<ForbidResult>(transactionsResult);
    }

    [Fact]
    public async Task ClientBalance_WithLimitedPaymentVisibility_DoesNotExposeOrSpendSharedCredit()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateBalanceUserAsync(db, canEditPayments: true, viewScope: DataVisibilityScope.Own);
        var otherUser = await CreateBalanceUserAsync(db, canEditPayments: true, viewScope: DataVisibilityScope.Own);
        var client = new Client { Name = "Клиент с общим авансом", ResponsibleUserId = user.Id, CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 4, 28), 1_000m, "Счёт клиента");
        payment.ClientId = client.Id;
        payment.PaidAmount = 0m;
        payment.Status = PaymentStatus.Pending;
        payment.Responsibles.Add(new PaymentResponsible { UserId = otherUser.Id });
        db.Payments.Add(payment);
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = client.Id,
            Amount = 800m,
            Type = ClientBalanceTransactionType.Overpayment,
            Direction = ClientBalanceDirection.Credit,
            Description = "Аванс другого сотрудника",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = CreateClientBalancesController(db, user.Id);
        Assert.IsType<ForbidResult>(await controller.GetSummary(client.Id, CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.GetTransactions(client.Id, CancellationToken.None));
        await AuthenticatePaymentWriterAsync(db, controller);
        Assert.IsType<ForbidResult>(await controller.ApplyCredit(
            client.Id,
            new ApplyClientBalanceRequest { PaymentId = payment.Id, Amount = 800m },
            CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync(item => item.Id == payment.Id)).PaidAmount);
        Assert.Single(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ClientBalance_WithFullPaymentVisibility_CanReadAndApplyCredit()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateBalanceUserAsync(db, canEditPayments: true, viewScope: DataVisibilityScope.All);
        var client = new Client { Name = "Клиент с доступным авансом", CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 4, 28), 1_000m, "Счёт клиента");
        payment.ClientId = client.Id;
        payment.PaidAmount = 0m;
        payment.Status = PaymentStatus.Pending;
        db.Payments.Add(payment);
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = client.Id,
            Amount = 800m,
            Type = ClientBalanceTransactionType.Overpayment,
            Direction = ClientBalanceDirection.Credit,
            Description = "Общий аванс",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = CreateClientBalancesController(db, user.Id);
        Assert.IsType<OkObjectResult>(await controller.GetSummary(client.Id, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await controller.GetTransactions(client.Id, CancellationToken.None));
        await AuthenticatePaymentWriterAsync(db, controller);
        Assert.IsType<OkObjectResult>(await controller.ApplyCredit(
            client.Id,
            new ApplyClientBalanceRequest { PaymentId = payment.Id, Amount = 800m },
            CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.Equal(800m, (await db.Payments.AsNoTracking().SingleAsync(item => item.Id == payment.Id)).PaidAmount);
        Assert.Equal(2, await db.ClientBalanceTransactions.CountAsync());
    }

    [Fact]
    public async Task Get_WithoutPaymentViewPermission_ReturnsForbidInsteadOfUnauthorized()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateBalanceUserAsync(
            db,
            canEditPayments: true,
            viewScope: DataVisibilityScope.All,
            canViewPayments: false);
        var controller = CreatePaymentsController(db, user.Id);

        var result = await controller.Get(
            from: null,
            to: null,
            clientId: null,
            caseId: null,
            legalEntityId: null,
            excludeInstallmentParents: true,
            clientRole: null,
            sortBy: null,
            sortDir: null,
            ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task LegalEntityBalanceRead_WithoutPaymentViewPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateBalanceUserAsync(
            db,
            canEditPayments: true,
            viewScope: DataVisibilityScope.All,
            canViewPayments: false);

        var legalEntity = new LegalEntity { ShortName = "Hidden payment balance legal entity" };
        db.LegalEntities.Add(legalEntity);
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            LegalEntity = legalEntity,
            Amount = 1_000m,
            Type = ClientBalanceTransactionType.Overpayment,
            Direction = ClientBalanceDirection.Credit,
            Description = "Hidden legal balance",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = CreateLegalEntityBalancesController(db, user.Id);

        var summaryResult = await controller.GetSummary(legalEntity.Id, CancellationToken.None);
        var transactionsResult = await controller.GetTransactions(legalEntity.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(summaryResult);
        Assert.IsType<ForbidResult>(transactionsResult);
    }

    [Fact]
    public async Task LegalEntityBalance_WithLimitedPaymentVisibility_DoesNotExposeOrSpendSharedCredit()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateBalanceUserAsync(db, canEditPayments: true, viewScope: DataVisibilityScope.Own);
        var otherUser = await CreateBalanceUserAsync(db, canEditPayments: true, viewScope: DataVisibilityScope.Own);
        var legalEntity = new LegalEntity { ShortName = "ООО Общий аванс", ResponsibleUserId = user.Id };
        db.LegalEntities.Add(legalEntity);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 4, 28), 1_000m, "Счёт организации");
        payment.ClientId = null;
        payment.LegalEntityId = legalEntity.Id;
        payment.PaidAmount = 0m;
        payment.Status = PaymentStatus.Pending;
        payment.Responsibles.Add(new PaymentResponsible { UserId = otherUser.Id });
        db.Payments.Add(payment);
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            LegalEntityId = legalEntity.Id,
            Amount = 800m,
            Type = ClientBalanceTransactionType.Overpayment,
            Direction = ClientBalanceDirection.Credit,
            Description = "Аванс другого сотрудника",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = CreateLegalEntityBalancesController(db, user.Id);
        Assert.IsType<ForbidResult>(await controller.GetSummary(legalEntity.Id, CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.GetTransactions(legalEntity.Id, CancellationToken.None));
        await AuthenticatePaymentWriterAsync(db, controller);
        Assert.IsType<ForbidResult>(await controller.ApplyCredit(
            legalEntity.Id,
            new ApplyClientBalanceRequest { PaymentId = payment.Id, Amount = 800m },
            CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync(item => item.Id == payment.Id)).PaidAmount);
        Assert.Single(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task LegalEntityBalance_WithFullPaymentVisibility_CanReadAndApplyCredit()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateBalanceUserAsync(db, canEditPayments: true, viewScope: DataVisibilityScope.All);
        var legalEntity = new LegalEntity { ShortName = "ООО Доступный аванс" };
        db.LegalEntities.Add(legalEntity);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 4, 28), 1_000m, "Счёт организации");
        payment.ClientId = null;
        payment.LegalEntityId = legalEntity.Id;
        payment.PaidAmount = 0m;
        payment.Status = PaymentStatus.Pending;
        db.Payments.Add(payment);
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            LegalEntityId = legalEntity.Id,
            Amount = 800m,
            Type = ClientBalanceTransactionType.Overpayment,
            Direction = ClientBalanceDirection.Credit,
            Description = "Общий аванс",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = CreateLegalEntityBalancesController(db, user.Id);
        Assert.IsType<OkObjectResult>(await controller.GetSummary(legalEntity.Id, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await controller.GetTransactions(legalEntity.Id, CancellationToken.None));
        await AuthenticatePaymentWriterAsync(db, controller);
        Assert.IsType<OkObjectResult>(await controller.ApplyCredit(
            legalEntity.Id,
            new ApplyClientBalanceRequest { PaymentId = payment.Id, Amount = 800m },
            CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.Equal(800m, (await db.Payments.AsNoTracking().SingleAsync(item => item.Id == payment.Id)).PaidAmount);
        Assert.Equal(2, await db.ClientBalanceTransactions.CountAsync());
    }

    [Fact]
    public async Task PaymentStatistics_WithOwnScope_ExcludeHiddenPaymentsInAllRoutes()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateBalanceUserAsync(db, canEditPayments: true, viewScope: DataVisibilityScope.Own);
        var otherUser = await CreateBalanceUserAsync(db, canEditPayments: true, viewScope: DataVisibilityScope.Own);
        var visible = CreatePayment(new DateTime(2026, 4, 28), 100m, "Доступный платёж");
        visible.PaidAmount = 100m;
        visible.Status = PaymentStatus.Completed;
        visible.Responsibles.Add(new PaymentResponsible { UserId = user.Id });
        var hidden = CreatePayment(new DateTime(2026, 4, 28), 900m, "Скрытый платёж");
        hidden.PaidAmount = 900m;
        hidden.Status = PaymentStatus.Completed;
        hidden.Responsibles.Add(new PaymentResponsible { UserId = otherUser.Id });
        db.Payments.AddRange(visible, hidden);
        await db.SaveChangesAsync();

        var context = CreateClientBalancesController(db, user.Id).ControllerContext;
        var monthController = new StatsController(db) { ControllerContext = context };
        var v2Controller = new StatsV2Controller(new StatsSummaryService(db), db) { ControllerContext = context };

        var month = Assert.IsType<MonthlyStats>(Assert.IsType<OkObjectResult>(
            await monthController.Month(2026, 4, CancellationToken.None)).Value);
        Assert.Equal(100m, month.Income);
        Assert.Equal(1, month.Counts.Total);
        Assert.True(month.IsPartial);

        var summary = Assert.IsType<StatsSummary>(Assert.IsType<OkObjectResult>(await v2Controller.Summary(
            null, null, new DateTime(2026, 4, 1), new DateTime(2026, 4, 30),
            null, null, null, null, null, CancellationToken.None)).Value);
        Assert.Equal(100m, summary.Income.CollectedAmount);
        Assert.Equal(1, summary.Income.TotalCount);
        Assert.True(summary.IsPartial);

        var months = Assert.IsType<OkObjectResult>(await v2Controller.Months(2026, 4, 2026, 4, CancellationToken.None));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(months.Value));
        Assert.True(json.RootElement.GetProperty("isPartial").GetBoolean());
        var april = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(100m, april.GetProperty("income").GetDecimal());
        Assert.Equal(1, april.GetProperty("counts").GetProperty("total").GetInt32());

        var fullUser = await CreateBalanceUserAsync(db, canEditPayments: true, viewScope: DataVisibilityScope.All);
        var fullContext = CreateClientBalancesController(db, fullUser.Id).ControllerContext;
        var fullMonthController = new StatsController(db) { ControllerContext = fullContext };
        var fullV2Controller = new StatsV2Controller(new StatsSummaryService(db), db) { ControllerContext = fullContext };
        var fullMonth = Assert.IsType<MonthlyStats>(Assert.IsType<OkObjectResult>(
            await fullMonthController.Month(2026, 4, CancellationToken.None)).Value);
        Assert.Equal(1_000m, fullMonth.Income);
        Assert.False(fullMonth.IsPartial);
        var fullSummary = Assert.IsType<StatsSummary>(Assert.IsType<OkObjectResult>(await fullV2Controller.Summary(
            null, null, new DateTime(2026, 4, 1), new DateTime(2026, 4, 30),
            null, null, null, null, null, CancellationToken.None)).Value);
        Assert.Equal(1_000m, fullSummary.Income.CollectedAmount);
        Assert.False(fullSummary.IsPartial);
        var fullMonths = Assert.IsType<OkObjectResult>(await fullV2Controller.Months(2026, 4, 2026, 4, CancellationToken.None));
        using var fullJson = JsonDocument.Parse(JsonSerializer.Serialize(fullMonths.Value));
        Assert.False(fullJson.RootElement.GetProperty("isPartial").GetBoolean());
        Assert.Equal(1_000m, Assert.Single(fullJson.RootElement.GetProperty("items").EnumerateArray())
            .GetProperty("income").GetDecimal());
    }

    [Fact]
    public async Task PaymentStatistics_WithoutPaymentView_ReturnForbid()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateBalanceUserAsync(
            db, canEditPayments: true, viewScope: DataVisibilityScope.All, canViewPayments: false);
        var context = CreateClientBalancesController(db, user.Id).ControllerContext;
        var monthController = new StatsController(db) { ControllerContext = context };
        var v2Controller = new StatsV2Controller(new StatsSummaryService(db), db) { ControllerContext = context };

        Assert.IsType<ForbidResult>(await monthController.Month(2026, 4, CancellationToken.None));
        Assert.IsType<ForbidResult>(await v2Controller.Summary(
            null, null, null, null, null, null, null, null, null, CancellationToken.None));
        Assert.IsType<ForbidResult>(await v2Controller.Months(2026, 4, 2026, 4, CancellationToken.None));
    }

    [Fact]
    public async Task ApplyCredit_WithSourcePaymentId_UsesOnlySelectedAdvanceRemainder()
    {
        await using var db = await CreateContextAsync();

        var client = new Client { Name = "Клиент с несколькими авансами", CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var sourceAdvance = CreatePayment(new DateTime(2026, 3, 2), 700m, "Первый аванс");
        sourceAdvance.ClientId = client.Id;
        sourceAdvance.PaidAmount = 700m;
        sourceAdvance.Status = PaymentStatus.Completed;
        sourceAdvance.IsPaid = true;
        sourceAdvance.AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance;

        var otherAdvance = CreatePayment(new DateTime(2026, 3, 5), 500m, "Второй аванс");
        otherAdvance.ClientId = client.Id;
        otherAdvance.PaidAmount = 500m;
        otherAdvance.Status = PaymentStatus.Completed;
        otherAdvance.IsPaid = true;
        otherAdvance.AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance;

        var targetPayment = CreatePayment(new DateTime(2026, 3, 10), 1_000m, "Открытый долг");
        targetPayment.ClientId = client.Id;
        targetPayment.PaidAmount = 0m;
        targetPayment.Status = PaymentStatus.Overdue;

        var previousTarget = CreatePayment(new DateTime(2026, 3, 6), 300m, "Ранее оплаченный долг");
        previousTarget.ClientId = client.Id;
        previousTarget.PaidAmount = 300m;
        previousTarget.Status = PaymentStatus.Completed;
        previousTarget.IsPaid = true;
        db.Payments.AddRange(sourceAdvance, otherAdvance, targetPayment, previousTarget);
        await db.SaveChangesAsync();

        db.ClientBalanceTransactions.AddRange(
            new ClientBalanceTransaction
            {
                ClientId = client.Id,
                PaymentId = sourceAdvance.Id,
                Amount = 700m,
                Type = ClientBalanceTransactionType.UnallocatedAdvance,
                Direction = ClientBalanceDirection.Credit,
                Description = "Первый аванс",
                ExternalKey = ClientBalanceService.BuildUnallocatedAdvanceKey(sourceAdvance.Id),
                CreatedAt = DateTime.UtcNow,
            },
            new ClientBalanceTransaction
            {
                ClientId = client.Id,
                PaymentId = previousTarget.Id,
                Amount = 300m,
                Type = ClientBalanceTransactionType.CreditApplied,
                Direction = ClientBalanceDirection.Debit,
                Description = "Ранее зачтено из первого аванса",
                ExternalKey = ClientBalanceService.BuildUnallocatedAdvanceApplicationKey(sourceAdvance.Id, previousTarget.Id, new DateTime(2026, 3, 6, 10, 0, 0, DateTimeKind.Utc)),
                CreatedAt = DateTime.UtcNow,
            },
            new ClientBalanceTransaction
            {
                ClientId = client.Id,
                PaymentId = otherAdvance.Id,
                Amount = 500m,
                Type = ClientBalanceTransactionType.UnallocatedAdvance,
                Direction = ClientBalanceDirection.Credit,
                Description = "Второй аванс",
                ExternalKey = ClientBalanceService.BuildUnallocatedAdvanceKey(otherAdvance.Id),
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        db.ObjectAuditEvents.RemoveRange(db.ObjectAuditEvents);
        await db.SaveChangesAsync();

        var controller = new ClientBalancesController(db);

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.ApplyCredit(
            client.Id,
            new ApplyClientBalanceRequest
            {
                PaymentId = targetPayment.Id,
                Amount = 800m,
                SourcePaymentId = sourceAdvance.Id,
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(actionResult);

        var updatedTarget = await db.Payments.FindAsync(targetPayment.Id);
        Assert.NotNull(updatedTarget);
        Assert.Equal(400m, updatedTarget!.PaidAmount);
        Assert.Equal(PaymentStatus.Overdue, updatedTarget.Status);

        var updatedSource = await db.Payments.FindAsync(sourceAdvance.Id);
        Assert.NotNull(updatedSource);
        Assert.Equal(PaymentAdvanceReviewStatus.Allocated, updatedSource!.AdvanceReviewStatus);
        Assert.Equal($"Аванс полностью распределен по платежу #{targetPayment.Id}", updatedSource.AdvanceReviewReason);

        var untouchedAdvance = await db.Payments.FindAsync(otherAdvance.Id);
        Assert.NotNull(untouchedAdvance);
        Assert.Equal(PaymentAdvanceReviewStatus.AcceptedAsAdvance, untouchedAdvance!.AdvanceReviewStatus);

        var selectedAdvanceRemainder = await ClientBalanceService.GetUnallocatedAdvanceAvailableAmountAsync(
            db,
            sourceAdvance.Id,
            client.Id,
            null);
        Assert.Equal(0m, selectedAdvanceRemainder);

        var totalBalance = await ClientBalanceService.GetBalanceAsync(db, client.Id);
        Assert.Equal(500m, totalBalance);

        var newDebit = await db.ClientBalanceTransactions
            .AsNoTracking()
            .OrderByDescending(item => item.Id)
            .FirstAsync(item => item.Type == ClientBalanceTransactionType.CreditApplied);
        Assert.Equal(400m, newDebit.Amount);
        Assert.NotNull(newDebit.ExternalKey);
        Assert.StartsWith(ClientBalanceService.BuildUnallocatedAdvanceApplicationKeyPrefix(sourceAdvance.Id), newDebit.ExternalKey);

        var allocationAuditEvents = await db.ObjectAuditEvents
            .AsNoTracking()
            .Where(item => item.ObjectType == "payment" && item.Action == "Updated")
            .ToListAsync();
        Assert.Contains(allocationAuditEvents, item =>
        {
            if (string.IsNullOrWhiteSpace(item.MetadataJson))
            {
                return false;
            }

            using var metadata = JsonDocument.Parse(item.MetadataJson);
            return metadata.RootElement.TryGetProperty("reason", out var reasonElement)
                && (reasonElement.GetString()?.Contains(
                    $"Контроль аванса: аванс #{sourceAdvance.Id} зачтен в платеж #{targetPayment.Id}",
                    StringComparison.Ordinal) ?? false);
        });
    }

    [Fact]
    public async Task LegalEntityBalance_ApplyCredit_UsesSelectedAdvanceRemainder()
    {
        await using var db = await CreateContextAsync();

        var legalEntity = new LegalEntity { ShortName = "ООО Аванс", Inn = "5400000000" };
        db.LegalEntities.Add(legalEntity);
        await db.SaveChangesAsync();

        var sourceAdvance = CreatePayment(new DateTime(2026, 4, 1), 900m, "Аванс юрлица");
        sourceAdvance.ClientId = null;
        sourceAdvance.LegalEntityId = legalEntity.Id;
        sourceAdvance.PaidAmount = 900m;
        sourceAdvance.Status = PaymentStatus.Completed;
        sourceAdvance.IsPaid = true;
        sourceAdvance.AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance;

        var targetPayment = CreatePayment(new DateTime(2026, 4, 15), 1_200m, "Долг юрлица");
        targetPayment.ClientId = null;
        targetPayment.LegalEntityId = legalEntity.Id;
        targetPayment.PaidAmount = 0m;
        targetPayment.Status = PaymentStatus.Pending;

        db.Payments.AddRange(sourceAdvance, targetPayment);
        await db.SaveChangesAsync();

        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            LegalEntityId = legalEntity.Id,
            PaymentId = sourceAdvance.Id,
            Amount = 900m,
            Type = ClientBalanceTransactionType.UnallocatedAdvance,
            Direction = ClientBalanceDirection.Credit,
            Description = "Аванс юрлица",
            ExternalKey = ClientBalanceService.BuildUnallocatedAdvanceKey(sourceAdvance.Id),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = new LegalEntityBalancesController(db);

        var summaryResult = await controller.GetSummary(legalEntity.Id, CancellationToken.None);
        var summaryOk = Assert.IsType<OkObjectResult>(summaryResult);
        Assert.Equal(900m, Assert.IsType<ClientBalanceSummaryDto>(summaryOk.Value).Balance);

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.ApplyCredit(
            legalEntity.Id,
            new ApplyClientBalanceRequest
            {
                PaymentId = targetPayment.Id,
                Amount = 1_200m,
                SourcePaymentId = sourceAdvance.Id,
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(actionResult);

        var updatedTarget = await db.Payments.FindAsync(targetPayment.Id);
        Assert.NotNull(updatedTarget);
        Assert.Equal(900m, updatedTarget!.PaidAmount);

        var updatedSource = await db.Payments.FindAsync(sourceAdvance.Id);
        Assert.NotNull(updatedSource);
        Assert.Equal(PaymentAdvanceReviewStatus.Allocated, updatedSource!.AdvanceReviewStatus);

        var legalEntityBalance = await ClientBalanceService.GetLegalEntityBalanceAsync(db, legalEntity.Id);
        Assert.Equal(0m, legalEntityBalance);

        var debit = await db.ClientBalanceTransactions
            .AsNoTracking()
            .SingleAsync(item => item.Type == ClientBalanceTransactionType.CreditApplied);
        Assert.Equal(legalEntity.Id, debit.LegalEntityId);
        Assert.Null(debit.ClientId);
        Assert.StartsWith(ClientBalanceService.BuildUnallocatedAdvanceApplicationKeyPrefix(sourceAdvance.Id), debit.ExternalKey);
    }

    [Fact]
    public async Task LegalEntityBalanceApplyCredit_WithOwnScope_ForHiddenEntityReturnsNotFoundAndDoesNotChangePayment()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateBalanceUserAsync(db, canEditPayments: true, viewScope: DataVisibilityScope.Own);
        var otherUser = await CreateBalanceUserAsync(db, canEditPayments: true, viewScope: DataVisibilityScope.Own);

        var hiddenLegalEntity = new LegalEntity
        {
            ShortName = "ООО Скрытый аванс",
            Inn = "5400000011",
            ResponsibleUserId = otherUser.Id,
        };
        db.LegalEntities.Add(hiddenLegalEntity);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 4, 15), 1_200m, "Hidden legal entity debt");
        payment.ClientId = null;
        payment.LegalEntityId = hiddenLegalEntity.Id;
        payment.PaidAmount = 0m;
        payment.Status = PaymentStatus.Pending;
        db.Payments.Add(payment);
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            LegalEntityId = hiddenLegalEntity.Id,
            Amount = 900m,
            Type = ClientBalanceTransactionType.UnallocatedAdvance,
            Direction = ClientBalanceDirection.Credit,
            Description = "Hidden legal entity advance",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = CreateLegalEntityBalancesController(db, user.Id);
        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.ApplyCredit(
            hiddenLegalEntity.Id,
            new ApplyClientBalanceRequest { PaymentId = payment.Id, Amount = 900m },
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(actionResult);
        db.ChangeTracker.Clear();
        var unchangedPayment = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == payment.Id);
        Assert.Equal(0m, unchangedPayment.PaidAmount);
        Assert.Equal(PaymentStatus.Pending, unchangedPayment.Status);
        Assert.Single(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ApplyCredit_WithSourcePaymentId_KeepsSourceAdvanceAcceptedWhenRemainderExists()
    {
        await using var db = await CreateContextAsync();

        var client = new Client { Name = "Клиент с частичным авансом", CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var sourceAdvance = CreatePayment(new DateTime(2026, 5, 1), 1_000m, "Частичный аванс");
        sourceAdvance.ClientId = client.Id;
        sourceAdvance.PaidAmount = 1_000m;
        sourceAdvance.Status = PaymentStatus.Completed;
        sourceAdvance.IsPaid = true;
        sourceAdvance.AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance;

        var targetPayment = CreatePayment(new DateTime(2026, 5, 10), 400m, "Небольшой долг");
        targetPayment.ClientId = client.Id;
        targetPayment.PaidAmount = 0m;
        targetPayment.Status = PaymentStatus.Pending;

        db.Payments.AddRange(sourceAdvance, targetPayment);
        await db.SaveChangesAsync();

        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = client.Id,
            PaymentId = sourceAdvance.Id,
            Amount = 1_000m,
            Type = ClientBalanceTransactionType.UnallocatedAdvance,
            Direction = ClientBalanceDirection.Credit,
            Description = "Частичный аванс",
            ExternalKey = ClientBalanceService.BuildUnallocatedAdvanceKey(sourceAdvance.Id),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = new ClientBalancesController(db);
        await AuthenticatePaymentWriterAsync(db, controller);
        var result = await controller.ApplyCredit(
            client.Id,
            new ApplyClientBalanceRequest
            {
                PaymentId = targetPayment.Id,
                Amount = 400m,
                SourcePaymentId = sourceAdvance.Id,
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);

        var updatedSource = await db.Payments.FindAsync(sourceAdvance.Id);
        Assert.NotNull(updatedSource);
        Assert.Equal(PaymentAdvanceReviewStatus.AcceptedAsAdvance, updatedSource!.AdvanceReviewStatus);

        var selectedAdvanceRemainder = await ClientBalanceService.GetUnallocatedAdvanceAvailableAmountAsync(
            db,
            sourceAdvance.Id,
            client.Id,
            null);
        Assert.Equal(600m, selectedAdvanceRemainder);
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

        await AuthenticatePaymentWriterAsync(db, controller);
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

        await AuthenticatePaymentWriterAsync(db, controller);
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

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.UpdateInvoiceManaged(firstInstallment.Id, update, CancellationToken.None);

        Assert.IsType<OkObjectResult>(actionResult);
        db.ChangeTracker.Clear();

        var refreshedParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == parentPayment.Id);
        Assert.Equal(6_000m, refreshedParent.PaidAmount);
        Assert.Equal(PaymentStatus.Processing, refreshedParent.Status);
        Assert.Equal(InvoiceWorkflowStatus.Draft, refreshedParent.InvoiceWorkflowStatus);
        Assert.False(refreshedParent.IsPaid);
        Assert.Equal(firstInstallment.Date, refreshedParent.PaidDate!.Value);
        Assert.Equal(secondInstallment.Date, refreshedParent.Date);
    }

    [Fact]
    public async Task TransferPartialRemainder_CreatesRemainderPayment_AndRefreshesParentInvoice()
    {
        await using var db = await CreateContextAsync();

        var today = DateTime.UtcNow.Date;
        var client = new Client { Name = "Клиент переноса остатка", CreatedAt = DateTime.UtcNow };
        var invoice = new Invoice { Number = "REM-1", Date = today.AddDays(-10) };
        db.Clients.Add(client);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var parentPayment = CreatePayment(invoice.Date, 320_000m, "Счет REM-1");
        parentPayment.ClientId = client.Id;
        parentPayment.DocumentKind = DocumentKind.Invoice;
        parentPayment.InvoiceId = invoice.Id;
        parentPayment.Account = invoice.Number;
        parentPayment.AccountDate = invoice.Date;

        var firstInstallment = CreatePayment(today, 80_000m, "Рассрочка по счету REM-1 · платеж 1/4");
        firstInstallment.ClientId = client.Id;
        firstInstallment.InvoiceId = invoice.Id;
        firstInstallment.Account = invoice.Number;
        firstInstallment.AccountDate = invoice.Date;
        firstInstallment.SystemNotes = $"{InstallmentPaymentMarker} {{\"Sequence\":1,\"Months\":4}}";

        var secondInstallment = CreatePayment(today.AddDays(30), 80_000m, "Рассрочка по счету REM-1 · платеж 2/4");
        secondInstallment.ClientId = client.Id;
        secondInstallment.InvoiceId = invoice.Id;
        secondInstallment.Account = invoice.Number;
        secondInstallment.AccountDate = invoice.Date;
        secondInstallment.SystemNotes = $"{InstallmentPaymentMarker} {{\"Sequence\":2,\"Months\":4}}";

        var thirdInstallment = CreatePayment(today.AddDays(60), 80_000m, "Рассрочка по счету REM-1 · платеж 3/4");
        thirdInstallment.ClientId = client.Id;
        thirdInstallment.InvoiceId = invoice.Id;
        thirdInstallment.Account = invoice.Number;
        thirdInstallment.AccountDate = invoice.Date;
        thirdInstallment.SystemNotes = $"{InstallmentPaymentMarker} {{\"Sequence\":3,\"Months\":4}}";

        var fourthInstallment = CreatePayment(today.AddDays(90), 80_000m, "Рассрочка по счету REM-1 · платеж 4/4");
        fourthInstallment.ClientId = client.Id;
        fourthInstallment.InvoiceId = invoice.Id;
        fourthInstallment.Account = invoice.Number;
        fourthInstallment.AccountDate = invoice.Date;
        fourthInstallment.SystemNotes = $"{InstallmentPaymentMarker} {{\"Sequence\":4,\"Months\":4}}";

        db.Payments.AddRange(parentPayment, firstInstallment, secondInstallment, thirdInstallment, fourthInstallment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var paidAmount = 35_000m;
        var remainderDate = today.AddDays(45);
        var update = CreatePayment(today, paidAmount, firstInstallment.Description);
        update.ClientId = client.Id;
        update.InvoiceId = invoice.Id;
        update.Account = invoice.Number;
        update.AccountDate = invoice.Date;
        update.Status = PaymentStatus.Completed;
        update.PaidAmount = paidAmount;
        update.PaidDate = today;
        update.LastPaymentDate = today;
        update.SystemNotes = firstInstallment.SystemNotes;

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.TransferPartialRemainder(
            firstInstallment.Id,
            new TransferPaymentRemainderRequest
            {
                Payment = update,
                RemainderDate = remainderDate,
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var response = Assert.IsType<TransferPaymentRemainderResponse>(ok.Value);
        Assert.True(response.CreatedRemainder);
        Assert.Equal(firstInstallment.Id, response.Payment.Id);
        Assert.Equal(45_000m, response.RemainderPayment.Amount);
        Assert.Equal(remainderDate, response.RemainderPayment.Date);
        Assert.Contains(PaymentRemainderTransferRules.PartialRemainderMarker, response.RemainderPayment.SystemNotes);

        db.ChangeTracker.Clear();
        var storedSource = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == firstInstallment.Id);
        var storedRemainder = await db.Payments.AsNoTracking()
            .SingleAsync(payment => payment.SystemNotes.Contains(PaymentRemainderTransferRules.PartialRemainderMarker));
        var transferLink = await db.PaymentRemainderTransfers.AsNoTracking().SingleAsync();
        var refreshedParent = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == parentPayment.Id);

        Assert.Equal(35_000m, storedSource.Amount);
        Assert.Equal(35_000m, storedSource.PaidAmount);
        Assert.Equal(PaymentStatus.Completed, storedSource.Status);
        Assert.True(storedSource.IsPaid);
        Assert.Equal(invoice.Id, storedRemainder.InvoiceId);
        Assert.Equal(client.Id, storedRemainder.ClientId);
        Assert.Equal(invoice.Number, storedRemainder.Account);
        Assert.Equal(45_000m, storedRemainder.Amount);
        Assert.Equal(0m, storedRemainder.PaidAmount);
        Assert.Equal(PaymentStatus.Pending, storedRemainder.Status);
        Assert.Equal(storedSource.Id, transferLink.SourcePaymentId);
        Assert.Equal(storedRemainder.Id, transferLink.RemainderPaymentId);
        Assert.Equal(80_000m, transferLink.SourceOriginalAmount);
        Assert.Equal(35_000m, transferLink.PaidAmount);
        Assert.Equal(45_000m, transferLink.RemainderAmount);
        Assert.Equal(remainderDate, transferLink.RemainderDate);
        Assert.Equal(320_000m, await db.Payments
            .Where(payment => payment.InvoiceId == invoice.Id && payment.DocumentKind != DocumentKind.Invoice)
            .SumAsync(payment => payment.Amount));
        Assert.Equal(35_000m, refreshedParent.PaidAmount);
        Assert.Equal(PaymentStatus.Processing, refreshedParent.Status);
        Assert.Equal(InvoiceWorkflowStatus.Draft, refreshedParent.InvoiceWorkflowStatus);
    }

    [Fact]
    public async Task TransferPartialRemainder_DoesNotCreateDuplicate_OnRepeatedRequest()
    {
        await using var db = await CreateContextAsync();

        var today = DateTime.UtcNow.Date;
        var client = new Client { Name = "Клиент без дублей", CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var source = CreatePayment(today, 10_000m, "Платеж с остатком");
        source.ClientId = client.Id;
        db.Payments.Add(source);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var update = CreatePayment(today, 4_000m, source.Description);
        update.ClientId = client.Id;
        update.PaidAmount = 4_000m;
        update.PaidDate = today;
        update.LastPaymentDate = today;
        var request = new TransferPaymentRemainderRequest
        {
            Payment = update,
            RemainderDate = today.AddDays(14),
        };

        await AuthenticatePaymentWriterAsync(db, controller);
        var firstResult = await controller.TransferPartialRemainder(source.Id, request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(firstResult);
        db.ChangeTracker.Clear();

        await AuthenticatePaymentWriterAsync(db, controller);
        var secondResult = await controller.TransferPartialRemainder(source.Id, request, CancellationToken.None);
        var secondOk = Assert.IsType<OkObjectResult>(secondResult);
        var secondResponse = Assert.IsType<TransferPaymentRemainderResponse>(secondOk.Value);

        Assert.False(secondResponse.CreatedRemainder);
        Assert.Equal(1, await db.Payments.CountAsync(payment =>
            payment.SystemNotes.Contains(PaymentRemainderTransferRules.PartialRemainderMarker)));
        Assert.Equal(1, await db.PaymentRemainderTransfers.CountAsync());
        Assert.Equal(6_000m, secondResponse.RemainderPayment.Amount);
    }

    [Fact]
    public async Task Delete_RejectsPartialRemainderPayment_AndItsSource()
    {
        await using var db = await CreateContextAsync();

        var today = DateTime.UtcNow.Date;
        var client = new Client { Name = "Клиент с защищенным остатком", CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var source = CreatePayment(today, 10_000m, "Платеж с защищенным остатком");
        source.ClientId = client.Id;
        db.Payments.Add(source);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var update = CreatePayment(today, 4_000m, source.Description);
        update.ClientId = client.Id;
        update.PaidAmount = 4_000m;
        update.PaidDate = today;
        update.LastPaymentDate = today;

        await AuthenticatePaymentWriterAsync(db, controller);
        var splitResult = await controller.TransferPartialRemainder(
            source.Id,
            new TransferPaymentRemainderRequest
            {
                Payment = update,
                RemainderDate = today.AddDays(14),
            },
            CancellationToken.None);
        var splitOk = Assert.IsType<OkObjectResult>(splitResult);
        var splitResponse = Assert.IsType<TransferPaymentRemainderResponse>(splitOk.Value);
        await ClearSystemNotesAsync(db, source.Id, splitResponse.RemainderPayment.Id);

        var sourceDelete = await controller.Delete(source.Id, CancellationToken.None);
        var sourceConflict = Assert.IsType<ConflictObjectResult>(sourceDelete);
        Assert.Contains(PaymentRemainderTransferRules.SourceDeleteBlockedMessage, sourceConflict.Value?.ToString());

        var remainderDelete = await controller.Delete(splitResponse.RemainderPayment.Id, CancellationToken.None);
        var remainderConflict = Assert.IsType<ConflictObjectResult>(remainderDelete);
        Assert.Contains(PaymentRemainderTransferRules.RemainderDeleteBlockedMessage, remainderConflict.Value?.ToString());

        Assert.NotNull(await db.Payments.FindAsync(source.Id));
        Assert.NotNull(await db.Payments.FindAsync(splitResponse.RemainderPayment.Id));
    }

    [Fact]
    public async Task Update_ProtectsPartialRemainderAmounts_ButAllowsPayingRemainder()
    {
        await using var db = await CreateContextAsync();

        var today = DateTime.UtcNow.Date;
        var client = new Client { Name = "Клиент с оплатой остатка", CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var source = CreatePayment(today, 10_000m, "Платеж с оплачиваемым остатком");
        source.ClientId = client.Id;
        db.Payments.Add(source);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var update = CreatePayment(today, 4_000m, source.Description);
        update.ClientId = client.Id;
        update.PaidAmount = 4_000m;
        update.PaidDate = today;
        update.LastPaymentDate = today;

        await AuthenticatePaymentWriterAsync(db, controller);
        var splitResult = await controller.TransferPartialRemainder(
            source.Id,
            new TransferPaymentRemainderRequest
            {
                Payment = update,
                RemainderDate = today.AddDays(14),
            },
            CancellationToken.None);
        var splitOk = Assert.IsType<OkObjectResult>(splitResult);
        var splitResponse = Assert.IsType<TransferPaymentRemainderResponse>(splitOk.Value);
        await ClearSystemNotesAsync(db, source.Id, splitResponse.RemainderPayment.Id);

        var sourceAmountChange = CreatePayment(today, 5_000m, "Пытаемся поменять исходник");
        sourceAmountChange.ClientId = client.Id;
        sourceAmountChange.PaidAmount = 5_000m;
        sourceAmountChange.PaidDate = today;
        sourceAmountChange.LastPaymentDate = today;

        await AuthenticatePaymentWriterAsync(db, controller);
        var sourceUpdateResult = await controller.Update(source.Id, sourceAmountChange, CancellationToken.None);
        var sourceConflict = Assert.IsType<ConflictObjectResult>(sourceUpdateResult);
        Assert.Contains(PaymentRemainderTransferRules.SourceAmountUpdateBlockedMessage, sourceConflict.Value?.ToString());

        var remainderAmountChange = CreatePayment(today.AddDays(14), 7_000m, "Пытаемся поменять остаток");
        remainderAmountChange.ClientId = client.Id;
        remainderAmountChange.PaidAmount = 0m;

        await AuthenticatePaymentWriterAsync(db, controller);
        var remainderAmountResult = await controller.Update(
            splitResponse.RemainderPayment.Id,
            remainderAmountChange,
            CancellationToken.None);
        var remainderConflict = Assert.IsType<ConflictObjectResult>(remainderAmountResult);
        Assert.Contains(PaymentRemainderTransferRules.RemainderAmountUpdateBlockedMessage, remainderConflict.Value?.ToString());

        var remainderPay = CreatePayment(today.AddDays(14), 6_000m, "Оплачиваем остаток");
        remainderPay.ClientId = client.Id;
        remainderPay.PaidAmount = 6_000m;
        remainderPay.PaidDate = today.AddDays(14);
        remainderPay.LastPaymentDate = today.AddDays(14);
        remainderPay.Status = PaymentStatus.Completed;

        await AuthenticatePaymentWriterAsync(db, controller);
        var remainderPayResult = await controller.Update(
            splitResponse.RemainderPayment.Id,
            remainderPay,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(remainderPayResult);
        var paidRemainder = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(6_000m, paidRemainder.Amount);
        Assert.Equal(6_000m, paidRemainder.PaidAmount);
        Assert.Equal(PaymentStatus.Completed, paidRemainder.Status);
    }

    [Fact]
    public async Task RollbackPartialRemainderTransfer_RestoresSourceAndRemovesUnpaidRemainder()
    {
        await using var db = await CreateContextAsync();

        var today = DateTime.UtcNow.Date;
        var client = new Client { Name = "Клиент с откатом остатка", CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var source = CreatePayment(today, 10_000m, "Платеж с откатом остатка");
        source.ClientId = client.Id;
        db.Payments.Add(source);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var update = CreatePayment(today, 4_000m, source.Description);
        update.ClientId = client.Id;
        update.PaidAmount = 4_000m;
        update.PaidDate = today;
        update.LastPaymentDate = today;

        await AuthenticatePaymentWriterAsync(db, controller);
        var splitResult = await controller.TransferPartialRemainder(
            source.Id,
            new TransferPaymentRemainderRequest
            {
                Payment = update,
                RemainderDate = today.AddDays(14),
            },
            CancellationToken.None);
        var splitOk = Assert.IsType<OkObjectResult>(splitResult);
        var splitResponse = Assert.IsType<TransferPaymentRemainderResponse>(splitOk.Value);
        await ClearSystemNotesAsync(db, source.Id, splitResponse.RemainderPayment.Id);

        await AuthenticatePaymentWriterAsync(db, controller);
        var rollbackResult = await controller.RollbackPartialRemainderTransfer(
            splitResponse.RemainderPayment.Id,
            CancellationToken.None);

        var rollbackOk = Assert.IsType<OkObjectResult>(rollbackResult);
        var rollbackResponse = Assert.IsType<RollbackPaymentRemainderTransferResponse>(rollbackOk.Value);
        db.ChangeTracker.Clear();

        var restoredSource = await db.Payments.FindAsync(source.Id);
        Assert.NotNull(restoredSource);
        Assert.Equal(source.Id, rollbackResponse.Payment.Id);
        Assert.Equal(10_000m, restoredSource!.Amount);
        Assert.Equal(4_000m, restoredSource.PaidAmount);
        Assert.False(restoredSource.IsPaid);
        Assert.DoesNotContain(PaymentRemainderTransferRules.PartialRemainderSourceMarker, restoredSource.SystemNotes);
        Assert.Null(await db.Payments.FindAsync(splitResponse.RemainderPayment.Id));
        Assert.Empty(await db.PaymentRemainderTransfers.AsNoTracking().ToListAsync());
        Assert.Equal(splitResponse.RemainderPayment.Id, rollbackResponse.RemovedRemainderPaymentId);
    }

    [Fact]
    public async Task AdjustPartialRemainderTransfer_RecalculatesSourceAndRemainder()
    {
        await using var db = await CreateContextAsync();

        var today = DateTime.UtcNow.Date;
        var client = new Client { Name = "Клиент с корректировкой остатка", CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var source = CreatePayment(today, 10_000m, "Платеж с корректировкой остатка");
        source.ClientId = client.Id;
        db.Payments.Add(source);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var update = CreatePayment(today, 4_000m, source.Description);
        update.ClientId = client.Id;
        update.PaidAmount = 4_000m;
        update.PaidDate = today;
        update.LastPaymentDate = today;

        await AuthenticatePaymentWriterAsync(db, controller);
        var splitResult = await controller.TransferPartialRemainder(
            source.Id,
            new TransferPaymentRemainderRequest
            {
                Payment = update,
                RemainderDate = today.AddDays(14),
            },
            CancellationToken.None);
        var splitOk = Assert.IsType<OkObjectResult>(splitResult);
        var splitResponse = Assert.IsType<TransferPaymentRemainderResponse>(splitOk.Value);
        await ClearSystemNotesAsync(db, source.Id, splitResponse.RemainderPayment.Id);

        var newRemainderDate = today.AddDays(30);
        await AuthenticatePaymentWriterAsync(db, controller);
        var adjustResult = await controller.AdjustPartialRemainderTransfer(
            source.Id,
            new AdjustPaymentRemainderTransferRequest
            {
                PaidAmount = 5_500m,
                RemainderDate = newRemainderDate,
            },
            CancellationToken.None);

        var adjustOk = Assert.IsType<OkObjectResult>(adjustResult);
        var adjustResponse = Assert.IsType<TransferPaymentRemainderResponse>(adjustOk.Value);
        db.ChangeTracker.Clear();

        var adjustedSource = await db.Payments.FindAsync(source.Id);
        var adjustedRemainder = await db.Payments.FindAsync(splitResponse.RemainderPayment.Id);
        Assert.NotNull(adjustedSource);
        Assert.NotNull(adjustedRemainder);
        Assert.False(adjustResponse.CreatedRemainder);
        Assert.Equal(5_500m, adjustedSource!.Amount);
        Assert.Equal(5_500m, adjustedSource.PaidAmount);
        Assert.Equal(PaymentStatus.Completed, adjustedSource.Status);
        Assert.Equal(4_500m, adjustedRemainder!.Amount);
        Assert.Equal(0m, adjustedRemainder.PaidAmount);
        Assert.Equal(newRemainderDate, adjustedRemainder.Date);
        Assert.Contains(PaymentRemainderTransferRules.PartialRemainderSourceMarker, adjustedSource.SystemNotes);
        Assert.Contains(PaymentRemainderTransferRules.PartialRemainderMarker, adjustedRemainder.SystemNotes);
        var adjustedLink = await db.PaymentRemainderTransfers.AsNoTracking().SingleAsync();
        Assert.Equal(source.Id, adjustedLink.SourcePaymentId);
        Assert.Equal(splitResponse.RemainderPayment.Id, adjustedLink.RemainderPaymentId);
        Assert.Equal(10_000m, adjustedLink.SourceOriginalAmount);
        Assert.Equal(5_500m, adjustedLink.PaidAmount);
        Assert.Equal(4_500m, adjustedLink.RemainderAmount);
        Assert.Equal(newRemainderDate, adjustedLink.RemainderDate);
    }

    [Fact]
    public async Task AdjustPartialRemainderTransfer_RejectsAlreadyPaidRemainder()
    {
        await using var db = await CreateContextAsync();

        var today = DateTime.UtcNow.Date;
        var client = new Client { Name = "Клиент с оплаченным остатком", CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var source = CreatePayment(today, 10_000m, "Платеж с оплаченным остатком");
        source.ClientId = client.Id;
        db.Payments.Add(source);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var update = CreatePayment(today, 4_000m, source.Description);
        update.ClientId = client.Id;
        update.PaidAmount = 4_000m;
        update.PaidDate = today;
        update.LastPaymentDate = today;

        await AuthenticatePaymentWriterAsync(db, controller);
        var splitResult = await controller.TransferPartialRemainder(
            source.Id,
            new TransferPaymentRemainderRequest
            {
                Payment = update,
                RemainderDate = today.AddDays(14),
            },
            CancellationToken.None);
        var splitOk = Assert.IsType<OkObjectResult>(splitResult);
        var splitResponse = Assert.IsType<TransferPaymentRemainderResponse>(splitOk.Value);
        db.ChangeTracker.Clear();

        var remainder = await db.Payments.FindAsync(splitResponse.RemainderPayment.Id);
        Assert.NotNull(remainder);
        remainder!.PaidAmount = remainder.Amount;
        remainder.IsPaid = true;
        remainder.Status = PaymentStatus.Completed;
        remainder.PaidDate = remainder.Date;
        remainder.LastPaymentDate = remainder.Date;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await AuthenticatePaymentWriterAsync(db, controller);
        var adjustResult = await controller.AdjustPartialRemainderTransfer(
            source.Id,
            new AdjustPaymentRemainderTransferRequest
            {
                PaidAmount = 5_000m,
                RemainderDate = today.AddDays(30),
            },
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(adjustResult);
        Assert.Contains(PaymentRemainderTransferRules.RemainderAlreadyProcessedMessage, conflict.Value?.ToString());
    }

    [Fact]
    public async Task GetPartialRemainderCandidates_ReturnsOnlyPastPartialPaymentsWithoutRemainder()
    {
        await using var db = await CreateContextAsync();

        var today = DateTime.UtcNow.Date;
        var suggestedRemainderDate = today.AddDays(14);
        var client = new Client { Name = "Клиент для старых частичных оплат", CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var candidate = CreatePayment(today.AddDays(-12), 10_000m, "Старый частичный платеж");
        candidate.ClientId = client.Id;
        candidate.PaidAmount = 4_000m;
        candidate.LastPaymentDate = today.AddDays(-12);
        candidate.SystemNotes =
            $"Системно: Частичная оплата: 4 000.00 / 10 000.00. Остаток 6 000.00 -> {suggestedRemainderDate:dd.MM.yyyy}.";

        var unpaid = CreatePayment(today.AddDays(-11), 8_000m, "Не оплачен");
        unpaid.ClientId = client.Id;

        var fullPaid = CreatePayment(today.AddDays(-10), 7_000m, "Полностью оплачен");
        fullPaid.ClientId = client.Id;
        fullPaid.PaidAmount = 7_000m;
        fullPaid.IsPaid = true;
        fullPaid.Status = PaymentStatus.Completed;

        var futurePartial = CreatePayment(today.AddDays(5), 6_000m, "Будущая частичная оплата");
        futurePartial.ClientId = client.Id;
        futurePartial.PaidAmount = 2_000m;

        var alreadySplit = CreatePayment(today.AddDays(-9), 9_000m, "Уже разделен");
        alreadySplit.ClientId = client.Id;
        alreadySplit.PaidAmount = 3_000m;

        db.Payments.AddRange(candidate, unpaid, fullPaid, futurePartial, alreadySplit);
        await db.SaveChangesAsync();

        var splitRemainder = CreatePayment(today.AddDays(7), 6_000m, "Остаток уже создан");
        splitRemainder.ClientId = client.Id;
        db.Payments.Add(splitRemainder);
        await db.SaveChangesAsync();
        db.PaymentRemainderTransfers.Add(new PaymentRemainderTransfer
        {
            SourcePaymentId = alreadySplit.Id,
            RemainderPaymentId = splitRemainder.Id,
            SourceOriginalAmount = alreadySplit.Amount,
            PaidAmount = alreadySplit.PaidAmount,
            RemainderAmount = alreadySplit.Amount - alreadySplit.PaidAmount,
            RemainderDate = splitRemainder.Date,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        var actionResult = await controller.GetPartialRemainderCandidates(
            from: null,
            to: null,
            clientId: null,
            caseId: null,
            includeFuture: false,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<PartialPaymentRemainderCandidatesResponse>(ok.Value);

        Assert.Equal(1, response.Total);
        var item = Assert.Single(response.Items);
        Assert.Equal(candidate.Id, item.Payment.Id);
        Assert.Equal(10_000m, item.OriginalAmount);
        Assert.Equal(4_000m, item.PaidAmount);
        Assert.Equal(6_000m, item.RemainderAmount);
        Assert.True(item.DaysPastDue >= 12);
        Assert.Equal(suggestedRemainderDate, item.SuggestedRemainderDate);
        Assert.Equal(6_000m, response.TotalRemainderAmount);
    }

    [Fact]
    public async Task GetPartialRemainderCandidates_ReturnsLegacyShrunkPartialPaymentFromSystemNote()
    {
        await using var db = await CreateContextAsync();

        var today = DateTime.UtcNow.Date;
        var suggestedRemainderDate = today.AddDays(12);
        var client = new Client { Name = "Клиент старого сжатого платежа", CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var candidate = CreatePayment(today.AddDays(-18), 35_000m, "Сжатый платеж с остатком");
        candidate.ClientId = client.Id;
        candidate.PaidAmount = 35_000m;
        candidate.PaidDate = today.AddDays(-18);
        candidate.LastPaymentDate = today.AddDays(-18);
        candidate.IsPaid = true;
        candidate.Status = PaymentStatus.Completed;
        candidate.SystemNotes =
            $"Системно: Частичная оплата: 35 000.00 / 80 000.00 • Остаток 45 000.00 → {suggestedRemainderDate:dd.MM.yyyy}";
        db.Payments.Add(candidate);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        var actionResult = await controller.GetPartialRemainderCandidates(
            from: null,
            to: null,
            clientId: null,
            caseId: null,
            includeFuture: false,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<PartialPaymentRemainderCandidatesResponse>(ok.Value);

        var item = Assert.Single(response.Items);
        Assert.Equal(candidate.Id, item.Payment.Id);
        Assert.Equal(80_000m, item.OriginalAmount);
        Assert.Equal(35_000m, item.PaidAmount);
        Assert.Equal(45_000m, item.RemainderAmount);
        Assert.Equal(suggestedRemainderDate, item.SuggestedRemainderDate);
        Assert.Equal(45_000m, response.TotalRemainderAmount);
    }

    [Fact]
    public async Task BackfillLegacyPartialRemainderLinks_CreatesStructuredLinksFromMarkers()
    {
        await using var db = await CreateContextAsync();

        var today = DateTime.UtcNow.Date;
        var client = new Client { Name = "Клиент для миграции связи остатка", CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var source = CreatePayment(today.AddDays(-5), 10_000m, "Старый исходный платеж");
        source.ClientId = client.Id;
        source.Amount = 4_000m;
        source.PaidAmount = 4_000m;
        source.IsPaid = true;
        source.Status = PaymentStatus.Completed;
        var remainder = CreatePayment(today.AddDays(10), 6_000m, "Старый остаток");
        remainder.ClientId = client.Id;
        db.Payments.AddRange(source, remainder);
        await db.SaveChangesAsync();

        remainder.SystemNotes = PaymentRemainderTransferRules.BuildMarker(
            source.Id,
            10_000m,
            4_000m,
            6_000m,
            remainder.Date);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var created = await PaymentRemainderTransferBackfillService.EnsureLegacyLinksAsync(db);

        Assert.Equal(1, created);
        var link = await db.PaymentRemainderTransfers.AsNoTracking().SingleAsync();
        Assert.Equal(source.Id, link.SourcePaymentId);
        Assert.Equal(remainder.Id, link.RemainderPaymentId);
        Assert.Equal(10_000m, link.SourceOriginalAmount);
        Assert.Equal(4_000m, link.PaidAmount);
        Assert.Equal(6_000m, link.RemainderAmount);
        Assert.Equal(remainder.Date, link.RemainderDate);

        var secondRunCreated = await PaymentRemainderTransferBackfillService.EnsureLegacyLinksAsync(db);
        Assert.Equal(0, secondRunCreated);
        Assert.Equal(1, await db.PaymentRemainderTransfers.CountAsync());
    }

    [Fact]
    public async Task TransferExistingPartialRemainder_CreatesRemainderFromStoredPayment()
    {
        await using var db = await CreateContextAsync();

        var today = DateTime.UtcNow.Date;
        var client = new Client { Name = "Клиент для переноса старого остатка", CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var source = CreatePayment(today.AddDays(-20), 12_000m, "Старый платеж с остатком");
        source.ClientId = client.Id;
        source.PaidAmount = 5_000m;
        source.PaidDate = today.AddDays(-19);
        source.LastPaymentDate = today.AddDays(-19);
        source.Status = PaymentStatus.Overdue;
        db.Payments.Add(source);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var remainderDate = today.AddDays(10);

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.TransferExistingPartialRemainder(
            source.Id,
            new TransferExistingPaymentRemainderRequest { RemainderDate = remainderDate },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var response = Assert.IsType<TransferPaymentRemainderResponse>(ok.Value);
        Assert.True(response.CreatedRemainder);

        db.ChangeTracker.Clear();
        var storedSource = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == source.Id);
        var storedRemainder = await db.Payments.AsNoTracking()
            .SingleAsync(payment => payment.SystemNotes.Contains(PaymentRemainderTransferRules.PartialRemainderMarker));
        var transferLink = await db.PaymentRemainderTransfers.AsNoTracking().SingleAsync();

        Assert.Equal(5_000m, storedSource.Amount);
        Assert.Equal(5_000m, storedSource.PaidAmount);
        Assert.True(storedSource.IsPaid);
        Assert.Equal(PaymentStatus.Completed, storedSource.Status);
        Assert.Contains(PaymentRemainderTransferRules.PartialRemainderSourceMarker, storedSource.SystemNotes);

        Assert.Equal(7_000m, storedRemainder.Amount);
        Assert.Equal(0m, storedRemainder.PaidAmount);
        Assert.False(storedRemainder.IsPaid);
        Assert.Equal(remainderDate, storedRemainder.Date);
        Assert.Equal(client.Id, storedRemainder.ClientId);
        Assert.Equal(storedSource.Id, transferLink.SourcePaymentId);
        Assert.Equal(storedRemainder.Id, transferLink.RemainderPaymentId);
        Assert.Equal(12_000m, transferLink.SourceOriginalAmount);
        Assert.Equal(5_000m, transferLink.PaidAmount);
        Assert.Equal(7_000m, transferLink.RemainderAmount);
        Assert.Equal(remainderDate, transferLink.RemainderDate);
        Assert.Equal(source.Id, response.Payment.Id);
        Assert.Equal(storedRemainder.Id, response.RemainderPayment.Id);
    }

    [Fact]
    public async Task TransferExistingPartialRemainder_UsesLegacyOriginalAmountFromSystemNote()
    {
        await using var db = await CreateContextAsync();

        var today = DateTime.UtcNow.Date;
        var client = new Client { Name = "Клиент переноса сжатого остатка", CreatedAt = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var source = CreatePayment(today.AddDays(-20), 35_000m, "Сжатый старый платеж");
        source.ClientId = client.Id;
        source.PaidAmount = 35_000m;
        source.PaidDate = today.AddDays(-19);
        source.LastPaymentDate = today.AddDays(-19);
        source.IsPaid = true;
        source.Status = PaymentStatus.Completed;
        source.SystemNotes =
            $"Системно: Частичная оплата: 35 000.00 / 80 000.00 • Остаток 45 000.00 → {today.AddDays(10):dd.MM.yyyy}";
        db.Payments.Add(source);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        var remainderDate = today.AddDays(10);

        await AuthenticatePaymentWriterAsync(db, controller);
        var actionResult = await controller.TransferExistingPartialRemainder(
            source.Id,
            new TransferExistingPaymentRemainderRequest { RemainderDate = remainderDate },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var response = Assert.IsType<TransferPaymentRemainderResponse>(ok.Value);
        Assert.True(response.CreatedRemainder);

        db.ChangeTracker.Clear();
        var storedSource = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == source.Id);
        var storedRemainder = await db.Payments.AsNoTracking()
            .SingleAsync(payment => payment.SystemNotes.Contains(PaymentRemainderTransferRules.PartialRemainderMarker));
        var transferLink = await db.PaymentRemainderTransfers.AsNoTracking().SingleAsync();

        Assert.Equal(35_000m, storedSource.Amount);
        Assert.Equal(35_000m, storedSource.PaidAmount);
        Assert.True(storedSource.IsPaid);
        Assert.Equal(PaymentStatus.Completed, storedSource.Status);
        Assert.Contains(PaymentRemainderTransferRules.PartialRemainderSourceMarker, storedSource.SystemNotes);

        Assert.Equal(45_000m, storedRemainder.Amount);
        Assert.Equal(0m, storedRemainder.PaidAmount);
        Assert.False(storedRemainder.IsPaid);
        Assert.Equal(remainderDate, storedRemainder.Date);
        Assert.Equal(client.Id, storedRemainder.ClientId);
        Assert.Equal(source.Id, transferLink.SourcePaymentId);
        Assert.Equal(storedRemainder.Id, transferLink.RemainderPaymentId);
        Assert.Equal(80_000m, transferLink.SourceOriginalAmount);
        Assert.Equal(35_000m, transferLink.PaidAmount);
        Assert.Equal(45_000m, transferLink.RemainderAmount);
        Assert.Equal(remainderDate, transferLink.RemainderDate);
        Assert.Equal(source.Id, response.Payment.Id);
        Assert.Equal(storedRemainder.Id, response.RemainderPayment.Id);
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
        Assert.Equal(InvoiceWorkflowStatus.Draft, refreshedParent.InvoiceWorkflowStatus);
        Assert.False(refreshedParent.IsPaid);
    }

    [Fact]
    public async Task Create_UnallocatedPaidIncomePayment_MarksAdvanceAsRequiresDecision()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false);
        await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Clients, DataVisibilityScope.All);
        var client = new Client
        {
            Name = "Винокуров Валерий Витальевич",
            ResponsibleUserId = user.Id,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id);
        var payment = CreatePayment(new DateTime(2026, 3, 2), 7_100m, "Винокуров - раздел лицевых счетов");
        payment.ClientId = client.Id;
        payment.PaidAmount = 7_100m;
        payment.Status = PaymentStatus.Completed;
        payment.IsPaid = true;
        payment.PaidDate = payment.Date;
        payment.LastPaymentDate = payment.Date;

        var result = await controller.Create(payment, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result);
        var createdPayment = Assert.IsType<Payment>(created.Value);
        Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, createdPayment.AdvanceReviewStatus);
        Assert.Empty(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Create_UnallocatedPaidIncomePayment_AddsAdvanceReviewNotificationForResponsibleUser()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false);
        await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Clients, DataVisibilityScope.All);
        var client = new Client
        {
            Name = "Клиент с авансом",
            ResponsibleUserId = user.Id,
            CreatedAt = DateTime.UtcNow,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id, CreateNotificationService(db));
        var payment = CreatePayment(new DateTime(2026, 3, 2), 7_100m, "Платеж без документов");
        payment.ClientId = client.Id;
        payment.PaidAmount = 7_100m;
        payment.Status = PaymentStatus.Completed;
        payment.IsPaid = true;
        payment.PaidDate = payment.Date;
        payment.LastPaymentDate = payment.Date;

        var result = await controller.Create(payment, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result);
        var createdPayment = Assert.IsType<Payment>(created.Value);
        var notification = Assert.Single(await db.UserNotifications.AsNoTracking().ToListAsync());
        Assert.Equal(user.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.PaymentAdvanceReviewRequired, notification.Type);
        Assert.Equal(PaymentAdvanceReviewService.BuildAdvanceReviewRequiredEventKey(createdPayment.Id), notification.EventKey);
        Assert.Equal("payment", notification.ObjectType);
        Assert.Equal(createdPayment.Id, notification.ObjectId);
        Assert.Equal("rose", notification.Tone);
        Assert.Contains("Платеж требует решения", notification.Title);

        using var metadata = JsonDocument.Parse(notification.MetadataJson);
        Assert.Equal(createdPayment.Id, metadata.RootElement.GetProperty("paymentId").GetInt32());
        Assert.Equal(7_100m, metadata.RootElement.GetProperty("advanceAmount").GetDecimal());
    }

    [Fact]
    public async Task Update_UnallocatedPaidIncomePayment_DoesNotDuplicateAdvanceReviewNotification()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false);
        await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Clients, DataVisibilityScope.All);
        var client = new Client
        {
            Name = "Клиент без дублей уведомлений",
            ResponsibleUserId = user.Id,
            CreatedAt = DateTime.UtcNow,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id, CreateNotificationService(db));
        var payment = CreatePayment(new DateTime(2026, 3, 3), 5_000m, "Первичная оплата без документов");
        payment.ClientId = client.Id;
        payment.PaidAmount = 5_000m;
        payment.Status = PaymentStatus.Completed;
        payment.IsPaid = true;
        payment.PaidDate = payment.Date;
        payment.LastPaymentDate = payment.Date;

        var createResult = await controller.Create(payment, CancellationToken.None);
        var createdPayment = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(createResult).Value);

        var update = CreatePayment(createdPayment.Date, createdPayment.Amount, "Уточненная оплата без документов");
        update.ClientId = client.Id;
        update.PaidAmount = createdPayment.PaidAmount;
        update.Status = PaymentStatus.Completed;
        update.IsPaid = true;
        update.PaidDate = createdPayment.PaidDate;
        update.LastPaymentDate = createdPayment.LastPaymentDate;
        update.AdvanceReviewStatus = createdPayment.AdvanceReviewStatus;

        var updateResult = await controller.Update(createdPayment.Id, update, CancellationToken.None);

        Assert.IsType<OkObjectResult>(updateResult);
        Assert.Single(await db.UserNotifications.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Create_UnallocatedPaidIncomePayment_SkipsAdvanceReviewNotificationWhenTypeDisabled()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false);
        await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Clients, DataVisibilityScope.All);
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot(
                [UserNotificationTypes.PaymentAdvanceReviewRequired],
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase))),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var client = new Client
        {
            Name = "Клиент с отключенным типом",
            ResponsibleUserId = user.Id,
            CreatedAt = DateTime.UtcNow,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id, CreateNotificationService(db));
        var payment = CreatePayment(new DateTime(2026, 3, 4), 3_000m, "Оплата без уведомления");
        payment.ClientId = client.Id;
        payment.PaidAmount = 3_000m;
        payment.Status = PaymentStatus.Completed;
        payment.IsPaid = true;
        payment.PaidDate = payment.Date;
        payment.LastPaymentDate = payment.Date;

        var result = await controller.Create(payment, CancellationToken.None);

        Assert.IsType<CreatedResult>(result);
        Assert.Empty(await db.UserNotifications.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Create_PartiallyPaidUnallocatedIncomePayment_MarksAdvanceAsRequiresDecision()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false);
        await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Clients, DataVisibilityScope.All);
        var client = new Client
        {
            Name = "Клиент с частичным платежом",
            ResponsibleUserId = user.Id,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id);
        var payment = CreatePayment(new DateTime(2026, 3, 5), 10_000m, "Частичный платеж без документов");
        payment.ClientId = client.Id;
        payment.PaidAmount = 4_000m;
        payment.Status = PaymentStatus.Processing;
        payment.IsPaid = false;
        payment.LastPaymentDate = payment.Date;

        var result = await controller.Create(payment, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result);
        var createdPayment = Assert.IsType<Payment>(created.Value);
        Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, createdPayment.AdvanceReviewStatus);
        Assert.Equal(4_000m, createdPayment.PaidAmount);
        Assert.Equal(PaymentStatus.Processing, createdPayment.Status);
        Assert.Empty(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task V2RequiresDecisionFilter_IncludesLegacyUnmarkedAdvanceCandidates()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false);
        var client = new Client { Name = "Клиент со старым авансом" };
        var ignoredClient = new Client { Name = "Клиент с проверенным платежом" };
        db.Clients.AddRange(client, ignoredClient);
        await db.SaveChangesAsync();
        var clientCase = new ClientCase
        {
            ClientId = client.Id,
            Title = "Дело без закрывающих документов",
            Status = ClientCaseStatus.Open,
            CreatedAt = DateTime.UtcNow,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        var contract = new Contract
        {
            Number = "CON-ADV-1",
            Title = "Договор без счета и акта",
            Date = new DateTime(2026, 2, 20),
            CreatedAt = DateTime.UtcNow,
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        var legacyCandidate = CreatePayment(new DateTime(2026, 3, 2), 7_100m, "Старый платеж без решения");
        legacyCandidate.ClientId = client.Id;
        legacyCandidate.PaidAmount = 7_100m;
        legacyCandidate.Status = PaymentStatus.Completed;
        legacyCandidate.IsPaid = true;
        legacyCandidate.PaidDate = legacyCandidate.Date;
        legacyCandidate.LastPaymentDate = legacyCandidate.Date;
        legacyCandidate.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;

        var alreadyMarked = CreatePayment(new DateTime(2026, 3, 3), 2_000m, "Уже требует решения");
        alreadyMarked.ClientId = client.Id;
        alreadyMarked.PaidAmount = 2_000m;
        alreadyMarked.Status = PaymentStatus.Completed;
        alreadyMarked.IsPaid = true;
        alreadyMarked.AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision;

        var caseLinkedCandidate = CreatePayment(new DateTime(2026, 3, 3, 12, 0, 0), 1_500m, "Платеж с делом без документов");
        caseLinkedCandidate.ClientId = client.Id;
        caseLinkedCandidate.ClientCaseId = clientCase.Id;
        caseLinkedCandidate.PaidAmount = 1_500m;
        caseLinkedCandidate.Status = PaymentStatus.Completed;
        caseLinkedCandidate.IsPaid = true;
        caseLinkedCandidate.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;

        var contractLinkedCandidate = CreatePayment(new DateTime(2026, 3, 3, 18, 0, 0), 1_700m, "Платеж с договором без счета и акта");
        contractLinkedCandidate.ClientId = client.Id;
        contractLinkedCandidate.ContractId = contract.Id;
        contractLinkedCandidate.PaidAmount = 1_700m;
        contractLinkedCandidate.Status = PaymentStatus.Completed;
        contractLinkedCandidate.IsPaid = true;
        contractLinkedCandidate.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;

        var ignoredPayment = CreatePayment(new DateTime(2026, 3, 4), 3_000m, "Игнорируемый платеж");
        ignoredPayment.ClientId = ignoredClient.Id;
        ignoredPayment.PaidAmount = 3_000m;
        ignoredPayment.Status = PaymentStatus.Completed;
        ignoredPayment.IsPaid = true;
        ignoredPayment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.Ignored;

        var linkedPayment = CreatePayment(new DateTime(2026, 3, 5), 4_000m, "Платеж со счетом");
        linkedPayment.ClientId = client.Id;
        linkedPayment.PaidAmount = 4_000m;
        linkedPayment.Status = PaymentStatus.Completed;
        linkedPayment.IsPaid = true;
        linkedPayment.Account = "INV-10";
        linkedPayment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;

        var linkedActPayment = CreatePayment(new DateTime(2026, 3, 6), 5_000m, "Платеж с актом");
        linkedActPayment.ClientId = client.Id;
        linkedActPayment.PaidAmount = 5_000m;
        linkedActPayment.Status = PaymentStatus.Completed;
        linkedActPayment.IsPaid = true;
        linkedActPayment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;
        linkedActPayment.Acts.Add(new Act
        {
            Number = "ACT-10",
            Date = linkedActPayment.Date,
            Amount = 5_000m,
            ClientId = client.Id,
        });

        db.Payments.AddRange(
            legacyCandidate,
            alreadyMarked,
            caseLinkedCandidate,
            contractLinkedCandidate,
            ignoredPayment,
            linkedPayment,
            linkedActPayment);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsV2Controller(db, user.Id);
        var page = await controller.Get(
            from: null,
            to: null,
            clientId: null,
            caseId: null,
            advanceReviewStatus: PaymentAdvanceReviewStatus.RequiresDecision,
            sortBy: "date",
            sortDir: "asc",
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(page.Result);
        var response = Assert.IsType<PaymentsPageResponse>(ok.Value);
        Assert.Equal(4, response.Total);
        Assert.Equal(
            [legacyCandidate.Id, alreadyMarked.Id, caseLinkedCandidate.Id, contractLinkedCandidate.Id],
            response.Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task GetAdvanceReviewCandidates_ReturnsDryRunReportWithoutChangingPayments()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false);
        var client = new Client { Name = "Dry-run client" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var legacyCandidate = CreatePayment(new DateTime(2026, 3, 2), 7_100m, "Legacy unallocated payment");
        legacyCandidate.ClientId = client.Id;
        legacyCandidate.PaidAmount = 7_100m;
        legacyCandidate.Status = PaymentStatus.Completed;
        legacyCandidate.IsPaid = true;
        legacyCandidate.PaidDate = legacyCandidate.Date;
        legacyCandidate.LastPaymentDate = legacyCandidate.Date;
        legacyCandidate.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;

        var markedCandidate = CreatePayment(new DateTime(2026, 3, 3), 2_500m, "Already requires decision");
        markedCandidate.ClientId = client.Id;
        markedCandidate.PaidAmount = 2_500m;
        markedCandidate.Status = PaymentStatus.Completed;
        markedCandidate.IsPaid = true;
        markedCandidate.AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision;

        var acceptedAdvance = CreatePayment(new DateTime(2026, 3, 4), 3_000m, "Accepted advance");
        acceptedAdvance.ClientId = client.Id;
        acceptedAdvance.PaidAmount = 3_000m;
        acceptedAdvance.Status = PaymentStatus.Completed;
        acceptedAdvance.IsPaid = true;
        acceptedAdvance.AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance;

        var linkedPayment = CreatePayment(new DateTime(2026, 3, 5), 4_000m, "Linked invoice payment");
        linkedPayment.ClientId = client.Id;
        linkedPayment.PaidAmount = 4_000m;
        linkedPayment.Status = PaymentStatus.Completed;
        linkedPayment.IsPaid = true;
        linkedPayment.Account = "INV-DRY-1";
        linkedPayment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;

        db.Payments.AddRange(legacyCandidate, markedCandidate, acceptedAdvance, linkedPayment);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id);
        var result = await controller.GetAdvanceReviewCandidates(limit: 10, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var report = Assert.IsType<PaymentAdvanceReviewCandidateReportDto>(ok.Value);
        Assert.Equal(2, report.Total);
        Assert.Equal(9_600m, report.TotalAmount);
        Assert.Equal(10, report.Limit);
        Assert.Equal([legacyCandidate.Id, markedCandidate.Id], report.Items.Select(item => item.Id).ToArray());
        Assert.Equal(7_100m, report.Items[0].AdvanceAmount);
        Assert.Equal(client.Id, report.Items[0].ClientId);
        Assert.Equal("Dry-run client", report.Items[0].CounterpartyName);

        db.ChangeTracker.Clear();
        var statuses = await db.Payments
            .AsNoTracking()
            .OrderBy(payment => payment.Id)
            .Select(payment => payment.AdvanceReviewStatus)
            .ToListAsync();
        Assert.Equal(
            [
                PaymentAdvanceReviewStatus.None,
                PaymentAdvanceReviewStatus.RequiresDecision,
                PaymentAdvanceReviewStatus.AcceptedAsAdvance,
                PaymentAdvanceReviewStatus.None,
            ],
            statuses);
        Assert.Empty(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task GetAdvanceReviewCandidates_WithOwnScope_ReturnsOnlyVisiblePayments()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.Own);
        var otherUser = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.Own);
        var visibleClient = new Client { Name = "Visible advance client", ResponsibleUserId = user.Id };
        var hiddenClient = new Client { Name = "Hidden advance client", ResponsibleUserId = otherUser.Id };
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        var visiblePayment = CreatePayment(new DateTime(2026, 3, 2), 7_100m, "Visible unallocated advance");
        visiblePayment.ClientId = visibleClient.Id;
        visiblePayment.PaidAmount = 7_100m;
        visiblePayment.Status = PaymentStatus.Completed;
        visiblePayment.IsPaid = true;
        visiblePayment.PaidDate = visiblePayment.Date;
        visiblePayment.LastPaymentDate = visiblePayment.Date;
        visiblePayment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;

        var hiddenPayment = CreatePayment(new DateTime(2026, 3, 3), 2_500m, "Hidden unallocated advance");
        hiddenPayment.ClientId = hiddenClient.Id;
        hiddenPayment.PaidAmount = 2_500m;
        hiddenPayment.Status = PaymentStatus.Completed;
        hiddenPayment.IsPaid = true;
        hiddenPayment.PaidDate = hiddenPayment.Date;
        hiddenPayment.LastPaymentDate = hiddenPayment.Date;
        hiddenPayment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;

        db.Payments.AddRange(visiblePayment, hiddenPayment);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id);
        var result = await controller.GetAdvanceReviewCandidates(limit: 10, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var report = Assert.IsType<PaymentAdvanceReviewCandidateReportDto>(ok.Value);
        Assert.Equal(1, report.Total);
        Assert.Equal(7_100m, report.TotalAmount);
        Assert.Equal([visiblePayment.Id], report.Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task GetAdvanceReviewDiagnostics_FindsSuspectedPaymentWithoutChangingData()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false);
        var client = new Client { Name = "Винокуров Валерий Витальевич" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var targetPayment = CreatePayment(new DateTime(2026, 3, 2), 7_100m, "Винокуров - нераспределенное поступление");
        targetPayment.ClientId = client.Id;
        targetPayment.PaidAmount = 7_100m;
        targetPayment.Status = PaymentStatus.Completed;
        targetPayment.IsPaid = true;
        targetPayment.PaidDate = targetPayment.Date;
        targetPayment.LastPaymentDate = targetPayment.Date;
        targetPayment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance;

        var otherPayment = CreatePayment(new DateTime(2026, 3, 2), 7_500m, "Винокуров - другой платеж");
        otherPayment.ClientId = client.Id;
        otherPayment.PaidAmount = 7_500m;
        otherPayment.Status = PaymentStatus.Completed;
        otherPayment.IsPaid = true;
        otherPayment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;

        db.Payments.AddRange(targetPayment, otherPayment);
        await db.SaveChangesAsync();

        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = client.Id,
            PaymentId = targetPayment.Id,
            Amount = 7_100m,
            Type = ClientBalanceTransactionType.UnallocatedAdvance,
            Direction = ClientBalanceDirection.Credit,
            Description = "Принят как аванс",
        });
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id);
        var result = await controller.GetAdvanceReviewDiagnostics(
            q: "Винокуров",
            from: new DateTime(2026, 3, 2),
            to: new DateTime(2026, 3, 2),
            amount: 7_100m,
            amountTolerance: 0.01m,
            limit: 10,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var report = Assert.IsType<PaymentAdvanceReviewDiagnosticsResponseDto>(ok.Value);
        Assert.Equal(1, report.Total);
        Assert.Equal(7_100m, report.TotalAmount);
        var item = Assert.Single(report.Items);
        Assert.Equal(targetPayment.Id, item.Id);
        Assert.True(item.IsUnallocatedAdvanceCandidate);
        Assert.False(item.HasInvoiceLink);
        Assert.False(item.HasActLink);
        Assert.False(item.HasAccountNumber);
        Assert.True(item.HasUnallocatedAdvanceBalanceTransaction);
        Assert.Equal(7_100m, item.UnallocatedAdvanceBalanceAmount);
        Assert.Equal(PaymentAdvanceReviewStatus.AcceptedAsAdvance, item.AdvanceReviewStatus);

        db.ChangeTracker.Clear();
        var statuses = await db.Payments
            .AsNoTracking()
            .OrderBy(payment => payment.Id)
            .Select(payment => payment.AdvanceReviewStatus)
            .ToListAsync();
        Assert.Equal(
            [
                PaymentAdvanceReviewStatus.AcceptedAsAdvance,
                PaymentAdvanceReviewStatus.None,
            ],
            statuses);
        Assert.Single(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task GetAdvanceReviewDiagnostics_WithOwnScope_ReturnsOnlyVisiblePayments()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.Own);
        var otherUser = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.Own);
        var visibleClient = new Client { Name = "Винокуров видимый", ResponsibleUserId = user.Id };
        var hiddenClient = new Client { Name = "Винокуров скрытый", ResponsibleUserId = otherUser.Id };
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        var visiblePayment = CreatePayment(new DateTime(2026, 3, 2), 7_100m, "Винокуров visible");
        visiblePayment.ClientId = visibleClient.Id;
        visiblePayment.PaidAmount = 7_100m;
        visiblePayment.Status = PaymentStatus.Completed;
        visiblePayment.IsPaid = true;
        visiblePayment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;

        var hiddenPayment = CreatePayment(new DateTime(2026, 3, 2), 7_100m, "Винокуров hidden");
        hiddenPayment.ClientId = hiddenClient.Id;
        hiddenPayment.PaidAmount = 7_100m;
        hiddenPayment.Status = PaymentStatus.Completed;
        hiddenPayment.IsPaid = true;
        hiddenPayment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;

        db.Payments.AddRange(visiblePayment, hiddenPayment);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id);
        var result = await controller.GetAdvanceReviewDiagnostics(
            q: "Винокуров",
            from: null,
            to: null,
            amount: 7_100m,
            amountTolerance: 0.01m,
            limit: 10,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var report = Assert.IsType<PaymentAdvanceReviewDiagnosticsResponseDto>(ok.Value);
        Assert.Equal(1, report.Total);
        Assert.Equal([visiblePayment.Id], report.Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task BackfillAdvanceReviewCandidates_RequiresExplicitConfirmation()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false);
        var client = new Client { Name = "Backfill confirmation client" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 3, 2), 7_100m, "Candidate without confirmation");
        payment.ClientId = client.Id;
        payment.PaidAmount = 7_100m;
        payment.Status = PaymentStatus.Completed;
        payment.IsPaid = true;
        payment.PaidDate = payment.Date;
        payment.LastPaymentDate = payment.Date;
        payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id);
        var result = await controller.BackfillAdvanceReviewCandidates(confirm: false, limit: 10, ct: CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        db.ChangeTracker.Clear();
        var status = await db.Payments
            .AsNoTracking()
            .Where(item => item.Id == payment.Id)
            .Select(item => item.AdvanceReviewStatus)
            .SingleAsync();
        Assert.Equal(PaymentAdvanceReviewStatus.None, status);
    }

    [Fact]
    public async Task BackfillAdvanceReviewCandidates_WithoutEditPermission_ReturnsForbidAndDoesNotChangePayments()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, canEdit: false);
        var client = new Client { Name = "Backfill no edit client" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 3, 2), 7_100m, "Candidate without edit permission");
        payment.ClientId = client.Id;
        payment.PaidAmount = 7_100m;
        payment.Status = PaymentStatus.Completed;
        payment.IsPaid = true;
        payment.PaidDate = payment.Date;
        payment.LastPaymentDate = payment.Date;
        payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id);
        var result = await controller.BackfillAdvanceReviewCandidates(confirm: true, limit: 10, ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        db.ChangeTracker.Clear();
        var status = await db.Payments
            .AsNoTracking()
            .Where(item => item.Id == payment.Id)
            .Select(item => item.AdvanceReviewStatus)
            .SingleAsync();
        Assert.Equal(PaymentAdvanceReviewStatus.None, status);
        Assert.Empty(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task BackfillAdvanceReviewCandidates_WithOwnScope_MarksOnlyVisiblePayments()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.Own);
        var otherUser = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.Own);
        var visibleClient = new Client { Name = "Visible backfill client", ResponsibleUserId = user.Id };
        var hiddenClient = new Client { Name = "Hidden backfill client", ResponsibleUserId = otherUser.Id };
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        var visiblePayment = CreatePayment(new DateTime(2026, 3, 2), 7_100m, "Visible backfill candidate");
        visiblePayment.ClientId = visibleClient.Id;
        visiblePayment.PaidAmount = 7_100m;
        visiblePayment.Status = PaymentStatus.Completed;
        visiblePayment.IsPaid = true;
        visiblePayment.PaidDate = visiblePayment.Date;
        visiblePayment.LastPaymentDate = visiblePayment.Date;
        visiblePayment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;

        var hiddenPayment = CreatePayment(new DateTime(2026, 3, 3), 2_500m, "Hidden backfill candidate");
        hiddenPayment.ClientId = hiddenClient.Id;
        hiddenPayment.PaidAmount = 2_500m;
        hiddenPayment.Status = PaymentStatus.Completed;
        hiddenPayment.IsPaid = true;
        hiddenPayment.PaidDate = hiddenPayment.Date;
        hiddenPayment.LastPaymentDate = hiddenPayment.Date;
        hiddenPayment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;

        db.Payments.AddRange(visiblePayment, hiddenPayment);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id);
        var result = await controller.BackfillAdvanceReviewCandidates(confirm: true, limit: 10, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<PaymentAdvanceReviewBackfillResponseDto>(ok.Value);
        Assert.Equal(1, response.Eligible);
        Assert.Equal(1, response.Updated);
        Assert.Equal([visiblePayment.Id], response.Items.Select(item => item.Id).ToArray());

        db.ChangeTracker.Clear();
        var statuses = await db.Payments
            .AsNoTracking()
            .OrderBy(payment => payment.Id)
            .Select(payment => payment.AdvanceReviewStatus)
            .ToListAsync();
        Assert.Equal(
            [
                PaymentAdvanceReviewStatus.RequiresDecision,
                PaymentAdvanceReviewStatus.None,
            ],
            statuses);
        Assert.Empty(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task BackfillAdvanceReviewCandidates_MarksOnlyLegacyCandidatesWithoutChangingBalance()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false);
        var client = new Client { Name = "Backfill client" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var legacyCandidate = CreatePayment(new DateTime(2026, 3, 2), 7_100m, "Legacy candidate");
        legacyCandidate.ClientId = client.Id;
        legacyCandidate.PaidAmount = 7_100m;
        legacyCandidate.Status = PaymentStatus.Completed;
        legacyCandidate.IsPaid = true;
        legacyCandidate.PaidDate = legacyCandidate.Date;
        legacyCandidate.LastPaymentDate = legacyCandidate.Date;
        legacyCandidate.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;

        var markedCandidate = CreatePayment(new DateTime(2026, 3, 3), 2_500m, "Already marked candidate");
        markedCandidate.ClientId = client.Id;
        markedCandidate.PaidAmount = 2_500m;
        markedCandidate.Status = PaymentStatus.Completed;
        markedCandidate.IsPaid = true;
        markedCandidate.AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision;

        var acceptedAdvance = CreatePayment(new DateTime(2026, 3, 4), 3_000m, "Accepted advance");
        acceptedAdvance.ClientId = client.Id;
        acceptedAdvance.PaidAmount = 3_000m;
        acceptedAdvance.Status = PaymentStatus.Completed;
        acceptedAdvance.IsPaid = true;
        acceptedAdvance.AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance;

        var linkedPayment = CreatePayment(new DateTime(2026, 3, 5), 4_000m, "Linked invoice payment");
        linkedPayment.ClientId = client.Id;
        linkedPayment.PaidAmount = 4_000m;
        linkedPayment.Status = PaymentStatus.Completed;
        linkedPayment.IsPaid = true;
        linkedPayment.Account = "INV-BACKFILL-1";
        linkedPayment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;

        db.Payments.AddRange(legacyCandidate, markedCandidate, acceptedAdvance, linkedPayment);
        await db.SaveChangesAsync();
        db.ObjectAuditEvents.RemoveRange(db.ObjectAuditEvents);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id);
        var result = await controller.BackfillAdvanceReviewCandidates(confirm: true, limit: 10, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<PaymentAdvanceReviewBackfillResponseDto>(ok.Value);
        Assert.Equal(1, response.Eligible);
        Assert.Equal(1, response.Updated);
        Assert.Equal(7_100m, response.TotalAmount);
        Assert.Equal([legacyCandidate.Id], response.Items.Select(item => item.Id).ToArray());
        Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, response.Items[0].AdvanceReviewStatus);

        db.ChangeTracker.Clear();
        var statuses = await db.Payments
            .AsNoTracking()
            .OrderBy(payment => payment.Id)
            .Select(payment => payment.AdvanceReviewStatus)
            .ToListAsync();
        Assert.Equal(
            [
                PaymentAdvanceReviewStatus.RequiresDecision,
                PaymentAdvanceReviewStatus.RequiresDecision,
                PaymentAdvanceReviewStatus.AcceptedAsAdvance,
                PaymentAdvanceReviewStatus.None,
            ],
            statuses);
        Assert.Empty(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());

        var auditEvent = await db.ObjectAuditEvents
            .AsNoTracking()
            .SingleAsync(item => item.ObjectType == "payment" && item.ObjectId == legacyCandidate.Id && item.Action == "Updated");
        Assert.NotNull(auditEvent.MetadataJson);
        using var metadata = JsonDocument.Parse(auditEvent.MetadataJson!);
        var reason = metadata.RootElement.GetProperty("reason").GetString();
        Assert.Contains("Backfill контроля авансов", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RollbackAdvanceReviewBackfill_RequiresExplicitConfirmation()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false);

        var controller = CreatePaymentsController(db, user.Id);
        var result = await controller.RollbackAdvanceReviewBackfill(confirm: false, limit: 10, ct: CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RollbackAdvanceReviewBackfill_WithoutEditPermission_ReturnsForbid()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, canEdit: false);

        var controller = CreatePaymentsController(db, user.Id);
        var result = await controller.RollbackAdvanceReviewBackfill(confirm: true, limit: 10, ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task RollbackAdvanceReviewBackfill_RevertsOnlyBackfilledPayments()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false);
        var client = new Client { Name = "Rollback client" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var legacyCandidate = CreatePayment(new DateTime(2026, 3, 2), 7_100m, "Legacy candidate for rollback");
        legacyCandidate.ClientId = client.Id;
        legacyCandidate.PaidAmount = 7_100m;
        legacyCandidate.Status = PaymentStatus.Completed;
        legacyCandidate.IsPaid = true;
        legacyCandidate.PaidDate = legacyCandidate.Date;
        legacyCandidate.LastPaymentDate = legacyCandidate.Date;
        legacyCandidate.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;

        var markedCandidate = CreatePayment(new DateTime(2026, 3, 3), 2_500m, "Already marked candidate");
        markedCandidate.ClientId = client.Id;
        markedCandidate.PaidAmount = 2_500m;
        markedCandidate.Status = PaymentStatus.Completed;
        markedCandidate.IsPaid = true;
        markedCandidate.AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision;

        var acceptedAdvance = CreatePayment(new DateTime(2026, 3, 4), 3_000m, "Accepted advance");
        acceptedAdvance.ClientId = client.Id;
        acceptedAdvance.PaidAmount = 3_000m;
        acceptedAdvance.Status = PaymentStatus.Completed;
        acceptedAdvance.IsPaid = true;
        acceptedAdvance.AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance;

        db.Payments.AddRange(legacyCandidate, markedCandidate, acceptedAdvance);
        await db.SaveChangesAsync();
        db.ObjectAuditEvents.RemoveRange(db.ObjectAuditEvents);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id);
        var backfillResult = await controller.BackfillAdvanceReviewCandidates(confirm: true, limit: 10, ct: CancellationToken.None);
        var backfillOk = Assert.IsType<OkObjectResult>(backfillResult);
        var backfill = Assert.IsType<PaymentAdvanceReviewBackfillResponseDto>(backfillOk.Value);
        Assert.Equal(1, backfill.Updated);

        var rollbackResult = await controller.RollbackAdvanceReviewBackfill(confirm: true, limit: 10, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(rollbackResult);
        var rollback = Assert.IsType<PaymentAdvanceReviewBackfillRollbackResponseDto>(ok.Value);
        Assert.Equal(1, rollback.Eligible);
        Assert.Equal(1, rollback.Updated);
        Assert.Equal(7_100m, rollback.TotalAmount);
        Assert.Equal([legacyCandidate.Id], rollback.Items.Select(item => item.Id).ToArray());
        Assert.Equal(PaymentAdvanceReviewStatus.None, rollback.Items[0].AdvanceReviewStatus);

        db.ChangeTracker.Clear();
        var statuses = await db.Payments
            .AsNoTracking()
            .OrderBy(payment => payment.Id)
            .Select(payment => payment.AdvanceReviewStatus)
            .ToListAsync();
        Assert.Equal(
            [
                PaymentAdvanceReviewStatus.None,
                PaymentAdvanceReviewStatus.RequiresDecision,
                PaymentAdvanceReviewStatus.AcceptedAsAdvance,
            ],
            statuses);
        Assert.Empty(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());

        var rollbackAudit = await db.ObjectAuditEvents
            .AsNoTracking()
            .Where(item => item.ObjectType == "payment" && item.ObjectId == legacyCandidate.Id && item.Action == "Updated")
            .OrderByDescending(item => item.CreatedAt)
            .FirstAsync();
        Assert.NotNull(rollbackAudit.MetadataJson);
        using var metadata = JsonDocument.Parse(rollbackAudit.MetadataJson!);
        var reason = metadata.RootElement.GetProperty("reason").GetString();
        Assert.Contains("Rollback backfill контроля авансов", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptAdvance_CreatesClientBalanceCredit_AndV2FilterReturnsPayment()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false);
        var client = new Client { Name = "Винокуров Валерий Витальевич" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 3, 2), 7_100m, "Нераспределенный платеж");
        payment.ClientId = client.Id;
        payment.PaidAmount = 7_100m;
        payment.Status = PaymentStatus.Completed;
        payment.IsPaid = true;
        payment.PaidDate = payment.Date;
        payment.LastPaymentDate = payment.Date;
        payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision;
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        db.ObjectAuditEvents.RemoveRange(db.ObjectAuditEvents);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id);
        var result = await controller.AcceptAdvance(
            payment.Id,
            new PaymentAdvanceReviewRequest { Reason = "Проверено бухгалтерией" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var acceptedPayment = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(PaymentAdvanceReviewStatus.AcceptedAsAdvance, acceptedPayment.AdvanceReviewStatus);

        var transaction = await db.ClientBalanceTransactions.AsNoTracking().SingleAsync();
        Assert.Equal(ClientBalanceTransactionType.UnallocatedAdvance, transaction.Type);
        Assert.Equal(ClientBalanceDirection.Credit, transaction.Direction);
        Assert.Equal(7_100m, transaction.Amount);
        Assert.Equal(client.Id, transaction.ClientId);
        Assert.Equal(payment.Id, transaction.PaymentId);

        var auditEvent = await db.ObjectAuditEvents
            .AsNoTracking()
            .SingleAsync(item => item.ObjectType == "payment" && item.ObjectId == payment.Id && item.Action == "Updated");
        Assert.NotNull(auditEvent.MetadataJson);
        using (var metadata = JsonDocument.Parse(auditEvent.MetadataJson!))
        {
            var reason = metadata.RootElement.GetProperty("reason").GetString();
            Assert.Contains("Контроль аванса: платеж принят как аванс", reason, StringComparison.Ordinal);
        }

        var v2 = CreatePaymentsV2Controller(db, user.Id);
        var page = await v2.Get(
            from: null,
            to: null,
            clientId: null,
            caseId: null,
            advanceReviewStatus: PaymentAdvanceReviewStatus.AcceptedAsAdvance,
            ct: CancellationToken.None);
        var okPage = Assert.IsType<OkObjectResult>(page.Result);
        var response = Assert.IsType<PaymentsPageResponse>(okPage.Value);
        Assert.Single(response.Items);
        Assert.Equal(payment.Id, response.Items[0].Id);
    }

    [Fact]
    public async Task AdvanceReviewAcceptanceFlow_CreatesAcceptsAndAppliesUnallocatedAdvance()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateBalanceUserAsync(db, canEditPayments: true, DataVisibilityScope.All);
        var client = new Client { Name = "Клиент приемки авансов" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var paymentsController = CreatePaymentsController(db, user.Id);
        var createResult = await paymentsController.Create(new Payment
        {
            ClientId = client.Id,
            Amount = 3_000m,
            PaidAmount = 3_000m,
            Date = new DateTime(2026, 6, 10),
            PlannedDate = new DateTime(2026, 6, 10),
            PaidDate = new DateTime(2026, 6, 10),
            LastPaymentDate = new DateTime(2026, 6, 10),
            Description = "Поступление без счета для приемки",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Completed,
            Type = PaymentType.Income,
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(createResult);
        var sourceAdvance = Assert.IsType<Payment>(created.Value);
        Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, sourceAdvance.AdvanceReviewStatus);
        Assert.Empty(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());

        var v2 = CreatePaymentsV2Controller(db, user.Id);
        var reviewPage = await v2.Get(
            from: null,
            to: null,
            clientId: null,
            caseId: null,
            advanceReviewStatus: PaymentAdvanceReviewStatus.RequiresDecision,
            ct: CancellationToken.None);
        var reviewOk = Assert.IsType<OkObjectResult>(reviewPage.Result);
        var reviewResponse = Assert.IsType<PaymentsPageResponse>(reviewOk.Value);
        Assert.Contains(reviewResponse.Items, item => item.Id == sourceAdvance.Id);

        var acceptResult = await paymentsController.AcceptAdvance(
            sourceAdvance.Id,
            new PaymentAdvanceReviewRequest { Reason = "Приемочный сценарий: деньги приняты как аванс" },
            CancellationToken.None);

        var acceptedOk = Assert.IsType<OkObjectResult>(acceptResult);
        var acceptedAdvance = Assert.IsType<Payment>(acceptedOk.Value);
        Assert.Equal(PaymentAdvanceReviewStatus.AcceptedAsAdvance, acceptedAdvance.AdvanceReviewStatus);
        Assert.Equal(3_000m, await ClientBalanceService.GetBalanceAsync(db, client.Id));

        var targetDebt = CreatePayment(new DateTime(2026, 6, 11), 3_000m, "Долг для зачета аванса");
        targetDebt.ClientId = client.Id;
        targetDebt.PaidAmount = 0m;
        targetDebt.Status = PaymentStatus.Pending;
        db.Payments.Add(targetDebt);
        await db.SaveChangesAsync();

        var balanceController = CreateClientBalancesController(db, user.Id);
        await AuthenticatePaymentWriterAsync(db, balanceController);
        var applyResult = await balanceController.ApplyCredit(
            client.Id,
            new ApplyClientBalanceRequest
            {
                PaymentId = targetDebt.Id,
                Amount = 3_000m,
                SourcePaymentId = sourceAdvance.Id,
                Description = "Приемочный зачет нераспределенного аванса",
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(applyResult);
        db.ChangeTracker.Clear();

        var updatedSource = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == sourceAdvance.Id);
        var updatedTarget = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == targetDebt.Id);
        Assert.Equal(PaymentAdvanceReviewStatus.Allocated, updatedSource.AdvanceReviewStatus);
        Assert.Equal(PaymentStatus.Completed, updatedTarget.Status);
        Assert.Equal(3_000m, updatedTarget.PaidAmount);
        Assert.Equal(0m, await ClientBalanceService.GetBalanceAsync(db, client.Id));
        Assert.Equal(
            0m,
            await ClientBalanceService.GetUnallocatedAdvanceAvailableAmountAsync(
                db,
                sourceAdvance.Id,
                client.Id,
                null));

        var balanceTransactions = await db.ClientBalanceTransactions
            .AsNoTracking()
            .OrderBy(item => item.Direction)
            .ThenBy(item => item.Id)
            .ToListAsync();
        Assert.Contains(balanceTransactions, item =>
            item.Type == ClientBalanceTransactionType.UnallocatedAdvance &&
            item.Direction == ClientBalanceDirection.Credit &&
            item.Amount == 3_000m &&
            item.PaymentId == sourceAdvance.Id);
        Assert.Contains(balanceTransactions, item =>
            item.Type == ClientBalanceTransactionType.CreditApplied &&
            item.Direction == ClientBalanceDirection.Debit &&
            item.Amount == 3_000m &&
            item.PaymentId == targetDebt.Id &&
            item.ExternalKey != null &&
            item.ExternalKey.StartsWith(ClientBalanceService.BuildUnallocatedAdvanceApplicationKeyPrefix(sourceAdvance.Id), StringComparison.Ordinal));
    }

    [Fact]
    public async Task AcceptAdvance_WithoutEditPermission_ReturnsForbidAndDoesNotCreateBalance()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, canEdit: false);
        var client = new Client { Name = "Advance no edit client" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 3, 2), 7_100m, "Advance without edit permission");
        payment.ClientId = client.Id;
        payment.PaidAmount = 7_100m;
        payment.Status = PaymentStatus.Completed;
        payment.IsPaid = true;
        payment.PaidDate = payment.Date;
        payment.LastPaymentDate = payment.Date;
        payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision;
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id);
        var result = await controller.AcceptAdvance(
            payment.Id,
            new PaymentAdvanceReviewRequest { Reason = "No edit permission" },
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        db.ChangeTracker.Clear();
        var status = await db.Payments
            .AsNoTracking()
            .Where(item => item.Id == payment.Id)
            .Select(item => item.AdvanceReviewStatus)
            .SingleAsync();
        Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, status);
        Assert.Empty(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RefundAdvance_AddsBalanceDebitAndMoneyOperation()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false);
        var client = new Client { Name = "Клиент для возврата аванса" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 3, 2), 7_100m, "Аванс к возврату");
        payment.ClientId = client.Id;
        payment.PaidAmount = 7_100m;
        payment.Status = PaymentStatus.Completed;
        payment.IsPaid = true;
        payment.PaidDate = payment.Date;
        payment.LastPaymentDate = payment.Date;
        payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance;
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = client.Id,
            PaymentId = payment.Id,
            Amount = 7_100m,
            Type = ClientBalanceTransactionType.UnallocatedAdvance,
            Direction = ClientBalanceDirection.Credit,
            Description = "Нераспределенный аванс",
            ExternalKey = ClientBalanceService.BuildUnallocatedAdvanceKey(payment.Id),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ObjectAuditEvents.RemoveRange(db.ObjectAuditEvents);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id);
        var result = await controller.RefundAdvance(
            payment.Id,
            new PaymentAdvanceReviewRequest { Reason = "Клиент попросил вернуть деньги" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var refundedPayment = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(PaymentAdvanceReviewStatus.Refunded, refundedPayment.AdvanceReviewStatus);

        var transactions = await db.ClientBalanceTransactions
            .AsNoTracking()
            .OrderBy(item => item.Direction)
            .ThenBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, transactions.Count);
        Assert.Contains(transactions, item =>
            item.Type == ClientBalanceTransactionType.UnallocatedAdvance
            && item.Direction == ClientBalanceDirection.Credit
            && item.Amount == 7_100m);
        Assert.Contains(transactions, item =>
            item.Type == ClientBalanceTransactionType.Refund
            && item.Direction == ClientBalanceDirection.Debit
            && item.Amount == 7_100m
            && item.ExternalKey == ClientBalanceService.BuildUnallocatedAdvanceRefundKey(payment.Id));

        var available = await ClientBalanceService.GetUnallocatedAdvanceAvailableAmountAsync(db, payment.Id, client.Id, null);
        Assert.Equal(0m, available);
        var balance = await ClientBalanceService.GetBalanceAsync(db, client.Id);
        Assert.Equal(0m, balance);

        var operation = await db.MoneyOperations.AsNoTracking().SingleAsync();
        Assert.Equal(MoneyOperationDirection.Expense, operation.Direction);
        Assert.Equal(MoneyOperationAccountingType.Fact, operation.AccountingType);
        Assert.Equal(7_100m, operation.Amount);
        Assert.Equal("Возврат клиенту", operation.CategoryName);
        Assert.Contains("Возврат нераспределенного аванса", operation.Purpose, StringComparison.Ordinal);

        var auditEvent = await db.ObjectAuditEvents
            .AsNoTracking()
            .Where(item => item.ObjectType == "payment" && item.ObjectId == payment.Id && item.Action == "Updated")
            .OrderByDescending(item => item.CreatedAt)
            .FirstAsync();
        Assert.NotNull(auditEvent.MetadataJson);
        using var metadata = JsonDocument.Parse(auditEvent.MetadataJson!);
        var reason = metadata.RootElement.GetProperty("reason").GetString();
        Assert.Contains("Контроль аванса: аванс", reason, StringComparison.Ordinal);
        Assert.Contains("возвращен клиенту", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptAdvance_WithOwnScope_ForHiddenPaymentReturnsNotFoundAndDoesNotCreateBalance()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.Own);
        var otherUser = await CreatePaymentsUserAsync(db, canExport: false, viewScope: DataVisibilityScope.Own);
        var hiddenClient = new Client { Name = "Hidden accepted advance client", ResponsibleUserId = otherUser.Id };
        db.Clients.Add(hiddenClient);
        await db.SaveChangesAsync();

        var hiddenPayment = CreatePayment(new DateTime(2026, 3, 2), 7_100m, "Hidden accepted advance");
        hiddenPayment.ClientId = hiddenClient.Id;
        hiddenPayment.PaidAmount = 7_100m;
        hiddenPayment.Status = PaymentStatus.Completed;
        hiddenPayment.IsPaid = true;
        hiddenPayment.PaidDate = hiddenPayment.Date;
        hiddenPayment.LastPaymentDate = hiddenPayment.Date;
        hiddenPayment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision;
        db.Payments.Add(hiddenPayment);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id);
        var result = await controller.AcceptAdvance(
            hiddenPayment.Id,
            new PaymentAdvanceReviewRequest { Reason = "Hidden payment" },
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        db.ChangeTracker.Clear();
        var status = await db.Payments
            .AsNoTracking()
            .Where(payment => payment.Id == hiddenPayment.Id)
            .Select(payment => payment.AdvanceReviewStatus)
            .SingleAsync();
        Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, status);
        Assert.Empty(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task IgnoreAdvance_WritesReadableAuditReason()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false);
        var client = new Client { Name = "Клиент с ошибочным авансом" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 3, 3), 3_500m, "Платеж для исключения");
        payment.ClientId = client.Id;
        payment.PaidAmount = 3_500m;
        payment.Status = PaymentStatus.Completed;
        payment.IsPaid = true;
        payment.PaidDate = payment.Date;
        payment.LastPaymentDate = payment.Date;
        payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision;
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        db.ObjectAuditEvents.RemoveRange(db.ObjectAuditEvents);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id);
        var result = await controller.IgnoreAdvance(
            payment.Id,
            new PaymentAdvanceReviewRequest { Reason = "Поступление уже разобрано вручную" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var ignoredPayment = Assert.IsType<Payment>(ok.Value);
        Assert.Equal(PaymentAdvanceReviewStatus.Ignored, ignoredPayment.AdvanceReviewStatus);
        Assert.Empty(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());

        var auditEvent = await db.ObjectAuditEvents
            .AsNoTracking()
            .SingleAsync(item => item.ObjectType == "payment" && item.ObjectId == payment.Id && item.Action == "Updated");
        Assert.NotNull(auditEvent.MetadataJson);
        using var metadata = JsonDocument.Parse(auditEvent.MetadataJson!);
        var reason = metadata.RootElement.GetProperty("reason").GetString();
        Assert.Contains("Контроль аванса: платеж исключен из контроля", reason, StringComparison.Ordinal);
        Assert.Contains("Поступление уже разобрано вручную", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IgnoreAdvance_WithoutEditPermission_ReturnsForbidAndDoesNotChangePayment()
    {
        await using var db = await CreateContextAsync();
        var user = await CreatePaymentsUserAsync(db, canExport: false, canEdit: false);
        var client = new Client { Name = "Ignore no edit client" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 3, 3), 3_500m, "Ignore without edit permission");
        payment.ClientId = client.Id;
        payment.PaidAmount = 3_500m;
        payment.Status = PaymentStatus.Completed;
        payment.IsPaid = true;
        payment.PaidDate = payment.Date;
        payment.LastPaymentDate = payment.Date;
        payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision;
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, user.Id);
        var result = await controller.IgnoreAdvance(
            payment.Id,
            new PaymentAdvanceReviewRequest { Reason = "No edit permission" },
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        db.ChangeTracker.Clear();
        var status = await db.Payments
            .AsNoTracking()
            .Where(item => item.Id == payment.Id)
            .Select(item => item.AdvanceReviewStatus)
            .SingleAsync();
        Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, status);
        Assert.Empty(await db.ClientBalanceTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Update_NotifiesPaymentResponsibleAboutImportantChanges()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreatePaymentsUserAsync(db, canExport: true, canEdit: true, DataVisibilityScope.All);
        var responsible = await CreatePaymentsUserAsync(db, canExport: false, canEdit: false, DataVisibilityScope.Own);
        var sourceBefore = new PaymentSource { Name = "Банк", IsActive = true };
        var sourceAfter = new PaymentSource { Name = "Касса", IsActive = true };
        db.PaymentSources.AddRange(sourceBefore, sourceAfter);
        await db.SaveChangesAsync();

        var payment = new Payment
        {
            Description = "Контрольный платеж",
            Date = new DateTime(2026, 6, 17),
            PlannedDate = new DateTime(2026, 6, 17),
            Amount = 10000m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            DocumentKind = DocumentKind.Payment,
            PaymentSourceId = sourceBefore.Id,
            Responsibles = [new PaymentResponsible { UserId = responsible.Id }]
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var controller = CreatePaymentsController(db, actor.Id, CreateNotificationService(db));
        var update = new Payment
        {
            Date = new DateTime(2026, 6, 19),
            PlannedDate = new DateTime(2026, 6, 19),
            Amount = 12000m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Description = "Контрольный платеж",
            DocumentKind = DocumentKind.Payment,
            PaymentSourceId = sourceAfter.Id,
            PaidAmount = 12000m,
            PaidDate = new DateTime(2026, 6, 19),
            LastPaymentDate = new DateTime(2026, 6, 19)
        };

        await AuthenticatePaymentWriterAsync(db, controller);
        var result = await controller.Update(payment.Id, update, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(responsible.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.PaymentChanged, notification.Type);
        Assert.Equal("payment", notification.ObjectType);
        Assert.Equal(payment.Id, notification.ObjectId);
        Assert.Contains("Статус", notification.BodyPreview);
        Assert.Contains("Дата", notification.BodyPreview);
        Assert.Contains("Сумма", notification.BodyPreview);
        Assert.Contains("Источник", notification.BodyPreview);
    }

    private static async Task AuthenticatePaymentWriterAsync(PaymentContext db, ControllerBase controller)
    {
        if (controller.User.GetUserId().HasValue) return;
        // These legacy direct-action fixtures had no HTTP authentication context.
        // Use a real approved database actor; retain the existing business assertions.
        Assert.False(db.ChangeTracker.HasChanges());
        var actor = new User
        {
            Email = $"ordinary-payment-{Guid.NewGuid():N}@test.local",
            FullName = "Synthetic ordinary payment editor",
            IsActive = true,
            IsApproved = true,
            IsEmployee = true,
            Role = new Role { Name = "admin", Description = "Synthetic payment editor" },
        };
        db.Users.Add(actor);
        await db.SaveChangesAsync();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString())], "synthetic")) }
        };
    }

    private static Task<PaymentContext> CreateContextAsync(bool useObjectAuditModel = true)
        => SqliteTestContextFactory.CreateAsync(useObjectAuditModel);

    private static async Task ClearSystemNotesAsync(PaymentContext db, params int[] paymentIds)
    {
        var payments = await db.Payments
            .Where(payment => paymentIds.Contains(payment.Id))
            .ToListAsync();
        foreach (var payment in payments)
        {
            payment.SystemNotes = string.Empty;
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
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

    private static PaymentsV1Controller CreatePaymentsV1Controller(PaymentContext db, int userId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "Test user"),
        };

        return new PaymentsV1Controller(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
                },
            },
        };
    }

    private static PaymentsV2Controller CreatePaymentsV2Controller(PaymentContext db, int userId, bool includeAdminRoleClaim = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "Test user"),
        };
        if (includeAdminRoleClaim)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        return new PaymentsV2Controller(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
                },
            },
        };
    }

    private static async Task<PaymentsController> CreateAdminRepairControllerAsync(PaymentContext db)
    {
        var role = await db.Roles.FirstOrDefaultAsync(item => item.Name == "admin") ?? new Role { Name = "admin", Description = "Admin" };
        var user = new User
        {
            Email = $"repair-{Guid.NewGuid():N}@fixture.local",
            FullName = "Repair admin",
            PasswordHash = "unused",
            Role = role,
            IsActive = true,
            IsApproved = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return CreatePaymentsController(db, user.Id);
    }

    private static NotificationService CreateNotificationService(PaymentContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        return new NotificationService(
            db,
            configuration,
            new NoopEmailService(),
            new NoopTelegramNotificationService(),
            NullLogger<NotificationService>.Instance);
    }

    private static PaymentsController CreatePaymentsController(
        PaymentContext db,
        int userId,
        INotificationService? notificationService = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "Test user"),
        };

        return new PaymentsController(db, new NoopFormRequirementsService(), notificationService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
                },
            },
        };
    }

    private static ClientBalancesController CreateClientBalancesController(PaymentContext db, int userId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "Test user"),
        };

        return new ClientBalancesController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
                },
            },
        };
    }

    private static LegalEntityBalancesController CreateLegalEntityBalancesController(PaymentContext db, int userId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "Test user"),
        };

        return new LegalEntityBalancesController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
                },
            },
        };
    }

    private static async Task<User> CreatePaymentsUserAsync(
        PaymentContext db,
        bool canExport,
        bool canEdit = true,
        string viewScope = DataVisibilityScope.All,
        bool canView = true)
    {
        var role = new Role
        {
            Name = $"payments-export-{Guid.NewGuid():N}",
            Description = "Payments export test role",
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Payments,
            CanView = canView,
            CanCreate = true,
            CanEdit = canEdit,
            CanDelete = true,
            CanExport = canExport,
            ViewScope = viewScope,
        });

        var user = new User
        {
            Email = $"payments-export-{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            FullName = "Payments Export User",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            IsEmployee = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task GrantSectionPermissionAsync(
        PaymentContext db,
        User user,
        string section,
        string viewScope = DataVisibilityScope.All)
    {
        var permission = await db.RolePermissions
            .SingleOrDefaultAsync(item => item.RoleId == user.RoleId && item.Section == section);
        if (permission is null)
        {
            db.RolePermissions.Add(new RolePermission
            {
                RoleId = user.RoleId,
                Section = section,
                CanView = true,
                CanCreate = true,
                CanEdit = true,
                CanDelete = true,
                CanExport = true,
                ViewScope = viewScope,
            });
        }
        else
        {
            permission.CanView = true;
            permission.CanCreate = true;
            permission.CanEdit = true;
            permission.CanDelete = true;
            permission.CanExport = true;
            permission.ViewScope = viewScope;
        }

        await db.SaveChangesAsync();
    }

    private sealed class NoopEmailService : IEmailService
    {
        public Task SendEmailAsync(
            string toEmail,
            string subject,
            string body,
            bool isHtml = false,
            CancellationToken cancellationToken = default,
            EmailSendSettings? settings = null)
            => Task.CompletedTask;

        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName)
            => Task.CompletedTask;

        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName)
            => Task.CompletedTask;

        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason)
            => Task.CompletedTask;

        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail)
            => Task.CompletedTask;

        public Task SendEmailWithAttachmentAsync(
            string toEmail,
            string subject,
            string body,
            string attachmentFileName,
            byte[] attachmentBytes,
            string attachmentContentType)
            => Task.CompletedTask;
    }

    private sealed class NoopTelegramNotificationService : ITelegramNotificationService
    {
        public Task<TelegramSendResult> SendMessageAsync(long chatId, string text, CancellationToken cancellationToken = default)
            => Task.FromResult(new TelegramSendResult(true, "sent"));

        public Task<TelegramSendResult> SendMessageWithTokenAsync(
            long chatId,
            string text,
            string botToken,
            CancellationToken cancellationToken = default,
            TelegramBotConnectionSettings? connection = null)
            => Task.FromResult(new TelegramSendResult(true, "sent"));

        public Task<TelegramNotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TelegramNotificationSettingsDto());
    }

    private static async Task<User> CreateBalanceUserAsync(
        PaymentContext db,
        bool canEditPayments,
        string viewScope,
        bool canViewPayments = true)
    {
        var role = new Role
        {
            Name = $"balance-{Guid.NewGuid():N}",
            Description = "Balance permission test role",
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Clients,
                CanView = true,
                ViewScope = viewScope,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.LegalEntities,
                CanView = true,
                ViewScope = viewScope,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Payments,
                CanView = canViewPayments,
                CanCreate = true,
                CanEdit = canEditPayments,
                CanDelete = true,
                ViewScope = viewScope,
            });

        var user = new User
        {
            Email = $"balance-{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            FullName = "Balance Permission User",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            IsEmployee = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
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
