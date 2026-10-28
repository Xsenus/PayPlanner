using System.Security.Claims;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class InternalChatControllerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TaskTimer_DirectAccessRevocationStopsTimeAndRollsBackWithUserChange(bool postgres, bool revokeApproval)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var actor = await fixture.AddUserAsync("timer-revoke-direct@example.test", "Сотрудник");
        var task = await AddTimerTaskAsync(fixture, actor);
        var clock = new TimerClock(new DateTimeOffset(2099, 10, 27, 10, 0, 0, TimeSpan.Zero));
        var id = Guid.NewGuid();
        TimerState(await TimerController(fixture.Db, actor, clock).Start(new(id, task.Id), default));
        await using (var transaction = await fixture.Db.Database.BeginTransactionAsync())
        {
            if (revokeApproval) actor.IsApproved = false; else actor.IsActive = false;
            await fixture.Db.SaveChangesAsync();
            Assert.NotNull((await fixture.Db.TaskTimeEntries.AsNoTracking().SingleAsync(entry => entry.Id == id)).StoppedAtUtc);
            await transaction.RollbackAsync();
        }
        fixture.Db.ChangeTracker.Clear();
        Assert.Null((await fixture.Db.TaskTimeEntries.AsNoTracking().SingleAsync(entry => entry.Id == id)).StoppedAtUtc);
        var restored = await fixture.Db.Users.SingleAsync(user => user.Id == actor.Id);
        if (revokeApproval) restored.IsApproved = false; else restored.IsActive = false;
        if (revokeApproval) fixture.Db.SaveChanges(); else await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        Assert.NotNull((await fixture.Db.TaskTimeEntries.SingleAsync(entry => entry.Id == id)).StoppedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskTimer_RejectedAccountPreservesAndClosesTimeWithoutSendingEmail(bool postgres)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var actor = await fixture.AddUserAsync("timer-rejected@example.test", "Сотрудник");
        var task = await AddTimerTaskAsync(fixture, actor);
        var clock = new TimerClock(new DateTimeOffset(2099, 10, 27, 10, 0, 0, TimeSpan.Zero));
        var id = Guid.NewGuid();
        TimerState(await TimerController(fixture.Db, actor, clock).Start(new(id, task.Id), default));
        var notifications = DispatchProxy.Create<INotificationService, NotificationProxy>();
        var service = new AuthService(fixture.Db, new ConfigurationBuilder().Build(), null!, null!, new PasswordHasher<User>(), notifications);
        Assert.True(await service.RejectUserAsync(actor.Id, actor.Id));
        fixture.Db.ChangeTracker.Clear();
        var saved = await fixture.Db.TaskTimeEntries.SingleAsync(entry => entry.Id == id);
        Assert.Null(saved.UserId);
        Assert.NotNull(saved.StoppedAtUtc);
        Assert.Equal("Сотрудник", saved.UserNameSnapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskTimer_UserDeletionPreservesTimerStartedDuringCalendarCleanup(bool postgres)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var actor = await fixture.AddUserAsync("timer-delete-cleanup@example.test", "Сотрудник");
        var task = await AddTimerTaskAsync(fixture, actor);
        await using var timerDb = CaptureFreshDb(fixture, independentSqlite: true);
        var clock = new TimerClock(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));
        var id = Guid.NewGuid();
        var cleanup = new TimerCalendarCleanup(async () => { TimerState(await TimerController(timerDb, actor, clock).Start(new(id, task.Id), default)); });
        var service = new AuthService(fixture.Db, new ConfigurationBuilder().Build(), null!, null!, new PasswordHasher<User>(), externalCalendarSyncCoordinator: cleanup);
        Assert.True(await service.DeleteUserAsync(actor.Id, actor.Id));
        Assert.Equal(1, cleanup.Calls);
        fixture.Db.ChangeTracker.Clear();
        var saved = await fixture.Db.TaskTimeEntries.SingleAsync(entry => entry.Id == id);
        Assert.Null(saved.UserId);
        Assert.NotNull(saved.StoppedAtUtc);
        Assert.Equal("Сотрудник", saved.UserNameSnapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskTimer_ConcurrentStartAndEmployeeDeletionNeverLeavesActiveOrReassignedTime(bool postgres)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var actor = await fixture.AddUserAsync("timer-delete-race@example.test", "Сотрудник");
        var task = await AddTimerTaskAsync(fixture, actor);
        await using var left = CaptureFreshDb(fixture, independentSqlite: true);
        await using var right = CaptureFreshDb(fixture, independentSqlite: true);
        var clock = new TimerClock(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));
        var start = Task.Run(() => TimerController(left, actor, clock).Start(new(Guid.NewGuid(), task.Id), default));
        var delete = Task.Run(async () => { var user = await right.Users.SingleAsync(item => item.Id == actor.Id); right.Users.Remove(user); await right.SaveChangesAsync(); });
        await Task.WhenAll(start, delete);
        Assert.True(await start is OkObjectResult or ConflictObjectResult or ForbidResult or NotFoundResult);
        fixture.Db.ChangeTracker.Clear();
        Assert.False(await fixture.Db.Users.AnyAsync(item => item.Id == actor.Id));
        Assert.False(await fixture.Db.TaskTimeEntries.AnyAsync(item => item.UserId == actor.Id || item.StoppedAtUtc == null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskTimer_DeletingEmployeeStopsAndAnonymizesWithoutLeakingToReusedId(bool postgres)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var actor = await fixture.AddUserAsync("timer-delete-user@example.test", "Первый сотрудник");
        var task = await AddTimerTaskAsync(fixture, actor);
        var clock = new TimerClock(new DateTimeOffset(2099, 10, 27, 10, 0, 0, TimeSpan.Zero));
        var id = Guid.NewGuid();
        TimerState(await TimerController(fixture.Db, actor, clock).Start(new(id, task.Id), default));
        var oldId = actor.Id;
        fixture.Db.Users.Remove(actor);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var saved = await fixture.Db.TaskTimeEntries.SingleAsync(item => item.Id == id);
        Assert.Null(saved.UserId);
        Assert.NotNull(saved.StoppedAtUtc);
        Assert.Equal("Первый сотрудник", saved.UserNameSnapshot);
        var replacement = new User
        {
            Id = oldId,
            Email = "timer-reused@example.test",
            FullName = "Другой сотрудник",
            PasswordHash = "test",
            RoleId = actor.RoleId,
            IsApproved = true,
            IsActive = true
        };
        fixture.Db.Users.Add(replacement);
        await fixture.Db.SaveChangesAsync();
        var currentTask = await fixture.Db.TaskJournalItems.SingleAsync(item => item.Id == task.Id);
        currentTask.AssigneeUserId = replacement.Id;
        await fixture.Db.SaveChangesAsync();
        var controller = TimerController(fixture.Db, replacement, clock);
        var page = Assert.IsType<TaskTimerHistoryDto>(Assert.IsType<OkObjectResult>(await controller.History(task.Id, default)).Value);
        Assert.Empty(page.Items);
        Assert.Null(Assert.IsType<OkObjectResult>(await controller.Current(default)).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskTimer_MergeStopsSourceAndTransfersHistoryWithoutStoppingTarget(bool postgres)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var source = await fixture.AddUserAsync("timer-merge-source@example.test", "Исходный сотрудник");
        var target = await fixture.AddUserAsync("timer-merge-target@example.test", "Основной сотрудник");
        var sourceTask = await AddTimerTaskAsync(fixture, source);
        var targetTask = await AddTimerTaskAsync(fixture, target, addPermission: false);
        var clock = new TimerClock(new DateTimeOffset(2099, 10, 27, 10, 0, 0, TimeSpan.Zero));
        var sourceEntry = Guid.NewGuid(); var targetEntry = Guid.NewGuid();
        TimerState(await TimerController(fixture.Db, source, clock).Start(new(sourceEntry, sourceTask.Id), default));
        TimerState(await TimerController(fixture.Db, target, clock).Start(new(targetEntry, targetTask.Id), default));
        var service = new AuthService(fixture.Db, new ConfigurationBuilder().Build(), null!, null!, new PasswordHasher<User>());
        await service.MergeUsersAsync(new MergeUsersRequest { TargetUserId = target.Id, SourceUserIds = [source.Id] }, target.Id);
        fixture.Db.ChangeTracker.Clear();
        var moved = await fixture.Db.TaskTimeEntries.SingleAsync(item => item.Id == sourceEntry);
        Assert.Equal(target.Id, moved.UserId);
        Assert.NotNull(moved.StoppedAtUtc);
        Assert.Equal("Исходный сотрудник", moved.UserNameSnapshot);
        Assert.Null((await fixture.Db.TaskTimeEntries.SingleAsync(item => item.Id == targetEntry)).StoppedAtUtc);
        Assert.False(await fixture.Db.Users.AnyAsync(item => item.Id == source.Id));
    }

    [Theory]
    [InlineData(false, "done")]
    [InlineData(false, "cancelled")]
    [InlineData(false, "deleted")]
    [InlineData(true, "done")]
    [InlineData(true, "cancelled")]
    [InlineData(true, "deleted")]
    public async Task TaskTimer_ClosingTaskStopsOnlyItsIntervalsInTheSameTransaction(bool postgres, string transition)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var actor = await fixture.AddUserAsync("timer-close@example.test", "Автор");
        var colleague = await fixture.AddUserAsync("timer-close-colleague@example.test", "Исполнитель");
        var other = await fixture.AddUserAsync("timer-close-other@example.test", "Другой");
        var task = await AddTimerTaskAsync(fixture, actor);
        task.AssigneeUserId = colleague.Id;
        var unrelated = await AddTimerTaskAsync(fixture, other, addPermission: false);
        await fixture.Db.SaveChangesAsync();
        var clock = new TimerClock(new DateTimeOffset(2099, 10, 27, 10, 0, 0, TimeSpan.Zero));
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var third = Guid.NewGuid();
        TimerState(await TimerController(fixture.Db, actor, clock).Start(new(first, task.Id), default));
        TimerState(await TimerController(fixture.Db, colleague, clock).Start(new(second, task.Id), default));
        TimerState(await TimerController(fixture.Db, other, clock).Start(new(third, unrelated.Id), default));
        await using (var transaction = await fixture.Db.Database.BeginTransactionAsync())
        {
            if (transition == "deleted") { fixture.Db.ChangeTracker.CascadeDeleteTiming = CascadeTiming.OnSaveChanges; fixture.Db.TaskJournalItems.Remove(task); }
            else task.Status = transition;
            await fixture.Db.SaveChangesAsync();
            Assert.Equal(2, await fixture.Db.TaskTimeEntries.CountAsync(item => item.TaskId == task.Id && item.StoppedAtUtc != null));
            await transaction.RollbackAsync();
        }
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(2, await fixture.Db.TaskTimeEntries.CountAsync(item => item.TaskId == task.Id && item.StoppedAtUtc == null));
        var current = await fixture.Db.TaskJournalItems.SingleAsync(item => item.Id == task.Id);
        if (transition == "deleted") { fixture.Db.ChangeTracker.CascadeDeleteTiming = CascadeTiming.OnSaveChanges; fixture.Db.TaskJournalItems.Remove(current); }
        else current.Status = transition;
        if (transition == "cancelled") fixture.Db.SaveChanges(); else await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(2, await fixture.Db.TaskTimeEntries.CountAsync(item => item.TaskId == task.Id && item.StoppedAtUtc != null));
        Assert.Null((await fixture.Db.TaskTimeEntries.SingleAsync(item => item.Id == third)).StoppedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskTimer_ReplayAndStopAreIdempotentWithoutFinancialSideEffects(bool postgres)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var actor = await fixture.AddUserAsync("timer-owner@example.test", "Автор");
        var task = await AddTimerTaskAsync(fixture, actor);
        var clock = new TimerClock(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));
        var controller = TimerController(fixture.Db, actor, clock);
        var id = Guid.NewGuid();
        var first = TimerState(await controller.Start(new(id, task.Id), default));
        Assert.Equal(task.Id, first.TaskId);
        Assert.Null(first.StoppedAtUtc);
        Assert.Equal(first, TimerState(await controller.Start(new(id, task.Id), default)));
        clock.Value = clock.Value.AddMinutes(37);
        var stopped = TimerState(await controller.Stop(id, default));
        Assert.Equal(first.StartedAtUtc.AddMinutes(37), stopped.StoppedAtUtc);
        clock.Value = clock.Value.AddMinutes(5);
        Assert.Equal(stopped, TimerState(await controller.Stop(id, default)));
        Assert.Equal(stopped, TimerState(await controller.Start(new(id, task.Id), default)));
        Assert.IsType<OkObjectResult>(await controller.Start(new(Guid.NewGuid(), task.Id), default));
        Assert.Equal(2, await fixture.Db.TaskTimeEntries.CountAsync());
        Assert.Equal(0, await fixture.Db.Payments.CountAsync());
        Assert.Equal(0, await fixture.Db.Invoices.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskTimer_ConcurrentStopsPreserveOneFinalTimestamp(bool postgres)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var actor = await fixture.AddUserAsync("timer-stop-race@example.test", "Автор");
        var task = await AddTimerTaskAsync(fixture, actor);
        var start = new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
        var id = Guid.NewGuid();
        TimerState(await TimerController(fixture.Db, actor, new TimerClock(start)).Start(new(id, task.Id), default));
        await using var left = CaptureFreshDb(fixture, independentSqlite: true);
        await using var right = CaptureFreshDb(fixture, independentSqlite: true);
        var first = Task.Run(() => TimerController(left, actor, new TimerClock(start.AddMinutes(10))).Stop(id, default));
        var second = Task.Run(() => TimerController(right, actor, new TimerClock(start.AddMinutes(11))).Stop(id, default));
        var results = await Task.WhenAll(first, second);
        Assert.Contains(results, result => result is OkObjectResult);
        Assert.All(results, result => Assert.True(result is OkObjectResult or ConflictObjectResult));
        fixture.Db.ChangeTracker.Clear();
        var entry = await fixture.Db.TaskTimeEntries.AsNoTracking().SingleAsync(item => item.Id == id);
        Assert.Contains(entry.StoppedAtUtc, new DateTime?[] { start.AddMinutes(10).UtcDateTime, start.AddMinutes(11).UtcDateTime });
        var repeated = TimerState(await TimerController(fixture.Db, actor, new TimerClock(start.AddMinutes(15))).Stop(id, default));
        Assert.Equal(entry.StoppedAtUtc, repeated.StoppedAtUtc);
        Assert.Single(await fixture.Db.TaskTimeEntries.AsNoTracking().Where(item => item.UserId == actor.Id).ToArrayAsync());
        Assert.Empty(await fixture.Db.Payments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.Invoices.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskTimer_ConcurrentStartsAllowOnlyOneRunningTimerPerEmployee(bool postgres)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var actor = await fixture.AddUserAsync("timer-race@example.test", "Автор");
        var first = await AddTimerTaskAsync(fixture, actor);
        var second = await AddTimerTaskAsync(fixture, actor, addPermission: false);
        await using var left = CaptureFreshDb(fixture, independentSqlite: true);
        await using var right = CaptureFreshDb(fixture, independentSqlite: true);
        var clock = new TimerClock(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));
        var a = Task.Run(() => TimerController(left, actor, clock).Start(new(Guid.NewGuid(), first.Id), default));
        var b = Task.Run(() => TimerController(right, actor, clock).Start(new(Guid.NewGuid(), second.Id), default));
        var results = await Task.WhenAll(a, b);
        Assert.Single(results.OfType<OkObjectResult>());
        Assert.Single(results.OfType<ConflictObjectResult>());
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(1, await fixture.Db.TaskTimeEntries.CountAsync(item => item.UserId == actor.Id && item.StoppedAtUtc == null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskTimer_ConcurrentStartAndTaskCompletionLeavesNoRunningInterval(bool postgres)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var actor = await fixture.AddUserAsync("timer-close-race@example.test", "Автор");
        var task = await AddTimerTaskAsync(fixture, actor);
        await using var left = CaptureFreshDb(fixture, independentSqlite: true);
        await using var right = CaptureFreshDb(fixture, independentSqlite: true);
        var clock = new TimerClock(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));
        var timer = Task.Run(() => TimerController(left, actor, clock).Start(new(Guid.NewGuid(), task.Id), default));
        var finish = Task.Run(async () => { var item = await right.TaskJournalItems.SingleAsync(row => row.Id == task.Id); item.Status = "done"; await right.SaveChangesAsync(); });
        await Task.WhenAll(timer, finish);
        Assert.True(await timer is OkObjectResult or ConflictObjectResult);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("done", (await fixture.Db.TaskJournalItems.SingleAsync(item => item.Id == task.Id)).Status);
        Assert.False(await fixture.Db.TaskTimeEntries.AnyAsync(item => item.TaskId == task.Id && item.StoppedAtUtc == null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskTimer_ConcurrentStartAndDeactivationLeavesNoRunningInterval(bool postgres)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var actor = await fixture.AddUserAsync("timer-deactivation-race@example.test", "Автор");
        var task = await AddTimerTaskAsync(fixture, actor);
        await using var left = CaptureFreshDb(fixture, independentSqlite: true);
        await using var right = CaptureFreshDb(fixture, independentSqlite: true);
        var clock = new TimerClock(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));
        var timer = Task.Run(() => TimerController(left, actor, clock).Start(new(Guid.NewGuid(), task.Id), default));
        var update = Task.Run(() => new AuthService(right, new ConfigurationBuilder().Build(), null!, null!, new PasswordHasher<User>())
            .UpdateUserAsync(actor.Id, new UpdateUserRequest { IsActive = false }, actor.Id));
        await Task.WhenAll(timer, update);
        Assert.True(await timer is OkObjectResult or ConflictObjectResult or ForbidResult);
        Assert.NotNull(await update);
        fixture.Db.ChangeTracker.Clear();
        Assert.False((await fixture.Db.Users.SingleAsync(item => item.Id == actor.Id)).IsActive);
        Assert.False(await fixture.Db.TaskTimeEntries.AnyAsync(item => item.UserId == actor.Id && item.StoppedAtUtc == null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskTimer_DeactivationStopsActiveIntervalAtomically(bool postgres)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var actor = await fixture.AddUserAsync("timer-deactivation@example.test", "Автор");
        var task = await AddTimerTaskAsync(fixture, actor);
        var clock = new TimerClock(new DateTimeOffset(2099, 10, 27, 10, 0, 0, TimeSpan.Zero));
        var id = Guid.NewGuid();
        var started = TimerState(await TimerController(fixture.Db, actor, clock).Start(new(id, task.Id), default));
        var service = new AuthService(fixture.Db, new ConfigurationBuilder().Build(), null!, null!, new PasswordHasher<User>());
        var result = await service.UpdateUserAsync(actor.Id, new UpdateUserRequest { IsActive = false }, actor.Id);
        Assert.NotNull(result);
        fixture.Db.ChangeTracker.Clear();
        var entry = await fixture.Db.TaskTimeEntries.SingleAsync(item => item.Id == id);
        Assert.Equal(started.StartedAtUtc, entry.StoppedAtUtc);
        Assert.False((await fixture.Db.Users.SingleAsync(item => item.Id == actor.Id)).IsActive);
        Assert.IsType<ForbidResult>(await TimerController(fixture.Db, actor, clock).Start(new(Guid.NewGuid(), task.Id), default));
        Assert.Equal(0, await fixture.Db.Payments.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskTimer_ConcurrentCorrectionsCreateOneAuditRecord(bool postgres)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var actor = await fixture.AddUserAsync("timer-correction-race@example.test", "Автор");
        var task = await AddTimerTaskAsync(fixture, actor);
        var clock = new TimerClock(new DateTimeOffset(2026, 10, 27, 23, 59, 0, TimeSpan.Zero));
        var id = Guid.NewGuid();
        TimerState(await TimerController(fixture.Db, actor, clock).Start(new(id, task.Id), default));
        clock.Value = clock.Value.AddMinutes(2);
        var stopped = TimerState(await TimerController(fixture.Db, actor, clock).Stop(id, default));
        Assert.Equal(new DateTime(2026, 10, 28, 0, 1, 0, DateTimeKind.Utc), stopped.StoppedAtUtc);
        Assert.Equal(DateTimeKind.Utc, stopped.StoppedAtUtc!.Value.Kind);
        await using var left = CaptureFreshDb(fixture, independentSqlite: true);
        await using var right = CaptureFreshDb(fixture, independentSqlite: true);
        var a = Task.Run(() => TimerController(left, actor, clock).Correct(task.Id, id, new(0, 60, "Первое исправление"), default));
        var b = Task.Run(() => TimerController(right, actor, clock).Correct(task.Id, id, new(0, 90, "Второе исправление"), default));
        var results = await Task.WhenAll(a, b);
        Assert.Single(results.OfType<OkObjectResult>());
        Assert.Single(results.OfType<ConflictObjectResult>());
        fixture.Db.ChangeTracker.Clear();
        var entry = await fixture.Db.TaskTimeEntries.SingleAsync(item => item.Id == id);
        var audit = await fixture.Db.TaskTimeCorrections.SingleAsync(item => item.EntryId == id);
        Assert.Equal(audit.NewSeconds, entry.CorrectedSeconds);
        Assert.Equal(1, entry.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskTimer_CorrectionPreservesClockAndAuditsReasonWithoutDuplicateReplay(bool postgres)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var actor = await fixture.AddUserAsync("timer-correction@example.test", "Автор");
        var other = await fixture.AddUserAsync("timer-correction-other@example.test", "Другой");
        var task = await AddTimerTaskAsync(fixture, actor);
        task.AssigneeUserId = other.Id;
        task.AssigneeName = other.FullName;
        await fixture.Db.SaveChangesAsync();
        Assert.True(await new PayPlanner.Api.Services.TaskFilePublicationAuthorizationService(fixture.Db).CanPublishAsync(other.Id, task.Id, default));
        var clock = new TimerClock(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));
        var controller = TimerController(fixture.Db, actor, clock);
        var id = Guid.NewGuid();
        var original = TimerState(await controller.Start(new(id, task.Id), default));
        Assert.IsType<ConflictObjectResult>(await controller.Correct(task.Id, id, new(0, 120, "Ошибка"), default));
        clock.Value = clock.Value.AddMinutes(20);
        var stopped = TimerState(await controller.Stop(id, default));
        Assert.IsType<BadRequestObjectResult>(await controller.Correct(task.Id, id, new(0, -1, "Ошибка"), default));
        Assert.IsType<BadRequestObjectResult>(await controller.Correct(task.Id, id, new(0, null, "Ошибка"), default));
        Assert.IsType<BadRequestObjectResult>(await controller.Correct(task.Id, id, new(null, 120, "Ошибка"), default));
        Assert.IsType<BadRequestObjectResult>(await controller.Correct(task.Id, id, new(0, 120, "  "), default));
        Assert.IsType<NotFoundResult>(await TimerController(fixture.Db, other, clock).Correct(task.Id, id, new(0, 120, "Ошибка"), default));
        var corrected = TimerState(await controller.Correct(task.Id, id, new(0, 120, " Уточнение "), default));
        Assert.Equal(original.StartedAtUtc, corrected.StartedAtUtc);
        Assert.Equal(stopped.StoppedAtUtc, corrected.StoppedAtUtc);
        Assert.Equal(120, corrected.CorrectedSeconds);
        Assert.Equal(1, corrected.Revision);
        Assert.IsType<ConflictObjectResult>(await controller.Correct(task.Id, id, new(0, 120, "Уточнение"), default));
        var audit = await fixture.Db.TaskTimeCorrections.SingleAsync();
        Assert.Null(audit.PreviousSeconds);
        Assert.Equal(120, audit.NewSeconds);
        Assert.Equal("Уточнение", audit.Reason);
        var changes = Assert.IsType<TaskTimerCorrectionsDto>(Assert.IsType<OkObjectResult>(await controller.Corrections(task.Id, id, default)).Value);
        Assert.Equal("Уточнение", Assert.Single(changes.Items).Reason);
        Assert.Null(changes.NextBeforeId);
        Assert.IsType<NotFoundResult>(await TimerController(fixture.Db, other, clock).Corrections(task.Id, id, default));
        Assert.Equal(0, await fixture.Db.Payments.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskTimer_HistoryIsPersonalBoundedAndRequiresTaskPermission(bool postgres)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var actor = await fixture.AddUserAsync("timer-history@example.test", "Автор");
        var outsider = await fixture.AddUserAsync("timer-history-other@example.test", "Другой");
        var task = await AddTimerTaskAsync(fixture, actor);
        var start = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 52; i++)
            fixture.Db.TaskTimeEntries.Add(new TaskTimeEntry
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                UserId = actor.Id,
                StartedAtUtc = start.AddHours(i),
                StoppedAtUtc = start.AddHours(i).AddMinutes(10)
            });
        fixture.Db.TaskTimeEntries.Add(new TaskTimeEntry
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            UserId = outsider.Id,
            StartedAtUtc = start.AddDays(10),
            StoppedAtUtc = start.AddDays(10).AddMinutes(10)
        });
        await fixture.Db.SaveChangesAsync();
        var controller = TimerController(fixture.Db, actor, new TimerClock(new DateTimeOffset(start)));
        var page = Assert.IsType<TaskTimerHistoryDto>(Assert.IsType<OkObjectResult>(await controller.History(task.Id, default)).Value);
        Assert.True(page.HasMore);
        Assert.Equal(50, page.Items.Count);
        Assert.Equal(start.AddHours(51), page.Items[0].StartedAtUtc);
        Assert.Equal(DateTimeKind.Utc, page.Items[0].StartedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, page.Items[0].StoppedAtUtc!.Value.Kind);
        Assert.Equal(start.AddHours(2), page.Items[^1].StartedAtUtc);
        Assert.NotNull(page.NextCursor);
        var remainder = Assert.IsType<TaskTimerHistoryDto>(Assert.IsType<OkObjectResult>(await controller.History(task.Id, default, page.NextCursor.BeforeStartedAtUtc, page.NextCursor.BeforeId)).Value);
        Assert.Equal(2, remainder.Items.Count);
        Assert.False(remainder.HasMore);
        Assert.Null(remainder.NextCursor);
        Assert.IsType<NotFoundResult>(await TimerController(fixture.Db, outsider, new TimerClock(new DateTimeOffset(start))).History(task.Id, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskTimer_HistoryPagesAreStableForEqualTimestampsAndNewerEntries(bool postgres)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var actor = await fixture.AddUserAsync("timer-pages@example.test", "Автор");
        var task = await AddTimerTaskAsync(fixture, actor);
        var at = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
        var ids = Enumerable.Range(0, 105).Select(_ => Guid.NewGuid()).ToHashSet();
        foreach (var id in ids) fixture.Db.TaskTimeEntries.Add(new TaskTimeEntry
        {
            Id = id,
            TaskId = task.Id,
            UserId = actor.Id,
            UserNameSnapshot = actor.FullName,
            StartedAtUtc = at,
            StoppedAtUtc = at.AddMinutes(10)
        });
        await fixture.Db.SaveChangesAsync();
        var controller = TimerController(fixture.Db, actor, new TimerClock(new DateTimeOffset(at)));
        var seen = new HashSet<Guid>();
        var page = Assert.IsType<TaskTimerHistoryDto>(Assert.IsType<OkObjectResult>(await controller.History(task.Id, default)).Value);
        foreach (var item in page.Items) Assert.True(seen.Add(item.Id));
        fixture.Db.TaskTimeEntries.Add(new TaskTimeEntry
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            UserId = actor.Id,
            UserNameSnapshot = actor.FullName,
            StartedAtUtc = at.AddHours(1),
            StoppedAtUtc = at.AddHours(1).AddMinutes(1)
        });
        await fixture.Db.SaveChangesAsync();
        while (page.NextCursor is { } cursor)
        {
            page = Assert.IsType<TaskTimerHistoryDto>(Assert.IsType<OkObjectResult>(await controller.History(task.Id, default, cursor.BeforeStartedAtUtc, cursor.BeforeId)).Value);
            foreach (var item in page.Items) Assert.True(seen.Add(item.Id));
        }
        Assert.True(ids.SetEquals(seen));
        Assert.IsType<BadRequestObjectResult>(await controller.History(task.Id, default, at, null));
        Assert.IsType<BadRequestObjectResult>(await controller.History(task.Id, default, DateTime.SpecifyKind(at, DateTimeKind.Unspecified), Guid.NewGuid()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskTimer_CorrectionHistoryIsPaginatedAndPersonal(bool postgres)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var actor = await fixture.AddUserAsync("timer-correction-pages@example.test", "Автор");
        var colleague = await fixture.AddUserAsync("timer-correction-pages-other@example.test", "Исполнитель");
        var task = await AddTimerTaskAsync(fixture, actor);
        task.AssigneeUserId = colleague.Id;
        var at = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
        var entry = new TaskTimeEntry
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            UserId = actor.Id,
            UserNameSnapshot = actor.FullName,
            StartedAtUtc = at,
            StoppedAtUtc = at.AddMinutes(1)
        };
        fixture.Db.TaskTimeEntries.Add(entry);
        for (var i = 0; i < 52; i++) fixture.Db.TaskTimeCorrections.Add(new TaskTimeCorrection
        {
            Entry = entry,
            NewSeconds = i,
            Reason = "Причина " + i,
            CreatedAtUtc = at
        });
        await fixture.Db.SaveChangesAsync();
        var controller = TimerController(fixture.Db, actor, new TimerClock(new DateTimeOffset(at)));
        var first = Assert.IsType<TaskTimerCorrectionsDto>(Assert.IsType<OkObjectResult>(await controller.Corrections(task.Id, entry.Id, default)).Value);
        Assert.Equal(50, first.Items.Count);
        Assert.NotNull(first.NextBeforeId);
        var second = Assert.IsType<TaskTimerCorrectionsDto>(Assert.IsType<OkObjectResult>(await controller.Corrections(task.Id, entry.Id, default, long.Parse(first.NextBeforeId!))).Value);
        Assert.Equal(2, second.Items.Count);
        Assert.Null(second.NextBeforeId);
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));
        Assert.IsType<NotFoundResult>(await TimerController(fixture.Db, colleague, new TimerClock(new DateTimeOffset(at))).Corrections(task.Id, entry.Id, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskTimer_RejectsInvisibleAndClosedTasksAndCanStopAfterAccessRevocation(bool postgres)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var actor = await fixture.AddUserAsync("timer-access@example.test", "Автор");
        var outsider = await fixture.AddUserAsync("timer-outsider@example.test", "Посторонний");
        var task = await AddTimerTaskAsync(fixture, actor);
        var clock = new TimerClock(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));
        Assert.IsType<NotFoundResult>(await TimerController(fixture.Db, outsider, clock).Start(new(Guid.NewGuid(), task.Id), default));
        task.Status = "done";
        await fixture.Db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>(await TimerController(fixture.Db, actor, clock).Start(new(Guid.NewGuid(), task.Id), default));
        task.Status = "inProgress";
        await fixture.Db.SaveChangesAsync();
        var id = Guid.NewGuid();
        TimerState(await TimerController(fixture.Db, actor, clock).Start(new(id, task.Id), default));
        var permission = await fixture.Db.RolePermissions.SingleAsync(item => item.RoleId == actor.RoleId && item.Section == RolePermissionCatalog.Tasks);
        permission.CanView = false;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var current = TimerState(await TimerController(fixture.Db, actor, clock).Current(default));
        Assert.Null(current.TaskId);
        var stopped = TimerState(await TimerController(fixture.Db, actor, clock).Stop(id, default));
        Assert.NotNull(stopped.StoppedAtUtc);
        Assert.Null(stopped.TaskId);
        Assert.IsType<NotFoundResult>(await TimerController(fixture.Db, outsider, clock).Stop(id, default));
    }

    private static async Task<TaskJournalItem> AddTimerTaskAsync(Fixture fixture, User actor, bool addPermission = true)
    {
        if (addPermission)
            fixture.Db.RolePermissions.Add(new RolePermission
            {
                RoleId = actor.RoleId,
                Section = RolePermissionCatalog.Tasks,
                CanView = true,
                CanEdit = true,
                ViewScope = DataVisibilityScope.Own
            });
        var task = new TaskJournalItem
        {
            ObjectName = "Объект",
            Title = "Работа",
            AuthorUserId = actor.Id,
            AuthorName = actor.FullName,
            DueAt = DateTime.UtcNow.AddDays(1),
            Status = "inProgress"
        };
        fixture.Db.TaskJournalItems.Add(task);
        await fixture.Db.SaveChangesAsync();
        return task;
    }

    private static TaskTimeController TimerController(PaymentContext db, User user, TimeProvider clock)
        => new(db, clock)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "test"))
                }
            }
        };

    private static TaskTimerDto TimerState(IActionResult result)
        => Assert.IsType<TaskTimerDto>(Assert.IsType<OkObjectResult>(result).Value);

    private sealed class TimerClock(DateTimeOffset value) : TimeProvider
    {
        public DateTimeOffset Value { get; set; } = value;
        public override DateTimeOffset GetUtcNow() => Value;
    }

    private sealed class TimerCalendarCleanup(Func<Task> action) : IExternalCalendarSyncCoordinator
    {
        public int Calls { get; private set; }
        public Task SyncTaskForConnectedUsersAsync(int taskId, string sourceProvider = "crm", CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteTaskForConnectedUsersAsync(int taskId, string sourceProvider = "crm", CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task CleanupDeletedUserAsync(int userId, int actorUserId, CancellationToken cancellationToken = default) { Calls++; await action(); }
        public Task HandleUserAccessRevokedAsync(int userId, int actorUserId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
