using System.Data;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public class SqliteTestContextFactoryTests
{
    [Fact]
    public async Task CreateAsync_ReturnsIsolatedDatabaseForEveryContext()
    {
        await using (var first = await SqliteTestContextFactory.CreateAsync())
        {
            first.Clients.Add(new Client { Name = "Isolated template client" });
            await first.SaveChangesAsync();
            Assert.Single(await first.Clients.ToListAsync());
        }

        await using var second = await SqliteTestContextFactory.CreateAsync();
        Assert.Empty(await second.Clients.ToListAsync());
    }

    [Fact]
    public async Task DisposeAsync_ClosesOwnedConnection()
    {
        var context = await SqliteTestContextFactory.CreateAsync(useObjectAuditModel: false);
        var connection = context.Database.GetDbConnection();

        await context.DisposeAsync();

        Assert.Equal(ConnectionState.Closed, connection.State);
    }
}
