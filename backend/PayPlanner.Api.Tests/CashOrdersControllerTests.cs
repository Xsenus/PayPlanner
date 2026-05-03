using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CashOrdersControllerTests
{
    [Fact]
    public async Task CreateInventory_CalculatesDifferenceAndCreatesCorrectionOrder()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-0001",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 1),
                Amount = 1_000m,
                Account = "Основная касса",
                CreatedAt = DateTime.UtcNow,
            },
            new CashOrder
            {
                Number = "РКО-2026-0001",
                Type = CashOrderType.Outgoing,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 2),
                Amount = 250m,
                Account = "Основная касса",
                CreatedAt = DateTime.UtcNow,
            },
            new CashOrder
            {
                Number = "ПКО-2026-0002",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 5, 2),
                Amount = 500m,
                Account = "Резервная касса",
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        var controller = new CashOrdersController(db);

        var action = await controller.CreateInventory(
            new CashInventoryCreateRequest
            {
                Date = new DateTime(2026, 5, 3),
                AccountName = " Основная касса ",
                ActualBalance = 800m,
                Comment = "Излишек по пересчету",
                CreateCorrection = true,
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action);
        var inventory = Assert.IsType<CashInventoryDto>(created.Value);
        Assert.Equal("ИНВК-2026-0001", inventory.Number);
        Assert.Equal(750m, inventory.AccountingBalance);
        Assert.Equal(800m, inventory.ActualBalance);
        Assert.Equal(50m, inventory.Difference);
        Assert.NotNull(inventory.CorrectionCashOrderId);

        var correction = await db.CashOrders.SingleAsync(order => order.Id == inventory.CorrectionCashOrderId);
        Assert.Equal(CashOrderType.Incoming, correction.Type);
        Assert.Equal(CashOrderStatus.Posted, correction.Status);
        Assert.Equal(50m, correction.Amount);
        Assert.Equal("Основная касса", correction.Account);

        var balanceAfterCorrection = await db.CashOrders
            .Where(order => order.Status == CashOrderStatus.Posted && order.Account == "Основная касса")
            .SumAsync(order => order.Type == CashOrderType.Incoming ? order.Amount : -order.Amount);
        Assert.Equal(800m, balanceAfterCorrection);
    }

    [Fact]
    public async Task GetInventories_FiltersByAccountAndPeriod()
    {
        await using var db = await CreateContextAsync();
        db.CashInventories.AddRange(
            new CashInventory
            {
                Number = "ИНВК-2026-0001",
                Date = new DateTime(2026, 5, 1),
                AccountName = "Основная касса",
                AccountingBalance = 1_000m,
                ActualBalance = 1_000m,
                Difference = 0m,
                CreatedAt = DateTime.UtcNow,
            },
            new CashInventory
            {
                Number = "ИНВК-2026-0002",
                Date = new DateTime(2026, 5, 2),
                AccountName = "Резервная касса",
                AccountingBalance = 500m,
                ActualBalance = 450m,
                Difference = -50m,
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        var controller = new CashOrdersController(db);

        var action = await controller.GetInventories(
            from: new DateTime(2026, 5, 1),
            to: new DateTime(2026, 5, 31),
            accountName: "Основная касса",
            page: 1,
            pageSize: 10,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var response = Assert.IsType<PagedResponse<CashInventoryDto>>(ok.Value);
        var item = Assert.Single(response.Items);
        Assert.Equal("ИНВК-2026-0001", item.Number);
        Assert.Equal("Основная касса", item.AccountName);
        Assert.Equal(1, response.Total);
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
