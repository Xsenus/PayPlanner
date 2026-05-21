using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ObjectAuditControllerTests
{
    [Fact]
    public async Task GetEvents_ReturnsFilteredPagedFeed_WhenUserHasAuditAccess()
    {
        await using var db = await CreateContextAsync();

        var role = new Role
        {
            Name = "admin",
            Description = "Administrator",
            CreatedAt = DateTime.UtcNow,
            PermissionsUpdatedAt = DateTime.UtcNow,
        };

        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var user = new User
        {
            Email = "audit@example.com",
            PasswordHash = "hash",
            FullName = "Иван Аудитор",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.ObjectAuditEvents.AddRange(
            new ObjectAuditEvent
            {
                ObjectType = "client",
                ObjectId = 11,
                Action = "Updated",
                Description = "Изменено поле: Телефон",
                ActorUserId = user.Id,
                ActorUserName = "Иван Аудитор",
                CreatedAt = new DateTime(2026, 4, 9, 10, 30, 0, DateTimeKind.Utc),
            },
            new ObjectAuditEvent
            {
                ObjectType = "client",
                ObjectId = 12,
                Action = "Created",
                Description = "Объект создан",
                ActorUserId = user.Id,
                ActorUserName = "Иван Аудитор",
                CreatedAt = new DateTime(2026, 4, 8, 8, 0, 0, DateTimeKind.Utc),
            },
            new ObjectAuditEvent
            {
                ObjectType = "payment",
                ObjectId = 33,
                Action = "Updated",
                Description = "Изменено поле: Сумма",
                ActorUserId = user.Id,
                ActorUserName = "Мария Тестова",
                CreatedAt = new DateTime(2026, 4, 9, 9, 0, 0, DateTimeKind.Utc),
            });

        await db.SaveChangesAsync();

        var controller = CreateController(db, user.Id, user.FullName);

        var result = await controller.GetEvents(
            search: "телефон",
            objectType: "client",
            action: "updated",
            actor: "иван",
            from: new DateTime(2026, 4, 9),
            to: new DateTime(2026, 4, 9),
            page: 1,
            pageSize: 25,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<PagedResponse<ObjectAuditEventDto>>(ok.Value);

        var item = Assert.Single(payload.Items);
        Assert.Equal(1, payload.Total);
        Assert.Equal("client", item.ObjectType);
        Assert.Equal(11, item.ObjectId);
        Assert.Equal("Updated", item.Action);
        Assert.Equal("Иван Аудитор", item.ActorUserName);
    }

    [Fact]
    public async Task GetEvents_ReturnsForbid_WhenUserHasNoAuditAccess()
    {
        await using var db = await CreateContextAsync();
        var controller = new ObjectAuditController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        var result = await controller.GetEvents(
            search: null,
            objectType: null,
            action: null,
            actor: null,
            from: null,
            to: null,
            page: 1,
            pageSize: 25,
            ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task CaseScopedHistory_RequiresVisibleCase()
    {
        await using var db = await CreateContextAsync();

        var role = new Role
        {
            Name = "lawyer",
            Description = "Lawyer",
            CreatedAt = DateTime.UtcNow,
            PermissionsUpdatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Audit,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.Own,
            });

        var currentUser = new User
        {
            Email = "lawyer@example.com",
            PasswordHash = "hash",
            FullName = "Юрист",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var otherUser = new User
        {
            Email = "other@example.com",
            PasswordHash = "hash",
            FullName = "Другой юрист",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Users.AddRange(currentUser, otherUser);
        await db.SaveChangesAsync();

        var visibleClient = new Client
        {
            Name = "Видимый клиент",
            IsActive = true,
            ResponsibleUserId = currentUser.Id,
        };
        var hiddenClient = new Client
        {
            Name = "Чужой клиент",
            IsActive = true,
            ResponsibleUserId = otherUser.Id,
        };
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        var visibleCase = new ClientCase
        {
            ClientId = visibleClient.Id,
            Title = "Видимое дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = currentUser.Id,
            CreatedAt = DateTime.UtcNow,
        };
        var hiddenCase = new ClientCase
        {
            ClientId = hiddenClient.Id,
            Title = "Чужое дело",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = otherUser.Id,
            CreatedAt = DateTime.UtcNow,
        };
        db.ClientCases.AddRange(visibleCase, hiddenCase);
        await db.SaveChangesAsync();

        var visibleDocument = new ClientCaseDocument
        {
            ClientCaseId = visibleCase.Id,
            OriginalFileName = "visible.pdf",
            StoredFileName = "visible.pdf",
            StoragePath = "visible.pdf",
            ContentType = "application/pdf",
            SizeBytes = 10,
            UploadedAt = DateTime.UtcNow,
        };
        var hiddenDocument = new ClientCaseDocument
        {
            ClientCaseId = hiddenCase.Id,
            OriginalFileName = "hidden.pdf",
            StoredFileName = "hidden.pdf",
            StoragePath = "hidden.pdf",
            ContentType = "application/pdf",
            SizeBytes = 10,
            UploadedAt = DateTime.UtcNow,
        };
        db.ClientCaseDocuments.AddRange(visibleDocument, hiddenDocument);
        await db.SaveChangesAsync();

        db.ObjectAuditEvents.AddRange(
            new ObjectAuditEvent
            {
                ObjectType = "client-case-document",
                ObjectId = visibleDocument.Id,
                Action = "Created",
                Description = "Видимый документ",
                ActorUserId = currentUser.Id,
                ActorUserName = currentUser.FullName,
                CreatedAt = DateTime.UtcNow,
            },
            new ObjectAuditEvent
            {
                ObjectType = "client-case-document",
                ObjectId = hiddenDocument.Id,
                Action = "Created",
                Description = "Скрытый документ",
                ActorUserId = otherUser.Id,
                ActorUserName = otherUser.FullName,
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUser.Id, currentUser.FullName);

        var visibleHistoryResult = await controller.GetHistory("client-case-document", visibleDocument.Id, CancellationToken.None);
        var visibleOk = Assert.IsType<OkObjectResult>(visibleHistoryResult.Result);
        var visibleHistory = Assert.IsAssignableFrom<IReadOnlyCollection<ObjectAuditEventDto>>(visibleOk.Value);
        Assert.Single(visibleHistory);

        var hiddenHistoryResult = await controller.GetHistory("client-case-document", hiddenDocument.Id, CancellationToken.None);
        Assert.IsType<ForbidResult>(hiddenHistoryResult.Result);

        var feedResult = await controller.GetEvents(
            search: null,
            objectType: "client-case-document",
            action: null,
            actor: null,
            from: null,
            to: null,
            page: 1,
            pageSize: 25,
            ct: CancellationToken.None);

        var feedOk = Assert.IsType<OkObjectResult>(feedResult.Result);
        var feed = Assert.IsType<PagedResponse<ObjectAuditEventDto>>(feedOk.Value);
        var item = Assert.Single(feed.Items);
        Assert.Equal(visibleDocument.Id, item.ObjectId);
    }

    private static ObjectAuditController CreateController(PaymentContext db, int userId, string userName)
    {
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, userName),
            },
            authenticationType: "TestAuth");

        return new ObjectAuditController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(identity),
                },
            },
        };
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
}
