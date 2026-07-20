using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalEntities;
using Xunit;
using ClientsV1Controller = PayPlanner.Api.Controllers.V1.ClientsV1Controller;

namespace PayPlanner.Api.Tests;

public class ClientsControllerTests
{
    private const string ClientProfileNotesPrefix = "__PP_CLIENT_PROFILE_V2__:";

    [Fact]
    public async Task V2Get_ReturnsPagedSearchResults_WithContactCounters()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var userId = await CreateUserAsync(db, "admin");
        var legalEntityId = await CreateLegalEntityAsync(db, "Alpha Legal");

        var target = new Client
        {
            Name = "Alpha Client",
            Email = "alpha@example.com",
            Phone = "+70000000001",
            Company = "Alpha",
            Address = "Alpha Street",
            LegalEntityId = legalEntityId,
            IsActive = true,
            ResponsibleUserId = userId,
        };
        var other = new Client
        {
            Name = "Beta Client",
            Email = "beta@example.com",
            Phone = "+70000000002",
            Company = "Beta",
            Address = "Beta Street",
            IsActive = true,
            ResponsibleUserId = userId,
        };

        db.Clients.AddRange(target, other);
        await db.SaveChangesAsync();

        db.ClientCases.AddRange(
            new ClientCase { ClientId = target.Id, Title = "Case 1", Description = "Case preview details" },
            new ClientCase { ClientId = target.Id, Title = "Case 2" });

        var contract = new Contract
        {
            Number = "C-1",
            Date = DateTime.UtcNow.Date,
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        db.ClientContracts.Add(new ClientContract { ClientId = target.Id, ContractId = contract.Id });

        db.Acts.Add(new Act
        {
            ClientId = target.Id,
            Number = "A-1",
            Date = DateTime.UtcNow.Date,
            Amount = 500m,
        });

        db.Payments.AddRange(
            new Payment
            {
                ClientId = target.Id,
                DocumentKind = DocumentKind.Invoice,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Date = DateTime.UtcNow.Date,
                Amount = 1000m,
            },
            new Payment
            {
                ExpenseClientId = target.Id,
                DocumentKind = DocumentKind.Invoice,
                Type = PaymentType.Expense,
                Status = PaymentStatus.Pending,
                Date = DateTime.UtcNow.Date,
                Amount = 2000m,
            },
            new Payment
            {
                ClientId = target.Id,
                DocumentKind = DocumentKind.Payment,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Date = DateTime.UtcNow.Date,
                Amount = 3000m,
            });
        await db.SaveChangesAsync();

        var controller = CreateV2Controller(db, userId);
        interceptor.Commands.Clear();

        var action = await controller.Get(
            search: "alpha",
            isActive: null,
            id: null,
            name: null,
            email: null,
            phone: null,
            company: null,
            address: null,
            notes: null,
            legalEntityId: null,
            legalEntityShortName: null,
            legalEntityFullName: null,
            legalEntityInn: null,
            legalEntityOgrn: null,
            legalEntityKpp: null,
            clientStatusId: null,
            clientStatus: null,
            sortBy: "name",
            sortDir: "asc",
            page: 1,
            pageSize: 1,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var items = Assert.IsAssignableFrom<IEnumerable<Client>>(ok.Value!.GetType().GetProperty("items")!.GetValue(ok.Value)).ToList();
        var total = Assert.IsType<int>(ok.Value.GetType().GetProperty("total")!.GetValue(ok.Value));
        var item = Assert.Single(items);

        Assert.Equal(1, total);
        Assert.Equal(target.Id, item.Id);
        Assert.Equal(2, item.CasesCount);
        Assert.Equal(2, item.Cases.Count);
        Assert.Contains(item.Cases, clientCase =>
            clientCase.Title == "Case 1" && clientCase.Description == "Case preview details");
        Assert.Equal(1, item.ContractsCount);
        Assert.Equal(2, item.InvoicesCount);
        Assert.Equal(1, item.ActsCount);
        Assert.Equal(1, item.PaymentsCount);
        Assert.Equal(2, item.RelatedCases.Count);
        Assert.Single(item.RelatedContracts);
        Assert.Equal(2, item.RelatedInvoices.Count);
        Assert.Single(item.RelatedActs);
        Assert.Single(item.RelatedPayments);
        Assert.Contains(item.RelatedContracts, link => link.Label == "№C-1");
        Assert.Contains(item.RelatedActs, link => link.Label == "№A-1" && link.Amount == 500m);
        Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"ClientCases\"", StringComparison.Ordinal));
        Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"ClientContracts\"", StringComparison.Ordinal));
        Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"Acts\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task V2Get_HidesFinancialRelatedDocuments_WhenSectionViewAccessDenied()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var userId = await CreateUserAsync(db);
        var roleId = await db.Users
            .Where(user => user.Id == userId)
            .Select(user => user.RoleId)
            .FirstAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Clients,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Accounts,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Acts,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Cases,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Contracts,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Payments,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            });

        var client = new Client
        {
            Name = "Restricted Client",
            IsActive = true,
            ResponsibleUserId = userId,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var contract = new Contract
        {
            Number = "C-SEC",
            Date = DateTime.UtcNow.Date,
            BillingMode = ContractBillingMode.Fixed,
            Amount = 3000m,
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        db.ClientContracts.Add(new ClientContract
        {
            ClientId = client.Id,
            ContractId = contract.Id,
        });
        db.ClientCases.Add(new ClientCase
        {
            ClientId = client.Id,
            Title = "Hidden case",
            Status = ClientCaseStatus.Open,
        });
        db.Acts.Add(new Act
        {
            ClientId = client.Id,
            Number = "A-SEC",
            Date = DateTime.UtcNow.Date,
            Amount = 500m,
        });
        db.Payments.AddRange(
            new Payment
            {
                ClientId = client.Id,
                DocumentKind = DocumentKind.Invoice,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Date = DateTime.UtcNow.Date,
                Amount = 1000m,
            },
            new Payment
            {
                ClientId = client.Id,
                DocumentKind = DocumentKind.Payment,
                Type = PaymentType.Income,
                Status = PaymentStatus.Completed,
                Date = DateTime.UtcNow.Date,
                Amount = 2000m,
            });
        await db.SaveChangesAsync();

        var controller = CreateV2Controller(db, userId);
        interceptor.Commands.Clear();

        var action = await controller.Get(
            search: "Restricted",
            isActive: null,
            id: null,
            name: null,
            email: null,
            phone: null,
            company: null,
            address: null,
            notes: null,
            legalEntityId: null,
            legalEntityShortName: null,
            legalEntityFullName: null,
            legalEntityInn: null,
            legalEntityOgrn: null,
            legalEntityKpp: null,
            clientStatusId: null,
            clientStatus: null,
            sortBy: "name",
            sortDir: "asc",
            page: 1,
            pageSize: 10,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var items = Assert.IsAssignableFrom<IEnumerable<Client>>(ok.Value!.GetType().GetProperty("items")!.GetValue(ok.Value)).ToList();
        var item = Assert.Single(items);

        Assert.Equal(client.Id, item.Id);
        Assert.Equal(0, item.CasesCount);
        Assert.Equal(0, item.ContractsCount);
        Assert.Equal(0, item.InvoicesCount);
        Assert.Equal(0, item.ActsCount);
        Assert.Equal(0, item.PaymentsCount);
        Assert.Empty(item.Cases);
        Assert.Empty(item.RelatedCases);
        Assert.Empty(item.RelatedContracts);
        Assert.Empty(item.RelatedInvoices);
        Assert.Empty(item.RelatedActs);
        Assert.Empty(item.RelatedPayments);
        Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"RolePermissions\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task V2GetById_HidesCases_WhenCasesViewAccessDenied()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        var roleId = await db.Users
            .Where(user => user.Id == userId)
            .Select(user => user.RoleId)
            .SingleAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Clients,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Cases,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            });

        var client = new Client
        {
            Name = "Client details with hidden cases",
            IsActive = true,
            ResponsibleUserId = userId,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        db.ClientCases.Add(new ClientCase
        {
            ClientId = client.Id,
            Title = "Hidden details case",
            Status = ClientCaseStatus.Open,
        });
        await db.SaveChangesAsync();

        var controller = CreateV2Controller(db, userId);

        var action = await controller.GetById(client.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var result = Assert.IsType<Client>(ok.Value);

        Assert.Equal(client.Id, result.Id);
        Assert.Empty(result.Cases);
    }

    [Fact]
    public async Task LegacyGetAndGetById_HideCases_WhenCasesViewAccessDenied()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        var roleId = await db.Users
            .Where(user => user.Id == userId)
            .Select(user => user.RoleId)
            .SingleAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Clients,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Cases,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            });

        var client = new Client
        {
            Name = "Legacy client with hidden cases",
            IsActive = true,
            ResponsibleUserId = userId,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        db.ClientCases.Add(new ClientCase
        {
            ClientId = client.Id,
            Title = "Hidden legacy case",
            Status = ClientCaseStatus.Open,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId);

        var listAction = await controller.GetAll(CancellationToken.None);
        var detailsAction = await controller.GetById(client.Id, CancellationToken.None);

        var listOk = Assert.IsType<OkObjectResult>(listAction);
        var listItem = Assert.Single(Assert.IsAssignableFrom<IEnumerable<Client>>(listOk.Value));
        var detailsOk = Assert.IsType<OkObjectResult>(detailsAction);
        var details = Assert.IsType<Client>(detailsOk.Value);

        Assert.Equal(client.Id, listItem.Id);
        Assert.Empty(listItem.Cases);
        Assert.Equal(client.Id, details.Id);
        Assert.Empty(details.Cases);
    }

    [Fact]
    public async Task V1GetById_HidesCases_WhenCasesViewAccessDenied()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        await SetClientPermissionAsync(db, userId, canCreate: true, canEdit: true, canDelete: true, canView: true);
        var roleId = await db.Users
            .Where(user => user.Id == userId)
            .Select(user => user.RoleId)
            .SingleAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = roleId,
            Section = RolePermissionCatalog.Cases,
            CanView = false,
            ViewScope = DataVisibilityScope.All,
        });

        var client = new Client
        {
            Name = "V1 client with hidden cases",
            IsActive = true,
            ResponsibleUserId = userId,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        db.ClientCases.Add(new ClientCase
        {
            ClientId = client.Id,
            Title = "Hidden V1 case",
            Status = ClientCaseStatus.Open,
        });
        await db.SaveChangesAsync();

        var action = await CreateV1Controller(db, userId).GetById(client.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var result = Assert.IsType<Client>(ok.Value);

        Assert.Equal(client.Id, result.Id);
        Assert.Empty(result.Cases);
    }

    [Fact]
    public async Task V2WriteEndpoints_RequireClientActionPermissions()
    {
        await using var db = await CreateContextAsync();
        var noCreateUserId = await CreateUserAsync(db, "client-no-create");
        var noEditUserId = await CreateUserAsync(db, "client-no-edit");
        var noDeleteUserId = await CreateUserAsync(db, "client-no-delete");
        await SetClientPermissionAsync(db, noCreateUserId, canCreate: false, canEdit: true, canDelete: true);
        await SetClientPermissionAsync(db, noEditUserId, canCreate: true, canEdit: false, canDelete: true);
        await SetClientPermissionAsync(db, noDeleteUserId, canCreate: true, canEdit: true, canDelete: false);
        var editClient = new Client
        {
            Name = "Клиент без редактирования",
            IsActive = true,
            ResponsibleUserId = noEditUserId,
        };
        var deleteClient = new Client
        {
            Name = "Клиент без удаления",
            IsActive = true,
            ResponsibleUserId = noDeleteUserId,
        };
        db.Clients.AddRange(editClient, deleteClient);
        await db.SaveChangesAsync();

        var createResult = await CreateV2Controller(db, noCreateUserId).Create(new Client
        {
            Name = "Запрещенное создание клиента",
            IsActive = true,
        }, CancellationToken.None);
        var updateResult = await CreateV2Controller(db, noEditUserId).Update(editClient.Id, new Client
        {
            Name = "Запрещенное изменение клиента",
            IsActive = false,
        }, CancellationToken.None);
        var deleteResult = await CreateV2Controller(db, noDeleteUserId).Delete(deleteClient.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(createResult);
        Assert.IsType<ForbidResult>(updateResult);
        Assert.IsType<ForbidResult>(deleteResult);
        Assert.False(await db.Clients.AnyAsync(item => item.Name == "Запрещенное создание клиента"));

        await db.Entry(editClient).ReloadAsync();
        Assert.Equal("Клиент без редактирования", editClient.Name);
        Assert.True(editClient.IsActive);
        Assert.True(await db.Clients.AnyAsync(item => item.Id == deleteClient.Id));
    }

    [Fact]
    public async Task LegacyWriteEndpoints_RequireClientActionPermissions()
    {
        await using var db = await CreateContextAsync();
        var noCreateUserId = await CreateUserAsync(db, "legacy-client-no-create");
        var noEditUserId = await CreateUserAsync(db, "legacy-client-no-edit");
        var noDeleteUserId = await CreateUserAsync(db, "legacy-client-no-delete");
        await SetClientPermissionAsync(db, noCreateUserId, canCreate: false, canEdit: true, canDelete: true);
        await SetClientPermissionAsync(db, noEditUserId, canCreate: true, canEdit: false, canDelete: true);
        await SetClientPermissionAsync(db, noDeleteUserId, canCreate: true, canEdit: true, canDelete: false);
        var editClient = new Client
        {
            Name = "Legacy клиент без редактирования",
            IsActive = true,
            ResponsibleUserId = noEditUserId,
        };
        var deleteClient = new Client
        {
            Name = "Legacy клиент без удаления",
            IsActive = true,
            ResponsibleUserId = noDeleteUserId,
        };
        db.Clients.AddRange(editClient, deleteClient);
        await db.SaveChangesAsync();

        var createResult = await CreateController(db, noCreateUserId).Create(new Client
        {
            Name = "Запрещенное legacy создание клиента",
            IsActive = true,
        }, CancellationToken.None);
        var updateResult = await CreateController(db, noEditUserId).Update(editClient.Id, new Client
        {
            Name = "Запрещенное legacy изменение клиента",
            IsActive = false,
        }, CancellationToken.None);
        var changeResponsibleResult = await CreateController(db, noEditUserId).ChangeResponsible(new BulkAssignResponsibleRequest
        {
            Ids = [editClient.Id],
            ResponsibleUserId = noCreateUserId,
        }, CancellationToken.None);
        var deleteResult = await CreateController(db, noDeleteUserId).Delete(deleteClient.Id, CancellationToken.None);
        var bulkDeleteResult = await CreateController(db, noDeleteUserId).BulkDelete(new BulkIdsRequest
        {
            Ids = [deleteClient.Id],
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(createResult);
        Assert.IsType<ForbidResult>(updateResult);
        Assert.IsType<ForbidResult>(changeResponsibleResult);
        Assert.IsType<ForbidResult>(deleteResult);
        Assert.IsType<ForbidResult>(bulkDeleteResult);
        Assert.False(await db.Clients.AnyAsync(item => item.Name == "Запрещенное legacy создание клиента"));

        await db.Entry(editClient).ReloadAsync();
        Assert.Equal("Legacy клиент без редактирования", editClient.Name);
        Assert.Equal(noEditUserId, editClient.ResponsibleUserId);
        Assert.True(editClient.IsActive);
        Assert.True(await db.Clients.AnyAsync(item => item.Id == deleteClient.Id));
    }

    [Fact]
    public async Task V1Endpoints_RequireClientSectionPermissions()
    {
        await using var db = await CreateContextAsync();
        var noViewUserId = await CreateUserAsync(db, "v1-client-no-view");
        var noCreateUserId = await CreateUserAsync(db, "v1-client-no-create");
        var noEditUserId = await CreateUserAsync(db, "v1-client-no-edit");
        var noDeleteUserId = await CreateUserAsync(db, "v1-client-no-delete");
        await SetClientPermissionAsync(db, noViewUserId, canCreate: true, canEdit: true, canDelete: true, canView: false);
        await SetClientPermissionAsync(db, noCreateUserId, canCreate: false, canEdit: true, canDelete: true);
        await SetClientPermissionAsync(db, noEditUserId, canCreate: true, canEdit: false, canDelete: true);
        await SetClientPermissionAsync(db, noDeleteUserId, canCreate: true, canEdit: true, canDelete: false);
        var viewClient = new Client
        {
            Name = "V1 клиент без просмотра",
            IsActive = true,
            ResponsibleUserId = noViewUserId,
        };
        var editClient = new Client
        {
            Name = "V1 клиент без редактирования",
            IsActive = true,
            ResponsibleUserId = noEditUserId,
        };
        var deleteClient = new Client
        {
            Name = "V1 клиент без удаления",
            IsActive = true,
            ResponsibleUserId = noDeleteUserId,
        };
        db.Clients.AddRange(viewClient, editClient, deleteClient);
        await db.SaveChangesAsync();

        var listResult = await CreateV1Controller(db, noViewUserId).Get(
            search: null,
            isActive: null,
            sortBy: null,
            sortDir: null,
            limit: null,
            ct: CancellationToken.None);
        var detailsResult = await CreateV1Controller(db, noViewUserId).GetById(viewClient.Id, CancellationToken.None);
        var createResult = await CreateV1Controller(db, noCreateUserId).Create(new Client
        {
            Name = "Запрещенное V1 создание клиента",
            IsActive = true,
        }, CancellationToken.None);
        var updateResult = await CreateV1Controller(db, noEditUserId).Update(editClient.Id, new Client
        {
            Name = "Запрещенное V1 изменение клиента",
            IsActive = false,
        }, CancellationToken.None);
        var deleteResult = await CreateV1Controller(db, noDeleteUserId).Delete(deleteClient.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(listResult);
        Assert.IsType<ForbidResult>(detailsResult);
        Assert.IsType<ForbidResult>(createResult);
        Assert.IsType<ForbidResult>(updateResult);
        Assert.IsType<ForbidResult>(deleteResult);
        Assert.False(await db.Clients.AnyAsync(item => item.Name == "Запрещенное V1 создание клиента"));

        await db.Entry(editClient).ReloadAsync();
        Assert.Equal("V1 клиент без редактирования", editClient.Name);
        Assert.True(editClient.IsActive);
        Assert.True(await db.Clients.AnyAsync(item => item.Id == deleteClient.Id));
    }

    [Fact]
    public async Task V2Get_DeduplicatesRelatedInvoices_WithSameVisibleNumber()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, "admin");

        var client = new Client
        {
            Name = "Zemtsov Test",
            IsActive = true,
            ResponsibleUserId = userId,
        };
        var invoice = new Invoice
        {
            Number = "8",
            Date = new DateTime(2026, 2, 2),
        };
        db.Clients.Add(client);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var canonicalInvoiceRow = new Payment
        {
            ClientId = client.Id,
            InvoiceId = invoice.Id,
            DocumentKind = DocumentKind.Invoice,
            Account = "8",
            AccountDate = invoice.Date,
            Date = new DateTime(2026, 2, 5),
            PlannedDate = new DateTime(2026, 2, 5),
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            Amount = 25000m,
        };

        db.Payments.AddRange(
            canonicalInvoiceRow,
            new Payment
            {
                ClientId = client.Id,
                DocumentKind = DocumentKind.Invoice,
                Account = "8",
                AccountDate = invoice.Date,
                Date = new DateTime(2026, 2, 5),
                PlannedDate = new DateTime(2026, 2, 5),
                Type = PaymentType.Income,
                Status = PaymentStatus.Cancelled,
                Amount = 100m,
            },
            new Payment
            {
                ClientId = client.Id,
                DocumentKind = DocumentKind.Invoice,
                LegacyAccountNumber = "8",
                LegacyAccountDate = invoice.Date,
                Date = new DateTime(2026, 2, 5),
                PlannedDate = new DateTime(2026, 2, 5),
                Type = PaymentType.Income,
                Status = PaymentStatus.Cancelled,
                Amount = 25000m,
            });
        await db.SaveChangesAsync();

        var controller = CreateV2Controller(db, userId);

        var action = await controller.Get(
            search: "Zemtsov",
            isActive: null,
            id: null,
            name: null,
            email: null,
            phone: null,
            company: null,
            address: null,
            notes: null,
            legalEntityId: null,
            legalEntityShortName: null,
            legalEntityFullName: null,
            legalEntityInn: null,
            legalEntityOgrn: null,
            legalEntityKpp: null,
            clientStatusId: null,
            clientStatus: null,
            sortBy: "name",
            sortDir: "asc",
            page: 1,
            pageSize: 25,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var items = Assert.IsAssignableFrom<IEnumerable<Client>>(ok.Value!.GetType().GetProperty("items")!.GetValue(ok.Value)).ToList();
        var item = Assert.Single(items);
        var relatedInvoice = Assert.Single(item.RelatedInvoices);

        Assert.Equal(1, item.InvoicesCount);
        Assert.Equal(canonicalInvoiceRow.Id, relatedInvoice.Id);
        Assert.Contains("8", relatedInvoice.Label);
    }

    [Fact]
    public async Task GetAll_ReturnsTooManyRequests_WhenFullClientListIsRequestedTooOften()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(db, userId);
        controller.Request.Headers.UserAgent = $"PayPlannerTest/{Guid.NewGuid():N}";
        controller.Request.Headers["X-Forwarded-For"] = $"203.0.113.{Random.Shared.Next(1, 200)}";

        for (var i = 0; i < 60; i += 1)
        {
            var action = await controller.GetAll(CancellationToken.None);
            Assert.IsType<OkObjectResult>(action);
        }

        var limited = await controller.GetAll(CancellationToken.None);
        for (var i = 0; i < 60 && limited is OkObjectResult; i += 1)
        {
            limited = await controller.GetAll(CancellationToken.None);
        }

        var result = Assert.IsType<ObjectResult>(limited);

        Assert.Equal(StatusCodes.Status429TooManyRequests, result.StatusCode);
        Assert.True(controller.Response.Headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public async Task Create_RoundTripsStructuredAddressEntries_WithFullAddress()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        var controller = CreateController(db, userId);

        const string fullAddress = "633208, Россия, Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19";
        var model = new Client
        {
            Name = "Тестовый клиент",
            Address = fullAddress,
            Notes = ClientProfileNotesPrefix + JsonSerializer.Serialize(new
            {
                addressEntries = new[]
                {
                    new
                    {
                        type = "registration",
                        sourceQuery = fullAddress,
                        fullAddress,
                        postalCode = "633208",
                        country = "Россия",
                        region = "Новосибирская обл",
                        district = "Искитимский р-н",
                        city = "г Искитим",
                        settlement = "Южный мкр",
                        street = "Южный мкр",
                        house = "37",
                        flat = "19",
                        fiasId = "fias-iskitim",
                        kladrId = "kladr-iskitim",
                        geoLat = "54.6265",
                        geoLon = "83.3045",
                    },
                },
            }),
            IsActive = true,
        };

        var action = await controller.Create(model, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action);
        var dto = Assert.IsType<Client>(created.Value);
        var address = Assert.Single(dto.Addresses);

        Assert.Equal(fullAddress, address.UnrestrictedValue);
        Assert.Equal("Россия", address.Country);
        Assert.Equal("Новосибирская обл", address.RegionWithType);
        Assert.Equal("Искитимский р-н", address.AreaWithType);
        Assert.Equal("г Искитим", address.CityWithType);
        Assert.Equal("Южный мкр", address.SettlementWithType);
        Assert.Equal("Южный мкр", address.StreetWithType);
        Assert.Equal("37", address.House);
        Assert.Equal("19", address.Flat);

        var stored = await db.AddressRecords.SingleAsync(item => item.ClientId == dto.Id);
        Assert.Equal(fullAddress, stored.UnrestrictedValue);
        Assert.Equal("Россия", stored.Country);
        Assert.Equal("Новосибирская обл", stored.RegionWithType);
        Assert.Equal("Искитимский р-н", stored.AreaWithType);
        Assert.Equal("г Искитим", stored.CityWithType);
        Assert.Equal("Южный мкр", stored.SettlementWithType);
        Assert.Equal("Южный мкр", stored.StreetWithType);
        Assert.Equal("37", stored.House);
        Assert.Equal("19", stored.Flat);
    }

    [Fact]
    public async Task Create_UsesExplicitPrimaryStructuredAddress_ForLegacyAddressAndOrdering()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        var controller = CreateController(db, userId);

        var model = new Client
        {
            Name = "Primary Address Client",
            Address = "Fallback address",
            Notes = ClientProfileNotesPrefix + JsonSerializer.Serialize(new
            {
                addressEntries = new[]
                {
                    new
                    {
                        type = "registration",
                        fullAddress = "First structured address",
                        isPrimary = false,
                    },
                    new
                    {
                        type = "actual",
                        fullAddress = "Second structured address",
                        isPrimary = true,
                    },
                },
            }),
            IsActive = true,
        };

        var action = await controller.Create(model, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action);
        var dto = Assert.IsType<Client>(created.Value);

        Assert.Equal("Second structured address", dto.Address);
        Assert.Equal(2, dto.Addresses.Count);
        Assert.Equal("Second structured address", dto.Addresses.First().UnrestrictedValue);
        Assert.True(dto.Addresses.First().IsPrimary);

        var storedAddresses = await db.AddressRecords
            .AsNoTracking()
            .Where(item => item.ClientId == dto.Id)
            .OrderByDescending(item => item.IsPrimary)
            .ThenBy(item => item.CreatedAt)
            .ToListAsync();

        Assert.Equal(2, storedAddresses.Count);
        Assert.Equal("Second structured address", storedAddresses[0].UnrestrictedValue);
        Assert.True(storedAddresses[0].IsPrimary);
        Assert.False(storedAddresses[1].IsPrimary);
    }

    [Fact]
    public async Task GetStats_IncludesPaymentsLinkedThroughInvoiceCases_ForSelectedCase()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(db, userId);

        var client = new Client
        {
            Name = "Тестовый клиент",
            ResponsibleUserId = userId,
            IsActive = true,
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело по счету",
            Status = ClientCaseStatus.Open,
        };

        var invoice = new Invoice
        {
            Number = "30",
            Date = new DateTime(2026, 4, 8),
        };

        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var linkedPayment = new Payment
        {
            Amount = 18_000m,
            PaidAmount = 9_000m,
            CounterpartyName = "Тестовый клиент",
            Date = new DateTime(2026, 4, 9),
            PlannedDate = new DateTime(2026, 4, 9),
            Description = "Рассрочка по счету №30",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Pending,
            IsPaid = false,
            Type = PaymentType.Income,
            ClientId = client.Id,
            ClientCaseId = null,
            InvoiceId = invoice.Id,
            Account = "30",
            AccountDate = invoice.Date,
            Notes = string.Empty,
            SystemNotes = string.Empty,
        };

        db.Payments.Add(linkedPayment);
        await db.SaveChangesAsync();

        db.InvoiceClientCases.Add(new InvoiceClientCase
        {
            PaymentId = linkedPayment.Id,
            ClientCaseId = clientCase.Id,
        });
        await db.SaveChangesAsync();

        var action = await controller.GetStats(client.Id, clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var stats = Assert.IsType<ClientStats>(ok.Value);

        Assert.Equal(client.Id, stats.ClientId);
        Assert.Equal(9_000m, stats.TotalIncome);
        Assert.Equal(9_000m, stats.OutstandingIncome);
        Assert.Equal(0m, stats.TotalExpenses);
        Assert.Equal(1, stats.TotalPayments);
        Assert.Equal(0, stats.PaidPayments);
        Assert.Equal(1, stats.PendingPayments);
        Assert.Equal(1, stats.OverduePayments);
        Assert.Collection(
            stats.RecentPayments,
            payment => Assert.Equal(linkedPayment.Id, payment.Id));
    }

    [Fact]
    public async Task GetStats_HidesSelectedCase_WhenCasesViewAccessDenied()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        var roleId = await db.Users
            .Where(user => user.Id == userId)
            .Select(user => user.RoleId)
            .SingleAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Clients,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Cases,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Payments,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            });

        var client = new Client
        {
            Name = "Hidden Stats Client",
            ResponsibleUserId = userId,
            IsActive = true,
        };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Hidden stats case",
            Status = ClientCaseStatus.Open,
        };

        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        db.Payments.Add(new Payment
        {
            ClientId = client.Id,
            ClientCaseId = clientCase.Id,
            Amount = 10_000m,
            PaidAmount = 10_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 6, 24),
            PlannedDate = new DateTime(2026, 6, 24),
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            Notes = string.Empty,
            SystemNotes = string.Empty,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId);

        var action = await controller.GetStats(client.Id, clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var stats = Assert.IsType<ClientStats>(ok.Value);

        Assert.Equal(client.Id, stats.ClientId);
        Assert.Equal(0m, stats.TotalIncome);
        Assert.Equal(0m, stats.OutstandingIncome);
        Assert.Equal(0m, stats.TotalExpenses);
        Assert.Equal(0m, stats.OutstandingExpenses);
        Assert.Equal(0, stats.TotalPayments);
        Assert.Empty(stats.RecentPayments);
    }

    [Fact]
    public async Task GetStats_CountsPendingDebtAsOverdueByProfileTimeZone()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var (timeZoneId, profileToday) = ResolveTimeZoneWithDifferentLocalDate();

        db.UserSettings.Add(new UserSetting
        {
            UserId = userId,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = timeZoneId,
        });

        var client = new Client
        {
            Name = "Timezone Debt Client",
            ResponsibleUserId = userId,
            IsActive = true,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var dueDate = profileToday.AddDays(-1);
        db.Payments.Add(new Payment
        {
            Amount = 12_000m,
            PaidAmount = 0m,
            CounterpartyName = client.Name,
            Date = dueDate,
            PlannedDate = dueDate,
            Description = "Pending debt before profile today",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Pending,
            IsPaid = false,
            Type = PaymentType.Income,
            ClientId = client.Id,
            Notes = string.Empty,
            SystemNotes = string.Empty,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId);
        var action = await controller.GetStats(client.Id, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var stats = Assert.IsType<ClientStats>(ok.Value);

        Assert.Equal(12_000m, stats.OutstandingIncome);
        Assert.Equal(1, stats.PendingPayments);
        Assert.Equal(1, stats.OverduePayments);
    }

    [Fact]
    public async Task GetStats_HidesRepresentativeInvoice_WhenInstallmentsExist()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(db, userId);

        var client = new Client
        {
            Name = "Клиент с рассрочкой",
            ResponsibleUserId = userId,
            IsActive = true,
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело по рассрочке",
            Status = ClientCaseStatus.Open,
        };

        var invoice = new Invoice
        {
            Number = "155",
            Date = new DateTime(2025, 10, 24),
        };

        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        db.Payments.AddRange(
            new Payment
            {
                Amount = 205_000m,
                PaidAmount = 0m,
                CounterpartyName = client.Name,
                Date = new DateTime(2026, 8, 25),
                PlannedDate = new DateTime(2026, 8, 25),
                Description = "Представительский счёт",
                DocumentKind = DocumentKind.Invoice,
                Status = PaymentStatus.Pending,
                IsPaid = false,
                Type = PaymentType.Income,
                ClientId = client.Id,
                ClientCaseId = clientCase.Id,
                InvoiceId = invoice.Id,
                Account = invoice.Number,
                AccountDate = invoice.Date,
                Notes = string.Empty,
                SystemNotes = string.Empty,
            },
            new Payment
            {
                Amount = 115_000m,
                PaidAmount = 115_000m,
                CounterpartyName = client.Name,
                Date = new DateTime(2026, 3, 25),
                PlannedDate = new DateTime(2026, 3, 25),
                Description = "Первый платёж по рассрочке",
                DocumentKind = DocumentKind.Payment,
                Status = PaymentStatus.Completed,
                IsPaid = true,
                Type = PaymentType.Income,
                ClientId = client.Id,
                ClientCaseId = clientCase.Id,
                InvoiceId = invoice.Id,
                Account = invoice.Number,
                AccountDate = invoice.Date,
                Notes = string.Empty,
                SystemNotes = InvoiceManagedPaymentRules.InstallmentPaymentMarker,
            },
            new Payment
            {
                Amount = 90_000m,
                PaidAmount = 0m,
                CounterpartyName = client.Name,
                Date = new DateTime(2026, 4, 25),
                PlannedDate = new DateTime(2026, 4, 25),
                Description = "Остаток по рассрочке",
                DocumentKind = DocumentKind.Payment,
                Status = PaymentStatus.Pending,
                IsPaid = false,
                Type = PaymentType.Income,
                ClientId = client.Id,
                ClientCaseId = clientCase.Id,
                InvoiceId = invoice.Id,
                Account = invoice.Number,
                AccountDate = invoice.Date,
                Notes = string.Empty,
                SystemNotes = InvoiceManagedPaymentRules.InstallmentPaymentMarker,
            });
        await db.SaveChangesAsync();

        var action = await controller.GetStats(client.Id, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var stats = Assert.IsType<ClientStats>(ok.Value);

        Assert.Equal(115_000m, stats.TotalIncome);
        Assert.Equal(90_000m, stats.OutstandingIncome);
        Assert.Equal(2, stats.TotalPayments);
        Assert.Equal(1, stats.PaidPayments);
        Assert.Equal(1, stats.PendingPayments);
        Assert.DoesNotContain(stats.RecentPayments, payment => payment.DocumentKind == DocumentKind.Invoice);
    }

    [Fact]
    public async Task GetStats_FiltersPaymentsByClientRole()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(db, userId);

        var client = new Client { Name = "Matter Client", ResponsibleUserId = userId, IsActive = true };
        var supplier = new Client { Name = "Supplier", ResponsibleUserId = userId, IsActive = true };
        db.Clients.AddRange(client, supplier);
        await db.SaveChangesAsync();

        db.Payments.AddRange(
            new Payment
            {
                Amount = 10_000m,
                PaidAmount = 10_000m,
                CounterpartyName = client.Name,
                Date = new DateTime(2026, 4, 10),
                PlannedDate = new DateTime(2026, 4, 10),
                Description = "Client income",
                DocumentKind = DocumentKind.Payment,
                Status = PaymentStatus.Completed,
                IsPaid = true,
                Type = PaymentType.Income,
                ClientId = client.Id,
                Notes = string.Empty,
                SystemNotes = string.Empty,
            },
            new Payment
            {
                Amount = 4_000m,
                PaidAmount = 1_000m,
                CounterpartyName = supplier.Name,
                Date = new DateTime(2026, 4, 11),
                PlannedDate = new DateTime(2026, 4, 11),
                Description = "Supplier expense for client",
                DocumentKind = DocumentKind.Payment,
                Status = PaymentStatus.Pending,
                Type = PaymentType.Expense,
                ClientId = supplier.Id,
                ExpenseClientId = client.Id,
                Notes = string.Empty,
                SystemNotes = string.Empty,
            });
        await db.SaveChangesAsync();

        var action = await controller.GetStats(
            client.Id,
            caseId: null,
            ct: CancellationToken.None,
            clientRole: "expenseClient");

        var ok = Assert.IsType<OkObjectResult>(action);
        var stats = Assert.IsType<ClientStats>(ok.Value);

        Assert.Equal(0m, stats.TotalIncome);
        Assert.Equal(1_000m, stats.TotalExpenses);
        Assert.Equal(3_000m, stats.OutstandingExpenses);
        Assert.Single(stats.RecentPayments);
    }

    [Fact]
    public async Task GetStats_DeniesFinancialStats_WhenPaymentsViewAccessDenied()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        var roleId = await db.Users
            .Where(user => user.Id == userId)
            .Select(user => user.RoleId)
            .FirstAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Clients,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Payments,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            });

        var client = new Client
        {
            Name = "Stats Restricted",
            ResponsibleUserId = userId,
            IsActive = true,
        };
        db.Clients.Add(client);
        db.Payments.Add(new Payment
        {
            Client = client,
            Amount = 10_000m,
            PaidAmount = 10_000m,
            Date = DateTime.UtcNow.Date,
            PlannedDate = DateTime.UtcNow.Date,
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Completed,
            Type = PaymentType.Income,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId);

        var action = await controller.GetStats(client.Id, null, CancellationToken.None);

        Assert.IsType<ForbidResult>(action);
    }

    [Fact]
    public async Task GetCaseFinanceSummaries_AggregatesPaymentsLinkedThroughInvoiceCases()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(db, userId);

        var client = new Client
        {
            Name = "Тестовый клиент",
            ResponsibleUserId = userId,
            IsActive = true,
        };

        var primaryCase = new ClientCase
        {
            Client = client,
            Title = "Основное дело",
            Status = ClientCaseStatus.Open,
        };

        var secondaryCase = new ClientCase
        {
            Client = client,
            Title = "Второе дело",
            Status = ClientCaseStatus.Open,
        };

        var invoice = new Invoice
        {
            Number = "31",
            Date = new DateTime(2026, 4, 10),
        };

        db.Clients.Add(client);
        db.ClientCases.AddRange(primaryCase, secondaryCase);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var linkedIncomePayment = new Payment
        {
            Amount = 18_000m,
            PaidAmount = 6_000m,
            CounterpartyName = "Тестовый клиент",
            Date = new DateTime(2026, 4, 10),
            PlannedDate = new DateTime(2026, 4, 10),
            Description = "Платеж по счету №31",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Pending,
            IsPaid = false,
            Type = PaymentType.Income,
            ClientId = client.Id,
            ClientCaseId = null,
            InvoiceId = invoice.Id,
            Account = "31",
            AccountDate = invoice.Date,
            Notes = string.Empty,
            SystemNotes = string.Empty,
        };

        var unrelatedExpense = new Payment
        {
            Amount = 4_500m,
            PaidAmount = 4_500m,
            CounterpartyName = "Поставщик",
            Date = new DateTime(2026, 4, 11),
            PlannedDate = new DateTime(2026, 4, 11),
            Description = "Расход по другому делу",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Expense,
            ClientId = client.Id,
            ClientCaseId = secondaryCase.Id,
            Notes = string.Empty,
            SystemNotes = string.Empty,
        };

        db.Payments.AddRange(linkedIncomePayment, unrelatedExpense);
        await db.SaveChangesAsync();

        db.InvoiceClientCases.Add(new InvoiceClientCase
        {
            PaymentId = linkedIncomePayment.Id,
            ClientCaseId = primaryCase.Id,
        });
        await db.SaveChangesAsync();

        var action = await controller.GetCaseFinanceSummaries(client.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var summaries = Assert.IsAssignableFrom<List<ClientCaseFinanceSummaryDto>>(ok.Value);

        var primarySummary = Assert.Single(summaries, item => item.CaseId == primaryCase.Id);
        Assert.Equal(18_000m, primarySummary.IncomeTotal);
        Assert.Equal(6_000m, primarySummary.IncomePaid);
        Assert.Equal(12_000m, primarySummary.IncomeOutstanding);
        Assert.Equal(1, primarySummary.IncomeCount);
        Assert.Equal(0m, primarySummary.ExpenseTotal);

        var secondarySummary = Assert.Single(summaries, item => item.CaseId == secondaryCase.Id);
        Assert.Equal(0m, secondarySummary.IncomeTotal);
        Assert.Equal(4_500m, secondarySummary.ExpenseTotal);
        Assert.Equal(4_500m, secondarySummary.ExpensePaid);
        Assert.Equal(0m, secondarySummary.ExpenseOutstanding);
        Assert.Equal(1, secondarySummary.ExpenseCount);
    }

    [Fact]
    public async Task GetCaseFinanceSummaries_ReturnsSupplierCaseGroups()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(db, userId);

        var supplier = new Client { Name = "Geo Supplier", ResponsibleUserId = userId, IsActive = true };
        var client = new Client { Name = "Matter Client", ResponsibleUserId = userId, IsActive = true };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Cadastral matter",
            Status = ClientCaseStatus.Open,
        };

        db.Clients.AddRange(supplier, client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        db.Payments.Add(new Payment
        {
            Amount = 5_000m,
            PaidAmount = 2_000m,
            CounterpartyName = supplier.Name,
            Date = new DateTime(2026, 4, 12),
            PlannedDate = new DateTime(2026, 4, 12),
            Description = "Geodesic survey",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Expense,
            ClientId = supplier.Id,
            ExpenseClientId = client.Id,
            ClientCaseId = clientCase.Id,
            Notes = string.Empty,
            SystemNotes = string.Empty,
        });
        await db.SaveChangesAsync();

        var action = await controller.GetCaseFinanceSummaries(
            supplier.Id,
            CancellationToken.None,
            clientRole: "counterparty");

        var ok = Assert.IsType<OkObjectResult>(action);
        var summaries = Assert.IsAssignableFrom<List<ClientCaseFinanceSummaryDto>>(ok.Value);
        var summary = Assert.Single(summaries);

        Assert.Equal(clientCase.Id, summary.CaseId);
        Assert.Equal(client.Id, summary.ClientId);
        Assert.Equal(client.Name, summary.ClientName);
        Assert.Equal(clientCase.Title, summary.CaseTitle);
        Assert.Equal(5_000m, summary.ExpenseTotal);
        Assert.Equal(2_000m, summary.ExpensePaid);
        Assert.Equal(3_000m, summary.ExpenseOutstanding);
        Assert.Equal(1, summary.ExpenseCount);
    }

    [Fact]
    public async Task GetCaseFinanceSummaries_HidesCases_WhenCasesViewAccessDenied()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        var roleId = await db.Users
            .Where(user => user.Id == userId)
            .Select(user => user.RoleId)
            .SingleAsync();
        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Clients,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Cases,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Payments,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            });

        var client = new Client { Name = "Hidden Case Client", IsActive = true };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Hidden client case",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.Payments.Add(new Payment
        {
            ClientId = client.Id,
            ClientCaseId = clientCase.Id,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Date = DateTime.UtcNow.Date,
            PlannedDate = DateTime.UtcNow.Date,
            Amount = 1000m,
            PaidAmount = 400m,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId);

        var action = await controller.GetCaseFinanceSummaries(client.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var summaries = Assert.IsAssignableFrom<IEnumerable<ClientCaseFinanceSummaryDto>>(ok.Value);
        Assert.Empty(summaries);
    }

    [Fact]
    public async Task GetCaseFinanceSummaries_HidesPaymentsOutsidePaymentVisibilityScope()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        var roleId = await db.Users
            .Where(user => user.Id == userId)
            .Select(user => user.RoleId)
            .SingleAsync();
        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Clients,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Payments,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            });

        var client = new Client { Name = "Hidden Payment Client", IsActive = true };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Visible client case",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.Payments.Add(new Payment
        {
            ClientId = client.Id,
            ClientCaseId = clientCase.Id,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Date = DateTime.UtcNow.Date,
            PlannedDate = DateTime.UtcNow.Date,
            Amount = 1000m,
            PaidAmount = 400m,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId);

        var action = await controller.GetCaseFinanceSummaries(client.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var summary = Assert.Single(Assert.IsAssignableFrom<IEnumerable<ClientCaseFinanceSummaryDto>>(ok.Value));
        Assert.Equal(clientCase.Id, summary.CaseId);
        Assert.Equal(0m, summary.IncomeTotal);
        Assert.Equal(0m, summary.IncomePaid);
        Assert.Equal(0m, summary.IncomeOutstanding);
        Assert.Equal(0, summary.IncomeCount);
    }

    [Fact]
    public async Task GetCaseFinanceSummaries_SupplierMode_HidesCases_WhenCasesViewAccessDenied()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        var roleId = await db.Users
            .Where(user => user.Id == userId)
            .Select(user => user.RoleId)
            .SingleAsync();
        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Clients,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Cases,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Payments,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            });

        var supplier = new Client { Name = "Hidden Case Supplier", IsActive = true };
        var client = new Client { Name = "Hidden Case Matter Client", IsActive = true };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Supplier hidden case",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.AddRange(supplier, client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.Payments.Add(new Payment
        {
            ClientId = supplier.Id,
            ExpenseClientId = client.Id,
            ClientCaseId = clientCase.Id,
            Type = PaymentType.Expense,
            Status = PaymentStatus.Pending,
            Date = DateTime.UtcNow.Date,
            PlannedDate = DateTime.UtcNow.Date,
            Amount = 5000m,
            PaidAmount = 2000m,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId);

        var action = await controller.GetCaseFinanceSummaries(
            supplier.Id,
            CancellationToken.None,
            clientRole: "counterparty");

        var ok = Assert.IsType<OkObjectResult>(action);
        var summaries = Assert.IsAssignableFrom<IEnumerable<ClientCaseFinanceSummaryDto>>(ok.Value);
        Assert.Empty(summaries);
    }

    [Fact]
    public async Task GetCaseExpenseDetails_ReturnsInvoiceLineBreakdown_ForClientCase()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(db, userId);

        var supplier = new Client { Name = "Geo Supplier", ResponsibleUserId = userId, IsActive = true };
        var client = new Client { Name = "Matter Client", ResponsibleUserId = userId, IsActive = true };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Cadastral matter",
            Status = ClientCaseStatus.Open,
        };
        var invoice = new Invoice
        {
            Number = "EXP-77",
            Date = new DateTime(2026, 4, 20),
        };

        db.Clients.AddRange(supplier, client);
        db.ClientCases.Add(clientCase);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var payment = new Payment
        {
            Amount = 5_000m,
            PaidAmount = 2_000m,
            CounterpartyName = supplier.Name,
            Date = new DateTime(2026, 4, 22),
            PlannedDate = new DateTime(2026, 4, 25),
            Description = "Geodesic survey",
            DocumentKind = DocumentKind.Invoice,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Expense,
            ClientId = supplier.Id,
            ExpenseClientId = client.Id,
            InvoiceId = invoice.Id,
            Account = invoice.Number,
            AccountDate = invoice.Date,
            PaymentOrderNumber = "PP-100",
            PaymentDocumentDate = new DateTime(2026, 4, 23),
            Notes = string.Empty,
            SystemNotes = string.Empty,
        };

        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        db.InvoiceClientCases.Add(new InvoiceClientCase
        {
            PaymentId = payment.Id,
            ClientCaseId = clientCase.Id,
        });
        db.InvoiceLineItems.AddRange(
            new InvoiceLineItem
            {
                PaymentId = payment.Id,
                SortOrder = 1,
                Name = "Survey",
                Quantity = 1m,
                Unit = "шт",
                Price = 3_000m,
            },
            new InvoiceLineItem
            {
                PaymentId = payment.Id,
                SortOrder = 2,
                Name = "Report",
                Quantity = 1m,
                Unit = "шт",
                Price = 2_000m,
            });
        await db.SaveChangesAsync();

        var action = await controller.GetCaseExpenseDetails(
            client.Id,
            caseId: clientCase.Id,
            clientRole: "expenseClient",
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var rows = Assert.IsAssignableFrom<List<ClientCaseExpenseDetailDto>>(ok.Value);

        Assert.Equal(2, rows.Count);
        var survey = Assert.Single(rows, row => row.LineItemName == "Survey");

        Assert.Equal(payment.Id, survey.PaymentId);
        Assert.Equal(invoice.Id, survey.InvoiceId);
        Assert.Equal("EXP-77", survey.InvoiceNumber);
        Assert.Equal(supplier.Id, survey.SupplierId);
        Assert.Equal(supplier.Name, survey.SupplierName);
        Assert.Equal(client.Id, survey.ExpenseClientId);
        Assert.Equal(clientCase.Id, survey.CaseId);
        Assert.Equal(3_000m, survey.Amount);
        Assert.Equal(1_200m, survey.PaidAmount);
        Assert.Equal(1_800m, survey.OutstandingAmount);
        Assert.Equal("PP-100", survey.PaymentOrderNumber);
        Assert.Equal(new DateTime(2026, 4, 23), survey.PaymentDocumentDate);
    }

    [Fact]
    public async Task GetCaseExpenseDetails_HidesSelectedCase_WhenCasesViewAccessDenied()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        var roleId = await db.Users
            .Where(user => user.Id == userId)
            .Select(user => user.RoleId)
            .SingleAsync();
        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Clients,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Cases,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Payments,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            });

        var supplier = new Client { Name = "Hidden Detail Supplier", IsActive = true };
        var client = new Client { Name = "Hidden Detail Client", IsActive = true };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Hidden expense detail case",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.AddRange(supplier, client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.Payments.Add(new Payment
        {
            ClientId = supplier.Id,
            ExpenseClientId = client.Id,
            ClientCaseId = clientCase.Id,
            Type = PaymentType.Expense,
            Status = PaymentStatus.Pending,
            Date = DateTime.UtcNow.Date,
            PlannedDate = DateTime.UtcNow.Date,
            Amount = 5000m,
            PaidAmount = 2000m,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId);

        var action = await controller.GetCaseExpenseDetails(
            client.Id,
            caseId: clientCase.Id,
            clientRole: "expenseClient",
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var rows = Assert.IsAssignableFrom<List<ClientCaseExpenseDetailDto>>(ok.Value);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetCaseExpenseDetails_SupplierMode_HidesCases_WhenCasesViewAccessDenied()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        var roleId = await db.Users
            .Where(user => user.Id == userId)
            .Select(user => user.RoleId)
            .SingleAsync();
        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Clients,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Cases,
                CanView = false,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Payments,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            });

        var supplier = new Client { Name = "Supplier Hidden Detail", IsActive = true };
        var client = new Client { Name = "Matter Hidden Detail", IsActive = true };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Supplier hidden expense case",
            Status = ClientCaseStatus.Open,
        };
        db.Clients.AddRange(supplier, client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        db.Payments.Add(new Payment
        {
            ClientId = supplier.Id,
            ExpenseClientId = client.Id,
            ClientCaseId = clientCase.Id,
            Type = PaymentType.Expense,
            Status = PaymentStatus.Pending,
            Date = DateTime.UtcNow.Date,
            PlannedDate = DateTime.UtcNow.Date,
            Amount = 5000m,
            PaidAmount = 2000m,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, userId);

        var action = await controller.GetCaseExpenseDetails(
            supplier.Id,
            caseId: null,
            clientRole: "counterparty",
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var rows = Assert.IsAssignableFrom<List<ClientCaseExpenseDetailDto>>(ok.Value);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetCaseExpenseDetails_UsesProfileTimeZoneForExpenseStatus()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var (timeZoneId, profileToday) = ResolveTimeZoneWithDifferentLocalDate();

        db.UserSettings.Add(new UserSetting
        {
            UserId = userId,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = timeZoneId,
        });

        var supplier = new Client { Name = "Timezone Supplier", ResponsibleUserId = userId, IsActive = true };
        var client = new Client { Name = "Timezone Matter Client", ResponsibleUserId = userId, IsActive = true };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Timezone expense matter",
            Status = ClientCaseStatus.Open,
        };

        db.Clients.AddRange(supplier, client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var plannedDate = profileToday.AddDays(-1);
        db.Payments.Add(new Payment
        {
            Amount = 4_000m,
            PaidAmount = 0m,
            CounterpartyName = supplier.Name,
            Date = plannedDate,
            PlannedDate = plannedDate,
            Description = "Expense before profile today",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Expense,
            ClientId = supplier.Id,
            ExpenseClientId = client.Id,
            ClientCaseId = clientCase.Id,
            Notes = string.Empty,
            SystemNotes = string.Empty,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, userId);
        var action = await controller.GetCaseExpenseDetails(
            client.Id,
            caseId: clientCase.Id,
            clientRole: "expenseClient",
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var rows = Assert.IsAssignableFrom<List<ClientCaseExpenseDetailDto>>(ok.Value);
        var row = Assert.Single(rows);

        Assert.Equal(4_000m, row.OutstandingAmount);
        Assert.Equal(PaymentStatus.Overdue, row.Status);
    }

    [Fact]
    public async Task Create_Update_Delete_RoundTripsStructuredProfile_AndCleansChildrenOnDelete()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var firstLegalEntityId = await CreateLegalEntityAsync(db, "Юрлицо 1");
        var secondLegalEntityId = await CreateLegalEntityAsync(db, "Юрлицо 2");
        var firstStatusId = await CreateClientStatusAsync(db, "Новый");
        var secondStatusId = await CreateClientStatusAsync(db, "VIP");
        var controller = CreateController(db, userId);

        var createModel = new Client
        {
            Name = "Клиент CRUD",
            Email = "client-crud@example.com",
            Phone = "+7 (900) 000-00-00",
            Company = "ООО Первое",
            Address = "Первый адрес",
            Notes = ClientProfileNotesPrefix + JsonSerializer.Serialize(new
            {
                addressEntries = new[]
                {
                    new
                    {
                        type = "registration",
                        sourceQuery = "Первый адрес",
                        fullAddress = "Первый адрес",
                        postalCode = "630001",
                        country = "Россия",
                        region = "Новосибирская обл",
                        city = "г Новосибирск",
                        street = "ул Первая",
                        house = "1",
                        flat = "10",
                    },
                },
                passportEntries = new[]
                {
                    new
                    {
                        series = "1234",
                        number = "567890",
                        issuedBy = "ОВД 1",
                        issueDate = new DateTime(2020, 1, 1),
                    },
                },
            }),
            IsActive = true,
            LegalEntityId = firstLegalEntityId,
            ResponsibleUserId = userId,
            ClientStatusId = firstStatusId,
        };

        var createAction = await controller.Create(createModel, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(createAction);
        var dto = Assert.IsType<Client>(created.Value);
        Assert.Equal(firstLegalEntityId, dto.LegalEntityId);
        Assert.Equal(firstStatusId, dto.ClientStatusId);
        Assert.Equal(userId, dto.ResponsibleUserId);
        Assert.Single(dto.Addresses);
        Assert.Equal("Первый адрес", dto.Addresses.Single().UnrestrictedValue);
        Assert.Equal("1234", (await db.ClientPassports.AsNoTracking().SingleAsync(item => item.ClientId == dto.Id)).Series);

        var updateModel = new Client
        {
            Name = "Клиент CRUD Updated",
            Email = "client-crud-updated@example.com",
            Phone = "+7 (900) 111-11-11",
            Company = "ООО Второе",
            Address = "Второй адрес",
            Notes = ClientProfileNotesPrefix + JsonSerializer.Serialize(new
            {
                addressEntries = new[]
                {
                    new
                    {
                        type = "actual",
                        sourceQuery = "Второй адрес",
                        fullAddress = "Второй адрес",
                        postalCode = "630002",
                        country = "Россия",
                        region = "Новосибирская обл",
                        city = "г Новосибирск",
                        street = "ул Вторая",
                        house = "2",
                        flat = "20",
                    },
                },
                passportEntries = new[]
                {
                    new
                    {
                        series = "4321",
                        number = "098765",
                        issuedBy = "ОВД 2",
                        issueDate = new DateTime(2021, 2, 2),
                    },
                },
            }),
            IsActive = false,
            LegalEntityId = secondLegalEntityId,
            ResponsibleUserId = userId,
            ClientStatusId = secondStatusId,
        };

        var updateAction = await controller.Update(dto.Id, updateModel, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(updateAction);
        var updated = Assert.IsType<Client>(ok.Value);
        Assert.Equal("Клиент CRUD Updated", updated.Name);
        Assert.Equal(secondLegalEntityId, updated.LegalEntityId);
        Assert.Equal(secondStatusId, updated.ClientStatusId);
        Assert.False(updated.IsActive);
        Assert.Single(updated.Addresses);
        Assert.Equal("Второй адрес", updated.Addresses.Single().UnrestrictedValue);

        var storedPassport = await db.ClientPassports.AsNoTracking().SingleAsync(item => item.ClientId == dto.Id);
        Assert.Equal("4321", storedPassport.Series);
        Assert.Equal("098765", storedPassport.Number);

        var deleteAction = await controller.Delete(dto.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteAction);
        Assert.Null(await db.Clients.AsNoTracking().SingleOrDefaultAsync(item => item.Id == dto.Id));
        Assert.Equal(0, await db.AddressRecords.AsNoTracking().CountAsync(item => item.ClientId == dto.Id));
        Assert.Equal(0, await db.ClientPassports.AsNoTracking().CountAsync(item => item.ClientId == dto.Id));
    }

    [Fact]
    public async Task Create_QuickModalValidation_UsesStructuredFields_AndIgnoresEmailRequirement()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(
            db,
            userId,
            new StaticFormRequirementsService(
                ("clients-modal", "Name"),
                ("clients-modal", "Notes"),
                ("clients-modal", "Phone"),
                ("clients-modal", "Email")));

        var missingStructuredFieldModel = new Client
        {
            Name = "Иванов Иван",
            Phone = "+7 (999) 000-00-00",
            Notes = ClientProfileNotesPrefix + JsonSerializer.Serialize(new
            {
                lastName = "Иванов",
                firstName = "Иван",
                middleName = "",
                comment = "",
            }),
            IsActive = true,
        };

        var badRequestAction = await controller.Create(missingStructuredFieldModel, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(badRequestAction);
        var missingFields = Assert.IsAssignableFrom<IEnumerable<string>>(badRequest.Value?.GetType().GetProperty("missingFields")?.GetValue(badRequest.Value));
        Assert.Contains("Comment", missingFields);
        Assert.DoesNotContain("Name", missingFields);
        Assert.DoesNotContain("Notes", missingFields);
        Assert.DoesNotContain("Email", missingFields);
        Assert.DoesNotContain("FirstName", missingFields);
        Assert.DoesNotContain("LastName", missingFields);
        Assert.DoesNotContain("MiddleName", missingFields);

        var validModel = new Client
        {
            Name = "Иванов Иван Иванович",
            Phone = "+7 (999) 000-00-00",
            Notes = ClientProfileNotesPrefix + JsonSerializer.Serialize(new
            {
                lastName = "Иванов",
                firstName = "Иван",
                middleName = "Иванович",
                comment = "Перезвонить после обеда",
            }),
            IsActive = true,
        };

        var createAction = await controller.Create(validModel, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(createAction);
        var dto = Assert.IsType<Client>(created.Value);
        Assert.Equal("Иванов Иван Иванович", dto.Name);
        Assert.Equal("+7 (999) 000-00-00", dto.Phone);
        Assert.Equal(string.Empty, dto.Email);
    }

    [Fact]
    public async Task Create_QuickModalValidation_LegacyNameRequirement_MapsToFirstNameOnly()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(
            db,
            userId,
            new StaticFormRequirementsService(("clients-modal", "Name")));

        var missingFirstNameModel = new Client
        {
            Name = "Client",
            Notes = ClientProfileNotesPrefix + JsonSerializer.Serialize(new
            {
                lastName = "Ivanov",
                firstName = "",
                middleName = "",
                comment = "callback",
            }),
            IsActive = true,
        };

        var badRequestAction = await controller.Create(missingFirstNameModel, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(badRequestAction);
        var missingFields = Assert.IsAssignableFrom<IEnumerable<string>>(badRequest.Value?.GetType().GetProperty("missingFields")?.GetValue(badRequest.Value));
        Assert.Contains("FirstName", missingFields);
        Assert.DoesNotContain("LastName", missingFields);
        Assert.DoesNotContain("MiddleName", missingFields);
        Assert.DoesNotContain("Name", missingFields);
    }

    [Fact]
    public async Task Delete_RejectsClient_WhenPaymentUsageExists()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(db, userId);

        var client = new Client
        {
            Name = "Клиент с платежом",
            ResponsibleUserId = userId,
            IsActive = true,
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var payment = new Payment
        {
            Amount = 7_500m,
            PaidAmount = 0m,
            CounterpartyName = "Клиент с платежом",
            Date = new DateTime(2026, 4, 12),
            PlannedDate = new DateTime(2026, 4, 12),
            Description = "Платеж клиента",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            ClientId = client.Id,
            Notes = string.Empty,
            SystemNotes = string.Empty,
        };

        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var action = await controller.Delete(client.Id, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(action);
        var usage = Assert.IsAssignableFrom<IEnumerable<string>>(conflict.Value?.GetType().GetProperty("usage")?.GetValue(conflict.Value));
        Assert.Contains(usage, item => !string.IsNullOrWhiteSpace(item));
        Assert.NotNull(await db.Clients.AsNoTracking().SingleOrDefaultAsync(item => item.Id == client.Id));
    }

    [Fact]
    public async Task BulkDelete_RemovesSelectedUnusedClients_AndSoftDeletesProfiles()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(db, userId);

        var firstClient = new Client
        {
            Name = "Клиент 1",
            ResponsibleUserId = userId,
            IsActive = true,
        };

        var secondClient = new Client
        {
            Name = "Клиент 2",
            ResponsibleUserId = userId,
            IsActive = true,
        };

        db.Clients.AddRange(firstClient, secondClient);
        await db.SaveChangesAsync();

        db.AddressRecords.AddRange(
            new AddressRecord { ClientId = firstClient.Id, Type = AddressType.Registration, Value = "Адрес 1", IsPrimary = true },
            new AddressRecord { ClientId = secondClient.Id, Type = AddressType.Registration, Value = "Адрес 2", IsPrimary = true });
        db.ClientPassports.AddRange(
            new ClientPassport { ClientId = firstClient.Id, Series = "1111", Number = "111111", IsPrimary = true },
            new ClientPassport { ClientId = secondClient.Id, Series = "2222", Number = "222222", IsPrimary = true });
        await db.SaveChangesAsync();

        var action = await controller.BulkDelete(new PayPlanner.Api.Models.Requests.BulkIdsRequest
        {
            Ids = [firstClient.Id, secondClient.Id],
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.Equal(2, ok.Value?.GetType().GetProperty("deleted")?.GetValue(ok.Value));
        Assert.Equal(0, await db.Clients.AsNoTracking().CountAsync(item => item.Id == firstClient.Id || item.Id == secondClient.Id));
        Assert.Equal(0, await db.AddressRecords.AsNoTracking().CountAsync(item => item.ClientId == firstClient.Id || item.ClientId == secondClient.Id));
        Assert.Equal(0, await db.ClientPassports.AsNoTracking().CountAsync(item => item.ClientId == firstClient.Id || item.ClientId == secondClient.Id));
    }

    [Fact]
    public async Task ChangeResponsible_UpdatesSelectedClients()
    {
        await using var db = await CreateContextAsync();
        var adminUserId = await CreateUserAsync(db, roleName: "admin");
        var nextUserId = await CreateUserAsync(db, roleName: "user");
        var controller = CreateController(db, adminUserId);

        var firstClient = new Client
        {
            Name = "Клиент A",
            ResponsibleUserId = adminUserId,
            IsActive = true,
        };

        var secondClient = new Client
        {
            Name = "Клиент B",
            ResponsibleUserId = adminUserId,
            IsActive = true,
        };

        db.Clients.AddRange(firstClient, secondClient);
        await db.SaveChangesAsync();

        var action = await controller.ChangeResponsible(new PayPlanner.Api.Models.Requests.BulkAssignResponsibleRequest
        {
            Ids = [firstClient.Id, secondClient.Id],
            ResponsibleUserId = nextUserId,
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.Equal(2, ok.Value?.GetType().GetProperty("updated")?.GetValue(ok.Value));

        var responsibles = await db.Clients.AsNoTracking()
            .Where(item => item.Id == firstClient.Id || item.Id == secondClient.Id)
            .Select(item => item.ResponsibleUserId)
            .Distinct()
            .ToListAsync();

        Assert.Equal([nextUserId], responsibles);
    }

    private static async Task<int> CreateUserAsync(PaymentContext db, string roleName = "user")
    {
        var role = new Role
        {
            Name = roleName,
            Description = roleName,
            CreatedAt = DateTime.UtcNow,
        };

        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var user = new User
        {
            Email = $"client-tests-{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            FullName = "Тестовый пользователь",
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

    private static async Task SetClientPermissionAsync(
        PaymentContext db,
        int userId,
        bool canCreate,
        bool canEdit,
        bool canDelete,
        bool canView = true)
    {
        var roleId = await db.Users
            .Where(user => user.Id == userId)
            .Select(user => user.RoleId)
            .SingleAsync();
        var permission = await db.RolePermissions
            .SingleOrDefaultAsync(item => item.RoleId == roleId && item.Section == RolePermissionCatalog.Clients);

        if (permission is null)
        {
            permission = new RolePermission
            {
                RoleId = roleId,
                Section = RolePermissionCatalog.Clients,
                ViewScope = DataVisibilityScope.All,
            };
            db.RolePermissions.Add(permission);
        }

        permission.CanView = canView;
        permission.CanCreate = canCreate;
        permission.CanEdit = canEdit;
        permission.CanDelete = canDelete;
        permission.CanExport = true;
        permission.ViewScope = DataVisibilityScope.All;
        await db.SaveChangesAsync();
    }

    private static async Task<int> CreateLegalEntityAsync(PaymentContext db, string shortName)
    {
        var entity = new LegalEntity
        {
            ShortName = shortName,
            FullName = shortName,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.LegalEntities.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private static async Task<int> CreateClientStatusAsync(PaymentContext db, string name)
    {
        var status = new ClientStatusEntity
        {
            Name = name,
            ColorHex = "#2563EB",
        };

        db.ClientStatuses.Add(status);
        await db.SaveChangesAsync();
        return status.Id;
    }

    private static ClientsController CreateController(
        PaymentContext db,
        int userId,
        IFormRequirementsService? formRequirementsService = null)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "Тестовый пользователь"),
        };

        var controller = new ClientsController(
            db,
            new NoopEnrichmentService(),
            formRequirementsService ?? new NoopFormRequirementsService())
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

    private static ClientsV2Controller CreateV2Controller(PaymentContext db, int userId)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "Тестовый пользователь"),
        };

        return new ClientsV2Controller(db)
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

    private static ClientsV1Controller CreateV1Controller(PaymentContext db, int userId)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "Тестовый пользователь"),
        };

        return new ClientsV1Controller(db)
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

    private static async Task<PaymentContext> CreateContextAsync(DbCommandInterceptor? interceptor = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var optionsBuilder = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection);
        if (interceptor is not null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        var db = new PaymentContext(optionsBuilder.Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private static (string TimeZoneId, DateTime ProfileToday) ResolveTimeZoneWithDifferentLocalDate()
    {
        var nowUtc = DateTime.UtcNow;
        var utcToday = nowUtc.Date;
        foreach (var timeZoneId in new[]
        {
            "Pacific/Kiritimati",
            "Line Islands Standard Time",
            "Pacific/Pago_Pago",
            "Samoa Standard Time",
        })
        {
            var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(timeZoneId);
            var profileToday = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone).Date;
            if (profileToday != utcToday)
            {
                return (timeZoneId, profileToday);
            }
        }

        throw new InvalidOperationException("No test timezone with a local date different from UTC was found.");
    }

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }

    private sealed class StaticFormRequirementsService : IFormRequirementsService
    {
        private readonly Dictionary<string, List<string>> _requiredBySection;

        public StaticFormRequirementsService(params (string Section, string FieldKey)[] requiredFields)
        {
            _requiredBySection = requiredFields
                .GroupBy(item => item.Section, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.FieldKey).ToList(),
                    StringComparer.OrdinalIgnoreCase);
        }

        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default)
            => Task.FromResult(_requiredBySection.TryGetValue(section, out var fields) ? fields.ToList() : new List<string>());

        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default)
        {
            var requiredFields = _requiredBySection.TryGetValue(section, out var fields)
                ? fields
                : new List<string>();

            var properties = model.GetType()
                .GetProperties()
                .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

            var missing = new List<string>();
            foreach (var field in requiredFields)
            {
                if (!properties.TryGetValue(field, out var property))
                {
                    missing.Add(field);
                    continue;
                }

                var value = property.GetValue(model);
                if (value is null)
                {
                    missing.Add(field);
                    continue;
                }

                if (value is string text && string.IsNullOrWhiteSpace(text))
                {
                    missing.Add(field);
                }
            }

            return Task.FromResult(missing);
        }
    }

    private sealed class NoopEnrichmentService : ILegalEntityEnrichmentService
    {
        public Task<IReadOnlyList<ILegalEntityEnrichmentService.DadataAddressSuggestion>> SuggestAddressesAsync(string query, int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ILegalEntityEnrichmentService.DadataAddressSuggestion>>(Array.Empty<ILegalEntityEnrichmentService.DadataAddressSuggestion>());

        public Task<CleanedAddress?> CleanAddressAsync(string rawAddress, CancellationToken cancellationToken)
            => Task.FromResult<CleanedAddress?>(null);

        public Task<CleanedEmail?> CleanEmailAsync(string rawEmail, CancellationToken cancellationToken)
            => Task.FromResult<CleanedEmail?>(null);

        public Task<CleanedPhone?> CleanPhoneAsync(string rawPhone, CancellationToken cancellationToken)
            => Task.FromResult<CleanedPhone?>(null);

        public Task<LegalEntityDetails?> FindByInnOrOgrnAsync(string query, string? kpp, CancellationToken cancellationToken)
            => Task.FromResult<LegalEntityDetails?>(null);

        public Task<IReadOnlyList<LegalEntitySuggestion>> SuggestAsync(string? query, string? inn, int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<LegalEntitySuggestion>>(Array.Empty<LegalEntitySuggestion>());
    }
}
