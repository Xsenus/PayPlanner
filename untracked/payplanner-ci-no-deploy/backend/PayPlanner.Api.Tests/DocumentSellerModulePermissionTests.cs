using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DocumentSellerModulePermissionTests
{
    [Theory]
    [InlineData("invoice", "first-file")]
    [InlineData("invoice", "on-create")]
    [InlineData("act", "first-file")]
    [InlineData("act", "on-create")]
    [InlineData("contract", "first-file")]
    [InlineData("contract", "on-create")]
    public async Task CreateAndPrint_UseDocumentRightsWithoutCompanyProfileView_AndRejectForeignSeller(string kind, string timing)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var section = kind switch { "invoice" => RolePermissionCatalog.Accounts, "act" => RolePermissionCatalog.Acts, _ => RolePermissionCatalog.Contracts };
        var permission = new RolePermission { Section = section, CanView = true, CanCreate = true, CanEdit = true, CanExport = true, ViewScope = DataVisibilityScope.All };
        var role = new Role
        {
            Name = "financial-document-only",
            Permissions = [permission,
            new RolePermission { Section = RolePermissionCatalog.Clients, CanView = true, ViewScope = DataVisibilityScope.All },
            new RolePermission { Section = RolePermissionCatalog.CompanyProfile, CanView = false, CanEdit = false, ViewScope = DataVisibilityScope.Own }]
        };
        var user = new User { Email = "seller-document-only@example.local", FullName = "Автор документа", PasswordHash = "synthetic", Role = role, IsActive = true, IsApproved = true };
        var client = new Client { Name = "Контрагент", ResponsibleUser = user, IsActive = true };
        var own = new LegalEntity { ShortName = "Разрешённый продавец", Inn = "7707083893", Kpp = "770701001", ProfileStatus = CompanyProfileStatuses.Active };
        var foreign = new LegalEntity { ShortName = "Чужая компания", Inn = "7700000000", ProfileStatus = CompanyProfileStatuses.Active };
        db.Users.Add(user); db.Clients.Add(client); db.LegalEntities.AddRange(own, foreign);
        await db.SaveChangesAsync();
        db.SystemSettings.AddRange(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{own.Id}]" },
            new SystemSetting { Key = DocumentSellerSnapshotPolicy.SettingKey, Value = timing },
            new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
        await db.SaveChangesAsync();
        var context = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, role.Name)], "Test"))
            }
        };
        var requirements = new NoopRequirements();
        var invoices = new InvoicesController(db, null!, requirements) { ControllerContext = context };
        var acts = new ActsController(db, requirements) { ControllerContext = context };
        var contracts = new ContractsController(db, requirements) { ControllerContext = context };

        async Task<IActionResult> Create(int sellerId)
        {
            if (kind == "invoice") return (await invoices.Create(new UpsertInvoiceRequest
            {
                Number = "SELLER-INVOICE",
                Date = new DateTime(2026, 10, 24),
                ClientId = client.Id,
                Amount = 100m,
                SellerCompanyId = sellerId
            }, default)).Result!;
            if (kind == "act") return (await acts.Create(new UpsertActRequest
            {
                Number = "SELLER-ACT",
                Date = new DateTime(2026, 10, 24),
                ClientId = client.Id,
                Amount = 100m,
                SellerCompanyId = sellerId
            }, default)).Result!;
            return (await contracts.Create(new UpsertContractRequest
            {
                Number = "SELLER-CONTRACT",
                Date = new DateTime(2026, 10, 24),
                IsOpenEnded = true,
                Amount = 100m,
                SellerCompanyId = sellerId,
                Clients = [new() { ClientId = client.Id }]
            }, default)).Result!;
        }

        // Controller-level typed error is mapped to 400 by DocumentSellerExceptionFilter.
        await Assert.ThrowsAsync<SellerSelectionException>(async () => { await Create(foreign.Id); });
        Assert.Empty(await db.Payments.ToArrayAsync()); Assert.Empty(await db.Acts.ToArrayAsync()); Assert.Empty(await db.Contracts.ToArrayAsync());
        // A later HTTP request receives a fresh tracker; no failed request's
        // uncommitted invoice graph is carried into the successful creation.
        db.ChangeTracker.Clear();
        Assert.IsType<CreatedResult>(await Create(own.Id));
        IDocumentSeller document = kind switch
        {
            "invoice" => await db.Payments.AsNoTracking().SingleAsync(),
            "act" => await db.Acts.AsNoTracking().SingleAsync(),
            _ => await db.Contracts.AsNoTracking().SingleAsync(),
        };
        Assert.Equal(timing, document.SellerSnapshotPolicy);
        Assert.Equal(timing == DocumentSellerSnapshotPolicy.OnCreate, !string.IsNullOrWhiteSpace(document.SellerSnapshotJson));
        var id = document switch { Payment p => p.Id, Act a => a.Id, Contract c => c.Id, _ => throw new InvalidOperationException() };
        async Task<IActionResult> Print() => kind switch
        {
            "invoice" => await invoices.GetDocument(id, "word", ct: default),
            "act" => await acts.GetDocument(id, "word", false, default),
            _ => await contracts.GetDocument(id, "word", ct: default),
        };
        Assert.IsAssignableFrom<FileResult>(await Print());
        Assert.False(role.Permissions.Single(p => p.Section == RolePermissionCatalog.CompanyProfile).CanView);
        var frozen = kind switch
        {
            "invoice" => await db.Payments.AsNoTracking().Where(p => p.Id == id).Select(p => p.SellerSnapshotJson).SingleAsync(),
            "act" => await db.Acts.AsNoTracking().Where(p => p.Id == id).Select(p => p.SellerSnapshotJson).SingleAsync(),
            _ => await db.Contracts.AsNoTracking().Where(p => p.Id == id).Select(p => p.SellerSnapshotJson).SingleAsync(),
        };
        Assert.Contains("Разрешённый продавец", frozen);
        (await db.RolePermissions.SingleAsync(p => p.RoleId == role.Id && p.Section == section)).CanExport = false;
        await db.SaveChangesAsync();
        Assert.IsType<ForbidResult>(await Print());
    }

    private sealed class NoopRequirements : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }
}
