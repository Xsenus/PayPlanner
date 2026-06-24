using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.LegalEntities;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DadataIntegrationSettingsControllerTests
{
    [Fact]
    public async Task Get_ReturnsMaskedServerKeysWithoutRawValues()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await CreateUserWithIntegrationPermissionAsync(scope.Db, canView: true, canEdit: false);
        scope.Db.SystemSettings.AddRange(
            new SystemSetting
            {
                Key = DadataLegalEntityEnrichmentService.ApiKeySettingKey,
                Value = "60b3fe73d105072c460248beb1ac9784bc836314",
            },
            new SystemSetting
            {
                Key = DadataLegalEntityEnrichmentService.SecretSettingKey,
                Value = "56fe56f8037313f76dfc975325eb3c68eb5f2906",
            });
        await scope.Db.SaveChangesAsync();
        var controller = CreateController(scope.Db, user.Id);

        var response = await controller.Get(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var settings = Assert.IsType<DadataIntegrationSettingsDto>(ok.Value);
        Assert.True(settings.ApiKeyConfigured);
        Assert.True(settings.SecretConfigured);
        Assert.Equal("••••6314", settings.ApiKeyMasked);
        Assert.Equal("••••2906", settings.SecretMasked);
        Assert.False(settings.CanEdit);
        Assert.DoesNotContain("60b3fe", settings.ApiKeyMasked, StringComparison.Ordinal);
        Assert.DoesNotContain("56fe56", settings.SecretMasked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_ForbidsUserWithoutIntegrationEditPermission()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await CreateUserWithIntegrationPermissionAsync(scope.Db, canView: true, canEdit: false);
        var controller = CreateController(scope.Db, user.Id);

        var response = await controller.Update(
            new UpdateDadataIntegrationSettingsRequest { ApiKey = "new-token" },
            CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.Empty(scope.Db.SystemSettings);
    }

    [Fact]
    public async Task Update_SavesKeysToSystemSettingsForUserWithIntegrationEditPermission()
    {
        await using var scope = await CreateDbScopeAsync();
        var user = await CreateUserWithIntegrationPermissionAsync(scope.Db, canView: true, canEdit: true);
        var controller = CreateController(scope.Db, user.Id);

        var response = await controller.Update(
            new UpdateDadataIntegrationSettingsRequest
            {
                ApiKey = " server-token ",
                Secret = " server-secret ",
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var settings = Assert.IsType<DadataIntegrationSettingsDto>(ok.Value);
        Assert.True(settings.ApiKeyConfigured);
        Assert.True(settings.SecretConfigured);
        Assert.True(settings.CanEdit);

        var saved = await scope.Db.SystemSettings.AsNoTracking().ToDictionaryAsync(item => item.Key, item => item.Value);
        Assert.Equal("server-token", saved[DadataLegalEntityEnrichmentService.ApiKeySettingKey]);
        Assert.Equal("server-secret", saved[DadataLegalEntityEnrichmentService.SecretSettingKey]);
    }

    private static DadataIntegrationSettingsController CreateController(PaymentContext db, int userId)
    {
        var controller = new DadataIntegrationSettingsController(
            db,
            Options.Create(new DadataOptions()));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                    "Test")),
            },
        };

        return controller;
    }

    private static async Task<User> CreateUserWithIntegrationPermissionAsync(
        PaymentContext db,
        bool canView,
        bool canEdit)
    {
        var role = new Role { Name = "custom", Description = "Роль" };
        var user = new User
        {
            Email = Guid.NewGuid() + "@example.test",
            FullName = "Пользователь",
            Role = role,
            IsActive = true,
            IsApproved = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Integrations,
            CanView = canView,
            CanEdit = canEdit,
            ViewScope = DataVisibilityScope.All,
        });
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<TestDbScope> CreateDbScopeAsync()
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
