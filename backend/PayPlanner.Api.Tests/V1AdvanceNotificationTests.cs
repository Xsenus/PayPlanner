using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers.V1;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class V1AdvanceNotificationTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    public static IEnumerable<object[]> Writes()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var legal in new[] { false, true })
                foreach (var update in new[] { false, true }) yield return [postgres, legal, update];
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public Task NotifyObservesCommittedReceiptAndDoesNotDuplicate(bool postgres, bool legal, bool update)
        => RunAsync(postgres, async db =>
        {
            var fixture = await SeedRecipientAsync(db, legal); var model = await ModelAsync(db, fixture, update);
            var notifications = Probe.Create(); var probe = (Probe)(object)notifications; var actual = RealNotifications(db);
            probe.Send = async envelope =>
            {
                Assert.Null(db.Database.CurrentTransaction);
                var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == envelope.ObjectId);
                Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, stored.AdvanceReviewStatus);
                Assert.Equal(fixture.ActorId, envelope.RecipientUserId);
                Assert.Equal(PaymentAdvanceReviewService.BuildAdvanceReviewRequiredEventKey(stored.Id), envelope.EventKey);
                return await actual.NotifyAsync(envelope);
            };
            var result = await WriteAsync(db, fixture, update, model, notifications);
            var saved = Saved(result, update);
            var notification = Assert.Single(await db.UserNotifications.AsNoTracking().ToArrayAsync());
            Assert.Equal(saved.Id, notification.ObjectId); Assert.Single(probe.Events);
            using var metadata = JsonDocument.Parse(notification.MetadataJson);
            Assert.Equal(100m, metadata.RootElement.GetProperty("advanceAmount").GetDecimal());
            var again = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == saved.Id); again.Notes = "Allowed note";
            var changedFixture = fixture with { PaymentId = saved.Id };
            Assert.IsType<OkObjectResult>(await WriteAsync(db, changedFixture, true, again, notifications));
            Assert.Single(await db.UserNotifications.ToArrayAsync()); Assert.Single(probe.Events);
        });

    public static IEnumerable<object[]> Suppressions()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var update in new[] { false, true })
                foreach (var mode in postgres ? new[] { "ban", "rights" } : new[] { "ban", "rights", "inactive", "no-recipient", "accepted", "unpaid", "expense", "account" })
                    yield return [postgres, update, mode];
    }

    [Theory]
    [MemberData(nameof(Suppressions))]
    public Task CommonRulesSuppressIneligibleOrForbiddenNotification(bool postgres, bool update, string mode)
        => RunAsync(postgres, async db =>
        {
            var fixture = await SeedRecipientAsync(db, false); var model = await ModelAsync(db, fixture, update);
            var recipient = await db.Users.SingleAsync(x => x.Id == fixture.ActorId);
            if (mode is "rights" or "inactive")
            {
                var role = new Role { Name = "separate-recipient" }; db.Roles.Add(role); await db.SaveChangesAsync();
                var other = new User { RoleId = role.Id, Email = "recipient@test.local", FullName = "Recipient", PasswordHash = "unused", IsActive = mode != "inactive", IsApproved = true, IsEmployee = true };
                db.Users.Add(other); await db.SaveChangesAsync();
                db.RolePermissions.Add(new() { RoleId = role.Id, Section = "payments", CanView = mode != "rights", ViewScope = "all" });
                (await db.Clients.SingleAsync()).ResponsibleUserId = other.Id;
            }
            if (mode == "ban") db.UserSettings.Add(new() { UserId = recipient.Id, Key = UserNotificationPreferenceSettings.SettingKey, Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([UserNotificationTypes.PaymentAdvanceReviewRequired], new Dictionary<string, IReadOnlyList<string>>())) });
            if (mode == "no-recipient") (await db.Clients.SingleAsync()).ResponsibleUserId = null;
            if (mode == "accepted") model.AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance;
            if (mode == "unpaid") { model.PaidAmount = 0; model.IsPaid = false; model.PaidDate = null; model.LastPaymentDate = null; model.Status = PaymentStatus.Pending; }
            if (mode == "expense") model.Type = PaymentType.Expense;
            if (mode == "account") model.Account = "Existing document";
            await db.SaveChangesAsync();
            Saved(await WriteAsync(db, fixture, update, model, RealNotifications(db)), update);
            Assert.Empty(await db.UserNotifications.ToArrayAsync()); Assert.Null(db.Database.CurrentTransaction);
        });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task DeniedWriterDoesNotSaveOrNotify(bool postgres, bool update)
        => RunAsync(postgres, async db =>
        {
            var fixture = await SeedRecipientAsync(db, false); var model = await ModelAsync(db, fixture, update);
            var permission = await db.RolePermissions.SingleAsync(x => x.Section == "payments");
            if (update) permission.CanEdit = false; else permission.CanCreate = false;
            await db.SaveChangesAsync(); var before = await PaymentCreateGraphTests.SnapshotAsync(db);
            Assert.IsType<ForbidResult>(await WriteAsync(db, fixture, update, model, RealNotifications(db)));
            Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db)); Assert.Empty(await db.UserNotifications.ToArrayAsync());
        });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task FailedSaveSendsNothing(bool postgres, bool update)
    {
        var save = new FailSave();
        return RunAsync(postgres, async db =>
        {
            var fixture = await SeedRecipientAsync(db, false); var model = await ModelAsync(db, fixture, update);
            var before = await PaymentCreateGraphTests.SnapshotAsync(db); var notifications = Probe.Create(); var probe = (Probe)(object)notifications;
            save.Enabled = true;
            await Assert.ThrowsAsync<IOException>(() => WriteAsync(db, fixture, update, model, notifications));
            Assert.Empty(probe.Events); Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction);
        }, save);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task FailedCommitDoesNotPublishNotification(bool postgres, bool update)
    {
        var commit = new FailCommit();
        return RunAsync(postgres, async db =>
        {
            var fixture = await SeedRecipientAsync(db, false); var model = await ModelAsync(db, fixture, update);
            var before = await PaymentCreateGraphTests.SnapshotAsync(db); var notifications = Probe.Create(); var probe = (Probe)(object)notifications;
            commit.Enabled = true;
            await Assert.ThrowsAsync<IOException>(() => WriteAsync(db, fixture, update, model, notifications));
            Assert.Empty(probe.Events); Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction);
        }, commit);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task NotificationFailureDoesNotRollBackOrRepeatFinancialWrite(bool postgres, bool update)
        => RunAsync(postgres, async db =>
        {
            var fixture = await SeedRecipientAsync(db, false); var model = await ModelAsync(db, fixture, update);
            model.Date = DateTime.UtcNow.Date.AddDays(-2); model.PaidDate = model.Date; model.LastPaymentDate = model.Date;
            var notifications = Probe.Create(); var probe = (Probe)(object)notifications;
            var logger = new RecordingLogger();
            probe.Send = _ => { Assert.Null(db.Database.CurrentTransaction); throw new IOException("Synthetic notification failure after commit"); };
            var saved = Saved(await WriteAsync(db, fixture, update, model, notifications, logger), update);
            Assert.Single(probe.Events); Assert.Equal(update ? 1 : 2, await db.Payments.CountAsync());
            var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == probe.Events[0].ObjectId);
            Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, stored.AdvanceReviewStatus); Assert.Equal(100m, PaymentAdvanceReviewService.GetAdvanceAmount(stored));
            Assert.Contains(saved.Id.ToString(), Assert.Single(logger.Errors));
            Assert.Null(db.Database.CurrentTransaction);
            var money = await AdvanceReviewMonitoringBoundaryTests.MoneyAsync(db);
            var recovered = await AdvanceReviewMonitoringBoundaryTests.Runner(db, RealNotifications(db)).RunOnceAsync();
            Assert.Equal(1, recovered.NotifiedCount); Assert.Single(await db.UserNotifications.ToArrayAsync());
            Assert.Equal(money, await AdvanceReviewMonitoringBoundaryTests.MoneyAsync(db));
        });

    [Fact]
    public async Task HttpDependencyInjectionPublishesOneNotificationForCreateAndUpdate()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedRecipientAsync(db, false);
        using var server = PaymentCreateGraphTests.Server(db, fixture, RealNotifications(db)); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", fixture.ActorId.ToString());
        var model = await ModelAsync(db, fixture, false);
        client.DefaultRequestHeaders.Add("Idempotency-Key", (await LegacyPaymentOperationKeyTests.RegisterAsync(db, fixture, model)).ToString("D"));
        using var response = await client.PostAsJsonAsync("/api/v1/payments", model); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var saved = (await response.Content.ReadFromJsonAsync<Payment>(options))!;
        Assert.Single(await db.UserNotifications.ToArrayAsync()); saved.Notes = "Changed through HTTP";
        using var changed = await client.PutAsJsonAsync($"/api/v1/payments/{saved.Id}", saved);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode); Assert.Single(await db.UserNotifications.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostCommitCancellationPreservesReceiptAndOnlyPropagatesRealRequestCancellation(bool requestCancelled)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedRecipientAsync(db, false);
        var model = await ModelAsync(db, fixture, false); using var cancellation = new CancellationTokenSource();
        var notifications = Probe.Create(); var probe = (Probe)(object)notifications; var logger = new RecordingLogger();
        probe.Send = _ => { if (requestCancelled) cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); };
        if (requestCancelled) await Assert.ThrowsAsync<OperationCanceledException>(() => WriteAsync(db, fixture, false, model, notifications, logger, cancellation.Token));
        else Saved(await WriteAsync(db, fixture, false, model, notifications, logger, cancellation.Token), false);
        Assert.Equal(requestCancelled ? 0 : 1, logger.Errors.Count); Assert.Single(probe.Events);
        Assert.Equal(2, await db.Payments.CountAsync()); Assert.Null(db.Database.CurrentTransaction);
        Assert.Equal(100m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == probe.Events[0].ObjectId)).PaidAmount);
    }

    private static Payment Saved(IActionResult result, bool update) => Assert.IsType<Payment>(update ? Assert.IsType<OkObjectResult>(result).Value : Assert.IsType<CreatedResult>(result).Value);
    private static async Task<Fixture> SeedRecipientAsync(PaymentContext db, bool legal)
    {
        var fixture = await SeedAsync(db, legal);
        if (legal) (await db.LegalEntities.SingleAsync()).ResponsibleUserId = fixture.ActorId;
        else (await db.Clients.SingleAsync()).ResponsibleUserId = fixture.ActorId;
        await db.SaveChangesAsync(); return fixture;
    }
    private static async Task<Payment> ModelAsync(PaymentContext db, Fixture fixture, bool update)
    {
        var model = update ? await VersionedPaymentWriterTests.ModelAsync(db, fixture) : PaymentFor(fixture);
        model.PaidAmount = 100m; model.IsPaid = true; model.PaidDate = model.Date; model.LastPaymentDate = model.Date; model.Status = PaymentStatus.Completed;
        return model;
    }
    private static async Task<IActionResult> WriteAsync(PaymentContext db, Fixture fixture, bool update, Payment model, INotificationService notifications, ILogger<PaymentsV1Controller>? logger = null, CancellationToken ct = default)
    {
        using var services = new ServiceCollection().AddSingleton(db).AddSingleton(notifications).BuildServiceProvider();
        var controller = logger is null ? ActivatorUtilities.CreateInstance<PaymentsV1Controller>(services)
            : ActivatorUtilities.CreateInstance<PaymentsV1Controller>(services, logger);
        controller.ControllerContext = BalanceApplicationPaymentWriterTests.Controller(db, fixture).ControllerContext;
        return update ? await controller.Update(fixture.PaymentId, model, ct) : await PaymentOperationKeyTestSetup.CreateAsync(controller, db, model, ct);
    }
    private static NotificationService RealNotifications(PaymentContext db)
        => new(db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Email:Enabled"] = "false", ["BackgroundServices:Enabled"] = "false" }).Build(),
            DispatchProxy.Create<IEmailService, IsolatedTransport>(), DispatchProxy.Create<ITelegramNotificationService, IsolatedTransport>(), NullLogger<NotificationService>.Instance);

    private async Task RunAsync(bool postgres, Func<PaymentContext, Task> test, IInterceptor? interceptor = null)
    {
        if (postgres)
        {
            await using var database = await template.CreateDatabaseAsync(); var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            await using var db = new PostgreSqlPaymentContext(options.Options); await test(db);
        }
        else { await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor); await test(db); }
    }
    public class Probe : DispatchProxy
    {
        public List<NotificationEventEnvelope> Events { get; } = [];
        public Func<NotificationEventEnvelope, Task<bool>>? Send { get; set; }
        public static INotificationService Create() => DispatchProxy.Create<INotificationService, Probe>();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != nameof(INotificationService.NotifyAsync) || args?[0] is not NotificationEventEnvelope envelope) throw new InvalidOperationException("Unexpected notification method");
            Events.Add(envelope); return Send?.Invoke(envelope) ?? Task.FromResult(true);
        }
    }
    public class IsolatedTransport : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name == nameof(ITelegramNotificationService.GetSettingsAsync) ? Task.FromResult(new TelegramNotificationSettingsDto())
                : throw new InvalidOperationException("External notification transport must never be called in this test");
    }
    private sealed class RecordingLogger : ILogger<PaymentsV1Controller>
    {
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Error) Errors.Add(formatter(state, error));
        }
    }
    private sealed class FailSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled) throw new IOException("Synthetic financial save failure"); return ValueTask.FromResult(result);
        }
    }
    private sealed class FailCommit : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData data, InterceptionResult result, CancellationToken ct = default)
        {
            if (Enabled) throw new IOException("Synthetic financial commit failure"); return ValueTask.FromResult(result);
        }
    }
}
