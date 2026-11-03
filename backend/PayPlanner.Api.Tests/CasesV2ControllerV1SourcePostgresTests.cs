using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseV1Source_PgRejectsACompletedCompetingEdit(bool checkedRoute)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var db = Open(); var (actor, ids) = await SeedBulkCasesAsync(db); var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]);
        await using (var other = Open()) await other.ClientCases.Where(row => row.Id == source.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "Чужая завершённая правка"));
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}{(checkedRoute ? "/checked" : "")}", V1SourceBody(source, V1SourceWire(source)));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Equal("Чужая завершённая правка", (await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id)).Title); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Fact]
    public async Task CaseV1Source_PgPinsTheObservedCaseUntilTheCheckedWriteCompletes()
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, ids) = await SeedBulkCasesAsync(seed); var source = await seed.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var reached = false; var committed = false; string? blocked = null;
        var hook = new ConfirmationBeforeSave(async ct =>
        {
            await using var other = Open(); await using var transaction = await other.Database.BeginTransactionAsync(ct); await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct); reached = true;
            try { await other.ClientCases.Where(row => row.Id == source.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "Параллельная правка"), ct); await transaction.CommitAsync(ct); committed = true; }
            catch (Exception error)
            {
                Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException;
                if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } conflict) blocked = conflict.SqlState; else throw;
            }
        });
        await using var request = Open(hook); using var server = CaseWriteServer(request, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}/checked", V1SourceBody(source, V1SourceWire(source)));
        Assert.True(hook.Called); Assert.True(reached); Assert.False(committed); Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked); Assert.Null(request.Database.CurrentTransaction); await using var verify = Open();
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict); Assert.Equal(response.StatusCode == HttpStatusCode.OK ? "Моя правка V1" : source.Title, (await verify.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id)).Title); await AssertBulkCasesUnchangedAsync(verify, ids);
    }
}
