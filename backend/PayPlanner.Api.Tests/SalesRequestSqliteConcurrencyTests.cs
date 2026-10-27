using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Requests;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SalesRequestSqliteConcurrencyTests
{
    [Fact]
    public async Task Transition_ReturnsConflictWhenAnotherWriterHoldsDatabase()
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = "pp_req_lock_" + Guid.NewGuid().ToString("N"), Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared, DefaultTimeout = 1, Pooling = false };
        var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(builder.ConnectionString).Options;
        await using var db = new PaymentContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var user = new User { Email = "sqlite-request-lock@example.test", FullName = "Тест", PasswordHash = "x", Role = new Role { Name = "admin" }, IsActive = true, IsApproved = true };
        var client = new Client { Name = "Клиент", Phone = "+79000000013" };
        db.AddRange(user, client);
        await db.SaveChangesAsync();
        var created = await new SalesRequestService(db).CreateAsync(new SalesRequestCreateInput("Call", client.Id, null, null, null, null, null, "Заявка", null, null, null), user.Id, default);
        Assert.True(created.Ok);
        var request = created.Value!;
        var paid = await db.SalesRequestStatuses.Where(status => status.Key == "paid").Select(status => status.Id).SingleAsync();
        await using var concurrent = new PaymentContext(options);
        await concurrent.Database.OpenConnectionAsync();
        await using var transaction = await concurrent.Database.BeginTransactionAsync();
        await concurrent.SalesRequests.Where(item => item.Id == request.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Summary, "Параллельное изменение"));
        var result = await new SalesRequestService(db).TransitionAsync(request.Id, new SalesRequestTransitionInput(paid, request.Version, null, false, null), user.Id, default);
        Assert.Equal(409, result.Status);
        Assert.Contains("сейчас изменяются", result.Error);
        await transaction.RollbackAsync();
        Assert.Null((await db.SalesRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).ResultClientCaseId);
    }
}
