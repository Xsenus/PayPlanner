using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Fact]
    public async Task CashCreateGraph_HttpCannotCreateNestedPaymentWithoutOperationKey()
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Payments);
        var owner = await db.ClientCases.Where(row => row.Id == ids[0]).Select(row => row.ClientId).SingleAsync();
        var input = new CashOrder { Number = "synthetic-nested-payment", Type = CashOrderType.Incoming, Status = CashOrderStatus.Draft, Amount = 99.95m, Currency = "RUB", Payment = new Payment { ClientId = owner, Amount = 99.95m, Type = PaymentType.Income, Status = PaymentStatus.Pending } };
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString()); using var response = await client.PostAsJsonAsync("/api/cash-orders", input);
        var payments = await db.Payments.CountAsync(); var orders = await db.CashOrders.CountAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest && payments == 0 && orders == 0, $"HTTP {(int)response.StatusCode}; payments={payments}; cashOrders={orders}");
        Assert.Contains("отдельно", await response.Content.ReadAsStringAsync()); db.ChangeTracker.Clear(); await AssertBulkCasesUnchangedAsync(db, ids); Assert.Equal(1, await db.Clients.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CashCreateGraph_PgRejectsNestedPaymentWithOrWithoutKeyHeader(bool header)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        var (user, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Payments); var owner = await db.ClientCases.Where(row => row.Id == ids[0]).Select(row => row.ClientId).SingleAsync();
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString()); if (header) client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.PostAsJsonAsync("/api/cash-orders", new CashOrder { Number = "synthetic-pg-nested", Amount = 99.95m, Currency = "RUB", Status = CashOrderStatus.Draft, Payment = new Payment { ClientId = owner, Amount = 99.95m, Type = PaymentType.Income, Status = PaymentStatus.Pending } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Contains("отдельно", await response.Content.ReadAsStringAsync()); Assert.Empty(await db.CashOrders.ToArrayAsync()); db.ChangeTracker.Clear(); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Fact]
    public async Task CashCreateGraph_AllMappedReferencesAreRejectedBeforeWriting()
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Payments);
        var entity = db.Model.FindEntityType(typeof(CashOrder))!; var navigations = entity.GetNavigations().ToArray(); Assert.Equal(4, navigations.Length); Assert.Empty(entity.GetSkipNavigations());
        foreach (var navigation in navigations)
        {
            var input = new CashOrder { Number = "synthetic-" + navigation.Name, Amount = 99.95m, Currency = "RUB", Status = CashOrderStatus.Draft }; navigation.PropertyInfo!.SetValue(input, Activator.CreateInstance(navigation.TargetEntityType.ClrType));
            var controller = new CashOrdersController(db) { ControllerContext = CreateController(db, user).ControllerContext }; var result = Assert.IsType<BadRequestObjectResult>(await controller.Create(input, CancellationToken.None));
            Assert.Contains("отдельно", System.Text.Json.JsonSerializer.SerializeToElement(result.Value).GetString());
            Assert.False(db.ChangeTracker.HasChanges()); Assert.Empty(await db.CashOrders.ToArrayAsync()); await AssertBulkCasesUnchangedAsync(db, ids);
        }
        Assert.Empty(await db.MoneyOperations.ToArrayAsync()); Assert.Empty(await db.Invoices.ToArrayAsync()); Assert.Equal(1, await db.Clients.CountAsync());
    }

    [Fact]
    public async Task CashCreateGraph_HttpRejectsPaymentInsideANestedClient()
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Payments);
        var input = new CashOrder { Number = "synthetic-client", Amount = 99.95m, Currency = "RUB", Status = CashOrderStatus.Draft, Client = new Client { Name = "Незапрошенный клиент", Payments = [new Payment { Amount = 99.95m, Type = PaymentType.Income, Status = PaymentStatus.Pending }] } };
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString()); using var response = await client.PostAsJsonAsync("/api/cash-orders", input);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Contains("отдельно", await response.Content.ReadAsStringAsync()); db.ChangeTracker.Clear(); await AssertBulkCasesUnchangedAsync(db, ids); Assert.Empty(await db.CashOrders.ToArrayAsync()); Assert.Equal(1, await db.Clients.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CashCreateGraph_HttpKeepsOrdinaryManualOrderWithoutPaymentKey(bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (user, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, user, RolePermissionCatalog.Payments);
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync("/api/cash-orders", new CashOrder { Number = " synthetic-manual ", Amount = 99.95m, Currency = " RUB ", Type = CashOrderType.Incoming, Status = CashOrderStatus.Draft, Date = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc), CreatedByUserId = 999 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(); var id = json.GetProperty("id").GetInt32(); Assert.True(id > 0); Assert.EndsWith($"/{id}", response.Headers.Location!.ToString());
        var order = await db.CashOrders.AsNoTracking().SingleAsync(); Assert.Equal("synthetic-manual", order.Number); Assert.Equal(99.95m, order.Amount); Assert.Equal("RUB", order.Currency); Assert.Equal(CashOrderStatus.Draft, order.Status); Assert.Equal(user.Id, order.CreatedByUserId); Assert.Equal(new DateTime(2026, 10, 30), order.Date.Date); Assert.Null(order.PaymentId); Assert.Null(order.MoneyOperationId); Assert.Null(order.InvoiceId); Assert.Null(order.ClientId);
        await AssertBulkCasesUnchangedAsync(db, ids); Assert.Empty(await db.MoneyOperations.ToArrayAsync());
    }
}
