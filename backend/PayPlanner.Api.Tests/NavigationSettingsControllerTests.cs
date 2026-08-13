using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class NavigationSettingsControllerTests
{
    [Fact]
    public async Task Update_PersistsNormalizedConfiguration_ForAdministrator()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "admin", canEditRoles: false);
        var controller = CreateController(db, user.Id);

        var result = await controller.Update(
            new UpdateNavigationSettingsRequest(
                ["tasks", "tasks", "unknown", "calendar"],
                new Dictionary<string, string>
                {
                    ["tasks"] = "  Рабочие задачи  ",
                    ["unknown"] = "Неизвестный пункт",
                }),
            CancellationToken.None);

        var dto = AssertOk(result);
        Assert.Equal("tasks", dto.Order[0]);
        Assert.Equal("calendar", dto.Order[1]);
        Assert.Equal("Рабочие задачи", dto.Labels["tasks"]);
        Assert.DoesNotContain("unknown", dto.Order);
        Assert.False(dto.Labels.ContainsKey("unknown"));
        Assert.Single(await db.SystemSettings.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Update_AllowsDelegatedRoleEditor()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "office-manager", canEditRoles: true);
        var controller = CreateController(db, user.Id);

        var result = await controller.Update(
            new UpdateNavigationSettingsRequest(["clients", "calendar"], new Dictionary<string, string>()),
            CancellationToken.None);

        AssertOk(result);
    }

    [Fact]
    public async Task Update_ForbidsOrdinaryUser()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "lawyer", canEditRoles: false);
        var controller = CreateController(db, user.Id);

        var result = await controller.Update(
            new UpdateNavigationSettingsRequest(["clients", "calendar"], new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(await db.SystemSettings.AsNoTracking().ToListAsync());
    }

    private static NavigationSettingsController CreateController(PaymentContext db, int userId)
        => new(db, new RoleManagementAccessService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                        "test")),
                },
            },
        };

    private static async Task<User> CreateUserAsync(PaymentContext db, string roleName, bool canEditRoles)
    {
        var role = new Role { Name = roleName, Description = roleName };
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            FullName = roleName,
            Role = role,
            IsActive = true,
            IsApproved = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        if (!string.Equals(roleName, "admin", StringComparison.OrdinalIgnoreCase))
        {
            db.RolePermissions.Add(new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Roles,
                CanView = canEditRoles,
                CanEdit = canEditRoles,
            });
            await db.SaveChangesAsync();
        }

        return user;
    }

    private static NavigationSettingsDto AssertOk(ActionResult<NavigationSettingsDto> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<NavigationSettingsDto>(ok.Value);
    }

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options;
        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
