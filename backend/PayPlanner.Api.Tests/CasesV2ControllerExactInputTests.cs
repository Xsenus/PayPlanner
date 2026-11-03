using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseExactInput_HttpPreservesUntouchedTextDatesAndConfirmations(bool postgres, bool legacy)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (user, source) = await SeedExactInputCaseAsync(db);
        var request = EditRequest(source); request.Title = "Только новое название";
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy), request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Equal(source.PositionReviewedAt, actual.PositionReviewedAt);
        Assert.Equal(source.NextHearingConfirmedAt, actual.NextHearingConfirmedAt);
        Assert.Equal(source.NextHearingConfirmedForAt, actual.NextHearingConfirmedForAt);
        Assert.Equal(source.Strategy, actual.Strategy); Assert.Equal(source.EventHistory, actual.EventHistory);
        Assert.Equal(source.DocumentsSummary, actual.DocumentsSummary); Assert.Equal(source.CaseNumber, actual.CaseNumber); Assert.Equal(source.CourtName, actual.CourtName);
        Assert.Equal(source.NextHearingAt, actual.NextHearingAt); Assert.Equal(source.StartDate, actual.StartDate);
        Assert.Equal(request.Title, actual.Title); Assert.Equal(123.45m, actual.CourtClaimAmount); Assert.Empty(await db.Payments.ToArrayAsync());
        var reply = await response.Content.ReadFromJsonAsync<ClientCase>(new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } }); Assert.NotNull(reply);
        Assert.Equal(actual.Strategy, reply.Strategy); Assert.Equal(actual.EventHistory, reply.EventHistory);
        Assert.Equal(actual.PositionReviewedAt, reply.PositionReviewedAt);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseExactInput_HttpNormalizesDeliberateChangesAndClearsOnlyRelatedConfirmations(bool postgres, bool legacy)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (user, source) = await SeedExactInputCaseAsync(db);
        var request = EditRequest(source); request.Strategy = "  Новая позиция  "; request.EventHistory = "  Моя новая запись  "; request.NextHearingAt = source.NextHearingAt!.Value.AddDays(1);
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy), request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Equal("Новая позиция", actual.Strategy); Assert.Equal("Моя новая запись", actual.EventHistory);
        Assert.Equal(request.NextHearingAt, actual.NextHearingAt); Assert.Null(actual.PositionReviewedAt); Assert.Null(actual.NextHearingConfirmedAt); Assert.Null(actual.NextHearingConfirmedForAt);
        Assert.Equal(source.StartDate, actual.StartDate); Assert.Equal(123.45m, actual.CourtClaimAmount); Assert.Empty(await db.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseExactInput_HttpMergeKeepsParallelHistoryWhitespaceAndReview(bool postgres, bool legacy)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (user, source) = await SeedExactInputCaseAsync(db);
        var request = EditRequest(source); request.Title = "Моё название";
        const string history = "  Новая запись коллеги\nС сохранённым форматированием  ";
        await db.ClientCases.Where(row => row.Id == source.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.EventHistory, history));
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PutAsJsonAsync(CaseWriteRoute(source.Id, legacy), request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Equal(history, actual.EventHistory); Assert.Equal(source.Strategy, actual.Strategy); Assert.Equal(source.PositionReviewedAt, actual.PositionReviewedAt);
        Assert.Equal(source.NextHearingConfirmedAt, actual.NextHearingConfirmedAt); Assert.Equal(123.45m, actual.CourtClaimAmount);
        Assert.Equal(history, (await response.Content.ReadFromJsonAsync<ClientCase>(new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } }))!.EventHistory);
    }

    private static async Task<(User User, ClientCase Case)> SeedExactInputCaseAsync(PaymentContext db)
    {
        var (user, source) = await SeedConfirmationCaseAsync(db);
        source.Strategy = "  Исходная позиция  "; source.EventHistory = "  История\nсотрудника  "; source.DocumentsSummary = "  Материалы  "; source.CaseNumber = "  Д-123  "; source.CourtName = "  Районный суд  ";
        source.NextHearingAt = new DateTime(2099, 11, 3, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234560);
        source.StartDate = new DateTime(2026, 10, 1, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234560);
        source.PositionReviewedAt = new DateTime(2026, 11, 3, 1, 0, 0, DateTimeKind.Utc); source.PositionReviewedByUserId = user.Id; source.PositionReviewedByName = user.FullName;
        source.NextHearingConfirmedAt = source.PositionReviewedAt; source.NextHearingConfirmedForAt = source.NextHearingAt; source.NextHearingConfirmedByUserId = user.Id; source.NextHearingConfirmedByName = user.FullName;
        await db.SaveChangesAsync(); await db.Entry(source).ReloadAsync();
        return (user, source);
    }
}
