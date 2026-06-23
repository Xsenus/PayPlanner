using System.Security.Claims;
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
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class AuthProvidersAdminControllerTests
{
    [Fact]
    public async Task AdminFilter_ReturnsForbid_ForUserWithoutAdminRoleInDatabase()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "user");
        var controller = CreateController(db);
        var context = CreateActionExecutingContext(controller, user.Id);
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
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "admin");
        var controller = CreateController(db);
        var context = CreateActionExecutingContext(controller, user.Id);
        var nextCalled = false;

        await controller.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, [], controller));
        });

        Assert.True(nextCalled);
        Assert.Null(context.Result);
    }

    private static AuthProvidersAdminController CreateController(PaymentContext db)
        => new(null!, db);

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

    private static async Task<User> CreateUserAsync(PaymentContext db, string roleName)
    {
        var role = new Role
        {
            Name = roleName,
            Description = roleName,
            CreatedAt = DateTime.UtcNow,
        };
        var user = new User
        {
            Email = $"{roleName}-{Guid.NewGuid():N}@example.test",
            PasswordHash = "hash",
            FullName = roleName,
            IsActive = true,
            IsApproved = true,
            Role = role,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static ActionExecutingContext CreateActionExecutingContext(
        AuthProvidersAdminController controller,
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
            new ControllerActionDescriptor());
        controller.ControllerContext = new ControllerContext(actionContext);

        return new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?>(),
            controller);
    }
}
