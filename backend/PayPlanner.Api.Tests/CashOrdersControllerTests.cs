using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CashOrdersControllerTests
{
    [Fact]
    public async Task GetNextNumber_ReturnsNextUnusedCashOrderNumber()
    {
        await using var db = await CreateContextAsync();
        db.CashOrders.AddRange(
            new CashOrder
            {
                Number = "ПКО-2026-0001",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 1),
                Amount = 1_000m,
                CreatedAt = DateTime.UtcNow,
            },
            new CashOrder
            {
                Number = "ПКО-2026-0003",
                Type = CashOrderType.Incoming,
                Status = CashOrderStatus.Posted,
                Date = new DateTime(2026, 4, 2),
                Amount = 2_000m,
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var controller = new CashOrdersController(db);
        var action = await controller.GetNextNumber(
            CashOrderType.Incoming,
            new DateTime(2026, 4, 20),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.NotNull(ok.Value);
        var value = ok.Value;
        var number = value.GetType().GetProperty("number")?.GetValue(value);
        var type = value.GetType().GetProperty("type")?.GetValue(value);
        var date = value.GetType().GetProperty("date")?.GetValue(value);

        Assert.Equal("ПКО-2026-0004", number);
        Assert.Equal(CashOrderType.Incoming, type);
        Assert.Equal(new DateTime(2026, 4, 20), date);
    }

    [Fact]
    public async Task DefaultDates_UseProfileTimeZoneForCashOrderToday()
    {
        await using var db = await CreateContextAsync();
        var selectedZone = ResolveTimeZoneWithDifferentLocalDate();
        var userId = await CreateUserWithTimeZoneAsync(db, selectedZone.TimeZoneId);
        var controller = CreateController(db, userId);

        var nextNumberAction = await controller.GetNextNumber(
            CashOrderType.Incoming,
            date: null,
            CancellationToken.None);

        var nextNumberOk = Assert.IsType<OkObjectResult>(nextNumberAction);
        Assert.NotNull(nextNumberOk.Value);
        var nextNumberDate = nextNumberOk.Value.GetType().GetProperty("date")?.GetValue(nextNumberOk.Value);
        Assert.Equal(selectedZone.ProfileToday, nextNumberDate);

        var createAction = await controller.Create(new CashOrder
        {
            Type = CashOrderType.Incoming,
            Status = CashOrderStatus.Posted,
            Date = default,
            Amount = 1_200m,
            Currency = "RUB",
            Account = "Main cashbox",
            PayerName = "Client",
            Basis = "Cash payment",
        }, CancellationToken.None);

        var createdResult = Assert.IsType<CreatedResult>(createAction);
        var created = Assert.IsType<CashOrderDto>(createdResult.Value);
        Assert.Equal(selectedZone.ProfileToday, created.Date);
        Assert.StartsWith($"ПКО-{selectedZone.ProfileToday.Year}-", created.Number);

        var cashBookAction = await controller.GetCashBook(
            from: null,
            to: null,
            accountName: "Main cashbox",
            ct: CancellationToken.None);

        var cashBookOk = Assert.IsType<OkObjectResult>(cashBookAction.Result);
        var cashBook = Assert.IsType<CashBookResponseDto>(cashBookOk.Value);
        Assert.Equal(selectedZone.ProfileToday, cashBook.From);
        Assert.Equal(selectedZone.ProfileToday, cashBook.To);
        Assert.Equal(1_200m, cashBook.Income);

        var inventoryAction = await controller.CreateInventory(
            new CashInventoryCreateRequest
            {
                Date = default,
                AccountName = "Main cashbox",
                ActualBalance = 1_200m,
                CreateCorrection = false,
            },
            CancellationToken.None);

        var inventoryCreated = Assert.IsType<CreatedResult>(inventoryAction);
        var inventory = Assert.IsType<CashInventoryDto>(inventoryCreated.Value);
        Assert.Equal(selectedZone.ProfileToday, inventory.Date);
        Assert.StartsWith($"ИНВК-{selectedZone.ProfileToday.Year}-", inventory.Number);

        var copyAction = await controller.Copy(created.Id, CancellationToken.None);
        var copyCreated = Assert.IsType<CreatedResult>(copyAction);
        var copied = Assert.IsType<CashOrderDto>(copyCreated.Value);
        Assert.Equal(selectedZone.ProfileToday, copied.Date);
        Assert.StartsWith($"ПКО-{selectedZone.ProfileToday.Year}-", copied.Number);
    }

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

    private static async Task<int> CreateUserWithTimeZoneAsync(PaymentContext db, string timeZoneId)
    {
        var role = new Role
        {
            Name = $"cash-order-timezone-role-{Guid.NewGuid():N}",
            Description = "Cash order timezone test role",
            CreatedAt = DateTime.UtcNow,
        };
        var user = new User
        {
            Email = $"cash-order-timezone-{Guid.NewGuid():N}@example.test",
            PasswordHash = "hash",
            FullName = "Cash order timezone user",
            Role = role,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Roles.Add(role);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = timeZoneId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return user.Id;
    }

    private static CashOrdersController CreateController(PaymentContext db, int userId)
        => new(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                            new Claim(ClaimTypes.Role, "admin"),
                        ],
                        authenticationType: "Test")),
                },
            },
        };

    private static (string TimeZoneId, DateTime ProfileToday) ResolveTimeZoneWithDifferentLocalDate()
    {
        var nowUtc = DateTime.UtcNow;
        var utcToday = nowUtc.Date;
        foreach (var timeZoneId in new[]
        {
            "Pacific/Kiritimati",
            "Line Islands Standard Time",
            "Pacific/Pago_Pago",
            "Samoa Standard Time",
        })
        {
            var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(timeZoneId);
            var profileToday = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone).Date;
            if (profileToday != utcToday)
            {
                return (timeZone.Id, profileToday);
            }
        }

        throw new InvalidOperationException("No test timezone with a local date different from UTC was found.");
    }
}
