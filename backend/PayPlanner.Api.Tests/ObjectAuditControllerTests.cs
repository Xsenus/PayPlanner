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
