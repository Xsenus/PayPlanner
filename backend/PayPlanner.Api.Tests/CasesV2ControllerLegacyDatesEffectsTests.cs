using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseLegacyDates_InternalOldModelDoesNotStartWritingNewDateFields(bool withSource)
    {
        await using var db = await CreateContextAsync(); var (user, source, taskId) = await SeedLegacyDatesAsync(db);
        var request = new ClientCase
        {
            ClientId = source.ClientId,
            Title = "Старый внутренний вызов",
            Status = source.Status,
            EventHistory = source.EventHistory,
            Strategy = source.Strategy,
            NextHearingAt = source.NextHearingAt,
            AppealDeadlineAt = source.AppealDeadlineAt,
            CourtRegisteredAt = source.CourtRegisteredAt!.Value.AddDays(3),
            CourtDecisionAt = source.CourtDecisionAt!.Value.AddDays(3),
            CassationDeadlineAt = source.CassationDeadlineAt!.Value.AddDays(3)
        };
        var controller = new CasesController(db) { ControllerContext = CreateController(db, user).ControllerContext };
        Assert.IsType<OkObjectResult>(await controller.Update(source.Id, request, CancellationToken.None, withSource ? source : null));
        var actual = await db.ClientCases.AsNoTracking().SingleAsync(); foreach (var field in LegacyDateFields) Assert.Equal(LegacyDateGet(source, field), LegacyDateGet(actual, field));
        await AssertLegacyDateHistoryAsync(db, source, taskId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseLegacyDates_FailureOrCancellationRollsBackDatesTasksAndPublication(bool cancel)
    {
        var failure = new ConfirmationAfterSaveFailure(cancel); await using var db = await CreateContextAsync(failure); var (user, source, taskId) = await SeedLegacyDatesAsync(db);
        var request = EditRequest(source); request.CourtDecisionAt = source.CourtDecisionAt!.Value.AddDays(3); request.CassationDeadlineAt = source.CassationDeadlineAt!.Value.AddDays(3);
        var publisher = new RecordingRealtimeNotificationPublisher(); var controller = new CasesController(db, publisher, CreateNotificationService(db)) { ControllerContext = CreateController(db, user).ControllerContext };
        using var cancellation = new CancellationTokenSource(); failure.Armed = true; failure.Cancel = cancellation.Cancel;
        Task Write() => controller.UpdateWithSource(source.Id, request, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<ConfirmationSaveFailureException>(Write);
        Assert.True(failure.Called); Assert.Equal(0, publisher.TaskJournalChangedCount); Assert.Empty(await db.UserNotifications.AsNoTracking().ToArrayAsync());
        var actual = await db.ClientCases.AsNoTracking().SingleAsync(); foreach (var field in LegacyDateFields) Assert.Equal(LegacyDateGet(source, field), LegacyDateGet(actual, field));
        await AssertLegacyDateHistoryAsync(db, source, taskId);
    }

    [Fact]
    public async Task CaseLegacyDates_DecisionNotificationFollowsCommitAndDoesNotRepeatForSameDate()
    {
        var hook = new LegacyDecisionNotificationBoundary(); await using var db = await CreateContextAsync(hook); var (user, source, taskId) = await SeedLegacyDatesAsync(db);
        var responsible = await CreateUserAsync(db, "legacy-date-recipient@example.local", "Ответственный", DataVisibilityScope.All); source.ResponsibleUserId = responsible.Id; await db.SaveChangesAsync();
        var controller = new CasesController(db, notificationService: CreateNotificationService(db)) { ControllerContext = CreateController(db, user).ControllerContext };
        hook.Armed = true; var request = EditRequest(source); request.CourtDecisionAt = source.CourtDecisionAt!.Value.AddDays(3);
        Assert.IsType<OkObjectResult>(await controller.UpdateWithSource(source.Id, request, CancellationToken.None));
        var message = Assert.Single(await db.UserNotifications.AsNoTracking().ToArrayAsync()); Assert.Equal(responsible.Id, message.RecipientUserId); Assert.Contains("Новая дата", message.BodyPreview);
        Assert.IsType<OkObjectResult>(await controller.UpdateWithSource(source.Id, request, CancellationToken.None)); Assert.Single(await db.UserNotifications.AsNoTracking().ToArrayAsync());
        var clear = EditRequest(await db.ClientCases.AsNoTracking().SingleAsync()); clear.CourtDecisionAt = null;
        Assert.IsType<OkObjectResult>(await controller.UpdateWithSource(source.Id, clear, CancellationToken.None));
        Assert.Equal(2, await db.UserNotifications.CountAsync()); Assert.Single(await db.UserNotifications.Where(row => row.Title.StartsWith("Удалена дата судебного решения")).ToArrayAsync());
        Assert.Equal(2, hook.Notifications); await AssertLegacyDateHistoryAsync(db, source, taskId);
    }

    [Fact]
    public async Task CaseLegacyDates_DecisionAndCassationKeepSuppliedManualDeadlines()
    {
        await using var db = await CreateContextAsync(); var (user, source, taskId) = await SeedLegacyDatesAsync(db); var request = EditRequest(source);
        request.CourtDecisionAt = source.CourtDecisionAt!.Value.AddMonths(1); request.CassationDeadlineAt = new DateTime(2027, 7, 9, 0, 0, 0, DateTimeKind.Utc);
        var controller = new CasesController(db) { ControllerContext = CreateController(db, user).ControllerContext };
        Assert.IsType<OkObjectResult>(await controller.UpdateWithSource(source.Id, request, CancellationToken.None));
        var actual = await db.ClientCases.AsNoTracking().SingleAsync(); Assert.Equal(request.CourtDecisionAt, actual.CourtDecisionAt); Assert.Equal(request.CassationDeadlineAt, actual.CassationDeadlineAt); Assert.Equal(source.AppealDeadlineAt, actual.AppealDeadlineAt);
        await AssertLegacyDateHistoryAsync(db, source, taskId);
    }

    private sealed class LegacyDecisionNotificationBoundary : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public int Notifications { get; private set; }
        private Guid? caseTransaction;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!;
            if (Armed && db.Database.CurrentTransaction is { } transaction && db.ChangeTracker.Entries<ClientCase>().Any(row => row.State == EntityState.Modified)) caseTransaction = transaction.TransactionId;
            if (Armed && db.ChangeTracker.Entries<UserNotification>().Any(row => row.State == EntityState.Added))
            {
                Assert.NotNull(caseTransaction); Assert.NotEqual(caseTransaction, db.Database.CurrentTransaction?.TransactionId); Notifications++;
            }
            return ValueTask.FromResult(result);
        }
    }
}
