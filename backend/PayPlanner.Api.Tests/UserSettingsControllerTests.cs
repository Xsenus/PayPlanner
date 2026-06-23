using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using System.Security.Claims;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class UserSettingsControllerTests
{
    [Fact]
    public async Task AdminFilter_ReturnsForbid_ForUserWithoutAdminRoleInDatabase()
    {
        await using var scope = await CreateScopeAsync();
        var user = await CreateUserAsync(scope.Db, "settings-filter-user@example.test");
        var controller = CreateController(scope.Db, user.Id, isAdminClaim: true);
        var context = CreateAdminActionExecutingContext(controller, user.Id);
        var nextCalled = false;

        await controller.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, [], controller));
        });

        Assert.False(nextCalled);
        Assert.IsType<ForbidResult>(context.Result);
    }

    [Fact]
    public async Task AdminFilter_AllowsUserWithAdminRoleInDatabase()
    {
        await using var scope = await CreateScopeAsync();
        var user = await CreateUserAsync(scope.Db, "settings-filter-admin@example.test", roleName: "admin");
        var controller = CreateController(scope.Db, user.Id, isAdminClaim: false);
        var context = CreateAdminActionExecutingContext(controller, user.Id);
        var nextCalled = false;

        await controller.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, [], controller));
        });

        Assert.True(nextCalled);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task Get_ReturnsNoContentForMissingCurrentUserSetting()
    {
        await using var scope = await CreateScopeAsync();
        var user = await CreateUserAsync(scope.Db, "settings-user@example.test");
        var controller = CreateController(scope.Db, user.Id);

        var result = await controller.Get("notifications.typePreferences.v1");

        Assert.IsType<NoContentResult>(result.Result);
    }

    [Fact]
    public async Task Get_ReturnsCurrentUserSettingWhenItExists()
    {
        await using var scope = await CreateScopeAsync();
        var user = await CreateUserAsync(scope.Db, "settings-existing@example.test");
        scope.Db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = "money.bankStatement.autoImport",
            Value = "{\"enabled\":true}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await scope.Db.SaveChangesAsync();
        var controller = CreateController(scope.Db, user.Id);

        var result = await controller.Get("money.bankStatement.autoImport");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<UserSettingDto>(ok.Value);
        Assert.Equal("money.bankStatement.autoImport", dto.Key);
        Assert.Equal("{\"enabled\":true}", dto.Value);
    }

    [Fact]
    public async Task GetForUser_KeepsNotFoundForMissingAdminLookup()
    {
        await using var scope = await CreateScopeAsync();
        var user = await CreateUserAsync(scope.Db, "settings-admin-lookup@example.test");
        var controller = CreateController(scope.Db, user.Id);

        var result = await controller.GetForUser(user.Id, "missing.admin.lookup");

        Assert.IsType<NotFoundResult>(result.Result);
    }

    private static UserSettingsController CreateController(PaymentContext db, int userId, bool isAdminClaim = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
        };
        if (isAdminClaim)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        return new UserSettingsController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
                },
            },
        };
    }

    private static async Task<User> CreateUserAsync(PaymentContext db, string email, string? roleName = null)
    {
        roleName ??= $"role-{Guid.NewGuid():N}";
        var role = new Role { Name = roleName, Description = "Test role" };
        var user = new User
        {
            Email = email,
            FullName = email,
            PasswordHash = "hash",
            Role = role,
            IsApproved = true,
            IsActive = true,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static ActionExecutingContext CreateAdminActionExecutingContext(
        UserSettingsController controller,
        int userId)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Name, $"User {userId}"),
                    new Claim(ClaimTypes.Role, "admin"),
                ],
                "TestAuth")),
        };
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ControllerActionDescriptor
            {
                EndpointMetadata = [new AuthorizeAttribute { Policy = "Admin" }],
            });
        controller.ControllerContext = new ControllerContext(actionContext);

        return new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?>(),
            controller);
    }

    private static async Task<TestDbScope> CreateScopeAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PaymentContext(options, useObjectAuditModel: false);
        await db.Database.EnsureCreatedAsync();
        return new TestDbScope(connection, db);
    }

    private sealed class TestDbScope(SqliteConnection connection, PaymentContext db) : IAsyncDisposable
    {
        public PaymentContext Db { get; } = db;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
