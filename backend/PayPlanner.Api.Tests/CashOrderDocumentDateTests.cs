using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
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

public sealed class CashOrderDocumentDateTests(PostgreSqlModelTemplate databases) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false, "UTC")]
    [InlineData(true, "UTC")]
    [InlineData(false, "Asia/Novosibirsk")]
    [InlineData(true, "Asia/Novosibirsk")]
    public Task CreateFromPayment_PreservesSelectedDayAcrossPreviewSaveReloadAndRetry(bool postgres, string timeZoneId)
        => WithDatabaseAsync(postgres, async db =>
        {
            var (payment, userId) = await SeedAsync(db, timeZoneId);
            var selectedDay = new DateTime(2099, 1, 1);
            Assert.Equal(selectedDay, payment.PaymentDocumentDate);
            Assert.Equal(DateTimeKind.Unspecified, payment.PaymentDocumentDate!.Value.Kind);
            Assert.NotEqual(payment.PaymentDocumentDate, payment.PlannedDate);
            Assert.NotEqual(payment.PaymentDocumentDate, payment.PaidDate);
            Assert.NotEqual(payment.PaymentDocumentDate, payment.LastPaymentDate);
            db.ChangeTracker.Clear();
            var before = await PaymentSnapshotAsync(db, payment.Id);
            var controller = CreateController(db, userId);

            var preview = Assert.IsType<ContentResult>(await controller.PrintForPayment(payment.Id, CancellationToken.None));
            AssertPrintedSelectedDay(preview.Content!);
            Assert.Empty(await db.CashOrders.ToListAsync());
            Assert.Empty(await db.MoneyOperations.ToListAsync());

            var created = CreatedOrder(await controller.CreateFromPayment(payment.Id, CancellationToken.None));
            Assert.Equal(selectedDay, created.Date);
            Assert.StartsWith("ПКО-2099-", created.Number);
            Assert.Equal(payment.PaidAmount, created.Amount);
            Assert.Equal(payment.Id, created.PaymentId);
            var repeated = Order(await controller.CreateFromPayment(payment.Id, CancellationToken.None));
            Assert.Equal(created.Id, repeated.Id);
            Assert.Equal(created.Number, repeated.Number);
            Assert.Equal(selectedDay, repeated.Date);

            db.ChangeTracker.Clear();
            var reloaded = Order(await controller.GetById(created.Id, CancellationToken.None));
            Assert.Equal(selectedDay, reloaded.Date);
            var savedPrint = Assert.IsType<ContentResult>(await controller.Print(created.Id, CancellationToken.None));
            var paymentPrint = Assert.IsType<ContentResult>(await controller.PrintForPayment(payment.Id, CancellationToken.None));
            AssertPrintedSelectedDay(savedPrint.Content!);
            AssertPrintedSelectedDay(paymentPrint.Content!);
            Assert.Single(await db.CashOrders.ToListAsync());
            Assert.Empty(await db.MoneyOperations.ToListAsync());
            Assert.Single(await db.Payments.ToListAsync());
            db.ChangeTracker.Clear();
            Assert.Equal(before, await PaymentSnapshotAsync(db, payment.Id));
        });

    [Theory]
    [InlineData(false, "last")]
    [InlineData(true, "last")]
    [InlineData(false, "paid")]
    [InlineData(true, "paid")]
    [InlineData(false, "date")]
    [InlineData(true, "date")]
    public Task CreateFromPayment_WithoutSelectedDayRetainsLegacyFallback(bool postgres, string fallback)
        => WithDatabaseAsync(postgres, async db =>
        {
            var (payment, userId) = await SeedAsync(db, "UTC");
            payment.PaymentDocumentDate = null;
            if (fallback != "last") payment.LastPaymentDate = null;
            if (fallback == "date") payment.PaidDate = null;
            await db.SaveChangesAsync();
            var expected = fallback == "last" ? new DateTime(2098, 12, 31)
                : fallback == "paid" ? new DateTime(2098, 12, 30) : new DateTime(2098, 12, 29);
            db.ChangeTracker.Clear();
            var before = await PaymentSnapshotAsync(db, payment.Id);
            var controller = CreateController(db, userId);

            var created = CreatedOrder(await controller.CreateFromPayment(payment.Id, CancellationToken.None));
            Assert.Equal(expected, created.Date);
            Assert.NotEqual(payment.PlannedDate, created.Date);
            var repeated = Order(await controller.CreateFromPayment(payment.Id, CancellationToken.None));
            Assert.Equal(created.Id, repeated.Id);
            Assert.Single(await db.CashOrders.ToListAsync());
            Assert.Empty(await db.MoneyOperations.ToListAsync());
            db.ChangeTracker.Clear();
            Assert.Equal(before, await PaymentSnapshotAsync(db, payment.Id));
        });

    [Theory]
    [InlineData(false, CashOrderStatus.Draft)]
    [InlineData(true, CashOrderStatus.Draft)]
    [InlineData(false, CashOrderStatus.Posted)]
    [InlineData(true, CashOrderStatus.Posted)]
    public Task CreateFromPayment_ReturnsExistingOrderWithoutRewritingHistory(bool postgres, CashOrderStatus status)
        => WithDatabaseAsync(postgres, async db =>
        {
            var (payment, userId) = await SeedAsync(db, "Asia/Novosibirsk");
            var oldOrder = new CashOrder
            {
                Number = "ПКО-2080-0042",
                Date = new DateTime(2080, 1, 2),
                Type = CashOrderType.Incoming,
                Status = status,
                Amount = 37m,
                PaymentId = payment.Id,
                Basis = "Сохранённое основание",
                Appendix = "Сохранённое приложение",
                Comment = "Сохранённый комментарий",
                CreatedAt = new DateTime(2080, 1, 2, 10, 0, 0, DateTimeKind.Utc),
            };
            db.CashOrders.Add(oldOrder);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var stored = await db.CashOrders.SingleAsync(order => order.Id == oldOrder.Id);
            var orderBefore = ScalarSnapshot(db, stored);
            var paymentBefore = await PaymentSnapshotAsync(db, payment.Id);
            var controller = CreateController(db, userId);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = Order(await controller.CreateFromPayment(payment.Id, CancellationToken.None));
                Assert.Equal(oldOrder.Id, result.Id);
                Assert.Equal(new DateTime(2080, 1, 2), result.Date);
                Assert.Equal("ПКО-2080-0042", result.Number);
                Assert.Equal(37m, result.Amount);
                Assert.Equal(status, result.Status);
            }
            var print = Assert.IsType<ContentResult>(await controller.PrintForPayment(payment.Id, CancellationToken.None));
            Assert.Contains("02.01.2080", print.Content);
            Assert.DoesNotContain("01.01.2099", print.Content);
            db.ChangeTracker.Clear();
            Assert.Equal(orderBefore, ScalarSnapshot(db, await db.CashOrders.SingleAsync()));
            Assert.Equal(paymentBefore, await PaymentSnapshotAsync(db, payment.Id));
            Assert.Empty(await db.MoneyOperations.ToListAsync());
        });

    private async Task WithDatabaseAsync(bool postgres, Func<PaymentContext, Task> action)
    {
        if (postgres)
        {
            await using var lease = await databases.CreateDatabaseAsync();
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(lease.ConnectionString).Options);
            await action(db);
            return;
        }
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var sqlite = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await sqlite.Database.EnsureCreatedAsync();
        await action(sqlite);
    }

    private static async Task<(Payment Payment, int UserId)> SeedAsync(PaymentContext db, string timeZoneId)
    {
        var user = new User
        {
            Email = $"cash-order-day-{Guid.NewGuid():N}@example.test",
            PasswordHash = "hash",
            FullName = "Синтетический бухгалтер",
            IsActive = true,
            IsApproved = true,
            Role = new Role { Name = "admin" },
        };
        var company = new LegalEntity { ShortName = "Синтетическая компания" };
        var source = new PaymentSource { Name = "Наличные", IsActive = true };
        db.Users.Add(user);
        db.LegalEntities.Add(company);
        db.PaymentSources.Add(source);
        await db.SaveChangesAsync();
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = timeZoneId,
        });
        db.SystemSettings.AddRange(
            new SystemSetting { Key = "legalEntities.defaultMainCompanyId", Value = company.Id.ToString(CultureInfo.InvariantCulture) },
            new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" });
        var payment = JsonSerializer.Deserialize<Payment>("""
            {"date":"2098-12-29","plannedDate":"2099-03-01","paidDate":"2098-12-30",
             "lastPaymentDate":"2098-12-31","paymentDocumentDate":"2099-01-01"}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        payment.Type = PaymentType.Income;
        payment.Status = PaymentStatus.Completed;
        payment.Amount = 100m;
        payment.PaidAmount = 100m;
        payment.IsPaid = true;
        payment.PaymentSourceId = source.Id;
        payment.DocumentKind = DocumentKind.Payment;
        payment.Description = "Синтетическая оплата для проверки календарного дня";
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return (payment, user.Id);
    }

    private static CashOrdersController CreateController(PaymentContext db, int userId) => new(db)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture)), new Claim(ClaimTypes.Role, "admin")], "Test")),
            },
        },
    };

    private static CashOrderDto CreatedOrder(IActionResult result) => Assert.IsType<CashOrderDto>(Assert.IsType<CreatedResult>(result).Value);

    private static CashOrderDto Order(IActionResult result) => Assert.IsType<CashOrderDto>(Assert.IsType<OkObjectResult>(result).Value);

    private static async Task<string> PaymentSnapshotAsync(PaymentContext db, int id)
        => ScalarSnapshot(db, await db.Payments.SingleAsync(payment => payment.Id == id));

    private static string ScalarSnapshot(PaymentContext db, object row)
        => JsonSerializer.Serialize(db.Entry(row).Properties.ToDictionary(property => property.Metadata.Name, property => property.CurrentValue));

    private static void AssertPrintedSelectedDay(string html)
    {
        Assert.Contains("01.01.2099", html);
        var receipt = html.IndexOf("receipt-date receipt-date-main", StringComparison.Ordinal);
        Assert.True(receipt >= 0, "Официальная форма должна содержать дату квитанции");
        Assert.Contains("<div class=\"line\">1</div>", html[receipt..]);
        Assert.Contains("января", html[receipt..]);
        Assert.Contains("<div class=\"line\">2099</div>", html[receipt..]);
    }
}
