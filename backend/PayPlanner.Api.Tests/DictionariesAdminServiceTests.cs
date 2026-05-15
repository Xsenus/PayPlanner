using Microsoft.AspNetCore.Mvc;
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

        var maxLength = db.Model
            .FindEntityType(typeof(ServiceDictionaryItem))!
            .FindProperty(nameof(ServiceDictionaryItem.Name))!
            .GetMaxLength();

        Assert.Equal(300, maxLength);
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

        var longName = "GR/ Receiving and filing a document in court or other state municipal administrative authorities and public offices";
        Assert.True(longName.Length > 100);

        var controller = new DictionariesAdminController(db);
        var result = await controller.CreateService(new ServiceDictionaryItem
        {
            Name = longName,
            Description = string.Empty,
            Unit = "service",
            DefaultPrice = 1000m,
            ColorHex = "#0EA5E9",
            IsActive = true,
            CategoryId = category.Id,
        });

        Assert.IsType<CreatedResult>(result);

        db.ChangeTracker.Clear();
        var saved = await db.Services.AsNoTracking().SingleAsync();
        Assert.Equal(longName, saved.Name);
        Assert.Equal(1000m, saved.DefaultPrice);
        Assert.Equal(category.Id, saved.CategoryId);
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
}
