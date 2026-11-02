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
using PayPlanner.Api.Models.Requests;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upsert_InitializesOneOwnSettingWithoutChangingOtherUsers(bool onlyIfMissing)
    {
        await using var scope = await CreateScopeAsync();
        var user = await CreateUserAsync(scope.Db, "timezone-owner@example.test");
        var other = await CreateUserAsync(scope.Db, "timezone-other@example.test");
        scope.Db.UserSettings.Add(new UserSetting { UserId = other.Id, Key = "profile.timeZoneId", Value = "Europe/Samara" });
        await scope.Db.SaveChangesAsync();
        var controller = CreateController(scope.Db, user.Id);
        var request = new UpsertUserSettingRequest { Value = "UTC" };
        var result = onlyIfMissing ? await controller.Initialize("profile.timeZoneId", request) : await controller.Upsert("profile.timeZoneId", request);
        var dto = Assert.IsType<UserSettingDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("UTC", dto.Value);
        Assert.Equal(2, await scope.Db.UserSettings.CountAsync());
        Assert.Equal("Europe/Samara", await scope.Db.UserSettings.AsNoTracking().Where(row => row.UserId == other.Id).Select(row => row.Value).SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upsert_ExplicitChoiceWinsInBothInitializationOrders(bool manualFirst)
    {
        await using var scope = await CreateScopeAsync();
        var user = await CreateUserAsync(scope.Db, "timezone-order@example.test");
        var controller = CreateController(scope.Db, user.Id);
        var automatic = new UpsertUserSettingRequest { Value = "UTC" };
        var manual = new UpsertUserSettingRequest { Value = "Europe/Samara" };
        await (manualFirst ? controller.Upsert("profile.timeZoneId", manual) : controller.Initialize("profile.timeZoneId", automatic));
        var original = await scope.Db.UserSettings.AsNoTracking().SingleAsync();
        await (manualFirst ? controller.Initialize("profile.timeZoneId", automatic) : controller.Upsert("profile.timeZoneId", manual));
        var final = await scope.Db.UserSettings.AsNoTracking().SingleAsync();
        Assert.Equal(original.Id, final.Id);
        Assert.Equal(original.CreatedAt, final.CreatedAt);
        Assert.Equal("Europe/Samara", final.Value);
        Assert.Single(await scope.Db.UserSettings.ToListAsync());
        if (manualFirst) Assert.Equal(original.UpdatedAt, final.UpdatedAt);
    }

    [Theory]
    [InlineData("with'quote", "value'; DELETE FROM \"Users\"; --")]
    [InlineData("empty.value", "")]
    public async Task Upsert_ParameterizesValuesAndPreservesValidEmptyStrings(string key, string value)
    {
        await using var scope = await CreateScopeAsync();
        var user = await CreateUserAsync(scope.Db, "setting-values@example.test");
        await CreateController(scope.Db, user.Id).Upsert(key, new UpsertUserSettingRequest { Value = value });
        var saved = await scope.Db.UserSettings.AsNoTracking().SingleAsync();
        Assert.Equal(key, saved.Key); Assert.Equal(value, saved.Value);
        Assert.True(await scope.Db.Users.AnyAsync(row => row.Id == user.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Upsert_RejectsAnInvalidKeyWithoutWrites(string key)
    {
        await using var scope = await CreateScopeAsync();
        var user = await CreateUserAsync(scope.Db, "setting-invalid@example.test");
        var result = await CreateController(scope.Db, user.Id).Initialize(key, new UpsertUserSettingRequest { Value = "UTC" });
        Assert.IsType<BadRequestResult>(result.Result);
        Assert.Empty(await scope.Db.UserSettings.ToListAsync());
    }

    [Fact]
    public async Task Upsert_RejectsMissingUserAndOversizedKeyWithoutWrites()
    {
        await using var scope = await CreateScopeAsync();
        var controller = CreateController(scope.Db, 999);
        Assert.IsType<NotFoundResult>((await controller.Initialize("profile.timeZoneId", new UpsertUserSettingRequest { Value = "UTC" })).Result);
        var user = await CreateUserAsync(scope.Db, "setting-key-size@example.test");
        Assert.IsType<BadRequestResult>((await CreateController(scope.Db, user.Id).Upsert(new string('x', 161), new UpsertUserSettingRequest { Value = "UTC" })).Result);
        Assert.Empty(await scope.Db.UserSettings.ToListAsync());
    }

    [Fact]
    public async Task Initialize_RejectsMissingIdentityWithoutWriting()
    {
        await using var scope = await CreateScopeAsync();
        var controller = CreateController(scope.Db, 999);
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        var result = await controller.Initialize("profile.timeZoneId", new UpsertUserSettingRequest { Value = "UTC" });
        Assert.IsType<UnauthorizedResult>(result.Result);
        Assert.Empty(await scope.Db.UserSettings.ToListAsync());
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
