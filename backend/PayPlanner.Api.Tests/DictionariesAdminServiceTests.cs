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
using Xunit;

namespace PayPlanner.Api.Tests;

public class DictionariesAdminServiceTests
{
    [Fact]
    public void ServiceDictionaryName_MaxLength_AllowsLongCourtServiceTitles()
    {
        using var db = CreateContext();

        var nameMaxLength = db.Model
            .FindEntityType(typeof(ServiceDictionaryItem))!
            .FindProperty(nameof(ServiceDictionaryItem.Name))!
            .GetMaxLength();
        var descriptionMaxLength = db.Model
            .FindEntityType(typeof(ServiceDictionaryItem))!
            .FindProperty(nameof(ServiceDictionaryItem.Description))!
            .GetMaxLength();

        Assert.Equal(1000, nameMaxLength);
        Assert.Equal(2000, descriptionMaxLength);
    }

    [Fact]
    public async Task CreateService_PersistsLongCourtServiceTitle()
    {
        await using var db = await CreateContextAsync();
        var category = new ServiceCategoryDictionaryItem
        {
            Name = "Administrative cases",
            Description = string.Empty,
            ColorHex = "#0EA5E9",
            IsActive = true,
        };
        db.ServiceCategories.Add(category);
        await db.SaveChangesAsync();

        var longName = "ГР/ получение/подача документа в суд либо в иные Государственные, Муниципальные, Административные органы власти";
        Assert.True(longName.Length > 100);

        var controller = new DictionariesAdminController(db);
        var result = await controller.CreateService(new ServiceDictionaryItem
        {
            Name = $"  {longName}  ",
            Description = "  Документы для суда и государственных органов.  ",
            Unit = string.Empty,
            DefaultPrice = -1000m,
            ColorHex = "#0EA5E9",
            IsActive = true,
            CategoryId = category.Id,
        });

        Assert.IsType<CreatedResult>(result);

        db.ChangeTracker.Clear();
        var saved = await db.Services.AsNoTracking().SingleAsync();
        Assert.Equal(longName, saved.Name);
        Assert.Equal("Документы для суда и государственных органов.", saved.Description);
        Assert.Equal("шт", saved.Unit);
        Assert.Equal(0m, saved.DefaultPrice);
        Assert.Equal(category.Id, saved.CategoryId);
    }

    [Fact]
    public async Task CreateService_ReturnsBadRequest_WhenTitleIsTooLong()
    {
        await using var db = await CreateContextAsync();
        var controller = new DictionariesAdminController(db);

        var result = await controller.CreateService(new ServiceDictionaryItem
        {
            Name = new string('а', 1001),
            Description = string.Empty,
            Unit = "шт",
            DefaultPrice = 1000m,
            ColorHex = "#0EA5E9",
            IsActive = true,
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Название услуги не должно быть длиннее 1000 символов.", badRequest.Value);
        Assert.Empty(await db.Services.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AdminFilter_ReturnsForbid_ForUserWithoutAdminRoleInDatabase()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "dictionary-user");
        var controller = new DictionariesAdminController(db);
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
        var controller = new DictionariesAdminController(db);
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

    private static PaymentContext CreateContext()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

        return new PaymentContext(options);
    }

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var db = CreateContext();
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
            PermissionsUpdatedAt = DateTime.UtcNow,
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
        DictionariesAdminController controller,
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
