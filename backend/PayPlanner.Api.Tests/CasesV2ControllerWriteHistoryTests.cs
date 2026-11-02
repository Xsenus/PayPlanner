using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public async Task CaseWriteHistory_ConcurrentAppendCannotBeLostOrMissingFromSavedReply(bool postgres, bool legacy, bool editHistory)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        var sqlite = $"Data Source=case-write-history-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=1;Pooling=False";
        await using var anchor = postgres ? null : new SqliteConnection(sqlite);
        if (anchor is not null) await anchor.OpenAsync();
        PaymentContext Open(params IInterceptor[] interceptors) => postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(database!.ConnectionString).AddInterceptors(interceptors).Options)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(sqlite).AddInterceptors(interceptors).Options, useObjectAuditModel: true);
        await using var seed = Open();
        if (!postgres) await seed.Database.EnsureCreatedAsync();
        var (user, clientCase) = await SeedConfirmationCaseAsync(seed);
        var confirmed = false;
        var hook = new ConfirmationBeforeSave(async ct =>
        {
            await using var other = Open();
            var confirmation = await CreateController(other, user).ReviewCasePosition(clientCase.Id, new(clientCase.Strategy), ct);
            Assert.True(confirmation.Result is OkObjectResult or ConflictObjectResult);
            confirmed = confirmation.Result is OkObjectResult;
        });
        await using var request = Open(hook);
        var controller = CreateController(request, user);
        var model = new ClientCase
        {
            ClientId = clientCase.ClientId,
            Title = "Изменённое название",
            Status = clientCase.Status,
            ResponsibleUserId = user.Id,
            Strategy = clientCase.Strategy,
            NextHearingAt = clientCase.NextHearingAt,
            EventHistory = editHistory ? "Моя запись" : clientCase.EventHistory,
        };
        var result = legacy
            ? await new CasesController(request) { ControllerContext = controller.ControllerContext }.Update(clientCase.Id, model, CancellationToken.None)
            : await controller.Update(clientCase.Id, model, CancellationToken.None);
        await using var verify = Open();
        var actual = await verify.ClientCases.AsNoTracking().SingleAsync();
        Console.WriteLine($"CASE_HISTORY postgres={postgres} legacy={legacy} editHistory={editHistory} competingCommitted={confirmed} response={result.GetType().Name}");
        if (confirmed && editHistory)
        {
            Assert.IsType<ConflictObjectResult>(result);
            Assert.Contains("Позиция по делу проверена", actual.EventHistory);
            Assert.Equal(clientCase.Title, actual.Title);
        }
        else
        {
            var reply = Assert.IsType<ClientCase>(Assert.IsType<OkObjectResult>(result).Value);
            Assert.Equal(actual.EventHistory, reply.EventHistory);
            if (confirmed) Assert.Contains("Позиция по делу проверена", reply.EventHistory);
        }
        Assert.True(hook.Called);
        Assert.Equal(123.45m, actual.CourtClaimAmount);
        Assert.Empty(await verify.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseWriteHistory_FailureOrCancellationRollsBackAndDoesNotPublish(bool legacy, bool cancel)
    {
        var failure = new ConfirmationAfterSaveFailure(cancel);
        await using var db = await CreateContextAsync(failure);
        var (user, source) = await SeedConfirmationCaseAsync(db);
        var request = EditRequest(source); request.Title = "Нельзя сохранить частично"; request.EventHistory = "Новая история";
        var publisher = new RecordingRealtimeNotificationPublisher();
        var controller = CreateController(db, user, realtimePublisher: publisher);
        using var cancellation = new CancellationTokenSource(); failure.Cancel = cancellation.Cancel; failure.Armed = true;
        Task Write() => legacy
            ? new CasesController(db, publisher) { ControllerContext = controller.ControllerContext }.UpdateWithSource(source.Id, request, cancellation.Token)
            : controller.UpdateWithSource(source.Id, request, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write);
        else await Assert.ThrowsAsync<ConfirmationSaveFailureException>(Write);
        Assert.True(failure.Called); Assert.Equal(0, publisher.TaskJournalChangedCount);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Equal(source.Title, actual.Title); Assert.Equal(source.EventHistory, actual.EventHistory);
        Assert.Equal(123.45m, actual.CourtClaimAmount); Assert.Empty(await db.Payments.ToArrayAsync());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseWriteHistory_PendingChangesOrCallerTransactionArePreserved(bool legacy, bool ambient)
    {
        await using var db = await CreateContextAsync(); var (user, source) = await SeedConfirmationCaseAsync(db);
        await using var transaction = ambient ? await db.Database.BeginTransactionAsync() : null;
        var pending = new Client { Name = "Несохранённый клиент", IsActive = true };
        if (!ambient) db.Clients.Add(pending);
        var request = EditRequest(source); request.Title = "Не сохранять";
        var controller = CreateController(db, user);
        var result = legacy
            ? await new CasesController(db) { ControllerContext = controller.ControllerContext }.UpdateWithSource(source.Id, request, CancellationToken.None)
            : await controller.UpdateWithSource(source.Id, request, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(result);
        if (ambient) Assert.Same(transaction, db.Database.CurrentTransaction);
        else Assert.Equal(EntityState.Added, db.Entry(pending).State);
        Assert.Equal(source.Title, (await db.ClientCases.AsNoTracking().SingleAsync()).Title);
        Assert.Single(await db.Clients.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseWriteHistory_RealtimeIsPublishedOnlyAfterCommit(bool legacy)
    {
        await using var db = await CreateContextAsync(); var (user, source) = await SeedConfirmationCaseAsync(db);
        var request = EditRequest(source); request.Title = "Сохранено до события";
        var publisher = new CaseWriteCommitPublisher(db, request.Title);
        var controller = CreateController(db, user, realtimePublisher: publisher);
        var result = legacy
            ? await new CasesController(db, publisher) { ControllerContext = controller.ControllerContext }.UpdateWithSource(source.Id, request, CancellationToken.None)
            : await controller.UpdateWithSource(source.Id, request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result); Assert.Equal(1, publisher.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseWriteHistory_PostgresAccessAndClientOwnerRemainLockedUntilCommit(bool legacy, bool owner)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PaymentContext Open(params IInterceptor[] interceptors) => new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(interceptors).Options);
        await using var seed = Open(); var (user, source) = await SeedConfirmationCaseAsync(seed);
        var otherUser = await CreateUserAsync(seed, "case-other-owner@example.local", "Другой сотрудник", DataVisibilityScope.All);
        source.ResponsibleUserId = null;
        await seed.RolePermissions.Where(row => row.RoleId == user.RoleId && row.Section == RolePermissionCatalog.Cases)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Own));
        await seed.SaveChangesAsync();
        var request = EditRequest(source); request.Title = "Сохранено с действующим доступом";
        var hook = new ConfirmationBeforeSave(async ct =>
        {
            await using var other = Open(); await using var transaction = await other.Database.BeginTransactionAsync(ct);
            await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
            var failure = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                if (owner) await other.Clients.Where(row => row.Id == source.ClientId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, otherUser.Id), ct);
                else await other.RolePermissions.Where(row => row.RoleId == user.RoleId && row.Section == RolePermissionCatalog.Cases)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false), ct);
            });
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, failure.SqlState);
            await transaction.RollbackAsync(ct);
        });
        await using var write = Open(hook); var controller = CreateController(write, user);
        var result = legacy
            ? await new CasesController(write) { ControllerContext = controller.ControllerContext }.UpdateWithSource(source.Id, request, CancellationToken.None)
            : await controller.UpdateWithSource(source.Id, request, CancellationToken.None);
        Assert.True(hook.Called, "Write stopped before the competing mutation: " + System.Text.Json.JsonSerializer.Serialize((result as ObjectResult)?.Value));
        Assert.True(result is OkObjectResult or ConflictObjectResult); Assert.Null(write.Database.CurrentTransaction);
        Console.WriteLine($"CASE_ACCESS_LOCK legacy={legacy} owner={owner} result={result.GetType().Name}");
        await using var verify = Open(); Assert.Equal(result is OkObjectResult ? request.Title : source.Title, (await verify.ClientCases.AsNoTracking().SingleAsync()).Title);
        Assert.Equal(user.Id, (await verify.Clients.AsNoTracking().SingleAsync()).ResponsibleUserId);
        Assert.True((await verify.RolePermissions.AsNoTracking().SingleAsync(row => row.RoleId == user.RoleId && row.Section == RolePermissionCatalog.Cases)).CanEdit);
        Assert.Equal(123.45m, (await verify.ClientCases.AsNoTracking().SingleAsync()).CourtClaimAmount);
    }

    private sealed class CaseWriteCommitPublisher(PaymentContext db, string title) : IRealtimeNotificationPublisher
    {
        public int Calls { get; private set; }
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default)
        {
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Equal(title, (await db.ClientCases.AsNoTracking().SingleAsync(cancellationToken)).Title);
            Calls++;
        }
    }
}
