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

public class InvoicesControllerCoverageTests
{
    private const string InvoiceMaxClientCasesSettingKey = "invoices.rules.maxClientCasesPerInvoice";
    private const string InvoiceMaxActsSettingKey = "invoices.rules.maxActsPerInvoice";

    [Fact]
    public async Task GetSummary_GroupsBuckets_AndCalculatesOverdueAging()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Summary Client");
        var today = DateTime.UtcNow.Date;

        await CreateInvoicePaymentAsync(db, "SUM-001", clientId, today.AddDays(5), 100m, PaymentStatus.Pending, PaymentType.Income);
        await CreateInvoicePaymentAsync(db, "SUM-002", clientId, today.AddDays(-1), 200m, PaymentStatus.Completed, PaymentType.Income);
        await CreateInvoicePaymentAsync(db, "SUM-003", clientId, today.AddDays(-5), 300m, PaymentStatus.Overdue, PaymentType.Income);
        await CreateInvoicePaymentAsync(db, "SUM-004", clientId, today.AddDays(-20), 400m, PaymentStatus.Overdue, PaymentType.Income);
        await CreateInvoicePaymentAsync(db, "SUM-005", clientId, today.AddDays(-45), 500m, PaymentStatus.Overdue, PaymentType.Income);
        await CreateInvoicePaymentAsync(db, "SUM-006", clientId, today.AddDays(-75), 600m, PaymentStatus.Overdue, PaymentType.Income);
        await CreateInvoicePaymentAsync(db, "SUM-007", clientId, today.AddDays(-120), 700m, PaymentStatus.Overdue, PaymentType.Income);
        await CreateInvoicePaymentAsync(db, "SUM-008", clientId, today.AddDays(-3), 800m, PaymentStatus.Overdue, PaymentType.Expense);

        var controller = CreateController(db, isAdmin: true, userId: null);

        var action = await controller.GetSummary(
            from: null,
            to: null,
            status: null,
            clientId: null,
            caseId: null,
            responsibleId: null,
            search: null,
            type: PaymentType.Income,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var summary = Assert.IsType<InvoiceSummaryDto>(ok.Value);

        Assert.Equal(7, summary.Total.Count);
        Assert.Equal(2_800m, summary.Total.Amount);
        Assert.Equal(1, summary.Pending.Count);
        Assert.Equal(100m, summary.Pending.Amount);
        Assert.Equal(1, summary.Paid.Count);
        Assert.Equal(200m, summary.Paid.Amount);
        Assert.Equal(5, summary.Overdue.Count);
        Assert.Equal(2_500m, summary.Overdue.Amount);
        Assert.Equal(300m, summary.OverdueAging.Day0To10);
        Assert.Equal(400m, summary.OverdueAging.Day11To30);
        Assert.Equal(500m, summary.OverdueAging.Day31To60);
        Assert.Equal(600m, summary.OverdueAging.Day61To90);
        Assert.Equal(700m, summary.OverdueAging.Day91Plus);
        Assert.Equal(2_500m, summary.OverdueAging.Total);
    }

    [Fact]
    public async Task GetById_ReturnsEnrichedInvoiceGraph()
    {
        await using var db = await CreateContextAsync();
        await ConfigureInvoiceRulesAsync(db, maxClientCases: 2, maxActs: 2);

        var actorUserId = await CreateUserAsync(db, "Coverage Admin", roleName: "admin");
        var clientId = await CreateClientAsync(db, "Coverage Client");
        var contractId = await CreateContractAsync(db, clientId, "COV-CTR-1");
        var clientCaseId = await CreateClientCaseAsync(db, clientId, "Coverage Case");
        var actId = await CreateActAsync(db, clientId, "COV-ACT-1");
        var responsibleId = await CreateUserAsync(db, "Coverage Responsible");
        var serviceId = await CreateServiceAsync(db, "Coverage Service");

        var controller = CreateController(db, isAdmin: true, userId: actorUserId);
        var createAction = await controller.Create(new UpsertInvoiceRequest
        {
            Number = "COV-INV-1",
            Date = new DateTime(2026, 4, 18),
            DueDate = new DateTime(2026, 4, 25),
            Amount = 9_000m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            ClientId = clientId,
            ContractId = contractId,
            ClientCaseId = clientCaseId,
            ClientCaseIds = [clientCaseId],
            ActId = actId,
            ActIds = [actId],
            ResponsibleIds = [responsibleId],
            ServiceId = serviceId,
            Description = "Coverage invoice",
            LineItems =
            [
                new InvoiceLineItemRequest
                {
                    ServiceId = serviceId,
                    Name = "Coverage line item",
                    Quantity = 1m,
                    Unit = "service",
                    Price = 9_000m,
                },
            ],
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        var getAction = await controller.GetById(createdDto.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(getAction.Result);
        var dto = Assert.IsType<InvoiceDto>(ok.Value);

        Assert.Equal("COV-INV-1", dto.Number);
        Assert.Equal(clientId, dto.ClientId);
        Assert.Equal("Coverage Client", dto.ClientName);
        Assert.Equal(contractId, dto.ContractId);
        Assert.Equal(clientCaseId, dto.ClientCaseId);
        Assert.Equal("Coverage Case", dto.ClientCaseTitle);
        Assert.Equal([clientCaseId], dto.ClientCaseIds);
        Assert.Single(dto.ClientCases);
        Assert.Equal(clientCaseId, dto.ClientCases[0].Id);
        Assert.Equal(actId, dto.ActId);
        Assert.Equal("COV-ACT-1", dto.ActNumber);
        Assert.Equal([actId], dto.ActIds);
        Assert.Single(dto.Acts);
        Assert.Equal(actId, dto.Acts[0].Id);
        Assert.Single(dto.Responsibles);
        Assert.Equal(responsibleId, dto.Responsibles[0].Id);
        Assert.Single(dto.LineItems);
        Assert.Equal(serviceId, dto.LineItems[0].ServiceId);
        Assert.Equal("Coverage line item", dto.LineItems[0].Name);
        Assert.Equal(serviceId, dto.ServiceId);
        Assert.Equal("Coverage Service", dto.ServiceName);
    }

    [Fact]
    public async Task NumberingSettings_RoundTrip_AndNextNumber_UsesFirstFreeStrategy()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, "Numbering User");
        var clientId = await CreateClientAsync(db, "Numbering Client");

        await CreateInvoicePaymentAsync(db, "5", clientId, new DateTime(2026, 4, 1), 1_000m, PaymentStatus.Pending, PaymentType.Income);
        await CreateInvoicePaymentAsync(db, "7", clientId, new DateTime(2026, 4, 2), 1_000m, PaymentStatus.Pending, PaymentType.Income);
        db.Invoices.Add(new Invoice
        {
            Number = "6",
            Date = new DateTime(2026, 4, 3),
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: false, userId: userId);

        var initialGetAction = await controller.GetNumberingSettings(CancellationToken.None);
        var initialOk = Assert.IsType<OkObjectResult>(initialGetAction.Result);
        Assert.Equal(1, GetAnonymousProperty<int>(initialOk.Value, "startNumber"));
        Assert.Equal("max-plus-one", GetAnonymousProperty<string>(initialOk.Value, "allocationStrategy"));

        var saveAction = await controller.UpsertNumberingSettings(new InvoiceNumberingSettingsRequest
        {
            StartNumber = 5,
            AllocationStrategy = "first-free",
        }, CancellationToken.None);

        var saveOk = Assert.IsType<OkObjectResult>(saveAction.Result);
        Assert.Equal(5, GetAnonymousProperty<int>(saveOk.Value, "startNumber"));
        Assert.Equal("first-free", GetAnonymousProperty<string>(saveOk.Value, "allocationStrategy"));

        var getAction = await controller.GetNumberingSettings(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(getAction.Result);
        Assert.Equal(5, GetAnonymousProperty<int>(ok.Value, "startNumber"));
        Assert.Equal("first-free", GetAnonymousProperty<string>(ok.Value, "allocationStrategy"));

        var nextAction = await controller.GetNextNumber(new DateTime(2026, 4, 10), CancellationToken.None);
        var nextOk = Assert.IsType<OkObjectResult>(nextAction.Result);
        Assert.Equal("6", GetAnonymousProperty<string>(nextOk.Value, "nextNumber"));
        Assert.Equal(5, GetAnonymousProperty<int>(nextOk.Value, "startNumber"));
        Assert.Equal("first-free", GetAnonymousProperty<string>(nextOk.Value, "allocationStrategy"));
    }

    [Fact]
    public async Task GetNumberingSettings_FallsBackForInvalidValues_AndUpdatesExistingUserSettings()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, "Numbering Fallback User");
        db.UserSettings.AddRange(
            new UserSetting
            {
                UserId = userId,
                Key = "invoices.numbering.startNumber",
                Value = "0",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            new UserSetting
            {
                UserId = userId,
                Key = "invoices.numbering.strategy",
                Value = "unsupported",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: false, userId: userId);

        var getAction = await controller.GetNumberingSettings(CancellationToken.None);

        var initialOk = Assert.IsType<OkObjectResult>(getAction.Result);
        Assert.Equal(1, GetAnonymousProperty<int>(initialOk.Value, "startNumber"));
        Assert.Equal("max-plus-one", GetAnonymousProperty<string>(initialOk.Value, "allocationStrategy"));

        var firstSave = await controller.UpsertNumberingSettings(new InvoiceNumberingSettingsRequest
        {
            StartNumber = 9,
            AllocationStrategy = "first-free",
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(firstSave.Result);

        var secondSave = await controller.UpsertNumberingSettings(new InvoiceNumberingSettingsRequest
        {
            StartNumber = 12,
            AllocationStrategy = "max-plus-one",
        }, CancellationToken.None);

        var savedOk = Assert.IsType<OkObjectResult>(secondSave.Result);
        Assert.Equal(12, GetAnonymousProperty<int>(savedOk.Value, "startNumber"));
        Assert.Equal("max-plus-one", GetAnonymousProperty<string>(savedOk.Value, "allocationStrategy"));

        var storedSettings = await db.UserSettings.AsNoTracking()
            .Where(item => item.UserId == userId)
            .OrderBy(item => item.Key)
            .ToListAsync();
        Assert.Equal(2, storedSettings.Count);
        Assert.Equal("12", storedSettings.Single(item => item.Key == "invoices.numbering.startNumber").Value);
        Assert.Equal("max-plus-one", storedSettings.Single(item => item.Key == "invoices.numbering.strategy").Value);
    }

    [Fact]
    public async Task UpsertRulesSettings_RequiresAdmin_AndPersistsValues()
    {
        await using var db = await CreateContextAsync();
        var nonAdminUserId = await CreateUserAsync(db, "Non Admin");
        var adminUserId = await CreateUserAsync(db, "Real Admin", roleName: "admin");

        var nonAdminController = CreateController(db, isAdmin: false, userId: nonAdminUserId);
        var forbidden = await nonAdminController.UpsertRulesSettings(new InvoiceRulesSettingsRequest
        {
            MaxClientCasesPerInvoice = 2,
            MaxActsPerInvoice = 3,
        }, CancellationToken.None);
        Assert.IsType<ForbidResult>(forbidden.Result);

        var adminController = CreateController(db, isAdmin: true, userId: adminUserId);
        var updateAction = await adminController.UpsertRulesSettings(new InvoiceRulesSettingsRequest
        {
            MaxClientCasesPerInvoice = 2,
            MaxActsPerInvoice = 3,
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(updateAction.Result);
        var dto = Assert.IsType<InvoiceRulesSettingsDto>(ok.Value);
        Assert.Equal(2, dto.MaxClientCasesPerInvoice);
        Assert.Equal(3, dto.MaxActsPerInvoice);

        var getAction = await adminController.GetRulesSettings(CancellationToken.None);
        var getOk = Assert.IsType<OkObjectResult>(getAction.Result);
        var stored = Assert.IsType<InvoiceRulesSettingsDto>(getOk.Value);
        Assert.Equal(2, stored.MaxClientCasesPerInvoice);
        Assert.Equal(3, stored.MaxActsPerInvoice);
    }

    [Fact]
    public async Task CleanupCounterpartyNames_ClearsInvoiceValues_AndRemovesRetiredRequirement()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Cleanup Client");

        var invoicePaymentId = await CreateInvoicePaymentAsync(
            db,
            "CLN-INV-1",
            clientId,
            new DateTime(2026, 4, 12),
            2_500m,
            PaymentStatus.Pending,
            PaymentType.Income,
            counterpartyName: "Legacy invoice counterparty");

        var standalonePayment = new Payment
        {
            Amount = 1_500m,
            CounterpartyName = "Standalone counterparty",
            Date = new DateTime(2026, 4, 12),
            PlannedDate = new DateTime(2026, 4, 12),
            Description = "Standalone payment",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            ClientId = clientId,
        };
        db.Payments.Add(standalonePayment);

        db.FormFieldRequirements.AddRange(
            new FormFieldRequirement
            {
                Section = "contract-invoices",
                FieldKey = "CounterpartyName",
                IsRequired = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            new FormFieldRequirement
            {
                Section = "contract-invoices",
                FieldKey = "Description",
                IsRequired = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true, userId: null);

        var action = await controller.CleanupCounterpartyNames(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        Assert.Equal(1, GetAnonymousProperty<int>(ok.Value, "cleared"));
        Assert.Equal(1, GetAnonymousProperty<int>(ok.Value, "removedRequirements"));

        db.ChangeTracker.Clear();

        var invoicePayment = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == invoicePaymentId);
        var standaloneStored = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == standalonePayment.Id);
        Assert.Null(invoicePayment.CounterpartyName);
        Assert.Equal("Standalone counterparty", standaloneStored.CounterpartyName);

        var remainingRequirementKeys = await db.FormFieldRequirements.AsNoTracking()
            .OrderBy(item => item.FieldKey)
            .Select(item => item.FieldKey)
            .ToListAsync();
        Assert.Equal(["Description"], remainingRequirementKeys);
    }

    [Fact]
    public async Task MigrateLegacyInvoiceLinks_CreatesInvoiceEntity_AndSyncsCaseLinks()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Migration Client");
        var clientCaseId = await CreateClientCaseAsync(db, clientId, "Migration Case");

        var invoiceDocument = new Payment
        {
            Account = "LEG-55",
            AccountDate = new DateTime(2026, 3, 12),
            Amount = 12_000m,
            ClientId = clientId,
            ClientCaseId = clientCaseId,
            Date = new DateTime(2026, 3, 20),
            PlannedDate = new DateTime(2026, 3, 20),
            Description = "Legacy invoice document",
            DocumentKind = DocumentKind.Invoice,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
        };

        var siblingPayment = new Payment
        {
            Account = "LEG-55",
            AccountDate = new DateTime(2026, 3, 12),
            Amount = 6_000m,
            ClientId = clientId,
            ClientCaseId = clientCaseId,
            Date = new DateTime(2026, 4, 20),
            PlannedDate = new DateTime(2026, 4, 20),
            Description = "Legacy sibling payment",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
        };

        db.Payments.AddRange(invoiceDocument, siblingPayment);
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true, userId: null);

        var action = await controller.MigrateLegacyInvoiceLinks(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var summary = Assert.IsType<InvoiceLegacyMigrationSummaryDto>(ok.Value);
        Assert.Equal(2, summary.ScannedPayments);
        Assert.Equal(1, summary.InvoicePayments);
        Assert.Equal(1, summary.CreatedInvoices);
        Assert.Equal(2, summary.LinkedPayments);
        Assert.Equal(1, summary.DistinctLegacyInvoiceKeys);
        Assert.Equal(1, summary.RepresentativePaymentsUpdated);

        db.ChangeTracker.Clear();

        var invoices = await db.Invoices.AsNoTracking().ToListAsync();
        var invoice = Assert.Single(invoices);
        Assert.Equal("LEG-55", invoice.Number);
        Assert.Equal(new DateTime(2026, 3, 12), invoice.Date);

        var migratedPayments = await db.Payments.AsNoTracking()
            .Where(item => item.Id == invoiceDocument.Id || item.Id == siblingPayment.Id)
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.All(migratedPayments, payment =>
        {
            Assert.Equal(invoice.Id, payment.InvoiceId);
            Assert.Equal("LEG-55", payment.Account);
            Assert.Equal(new DateTime(2026, 3, 12), payment.AccountDate);
            Assert.Equal("LEG-55", payment.LegacyAccountNumber);
            Assert.Equal(new DateTime(2026, 3, 12), payment.LegacyAccountDate);
            Assert.Equal(clientId, payment.ClientId);
            Assert.Equal(clientCaseId, payment.ClientCaseId);
        });

        var caseLinks = await db.InvoiceClientCases.AsNoTracking()
            .Where(item => item.PaymentId == invoiceDocument.Id)
            .Select(item => item.ClientCaseId)
            .ToListAsync();
        Assert.Equal([clientCaseId], caseLinks);
    }

    [Fact]
    public async Task ChangeResponsible_AndBulkDelete_ReturnValidationErrors_ForBadInput()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);

        var emptySelection = await controller.ChangeResponsible(new BulkAssignResponsibleRequest
        {
            Ids = [],
            ResponsibleUserId = 999,
        }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(emptySelection);

        var unknownResponsible = await controller.ChangeResponsible(new BulkAssignResponsibleRequest
        {
            Ids = [1],
            ResponsibleUserId = 999,
        }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(unknownResponsible);

        var bulkDeleteAction = await controller.BulkDelete(new BulkIdsRequest
        {
            Ids = [],
        }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(bulkDeleteAction);
    }

    [Fact]
    public async Task Get_AndGetSummary_ReturnUnauthorized_ForAnonymousUser()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: false, userId: null);

        var listAction = await controller.Get(
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
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);
        Assert.IsType<UnauthorizedResult>(listAction);

        var summaryAction = await controller.GetSummary(
            from: null,
            to: null,
            status: null,
            clientId: null,
            caseId: null,
            responsibleId: null,
            search: null,
            type: null,
            ct: CancellationToken.None);
        Assert.IsType<UnauthorizedResult>(summaryAction.Result);
    }

    [Fact]
    public async Task Get_FiltersByClientIds_SearchesByClient_AndSortsByAmountDescending()
    {
        await using var db = await CreateContextAsync();
        var alphaClientId = await CreateClientAsync(db, "Alpha Client");
        var betaClientId = await CreateClientAsync(db, "Beta Client");

        await CreateInvoicePaymentAsync(db, "ALPHA-10", alphaClientId, new DateTime(2026, 4, 10), 100m, PaymentStatus.Pending, PaymentType.Income);
        await CreateInvoicePaymentAsync(db, "BETA-30", betaClientId, new DateTime(2026, 4, 11), 300m, PaymentStatus.Pending, PaymentType.Income);
        await CreateInvoicePaymentAsync(db, "BETA-20", betaClientId, new DateTime(2026, 4, 12), 200m, PaymentStatus.Pending, PaymentType.Income);

        var controller = CreateController(db, isAdmin: true, userId: null);

        var listAction = await controller.Get(
            from: null,
            to: null,
            status: null,
            clientId: null,
            clientIds: $"{alphaClientId}, invalid, 0, {betaClientId}",
            contractId: null,
            caseId: null,
            responsibleId: null,
            search: null,
            type: PaymentType.Income,
            sortBy: "duedate",
            sortDir: "desc",
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(listAction);
        var items = GetAnonymousProperty<List<InvoiceDto>>(ok.Value, "items");
        Assert.Equal(["BETA-20", "BETA-30", "ALPHA-10"], items.Select(item => item.Number).ToArray());

        var searchAction = await controller.Get(
            from: null,
            to: null,
            status: null,
            clientId: null,
            clientIds: $"{alphaClientId},{betaClientId}",
            contractId: null,
            caseId: null,
            responsibleId: null,
            search: "beta",
            type: PaymentType.Income,
            sortBy: "number",
            sortDir: "asc",
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var searchOk = Assert.IsType<OkObjectResult>(searchAction);
        var searchedItems = GetAnonymousProperty<List<InvoiceDto>>(searchOk.Value, "items");
        Assert.Equal(["BETA-20", "BETA-30"], searchedItems.Select(item => item.Number).ToArray());
    }

    [Fact]
    public async Task Get_FiltersByStatusResponsibleAndCase_AndSupportsExtendedSorts()
    {
        await using var db = await CreateContextAsync();
        var alphaClientId = await CreateClientAsync(db, "Alpha Client");
        var betaClientId = await CreateClientAsync(db, "Beta Client");
        var alphaCaseId = await CreateClientCaseAsync(db, alphaClientId, "Alpha Case");
        var betaCaseId = await CreateClientCaseAsync(db, betaClientId, "Beta Case");
        var zuluResponsibleId = await CreateUserAsync(db, "Zulu Lead");
        var alphaResponsibleId = await CreateUserAsync(db, "Alpha Lead");

        var firstInvoiceId = await CreateInvoicePaymentAsync(db, "FLT-1", alphaClientId, new DateTime(2026, 4, 10), 100m, PaymentStatus.Pending, PaymentType.Income);
        var secondInvoiceId = await CreateInvoicePaymentAsync(db, "FLT-2", betaClientId, new DateTime(2026, 4, 11), 200m, PaymentStatus.Overdue, PaymentType.Income);
        var thirdInvoiceId = await CreateInvoicePaymentAsync(db, "FLT-3", alphaClientId, new DateTime(2026, 4, 12), 300m, PaymentStatus.Processing, PaymentType.Income);

        var firstPayment = await db.Payments.SingleAsync(item => item.Id == firstInvoiceId);
        firstPayment.ClientCaseId = alphaCaseId;
        firstPayment.CreatedAt = new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc);

        var secondPayment = await db.Payments.SingleAsync(item => item.Id == secondInvoiceId);
        secondPayment.ClientCaseId = betaCaseId;
        secondPayment.CreatedAt = new DateTime(2026, 4, 3, 8, 0, 0, DateTimeKind.Utc);

        var thirdPayment = await db.Payments.SingleAsync(item => item.Id == thirdInvoiceId);
        thirdPayment.ClientCaseId = alphaCaseId;
        thirdPayment.CreatedAt = new DateTime(2026, 4, 2, 8, 0, 0, DateTimeKind.Utc);

        db.Acts.AddRange(
            new Act
            {
                Number = "ACT-FLT-1",
                Title = "Act for FLT-1",
                Date = new DateTime(2026, 4, 9),
                Amount = 100m,
                ClientId = alphaClientId,
                InvoiceId = firstInvoiceId,
                InvoiceNumber = "FLT-1",
                ResponsibleId = zuluResponsibleId,
                Status = ActStatus.Created,
            },
            new Act
            {
                Number = "ACT-FLT-2",
                Title = "Act for FLT-2",
                Date = new DateTime(2026, 4, 10),
                Amount = 200m,
                ClientId = betaClientId,
                InvoiceId = secondInvoiceId,
                InvoiceNumber = "FLT-2",
                ResponsibleId = alphaResponsibleId,
                Status = ActStatus.Created,
            });
        await db.SaveChangesAsync();

        await AttachResponsibleAsync(db, firstInvoiceId, zuluResponsibleId);
        await AttachResponsibleAsync(db, secondInvoiceId, alphaResponsibleId);
        await AttachResponsibleAsync(db, thirdInvoiceId, alphaResponsibleId);

        var controller = CreateController(db, isAdmin: true, userId: null);

        var filteredAction = await controller.Get(
            from: null,
            to: null,
            status: PaymentStatus.Processing,
            clientId: null,
            clientIds: null,
            contractId: null,
            caseId: alphaCaseId,
            responsibleId: alphaResponsibleId,
            search: null,
            type: PaymentType.Income,
            sortBy: "createdat",
            sortDir: "desc",
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);
        var filteredOk = Assert.IsType<OkObjectResult>(filteredAction);
        var filteredItems = GetAnonymousProperty<List<InvoiceDto>>(filteredOk.Value, "items");
        Assert.Equal(["FLT-3"], filteredItems.Select(item => item.Number).ToArray());

        var clientSortAction = await controller.Get(
            from: null,
            to: null,
            status: null,
            clientId: null,
            clientIds: null,
            contractId: null,
            caseId: null,
            responsibleId: null,
            search: null,
            type: PaymentType.Income,
            sortBy: "client",
            sortDir: "asc",
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);
        var clientSortOk = Assert.IsType<OkObjectResult>(clientSortAction);
        var clientSortedItems = GetAnonymousProperty<List<InvoiceDto>>(clientSortOk.Value, "items");
        Assert.Equal(["Alpha Client", "Alpha Client", "Beta Client"], clientSortedItems.Select(item => item.ClientName ?? string.Empty).ToArray());

        var statusSortAction = await controller.Get(
            from: null,
            to: null,
            status: null,
            clientId: null,
            clientIds: null,
            contractId: null,
            caseId: null,
            responsibleId: null,
            search: null,
            type: PaymentType.Income,
            sortBy: "status",
            sortDir: "desc",
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);
        var statusSortOk = Assert.IsType<OkObjectResult>(statusSortAction);
        var statusSortedItems = GetAnonymousProperty<List<InvoiceDto>>(statusSortOk.Value, "items");
        Assert.Equal("FLT-3", statusSortedItems[0].Number);
        Assert.Equal(3, statusSortedItems.Count);
        Assert.Contains("FLT-1", statusSortedItems.Select(item => item.Number));
        Assert.Contains("FLT-2", statusSortedItems.Select(item => item.Number));
    }

    [Fact]
    public async Task GetById_ReturnsNotFound_WhenInvoiceMissing()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);

        var action = await controller.GetById(999, CancellationToken.None);

        Assert.IsType<NotFoundResult>(action.Result);
    }

    [Fact]
    public async Task NumberingEndpoints_RequireAuthenticatedUser_AndRejectInvalidStartNumber()
    {
        await using var db = await CreateContextAsync();
        var anonymousController = CreateController(db, isAdmin: false, userId: null);

        Assert.IsType<UnauthorizedResult>((await anonymousController.GetNumberingSettings(CancellationToken.None)).Result);
        Assert.IsType<UnauthorizedResult>((await anonymousController.UpsertNumberingSettings(new InvoiceNumberingSettingsRequest
        {
            StartNumber = 5,
            AllocationStrategy = "first-free",
        }, CancellationToken.None)).Result);
        Assert.IsType<UnauthorizedResult>((await anonymousController.GetNextNumber(new DateTime(2026, 4, 10), CancellationToken.None)).Result);

        var userId = await CreateUserAsync(db, "Numbering Owner");
        var controller = CreateController(db, isAdmin: false, userId: userId);
        var badRequest = await controller.UpsertNumberingSettings(new InvoiceNumberingSettingsRequest
        {
            StartNumber = 0,
            AllocationStrategy = "first-free",
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(badRequest.Result);
    }

    [Fact]
    public async Task RulesAndCleanupEndpoints_EnforceAdminAccess_AndHandleEmptyCleanup()
    {
        await using var db = await CreateContextAsync();
        var nonAdminUserId = await CreateUserAsync(db, "Regular User");
        var nonAdminController = CreateController(db, isAdmin: false, userId: nonAdminUserId);

        Assert.IsType<ForbidResult>((await nonAdminController.CleanupCounterpartyNames(CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await nonAdminController.CleanupOrphanInvoices(CancellationToken.None)).Result);

        var adminController = CreateController(db, isAdmin: true, userId: null);
        Assert.IsType<BadRequestObjectResult>((await adminController.UpsertRulesSettings(new InvoiceRulesSettingsRequest
        {
            MaxClientCasesPerInvoice = 0,
            MaxActsPerInvoice = 0,
        }, CancellationToken.None)).Result);

        var cleanup = await adminController.CleanupOrphanInvoices(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(cleanup.Result);
        Assert.Equal(0, GetAnonymousProperty<int>(ok.Value, "deleted"));
    }

    [Fact]
    public async Task BulkDelete_ReturnsUnauthorized_WhenCurrentUserIsHidden()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: false, userId: null);

        var action = await controller.BulkDelete(new BulkIdsRequest
        {
            Ids = [1],
        }, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(action);
    }

    [Fact]
    public async Task BulkDelete_UsesCurrentUserScope_AndReportsMissingIds()
    {
        await using var db = await CreateContextAsync();
        var ownerUserId = await CreateUserAsync(db, "Owner User");
        var otherUserId = await CreateUserAsync(db, "Other User");
        var clientId = await CreateClientAsync(db, "Scoped Client");
        var ownedPaymentId = await CreateInvoicePaymentAsync(db, "SCP-1", clientId, new DateTime(2026, 4, 10), 100m, PaymentStatus.Pending, PaymentType.Income);
        var foreignPaymentId = await CreateInvoicePaymentAsync(db, "SCP-2", clientId, new DateTime(2026, 4, 11), 200m, PaymentStatus.Pending, PaymentType.Income);
        await AttachResponsibleAsync(db, ownedPaymentId, ownerUserId);
        await AttachResponsibleAsync(db, foreignPaymentId, otherUserId);

        var controller = CreateController(db, isAdmin: false, userId: ownerUserId);

        var action = await controller.BulkDelete(new BulkIdsRequest
        {
            Ids = [ownedPaymentId, foreignPaymentId],
        }, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(action);
        Assert.Equal([foreignPaymentId], GetAnonymousProperty<List<int>>(notFound.Value, "missingIds"));
    }

    [Fact]
    public async Task ChangeResponsible_ReturnsUnauthorized_WhenCurrentUserIsHidden()
    {
        await using var db = await CreateContextAsync();
        var responsibleId = await CreateUserAsync(db, "Target Responsible");
        var controller = CreateController(db, isAdmin: false, userId: null);

        var action = await controller.ChangeResponsible(new BulkAssignResponsibleRequest
        {
            Ids = [1],
            ResponsibleUserId = responsibleId,
        }, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(action);
    }

    [Fact]
    public async Task ChangeResponsible_UsesCurrentUserScope_AndReportsMissingIds()
    {
        await using var db = await CreateContextAsync();
        var ownerUserId = await CreateUserAsync(db, "Owner Responsible");
        var otherUserId = await CreateUserAsync(db, "Foreign Responsible");
        var newResponsibleId = await CreateUserAsync(db, "Replacement Responsible");
        var clientId = await CreateClientAsync(db, "Scoped Responsible Client");
        var ownedPaymentId = await CreateInvoicePaymentAsync(db, "SCR-1", clientId, new DateTime(2026, 4, 10), 100m, PaymentStatus.Pending, PaymentType.Income);
        var foreignPaymentId = await CreateInvoicePaymentAsync(db, "SCR-2", clientId, new DateTime(2026, 4, 11), 200m, PaymentStatus.Pending, PaymentType.Income);
        await AttachResponsibleAsync(db, ownedPaymentId, ownerUserId);
        await AttachResponsibleAsync(db, foreignPaymentId, otherUserId);

        var controller = CreateController(db, isAdmin: false, userId: ownerUserId);

        var action = await controller.ChangeResponsible(new BulkAssignResponsibleRequest
        {
            Ids = [ownedPaymentId, foreignPaymentId],
            ResponsibleUserId = newResponsibleId,
        }, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(action);
        Assert.Equal([foreignPaymentId], GetAnonymousProperty<List<int>>(notFound.Value, "missingIds"));
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_WhenInvoiceMissing()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);

        var action = await controller.Delete(999, CancellationToken.None);

        Assert.IsType<NotFoundResult>(action);
    }

    [Fact]
    public async Task MigrateLegacyInvoiceLinks_SkipsPaymentsWithoutClientBinding()
    {
        await using var db = await CreateContextAsync();

        var orphanInvoice = new Invoice
        {
            Number = "LEG-SKIP",
            Date = new DateTime(2026, 3, 5),
        };
        db.Invoices.Add(orphanInvoice);
        await db.SaveChangesAsync();

        db.Payments.Add(new Payment
        {
            Account = "LEG-SKIP",
            AccountDate = new DateTime(2026, 3, 5),
            Amount = 2_000m,
            ClientId = null,
            Date = new DateTime(2026, 3, 20),
            PlannedDate = new DateTime(2026, 3, 20),
            Description = "Skipped legacy payment",
            DocumentKind = DocumentKind.Invoice,
            InvoiceId = orphanInvoice.Id,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true, userId: null);
        var action = await controller.MigrateLegacyInvoiceLinks(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var summary = Assert.IsType<InvoiceLegacyMigrationSummaryDto>(ok.Value);
        Assert.Equal(1, summary.ScannedPayments);
        Assert.Equal(1, summary.InvoicePayments);
        Assert.Equal(1, summary.MissingClientSkipped);
        Assert.Equal(0, summary.DistinctLegacyInvoiceKeys);
    }

    [Fact]
    public async Task MigrateLegacyInvoiceLinks_SkipsPaymentsWithoutNormalizedNumber()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Missing Number Client");

        db.Payments.Add(new Payment
        {
            Account = "   ",
            AccountDate = new DateTime(2026, 3, 6),
            Amount = 2_500m,
            ClientId = clientId,
            Date = new DateTime(2026, 3, 21),
            PlannedDate = new DateTime(2026, 3, 21),
            Description = "Missing number legacy payment",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true, userId: null);
        var action = await controller.MigrateLegacyInvoiceLinks(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var summary = Assert.IsType<InvoiceLegacyMigrationSummaryDto>(ok.Value);
        Assert.Equal(1, summary.ScannedPayments);
        Assert.Equal(1, summary.MissingNumberSkipped);
        Assert.Equal(0, summary.DistinctLegacyInvoiceKeys);
    }

    [Fact]
    public async Task MigrateLegacyInvoiceLinks_SplitsSharedInvoiceEntity_AndRelinksMismatchedPayment()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Split Client");
        var caseId1 = await CreateClientCaseAsync(db, clientId, "Split Case 1");
        var caseId2 = await CreateClientCaseAsync(db, clientId, "Split Case 2");

        var sharedInvoice = new Invoice
        {
            Number = "LEG-SHARED",
            Date = new DateTime(2026, 2, 14),
        };
        db.Invoices.Add(sharedInvoice);
        await db.SaveChangesAsync();

        var canonicalPayment = new Payment
        {
            Account = "LEG-SHARED",
            AccountDate = new DateTime(2026, 2, 14),
            Amount = 7_000m,
            ClientId = clientId,
            ClientCaseId = caseId1,
            Date = new DateTime(2026, 2, 20),
            PlannedDate = new DateTime(2026, 2, 20),
            Description = "Canonical shared payment",
            DocumentKind = DocumentKind.Invoice,
            InvoiceId = sharedInvoice.Id,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
        };
        var relinkedPayment = new Payment
        {
            Account = "LEG-SHARED",
            AccountDate = new DateTime(2026, 2, 14),
            Amount = 3_500m,
            ClientId = clientId,
            ClientCaseId = caseId2,
            Date = new DateTime(2026, 3, 20),
            PlannedDate = new DateTime(2026, 3, 20),
            Description = "Relinked shared payment",
            DocumentKind = DocumentKind.Payment,
            InvoiceId = sharedInvoice.Id,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
        };

        db.Payments.AddRange(canonicalPayment, relinkedPayment);
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true, userId: null);
        var action = await controller.MigrateLegacyInvoiceLinks(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var summary = Assert.IsType<InvoiceLegacyMigrationSummaryDto>(ok.Value);
        Assert.Equal(1, summary.CreatedInvoices);
        Assert.Equal(1, summary.LinkedToExistingInvoice);
        Assert.Equal(1, summary.SplitSharedInvoiceGroups);
        Assert.Equal(1, summary.AlreadyLinked);
        Assert.Equal(1, summary.RelinkedPayments);

        var storedInvoices = await db.Invoices.AsNoTracking()
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, storedInvoices.Count);

        var payments = await db.Payments.AsNoTracking()
            .Where(item => item.Id == canonicalPayment.Id || item.Id == relinkedPayment.Id)
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, payments.Select(item => item.InvoiceId).Distinct().Count());
    }

    [Fact]
    public async Task MigrateLegacyInvoiceLinks_ReusesExistingInvoice_AndTracksAlreadyLinkedPayments()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Reuse Client");
        var clientCaseId = await CreateClientCaseAsync(db, clientId, "Reuse Case");
        var extraCaseId = await CreateClientCaseAsync(db, clientId, "Legacy Extra Case");

        var invoice = new Invoice
        {
            Number = "LEG-EXIST",
            Date = new DateTime(2026, 3, 12),
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var invoiceDocument = new Payment
        {
            Account = "LEG-EXIST",
            AccountDate = new DateTime(2026, 3, 12),
            Amount = 10_000m,
            ClientId = clientId,
            ClientCaseId = clientCaseId,
            Date = new DateTime(2026, 3, 20),
            PlannedDate = new DateTime(2026, 3, 20),
            Description = "Existing invoice document",
            DocumentKind = DocumentKind.Invoice,
            InvoiceId = invoice.Id,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
        };
        invoiceDocument.InvoiceClientCases.Add(new InvoiceClientCase
        {
            ClientCaseId = extraCaseId,
        });

        var siblingPayment = new Payment
        {
            Account = "LEG-EXIST",
            AccountDate = new DateTime(2026, 3, 12),
            Amount = 4_000m,
            ClientId = clientId,
            ClientCaseId = clientCaseId,
            Date = new DateTime(2026, 4, 20),
            PlannedDate = new DateTime(2026, 4, 20),
            Description = "Legacy sibling payment",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
        };

        db.Payments.AddRange(invoiceDocument, siblingPayment);
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true, userId: null);
        var action = await controller.MigrateLegacyInvoiceLinks(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var summary = Assert.IsType<InvoiceLegacyMigrationSummaryDto>(ok.Value);
        Assert.Equal(0, summary.CreatedInvoices);
        Assert.Equal(1, summary.LinkedToExistingInvoice);
        Assert.Equal(1, summary.AlreadyLinked);
        Assert.Equal(1, summary.LinkedPayments);
        Assert.Equal(1, summary.RepresentativePaymentsUpdated);

        db.ChangeTracker.Clear();

        var storedSibling = await db.Payments.AsNoTracking().SingleAsync(item => item.Id == siblingPayment.Id);
        Assert.Equal(invoice.Id, storedSibling.InvoiceId);

        var caseLinks = await db.InvoiceClientCases.AsNoTracking()
            .Where(item => item.PaymentId == invoiceDocument.Id)
            .Select(item => item.ClientCaseId)
            .ToListAsync();
        Assert.Equal([clientCaseId], caseLinks);
    }

    [Fact]
    public async Task MigrateLegacyInvoiceLinks_UsesFallbackCarrier_WhenNoInvoiceDocumentExists()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Fallback Client");
        var clientCaseId = await CreateClientCaseAsync(db, clientId, "Fallback Case");

        db.Payments.Add(new Payment
        {
            Account = "LEG-FALLBACK",
            AccountDate = new DateTime(2026, 2, 10),
            Amount = 5_000m,
            ClientId = clientId,
            Date = new DateTime(2026, 2, 15),
            PlannedDate = new DateTime(2026, 2, 15),
            Description = "Fallback migration payment",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            ClientCaseId = clientCaseId,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true, userId: null);
        var action = await controller.MigrateLegacyInvoiceLinks(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var summary = Assert.IsType<InvoiceLegacyMigrationSummaryDto>(ok.Value);
        Assert.Equal(1, summary.CreatedInvoices);
        Assert.Equal(1, summary.LinkedPayments);
        Assert.Equal(1, summary.RepresentativePaymentsUpdated);

        var payment = await db.Payments.AsNoTracking().SingleAsync(item => item.Account == "LEG-FALLBACK");
        var caseLinks = await db.InvoiceClientCases.AsNoTracking()
            .Where(item => item.PaymentId == payment.Id)
            .Select(item => item.ClientCaseId)
            .ToListAsync();
        Assert.Equal([clientCaseId], caseLinks);
    }

    [Fact]
    public async Task MigrateLegacyInvoiceLinks_UsesUserNumberingSettings_ForNumericConflicts()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, "Migration Numbering User");
        var clientId = await CreateClientAsync(db, "Migration Numbering Client");
        var clientCaseId = await CreateClientCaseAsync(db, clientId, "Migration Numbering Case");

        db.UserSettings.AddRange(
            new UserSetting
            {
                UserId = userId,
                Key = "invoices.numbering.startNumber",
                Value = "10",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            new UserSetting
            {
                UserId = userId,
                Key = "invoices.numbering.strategy",
                Value = "first-free",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

        await CreateInvoicePaymentAsync(db, "10", clientId, new DateTime(2026, 4, 1), 1_000m, PaymentStatus.Pending, PaymentType.Income);
        db.Invoices.Add(new Invoice
        {
            Number = "11",
            Date = new DateTime(2026, 4, 2),
        });
        db.Payments.Add(new Payment
        {
            Account = "10",
            AccountDate = new DateTime(2026, 4, 3),
            Amount = 2_500m,
            ClientId = clientId,
            ClientCaseId = clientCaseId,
            Date = new DateTime(2026, 4, 15),
            PlannedDate = new DateTime(2026, 4, 15),
            Description = "Legacy numeric invoice",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: false, userId: userId);

        var action = await controller.MigrateLegacyInvoiceLinks(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var summary = Assert.IsType<InvoiceLegacyMigrationSummaryDto>(ok.Value);
        Assert.Equal(1, summary.CreatedInvoices);

        var migratedInvoice = await db.Invoices.AsNoTracking()
            .OrderByDescending(item => item.Id)
            .FirstAsync();
        Assert.Equal("12", migratedInvoice.Number);
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

    private static T GetAnonymousProperty<T>(object? source, string name)
    {
        Assert.NotNull(source);
        var property = source!.GetType().GetProperty(name);
        Assert.NotNull(property);
        return Assert.IsType<T>(property!.GetValue(source));
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
            Email = $"invoice-coverage-{Guid.NewGuid():N}@example.com",
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
            Amount = 50_000m,
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
        var entity = new ClientCase
        {
            ClientId = clientId,
            Title = title,
            Status = ClientCaseStatus.Open,
        };

        db.ClientCases.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private static async Task<int> CreateActAsync(PaymentContext db, int clientId, string number)
    {
        var entity = new Act
        {
            Number = number,
            Title = $"Act {number}",
            Date = new DateTime(2026, 4, 1),
            Amount = 5_000m,
            ClientId = clientId,
            Status = ActStatus.Created,
        };

        db.Acts.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private static async Task<int> CreateServiceAsync(PaymentContext db, string name)
    {
        var entity = new ServiceDictionaryItem
        {
            Name = name,
            Description = string.Empty,
            Unit = "service",
            DefaultPrice = 0m,
            ColorHex = "#111827",
            IsActive = true,
        };

        db.Services.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private static async Task<int> CreateInvoicePaymentAsync(
        PaymentContext db,
        string invoiceNumber,
        int clientId,
        DateTime dueDate,
        decimal amount,
        PaymentStatus status,
        PaymentType type,
        string? counterpartyName = null)
    {
        var invoice = new Invoice
        {
            Number = invoiceNumber,
            Date = dueDate,
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var payment = new Payment
        {
            Account = invoiceNumber,
            AccountDate = dueDate,
            Amount = amount,
            ClientId = clientId,
            CounterpartyName = counterpartyName,
            Date = dueDate,
            PlannedDate = dueDate,
            Description = $"Invoice {invoiceNumber}",
            DocumentKind = DocumentKind.Invoice,
            InvoiceId = invoice.Id,
            Status = status,
            Type = type,
            PaidAmount = status == PaymentStatus.Completed ? amount : 0m,
            PaidDate = status == PaymentStatus.Completed ? dueDate : null,
            IsPaid = status == PaymentStatus.Completed,
        };

        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment.Id;
    }

    private static async Task AttachResponsibleAsync(PaymentContext db, int paymentId, int userId)
    {
        db.Set<PaymentResponsible>().Add(new PaymentResponsible
        {
            PaymentId = paymentId,
            UserId = userId,
        });
        await db.SaveChangesAsync();
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
