using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class CourtNotificationChannelIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseDateNotice_PreDateSnapshotsRemainReadableWithoutMutation(bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedAsync(db, false, false);
        var intent = await db.CaseCourtNotificationOutboxes.SingleAsync();
        var old = JsonNode.Parse(intent.PayloadJson)!.AsObject();
        Assert.True(old.Remove("dateChange"));
        intent.PayloadJson = old.ToJsonString();
        var exactSnapshot = intent.PayloadJson;
        var decoded = System.Text.Json.JsonSerializer.Deserialize<CaseCourtNotificationPayload>(intent.PayloadJson,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.NotNull(decoded);
        Assert.Null(decoded.DateChange);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var domain = await DomainAsync(db);
        var publisher = new CourtNoticeIntegrationRealtime(db);
        var service = NotificationServiceTests.CreateService(db, new NotificationServiceTests.RecordingEmailService(),
            new NotificationServiceTests.RecordingTelegramService(), publisher);
        Assert.True(await new CaseCourtNotificationDispatchProcessor(db, service, publisher).ProcessAsync(seed.IntentId, default));
        var complete = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync();
        Assert.NotNull(complete.CompletedAt);
        Assert.Equal(exactSnapshot, complete.PayloadJson);
        Assert.Single(publisher.Delivered);
        Assert.Single(await db.UserNotifications.AsNoTracking().ToArrayAsync());
        Assert.Equal(domain, await DomainAsync(db));
    }
}
