using System.Collections;
using System.Reflection;
using System.Security.Claims;
using System.Text;
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
using PayPlanner.Api.Services.Invoices;
using Xunit;

namespace PayPlanner.Api.Tests;

public class InvoicesControllerInternalCoverageTests
{
    [Fact]
    public void BuildInvoiceHtml_RendersRowsAndTotals()
    {
        var payment = new Payment
        {
            Account = "INV-REF",
            Amount = 1_500m,
            Date = new DateTime(2026, 4, 10),
            Client = new Client { Name = "Internal Client" },
            Invoice = new Invoice
            {
                Number = "INV-REF",
                Date = new DateTime(2026, 4, 10),
            },
        };

        var html = Assert.IsType<string>(InvokePrivateStatic(
            "BuildInvoiceHtml",
            payment,
            new List<InvoiceLineItemDto>
            {
                new()
                {
                    Name = "Service row",
                    Quantity = 2m,
                    Unit = "hour",
                    Price = 750m,
                },
            }));

        Assert.Contains("INV-REF", html);
        Assert.Contains("Internal Client", html);
        Assert.Contains("Service row", html);
        Assert.Contains("1500", html);
    }

    [Fact]
    public async Task GetInvoiceDocumentTemplateAsync_UsesPreferredKey_AndFallsBackToActiveTemplate()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);

        var saveAction = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = "template-a",
                Templates =
                [
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = "template-a",
                        Name = "Template A",
                        Html = "<html><body>a</body></html>",
                    },
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = "template-b",
                        Name = "Template B",
                        Html = "<html><body>b</body></html>",
                    },
                ],
            },
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(saveAction.Result);

        var preferred = await InvokePrivateAsync<InvoiceDocumentTemplateDto>(
            controller,
            "GetInvoiceDocumentTemplateAsync",
            "template-b",
            CancellationToken.None);
        var fallback = await InvokePrivateAsync<InvoiceDocumentTemplateDto>(
            controller,
            "GetInvoiceDocumentTemplateAsync",
            "missing-template",
            CancellationToken.None);

        Assert.Equal("template-b", preferred.Key);
        Assert.Equal("template-a", fallback.Key);
    }

    [Fact]
    public void SyncInvoiceMigrationCaseLinks_UpdatesClientCaseAndClearsLegacyLinks()
    {
        var payment = new Payment
        {
            Id = 7,
            ClientId = 1,
            ClientCaseId = 1,
            InvoiceClientCases =
            [
                new InvoiceClientCase { PaymentId = 7, ClientCaseId = 1 },
                new InvoiceClientCase { PaymentId = 7, ClientCaseId = 2 },
            ],
        };

        var updated = Assert.IsType<bool>(InvokePrivateStatic(
            "SyncInvoiceMigrationCaseLinks",
            payment,
            2,
            (int?)3));
        Assert.True(updated);
        Assert.Equal(2, payment.ClientId);
        Assert.Equal(3, payment.ClientCaseId);
        Assert.Equal([3], payment.InvoiceClientCases.Select(item => item.ClientCaseId).ToArray());

        var cleared = Assert.IsType<bool>(InvokePrivateStatic(
            "SyncInvoiceMigrationCaseLinks",
            payment,
            2,
            null));
        Assert.True(cleared);
        Assert.Null(payment.ClientCaseId);
        Assert.Empty(payment.InvoiceClientCases);
    }

    [Fact]
    public void ExtractInstallmentPaymentMetadata_HandlesBlankMissingBrokenAndValidPayloads()
    {
        Assert.Null(InvokePrivateStatic("ExtractInstallmentPaymentMetadata", (object?)null));
        Assert.Null(InvokePrivateStatic("ExtractInstallmentPaymentMetadata", string.Empty));
        Assert.Null(InvokePrivateStatic("ExtractInstallmentPaymentMetadata", "plain-text"));
        Assert.Null(InvokePrivateStatic("ExtractInstallmentPaymentMetadata", "[pp:installment-payment]"));
        Assert.Null(InvokePrivateStatic("ExtractInstallmentPaymentMetadata", "[pp:installment-payment] broken-json"));

        var valid = InvokePrivateStatic(
            "ExtractInstallmentPaymentMetadata",
            "[pp:installment-payment] {\"Sequence\":2,\"Item\":{\"Sequence\":2,\"Date\":\"2026-06-15T00:00:00\",\"Payment\":4500}}");

        Assert.NotNull(valid);
        Assert.Equal(2, Assert.IsType<int>(valid!.GetType().GetProperty("Sequence")!.GetValue(valid)));
    }

    [Fact]
    public void AssignLegacyAccountSource_SkipsBlankNumber_AndStoresNormalizedValue()
    {
        var payment = new Payment();

        InvokePrivateStatic("AssignLegacyAccountSource", payment, "   ", new DateTime(2026, 4, 10));
        Assert.Null(payment.LegacyAccountNumber);
        Assert.Null(payment.LegacyAccountDate);

        InvokePrivateStatic("AssignLegacyAccountSource", payment, " INV-77 ", new DateTime(2026, 4, 11));
        Assert.Equal("INV-77", payment.LegacyAccountNumber);
        Assert.Equal(new DateTime(2026, 4, 11), payment.LegacyAccountDate);
    }

    [Fact]
    public void ApplySort_SortsByAmountAndResponsible_InMemory()
    {
        var amountSortedDesc = ExtractSortedAccounts(InvokeApplySort(
            CreatePrivateQuery(new[]
            {
                CreateQueryItem("INV-100", 100m, null),
                CreateQueryItem("INV-300", 300m, new User { FullName = "Alpha Lead", Email = "alpha@example.com" }),
                CreateQueryItem("INV-200", 200m, new User { FullName = "Zulu Lead", Email = "zulu@example.com" }),
            }),
            "amount",
            "desc"));
        Assert.Equal(["INV-300", "INV-200", "INV-100"], amountSortedDesc);

        var amountSortedAsc = ExtractSortedAccounts(InvokeApplySort(
            CreatePrivateQuery(new[]
            {
                CreateQueryItem("INV-100", 100m, null),
                CreateQueryItem("INV-300", 300m, new User { FullName = "Alpha Lead", Email = "alpha@example.com" }),
                CreateQueryItem("INV-200", 200m, new User { FullName = "Zulu Lead", Email = "zulu@example.com" }),
            }),
            "amount",
            "asc"));
        Assert.Equal(["INV-100", "INV-200", "INV-300"], amountSortedAsc);

        var responsibleSortedDesc = ExtractSortedAccounts(InvokeApplySort(
            CreatePrivateQuery(new[]
            {
                CreateQueryItem("INV-NONE", 100m, null),
                CreateQueryItem("INV-ALPHA", 200m, new User { FullName = "Alpha Lead", Email = "alpha@example.com" }),
                CreateQueryItem("INV-ZULU", 300m, new User { FullName = "Zulu Lead", Email = "zulu@example.com" }),
            }),
            "responsible",
            "desc"));
        Assert.Equal(["INV-ZULU", "INV-ALPHA", "INV-NONE"], responsibleSortedDesc);

        var responsibleSortedAsc = ExtractSortedAccounts(InvokeApplySort(
            CreatePrivateQuery(new[]
            {
                CreateQueryItem("INV-NONE", 100m, null),
                CreateQueryItem("INV-ALPHA", 200m, new User { FullName = "Alpha Lead", Email = "alpha@example.com" }),
                CreateQueryItem("INV-ZULU", 300m, new User { FullName = "Zulu Lead", Email = "zulu@example.com" }),
            }),
            "responsible",
            "asc"));
        Assert.Equal(["INV-NONE", "INV-ALPHA", "INV-ZULU"], responsibleSortedAsc);
    }

    [Fact]
    public async Task ApplyFilters_FiltersByDateRangeAndClient_InMemory()
    {
        var filtered = ExtractSortedAccounts(await InvokeApplyFiltersAsync(
            CreatePrivateQuery(new[]
            {
                CreateQueryItem(new Payment
                {
                    Account = "INV-OLD",
                    AccountDate = new DateTime(2026, 4, 10),
                    Date = new DateTime(2026, 4, 10),
                    ClientId = 1,
                    Status = PaymentStatus.Pending,
                    Type = PaymentType.Income,
                }, null),
                CreateQueryItem(new Payment
                {
                    Account = "INV-MATCH",
                    AccountDate = new DateTime(2026, 5, 10),
                    Date = new DateTime(2026, 5, 10),
                    ExpenseClientId = 1,
                    Status = PaymentStatus.Pending,
                    Type = PaymentType.Income,
                    Invoice = new Invoice
                    {
                        Number = "INV-MATCH",
                        Date = new DateTime(2026, 5, 15),
                    },
                }, null),
                CreateQueryItem(new Payment
                {
                    Account = "INV-LATE",
                    AccountDate = new DateTime(2026, 6, 10),
                    Date = new DateTime(2026, 6, 10),
                    ClientId = 1,
                    Status = PaymentStatus.Pending,
                    Type = PaymentType.Income,
                }, null),
            }),
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31),
            null,
            1,
            null,
            null,
            null,
            null,
            null,
            null,
            null));

        Assert.Equal(["INV-MATCH"], filtered);
    }

    [Fact]
    public void BuildInstallmentDescription_UsesFallbackText_WhenInvoiceNumberMissing()
    {
        var description = Assert.IsType<string>(InvokePrivateStatic(
            "BuildInstallmentDescription",
            "   ",
            2,
            4));

        Assert.Contains("2/4", description);
    }

    [Fact]
    public void ValidateInvoiceDocumentTemplateSettings_ReturnsSpecificErrors_ForInvalidStorage()
    {
        var blankKeyStorage = InvoiceDocumentTemplateEngine.NormalizeRequest(new InvoiceDocumentTemplateSettingsRequest
        {
            ActiveTemplateKey = "template-a",
            Templates =
            [
                new InvoiceDocumentTemplateRequest
                {
                    Key = " ",
                    Name = "Template A",
                    Html = "<p>a</p>",
                },
            ],
        });
        var blankKeyError = Assert.IsType<string>(InvokePrivateStatic("ValidateInvoiceDocumentTemplateSettings", blankKeyStorage));
        Assert.Contains("ключ", blankKeyError, StringComparison.OrdinalIgnoreCase);

        var duplicateKeyStorage = InvoiceDocumentTemplateEngine.NormalizeRequest(new InvoiceDocumentTemplateSettingsRequest
        {
            ActiveTemplateKey = "template-a",
            Templates =
            [
                new InvoiceDocumentTemplateRequest
                {
                    Key = "template-a",
                    Name = "Template A",
                    Html = "<p>a</p>",
                },
                new InvoiceDocumentTemplateRequest
                {
                    Key = "template-a",
                    Name = "Template B",
                    Html = "<p>b</p>",
                },
            ],
        });
        var duplicateKeyError = Assert.IsType<string>(InvokePrivateStatic("ValidateInvoiceDocumentTemplateSettings", duplicateKeyStorage));
        Assert.Contains("существ", duplicateKeyError, StringComparison.OrdinalIgnoreCase);

        var blankNameStorage = InvoiceDocumentTemplateEngine.NormalizeRequest(new InvoiceDocumentTemplateSettingsRequest
        {
            ActiveTemplateKey = "template-a",
            Templates =
            [
                new InvoiceDocumentTemplateRequest
                {
                    Key = "template-a",
                    Name = " ",
                    Html = "<p>a</p>",
                },
            ],
        });
        var blankNameError = Assert.IsType<string>(InvokePrivateStatic("ValidateInvoiceDocumentTemplateSettings", blankNameStorage));
        Assert.Contains("назван", blankNameError, StringComparison.OrdinalIgnoreCase);

        var blankHtmlStorage = InvoiceDocumentTemplateEngine.NormalizeRequest(new InvoiceDocumentTemplateSettingsRequest
        {
            ActiveTemplateKey = "template-a",
            Templates =
            [
                new InvoiceDocumentTemplateRequest
                {
                    Key = "template-a",
                    Name = "Template A",
                    Html = " ",
                },
            ],
        });
        var blankHtmlError = Assert.IsType<string>(InvokePrivateStatic("ValidateInvoiceDocumentTemplateSettings", blankHtmlStorage));
        Assert.Contains("пуст", blankHtmlError, StringComparison.OrdinalIgnoreCase);

        var blankActiveStorage = InvoiceDocumentTemplateEngine.NormalizeRequest(new InvoiceDocumentTemplateSettingsRequest
        {
            ActiveTemplateKey = " ",
            Templates =
            [
                new InvoiceDocumentTemplateRequest
                {
                    Key = "template-a",
                    Name = "Template A",
                    Html = "<p>a</p>",
                },
            ],
        });
        var blankActiveError = Assert.IsType<string>(InvokePrivateStatic("ValidateInvoiceDocumentTemplateSettings", blankActiveStorage));
        Assert.Contains("актив", blankActiveError, StringComparison.OrdinalIgnoreCase);

        var missingActiveStorage = InvoiceDocumentTemplateEngine.NormalizeRequest(new InvoiceDocumentTemplateSettingsRequest
        {
            ActiveTemplateKey = "template-missing",
            Templates =
            [
                new InvoiceDocumentTemplateRequest
                {
                    Key = "template-a",
                    Name = "Template A",
                    Html = "<p>a</p>",
                },
            ],
        });
        var missingActiveError = Assert.IsType<string>(InvokePrivateStatic("ValidateInvoiceDocumentTemplateSettings", missingActiveStorage));
        Assert.Contains("присутств", missingActiveError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveMigrationInvoiceNumberAsync_UsesNextNumber_ForBlankSource()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Migration Client", null);
        await CreateActiveInvoiceAsync(db, "10", new DateTime(2026, 4, 1), clientId);
        var controller = CreateController(db, isAdmin: true, userId: null);

        var number = await InvokePrivateAsync<string>(
            controller,
            "ResolveMigrationInvoiceNumberAsync",
            "   ",
            new DateTime(2026, 4, 5),
            10,
            "first-free",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);

        Assert.Equal("11", number);
    }

    [Fact]
    public async Task ResolveInvoiceResponsibleIdsAsync_UsesClientResponsible_ThenCurrentUser()
    {
        await using var db = await CreateContextAsync();
        var clientResponsibleId = await CreateUserAsync(db, "Client Responsible");
        var currentUserId = await CreateUserAsync(db, "Current User");
        var clientWithResponsibleId = await CreateClientAsync(db, "Client With Responsible", clientResponsibleId);
        var clientWithoutResponsibleId = await CreateClientAsync(db, "Client Without Responsible", null);
        var controller = CreateController(db, isAdmin: true, userId: currentUserId);

        var fromClient = await InvokePrivateAsync<HashSet<int>>(
            controller,
            "ResolveInvoiceResponsibleIdsAsync",
            clientWithResponsibleId,
            null,
            null,
            CancellationToken.None);
        var fromCurrentUser = await InvokePrivateAsync<HashSet<int>>(
            controller,
            "ResolveInvoiceResponsibleIdsAsync",
            clientWithoutResponsibleId,
            null,
            null,
            CancellationToken.None);

        Assert.Equal([clientResponsibleId], fromClient.OrderBy(id => id).ToArray());
        Assert.Equal([currentUserId], fromCurrentUser.OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task InternalLoaders_ReturnEmptyMaps_AndEnrichSkipsEmptyDtos()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);

        var responsibles = await InvokePrivateAsync<object>(controller, "LoadResponsiblesMapAsync", Array.Empty<int>(), CancellationToken.None);
        var acts = await InvokePrivateAsync<object>(controller, "LoadActsMapAsync", Array.Empty<int>(), CancellationToken.None);
        var clientCases = await InvokePrivateAsync<object>(controller, "LoadClientCasesMapAsync", Array.Empty<int>(), CancellationToken.None);
        var lineItems = await InvokePrivateAsync<object>(controller, "LoadLineItemsMapAsync", Array.Empty<int>(), CancellationToken.None);

        Assert.Empty(Assert.IsAssignableFrom<IDictionary>(responsibles).Keys.Cast<object>());
        Assert.Empty(Assert.IsAssignableFrom<IDictionary>(acts).Keys.Cast<object>());
        Assert.Empty(Assert.IsAssignableFrom<IDictionary>(clientCases).Keys.Cast<object>());
        Assert.Empty(Assert.IsAssignableFrom<IDictionary>(lineItems).Keys.Cast<object>());

        await InvokePrivateAsync(controller, "EnrichInvoiceDtosAsync", new List<InvoiceDto>(), CancellationToken.None);
    }

    [Fact]
    public async Task EnrichInvoiceDtosAsync_PopulatesPrimaryActCaseAndStoredLineItems()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "Enrich client",
            IsActive = true,
        };
        var payment = new Payment
        {
            Account = "INV-ENRICH",
            AccountDate = new DateTime(2026, 4, 12),
            Amount = 5_000m,
            Date = new DateTime(2026, 4, 12),
            PlannedDate = new DateTime(2026, 4, 12),
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            Client = client,
            Invoice = new Invoice
            {
                Number = "INV-ENRICH",
                Date = new DateTime(2026, 4, 12),
            },
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Primary case",
            Description = "Case from enrich",
            Status = ClientCaseStatus.Open,
            CreatedAt = new DateTime(2026, 4, 10),
        };
        var act = new Act
        {
            Number = "ACT-ENRICH",
            Title = "Primary act",
            Date = new DateTime(2026, 4, 13),
            Amount = 5_000m,
            Status = ActStatus.Created,
            Client = client,
        };

        db.Payments.Add(payment);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        act.InvoiceId = payment.Id;
        act.InvoiceNumber = payment.Account;
        db.Acts.Add(act);
        db.InvoiceClientCases.Add(new InvoiceClientCase
        {
            PaymentId = payment.Id,
            ClientCaseId = clientCase.Id,
        });
        db.InvoiceLineItems.Add(new InvoiceLineItem
        {
            PaymentId = payment.Id,
            SortOrder = 1,
            Name = "Stored line",
            Quantity = 2m,
            Unit = "hour",
            Price = 2_500m,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true, userId: null);
        var dto = new InvoiceDto
        {
            Id = payment.Id,
            Number = payment.Account!,
            Date = payment.Date,
            Amount = payment.Amount,
            Status = payment.Status,
            Type = payment.Type,
            LineItems = [],
        };

        await InvokePrivateAsync(controller, "EnrichInvoiceDtosAsync", new List<InvoiceDto> { dto }, CancellationToken.None);

        Assert.Equal(act.Id, dto.ActId);
        Assert.Equal("ACT-ENRICH", dto.ActNumber);
        Assert.Equal("Primary act", dto.ActTitle);
        Assert.Equal(ActStatus.Created, dto.ActStatus);
        Assert.Equal(clientCase.Id, dto.ClientCaseId);
        Assert.Equal("Primary case", dto.ClientCaseTitle);
        Assert.Single(dto.LineItems);
        Assert.Equal("Stored line", dto.LineItems[0].Name);
    }

    [Fact]
    public async Task ExpandInvoiceAggregatePaymentsAsync_ReturnsSourcePayments_WhenInvoiceEntityMissing()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);

        var source = new List<Payment>
        {
            new()
            {
                Id = 101,
                Account = "INV-NO-ENTITY",
                DocumentKind = DocumentKind.Invoice,
            },
        };

        var expanded = await InvokePrivateAsync<List<Payment>>(
            controller,
            "ExpandInvoiceAggregatePaymentsAsync",
            source,
            CancellationToken.None);

        var single = Assert.Single(expanded);
        Assert.Equal(101, single.Id);
    }

    [Fact]
    public async Task LoadActsMapAsync_UsesInvoiceNumberFallback_WhenDirectInvoiceLinkMissing()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "Act fallback client",
            IsActive = true,
        };
        var payment = new Payment
        {
            Account = "INV-FALLBACK",
            AccountDate = new DateTime(2026, 4, 15),
            Amount = 3_000m,
            Date = new DateTime(2026, 4, 15),
            PlannedDate = new DateTime(2026, 4, 15),
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            Client = client,
        };

        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        db.Acts.Add(new Act
        {
            Number = "ACT-FALLBACK",
            Title = "Fallback linked act",
            Date = new DateTime(2026, 4, 16),
            Amount = 3_000m,
            InvoiceNumber = "INV-FALLBACK",
            Status = ActStatus.Created,
            ClientId = client.Id,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true, userId: null);
        var map = await InvokePrivateAsync<Dictionary<int, List<InvoiceActDto>>>(
            controller,
            "LoadActsMapAsync",
            new[] { payment.Id },
            CancellationToken.None);

        var acts = Assert.Single(map[payment.Id]);
        Assert.Equal("ACT-FALLBACK", acts.Number);
    }

    [Fact]
    public async Task LoadClientCasesMapAsync_CreatesBucketForLinkedCases_WhenLegacyReferenceMissing()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "Client case bucket client",
            IsActive = true,
        };
        var payment = new Payment
        {
            Account = "INV-CASE",
            AccountDate = new DateTime(2026, 4, 18),
            Amount = 2_000m,
            Date = new DateTime(2026, 4, 18),
            PlannedDate = new DateTime(2026, 4, 18),
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            Client = client,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Linked only case",
            Description = "Linked via join table",
            Status = ClientCaseStatus.Open,
            CreatedAt = new DateTime(2026, 4, 19),
        };

        db.Payments.Add(payment);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        db.InvoiceClientCases.Add(new InvoiceClientCase
        {
            PaymentId = payment.Id,
            ClientCaseId = clientCase.Id,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true, userId: null);
        var map = await InvokePrivateAsync<Dictionary<int, List<InvoiceClientCaseDto>>>(
            controller,
            "LoadClientCasesMapAsync",
            new[] { payment.Id },
            CancellationToken.None);

        var cases = Assert.Single(map[payment.Id]);
        Assert.Equal("Linked only case", cases.Title);
    }

    [Fact]
    public async Task SyncInvoiceClientCasesAsync_SkipsExistingLinks_AndAddsMissingOnes()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "Sync cases client",
            IsActive = true,
        };
        var payment = new Payment
        {
            Account = "INV-SYNC",
            AccountDate = new DateTime(2026, 4, 20),
            Amount = 1_000m,
            Date = new DateTime(2026, 4, 20),
            PlannedDate = new DateTime(2026, 4, 20),
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            Client = client,
        };
        var caseOne = new ClientCase
        {
            Client = client,
            Title = "Case one",
            Description = "One",
            Status = ClientCaseStatus.Open,
        };
        var caseTwo = new ClientCase
        {
            Client = client,
            Title = "Case two",
            Description = "Two",
            Status = ClientCaseStatus.Open,
        };

        db.Payments.Add(payment);
        db.ClientCases.AddRange(caseOne, caseTwo);
        await db.SaveChangesAsync();

        payment.InvoiceClientCases.Add(new InvoiceClientCase
        {
            PaymentId = payment.Id,
            ClientCaseId = caseOne.Id,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true, userId: null);
        await InvokePrivateAsync(
            controller,
            "SyncInvoiceClientCasesAsync",
            payment,
            new[] { caseOne.Id, caseOne.Id, caseTwo.Id },
            CancellationToken.None);

        Assert.Equal(2, payment.InvoiceClientCases.Count);
        Assert.Single(payment.InvoiceClientCases, link => link.ClientCaseId == caseOne.Id);
        Assert.Single(payment.InvoiceClientCases, link => link.ClientCaseId == caseTwo.Id);
        Assert.True(payment.ClientCaseId.HasValue);
        Assert.Contains(payment.ClientCaseId.Value, new[] { caseOne.Id, caseTwo.Id });
    }

    [Fact]
    public async Task SyncInstallmentPaymentsAsync_HandlesValidationAndDeletionBranches()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);

        var noInvoiceResult = await InvokePrivateAsync<ActionResult?>(
            controller,
            "SyncInstallmentPaymentsAsync",
            new Payment { Id = 900, Account = "INV-NO-ID" },
            new UpsertInvoiceRequest { Date = new DateTime(2026, 4, 10), Amount = 100m },
            Array.Empty<int>(),
            CancellationToken.None);
        Assert.Null(noInvoiceResult);

        var client = new Client
        {
            Name = "Installment client",
            IsActive = true,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var invoiceEntity = new Invoice
        {
            Number = "INV-INSTALLMENT",
            Date = new DateTime(2026, 4, 10),
        };
        db.Invoices.Add(invoiceEntity);
        await db.SaveChangesAsync();

        var invoicePayment = new Payment
        {
            Account = "INV-INSTALLMENT",
            AccountDate = new DateTime(2026, 4, 10),
            Amount = 3_000m,
            Date = new DateTime(2026, 4, 10),
            PlannedDate = new DateTime(2026, 4, 10),
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            ClientId = client.Id,
            InvoiceId = invoiceEntity.Id,
        };

        db.Payments.Add(invoicePayment);
        await db.SaveChangesAsync();

        db.Payments.Add(new Payment
        {
            InvoiceId = invoiceEntity.Id,
            Account = invoicePayment.Account,
            AccountDate = invoicePayment.AccountDate,
            Amount = 1_000m,
            Date = new DateTime(2026, 5, 10),
            PlannedDate = new DateTime(2026, 5, 10),
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Payment,
            ClientId = client.Id,
            PaidAmount = 100m,
            SystemNotes = "[pp:installment-payment] {\"Sequence\":1,\"Item\":{\"Sequence\":1,\"Date\":\"2026-05-10T00:00:00\",\"Payment\":1000}}",
        });
        await db.SaveChangesAsync();

        var removeProtected = await InvokePrivateAsync<ActionResult?>(
            controller,
            "SyncInstallmentPaymentsAsync",
            invoicePayment,
            new UpsertInvoiceRequest
            {
                Date = invoicePayment.Date,
                Amount = invoicePayment.Amount,
                ReplaceInstallmentPlan = true,
            },
            Array.Empty<int>(),
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(removeProtected);

        var invalidValues = await InvokePrivateAsync<ActionResult?>(
            controller,
            "SyncInstallmentPaymentsAsync",
            invoicePayment,
            new UpsertInvoiceRequest
            {
                Date = invoicePayment.Date,
                Amount = invoicePayment.Amount,
                InstallmentPlan = new InvoiceInstallmentPlanRequest
                {
                    Total = 100m,
                    ToPay = 100m,
                    Months = 1,
                    StartDate = new DateTime(2026, 5, 1),
                    Items =
                    [
                        new InvoiceInstallmentPlanItemRequest
                        {
                            Sequence = 0,
                            Date = new DateTime(2026, 5, 1),
                            Payment = 100m,
                            Principal = 100m,
                            Interest = 0m,
                            Balance = 0m,
                        },
                    ],
                },
            },
            Array.Empty<int>(),
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(invalidValues);

        var duplicateSequence = await InvokePrivateAsync<ActionResult?>(
            controller,
            "SyncInstallmentPaymentsAsync",
            invoicePayment,
            new UpsertInvoiceRequest
            {
                Date = invoicePayment.Date,
                Amount = invoicePayment.Amount,
                InstallmentPlan = new InvoiceInstallmentPlanRequest
                {
                    Total = 200m,
                    ToPay = 200m,
                    Months = 2,
                    StartDate = new DateTime(2026, 5, 1),
                    Items =
                    [
                        new InvoiceInstallmentPlanItemRequest
                        {
                            Sequence = 1,
                            Date = new DateTime(2026, 5, 1),
                            Payment = 100m,
                            Principal = 100m,
                            Interest = 0m,
                            Balance = 100m,
                        },
                        new InvoiceInstallmentPlanItemRequest
                        {
                            Sequence = 1,
                            Date = new DateTime(2026, 6, 1),
                            Payment = 100m,
                            Principal = 100m,
                            Interest = 0m,
                            Balance = 0m,
                        },
                    ],
                },
            },
            Array.Empty<int>(),
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(duplicateSequence);
    }

    [Fact]
    public async Task SyncInstallmentPaymentsAsync_RemovesObsoletePayment_WhenNotProtected()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "Obsolete installment client",
            IsActive = true,
        };
        var invoiceEntity = new Invoice
        {
            Number = "INV-REMOVE",
            Date = new DateTime(2026, 4, 10),
        };
        var invoicePayment = new Payment
        {
            Account = "INV-REMOVE",
            AccountDate = new DateTime(2026, 4, 10),
            Amount = 2_000m,
            Date = new DateTime(2026, 4, 10),
            PlannedDate = new DateTime(2026, 4, 10),
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            Client = client,
            Invoice = invoiceEntity,
        };
        var obsoleteInstallment = new Payment
        {
            Invoice = invoiceEntity,
            Account = "INV-REMOVE",
            AccountDate = new DateTime(2026, 4, 10),
            Amount = 1_000m,
            Date = new DateTime(2026, 6, 10),
            PlannedDate = new DateTime(2026, 6, 10),
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Payment,
            Client = client,
            SystemNotes = "[pp:installment-payment] {\"Sequence\":2,\"Item\":{\"Sequence\":2,\"Date\":\"2026-06-10T00:00:00\",\"Payment\":1000}}",
        };

        db.Payments.AddRange(invoicePayment, obsoleteInstallment);
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true, userId: null);
        var result = await InvokePrivateAsync<ActionResult?>(
            controller,
            "SyncInstallmentPaymentsAsync",
            invoicePayment,
            new UpsertInvoiceRequest
            {
                Date = invoicePayment.Date,
                Amount = invoicePayment.Amount,
                ClientId = client.Id,
                InstallmentPlan = new InvoiceInstallmentPlanRequest
                {
                    Total = 1_000m,
                    ToPay = 1_000m,
                    Months = 1,
                    StartDate = new DateTime(2026, 5, 10),
                    Items =
                    [
                        new InvoiceInstallmentPlanItemRequest
                        {
                            Sequence = 1,
                            Date = new DateTime(2026, 5, 10),
                            Payment = 1_000m,
                            Principal = 1_000m,
                            Interest = 0m,
                            Balance = 0m,
                        },
                    ],
                },
            },
            Array.Empty<int>(),
            CancellationToken.None);

        Assert.Null(result);
        Assert.DoesNotContain(db.Payments.Local, payment => payment.Id == obsoleteInstallment.Id);
    }

    [Fact]
    public async Task SyncInstallmentPaymentsAsync_BlocksRemovalOfProtectedObsoletePayment()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "Protected obsolete installment client",
            IsActive = true,
        };
        var invoiceEntity = new Invoice
        {
            Number = "INV-PROTECTED-REMOVE",
            Date = new DateTime(2026, 4, 10),
        };
        var invoicePayment = new Payment
        {
            Account = "INV-PROTECTED-REMOVE",
            AccountDate = new DateTime(2026, 4, 10),
            Amount = 2_000m,
            Date = new DateTime(2026, 4, 10),
            PlannedDate = new DateTime(2026, 4, 10),
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            Client = client,
            Invoice = invoiceEntity,
        };
        var protectedInstallment = new Payment
        {
            Invoice = invoiceEntity,
            Account = "INV-PROTECTED-REMOVE",
            AccountDate = new DateTime(2026, 4, 10),
            Amount = 1_000m,
            Date = new DateTime(2026, 6, 10),
            PlannedDate = new DateTime(2026, 6, 10),
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Payment,
            Client = client,
            PaidAmount = 1m,
            SystemNotes = "[pp:installment-payment] {\"Sequence\":2,\"Item\":{\"Sequence\":2,\"Date\":\"2026-06-10T00:00:00\",\"Payment\":1000}}",
        };

        db.Payments.AddRange(invoicePayment, protectedInstallment);
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true, userId: null);
        var result = await InvokePrivateAsync<ActionResult?>(
            controller,
            "SyncInstallmentPaymentsAsync",
            invoicePayment,
            new UpsertInvoiceRequest
            {
                Date = invoicePayment.Date,
                Amount = invoicePayment.Amount,
                ClientId = client.Id,
                InstallmentPlan = new InvoiceInstallmentPlanRequest
                {
                    Total = 1_000m,
                    ToPay = 1_000m,
                    Months = 1,
                    StartDate = new DateTime(2026, 5, 10),
                    Items =
                    [
                        new InvoiceInstallmentPlanItemRequest
                        {
                            Sequence = 1,
                            Date = new DateTime(2026, 5, 10),
                            Payment = 1_000m,
                            Principal = 1_000m,
                            Interest = 0m,
                            Balance = 0m,
                        },
                    ],
                },
            },
            Array.Empty<int>(),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Нельзя удалить платеж рассрочки", badRequest.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveMigrationInvoiceNumberAsync_HandlesReservedSuffixes_AndFallbackAfterLimit()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);
        var invoiceDate = new DateTime(2026, 4, 21);

        var reservedForSuffix = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            BuildReservedInvoiceNumberKey("DOC", invoiceDate),
            BuildReservedInvoiceNumberKey("DOC-IMP1", invoiceDate),
        };

        var withSuffix = await InvokePrivateAsync<string>(
            controller,
            "ResolveMigrationInvoiceNumberAsync",
            "DOC",
            invoiceDate,
            10,
            "first-free",
            reservedForSuffix,
            CancellationToken.None);

        Assert.Equal("DOC-IMP2", withSuffix);

        var fullyReserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            BuildReservedInvoiceNumberKey("DOC", invoiceDate),
        };
        for (var i = 1; i <= 9999; i++)
        {
            fullyReserved.Add(BuildReservedInvoiceNumberKey($"DOC-IMP{i}", invoiceDate));
        }

        var fallback = await InvokePrivateAsync<string>(
            controller,
            "ResolveMigrationInvoiceNumberAsync",
            "DOC",
            invoiceDate,
            10,
            "first-free",
            fullyReserved,
            CancellationToken.None);

        Assert.Equal("10", fallback);
    }

    [Fact]
    public async Task ResolveMigrationInvoiceNumberAsync_SkipsOccupiedSuffixCandidate()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);
        var clientId = await CreateClientAsync(db, "Occupied suffix client", null);
        var invoiceDate = new DateTime(2026, 4, 22);

        await CreateActiveInvoiceAsync(db, "DOC-IMP2", invoiceDate, clientId);

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            BuildReservedInvoiceNumberKey("DOC", invoiceDate),
            BuildReservedInvoiceNumberKey("DOC-IMP1", invoiceDate),
        };

        var result = await InvokePrivateAsync<string>(
            controller,
            "ResolveMigrationInvoiceNumberAsync",
            "DOC",
            invoiceDate,
            10,
            "first-free",
            reserved,
            CancellationToken.None);

        Assert.Equal("DOC-IMP3", result);
    }

    [Fact]
    public async Task BuildInstallmentScheduleHtmlAsync_ReturnsEmpty_WhenInvoiceIdMissing()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);

        var html = await InvokePrivateAsync<string>(
            controller,
            "BuildInstallmentScheduleHtmlAsync",
            new Payment
            {
                Id = 404,
                Account = "INV-NO-SCHEDULE",
            },
            CancellationToken.None);

        Assert.Equal(string.Empty, html);
    }

    [Fact]
    public async Task BuildInstallmentScheduleHtmlAsync_RendersOrderedItems_FromMetadataAndFallbackValues()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "Schedule client",
            IsActive = true,
        };
        var invoiceEntity = new Invoice
        {
            Number = "INV-SCHEDULE",
            Date = new DateTime(2026, 4, 10),
        };
        var invoicePayment = new Payment
        {
            Account = "INV-SCHEDULE",
            AccountDate = new DateTime(2026, 4, 10),
            Amount = 3_000m,
            Date = new DateTime(2026, 4, 10),
            PlannedDate = new DateTime(2026, 4, 10),
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            Client = client,
            Invoice = invoiceEntity,
        };
        var firstInstallment = new Payment
        {
            Invoice = invoiceEntity,
            Account = "INV-SCHEDULE",
            AccountDate = new DateTime(2026, 4, 10),
            Amount = 1_500m,
            Date = new DateTime(2026, 5, 10),
            PlannedDate = new DateTime(2026, 5, 10),
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Payment,
            Client = client,
            SystemNotes = "[pp:installment-payment] {\"Sequence\":2,\"Item\":{\"Sequence\":2,\"Date\":\"2026-05-20T00:00:00\",\"Payment\":1750}}",
        };
        var secondInstallment = new Payment
        {
            Invoice = invoiceEntity,
            Account = "INV-SCHEDULE",
            AccountDate = new DateTime(2026, 4, 10),
            Amount = 1_250m,
            Date = new DateTime(2026, 5, 5),
            PlannedDate = new DateTime(2026, 5, 5),
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Payment,
            Client = client,
            SystemNotes = "[pp:installment-payment] {\"Sequence\":0,\"Item\":{\"Sequence\":0,\"Date\":\"0001-01-01T00:00:00\",\"Payment\":0}}",
        };

        db.Payments.AddRange(invoicePayment, firstInstallment, secondInstallment);
        await db.SaveChangesAsync();

        var controller = CreateController(db, isAdmin: true, userId: null);
        var html = await InvokePrivateAsync<string>(
            controller,
            "BuildInstallmentScheduleHtmlAsync",
            invoicePayment,
            CancellationToken.None);
        var normalizedHtml = html.Replace('\u00A0', ' ');

        Assert.Contains("installment-schedule", html);
        Assert.Contains("20.05.2026", normalizedHtml);
        Assert.Contains("1 750", normalizedHtml);
        Assert.Contains("05.05.2026", normalizedHtml);
        Assert.Contains("1 250", normalizedHtml);
        Assert.True(normalizedHtml.IndexOf("20.05.2026", StringComparison.Ordinal) < normalizedHtml.IndexOf("05.05.2026", StringComparison.Ordinal));
    }

    [Fact]
    public void InvoiceNestedRecords_ExposeConstructorValues()
    {
        var installmentType = typeof(InvoicesController).GetNestedType("InstallmentScheduleItem", BindingFlags.NonPublic);
        var bindingType = typeof(InvoicesController).GetNestedType("InvoiceMigrationBinding", BindingFlags.NonPublic);

        Assert.NotNull(installmentType);
        Assert.NotNull(bindingType);

        var installment = Activator.CreateInstance(installmentType!, 3, new DateTime(2026, 6, 15), 4_500m, 91);
        Assert.Equal(3, installmentType!.GetProperty("Sequence")!.GetValue(installment));
        Assert.Equal(new DateTime(2026, 6, 15), installmentType.GetProperty("Date")!.GetValue(installment));
        Assert.Equal(4_500m, installmentType.GetProperty("Amount")!.GetValue(installment));
        Assert.Equal(91, installmentType.GetProperty("Id")!.GetValue(installment));
        installmentType.GetProperty("Sequence")!.SetValue(installment, 4);
        installmentType.GetProperty("Date")!.SetValue(installment, new DateTime(2026, 6, 16));
        installmentType.GetProperty("Amount")!.SetValue(installment, 4_600m);
        installmentType.GetProperty("Id")!.SetValue(installment, 92);

        var payment = new Payment { Id = 77, Type = PaymentType.Expense };
        var binding = Activator.CreateInstance(
            bindingType!,
            payment,
            "2026::INV-1",
            "INV-1",
            new DateTime(2026, 4, 1),
            12,
            34,
            PaymentType.Expense);

        Assert.Equal(payment, bindingType!.GetProperty("Payment")!.GetValue(binding));
        Assert.Equal("2026::INV-1", bindingType.GetProperty("Key")!.GetValue(binding));
        Assert.Equal("INV-1", bindingType.GetProperty("Number")!.GetValue(binding));
        Assert.Equal(12, bindingType.GetProperty("ClientId")!.GetValue(binding));
        Assert.Equal(34, bindingType.GetProperty("ClientCaseId")!.GetValue(binding));
        Assert.Equal(PaymentType.Expense, bindingType.GetProperty("Type")!.GetValue(binding));
        var updatedPayment = new Payment { Id = 78, Type = PaymentType.Income };
        bindingType.GetProperty("Payment")!.SetValue(binding, updatedPayment);
        bindingType.GetProperty("Key")!.SetValue(binding, "2026::INV-2");
        bindingType.GetProperty("Number")!.SetValue(binding, "INV-2");
        bindingType.GetProperty("InvoiceDate")!.SetValue(binding, new DateTime(2026, 4, 2));
        bindingType.GetProperty("ClientId")!.SetValue(binding, 13);
        bindingType.GetProperty("ClientCaseId")!.SetValue(binding, 35);
        bindingType.GetProperty("Type")!.SetValue(binding, PaymentType.Income);

        var installmentCopyCtor = installmentType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            [installmentType],
            modifiers: null);
        Assert.NotNull(installmentCopyCtor);
        Assert.NotNull(installmentCopyCtor!.Invoke([installment]));

        var bindingCopyCtor = bindingType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            [bindingType],
            modifiers: null);
        Assert.NotNull(bindingCopyCtor);
        Assert.NotNull(bindingCopyCtor!.Invoke([binding]));

        Assert.Contains("InstallmentScheduleItem", installment!.ToString(), StringComparison.Ordinal);
        Assert.Contains("InvoiceMigrationBinding", binding!.ToString(), StringComparison.Ordinal);
        Assert.True(installment.Equals(Activator.CreateInstance(installmentType, 4, new DateTime(2026, 6, 16), 4_600m, 92)));
        Assert.True(binding.Equals(Activator.CreateInstance(
            bindingType,
            updatedPayment,
            "2026::INV-2",
            "INV-2",
            new DateTime(2026, 4, 2),
            13,
            35,
            PaymentType.Income)));
    }

    [Fact]
    public async Task GetDocumentTemplateSettings_ReturnsStoredTemplates()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);

        var saveAction = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = "custom-template",
                PrintWithSignatureAndSealByDefault = true,
                Templates =
                [
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = "custom-template",
                        Name = "Custom",
                        Html = "<html><body>{{InvoiceNumber}}</body></html>",
                    },
                ],
            },
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(saveAction.Result);

        var getAction = await controller.GetDocumentTemplateSettings(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(getAction.Result);
        var settings = Assert.IsType<InvoiceDocumentTemplateSettingsDto>(ok.Value);
        Assert.Equal("custom-template", settings.ActiveTemplateKey);
        Assert.True(settings.PrintWithSignatureAndSealByDefault);
        Assert.Contains(settings.Templates, template => template.Key == "custom-template");
        Assert.Contains(settings.Templates, template => template.Key == InvoiceDocumentTemplateEngine.OfferTemplateKey);
    }

    [Fact]
    public async Task GetDocument_ReturnsHtml_WithStoredLineItemsAndMainCompany()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);

        var company = new LegalEntity
        {
            ShortName = "Main Company",
            FullName = "Main Company LLC",
            CreatedAt = DateTime.UtcNow,
        };
        var client = new Client
        {
            Name = "Document client",
            Email = "document-client@example.com",
            IsActive = true,
        };
        var payment = new Payment
        {
            Account = "INV-DOC",
            AccountDate = new DateTime(2026, 4, 23),
            Amount = 7_500m,
            Date = new DateTime(2026, 4, 23),
            PlannedDate = new DateTime(2026, 4, 23),
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            Client = client,
            Invoice = new Invoice
            {
                Number = "INV-DOC",
                Date = new DateTime(2026, 4, 23),
            },
        };

        db.LegalEntities.Add(company);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.defaultMainCompanyId",
            Value = company.Id.ToString(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.InvoiceLineItems.Add(new InvoiceLineItem
        {
            PaymentId = payment.Id,
            SortOrder = 1,
            Name = "Document line item",
            Quantity = 1m,
            Unit = "service",
            Price = 7_500m,
        });
        await db.SaveChangesAsync();

        var action = await controller.GetDocument(payment.Id, "html", null, false, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(action);
        Assert.Equal("text/html; charset=utf-8", content.ContentType);
        Assert.Contains("INV-DOC", content.Content ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("Document line item", content.Content ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("Main Company", content.Content ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendInvoiceEmail_UsesClientEmail_AndNormalizesHtmlFormatToWord()
    {
        await using var db = await CreateContextAsync();
        var emailService = new RecordingEmailService();
        var controller = CreateController(db, isAdmin: true, userId: null, emailService: emailService);

        var client = new Client
        {
            Name = "Email client",
            Email = "email-client@example.com",
            IsActive = true,
        };
        var payment = new Payment
        {
            Account = "INV-MAIL",
            AccountDate = new DateTime(2026, 4, 24),
            Amount = 4_000m,
            Date = new DateTime(2026, 4, 24),
            PlannedDate = new DateTime(2026, 4, 24),
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            Client = client,
            Invoice = new Invoice
            {
                Number = "INV-MAIL",
                Date = new DateTime(2026, 4, 24),
            },
        };

        db.Payments.Add(payment);
        db.InvoiceLineItems.Add(new InvoiceLineItem
        {
            Payment = payment,
            SortOrder = 1,
            Name = "Mail line item",
            Quantity = 1m,
            Unit = "service",
            Price = 4_000m,
        });
        await db.SaveChangesAsync();

        var action = await controller.SendInvoiceEmail(
            payment.Id,
            new SendInvoiceEmailRequest
            {
                Format = "html",
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.NotNull(emailService.LastMessage);
        Assert.Equal("email-client@example.com", emailService.LastMessage!.ToEmail);
        Assert.Equal("application/msword", emailService.LastMessage.ContentType);
        Assert.EndsWith(".doc", emailService.LastMessage.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INV-MAIL", emailService.LastMessage.Subject, StringComparison.Ordinal);
        Assert.Contains("<!DOCTYPE html>", Encoding.UTF8.GetString(emailService.LastMessage.Bytes), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("email-client@example.com", ok.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void SyncInvoiceMigrationCaseLinks_LeavesMatchingLinkUntouched()
    {
        var payment = new Payment
        {
            Id = 8,
            ClientId = 2,
            ClientCaseId = 7,
            InvoiceClientCases =
            [
                new InvoiceClientCase
                {
                    PaymentId = 8,
                    ClientCaseId = 7,
                },
            ],
        };

        var changed = Assert.IsType<bool>(InvokePrivateStatic(
            "SyncInvoiceMigrationCaseLinks",
            payment,
            2,
            (int?)7));

        Assert.False(changed);
        Assert.Single(payment.InvoiceClientCases);
        Assert.Equal(7, payment.InvoiceClientCases.Single().ClientCaseId);
    }

    [Fact]
    public async Task UpsertDocumentTemplateSettings_ReturnsForbid_WhenUserIsNotAdmin()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: false, userId: null);

        var action = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = "template-a",
                Templates =
                [
                    new InvoiceDocumentTemplateRequest
                    {
                        Key = "template-a",
                        Name = "Template A",
                        Html = "<html><body>a</body></html>",
                    },
                ],
            },
            CancellationToken.None);

        Assert.IsType<ForbidResult>(action.Result);
    }

    [Fact]
    public async Task UpsertDocumentTemplateSettings_ReturnsValidationProblem_WhenModelStateIsInvalid()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);
        controller.ModelState.AddModelError("Templates", "Templates are required.");

        var action = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = "template-a",
            },
            CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(action.Result);
        Assert.IsType<ValidationProblemDetails>(problem.Value);
    }

    [Fact]
    public async Task UpsertDocumentTemplateSettings_ReturnsBadRequest_ForInvalidTemplatePayload()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);

        var action = await controller.UpsertDocumentTemplateSettings(
            new InvoiceDocumentTemplateSettingsRequest
            {
                ActiveTemplateKey = string.Empty,
                Templates = [],
            },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("шаблон", badRequest.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetDocument_ReturnsNotFound_WhenPaymentDoesNotExist()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);

        var action = await controller.GetDocument(42_424, "html", null, null, CancellationToken.None);

        Assert.IsType<NotFoundResult>(action);
    }

    [Fact]
    public async Task GetDocument_ReturnsWordFile_ForNonHtmlFormat()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);
        var payment = CreateInvoiceDocumentPayment("INV-WORD", new DateTime(2026, 4, 25), 3_300m, "Word client");

        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var action = await controller.GetDocument(payment.Id, "word", null, false, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(action);
        Assert.Equal("application/msword", file.ContentType);
        Assert.EndsWith(".doc", file.FileDownloadName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<!DOCTYPE html>", Encoding.UTF8.GetString(file.FileContents), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetDocument_ReturnsPdfFile_WhenPdfServiceIsAvailable()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(
            db,
            isAdmin: true,
            userId: null,
            pdfService: new FakeInvoiceDocumentPdfService([1, 2, 3, 4]));
        var payment = CreateInvoiceDocumentPayment("INV-PDF", new DateTime(2026, 4, 26), 5_000m, "Pdf client");

        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var action = await controller.GetDocument(payment.Id, "pdf", null, true, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(action);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.EndsWith(".pdf", file.FileDownloadName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([1, 2, 3, 4], file.FileContents);
    }

    [Fact]
    public async Task GetDocument_ReturnsProblem_WhenPdfGeneratorIsMissing()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);
        var payment = CreateInvoiceDocumentPayment("INV-PDF-ERR", new DateTime(2026, 4, 27), 6_100m, "Pdf error client");

        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var action = await controller.GetDocument(payment.Id, "pdf", null, null, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        var details = Assert.IsType<ProblemDetails>(problem.Value);
        Assert.False(string.IsNullOrWhiteSpace(details.Detail));
    }

    [Fact]
    public async Task SendInvoiceEmail_ReturnsValidationProblem_WhenModelStateIsInvalid()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);
        controller.ModelState.AddModelError("Email", "Invalid email.");

        var action = await controller.SendInvoiceEmail(
            1,
            new SendInvoiceEmailRequest
            {
                Email = "bad-email",
            },
            CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(action);
        Assert.IsType<ValidationProblemDetails>(problem.Value);
    }

    [Fact]
    public async Task SendInvoiceEmail_ReturnsNotFound_WhenPaymentDoesNotExist()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);

        var action = await controller.SendInvoiceEmail(
            99_999,
            new SendInvoiceEmailRequest
            {
                Format = "word",
            },
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(action);
    }

    [Fact]
    public async Task SendInvoiceEmail_ReturnsBadRequest_WhenRecipientEmailIsMissing()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);
        var payment = CreateInvoiceDocumentPayment("INV-NO-MAIL", new DateTime(2026, 4, 28), 2_000m, "No mail client", "   ");

        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var action = await controller.SendInvoiceEmail(
            payment.Id,
            new SendInvoiceEmailRequest
            {
                Format = "word",
            },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Equal("Recipient email is required.", badRequest.Value);
    }

    [Fact]
    public async Task SendInvoiceEmail_SendsPdf_WithExplicitOverrides()
    {
        await using var db = await CreateContextAsync();
        var emailService = new RecordingEmailService();
        var controller = CreateController(
            db,
            isAdmin: true,
            userId: null,
            emailService: emailService,
            pdfService: new FakeInvoiceDocumentPdfService([9, 8, 7]));
        var payment = CreateInvoiceDocumentPayment("INV-MAIL-PDF", new DateTime(2026, 4, 29), 8_200m, "Pdf email client");

        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var action = await controller.SendInvoiceEmail(
            payment.Id,
            new SendInvoiceEmailRequest
            {
                Email = "override@example.com",
                Subject = "Custom invoice subject",
                Message = "Custom invoice body",
                Format = "pdf",
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.NotNull(emailService.LastMessage);
        Assert.Equal("override@example.com", emailService.LastMessage!.ToEmail);
        Assert.Equal("Custom invoice subject", emailService.LastMessage.Subject);
        Assert.Equal("Custom invoice body", emailService.LastMessage.Body);
        Assert.Equal("application/pdf", emailService.LastMessage.ContentType);
        Assert.EndsWith(".pdf", emailService.LastMessage.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([9, 8, 7], emailService.LastMessage.Bytes);
        Assert.Contains("override@example.com", ok.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendInvoiceEmail_ReturnsProblem_WhenPdfGeneratorIsMissing()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);
        var payment = CreateInvoiceDocumentPayment("INV-MAIL-PDF-ERR", new DateTime(2026, 4, 30), 4_800m, "Pdf fail client");

        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var action = await controller.SendInvoiceEmail(
            payment.Id,
            new SendInvoiceEmailRequest
            {
                Email = "pdf@example.com",
                Format = "pdf",
            },
            CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        var details = Assert.IsType<ProblemDetails>(problem.Value);
        Assert.False(string.IsNullOrWhiteSpace(details.Detail));
    }

    [Fact]
    public async Task LoadMainCompanyAsync_UsesLegacyAndIdsSettings_AndHandlesInvalidJson()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);
        var legacyCompany = new LegalEntity
        {
            ShortName = "Legacy company",
            FullName = "Legacy company LLC",
            CreatedAt = DateTime.UtcNow,
        };
        var idsCompany = new LegalEntity
        {
            ShortName = "Ids company",
            FullName = "Ids company LLC",
            CreatedAt = DateTime.UtcNow,
        };

        db.LegalEntities.AddRange(legacyCompany, idsCompany);
        await db.SaveChangesAsync();

        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.mainCompanyId",
            Value = legacyCompany.Id.ToString(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var fromLegacy = await InvokePrivateAsync<LegalEntity?>(controller, "LoadMainCompanyAsync", CancellationToken.None);
        Assert.NotNull(fromLegacy);
        Assert.Equal(legacyCompany.Id, fromLegacy!.Id);

        db.SystemSettings.RemoveRange(db.SystemSettings.ToList());
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "legalEntities.mainCompanyIds",
            Value = $"[0,{idsCompany.Id}]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var fromIds = await InvokePrivateAsync<LegalEntity?>(controller, "LoadMainCompanyAsync", CancellationToken.None);
        Assert.NotNull(fromIds);
        Assert.Equal(idsCompany.Id, fromIds!.Id);

        var idsSetting = await db.SystemSettings.SingleAsync();
        idsSetting.Value = "{broken json";
        await db.SaveChangesAsync();

        var invalid = await InvokePrivateAsync<LegalEntity?>(controller, "LoadMainCompanyAsync", CancellationToken.None);
        Assert.Null(invalid);
    }

    [Fact]
    public async Task DetachActsFromDeletedInvoicePaymentsAsync_ReturnsWhenRepresentativePaymentsMissing()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);

        await InvokePrivateAsync(
            controller,
            "DetachActsFromDeletedInvoicePaymentsAsync",
            new[]
            {
                new Payment
                {
                    Id = 501,
                    DocumentKind = DocumentKind.Payment,
                    InvoiceId = 501,
                },
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task DetachActsFromDeletedInvoicePaymentsAsync_ReturnsWhenLinkedActsMissing()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);

        await InvokePrivateAsync(
            controller,
            "DetachActsFromDeletedInvoicePaymentsAsync",
            new[]
            {
                new Payment
                {
                    Id = 777,
                    DocumentKind = DocumentKind.Invoice,
                    InvoiceId = 777,
                },
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task ValidateInvoiceClientCasesAsync_ReturnsMissingAndForeignErrors()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);

        var owner = new Client
        {
            Name = "Owner client",
            IsActive = true,
        };
        var foreign = new Client
        {
            Name = "Foreign client",
            IsActive = true,
        };
        db.Clients.AddRange(owner, foreign);
        await db.SaveChangesAsync();

        db.SystemSettings.Add(new SystemSetting
        {
            Key = "invoices.rules.maxClientCasesPerInvoice",
            Value = "5",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var ownerCase = new ClientCase
        {
            ClientId = owner.Id,
            Title = "Owner case",
            Status = ClientCaseStatus.Open,
        };
        var foreignCase = new ClientCase
        {
            ClientId = foreign.Id,
            Title = "Foreign case",
            Status = ClientCaseStatus.Open,
        };
        var foreignCaseTwo = new ClientCase
        {
            ClientId = foreign.Id,
            Title = "Foreign case two",
            Status = ClientCaseStatus.Open,
        };
        db.ClientCases.AddRange(ownerCase, foreignCase, foreignCaseTwo);
        await db.SaveChangesAsync();

        var missingResult = await InvokePrivateAsync<object>(
            controller,
            "ValidateInvoiceClientCasesAsync",
            owner.Id,
            null,
            new UpsertInvoiceRequest
            {
                ClientCaseIds = [ownerCase.Id, 99_001],
            },
            CancellationToken.None);

        var missingError = Assert.IsType<BadRequestObjectResult>(GetTupleField(missingResult, "Item1"));
        Assert.Contains("99001", missingError.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(Assert.IsType<List<int>>(GetTupleField(missingResult, "Item2")));

        var foreignResult = await InvokePrivateAsync<object>(
            controller,
            "ValidateInvoiceClientCasesAsync",
            owner.Id,
            null,
            new UpsertInvoiceRequest
            {
                ClientCaseIds = [foreignCase.Id, foreignCaseTwo.Id],
            },
            CancellationToken.None);

        var foreignError = Assert.IsType<BadRequestObjectResult>(GetTupleField(foreignResult, "Item1"));
        Assert.Contains(foreignCase.Id.ToString(), foreignError.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(Assert.IsType<List<int>>(GetTupleField(foreignResult, "Item2")));
    }

    [Fact]
    public async Task ValidateInvoiceActsAsync_ReturnsMissingForeignAndLinkedErrors()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);

        var owner = new Client
        {
            Name = "Owner act client",
            IsActive = true,
        };
        var foreign = new Client
        {
            Name = "Foreign act client",
            IsActive = true,
        };
        db.Clients.AddRange(owner, foreign);
        await db.SaveChangesAsync();

        db.SystemSettings.Add(new SystemSetting
        {
            Key = "invoices.rules.maxActsPerInvoice",
            Value = "5",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var ownAct = new Act
        {
            Number = "ACT-OWN",
            Date = new DateTime(2026, 5, 1),
            Amount = 1_000m,
            ClientId = owner.Id,
        };
        var foreignAct = new Act
        {
            Number = "ACT-FOREIGN",
            Date = new DateTime(2026, 5, 1),
            Amount = 1_100m,
            ClientId = foreign.Id,
        };
        var linkedAct = new Act
        {
            Number = "ACT-LINKED",
            Date = new DateTime(2026, 5, 1),
            Amount = 1_200m,
            ClientId = owner.Id,
        };
        var foreignActTwo = new Act
        {
            Number = "ACT-FOREIGN-2",
            Date = new DateTime(2026, 5, 1),
            Amount = 1_150m,
            ClientId = foreign.Id,
        };
        var linkedActTwo = new Act
        {
            Number = "ACT-LINKED-2",
            Date = new DateTime(2026, 5, 1),
            Amount = 1_250m,
            ClientId = owner.Id,
        };

        var linkedPayment = CreateInvoiceDocumentPayment("INV-LINKED", new DateTime(2026, 5, 1), 3_000m, "Linked act owner");

        db.Payments.Add(linkedPayment);
        db.Acts.AddRange(ownAct, foreignAct, linkedAct, foreignActTwo, linkedActTwo);
        await db.SaveChangesAsync();

        linkedAct.InvoiceId = linkedPayment.Id;
        linkedActTwo.InvoiceId = linkedPayment.Id;
        await db.SaveChangesAsync();

        var missingResult = await InvokePrivateAsync<object>(
            controller,
            "ValidateInvoiceActsAsync",
            owner.Id,
            null,
            new UpsertInvoiceRequest
            {
                ActIds = [ownAct.Id, 88_001],
            },
            null,
            null,
            CancellationToken.None);

        var missingError = Assert.IsType<BadRequestObjectResult>(GetTupleField(missingResult, "Item1"));
        Assert.Contains("88001", missingError.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);

        var foreignResult = await InvokePrivateAsync<object>(
            controller,
            "ValidateInvoiceActsAsync",
            owner.Id,
            null,
            new UpsertInvoiceRequest
            {
                ActIds = [foreignAct.Id, foreignActTwo.Id],
            },
            null,
            null,
            CancellationToken.None);

        var foreignError = Assert.IsType<BadRequestObjectResult>(GetTupleField(foreignResult, "Item1"));
        Assert.Contains(foreignAct.Id.ToString(), foreignError.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);

        var linkedResult = await InvokePrivateAsync<object>(
            controller,
            "ValidateInvoiceActsAsync",
            owner.Id,
            null,
            new UpsertInvoiceRequest
            {
                ActIds = [linkedAct.Id, linkedActTwo.Id],
            },
            null,
            null,
            CancellationToken.None);

        var linkedError = Assert.IsType<BadRequestObjectResult>(GetTupleField(linkedResult, "Item1"));
        Assert.Contains(linkedAct.Id.ToString(), linkedError.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertSystemSettingAsync_UpdatesExistingSetting()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);
        var createdAt = new DateTime(2026, 5, 2, 9, 0, 0, DateTimeKind.Utc);
        var updatedAt = createdAt.AddHours(2);

        db.SystemSettings.Add(new SystemSetting
        {
            Key = "internal-test-setting",
            Value = "old",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();

        await InvokePrivateAsync(
            controller,
            "UpsertSystemSettingAsync",
            "internal-test-setting",
            "new",
            updatedAt,
            CancellationToken.None);
        await db.SaveChangesAsync();

        var setting = await db.SystemSettings.SingleAsync(x => x.Key == "internal-test-setting");
        Assert.Equal("new", setting.Value);
        Assert.Equal(updatedAt, setting.UpdatedAt);
        Assert.Equal(createdAt, setting.CreatedAt);
    }

    [Fact]
    public void ResolveAvailableImportInvoiceNumber_ReturnsNull_WhenAllImportSuffixesAreReserved()
    {
        var reservedNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "INV-LOCKED",
        };
        reservedNumbers.UnionWith(Enumerable.Range(1, 9_999).Select(i => $"INV-LOCKED-IMP{i}"));

        var resolved = InvokePrivateStatic(
            "ResolveAvailableImportInvoiceNumber",
            "INV-LOCKED",
            reservedNumbers);

        Assert.Null(resolved);
    }

    private static string BuildReservedInvoiceNumberKey(string number, DateTime invoiceDate)
    {
        var method = typeof(InvoicesController).GetMethod("BuildReservedInvoiceNumberKey", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(null, [number, invoiceDate]));
    }

    private static object? InvokePrivateStatic(string methodName, params object?[] args)
    {
        var method = typeof(InvoicesController).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!.Invoke(null, args);
    }

    private static object CreateQueryItem(string account, decimal amount, User? responsible)
    {
        return CreateQueryItem(new Payment
        {
            Account = account,
            Amount = amount,
        }, responsible);
    }

    private static object CreateQueryItem(Payment payment, User? responsible)
    {
        var queryItemType = typeof(InvoicesController).GetNestedType("InvoiceQueryItem", BindingFlags.NonPublic);
        Assert.NotNull(queryItemType);

        var instance = Activator.CreateInstance(queryItemType!, nonPublic: true);
        Assert.NotNull(instance);

        queryItemType!.GetProperty("Payment")!.SetValue(instance, payment);
        queryItemType.GetProperty("Act")!.SetValue(instance, responsible is null
            ? null
            : new Act
            {
                Responsible = responsible,
            });

        return instance;
    }

    private static object CreatePrivateQuery(IEnumerable<object> items)
    {
        var queryItemType = typeof(InvoicesController).GetNestedType("InvoiceQueryItem", BindingFlags.NonPublic);
        Assert.NotNull(queryItemType);

        var array = Array.CreateInstance(queryItemType!, items.Count());
        var index = 0;
        foreach (var item in items)
        {
            array.SetValue(item, index);
            index++;
        }

        var asQueryable = typeof(Queryable).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method =>
                method.Name == nameof(Queryable.AsQueryable)
                && method.IsGenericMethodDefinition
                && method.GetParameters().Length == 1);

        return asQueryable.MakeGenericMethod(queryItemType!).Invoke(null, [array])!;
    }

    private static object InvokeApplySort(object query, string sortBy, string sortDir)
    {
        var method = typeof(InvoicesController).GetMethod("ApplySort", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!.Invoke(null, [query, sortBy, sortDir])!;
    }

    private static async Task<object> InvokeApplyFiltersAsync(
        object query,
        DateTime? from,
        DateTime? to,
        PaymentStatus? status,
        int? clientId,
        int? legalEntityId,
        int? contractId,
        int? caseId,
        int? responsibleId,
        string? search,
        PaymentType? type,
        IReadOnlySet<int>? clientIds,
        string? clientRole = null)
    {
        var db = await CreateContextAsync();
        var controller = CreateController(db, isAdmin: true, userId: null);
        var method = typeof(InvoicesController).GetMethod("ApplyFilters", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return method!.Invoke(controller, [query, from, to, status, clientId, legalEntityId, contractId, caseId, responsibleId, search, type, clientRole, clientIds])!;
    }

    private static string[] ExtractSortedAccounts(object query)
    {
        var results = new List<string>();
        foreach (var item in Assert.IsAssignableFrom<IEnumerable>(query))
        {
            var payment = Assert.IsType<Payment>(item!.GetType().GetProperty("Payment")!.GetValue(item));
            results.Add(payment.Account ?? string.Empty);
        }

        return results.ToArray();
    }

    private static async Task InvokePrivateAsync(object target, string methodName, params object?[] args)
    {
        var task = await InvokePrivateTaskCoreAsync(target, methodName, args);
        await task;
    }

    private static async Task<T> InvokePrivateAsync<T>(object target, string methodName, params object?[] args)
    {
        var task = await InvokePrivateTaskCoreAsync(target, methodName, args);
        await task;
        return (T)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static Task<Task> InvokePrivateTaskCoreAsync(object target, string methodName, object?[] args)
    {
        var method = typeof(InvoicesController).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(target, args));
        return Task.FromResult(task);
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

    private static InvoicesController CreateController(
        PaymentContext db,
        bool isAdmin,
        int? userId,
        IEmailService? emailService = null,
        IInvoiceDocumentPdfService? pdfService = null)
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

        var controller = new InvoicesController(
            db,
            emailService ?? new NoopEmailService(),
            new NoopFormRequirementsService(),
            pdfService);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
            },
        };

        return controller;
    }

    private static async Task<int> CreateUserAsync(PaymentContext db, string fullName)
    {
        var role = await db.Roles.FirstOrDefaultAsync(item => item.Name == "user");
        if (role is null)
        {
            role = new Role
            {
                Name = "user",
                Description = "user",
                CreatedAt = DateTime.UtcNow,
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
        }

        var user = new User
        {
            Email = $"internal-invoice-{Guid.NewGuid():N}@example.com",
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

    private static async Task<int> CreateClientAsync(PaymentContext db, string name, int? responsibleUserId)
    {
        var client = new Client
        {
            Name = name,
            IsActive = true,
            ResponsibleUserId = responsibleUserId,
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
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
            Amount = 1_000m,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            Description = "Internal active invoice",
        });

        await db.SaveChangesAsync();
    }

    private static object? GetTupleField(object tuple, string fieldName)
    {
        var field = tuple.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(field);
        return field!.GetValue(tuple);
    }

    private static Payment CreateInvoiceDocumentPayment(string number, DateTime date, decimal amount, string clientName, string? clientEmail = null)
    {
        return new Payment
        {
            Account = number,
            AccountDate = date,
            Amount = amount,
            Date = date,
            PlannedDate = date,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            Client = new Client
            {
                Name = clientName,
                Email = clientEmail ?? $"{Guid.NewGuid():N}@example.com",
                IsActive = true,
            },
            Invoice = new Invoice
            {
                Number = number,
                Date = date,
            },
        };
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

    private sealed class RecordingEmailService : IEmailService
    {
        public RecordedEmail? LastMessage { get; private set; }

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
            string attachmentContentType)
        {
            LastMessage = new RecordedEmail(toEmail, subject, body, attachmentFileName, attachmentBytes, attachmentContentType);
            return Task.CompletedTask;
        }
    }

    private sealed record RecordedEmail(
        string ToEmail,
        string Subject,
        string Body,
        string FileName,
        byte[] Bytes,
        string ContentType);

    private sealed class FakeInvoiceDocumentPdfService : IInvoiceDocumentPdfService
    {
        private readonly byte[] _bytes;

        public FakeInvoiceDocumentPdfService(byte[] bytes)
        {
            _bytes = bytes;
        }

        public Task<byte[]> RenderAsync(string html, CancellationToken ct = default)
            => Task.FromResult(_bytes);
    }
}
