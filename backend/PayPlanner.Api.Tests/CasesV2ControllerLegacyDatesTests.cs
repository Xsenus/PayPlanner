using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static readonly string[] LegacyDateFields = [nameof(ClientCase.CourtRegisteredAt), nameof(ClientCase.CourtDecisionAt), nameof(ClientCase.CassationDeadlineAt)];
    private static readonly JsonSerializerOptions LegacyDateJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public static IEnumerable<object[]> LegacyDateCases() => from postgres in new[] { false, true } from field in LegacyDateFields select new object[] { postgres, field };
    public static IEnumerable<object[]> LegacyDateMergeCases() => from row in LegacyDateCases() from edited in new[] { false, true } select new object[] { row[0], row[1], edited };

    [Theory]
    [MemberData(nameof(LegacyDateCases))]
    public async Task CaseLegacyDates_HttpActuallySavesEachRequestedDate(bool postgres, string field)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (user, source, taskId) = await SeedLegacyDatesAsync(db); var request = EditRequest(source);
        var value = new DateTime(2027, 3, 17, 0, 0, 0, DateTimeKind.Utc); LegacyDateSet(request, field, value);
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy: true), request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync(); Assert.Equal(value, LegacyDateGet(actual, field));
        var reply = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(value, reply.GetProperty(JsonNamingPolicy.CamelCase.ConvertName(field)).GetDateTime());
        foreach (var other in LegacyDateFields.Where(name => name != field)) Assert.Equal(LegacyDateGet(source, other), LegacyDateGet(actual, other));
        await AssertLegacyDateHistoryAsync(db, source, taskId);
    }

    [Theory]
    [MemberData(nameof(LegacyDateCases))]
    public async Task CaseLegacyDates_HttpAcceptsDateOnlyValueFromCaseForm(bool postgres, string field)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (user, source, taskId) = await SeedLegacyDatesAsync(db);
        var body = JsonSerializer.SerializeToNode(EditRequest(source), LegacyDateJson)!.AsObject();
        body[JsonNamingPolicy.CamelCase.ConvertName(field)] = "2027-03-17";
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy: true), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expected = new DateTime(2027, 3, 17);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync(); Assert.Equal(expected, LegacyDateGet(actual, field));
        var reply = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(expected, reply.GetProperty(JsonNamingPolicy.CamelCase.ConvertName(field)).GetDateTime());
        foreach (var other in LegacyDateFields.Where(name => name != field)) Assert.Equal(LegacyDateGet(source, other), LegacyDateGet(actual, other));
        await AssertLegacyDateHistoryAsync(db, source, taskId);
    }

    [Theory]
    [InlineData("CourtRegisteredAt")]
    [InlineData("CourtDecisionAt")]
    [InlineData("CassationDeadlineAt")]
    public async Task CaseLegacyDates_HttpExplicitClearWorksOnOriginalRoute(string field)
    {
        await using var db = await CreateContextAsync(); var (user, source, taskId) = await SeedLegacyDatesAsync(db); var request = EditRequest(source);
        LegacyDateSet(request, field, null); if (field == nameof(ClientCase.CourtRegisteredAt)) request.ClearCourtRegisteredAt = true;
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy: true, oldRoute: true), request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Null(LegacyDateGet(await db.ClientCases.AsNoTracking().SingleAsync(), field));
        var reply = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(JsonValueKind.Null, reply.GetProperty(JsonNamingPolicy.CamelCase.ConvertName(field)).ValueKind);
        await AssertLegacyDateHistoryAsync(db, source, taskId);
    }

    [Theory]
    [InlineData("CourtRegisteredAt")]
    [InlineData("CourtDecisionAt")]
    [InlineData("CassationDeadlineAt")]
    public async Task CaseLegacyDates_HttpOmissionPreservesFreshDateEvenWithOldSource(string field)
    {
        await using var db = await CreateContextAsync(); var (user, source, taskId) = await SeedLegacyDatesAsync(db);
        var body = JsonSerializer.SerializeToNode(EditRequest(source), LegacyDateJson)!.AsObject(); foreach (var name in LegacyDateFields) body.Remove(JsonNamingPolicy.CamelCase.ConvertName(name));
        body["title"] = "Только название"; body["hasCourtDecisionAt"] = true; body["hasCassationDeadlineAt"] = true;
        var value = new DateTime(2027, 4, 19, 0, 0, 0, DateTimeKind.Utc); await SetLegacyDateInDatabaseAsync(db, source.Id, field, value);
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy: true), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var actual = await db.ClientCases.AsNoTracking().SingleAsync(); Assert.Equal(value, LegacyDateGet(actual, field)); Assert.Equal("Только название", actual.Title);
        await AssertLegacyDateHistoryAsync(db, source, taskId);
    }

    [Theory]
    [MemberData(nameof(LegacyDateMergeCases))]
    public async Task CaseLegacyDates_HttpConcurrentDateIsPreservedOrConflictingEditRejected(bool postgres, string field, bool edited)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (user, source, taskId) = await SeedLegacyDatesAsync(db); var request = EditRequest(source); request.Title = "Моё название";
        var current = new DateTime(2027, 4, 19, 0, 0, 0, DateTimeKind.Utc); await SetLegacyDateInDatabaseAsync(db, source.Id, field, current);
        if (edited) LegacyDateSet(request, field, current.AddDays(1));
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy: true), request);
        Assert.Equal(edited ? HttpStatusCode.Conflict : HttpStatusCode.OK, response.StatusCode);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync(); Assert.Equal(current, LegacyDateGet(actual, field)); Assert.Equal(edited ? source.Title : request.Title, actual.Title);
        await AssertLegacyDateHistoryAsync(db, source, taskId);
    }

    [Theory]
    [InlineData("CourtRegisteredAt")]
    [InlineData("CourtDecisionAt")]
    [InlineData("CassationDeadlineAt")]
    public async Task CaseLegacyDates_InvalidDateDoesNotWriteAnything(string field)
    {
        await using var db = await CreateContextAsync(); var (user, source, taskId) = await SeedLegacyDatesAsync(db);
        var body = JsonSerializer.SerializeToNode(EditRequest(source), LegacyDateJson)!.AsObject(); body[JsonNamingPolicy.CamelCase.ConvertName(field)] = "not-a-date";
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy: true), body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(LegacyDateGet(source, field), LegacyDateGet(await db.ClientCases.AsNoTracking().SingleAsync(), field));
        await AssertLegacyDateHistoryAsync(db, source, taskId);
    }

    private static DateTime? LegacyDateGet(ClientCase value, string field) => (DateTime?)typeof(ClientCase).GetProperty(field)!.GetValue(value);
    private static void LegacyDateSet(ClientCase value, string field, DateTime? date) => typeof(ClientCase).GetProperty(field)!.SetValue(value, date);
    private static Task<int> SetLegacyDateInDatabaseAsync(PaymentContext db, int id, string field, DateTime value) => field switch
    {
        nameof(ClientCase.CourtRegisteredAt) => db.ClientCases.Where(row => row.Id == id).ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtRegisteredAt, value)),
        nameof(ClientCase.CourtDecisionAt) => db.ClientCases.Where(row => row.Id == id).ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtDecisionAt, value)),
        _ => db.ClientCases.Where(row => row.Id == id).ExecuteUpdateAsync(set => set.SetProperty(row => row.CassationDeadlineAt, value)),
    };
    private static async Task<(User User, ClientCase Case, int TaskId)> SeedLegacyDatesAsync(PaymentContext db)
    {
        var (user, source) = await SeedConfirmationCaseAsync(db);
        source.CourtRegisteredAt = new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc); source.CourtDecisionAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc); source.CassationDeadlineAt = new DateTime(2027, 2, 18, 0, 0, 0, DateTimeKind.Utc); source.AppealDeadlineAt = new DateTime(2026, 12, 14, 0, 0, 0, DateTimeKind.Utc);
        source.EventHistory = "История без изменений";
        var task = new TaskJournalItem { ClientCaseId = source.Id, Title = "Завершённая ручная задача", Status = "done", Result = "Сохранённый результат", TagsJson = "[]" };
        db.TaskJournalItems.Add(task); await db.SaveChangesAsync(); await db.Entry(source).ReloadAsync(); return (user, source, task.Id);
    }
    private static async Task AssertLegacyDateHistoryAsync(PaymentContext db, ClientCase before, int taskId)
    {
        var actual = await db.ClientCases.AsNoTracking().SingleAsync(); Assert.Equal(before.EventHistory, actual.EventHistory); Assert.Equal(123.45m, actual.CourtClaimAmount);
        var task = await db.TaskJournalItems.AsNoTracking().SingleAsync(row => row.Id == taskId); Assert.Equal("done", task.Status); Assert.Equal("Сохранённый результат", task.Result); Assert.Equal(before.Id, task.ClientCaseId);
        Assert.Empty(await db.Payments.ToArrayAsync()); Assert.Null(db.Database.CurrentTransaction);
    }
}
