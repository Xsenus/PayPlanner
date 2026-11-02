using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class InvoicesControllerListTests
{
    [Fact]
    public async Task OwnershipBoundary_PaymentMutationsAndInvoiceLinkingRefuseConflictedAggregate()
    {
        await using var db = await CreateContextAsync();
        var seed = await SeedOwnershipBoundaryAsync(db);
        seed.FirstUser.Role!.Permissions.Add(new RolePermission
        {
            Section = RolePermissionCatalog.Payments,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            CanDelete = true,
            ViewScope = DataVisibilityScope.Own
        });
        await db.SaveChangesAsync();
        var controller = new PaymentsController(db, new BoundaryFormRequirements())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = BoundaryPrincipal(seed.FirstUser) } },
        };
        var before = await BoundaryMoneySnapshotAsync(db);
        Assert.IsType<NotFoundResult>(await controller.UpdateStatus(seed.First.Id,
            new UpdatePaymentStatusRequest { Status = "Completed" }, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Update(seed.First.Id,
            new Payment { Amount = 999, InvoiceId = seed.First.InvoiceId }, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Delete(seed.First.Id, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await PaymentOperationKeyTestSetup.CreateAsync(controller, db, new Payment
        {
            InvoiceId = seed.First.InvoiceId,
            ClientId = seed.First.ClientId,
            Amount = 1,
            Type = PaymentType.Expense,
            Date = seed.First.Date
        }, CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.RepairInvoiceLinks(CancellationToken.None));
        controller.ControllerContext.HttpContext.User = BoundaryPrincipal(seed.Admin);
        Assert.IsType<ConflictObjectResult>(await controller.RepairInvoiceLinks(CancellationToken.None));
        Assert.Equal(before, await BoundaryMoneySnapshotAsync(db));
    }

    [Fact]
    public async Task OwnershipBoundary_ImplicitInvoiceLinkCannotBypassForeignDocumentAccess()
    {
        await using var db = await CreateContextAsync();
        var seed = await SeedOwnershipBoundaryAsync(db);
        seed.Second.InvoiceId = null;
        seed.Second.Invoice = new Invoice { Number = seed.First.Account!, Date = seed.First.AccountDate!.Value };
        seed.FirstUser.Role!.Permissions.Add(new RolePermission
        {
            Section = RolePermissionCatalog.Payments,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            ViewScope = DataVisibilityScope.Own
        });
        await db.SaveChangesAsync();
        var controller = new PaymentsController(db, new BoundaryFormRequirements())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = BoundaryPrincipal(seed.FirstUser) } },
        };
        var before = await BoundaryMoneySnapshotAsync(db);
        Assert.IsType<NotFoundResult>(await PaymentOperationKeyTestSetup.CreateAsync(controller, db, new Payment
        {
            ClientId = seed.Second.ClientId,
            Account = seed.Second.Account,
            AccountDate = seed.Second.AccountDate,
            Type = seed.Second.Type,
            Date = seed.Second.Date,
            Amount = 100,
            Description = "Must not bind a foreign invoice"
        }, CancellationToken.None));
        Assert.Equal(before, await BoundaryMoneySnapshotAsync(db));
    }

    private sealed class BoundaryFormRequirements : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }

    [Fact]
    public async Task OwnershipBoundary_PostgreSqlExecutesVisibilityAndConflictQueries()
    {
        var configured = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true", "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        const string prefix = "pp_invoice_owner_";
        var database = prefix + Guid.NewGuid().ToString("N");
        var builder = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
            builder.Database = database;
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(builder.ConnectionString).Options);
            await db.Database.EnsureCreatedAsync();
            var seed = await SeedOwnershipBoundaryAsync(db);
            var access = await db.ResolveDataVisibilityAsync(BoundaryPrincipal(seed.FirstUser), RolePermissionCatalog.Accounts);
            Assert.Equal(new[] { seed.First.Id }, await db.Payments.ApplyPaymentVisibility(access).Select(p => p.Id).ToArrayAsync());
            Assert.True(await db.Payments.HasAmbiguousInvoiceLinksAsync());
            Assert.Empty(await db.Payments.ApplyUnambiguousInvoiceLinks().ToArrayAsync());
            seed.Second.DocumentKind = DocumentKind.Payment;
            seed.Second.ClientId = null;
            seed.Second.Type = seed.First.Type;
            await db.SaveChangesAsync();
            Assert.False(await db.Payments.HasAmbiguousInvoiceLinksAsync());
            Assert.Equal(2, await db.Payments.ApplyPaymentVisibility(access).ApplyUnambiguousInvoiceLinks().CountAsync());
            // Also exercise the correlated legacy-act scope on the deployed database provider.
            db.Acts.Add(new Act
            {
                Number = "PG-OWN-ACT",
                InvoiceId = seed.First.Id,
                ClientId = seed.First.ClientId,
                Date = seed.First.Date,
                Amount = 1
            });
            await db.SaveChangesAsync();
            Assert.Single(await db.Acts.ApplyInvoiceActOwnership(db.Payments).ToArrayAsync());
        }
        finally
        {
            Assert.StartsWith(prefix, database, StringComparison.Ordinal);
            await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(admin, database);
        }
    }

    [Fact]
    public async Task OwnershipBoundary_ForeignActsAreNotExposedThroughNumberOrDirectLink()
    {
        await using var db = await CreateContextAsync();
        var seed = await SeedOwnershipBoundaryAsync(db);
        seed.Second.InvoiceId = null;
        seed.Second.Invoice = new Invoice { Number = seed.First.Account!, Date = seed.First.AccountDate!.Value };
        await db.SaveChangesAsync();
        db.Acts.AddRange(
            new Act { Number = "OWN-LEGACY-ACT", InvoiceNumber = seed.First.Account, ClientId = seed.First.ClientId, Date = seed.First.Date, Amount = 1 },
            new Act { Number = "FOREIGN-LEGACY-ACT", InvoiceNumber = seed.First.Account, ClientId = seed.Second.ClientId, Date = seed.First.Date, Amount = 2 },
            new Act { Number = "FOREIGN-DIRECT-ACT", InvoiceId = seed.First.Id, ClientId = seed.Second.ClientId, Date = seed.First.Date, Amount = 3 });
        await db.SaveChangesAsync();
        var controller = CreateController(db);
        controller.ControllerContext.HttpContext.User = BoundaryPrincipal(seed.FirstUser);
        var detail = Assert.IsType<OkObjectResult>((await controller.GetById(seed.First.Id, CancellationToken.None)).Result);
        var json = JsonSerializer.Serialize(detail.Value);
        Assert.Contains("OWN-LEGACY-ACT", json);
        Assert.DoesNotContain("FOREIGN-", json);
        var registry = new AccountsController(db) { ControllerContext = controller.ControllerContext };
        var rows = Assert.IsType<OkObjectResult>((await registry.GetRegistry(null, null, null, null, null, null, null)).Result);
        var registryJson = JsonSerializer.Serialize(rows.Value, new JsonSerializerOptions
        { ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles });
        Assert.Contains("OWN-LEGACY-ACT", registryJson);
        Assert.DoesNotContain("FOREIGN-", registryJson);
    }

    [Fact]
    public async Task OwnershipBoundary_SameClientDifferentSellerCompaniesAreQuarantined()
    {
        await using var db = await CreateContextAsync();
        var seed = await SeedOwnershipBoundaryAsync(db, conflicting: false);
        var sellerA = new LegalEntity { ShortName = "Seller A" };
        var sellerB = new LegalEntity { ShortName = "Seller B" };
        db.LegalEntities.AddRange(sellerA, sellerB);
        await db.SaveChangesAsync();
        seed.First.SellerCompanyId = sellerA.Id;
        seed.Second.SellerCompanyId = sellerB.Id;
        await db.SaveChangesAsync();
        Assert.True(await db.Payments.HasAmbiguousInvoiceLinksAsync());
        Assert.Empty(await db.Payments.ApplyUnambiguousInvoiceLinks().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnershipBoundary_SharedInvoiceDoesNotGrantForeignPaymentVisibility(bool secondOwner)
    {
        await using var db = await CreateContextAsync();
        var seed = await SeedOwnershipBoundaryAsync(db);
        var user = secondOwner ? seed.SecondUser : seed.FirstUser;
        var access = await db.ResolveDataVisibilityAsync(BoundaryPrincipal(user), RolePermissionCatalog.Accounts);
        var visible = await db.Payments.AsNoTracking().ApplyPaymentVisibility(access).Select(p => p.Id).ToArrayAsync();
        Assert.Contains(secondOwner ? seed.Second.Id : seed.First.Id, visible);
        Assert.DoesNotContain(secondOwner ? seed.First.Id : seed.Second.Id, visible);
    }

    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    [InlineData("admin")]
    public async Task OwnershipBoundary_ConflictingDocumentIsUnavailableAcrossInvoiceReadAndWriteRoutes(string role)
    {
        await using var db = await CreateContextAsync();
        var seed = await SeedOwnershipBoundaryAsync(db);
        var user = role == "first" ? seed.FirstUser : role == "second" ? seed.SecondUser : seed.Admin;
        var controller = CreateController(db);
        controller.ControllerContext.HttpContext.User = BoundaryPrincipal(user);
        var registry = new AccountsController(db) { ControllerContext = controller.ControllerContext };
        var before = await BoundaryMoneySnapshotAsync(db);
        foreach (var id in new[] { seed.First.Id, seed.Second.Id })
        {
            Assert.IsType<NotFoundResult>((await controller.GetById(id, CancellationToken.None)).Result);
            Assert.IsType<NotFoundResult>(await controller.GetPreview(id));
            foreach (var format in new[] { "html", "pdf", "word" })
                Assert.IsType<NotFoundResult>(await controller.GetDocument(id, format));
            Assert.IsType<NotFoundResult>(await controller.Delete(id, CancellationToken.None));
        }
        var rows = Assert.IsType<AccountsPageResponse>(Assert.IsType<OkObjectResult>((await registry.GetRegistry(
            null, null, null, null, null, null, null)).Result).Value);
        Assert.Empty(rows.Items);
        var invoices = ExtractInvoiceItems(Assert.IsType<OkObjectResult>(await controller.Get(
            null, null, null, null, null, null, null, null, null, null, null, null)));
        Assert.Empty(invoices);
        Assert.Equal(before, await BoundaryMoneySnapshotAsync(db));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OwnershipBoundary_PreservesAllowedInstallmentAndSameOwnerRefund(bool missingChildOwner, bool refund)
    {
        await using var db = await CreateContextAsync();
        var seed = await SeedOwnershipBoundaryAsync(db, conflicting: false);
        seed.Second.DocumentKind = DocumentKind.Payment;
        seed.Second.ClientId = missingChildOwner ? null : seed.First.ClientId;
        if (refund) seed.First.Type = PaymentType.Income;
        seed.Second.Type = refund ? PaymentType.Expense : seed.First.Type;
        // Inheritance through the permitted document remains sufficient without payment-journal permission.
        seed.First.Responsibles.Add(new PaymentResponsible { UserId = seed.FirstUser.Id });
        await db.SaveChangesAsync();
        var before = await BoundaryMoneySnapshotAsync(db);
        var principal = BoundaryPrincipal(seed.FirstUser);
        var access = await db.ResolveDataVisibilityAsync(principal, RolePermissionCatalog.Accounts);
        Assert.True(await db.Payments.AsNoTracking().ApplyPaymentVisibility(access).AnyAsync(p => p.Id == seed.Second.Id));
        var controller = CreateController(db);
        controller.ControllerContext.HttpContext.User = principal;
        Assert.IsType<OkObjectResult>((await controller.GetById(seed.First.Id, CancellationToken.None)).Result);
        Assert.IsType<ContentResult>(await controller.GetDocument(seed.First.Id, "html"));
        Assert.Equal(before, await BoundaryMoneySnapshotAsync(db));
    }

    private static ClaimsPrincipal BoundaryPrincipal(User user) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "OwnershipFixture"));

    private static async Task<string> BoundaryMoneySnapshotAsync(PaymentContext db)
        => JsonSerializer.Serialize(await db.Payments.AsNoTracking().OrderBy(p => p.Id)
            .Select(p => new
            {
                p.Id,
                p.InvoiceId,
                p.ClientId,
                p.LegalEntityId,
                p.Type,
                p.DocumentKind,
                p.Amount,
                p.PaidAmount,
                p.Status,
                p.Account,
                p.AccountDate,
                p.Date
            }).ToArrayAsync());

    private static async Task<(Payment First, Payment Second, User FirstUser, User SecondUser, User Admin)>
        SeedOwnershipBoundaryAsync(PaymentContext db, bool conflicting = true)
    {
        var role = new Role { Name = "ownership-own", Description = "Own documents" };
        role.Permissions.Add(new RolePermission
        {
            Section = RolePermissionCatalog.Accounts,
            CanView = true,
            CanEdit = true,
            CanDelete = true,
            CanExport = true,
            ViewScope = DataVisibilityScope.Own
        });
        var firstUser = new User { Email = "ownership-first@fixture.local", FullName = "First", PasswordHash = "unused", Role = role, IsActive = true, IsApproved = true };
        var secondUser = new User { Email = "ownership-second@fixture.local", FullName = "Second", PasswordHash = "unused", Role = role, IsActive = true, IsApproved = true };
        var admin = new User
        {
            Email = "ownership-admin@fixture.local",
            FullName = "Admin",
            PasswordHash = "unused",
            Role = new Role { Name = "admin", Description = "Admin" },
            IsActive = true,
            IsApproved = true
        };
        db.Users.AddRange(firstUser, secondUser, admin);
        await db.SaveChangesAsync();
        var firstClient = await CreateClientAsync(db, "First synthetic owner");
        var secondClient = await CreateClientAsync(db, "Second synthetic owner");
        firstClient.ResponsibleUserId = firstUser.Id;
        secondClient.ResponsibleUserId = secondUser.Id;
        var first = await CreateInvoiceDocumentAsync(db, "OWN-SAME", new DateTime(2026, 6, 16), 2500m);
        first.ClientId = firstClient.Id;
        first.Type = PaymentType.Expense;
        var second = CreatePayment(first.Date, 1000m, "Second document with same number");
        second.DocumentKind = DocumentKind.Invoice;
        second.InvoiceId = first.InvoiceId;
        second.Account = first.Account;
        second.AccountDate = first.AccountDate;
        second.ClientId = conflicting ? secondClient.Id : firstClient.Id;
        second.Type = conflicting ? PaymentType.Income : first.Type;
        db.Payments.Add(second);
        db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
        await db.SaveChangesAsync();
        return (first, second, firstUser, secondUser, admin);
    }
}
