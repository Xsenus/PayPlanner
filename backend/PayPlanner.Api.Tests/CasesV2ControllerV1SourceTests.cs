using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static Dictionary<string, object?> V1SourceWire(ClientCase source) => new()
    {
        ["id"] = source.Id,
        ["title"] = source.Title,
        ["description"] = source.Description,
        ["status"] = source.Status.ToString(),
        ["clientId"] = source.ClientId,
        ["legalEntityId"] = source.LegalEntityId,
    };

    private static object V1SourceBody(ClientCase source, object? snapshot)
        => new { source.ClientId, source.LegalEntityId, Title = "Моя правка V1", source.Description, source.Status, ExpectedSource = snapshot };

    [Fact]
    public Task CaseV1Source_LegacyRouteRequiresSource() => CheckV1MissingSourceAsync(false);
    [Fact]
    public Task CaseV1Source_CheckedRouteRequiresSource() => CheckV1MissingSourceAsync(true);

    private async Task CheckV1MissingSourceAsync(bool checkedRoute)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]);
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}{(checkedRoute ? "/checked" : "")}", V1SourceBody(source, null));
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"HTTP {(int)response.StatusCode}; missingSource=true"); Assert.Equal(source.Title, (await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id)).Title); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    public static IEnumerable<object[]> V1SourceRequiredFields() => from route in new[] { false, true } from field in new[] { "id", "title", "description", "status", "owners" } select new object[] { route, field };
    [Theory]
    [MemberData(nameof(V1SourceRequiredFields))]
    public async Task CaseV1Source_HttpRejectsAnIncompleteSnapshot(bool checkedRoute, string field)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var snapshot = V1SourceWire(source);
        if (field == "owners") { snapshot.Remove("clientId"); snapshot.Remove("legalEntityId"); } else snapshot.Remove(field);
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}{(checkedRoute ? "/checked" : "")}", V1SourceBody(source, snapshot));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(source.Title, (await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id)).Title); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false, "title")]
    [InlineData(false, "description")]
    [InlineData(true, "title")]
    [InlineData(true, "description")]
    public async Task CaseV1Source_HttpRejectsNullSnapshotText(bool checkedRoute, string field)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var snapshot = V1SourceWire(source); snapshot[field] = null;
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}{(checkedRoute ? "/checked" : "")}", V1SourceBody(source, snapshot));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); await AssertBulkCasesUnchangedAsync(db, ids); Assert.Equal(source.Title, (await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id)).Title);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseV1Source_HttpAcceptsTheOmittedNullOwnerFromARead(bool checkedRoute, bool legal)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db);
        if (legal) { var owner = new LegalEntity { ShortName = "Владелец снимка", ResponsibleUserId = actor.Id }; db.LegalEntities.Add(owner); await db.SaveChangesAsync(); await db.ClientCases.Where(row => row.Id == ids[0]).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientId, (int?)null).SetProperty(row => row.LegalEntityId, owner.Id)); }
        var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var snapshot = V1SourceWire(source); snapshot.Remove(legal ? "clientId" : "legalEntityId");
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}{(checkedRoute ? "/checked" : "")}", V1SourceBody(source, snapshot));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("Моя правка V1", (await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id)).Title); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    public static IEnumerable<object[]> V1SourceMergeFields() => from field in new[] { "Title", "Description", "Status", "ClientId", "LegalEntityId" } from edited in new[] { false, true } select new object[] { field, edited };
    [Theory]
    [MemberData(nameof(V1SourceMergeFields))]
    public Task CaseV1Source_LegacyMergeChecksExactlyFiveFields(string field, bool edited) => CheckV1SourceMergeAsync(field, edited, false);
    [Theory]
    [MemberData(nameof(V1SourceMergeFields))]
    public Task CaseV1Source_CheckedMergeChecksExactlyFiveFields(string field, bool edited) => CheckV1SourceMergeAsync(field, edited, true);

    private async Task CheckV1SourceMergeAsync(string field, bool edited, bool checkedRoute)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]);
        object actualValue, wantedValue; var property = typeof(ClientCase).GetProperty(field)!;
        if (field == "ClientId") { var next = new Client { Name = "Другой владелец", IsActive = true, ResponsibleUserId = actor.Id }; var wanted = new Client { Name = "Выбранный владелец", IsActive = true, ResponsibleUserId = actor.Id }; db.Clients.AddRange(next, wanted); await db.SaveChangesAsync(); actualValue = next.Id; wantedValue = wanted.Id; }
        else if (field == "LegalEntityId")
        {
            var first = new LegalEntity { ShortName = "Прежняя организация" }; var next = new LegalEntity { ShortName = "Другая организация" }; var wanted = new LegalEntity { ShortName = "Выбранная организация" }; db.LegalEntities.AddRange(first, next, wanted); await db.SaveChangesAsync();
            await db.ClientCases.Where(row => row.Id == source.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientId, (int?)null).SetProperty(row => row.LegalEntityId, first.Id)); source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id); actualValue = next.Id; wantedValue = wanted.Id;
        }
        else if (field == "Status") { actualValue = ClientCaseStatus.Closed; wantedValue = ClientCaseStatus.Lost; }
        else { actualValue = "Чужая правка"; wantedValue = "Моя правка"; }
        db.ChangeTracker.Clear(); var input = EditRequest(source); if (field == "Title") input.Description = "Моё независимое описание"; else input.Title = "Моё независимое название"; if (edited) property.SetValue(input, wantedValue);
        var concurrent = await db.ClientCases.SingleAsync(row => row.Id == source.Id); property.SetValue(concurrent, actualValue); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}{(checkedRoute ? "/checked" : "")}", input);
        var conflict = edited || field is "ClientId" or "LegalEntityId"; var saved = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id);
        Assert.True(response.StatusCode == (conflict ? HttpStatusCode.Conflict : HttpStatusCode.OK) && Equals(actualValue, property.GetValue(saved)), $"HTTP {(int)response.StatusCode}; field={field}; overwritten={!Equals(actualValue, property.GetValue(saved))}; edited={edited}");
        if (!conflict) Assert.Equal(field == "Title" ? "Моё независимое описание" : "Моё независимое название", field == "Title" ? saved.Description : saved.Title);
        Assert.Equal(source.EventHistory, saved.EventHistory); Assert.Equal(source.CourtClaimAmount, saved.CourtClaimAmount); Assert.Equal(3, await db.TaskJournalItems.CountAsync()); Assert.Equal("Сохранённый результат", (await db.TaskJournalItems.SingleAsync(row => row.Title == "Завершённая задача")).Result); Assert.Empty(await db.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseV1Source_HttpRejectsAnotherCaseSnapshot(bool checkedRoute)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var snapshot = V1SourceWire(source); snapshot["id"] = ids[1];
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}{(checkedRoute ? "/checked" : "")}", V1SourceBody(source, snapshot));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); await AssertBulkCasesUnchangedAsync(db, ids); Assert.Equal(source.Title, (await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id)).Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseV1Source_HttpAcceptsAnExactRetryWithoutDuplicateTasks(bool checkedRoute)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var input = EditRequest(source); input.Title = "Повторяемая правка";
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());
        using var first = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}{(checkedRoute ? "/checked" : "")}", input); using var retry = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}{(checkedRoute ? "/checked" : "")}", input);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode); Assert.Equal(HttpStatusCode.OK, retry.StatusCode); Assert.Equal(input.Title, (await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id)).Title); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Fact]
    public async Task CaseV1Source_HttpIgnoresUnwrittenSnapshotAndRequestFields()
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var input = EditRequest(source); input.Title = "Пять полей V1";
        input.ExpectedSource!.FeeAmount = -99m; input.ExpectedSource.CourtName = "Другое исходное значение"; input.ExpectedSource.EventHistory = "Не сравнивать эту историю"; input.ResponsibleUserId = int.MaxValue; input.FeeAmount = 777m; input.CourtName = "Не сохранять суд"; input.EventHistory = "Не сохранять историю";
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}/checked", input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var saved = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id); Assert.Equal(input.Title, saved.Title); Assert.Equal(source.FeeAmount, saved.FeeAmount); Assert.Equal(source.CourtName, saved.CourtName); Assert.Equal(source.ResponsibleUserId, saved.ResponsibleUserId); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseV1Source_StaleEditorDoesNotReopenAClosedCaseOrCancelItsReport(bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, ids) = await SeedBulkCasesAsync(db); var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var close = EditRequest(source); close.Status = ClientCaseStatus.Closed; var stale = EditRequest(source); stale.Title = "Независимая правка названия";
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var closed = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}", close); Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        using var response = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}", stale); Assert.Equal(HttpStatusCode.OK, response.StatusCode); var saved = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id); var report = await db.TaskJournalItems.AsNoTracking().SingleAsync(row => row.IsCaseFinalReport);
        Assert.True(saved.Status == ClientCaseStatus.Closed && report.Status == "new", $"status={saved.Status}; reportStatus={report.Status}"); Assert.Equal(stale.Title, saved.Title); Assert.Equal(4, await db.TaskJournalItems.CountAsync()); Assert.Equal("Сохранённый результат", (await db.TaskJournalItems.SingleAsync(row => row.Title == "Завершённая задача")).Result); Assert.Empty(await db.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseV1Source_InjectedCourtFieldsCannotClassifyAnOrdinaryCase(bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, ids) = await SeedBulkCasesAsync(db); await db.SystemSettings.ExecuteDeleteAsync(); var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[1]); var input = EditRequest(source); input.Status = ClientCaseStatus.Closed; input.CourtName = "Игнорируемый суд";
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}", input);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"HTTP {(int)response.StatusCode}; ordinaryStoredCase=true"); var saved = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id); Assert.Equal(ClientCaseStatus.Closed, saved.Status); Assert.Equal(source.CourtName, saved.CourtName); Assert.False(await db.TaskJournalItems.AnyAsync(row => row.IsCaseFinalReport)); Assert.Equal(3, await db.TaskJournalItems.CountAsync()); Assert.Equal("Сохранённый результат", (await db.TaskJournalItems.SingleAsync(row => row.Title == "Завершённая задача")).Result); Assert.Empty(await db.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseV1Source_StoredCourtFieldsStillCreateTheFinalReport(bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, ids) = await SeedBulkCasesAsync(db); var source = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var input = EditRequest(source); input.Status = ClientCaseStatus.Closed; input.CourtName = "";
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.PutAsJsonAsync($"/api/v1/cases/{source.Id}/checked", input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("new", (await db.TaskJournalItems.AsNoTracking().SingleAsync(row => row.IsCaseFinalReport)).Status); Assert.Equal(source.CourtName, (await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id)).CourtName); Assert.Equal(4, await db.TaskJournalItems.CountAsync()); Assert.Empty(await db.Payments.ToArrayAsync());
    }
}
