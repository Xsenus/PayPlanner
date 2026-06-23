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
using Xunit;

namespace PayPlanner.Api.Tests;

public class ContactsControllerTests
{
    [Fact]
    public async Task Endpoints_RequireClientSectionPermissions()
    {
        await using var db = await CreateContextAsync();
        var noViewUser = await CreateClientUserAsync(db, "contacts-no-view", canView: false, canCreate: true, canEdit: true, canDelete: true, canExport: true);
        var noCreateUser = await CreateClientUserAsync(db, "contacts-no-create", canView: true, canCreate: false, canEdit: true, canDelete: true, canExport: true);
        var noEditUser = await CreateClientUserAsync(db, "contacts-no-edit", canView: true, canCreate: true, canEdit: false, canDelete: true, canExport: true);
        var noDeleteUser = await CreateClientUserAsync(db, "contacts-no-delete", canView: true, canCreate: true, canEdit: true, canDelete: false, canExport: true);
        var noExportUser = await CreateClientUserAsync(db, "contacts-no-export", canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: false);
        var client = await CreateClientAsync(db, noEditUser.Id);
        var externalForEdit = await CreateExternalContactAsync(db, noEditUser.Id);
        var externalForDelete = await CreateExternalContactAsync(db, noDeleteUser.Id);

        var listResult = await CreateController(db, noViewUser.Id).GetAll(
            scope: null,
            search: null,
            legalEntityId: null,
            includeInactive: false,
            syncOnly: null,
            ct: CancellationToken.None);
        var createResult = await CreateController(db, noCreateUser.Id).CreateExternalContact(
            BuildExternalContactRequest("Forbidden create"),
            CancellationToken.None);
        var updateClientResult = await CreateController(db, noEditUser.Id).UpdateClientContact(
            client.Id,
            BuildClientContactRequest("Forbidden client update"),
            CancellationToken.None);
        var updateExternalResult = await CreateController(db, noEditUser.Id).UpdateExternalContact(
            externalForEdit.Id,
            BuildExternalContactRequest("Forbidden external update"),
            CancellationToken.None);
        var deleteResult = await CreateController(db, noDeleteUser.Id).DeleteExternalContact(
            externalForDelete.Id,
            CancellationToken.None);
        var exportResult = await CreateController(db, noExportUser.Id).DownloadYealinkXml(CancellationToken.None);

        Assert.IsType<ForbidResult>(listResult.Result);
        Assert.IsType<ForbidResult>(createResult.Result);
        Assert.IsType<ForbidResult>(updateClientResult.Result);
        Assert.IsType<ForbidResult>(updateExternalResult.Result);
        Assert.IsType<ForbidResult>(deleteResult);
        Assert.IsType<ForbidResult>(exportResult);
    }

    [Fact]
    public async Task DownloadYealinkXml_ExportsOnlyVisibleContacts_ForUserWithExportPermission()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(db, "contacts-export", canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: true);
        var otherUser = await CreateClientUserAsync(db, "contacts-other", canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: true);
        await CreateClientAsync(db, user.Id, "Visible Client", "111");
        await CreateClientAsync(db, otherUser.Id, "Hidden Client", "222");
        await CreateExternalContactAsync(db, user.Id, "Visible External", "333");
        await CreateExternalContactAsync(db, otherUser.Id, "Hidden External", "444");
        var controller = CreateController(db, user.Id);

        var action = await controller.DownloadYealinkXml(CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(action);
        var xml = System.Text.Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("Visible Client", xml);
        Assert.Contains("Visible External", xml);
        Assert.DoesNotContain("Hidden Client", xml);
        Assert.DoesNotContain("Hidden External", xml);
    }

    private static UpsertExternalContactRequest BuildExternalContactRequest(string name)
        => new()
        {
            FullName = name,
            Phone = "79990000001",
            Phones = ["79990000001"],
            IsActive = true,
            SyncToPhonebook = true,
        };

    private static UpdateClientContactRequest BuildClientContactRequest(string name)
        => new()
        {
            FullName = name,
            Phone = "79990000002",
            Phones = ["79990000002"],
            SyncToPhonebook = true,
        };

    private static async Task<Client> CreateClientAsync(
        PaymentContext db,
        int responsibleUserId,
        string name = "Client Contact",
        string phone = "79991112233")
    {
        var client = new Client
        {
            Name = name,
            Phone = phone,
            Company = "Test Company",
            ResponsibleUserId = responsibleUserId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    private static async Task<ExternalContact> CreateExternalContactAsync(
        PaymentContext db,
        int responsibleUserId,
        string name = "External Contact",
        string phone = "79994445566")
    {
        var contact = new ExternalContact
        {
            FullName = name,
            Phone = phone,
            PhonesJson = System.Text.Json.JsonSerializer.Serialize(new[] { phone }),
            ResponsibleUserId = responsibleUserId,
            IsActive = true,
            SyncToPhonebook = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.ExternalContacts.Add(contact);
        await db.SaveChangesAsync();
        return contact;
    }

    private static async Task<User> CreateClientUserAsync(
        PaymentContext db,
        string name,
        bool canView,
        bool canCreate,
        bool canEdit,
        bool canDelete,
        bool canExport)
    {
        var role = new Role
        {
            Name = $"{name}-{Guid.NewGuid():N}",
            Description = name,
            Permissions =
            {
                new RolePermission
                {
                    Section = RolePermissionCatalog.Clients,
                    CanView = canView,
                    CanCreate = canCreate,
                    CanEdit = canEdit,
                    CanDelete = canDelete,
                    CanExport = canExport,
                    ViewScope = DataVisibilityScope.All,
                },
            },
        };
        var user = new User
        {
            Email = $"{name}-{Guid.NewGuid():N}@example.test",
            FullName = name,
            PasswordHash = "test",
            Role = role,
            IsActive = true,
            IsApproved = true,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static ContactsController CreateController(PaymentContext db, int userId)
        => new(db, new NoopFormRequirementsService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                        new Claim(ClaimTypes.Name, "Test user"),
                    ], "Test")),
                },
            },
        };

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

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default)
            => Task.FromResult(new List<string>());

        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default)
            => Task.FromResult(new List<string>());
    }
}
