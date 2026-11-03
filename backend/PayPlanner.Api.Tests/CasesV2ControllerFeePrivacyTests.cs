using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private const decimal RequestedPrivateCaseFee = 817.77m;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseFeePrivacy_RootHttpChangesFeeOnlyWithFinanceAccess(bool postgres, bool finance)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, source, ids) = await SeedReadPrivacyAsync(db, edit: true, finance, legal: false); var input = EditRequest(source); input.Title = "Изменённое название"; input.FeeAmount = RequestedPrivateCaseFee;
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.PutAsJsonAsync($"/api/cases/{source.Id}/checked", input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var row = await response.Content.ReadFromJsonAsync<JsonElement>(); var actual = await db.ClientCases.AsNoTracking().SingleAsync(item => item.Id == source.Id);
        Assert.True(actual.FeeAmount == (finance ? RequestedPrivateCaseFee : PrivateCaseFee) && row.GetProperty("feeAmount").GetDecimal() == (finance ? RequestedPrivateCaseFee : 0m), $"HTTP {(int)response.StatusCode}; storedFee={actual.FeeAmount}; replyFee={row.GetProperty("feeAmount").GetDecimal()}; finance={finance}");
        Assert.Equal(input.Title, actual.Title); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Fact]
    public async Task CaseFeePrivacy_RootBusinessEntryCannotChangeHiddenFeeWithoutASnapshot()
    {
        await using var db = await CreateContextAsync(); var (actor, source, ids) = await SeedReadPrivacyAsync(db, edit: true, finance: false, legal: false); var input = EditRequest(source); input.FeeAmount = RequestedPrivateCaseFee; input.Title = "Обычная правка";
        var controller = new CasesController(db) { ControllerContext = CreateController(db, actor).ControllerContext }; var result = Assert.IsType<OkObjectResult>(await controller.Update(source.Id, input, CancellationToken.None)); var reply = Assert.IsType<ClientCase>(result.Value);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id); Assert.True(actual.FeeAmount == PrivateCaseFee && reply.FeeAmount == 0m, $"storedFee={actual.FeeAmount}; replyFee={reply.FeeAmount}"); Assert.Equal(input.Title, actual.Title); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Fact]
    public async Task CaseFeePrivacy_RootHttpKeepsAMaskedFeeThroughAnOrdinaryTitleEdit()
    {
        await using var db = await CreateContextAsync(); var (actor, source, ids) = await SeedReadPrivacyAsync(db, edit: true, finance: false, legal: false); var input = EditRequest(source); input.ExpectedSource!.FeeAmount = 0m; input.FeeAmount = 0m; input.Title = "Название без изменения гонорара";
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.PutAsJsonAsync($"/api/cases/{source.Id}/checked", input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var row = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(0m, row.GetProperty("feeAmount").GetDecimal()); Assert.Equal(PrivateCaseFee, (await db.ClientCases.AsNoTracking().SingleAsync(item => item.Id == source.Id)).FeeAmount); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseFeePrivacy_V1KeepsItsIgnoredFeeAndMasksTheReply(bool finance)
    {
        await using var db = await CreateContextAsync(); var (actor, source, ids) = await SeedReadPrivacyAsync(db, edit: true, finance, legal: false); var input = V1CaseInput(source); input.Title = "Обычная правка V1"; input.FeeAmount = RequestedPrivateCaseFee;
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}", input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var row = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(finance ? PrivateCaseFee : 0m, row.GetProperty("feeAmount").GetDecimal()); Assert.Equal(PrivateCaseFee, (await db.ClientCases.AsNoTracking().SingleAsync(item => item.Id == source.Id)).FeeAmount); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData("root", false)]
    [InlineData("root", true)]
    [InlineData("v1", false)]
    [InlineData("v1", true)]
    [InlineData("v2", false)]
    [InlineData("v2", true)]
    public async Task CaseFeePrivacy_HttpCreateUsesTheSameFinanceRuleInAllVersions(string version, bool finance)
    {
        await using var db = await CreateContextAsync(); var (actor, source, ids) = await SeedReadPrivacyAsync(db, edit: true, finance, legal: false); var route = version == "root" ? "cases" : version + "/cases";
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.PostAsJsonAsync($"/api/{route}", new ClientCase { ClientId = source.ClientId, Title = "Новое дело с гонораром", Status = ClientCaseStatus.Open, FeeAmount = RequestedPrivateCaseFee });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); var row = await response.Content.ReadFromJsonAsync<JsonElement>(); var id = row.GetProperty("id").GetInt32(); var actual = await db.ClientCases.AsNoTracking().SingleAsync(item => item.Id == id);
        Assert.True(actual.FeeAmount == (finance ? RequestedPrivateCaseFee : 0m) && row.GetProperty("feeAmount").GetDecimal() == actual.FeeAmount, $"HTTP {(int)response.StatusCode}; storedNewFee={actual.FeeAmount}; finance={finance}; version={version}");
        Assert.Equal(ids.Length + 1, await db.ClientCases.CountAsync()); Assert.Equal(PrivateCaseFee, (await db.ClientCases.AsNoTracking().SingleAsync(item => item.Id == source.Id)).FeeAmount); Assert.Empty(await db.Payments.ToArrayAsync()); Assert.Equal("Сохранённый результат", (await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Status == "done")).Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseFeePrivacy_PgPinsFinancePermissionUntilTheWriteCommits(bool v1)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(interceptors).Options);
        await using var seed = Open(); var (actor, source, ids) = await SeedReadPrivacyAsync(seed, edit: true, finance: true, legal: false); var input = EditRequest(source); input.Title = "Разрешённая правка"; input.FeeAmount = RequestedPrivateCaseFee;
        var reached = false; var committed = false; string? blocked = null;
        var hook = new ConfirmationBeforeSave(async ct =>
        {
            await using var other = Open(); await using var transaction = await other.Database.BeginTransactionAsync(ct); await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct); reached = true;
            try { await other.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Payments).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false), ct); await transaction.CommitAsync(ct); committed = true; }
            catch (Exception error)
            {
                Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException;
                if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } conflict) blocked = conflict.SqlState; else throw;
            }
        });
        await using var request = Open(hook); IActionResult result = v1 ? await UpdateV1CaseAsync(request, actor, source, input, CancellationToken.None) : await new CasesController(request) { ControllerContext = CreateController(request, actor).ControllerContext }.Update(source.Id, input, CancellationToken.None, input.ExpectedSource);
        Assert.True(hook.Called); Assert.True(reached); Assert.False(committed); Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked); Assert.Null(request.Database.CurrentTransaction); await using var verify = Open();
        if (result is ConflictObjectResult) { Assert.Equal(source.Title, (await verify.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id)).Title); Assert.Equal(PrivateCaseFee, (await verify.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id)).FeeAmount); }
        else { var reply = Assert.IsType<ClientCase>(Assert.IsType<OkObjectResult>(result).Value); Assert.Equal(v1 ? PrivateCaseFee : RequestedPrivateCaseFee, reply.FeeAmount); Assert.Equal(reply.FeeAmount, (await verify.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id)).FeeAmount); }
        Assert.True(await verify.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Payments).Select(row => row.CanView).SingleAsync()); await AssertBulkCasesUnchangedAsync(verify, ids);
    }
}
