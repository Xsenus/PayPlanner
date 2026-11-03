using System.ComponentModel.DataAnnotations.Schema;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static readonly DateTime CreationSpoofTime = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    private const string CreationSpoofName = "Подложный автор отметки";
    private static string CreationRoute(int route) => route switch { 0 => "/api/cases", 1 => "/api/v1/cases", _ => "/api/v2/cases" };

    private static ClientCase CreateMetadataInput(int owner, int actor, int spoofActor) => new()
    {
        ClientId = owner,
        ResponsibleUserId = actor,
        Title = "Дело без подложных отметок",
        Description = "Описание",
        Strategy = "Сохранённая позиция",
        EventHistory = "Введённая история",
        CaseNumber = "CRM-47",
        CourtCaseNumber = "Суд-47",
        StartDate = CreationSpoofTime,
        CourtName = "Суд",
        CourtClaimAmount = 123.45m,
        NextHearingAt = new DateTime(2099, 11, 3, 12, 0, 0, DateTimeKind.Utc),
        CourtSyncMonitoringEnabled = false,
        InactivityRemindersEnabled = false,
        InactivityRemindersDisabledReason = "Согласованное исключение",
        CreatedAt = CreationSpoofTime,
        CourtSyncLastAttemptAt = CreationSpoofTime,
        CourtSyncLastSuccessAt = CreationSpoofTime,
        CourtSyncFailureStartedAt = CreationSpoofTime,
        CourtSyncLastError = "Подложная ошибка синхронизации",
        NextHearingConfirmedForAt = new DateTime(2099, 11, 3, 12, 0, 0, DateTimeKind.Utc),
        NextHearingConfirmedAt = CreationSpoofTime,
        NextHearingConfirmedByUserId = spoofActor,
        NextHearingConfirmedByName = CreationSpoofName,
        PositionReviewedAt = CreationSpoofTime,
        PositionReviewedByUserId = spoofActor,
        PositionReviewedByName = CreationSpoofName,
        LastViewedAt = CreationSpoofTime,
        LastViewedByUserId = spoofActor,
        LastViewedByUserName = CreationSpoofName,
        ActivityConfirmedAt = CreationSpoofTime,
        ActivityConfirmedByUserId = spoofActor,
        ActivityConfirmedByUserName = CreationSpoofName,
        ControlMarkedAt = CreationSpoofTime,
        ControlMarkedByUserId = spoofActor,
        ControlMarkedByUserName = CreationSpoofName,
        HiddenFromExecutorAt = CreationSpoofTime,
        HiddenFromExecutorByUserId = spoofActor,
        HiddenFromExecutorByUserName = CreationSpoofName,
        AccessRequestedAt = CreationSpoofTime,
        AccessRequestedByUserId = spoofActor,
        AccessRequestedByUserName = CreationSpoofName,
        AccessRequestReason = "Подложный запрос доступа",
    };

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task CaseCreateMetadata_HttpDiscardsForgedStoredStampsButKeepsBusinessFields(int route, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, ids) = await SeedBulkCasesAsync(db); var other = await CreateUserAsync(db, "create-metadata-other@example.local", "Другой сотрудник"); var input = CreateMetadataInput(await db.Clients.Select(row => row.Id).SingleAsync(), actor.Id, other.Id);
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); var before = DateTime.UtcNow;
        using var response = await client.PostAsJsonAsync(CreationRoute(route), input); Assert.Equal(HttpStatusCode.Created, response.StatusCode); var returned = await response.Content.ReadFromJsonAsync<ClientCase>(new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } }); Assert.NotNull(returned);
        var saved = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == returned.Id); var after = DateTime.UtcNow;
        var forgedCreated = saved.CreatedAt < before || saved.CreatedAt > after; var forgedHearing = saved.NextHearingConfirmedAt.HasValue || saved.NextHearingConfirmedForAt.HasValue || saved.NextHearingConfirmedByUserId.HasValue || saved.NextHearingConfirmedByName != "";
        var forgedPosition = saved.PositionReviewedAt.HasValue || saved.PositionReviewedByUserId.HasValue || saved.PositionReviewedByName != ""; var forgedSync = saved.CourtSyncLastAttemptAt.HasValue || saved.CourtSyncLastSuccessAt.HasValue || saved.CourtSyncFailureStartedAt.HasValue || saved.CourtSyncLastError != "";
        var forgedActivity = saved.ActivityConfirmedAt.HasValue || saved.ActivityConfirmedByUserId.HasValue || saved.ActivityConfirmedByUserName != ""; var forgedView = saved.LastViewedAt.HasValue || saved.LastViewedByUserId.HasValue || saved.LastViewedByUserName != "";
        Assert.True(!forgedCreated && !forgedHearing && !forgedPosition && !forgedSync && !forgedActivity && !forgedView, $"created={forgedCreated}; hearing={forgedHearing}; position={forgedPosition}; sync={forgedSync}; activity={forgedActivity}; view={forgedView}");
        Assert.Equal(saved.CreatedAt, returned.CreatedAt); Assert.Null(returned.PositionReviewedAt); Assert.Null(returned.NextHearingConfirmedAt); Assert.Null(returned.ActivityConfirmedAt); Assert.Null(returned.LastViewedAt); Assert.Null(returned.CourtSyncLastSuccessAt);
        Assert.Equal(input.Title, saved.Title); Assert.Equal(input.Description, saved.Description); Assert.Equal(input.Strategy, saved.Strategy); Assert.Equal(input.EventHistory, saved.EventHistory); Assert.Equal(input.CaseNumber, saved.CaseNumber); Assert.Equal(input.CourtCaseNumber, saved.CourtCaseNumber); Assert.Equal(input.StartDate, saved.StartDate); Assert.Equal(input.NextHearingAt, saved.NextHearingAt); Assert.Equal(123.45m, saved.CourtClaimAmount); Assert.False(saved.CourtSyncMonitoringEnabled); Assert.False(saved.InactivityRemindersEnabled); Assert.Equal(input.InactivityRemindersDisabledReason, saved.InactivityRemindersDisabledReason);
        Assert.False(saved.IsUnderControl); Assert.Null(saved.ControlMarkedAt); Assert.Null(saved.ControlMarkedByUserId); Assert.Equal("", saved.ControlMarkedByUserName); Assert.False(saved.IsHiddenFromExecutor); Assert.Null(saved.HiddenFromExecutorAt); Assert.Null(saved.HiddenFromExecutorByUserId); Assert.Equal("", saved.HiddenFromExecutorByUserName); Assert.Null(saved.AccessRequestedAt); Assert.Null(saved.AccessRequestedByUserId); Assert.Equal("", saved.AccessRequestedByUserName); Assert.Equal("", saved.AccessRequestReason);
        await AssertCreateMetadataHistoryAsync(db, ids, route == 1 ? 3 : 4);
    }

    private static PropertyInfo[] CreationReadOnlyScalars() => typeof(ClientCase).GetProperties().Where(property => property.GetCustomAttribute<NotMappedAttribute>() is not null && property.CanRead && property.CanWrite && (property.PropertyType.IsValueType || property.PropertyType == typeof(string)) && !property.Name.StartsWith("Clear", StringComparison.Ordinal)).ToArray();

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task CaseCreateMetadata_HttpCannotForgeComputedResponseFields(int route, bool finance)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); if (finance) await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Payments);
        var input = new ClientCase { ClientId = await db.Clients.Select(row => row.Id).SingleAsync(), Title = "Вычисляемые поля назначает сервер", FeeAmount = 817.77m }; var defaults = new ClientCase(); var fields = CreationReadOnlyScalars(); Assert.True(fields.Length >= 25);
        foreach (var property in fields)
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            property.SetValue(input, type == typeof(string) ? CreationSpoofName : type == typeof(bool) ? !Equals(property.GetValue(defaults), true) : type == typeof(DateTime) ? CreationSpoofTime : type == typeof(decimal) ? 4321.12m : type == typeof(int) ? (object)42 : throw new InvalidOperationException(property.Name));
        }
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.PostAsJsonAsync(CreationRoute(route), input); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var returned = await response.Content.ReadFromJsonAsync<ClientCase>(new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } }); Assert.NotNull(returned);
        var forged = fields.Where(property => !Equals(property.GetValue(defaults), property.GetValue(returned))).Select(property => property.Name).ToArray(); Assert.True(forged.Length == 0, "forgedResponseFields=" + string.Join(',', forged));
        Assert.Equal(finance ? 817.77m : 0m, returned.FeeAmount); Assert.Equal(returned.FeeAmount, (await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == returned.Id)).FeeAmount); await AssertCreateMetadataHistoryAsync(db, ids, 3);
    }

    public static IEnumerable<object[]> CreationFlagCases()
    {
        foreach (var route in new[] { 0, 1, 2 }) foreach (var control in new[] { false, true }) foreach (var hidden in new[] { false, true }) yield return new object[] { route, control, hidden, false };
        foreach (var route in new[] { 0, 1, 2 }) yield return new object[] { route, true, true, true };
    }
    [Theory]
    [MemberData(nameof(CreationFlagCases))]
    public async Task CaseCreateMetadata_InitialFlagsUseTheRealActorAndNeverCreateAnAccessRequest(int route, bool control, bool hidden, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, ids) = await SeedBulkCasesAsync(db); var other = await CreateUserAsync(db, "create-flags-other@example.local", "Другой"); var input = CreateMetadataInput(await db.Clients.Select(row => row.Id).SingleAsync(), actor.Id, other.Id); input.NextHearingAt = null; input.IsUnderControl = control; input.IsHiddenFromExecutor = hidden; var before = DateTime.UtcNow;
        var result = Assert.IsType<CreatedResult>(await CreateAtomicCaseAsync(db, actor, input, route, new BulkPublicationBoundary(db))); var returned = Assert.IsType<ClientCase>(result.Value); var saved = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == returned.Id); var after = DateTime.UtcNow;
        var validControl = control ? saved.ControlMarkedByUserId == actor.Id && saved.ControlMarkedByUserName == actor.FullName && saved.ControlMarkedAt >= before && saved.ControlMarkedAt <= after : saved.ControlMarkedAt is null && saved.ControlMarkedByUserId is null && saved.ControlMarkedByUserName == "";
        var validHidden = hidden ? saved.HiddenFromExecutorByUserId == actor.Id && saved.HiddenFromExecutorByUserName == actor.FullName && saved.HiddenFromExecutorAt >= before && saved.HiddenFromExecutorAt <= after : saved.HiddenFromExecutorAt is null && saved.HiddenFromExecutorByUserId is null && saved.HiddenFromExecutorByUserName == "";
        var forgedRequest = saved.AccessRequestedAt.HasValue || saved.AccessRequestedByUserId.HasValue || saved.AccessRequestedByUserName != "" || saved.AccessRequestReason != "";
        Assert.True(validControl && validHidden && !forgedRequest, $"validControl={validControl}; validHidden={validHidden}; forgedAccessRequest={forgedRequest}"); Assert.Equal(control, saved.IsUnderControl); Assert.Equal(hidden, saved.IsHiddenFromExecutor); Assert.Equal(saved.ControlMarkedAt, returned.ControlMarkedAt); Assert.Equal(saved.HiddenFromExecutorAt, returned.HiddenFromExecutorAt); await AssertCreateMetadataHistoryAsync(db, ids, 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CaseCreateMetadata_InitialActorCaptionUsesTheExistingLengthRule(int route)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); var longName = new string('А', 250) + "      Продолжение"; await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.FullName, longName)); actor = await db.Users.AsNoTracking().SingleAsync(row => row.Id == actor.Id);
        var input = CreateMetadataInput(await db.Clients.Select(row => row.Id).SingleAsync(), actor.Id, actor.Id); input.NextHearingAt = null; input.IsUnderControl = true; input.IsHiddenFromExecutor = true;
        var result = Assert.IsType<CreatedResult>(await CreateAtomicCaseAsync(db, actor, input, route, new BulkPublicationBoundary(db))); var returned = Assert.IsType<ClientCase>(result.Value); var expected = longName[..256].TrimEnd(); Assert.Equal(expected, returned.ControlMarkedByUserName); Assert.Equal(expected, returned.HiddenFromExecutorByUserName); Assert.Equal(actor.Id, returned.ControlMarkedByUserId); Assert.Equal(actor.Id, returned.HiddenFromExecutorByUserId); await AssertCreateMetadataHistoryAsync(db, ids, 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseCreateMetadata_ConfirmationStillRequiresAndAcceptsItsSeparateAction(bool hearing)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); var input = CreateMetadataInput(await db.Clients.Select(row => row.Id).SingleAsync(), actor.Id, actor.Id);
        var created = Assert.IsType<CreatedResult>(await CreateAtomicCaseAsync(db, actor, input, 2, new BulkPublicationBoundary(db))); var id = Assert.IsType<ClientCase>(created.Value).Id; var saved = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == id); Assert.Null(saved.PositionReviewedAt); Assert.Null(saved.NextHearingConfirmedAt);
        var controller = CreateController(db, actor); if (hearing) Assert.IsType<OkObjectResult>((await controller.ConfirmNextHearing(id, new(saved.NextHearingAt), CancellationToken.None)).Result); else Assert.IsType<OkObjectResult>((await controller.ReviewCasePosition(id, new(saved.Strategy), CancellationToken.None)).Result);
        saved = await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == id); Assert.Equal(actor.Id, hearing ? saved.NextHearingConfirmedByUserId : saved.PositionReviewedByUserId); Assert.Equal(actor.FullName, hearing ? saved.NextHearingConfirmedByName : saved.PositionReviewedByName); Assert.NotNull(hearing ? saved.NextHearingConfirmedAt : saved.PositionReviewedAt); Assert.Null(hearing ? saved.PositionReviewedAt : saved.NextHearingConfirmedAt); await AssertCreateMetadataHistoryAsync(db, ids, 4);
    }

    private static async Task AssertCreateMetadataHistoryAsync(PaymentContext db, int[] ids, int taskCount)
    {
        Assert.Equal(ids.Length + 1, await db.ClientCases.CountAsync()); var previous = await db.ClientCases.AsNoTracking().Where(row => ids.Contains(row.Id)).ToArrayAsync(); Assert.All(previous, row => { Assert.Equal("Исходная история", row.EventHistory); Assert.Equal(123.45m, row.CourtClaimAmount); Assert.Equal(ClientCaseStatus.Open, row.Status); }); Assert.Equal(taskCount, await db.TaskJournalItems.CountAsync()); Assert.Equal("Сохранённый результат", (await db.TaskJournalItems.SingleAsync(row => row.Status == "done")).Result); Assert.Empty(await db.Payments.ToArrayAsync());
    }
}
