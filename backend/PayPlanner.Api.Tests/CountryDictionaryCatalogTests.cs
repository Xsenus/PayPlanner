using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.ReferenceData;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CountryDictionaryCatalogTests
{
    [Fact]
    public async Task EnsureSeededAsync_FillsDictionaryWithBroadCountrySet()
    {
        await using var db = await CreateContextAsync();

        var added = await CountryDictionaryCatalog.EnsureSeededAsync(db);
        var all = await db.Countries.AsNoTracking().OrderBy(x => x.Name).ToListAsync();

        Assert.True(added >= 200);
        Assert.True(all.Count >= 200);
        Assert.Contains(all, item => item.Name == "Россия");
        Assert.Contains(all, item => item.Name == "Казахстан");
        Assert.Contains(all, item => item.Name == "Германия");
        Assert.DoesNotContain(all, item => item.Name == "??????");
        Assert.Equal(all.Count, all.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task EnsureSeededAsync_AddsMissingCountries_AndPreservesExistingEntries()
    {
        await using var db = await CreateContextAsync();
        db.Countries.AddRange(
            new CountryDictionaryItem
            {
                Name = "Россия",
                Description = "Российская Федерация",
                ColorHex = "#2563EB",
                IsActive = true,
            },
            new CountryDictionaryItem
            {
                Name = "Пользовательская страна",
                Description = "Кастомная запись",
                ColorHex = "#123456",
                IsActive = false,
            });
        await db.SaveChangesAsync();

        var firstAdded = await CountryDictionaryCatalog.EnsureSeededAsync(db);
        var secondAdded = await CountryDictionaryCatalog.EnsureSeededAsync(db);

        var all = await db.Countries.AsNoTracking().ToListAsync();

        Assert.True(firstAdded >= 199);
        Assert.Equal(0, secondAdded);
        Assert.Single(all, item => item.Name == "Россия");
        Assert.Single(all, item => item.Name == "Пользовательская страна");
        Assert.Contains(all, item => item.Name == "Беларусь");
        Assert.Contains(all, item => item.Name == "Франция");
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
