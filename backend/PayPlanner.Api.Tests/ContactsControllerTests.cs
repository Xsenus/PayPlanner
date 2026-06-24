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

    [Fact]
    public async Task GetAll_WithAllScope_ReturnsContactsAcrossResponsibles()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(
            db,
            "contacts-all-scope",
            canView: true,
            canCreate: true,
            canEdit: true,
            canDelete: true,
            canExport: true,
            viewScope: DataVisibilityScope.All);
        var otherUser = await CreateClientUserAsync(db, "contacts-all-scope-other", canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: true);
        await CreateClientAsync(db, otherUser.Id, "Other Responsible Client", "222");
        await CreateExternalContactAsync(db, otherUser.Id, "Other Responsible External", "333");

        var action = await CreateController(db, user.Id).GetAll(
            scope: null,
            search: null,
            legalEntityId: null,
            includeInactive: true,
            syncOnly: null,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var items = Assert.IsType<List<ContactDirectoryItemDto>>(ok.Value);
        Assert.Contains(items, item => item.FullName == "Other Responsible Client");
        Assert.Contains(items, item => item.FullName == "Other Responsible External");
    }

    [Fact]
    public async Task UpdateClientContact_WithAllScope_AllowsOtherResponsibleClient()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(
            db,
            "contacts-edit-all-scope",
            canView: true,
            canCreate: true,
            canEdit: true,
            canDelete: true,
            canExport: true,
            viewScope: DataVisibilityScope.All);
        var otherUser = await CreateClientUserAsync(db, "contacts-edit-all-scope-other", canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: true);
        var client = await CreateClientAsync(db, otherUser.Id, "Other Client Before Update", "222");

        var action = await CreateController(db, user.Id).UpdateClientContact(
            client.Id,
            BuildClientContactRequest("Other Client After Update"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var item = Assert.IsType<ContactDirectoryItemDto>(ok.Value);
        Assert.Equal("Other Client After Update", item.FullName);

        var stored = await db.Clients.AsNoTracking().SingleAsync(entity => entity.Id == client.Id);
        Assert.Equal("Other Client After Update", stored.Name);
        Assert.Equal(otherUser.Id, stored.ResponsibleUserId);
    }

    [Fact]
    public async Task UpdateExternalContact_WithAllScope_AllowsOtherResponsibleContact()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(
            db,
            "contacts-external-all-scope",
            canView: true,
            canCreate: true,
            canEdit: true,
            canDelete: true,
            canExport: true,
            viewScope: DataVisibilityScope.All);
        var otherUser = await CreateClientUserAsync(db, "contacts-external-all-scope-other", canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: true);
        var contact = await CreateExternalContactAsync(db, otherUser.Id, "Other External Before Update", "333");

        var action = await CreateController(db, user.Id).UpdateExternalContact(
            contact.Id,
            BuildExternalContactRequest("Other External After Update"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var item = Assert.IsType<ContactDirectoryItemDto>(ok.Value);
        Assert.Equal("Other External After Update", item.FullName);

        var stored = await db.ExternalContacts.AsNoTracking().SingleAsync(entity => entity.Id == contact.Id);
        Assert.Equal("Other External After Update", stored.FullName);
        Assert.Equal(otherUser.Id, stored.ResponsibleUserId);
    }

    [Fact]
    public async Task UpdateClientContact_WithHiddenLegalEntity_ReturnsBadRequestAndKeepsOriginalLink()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(
            db,
            "contacts-client-hidden-legal",
            canView: true,
            canCreate: true,
            canEdit: true,
            canDelete: true,
            canExport: true,
            viewScope: DataVisibilityScope.All,
            legalEntityViewScope: DataVisibilityScope.Own);
        var otherUser = await CreateClientUserAsync(db, "contacts-client-hidden-legal-other", canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: true);
        var client = await CreateClientAsync(db, user.Id, "Client before hidden legal", "222");
        var visibleLegal = await CreateLegalEntityAsync(db, user.Id, "Visible legal");
        var hiddenLegal = await CreateLegalEntityAsync(db, otherUser.Id, "Hidden legal");
        client.LegalEntityId = visibleLegal.Id;
        await db.SaveChangesAsync();

        var request = BuildClientContactRequest("Client after hidden legal");
        request.LegalEntityId = hiddenLegal.Id;

        var action = await CreateController(db, user.Id).UpdateClientContact(client.Id, request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("юридическое лицо", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);

        var stored = await db.Clients.AsNoTracking().SingleAsync(entity => entity.Id == client.Id);
        Assert.Equal("Client before hidden legal", stored.Name);
        Assert.Equal(visibleLegal.Id, stored.LegalEntityId);
    }

    [Fact]
    public async Task CreateExternalContact_WithHiddenLegalEntity_ReturnsBadRequestAndDoesNotCreateContact()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(
            db,
            "contacts-external-hidden-legal",
            canView: true,
            canCreate: true,
            canEdit: true,
            canDelete: true,
            canExport: true,
            viewScope: DataVisibilityScope.All,
            legalEntityViewScope: DataVisibilityScope.Own);
        var otherUser = await CreateClientUserAsync(db, "contacts-external-hidden-legal-other", canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: true);
        var hiddenLegal = await CreateLegalEntityAsync(db, otherUser.Id, "Hidden external legal");

        var request = BuildExternalContactRequest("External with hidden legal");
        request.LegalEntityId = hiddenLegal.Id;

        var action = await CreateController(db, user.Id).CreateExternalContact(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("юридическое лицо", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(await db.ExternalContacts.AnyAsync(contact => contact.FullName == "External with hidden legal"));
    }

    [Fact]
    public async Task UpdateExternalContact_WithHiddenLegalEntity_ReturnsBadRequestAndKeepsOriginalLink()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateClientUserAsync(
            db,
            "contacts-external-update-hidden-legal",
            canView: true,
            canCreate: true,
            canEdit: true,
            canDelete: true,
            canExport: true,
            viewScope: DataVisibilityScope.All,
            legalEntityViewScope: DataVisibilityScope.Own);
        var otherUser = await CreateClientUserAsync(db, "contacts-external-update-hidden-legal-other", canView: true, canCreate: true, canEdit: true, canDelete: true, canExport: true);
        var visibleLegal = await CreateLegalEntityAsync(db, user.Id, "Visible external legal");
        var hiddenLegal = await CreateLegalEntityAsync(db, otherUser.Id, "Hidden external update legal");
        var contact = await CreateExternalContactAsync(db, user.Id, "External before hidden legal", legalEntityId: visibleLegal.Id);

        var request = BuildExternalContactRequest("External after hidden legal");
        request.LegalEntityId = hiddenLegal.Id;

        var action = await CreateController(db, user.Id).UpdateExternalContact(contact.Id, request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("юридическое лицо", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);

        var stored = await db.ExternalContacts.AsNoTracking().SingleAsync(entity => entity.Id == contact.Id);
        Assert.Equal("External before hidden legal", stored.FullName);
        Assert.Equal(visibleLegal.Id, stored.LegalEntityId);
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
        string phone = "79994445566",
        int? legalEntityId = null)
    {
        var contact = new ExternalContact
        {
            FullName = name,
            Phone = phone,
            PhonesJson = System.Text.Json.JsonSerializer.Serialize(new[] { phone }),
            ResponsibleUserId = responsibleUserId,
            LegalEntityId = legalEntityId,
            IsActive = true,
            SyncToPhonebook = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.ExternalContacts.Add(contact);
        await db.SaveChangesAsync();
        return contact;
    }

    private static async Task<LegalEntity> CreateLegalEntityAsync(
        PaymentContext db,
        int responsibleUserId,
        string shortName)
    {
        var entity = new LegalEntity
        {
            ShortName = shortName,
            ResponsibleUserId = responsibleUserId,
            CreatedAt = DateTime.UtcNow,
        };
        db.LegalEntities.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    private static async Task<User> CreateClientUserAsync(
        PaymentContext db,
        string name,
        bool canView,
        bool canCreate,
        bool canEdit,
        bool canDelete,
        bool canExport,
        string viewScope = DataVisibilityScope.Own,
        string? legalEntityViewScope = null)
    {
        var permissions = new List<RolePermission>
        {
            new()
            {
                Section = RolePermissionCatalog.Clients,
                CanView = canView,
                CanCreate = canCreate,
                CanEdit = canEdit,
                CanDelete = canDelete,
                CanExport = canExport,
                ViewScope = viewScope,
            },
        };
        if (!string.IsNullOrWhiteSpace(legalEntityViewScope))
        {
            permissions.Add(new RolePermission
            {
                Section = RolePermissionCatalog.LegalEntities,
                CanView = true,
                ViewScope = legalEntityViewScope,
            });
        }

        var role = new Role
        {
            Name = $"{name}-{Guid.NewGuid():N}",
            Description = name,
            Permissions = permissions,
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
