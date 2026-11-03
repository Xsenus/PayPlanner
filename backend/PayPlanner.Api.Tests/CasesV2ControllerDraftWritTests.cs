using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> HistoricalWritCases() => from postgres in new[] { false, true } from legacy in new[] { false, true } from destination in new[] { "bank", "fssp", " BANK " } select new object[] { postgres, legacy, destination };

    [Theory]
    [MemberData(nameof(HistoricalWritCases))]
    public async Task CaseDraftWrit_HttpKeepsUnchangedHistoricalDestination(bool postgres, bool legacy, string destination)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (user, source, taskId) = await SeedHistoricalWritAsync(db, destination); var request = EditRequest(source); request.Title = "Только название";
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy), request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync(); Assert.Equal(request.Title, actual.Title); Assert.Null(actual.ExecutiveWritSubmittedAt); Assert.Equal(destination, actual.ExecutiveWritSubmittedTo);
        await AssertLegacyDateHistoryAsync(db, source, taskId); Assert.Single(await db.TaskJournalItems.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false, "new")]
    [InlineData(true, "new")]
    [InlineData(false, "destination")]
    [InlineData(true, "destination")]
    [InlineData(false, "receipt")]
    [InlineData(true, "receipt")]
    public async Task CaseDraftWrit_HttpCannotIntroduceOrChangeAnInvalidSubmission(bool legacy, string change)
    {
        await using var db = await CreateContextAsync(); var (user, source, taskId) = await SeedHistoricalWritAsync(db, change == "new" ? null : "bank"); var request = EditRequest(source); request.Title = "Не записывать";
        if (change == "receipt") request.ExecutiveWritReceivedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc); else request.ExecutiveWritSubmittedTo = "fssp";
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy), request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync(); Assert.Equal(source.Title, actual.Title); Assert.Null(actual.ExecutiveWritReceivedAt); Assert.Null(actual.ExecutiveWritSubmittedAt); Assert.Equal(source.ExecutiveWritSubmittedTo, actual.ExecutiveWritSubmittedTo);
        await AssertLegacyDateHistoryAsync(db, source, taskId); Assert.Single(await db.TaskJournalItems.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CaseDraftWrit_HttpAllowsAnExplicitRepairOrClear(bool legacy, bool clear)
    {
        await using var db = await CreateContextAsync(); var (user, source, taskId) = await SeedHistoricalWritAsync(db, "bank"); var request = EditRequest(source);
        if (clear) { request.ClearExecutiveWritSubmission = true; request.ExecutiveWritSubmittedTo = ""; }
        else { request.ExecutiveWritReceivedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc); request.ExecutiveWritSubmittedAt = request.ExecutiveWritReceivedAt.Value.AddDays(1); }
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy), request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync(); Assert.Equal(clear ? null : request.ExecutiveWritSubmittedAt, actual.ExecutiveWritSubmittedAt); Assert.Equal(clear ? null : "bank", actual.ExecutiveWritSubmittedTo);
        var task = await db.TaskJournalItems.AsNoTracking().SingleAsync(row => row.Id == taskId); Assert.Equal("done", task.Status); Assert.Equal("Сохранённый результат", task.Result); Assert.Equal(source.Id, task.ClientCaseId); Assert.Equal(source.CourtClaimAmount, actual.CourtClaimAmount); Assert.Empty(await db.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CaseDraftWrit_HttpUneditedSubmissionKeepsConcurrentRepair(bool postgres, bool legacy)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (user, source, taskId) = await SeedHistoricalWritAsync(db, "bank"); var request = EditRequest(source); request.Title = "Только название"; request.ClearExecutiveWritReceivedAt = true;
        var received = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc); var submitted = received.AddDays(1);
        await db.ClientCases.Where(row => row.Id == source.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ExecutiveWritReceivedAt, received).SetProperty(row => row.ExecutiveWritSubmittedAt, submitted).SetProperty(row => row.ExecutiveWritSubmittedTo, "fssp"));
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy), request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync(); Assert.Equal(received, actual.ExecutiveWritReceivedAt); Assert.Equal(submitted, actual.ExecutiveWritSubmittedAt); Assert.Equal("fssp", actual.ExecutiveWritSubmittedTo);
        await AssertLegacyDateHistoryAsync(db, source, taskId);
    }

    private static async Task<(User User, ClientCase Case, int TaskId)> SeedHistoricalWritAsync(PaymentContext db, string? destination)
    {
        var (user, source, taskId) = await SeedLegacyDatesAsync(db);
        source.CourtRegisteredAt = null; source.CourtDecisionAt = null; source.CassationDeadlineAt = null; source.AppealDeadlineAt = null; source.NextHearingAt = null; source.ExecutiveWritReceivedAt = null; source.ExecutiveWritSubmittedAt = null; source.ExecutiveWritSubmittedTo = destination;
        await db.SaveChangesAsync(); await db.Entry(source).ReloadAsync(); return (user, source, taskId);
    }
}
