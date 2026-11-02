using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task CaseConfirmations_ConcurrentSourceOrPermissionChangeCannotReceiveStaleMark(bool postgres, bool hearing, bool permission)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        var sqlite = $"Data Source=case-confirmation-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=1;Pooling=False";
        await using var anchor = postgres ? null : new SqliteConnection(sqlite);
        if (anchor is not null) await anchor.OpenAsync();
        PaymentContext Open(params IInterceptor[] interceptors) => postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(database!.ConnectionString).AddInterceptors(interceptors).Options)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(sqlite).AddInterceptors(interceptors).Options, useObjectAuditModel: true);
        await using var seed = Open();
        if (!postgres) await seed.Database.EnsureCreatedAsync();
        var user = await CreateUserAsync(seed, "confirmation-race@example.local", "Юрист", DataVisibilityScope.All);
        var date = new DateTime(2099, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = user.Id },
            ResponsibleUserId = user.Id,
            Title = "Дело",
            Status = ClientCaseStatus.Open,
            Strategy = "Исходная позиция",
            NextHearingAt = date,
            CourtClaimAmount = 123.45m,
        };
        seed.ClientCases.Add(clientCase);
        await seed.SaveChangesAsync();
        var competingCommitted = false;
        var hook = new ConfirmationBeforeSave(async ct =>
        {
            await using var other = Open();
            try
            {
                await using var transaction = await other.Database.BeginTransactionAsync(ct);
                if (postgres) await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
                if (permission)
                    await other.RolePermissions.Where(row => row.RoleId == user.RoleId && row.Section == RolePermissionCatalog.Cases)
                        .ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false), ct);
                else if (hearing)
                    await other.ClientCases.Where(row => row.Id == clientCase.Id)
                        .ExecuteUpdateAsync(set => set.SetProperty(row => row.NextHearingAt, date.AddDays(1)), ct);
                else
                    await other.ClientCases.Where(row => row.Id == clientCase.Id)
                        .ExecuteUpdateAsync(set => set.SetProperty(row => row.Strategy, "Новая позиция"), ct);
                await transaction.CommitAsync(ct);
                competingCommitted = true;
            }
            catch (Exception error) when (IsRecoveryTestLockConflict(error)) { }
        });
        await using var request = Open(hook);
        var controller = CreateController(request, user);
        ActionResult result = hearing
            ? (await controller.ConfirmNextHearing(clientCase.Id, new(clientCase.NextHearingAt ?? new DateTime(2099, 11, 3, 12, 0, 0, DateTimeKind.Utc)), CancellationToken.None)).Result!
            : (await controller.ReviewCasePosition(clientCase.Id, new(clientCase.Strategy), CancellationToken.None)).Result!;
        Assert.True(hook.Called);
        await using var verify = Open();
        var actual = await verify.ClientCases.AsNoTracking().SingleAsync(row => row.Id == clientCase.Id);
        Console.WriteLine($"CONFIRMATION_RACE postgres={postgres} hearing={hearing} permission={permission} competingCommitted={competingCommitted} response={result.GetType().Name}");
        if (competingCommitted)
        {
            Assert.IsType<ConflictObjectResult>(result);
            Assert.Null(actual.PositionReviewedAt);
            Assert.Null(actual.NextHearingConfirmedAt);
            Assert.Empty(actual.EventHistory);
        }
        else
        {
            Assert.IsType<OkObjectResult>(result);
            Assert.Equal("Исходная позиция", actual.Strategy);
            Assert.Equal(date, actual.NextHearingAt);
            Assert.True(hearing ? actual.NextHearingConfirmedAt.HasValue : actual.PositionReviewedAt.HasValue);
        }
        Assert.Equal(123.45m, actual.CourtClaimAmount);
        Assert.Empty(await verify.Payments.ToArrayAsync());
        Assert.Null(request.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseConfirmations_ConcurrentConfirmationsKeepOneHistoryEntry(bool postgres, bool hearing)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        var sqlite = $"Data Source=case-confirmation-repeat-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=1;Pooling=False";
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
        ActionResult? competing = null;
        var hook = new ConfirmationBeforeSave(async ct =>
        {
            await using var other = Open();
            var controller = CreateController(other, user);
            competing = hearing
                ? (await controller.ConfirmNextHearing(clientCase.Id, new(clientCase.NextHearingAt ?? new DateTime(2099, 11, 3, 12, 0, 0, DateTimeKind.Utc)), ct)).Result
                : (await controller.ReviewCasePosition(clientCase.Id, new(clientCase.Strategy), ct)).Result;
            Assert.Null(other.Database.CurrentTransaction);
        });
        await using var request = Open(hook);
        var firstController = CreateController(request, user);
        ActionResult first = hearing
            ? (await firstController.ConfirmNextHearing(clientCase.Id, new(clientCase.NextHearingAt ?? new DateTime(2099, 11, 3, 12, 0, 0, DateTimeKind.Utc)), CancellationToken.None)).Result!
            : (await firstController.ReviewCasePosition(clientCase.Id, new(clientCase.Strategy), CancellationToken.None)).Result!;
        Assert.True(hook.Called);
        Assert.IsType<ConflictObjectResult>(competing);
        Assert.IsType<OkObjectResult>(first);
        await using var verify = Open();
        var actual = await verify.ClientCases.AsNoTracking().SingleAsync();
        var text = hearing ? "Дата следующего заседания проверена" : "Позиция по делу проверена";
        Assert.Equal(1, actual.EventHistory.Split(text, StringSplitOptions.None).Length - 1);
        Assert.True(hearing ? actual.NextHearingConfirmedAt.HasValue : actual.PositionReviewedAt.HasValue);
        Assert.Equal(123.45m, actual.CourtClaimAmount);
        Assert.Empty(await verify.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, "permission")]
    [InlineData(true, "permission")]
    [InlineData(false, "inactive")]
    [InlineData(true, "inactive")]
    [InlineData(false, "unapproved")]
    [InlineData(true, "unapproved")]
    public async Task CaseConfirmations_RevalidateCachedAccessBeforeRepeatedConfirmation(bool hearing, string change)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedConfirmationCaseAsync(db);
        var controller = CreateController(db, user);
        ActionResult first = hearing
            ? (await controller.ConfirmNextHearing(clientCase.Id, new(clientCase.NextHearingAt ?? new DateTime(2099, 11, 3, 12, 0, 0, DateTimeKind.Utc)), CancellationToken.None)).Result!
            : (await controller.ReviewCasePosition(clientCase.Id, new(clientCase.Strategy), CancellationToken.None)).Result!;
        Assert.IsType<OkObjectResult>(first);
        var before = await db.ClientCases.AsNoTracking().SingleAsync();
        if (change == "permission")
            await db.RolePermissions.Where(row => row.RoleId == user.RoleId && row.Section == RolePermissionCatalog.Cases)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false));
        else if (change == "inactive")
            await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        else
            await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        ActionResult repeated = hearing
            ? (await controller.ConfirmNextHearing(clientCase.Id, new(clientCase.NextHearingAt ?? new DateTime(2099, 11, 3, 12, 0, 0, DateTimeKind.Utc)), CancellationToken.None)).Result!
            : (await controller.ReviewCasePosition(clientCase.Id, new(clientCase.Strategy), CancellationToken.None)).Result!;
        if (change == "permission") Assert.IsType<ForbidResult>(repeated);
        else Assert.IsType<UnauthorizedResult>(repeated);
        var after = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Equal(before.EventHistory, after.EventHistory);
        Assert.Equal(before.PositionReviewedAt, after.PositionReviewedAt);
        Assert.Equal(before.NextHearingConfirmedAt, after.NextHearingConfirmedAt);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseConfirmations_RejectCallerTransactionAndPendingChangesWithoutDiscardingThem(bool hearing, bool ambient)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedConfirmationCaseAsync(db);
        await using var transaction = ambient ? await db.Database.BeginTransactionAsync() : null;
        var pending = new Client { Name = "Несохранённый клиент", IsActive = true };
        if (!ambient) db.Clients.Add(pending);
        var controller = CreateController(db, user);
        ActionResult result = hearing
            ? (await controller.ConfirmNextHearing(clientCase.Id, new(clientCase.NextHearingAt ?? new DateTime(2099, 11, 3, 12, 0, 0, DateTimeKind.Utc)), CancellationToken.None)).Result!
            : (await controller.ReviewCasePosition(clientCase.Id, new(clientCase.Strategy), CancellationToken.None)).Result!;
        Assert.IsType<ConflictObjectResult>(result);
        if (ambient) Assert.Same(transaction, db.Database.CurrentTransaction);
        else Assert.Equal(EntityState.Added, db.Entry(pending).State);
        Assert.Single(await db.Clients.AsNoTracking().ToArrayAsync());
        var actual = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Null(actual.PositionReviewedAt);
        Assert.Null(actual.NextHearingConfirmedAt);
        Assert.Empty(actual.EventHistory);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseConfirmations_SaveFailureOrCancellationRollsBackMarkAndHistory(bool hearing, bool cancel)
    {
        var failure = new ConfirmationAfterSaveFailure(cancel);
        await using var db = await CreateContextAsync(failure);
        var (user, clientCase) = await SeedConfirmationCaseAsync(db);
        failure.Armed = true;
        var controller = CreateController(db, user);
        using var cancellation = new CancellationTokenSource();
        failure.Cancel = cancellation.Cancel;
        Task Confirm() => hearing
            ? controller.ConfirmNextHearing(clientCase.Id, new(clientCase.NextHearingAt ?? new DateTime(2099, 11, 3, 12, 0, 0, DateTimeKind.Utc)), cancellation.Token)
            : controller.ReviewCasePosition(clientCase.Id, new(clientCase.Strategy), cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Confirm);
        else await Assert.ThrowsAsync<ConfirmationSaveFailureException>(Confirm);
        Assert.True(failure.Called);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Null(actual.PositionReviewedAt);
        Assert.Null(actual.NextHearingConfirmedAt);
        Assert.Empty(actual.EventHistory);
        Assert.Equal(123.45m, actual.CourtClaimAmount);
        Assert.Empty(await db.Payments.ToArrayAsync());
        Assert.Null(db.Database.CurrentTransaction);
    }

    private static async Task<(User User, ClientCase Case)> SeedConfirmationCaseAsync(PaymentContext db)
    {
        var user = await CreateUserAsync(db, "confirmation@example.local", "Юрист", DataVisibilityScope.All);
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Клиент", IsActive = true, ResponsibleUserId = user.Id },
            ResponsibleUserId = user.Id,
            Title = "Дело",
            Status = ClientCaseStatus.Open,
            Strategy = "Исходная позиция",
            NextHearingAt = new DateTime(2099, 11, 3, 12, 0, 0, DateTimeKind.Utc),
            CourtClaimAmount = 123.45m,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        return (user, clientCase);
    }

    private sealed class ConfirmationSaveFailureException : Exception;

    private sealed class ConfirmationAfterSaveFailure(bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public Action Cancel { get; set; } = () => { };

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return ValueTask.FromResult(result);
            Called = true;
            if (cancel) { Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
            throw new ConfirmationSaveFailureException();
        }
    }

    private sealed class ConfirmationBeforeSave(Func<CancellationToken, Task> action) : SaveChangesInterceptor
    {
        public bool Called { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Called && eventData.Context!.ChangeTracker.Entries<ClientCase>().Any(entry => entry.State == EntityState.Modified))
            {
                Called = true;
                await action(cancellationToken);
            }
            return result;
        }
    }
}
