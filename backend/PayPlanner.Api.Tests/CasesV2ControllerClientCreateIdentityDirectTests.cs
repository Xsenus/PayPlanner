using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ClientCreateIdentity_PassportBusinessEntryRejectsAssignedIdOrForeignOwner(bool v2, bool foreignOwner, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Clients); var owner = await db.Clients.Select(row => row.Id).SingleAsync(); var input = new Client { Name = "Неверная паспортная идентичность" };
        input.Passports.Add(new ClientPassport { Id = foreignOwner ? 0 : 987654, ClientId = foreignOwner ? owner : 0, Series = "AA", Number = "000111" });
        var result = await CreateGraphClientAsync(db, actor, input, v2); var clients = await db.Clients.CountAsync(); var stored = await db.ClientPassports.AsNoTracking().SingleOrDefaultAsync();
        Assert.True(result is BadRequestObjectResult && clients == 1 && stored is null, $"result={result.GetType().Name}; clients={clients}; passportId={stored?.Id}; owner={stored?.ClientId}; requestedOwner={(foreignOwner ? owner : 0)}"); Assert.False(db.ChangeTracker.HasChanges()); Assert.Empty(await db.AddressRecords.ToArrayAsync()); await AssertBulkCasesUnchangedAsync(db, ids);
    }
}
