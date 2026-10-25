using System.Data.Common;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services.Invoices;
using Xunit;

namespace PayPlanner.Api.Tests;

// Characterizes independent source availability; every fixture contains synthetic records only.
public sealed class ActsSourceAvailabilityTests
{
    public static IEnumerable<object[]> DeniedSources()
    {
        foreach (var update in new[] { false, true })
            foreach (var source in new[] { "invoice", "client", "company", "contract" })
                foreach (var state in new[] { "hidden", "missing", "deleted" })
                    yield return [update, source, state];
    }

    [Theory]
    [MemberData(nameof(DeniedSources))]
    public async Task Save_RejectsUnavailableSourceBeforePayloadOrMutation(bool update, string source, string state)
    {
        await using var f = await Fixture.CreateAsync();
        if (source == "contract") f.Permission(RolePermissionCatalog.Clients).ViewScope = DataVisibilityScope.All;
        var request = f.Request();
        request.Comment = "Must not be saved";
        switch (source)
        {
            case "invoice":
                request.InvoiceId = state == "missing" ? 999_991 : f.Invoice.Id;
                request.ClientId = null;
                request.CreationMode = ActCreationMode.Full;
                break;
            case "client": request.ClientId = state == "missing" ? 999_992 : f.ForeignClient.Id; break;
            case "company":
                request.ClientId = null;
                request.LegalEntityId = state == "missing" ? 999_993 : f.ForeignCompany.Id;
                break;
            case "contract":
                request.ClientId = f.ForeignClient.Id;
                request.ContractId = state == "missing" ? 999_994 : f.Contract.Id;
                break;
        }
        if (state == "deleted")
        {
            object entity = source switch
            {
                "invoice" => f.Invoice,
                "client" => f.ForeignClient,
                "company" => f.ForeignCompany,
                "contract" => f.Contract,
                _ => throw new ArgumentOutOfRangeException(nameof(source)),
            };
            f.Db.Entry(entity).Property("IsDeleted").CurrentValue = true;
        }
        await f.Db.SaveChangesAsync();
        var before = await f.SnapshotAsync();
        f.Commands.Clear();

        var result = await f.SaveAsync(update, request);
        var commands = f.Commands.ToArray();

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal("{\"message\":\"Выбранное основание акта недоступно.\"}",
            JsonSerializer.Serialize(notFound.Value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.DoesNotContain(commands, IsSourceLinePayloadQuery);
        Assert.DoesNotContain(commands, IsLegacyPayloadQuery);
        Assert.Empty(f.Notifications.Calls);
        Assert.Equal(before, await f.SnapshotAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Save_AllowsAvailableInvoiceWithoutClientOrProfileOrPaymentPermissions(bool update, bool company)
    {
        await using var f = await Fixture.CreateAsync();
        f.Permission(RolePermissionCatalog.Accounts).ViewScope = DataVisibilityScope.All;
        f.Permission(RolePermissionCatalog.Clients).CanView = false;
        f.Permission(RolePermissionCatalog.LegalEntities).CanView = false;
        if (company)
        {
            f.Invoice.ClientId = null;
            f.Invoice.LegalEntityId = f.ForeignCompany.Id;
            f.Db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{f.ForeignCompany.Id}]" });
        }
        await f.Db.SaveChangesAsync();
        var request = f.Request();
        request.InvoiceId = f.Invoice.Id;
        request.ClientId = null;
        request.CreationMode = ActCreationMode.Full;

        var result = await f.SaveAsync(update, request);

        var dto = SuccessfulDto(result, update);
        Assert.Equal(123.45m, dto.Amount);
        Assert.Equal("Synthetic confidential line", Assert.Single(dto.LineItems).Name);
        Assert.Equal(f.Invoice.Id, dto.InvoiceId);
        Assert.Equal(company ? (int?)null : f.ForeignClient.Id, dto.ClientId);
        Assert.Equal(company ? f.ForeignCompany.Id : (int?)null, dto.LegalEntityId);
        Assert.False(f.Permission(RolePermissionCatalog.CompanyProfile).CanView);
        Assert.False(f.Permission(RolePermissionCatalog.Payments).CanView);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Save_AllowsVisibleManualCounterpartyWithoutInvoiceOrEditPermission(bool update, bool company)
    {
        await using var f = await Fixture.CreateAsync();
        var request = f.Request();
        if (company)
        {
            f.ForeignCompany.ResponsibleUserId = f.Actor.Id;
            request.ClientId = null;
            request.LegalEntityId = f.ForeignCompany.Id;
        }
        await f.Db.SaveChangesAsync();

        var dto = SuccessfulDto(await f.SaveAsync(update, request), update);

        Assert.Null(dto.InvoiceId);
        Assert.Equal(10m, dto.Amount);
        Assert.False(f.Permission(RolePermissionCatalog.Clients).CanEdit);
        Assert.False(f.Permission(RolePermissionCatalog.LegalEntities).CanEdit);
        Assert.False(f.Permission(RolePermissionCatalog.CompanyProfile).CanEdit);
    }

    [Theory]
    [InlineData(false, DataVisibilityScope.Own, false)]
    [InlineData(true, DataVisibilityScope.Own, false)]
    [InlineData(false, DataVisibilityScope.Department, true)]
    [InlineData(true, DataVisibilityScope.Department, true)]
    [InlineData(false, DataVisibilityScope.All, true)]
    [InlineData(true, DataVisibilityScope.All, true)]
    public async Task Save_UsesAccountsScopeForInvoiceIndependentlyOfActsScope(bool update, string scope, bool allowed)
    {
        await using var f = await Fixture.CreateAsync();
        f.Actor.Department = f.Other.Department = "Synthetic department";
        f.Permission(RolePermissionCatalog.Accounts).ViewScope = scope;
        await f.Db.SaveChangesAsync();
        var request = f.Request();
        request.InvoiceId = f.Invoice.Id;
        request.ClientId = null;
        request.CreationMode = ActCreationMode.Full;

        var result = await f.SaveAsync(update, request);

        if (allowed) SuccessfulDto(result, update);
        else Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_DoesNotTrustForgedAdminClaimForForeignInvoice(bool update)
    {
        await using var f = await Fixture.CreateAsync();
        f.Controller.User.AddIdentity(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")]));
        var request = f.Request();
        request.InvoiceId = f.Invoice.Id;
        request.ClientId = null;
        request.CreationMode = ActCreationMode.Full;
        var before = await f.SnapshotAsync();

        var result = await f.SaveAsync(update, request);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal(before, await f.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_RejectsOwnerSpoofEvenWhenBothInvoiceAndClientAreVisible(bool update)
    {
        await using var f = await Fixture.CreateAsync();
        f.Permission(RolePermissionCatalog.Accounts).ViewScope = DataVisibilityScope.All;
        f.Permission(RolePermissionCatalog.Clients).ViewScope = DataVisibilityScope.All;
        await f.Db.SaveChangesAsync();
        var request = f.Request();
        request.InvoiceId = f.Invoice.Id;
        request.ClientId = f.OwnClient.Id;
        request.CreationMode = ActCreationMode.Full;
        var before = await f.SnapshotAsync();
        f.Commands.Clear();

        var result = await f.SaveAsync(update, request);
        var commands = f.Commands.ToArray();

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.DoesNotContain(commands, IsSourceLinePayloadQuery);
        Assert.DoesNotContain(commands, IsLegacyPayloadQuery);
        Assert.Equal(before, await f.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_DoesNotForgeContractVisibilityThroughNewAct(bool update)
    {
        await using var f = await Fixture.CreateAsync();
        f.Permission(RolePermissionCatalog.Clients).ViewScope = DataVisibilityScope.All;
        await f.Db.SaveChangesAsync();
        var contractReader = f.ContractReader();
        Assert.IsType<NotFoundResult>((await contractReader.GetById(f.Contract.Id, default)).Result);
        var request = f.Request();
        request.ClientId = f.ForeignClient.Id;
        request.ContractId = f.Contract.Id;
        var before = await f.SnapshotAsync();

        var result = await f.SaveAsync(update, request);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal(before, await f.SnapshotAsync());
        Assert.IsType<NotFoundResult>((await contractReader.GetById(f.Contract.Id, default)).Result);
        Assert.False(await f.Db.ContractActs.AnyAsync());
    }

    [Fact]
    public async Task Update_PreservesUnchangedStandaloneOwnerWithoutNewClientAccess()
    {
        await using var f = await Fixture.CreateAsync();
        f.Permission(RolePermissionCatalog.Clients).CanView = false;
        await f.Db.SaveChangesAsync();
        var request = f.Request();
        request.Comment = "Allowed existing limited context";

        var dto = SuccessfulDto(await f.SaveAsync(true, request), true);

        Assert.Equal(request.Comment, dto.Comment);
        Assert.Equal(f.OwnClient.Id, dto.ClientId);
        Assert.Null(dto.InvoiceId);
    }

    [Fact]
    public async Task Update_PreservesUnchangedManualContractLinkWithoutNewContractAccess()
    {
        await using var f = await Fixture.CreateAsync();
        f.Existing.ClientId = f.ForeignClient.Id;
        f.Db.ContractActs.Add(new ContractAct { ActId = f.Existing.Id, ContractId = f.Contract.Id });
        f.Permission(RolePermissionCatalog.Clients).CanView = false;
        f.Permission(RolePermissionCatalog.Contracts).CanView = false;
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        var request = f.Request();
        request.ClientId = f.ForeignClient.Id;
        request.ContractId = f.Contract.Id;
        request.Comment = "Preserve existing link";
        f.Commands.Clear();

        var dto = SuccessfulDto(await f.SaveAsync(true, request), true);
        var commands = f.Commands.ToArray();

        Assert.Equal(f.Contract.Id, dto.ContractId);
        Assert.DoesNotContain(commands, sql => sql.Contains("DELETE FROM \"ContractActs\"", StringComparison.Ordinal)
            || sql.Contains("INSERT INTO \"ContractActs\"", StringComparison.Ordinal));
        Assert.Equal(f.Contract.Id, (await f.Db.ContractActs.SingleAsync()).ContractId);
    }

    [Fact]
    public async Task Update_UnchangedInvoiceStillRequiresFreshAccountsAccessBeforeReadingItsLines()
    {
        await using var f = await Fixture.CreateAsync();
        f.Existing.InvoiceId = f.Invoice.Id;
        f.Existing.ClientId = f.ForeignClient.Id;
        f.Permission(RolePermissionCatalog.Accounts).CanView = false;
        await f.Db.SaveChangesAsync();
        var request = f.Request();
        request.InvoiceId = f.Invoice.Id;
        request.ClientId = f.ForeignClient.Id;
        request.CreationMode = ActCreationMode.Full;
        var before = await f.SnapshotAsync();
        f.Commands.Clear();

        var result = await f.SaveAsync(true, request);
        var commands = f.Commands.ToArray();

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.DoesNotContain(commands, IsSourceLinePayloadQuery);
        Assert.DoesNotContain(commands, IsLegacyPayloadQuery);
        Assert.Equal(before, await f.SnapshotAsync());
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task Save_ManualOwnCompanyUsesProfileViewAndAssignmentWithoutProfileEdit(bool update, bool view, bool assigned)
    {
        await using var f = await Fixture.CreateAsync();
        f.Permission(RolePermissionCatalog.LegalEntities).CanView = false;
        f.Permission(RolePermissionCatalog.CompanyProfile).CanView = view;
        f.Db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{f.ForeignCompany.Id}]" });
        if (assigned) f.Db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = f.Actor.Id, LegalEntityId = f.ForeignCompany.Id });
        await f.Db.SaveChangesAsync();
        var request = f.Request();
        request.ClientId = null;
        request.LegalEntityId = f.ForeignCompany.Id;
        var before = await f.SnapshotAsync();

        var result = await f.SaveAsync(update, request);

        if (view && assigned) Assert.Equal(f.ForeignCompany.Id, SuccessfulDto(result, update).LegalEntityId);
        else
        {
            Assert.IsType<NotFoundObjectResult>(result.Result);
            Assert.Equal(before, await f.SnapshotAsync());
        }
        Assert.False(f.Permission(RolePermissionCatalog.CompanyProfile).CanEdit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_UsesActualDatabaseAdminRoleForAvailableInvoice(bool update)
    {
        await using var f = await Fixture.CreateAsync();
        f.Actor.Role!.Name = "admin";
        foreach (var permission in f.Actor.Role.Permissions) permission.CanView = false;
        await f.Db.SaveChangesAsync();
        var request = f.Request();
        request.InvoiceId = f.Invoice.Id;
        request.ClientId = null;
        request.CreationMode = ActCreationMode.Full;

        Assert.Equal(123.45m, SuccessfulDto(await f.SaveAsync(update, request), update).Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_PreservesSharedInvoiceAccessThroughCurrentResponsible(bool update)
    {
        await using var f = await Fixture.CreateAsync();
        f.Permission(RolePermissionCatalog.Clients).CanView = false;
        f.Db.PaymentResponsibles.Add(new PaymentResponsible { PaymentId = f.Invoice.Id, UserId = f.Actor.Id });
        await f.Db.SaveChangesAsync();
        var request = f.Request();
        request.InvoiceId = f.Invoice.Id;
        request.ClientId = null;
        request.CreationMode = ActCreationMode.Full;

        Assert.Equal(f.ForeignClient.Id, SuccessfulDto(await f.SaveAsync(update, request), update).ClientId);
    }

    [Theory]
    [InlineData(false, "primary")]
    [InlineData(true, "primary")]
    [InlineData(false, "expense")]
    [InlineData(true, "expense")]
    [InlineData(false, "third")]
    [InlineData(true, "third")]
    public async Task Save_ExpenseInvoiceAllowsItsExistingOwnerPairsAndRejectsThirdOwner(bool update, string owner)
    {
        await using var f = await Fixture.CreateAsync();
        f.Permission(RolePermissionCatalog.Accounts).ViewScope = DataVisibilityScope.All;
        f.Permission(RolePermissionCatalog.LegalEntities).ViewScope = DataVisibilityScope.All;
        f.Invoice.Type = PaymentType.Expense;
        f.Invoice.ExpenseClientId = f.OwnClient.Id;
        await f.Db.SaveChangesAsync();
        var request = f.Request();
        request.InvoiceId = f.Invoice.Id;
        request.CreationMode = ActCreationMode.Full;
        request.ClientId = owner == "primary" ? f.ForeignClient.Id : f.OwnClient.Id;
        if (owner == "third") { request.ClientId = null; request.LegalEntityId = f.ForeignCompany.Id; }
        var before = await f.SnapshotAsync();
        f.Commands.Clear();

        var result = await f.SaveAsync(update, request);
        var commands = f.Commands.ToArray();

        if (owner != "third") Assert.Equal(request.ClientId, SuccessfulDto(result, update).ClientId);
        else
        {
            Assert.IsType<NotFoundObjectResult>(result.Result);
            Assert.DoesNotContain(commands, IsSourceLinePayloadQuery);
            Assert.DoesNotContain(commands, IsLegacyPayloadQuery);
            Assert.Equal(before, await f.SnapshotAsync());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Save_PreservesLegacyLineFallbackOnlyAfterSourceIsAvailable(bool update, bool allowed)
    {
        await using var f = await Fixture.CreateAsync();
        if (allowed) f.Permission(RolePermissionCatalog.Accounts).ViewScope = DataVisibilityScope.All;
        f.Db.InvoiceLineItems.RemoveRange(await f.Db.InvoiceLineItems.ToListAsync());
        f.Invoice.SystemNotes = InvoiceLineItemStorage.MergeIntoSystemNotes("Synthetic history",
            [new InvoiceLineItemRequest { Name = "Synthetic legacy secret", Quantity = 1m, Price = 123.45m, Unit = "шт" }]);
        await f.Db.SaveChangesAsync();
        var request = f.Request();
        request.InvoiceId = f.Invoice.Id;
        request.ClientId = null;
        request.CreationMode = ActCreationMode.Full;
        var before = await f.SnapshotAsync();
        f.Commands.Clear();

        var result = await f.SaveAsync(update, request);
        var commands = f.Commands.ToArray();

        if (allowed) Assert.Equal("Synthetic legacy secret", Assert.Single(SuccessfulDto(result, update).LineItems).Name);
        else
        {
            Assert.IsType<NotFoundObjectResult>(result.Result);
            Assert.DoesNotContain(commands, IsSourceLinePayloadQuery);
            Assert.DoesNotContain(commands, IsLegacyPayloadQuery);
            Assert.Equal(before, await f.SnapshotAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_RejectsHiddenNewContractBeforeReadingAllowedInvoicePayload(bool update)
    {
        await using var f = await Fixture.CreateAsync();
        f.Permission(RolePermissionCatalog.Accounts).ViewScope = DataVisibilityScope.All;
        await f.Db.SaveChangesAsync();
        var request = f.Request();
        request.InvoiceId = f.Invoice.Id;
        request.ClientId = null;
        request.ContractId = f.Contract.Id;
        request.CreationMode = ActCreationMode.Full;
        var before = await f.SnapshotAsync();
        f.Commands.Clear();

        var result = await f.SaveAsync(update, request);
        var commands = f.Commands.ToArray();

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.DoesNotContain(commands, IsSourceLinePayloadQuery);
        Assert.DoesNotContain(commands, IsLegacyPayloadQuery);
        Assert.Equal(before, await f.SnapshotAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Save_UsesCurrentInvoiceRepresentativeAndAmbiguousLinkProtection(bool update, bool ambiguous)
    {
        await using var f = await Fixture.CreateAsync();
        f.Permission(RolePermissionCatalog.Accounts).ViewScope = DataVisibilityScope.All;
        var sibling = new Payment
        {
            InvoiceId = f.Invoice.InvoiceId,
            DocumentKind = DocumentKind.Invoice,
            Account = "SYN-SIBLING",
            ClientId = ambiguous ? f.OwnClient.Id : f.ForeignClient.Id,
            Amount = 1m,
            Type = PaymentType.Income,
            Date = new DateTime(2026, 10, 24),
        };
        f.Db.Payments.Add(sibling);
        await f.Db.SaveChangesAsync();
        var request = f.Request();
        request.InvoiceId = ambiguous ? f.Invoice.Id : sibling.Id;
        request.ClientId = null;
        request.CreationMode = ActCreationMode.Full;
        var before = await f.SnapshotAsync();
        f.Commands.Clear();

        var result = await f.SaveAsync(update, request);
        var commands = f.Commands.ToArray();

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.DoesNotContain(commands, IsSourceLinePayloadQuery);
        Assert.DoesNotContain(commands, IsLegacyPayloadQuery);
        Assert.Equal(before, await f.SnapshotAsync());
    }

    [Fact]
    public async Task ContractCreate_RejectsOversizedSourceSetWithoutTruncationOrPersistence()
    {
        await using var f = await Fixture.CreateAsync();
        f.Permission(RolePermissionCatalog.Contracts).CanCreate = true;
        await f.Db.SaveChangesAsync();
        var before = await f.SnapshotAsync();
        var count = await f.Db.Contracts.CountAsync();
        var controller = f.ContractReader();

        var result = await controller.Create(new UpsertContractRequest
        {
            Number = "SYN-TOO-LARGE",
            Date = new DateTime(2026, 10, 24),
            BillingMode = ContractBillingMode.Actual,
            IsOpenEnded = true,
            Clients = [new UpsertContractClientRequest { ClientId = f.OwnClient.Id }],
            InvoiceIds = Enumerable.Range(1, 5001).ToList(),
        }, default);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("Слишком много связанных документов", JsonSerializer.Serialize(badRequest.Value,
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), StringComparison.Ordinal);
        Assert.Equal(count, await f.Db.Contracts.CountAsync());
        Assert.Equal(before, await f.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_RefreshesCleanTrackedOwnerAndInvoiceBeforeApplyingRequestedValues(bool invoiceContext)
    {
        await using var f = await Fixture.CreateAsync();
        f.Permission(RolePermissionCatalog.Accounts).ViewScope = DataVisibilityScope.All;
        f.Permission(RolePermissionCatalog.Clients).ViewScope = DataVisibilityScope.All;
        f.Existing.ClientId = invoiceContext ? f.ForeignClient.Id : f.OwnClient.Id;
        f.Existing.InvoiceId = invoiceContext ? f.Invoice.Id : null;
        await f.Db.SaveChangesAsync();
        var cachedOwner = f.Existing.ClientId;
        var cachedInvoice = f.Existing.InvoiceId;
        var connection = f.Db.Database.GetDbConnection();
        await using (var authority = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                         .UseSqlite(connection).Options))
        {
            var actual = await authority.Acts.SingleAsync(a => a.Id == f.Existing.Id);
            if (invoiceContext)
            {
                var replacement = new Payment
                {
                    Account = "SYN-REPLACEMENT",
                    ClientId = f.ForeignClient.Id,
                    DocumentKind = DocumentKind.Invoice,
                    Type = PaymentType.Income,
                    Amount = 999m,
                    Date = new DateTime(2026, 10, 24),
                    Invoice = new Invoice { Number = "SYN-REPLACEMENT", Date = new DateTime(2026, 10, 24) },
                };
                authority.Payments.Add(replacement);
                await authority.SaveChangesAsync();
                actual.InvoiceId = replacement.Id;
            }
            else actual.ClientId = f.ForeignClient.Id;
            await authority.SaveChangesAsync();
        }
        // Deliberately retain stale Unchanged entities. SnapshotAsync would clear and hide this regression.
        Assert.Equal(EntityState.Unchanged, f.Db.Entry(f.Existing).State);
        Assert.Equal(cachedOwner, f.Existing.ClientId);
        Assert.Equal(cachedInvoice, f.Existing.InvoiceId);
        var request = f.Request();
        request.ClientId = cachedOwner;
        request.InvoiceId = cachedInvoice;
        request.CreationMode = invoiceContext ? ActCreationMode.Full : ActCreationMode.Manual;

        var result = await f.Controller.Update(f.Existing.Id, request, default);

        SuccessfulDto(result, true);
        await using var read = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        var saved = await read.Acts.AsNoTracking().SingleAsync(a => a.Id == f.Existing.Id);
        Assert.Equal(cachedOwner, saved.ClientId);
        Assert.Equal(cachedInvoice, saved.InvoiceId);
        if (invoiceContext)
        {
            var lines = await read.Set<ActLineItem>().AsNoTracking().Where(l => l.ActId == saved.Id).ToArrayAsync();
            var line = Assert.Single(lines);
            Assert.Equal("Synthetic confidential line", line.Name);
            Assert.Equal(123.45m, saved.Amount);
        }
    }

    [Fact]
    public async Task ContractCancel_RefreshesTrackedRelationsAndPreservesExternallyPaidInvoice()
    {
        await using var f = await Fixture.CreateAsync();
        f.Permission(RolePermissionCatalog.Contracts).ViewScope = DataVisibilityScope.All;
        f.Permission(RolePermissionCatalog.Contracts).CanEdit = true;
        f.Invoice.ContractId = f.Contract.Id;
        f.Db.ContractInvoices.Add(new ContractInvoice { ContractId = f.Contract.Id, InvoiceId = f.Invoice.Id });
        await f.Db.SaveChangesAsync();
        var cached = await f.Db.Contracts.Include(c => c.ContractInvoices).ThenInclude(l => l.Invoice)
            .SingleAsync(c => c.Id == f.Contract.Id);
        Assert.Same(f.Invoice, Assert.Single(cached.ContractInvoices).Invoice);
        Assert.Equal(PaymentStatus.Pending, f.Invoice.Status);
        var connection = f.Db.Database.GetDbConnection();
        await using (var authority = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options))
        {
            var invoice = await authority.Payments.SingleAsync(p => p.Id == f.Invoice.Id);
            invoice.Status = PaymentStatus.Completed;
            invoice.PaidAmount = invoice.Amount;
            invoice.IsPaid = true;
            await authority.SaveChangesAsync();
        }
        Assert.Equal(EntityState.Unchanged, f.Db.Entry(f.Invoice).State);
        Assert.Equal(PaymentStatus.Pending, f.Invoice.Status);

        var result = await f.ContractReader().Cancel(f.Contract.Id,
            new CancelContractRequest { Reason = "Synthetic cancellation after external payment" }, default);

        Assert.IsType<OkObjectResult>(result.Result);
        await using var read = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        var saved = await read.Payments.AsNoTracking().SingleAsync(p => p.Id == f.Invoice.Id);
        Assert.Equal(PaymentStatus.Completed, saved.Status);
        Assert.Equal(123.45m, saved.PaidAmount);
        Assert.True(saved.IsPaid);
        Assert.Null(saved.InvoiceCancelledAt);
        Assert.Equal(ContractStatus.Cancelled, await read.Contracts.Where(c => c.Id == f.Contract.Id).Select(c => c.Status).SingleAsync());
    }

    [Theory]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task Save_RejectsForeignPendingWorkWithoutSavingOrDiscardingTrackedEntries(EntityState state)
    {
        await using var f = await Fixture.CreateAsync();
        var request = f.Request();
        var originalName = f.ForeignCompany.ShortName;
        var originalCount = await f.Db.LegalEntities.CountAsync();
        var originalAuditCount = await f.Db.ObjectAuditEvents.CountAsync();
        var originalActCount = await f.Db.Acts.CountAsync();
        var entity = state == EntityState.Added ? new LegalEntity { ShortName = "Synthetic pending new company" } : f.ForeignCompany;
        if (state == EntityState.Added) f.Db.LegalEntities.Add(entity);
        else if (state == EntityState.Modified) entity.ShortName = "Synthetic pending company rename";
        else f.Db.LegalEntities.Remove(entity);
        f.Db.ChangeTracker.DetectChanges();
        Assert.Equal(state, f.Db.Entry(entity).State);
        var pendingName = entity.ShortName;

        var result = await f.Controller.Create(request, default);

        Assert.IsType<ConflictObjectResult>(result.Result);
        // Inspect the original entry before any cache-clearing snapshot or fixture disposal.
        Assert.Equal(state, f.Db.Entry(entity).State);
        Assert.Equal(pendingName, entity.ShortName);
        Assert.Equal(originalCount, await f.Db.LegalEntities.AsNoTracking().CountAsync());
        Assert.Equal(originalName, await f.Db.LegalEntities.AsNoTracking().Where(c => c.Id == f.ForeignCompany.Id)
            .Select(c => c.ShortName).SingleAsync());
        Assert.Equal(originalActCount, await f.Db.Acts.CountAsync());
        Assert.Equal(originalAuditCount, await f.Db.ObjectAuditEvents.CountAsync());
        Assert.Empty(f.Notifications.Calls);
    }

    [Fact]
    public async Task Save_SqliteImmediateAdmissionHasBoundedBusyWaitAndRestoresConnectionTimeout()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pp-act-source-{Guid.NewGuid():N}.db");
        try
        {
            await using var f = await Fixture.CreateSqliteFileAsync(path);
            f.Permission(RolePermissionCatalog.Accounts).ViewScope = DataVisibilityScope.All;
            await f.Db.SaveChangesAsync();
            var request = f.Request();
            request.InvoiceId = f.Invoice.Id;
            request.ClientId = null;
            request.CreationMode = ActCreationMode.Full;
            var before = await f.SnapshotAsync();
            var actorConnection = (SqliteConnection)f.Db.Database.GetDbConnection();
            actorConnection.DefaultTimeout = 7;
            await using var blocker = new SqliteConnection($"Data Source={path};Pooling=false");
            await blocker.OpenAsync();
            await using var transaction = blocker.BeginTransaction(deferred: false);
            var timer = System.Diagnostics.Stopwatch.StartNew();

            var result = await f.Controller.Create(request, default);

            Assert.IsType<ConflictObjectResult>(result.Result);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), "Immediate admission must not wait for the blocker's commit.");
            Assert.Equal(7, actorConnection.DefaultTimeout);
            Console.WriteLine($"SQLite immediate busy admission elapsedMs={timer.Elapsed.TotalMilliseconds:F0}; restoredTimeout={actorConnection.DefaultTimeout}");
            Assert.Equal(before, await f.SnapshotAsync());
            Assert.Empty(f.Notifications.Calls);
            await transaction.RollbackAsync();
        }
        finally
        {
            // Only this test's exact new files; no directory traversal or recursive cleanup.
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(path + suffix);
        }
    }

    private static ActDto SuccessfulDto(ActionResult<ActDto> result, bool update)
        => Assert.IsType<ActDto>(update
            ? Assert.IsType<OkObjectResult>(result.Result).Value
            : Assert.IsType<CreatedResult>(result.Result).Value);

    private static bool IsSourceLinePayloadQuery(string sql)
        => sql.Contains("FROM \"InvoiceLineItems\"", StringComparison.Ordinal);

    private static bool IsLegacyPayloadQuery(string sql)
        => sql.Contains("\"SystemNotes\"", StringComparison.Ordinal)
            && sql.Contains("FROM \"Payments\"", StringComparison.Ordinal);

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly IAsyncDisposable? connection;
        private readonly CommandRecorder recorder;
        public PaymentContext Db { get; }
        public User Actor { get; }
        public User Other { get; }
        public Client OwnClient { get; }
        public Client ForeignClient { get; }
        public LegalEntity ForeignCompany { get; }
        public Payment Invoice { get; }
        public Contract Contract { get; }
        public Act Existing { get; }
        public ActsController Controller { get; }
        public NotificationRecorder Notifications { get; }
        public List<string> Commands => recorder.Commands;

        private Fixture(IAsyncDisposable? connection, PaymentContext db, CommandRecorder recorder,
            User actor, User other, Client ownClient, Client foreignClient, LegalEntity company,
            Payment invoice, Contract contract, Act existing)
        {
            this.connection = connection; this.recorder = recorder;
            Db = db; Actor = actor; Other = other; OwnClient = ownClient; ForeignClient = foreignClient;
            ForeignCompany = company; Invoice = invoice; Contract = contract; Existing = existing;
            var proxy = DispatchProxy.Create<INotificationService, NotificationRecorder>();
            Notifications = (NotificationRecorder)(object)proxy;
            Controller = new ActsController(Db, new NoopFormRequirementsService(), notificationService: proxy)
            {
                ControllerContext = Context(),
            };
        }

        public RolePermission Permission(string section) => Actor.Role!.Permissions.Single(p => p.Section == section);

        private ControllerContext Context() => new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Actor.Id.ToString())], "SyntheticTest")),
            },
        };

        public ContractsController ContractReader() => new(Db, new NoopFormRequirementsService()) { ControllerContext = Context() };

        public UpsertActRequest Request() => new()
        {
            Number = "SYN-ACT-REQUEST",
            Date = new DateTime(2026, 10, 24),
            Amount = 10m,
            ClientId = OwnClient.Id,
            ResponsibleId = Actor.Id,
            CreationMode = ActCreationMode.Manual,
            Status = ActStatus.Created,
        };

        public Task<ActionResult<ActDto>> SaveAsync(bool update, UpsertActRequest request)
            => update ? Controller.Update(Existing.Id, request, default) : Controller.Create(request, default);

        public Task<string> SnapshotAsync()
        {
            Db.ChangeTracker.Clear();
            return CaptureAsync(Db);
        }

        internal static async Task<string> CaptureAsync(PaymentContext db)
        {
            db.ChangeTracker.Clear();
            return JsonSerializer.Serialize(new
            {
                Acts = await db.Acts.IgnoreQueryFilters().OrderBy(x => x.Id)
                    .Select(x => new { x.Id, x.Number, x.Date, x.Amount, x.CreationMode, x.InvoiceId, x.InvoiceNumber, x.ClientId, x.LegalEntityId, x.ResponsibleId, x.Status, x.WorkflowStatus, x.Comment, x.UpdatedAt }).ToArrayAsync(),
                Lines = await db.Set<ActLineItem>().IgnoreQueryFilters().OrderBy(x => x.Id)
                    .Select(x => new { x.Id, x.ActId, x.InvoiceLineItemId, x.SourceKey, x.Name, x.Quantity, x.Price, x.VatRate }).ToArrayAsync(),
                Links = await db.ContractActs.IgnoreQueryFilters().OrderBy(x => x.ContractId).ThenBy(x => x.ActId)
                    .Select(x => new { x.ContractId, x.ActId }).ToArrayAsync(),
                Source = await db.Payments.IgnoreQueryFilters().OrderBy(x => x.Id)
                    .Select(x => new { x.Id, x.Amount, x.ClientId, x.LegalEntityId, x.ContractId, x.SystemNotes }).ToArrayAsync(),
                SourceLines = await db.InvoiceLineItems.IgnoreQueryFilters().OrderBy(x => x.Id)
                    .Select(x => new { x.Id, x.PaymentId, x.Name, x.Quantity, x.Price }).ToArrayAsync(),
                AuditCount = await db.ObjectAuditEvents.CountAsync(),
                NotificationCount = await db.UserNotifications.CountAsync(),
            });
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var recorder = new CommandRecorder();
            var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).AddInterceptors(recorder).Options);
            await db.Database.EnsureCreatedAsync();
            return await SeedAsync(db, recorder, connection);
        }

        internal static async Task<Fixture> CreateSqliteFileAsync(string path)
        {
            var connection = new SqliteConnection($"Data Source={path};Pooling=false");
            await connection.OpenAsync();
            var recorder = new CommandRecorder();
            var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).AddInterceptors(recorder).Options);
            await db.Database.EnsureCreatedAsync();
            return await SeedAsync(db, recorder, connection);
        }

        internal static async Task<Fixture> CreatePostgreSqlAsync(string connectionString, params IInterceptor[] additional)
        {
            var recorder = new CommandRecorder();
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connectionString).AddInterceptors(recorder);
            if (additional.Length > 0) options.AddInterceptors(additional);
            return await SeedAsync(new PostgreSqlPaymentContext(options.Options), recorder, null);
        }

        private static async Task<Fixture> SeedAsync(PaymentContext db, CommandRecorder recorder, IAsyncDisposable? connection)
        {
            var role = new Role { Name = "Synthetic actor", Description = "Synthetic actor" };
            foreach (var section in new[] { RolePermissionCatalog.Acts, RolePermissionCatalog.Accounts,
                         RolePermissionCatalog.Clients, RolePermissionCatalog.LegalEntities, RolePermissionCatalog.Contracts,
                         RolePermissionCatalog.CompanyProfile, RolePermissionCatalog.Payments })
            {
                role.Permissions.Add(new RolePermission
                {
                    Section = section,
                    CanView = section is not RolePermissionCatalog.CompanyProfile and not RolePermissionCatalog.Payments,
                    CanCreate = section == RolePermissionCatalog.Acts,
                    CanEdit = section == RolePermissionCatalog.Acts,
                    ViewScope = DataVisibilityScope.Own,
                });
            }
            var actor = new User { Email = "actor@example.test", FullName = "Synthetic actor", PasswordHash = "test", IsActive = true, IsApproved = true, Role = role };
            var other = new User { Email = "other@example.test", FullName = "Synthetic other", PasswordHash = "test", IsActive = true, IsApproved = true, Role = new Role { Name = "Synthetic other", Description = "Synthetic other" } };
            db.Users.AddRange(actor, other);
            await db.SaveChangesAsync();
            var own = new Client { Name = "Synthetic own client", ResponsibleUserId = actor.Id, IsActive = true };
            var foreign = new Client { Name = "Synthetic foreign client", ResponsibleUserId = other.Id, IsActive = true };
            var company = new LegalEntity { ShortName = "Synthetic foreign company", ResponsibleUserId = other.Id };
            var contract = new Contract { Number = "SYN-CONTRACT", Date = new DateTime(2026, 10, 24), BillingMode = ContractBillingMode.Actual, IsOpenEnded = true };
            db.Clients.AddRange(own, foreign); db.LegalEntities.Add(company); db.Contracts.Add(contract);
            await db.SaveChangesAsync();
            db.ClientContracts.Add(new ClientContract { ClientId = foreign.Id, ContractId = contract.Id });
            var invoice = new Payment
            {
                Account = "SYN-INVOICE",
                Amount = 123.45m,
                Date = new DateTime(2026, 10, 24),
                ClientId = foreign.Id,
                DocumentKind = DocumentKind.Invoice,
                Type = PaymentType.Income,
                Status = PaymentStatus.Pending,
                Invoice = new Invoice { Number = "SYN-INVOICE", Date = new DateTime(2026, 10, 24) },
            };
            var existing = new Act
            {
                Number = "SYN-EXISTING",
                Date = new DateTime(2026, 10, 23),
                ClientId = own.Id,
                ResponsibleId = actor.Id,
                Amount = 5m,
                Status = ActStatus.Created,
                CreationMode = ActCreationMode.Manual,
                Comment = "Preserved original",
                UpdatedAt = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc),
            };
            db.Payments.Add(invoice); db.Acts.Add(existing);
            await db.SaveChangesAsync();
            db.InvoiceLineItems.Add(new InvoiceLineItem
            {
                PaymentId = invoice.Id,
                SortOrder = 0,
                Name = "Synthetic confidential line",
                Quantity = 1m,
                Price = 123.45m,
                Unit = "шт"
            });
            await db.SaveChangesAsync();
            return new Fixture(connection, db, recorder, actor, other, own, foreign, company, invoice, contract, existing);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (connection is not null) await connection.DisposeAsync();
        }
    }

    public class NotificationRecorder : DispatchProxy
    {
        public List<string> Calls { get; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.Add(targetMethod!.Name);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default)
            => Task.FromResult(new List<string>());

        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default)
            => Task.FromResult(new List<string>());
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
