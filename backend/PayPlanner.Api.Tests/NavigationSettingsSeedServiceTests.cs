using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class NavigationSettingsSeedServiceTests
{
    [Fact]
    public async Task EnsureDefaultGroupsAsync_CreatesDatabaseConfiguration()
    {
        await using var db = await CreateContextAsync();

        var changed = await NavigationSettingsSeedService.EnsureDefaultGroupsAsync(db);

        Assert.True(changed);
        var setting = await db.SystemSettings.SingleAsync(item => item.Key == NavigationSettingsSeedService.SettingKey);
        using var json = JsonDocument.Parse(setting.Value);
        var groups = json.RootElement.GetProperty("groups").EnumerateArray().ToArray();
        Assert.Equal(["Работа с клиентами", "Бухгалтерия", "Управление"], groups.Select(group => group.GetProperty("name").GetString()));
        Assert.Equal(["legalEntities", "clients", "contacts"], groups[0].GetProperty("itemIds").EnumerateArray().Select(item => item.GetString()));
        var order = json.RootElement.GetProperty("order").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal("workspace", order[0]);
        Assert.Contains("courtCases", order);
        Assert.Equal(Array.IndexOf(order, "group-accounting") + 1, Array.IndexOf(order, "payroll"));
    }

    [Fact]
    public async Task EnsureDefaultGroupsAsync_PreservesConfiguredGroups()
    {
        await using var db = await CreateContextAsync();
        const string customValue = """{"order":["group-custom"],"labels":{},"groups":[{"id":"group-custom","name":"Моя группа","itemIds":["tasks"]}]}""";
        db.SystemSettings.Add(new SystemSetting
        {
            Key = NavigationSettingsSeedService.SettingKey,
            Value = customValue,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var changed = await NavigationSettingsSeedService.EnsureDefaultGroupsAsync(db);

        Assert.True(changed);
        using var json = JsonDocument.Parse((await db.SystemSettings.SingleAsync()).Value);
        Assert.Equal("Моя группа", json.RootElement.GetProperty("groups")[0].GetProperty("name").GetString());
        Assert.Equal("workspace", json.RootElement.GetProperty("order")[0].GetString());
        Assert.Contains("payroll", json.RootElement.GetProperty("order").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("courtCases", json.RootElement.GetProperty("order").EnumerateArray().Select(item => item.GetString()));

        Assert.False(await NavigationSettingsSeedService.EnsureDefaultGroupsAsync(db));
    }

    [Fact]
    public async Task EnsureDefaultGroupsAsync_MigratesLegacyValueAndPreservesLabels()
    {
        await using var db = await CreateContextAsync();
        db.SystemSettings.Add(new SystemSetting
        {
            Key = NavigationSettingsSeedService.SettingKey,
            Value = """{"order":["tasks","accounts","calendar"],"labels":{"tasks":"Рабочие задачи"}}""",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.True(await NavigationSettingsSeedService.EnsureDefaultGroupsAsync(db));

        using var json = JsonDocument.Parse((await db.SystemSettings.SingleAsync()).Value);
        Assert.Equal("Рабочие задачи", json.RootElement.GetProperty("labels").GetProperty("tasks").GetString());
        var order = json.RootElement.GetProperty("order").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal("workspace", order[0]);
        Assert.Equal("tasks", order[1]);
        Assert.Equal("group-accounting", order[2]);
        Assert.Equal("payroll", order[3]);
        Assert.Equal("calendar", order[4]);
    }

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
