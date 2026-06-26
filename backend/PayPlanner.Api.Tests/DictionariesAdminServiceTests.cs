using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Filters;
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
    public void Controller_UsesDictionaryRolePermissionInsteadOfAdminPolicy()
    {
        var authorize = Assert.Single(typeof(DictionariesAdminController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true));
        Assert.Null(((AuthorizeAttribute)authorize).Policy);

        var section = Assert.Single(typeof(DictionariesAdminController).GetCustomAttributes(typeof(RequireSectionPermissionAttribute), inherit: true));
        var permission = Assert.IsType<RequireSectionPermissionAttribute>(section);
        Assert.Equal(RolePermissionCatalog.Dictionaries, permission.Section);
        Assert.Equal(RequireSectionPermissionOperations.View, permission.Operation);
    }

    [Fact]
    public void Controller_WriteActionsUseDictionaryOperationPermissions()
    {
        AssertMethodPermission(nameof(DictionariesAdminController.CreateDealType), RequireSectionPermissionOperations.Create);
        AssertMethodPermission(nameof(DictionariesAdminController.UpdateDealType), RequireSectionPermissionOperations.Edit);
        AssertMethodPermission(nameof(DictionariesAdminController.DeleteDealType), RequireSectionPermissionOperations.Delete);
        AssertMethodPermission(nameof(DictionariesAdminController.ToggleDealType), RequireSectionPermissionOperations.Edit);
        AssertMethodPermission(nameof(DictionariesAdminController.MergeSimpleDictionaryDuplicates), RequireSectionPermissionOperations.Edit);
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

    private static void AssertMethodPermission(string methodName, string operation)
    {
        var method = typeof(DictionariesAdminController).GetMethod(methodName);
        Assert.NotNull(method);

        var attribute = Assert.Single(method.GetCustomAttributes(typeof(RequireSectionPermissionAttribute), inherit: true));
        var permission = Assert.IsType<RequireSectionPermissionAttribute>(attribute);
        Assert.Equal(RolePermissionCatalog.Dictionaries, permission.Section);
        Assert.Equal(operation, permission.Operation);
    }
}
