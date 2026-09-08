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

public sealed class AppearanceSettingsControllerTests
{
    [Fact]
    public async Task Get_ReturnsStandardTheme_WhenSettingDoesNotExist()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "lawyer");
        var controller = CreateController(db, user.Id);

        var dto = AssertOk(await controller.Get(CancellationToken.None));

        Assert.Equal("standard", dto.ThemeKey);
        Assert.Null(dto.UpdatedAt);
    }

    [Fact]
    public async Task Get_FallsBackToStandardTheme_WhenStoredValueIsUnknown()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "lawyer");
        db.SystemSettings.Add(new SystemSetting
        {
            Key = AppearanceSettingsController.SettingKey,
            Value = "unknown",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, user.Id);

        var dto = AssertOk(await controller.Get(CancellationToken.None));

        Assert.Equal("standard", dto.ThemeKey);
    }

    [Fact]
    public async Task Update_PersistsTheme_ForAdministrator()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "admin");
        var controller = CreateController(db, user.Id);

        var dto = AssertOk(await controller.Update(
            new UpdateAppearanceSettingsRequest(" Graphite "),
            CancellationToken.None));

        Assert.Equal("graphite", dto.ThemeKey);
        Assert.Equal("graphite", await db.SystemSettings
            .Where(item => item.Key == AppearanceSettingsController.SettingKey)
            .Select(item => item.Value)
            .SingleAsync());
    }

    [Fact]
    public async Task Update_ForbidsOrdinaryUser()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "lawyer");
        var controller = CreateController(db, user.Id);

        var result = await controller.Update(
            new UpdateAppearanceSettingsRequest("ocean"),
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(await db.SystemSettings.ToListAsync());
    }

    [Fact]
    public async Task Update_RejectsUnknownTheme()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "admin");
        var controller = CreateController(db, user.Id);

        var result = await controller.Update(
            new UpdateAppearanceSettingsRequest("personal-random"),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await db.SystemSettings.ToListAsync());
    }

    private static AppearanceSettingsController CreateController(PaymentContext db, int userId)
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

    private static async Task<User> CreateUserAsync(PaymentContext db, string roleName)
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
        return user;
    }

    private static AppearanceSettingsDto AssertOk(ActionResult<AppearanceSettingsDto> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<AppearanceSettingsDto>(ok.Value);
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
