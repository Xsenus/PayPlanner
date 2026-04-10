using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.ReferenceData;
using Xunit;

namespace PayPlanner.Api.Tests;

public class TimezoneDictionaryCatalogTests
{
    [Fact]
    public async Task EnsureSeededAsync_FillsDictionaryWithFullTimezoneSet()
    {
        await using var db = await CreateContextAsync();

        var added = await TimezoneDictionaryCatalog.EnsureSeededAsync(db);

        var all = await db.Timezones.AsNoTracking().OrderBy(x => x.Name).ToListAsync();

        Assert.True(added >= 418);
        Assert.True(all.Count >= 418);
        Assert.Contains(all, item => item.Name == "UTC");
        Assert.Contains(all, item => item.Name == "Europe/Moscow");
        Assert.Contains(all, item => item.Name == "Asia/Novosibirsk");
        Assert.Contains(all, item => item.Name == "America/New_York");
        Assert.Equal(all.Count, all.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task EnsureSeededAsync_AddsMissingTimezones_AndPreservesExistingEntries()
    {
        await using var db = await CreateContextAsync();
        db.Timezones.AddRange(
            new TimezoneDictionaryItem
            {
                Name = "Europe/Moscow",
                Description = "Москва",
                ColorHex = "#111111",
                IsActive = true,
            },
            new TimezoneDictionaryItem
            {
                Name = "Custom/Office",
                Description = "Кастомный офисный пояс",
                ColorHex = "#222222",
                IsActive = false,
            });
        await db.SaveChangesAsync();

        var firstAdded = await TimezoneDictionaryCatalog.EnsureSeededAsync(db);
        var secondAdded = await TimezoneDictionaryCatalog.EnsureSeededAsync(db);

        var all = await db.Timezones.AsNoTracking().ToListAsync();

        Assert.True(firstAdded >= 417);
        Assert.Equal(0, secondAdded);
        Assert.Single(all, item => item.Name == "Europe/Moscow");
        Assert.Single(all, item => item.Name == "Custom/Office");
        Assert.Contains(all, item => item.Name == "Asia/Yekaterinburg");
        Assert.Contains(all, item => item.Name == "Asia/Novosibirsk");
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
