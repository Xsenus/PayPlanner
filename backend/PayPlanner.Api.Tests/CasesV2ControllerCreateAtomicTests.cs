using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Controllers.V1;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static Task<IActionResult> CreateAtomicCaseAsync(PaymentContext db, User actor, ClientCase input, int route, BulkPublicationBoundary publisher, CancellationToken ct = default)
    {
        var controller = CreateController(db, actor, realtimePublisher: publisher);
        return route switch
        {
            0 => new CasesController(db, publisher) { ControllerContext = controller.ControllerContext }.Create(input, ct),
            1 => new CasesV1Controller(db) { ControllerContext = controller.ControllerContext }.Create(input, ct),
            _ => controller.Create(input, ct),
        };
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CaseCreateAtomic_RejectsAClientSuppliedIdentity(int route)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); var owner = await db.Clients.Select(row => row.Id).SingleAsync();
        var input = new ClientCase { Id = 987654, ClientId = owner, Title = "Не назначать чужой ID" }; var publisher = new BulkPublicationBoundary(db);
        var result = await CreateAtomicCaseAsync(db, actor, input, route, publisher); var stored = await db.ClientCases.AsNoTracking().AnyAsync(row => row.Id == input.Id);
        Assert.True(result is BadRequestObjectResult && !stored, $"result={result.GetType().Name}; explicitIdStored={stored}"); Assert.Equal(0, publisher.Count); Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CaseCreateAtomic_RejectsAnUnapprovedActorEvenWithAStalePrincipal(int route)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        var input = new ClientCase { ClientId = await db.Clients.Select(row => row.Id).SingleAsync(), Title = "Нет действующего допуска" }; var publisher = new BulkPublicationBoundary(db);
        var result = await CreateAtomicCaseAsync(db, actor, input, route, publisher); var stored = await db.ClientCases.CountAsync();
        Assert.True(result is UnauthorizedResult or ForbidResult && stored == 2, $"result={result.GetType().Name}; storedCases={stored}"); Assert.Equal(0, publisher.Count); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task CaseCreateAtomic_PreservesForeignTransactionAndPendingWork(int route, bool pending)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); var input = new ClientCase { ClientId = await db.Clients.Select(row => row.Id).SingleAsync(), Title = "Не сохранять чужую работу" };
        await using var transaction = pending ? null : await db.Database.BeginTransactionAsync(); var task = await db.TaskJournalItems.SingleAsync(row => row.Status == "done"); if (pending) task.Result = "Чужой несохранённый результат";
        // The legacy publisher is not inspected until after the result; a foreign
        // transaction must be refused before any publication can be attempted.
        var publisher = new BulkPublicationBoundary(db); IActionResult? result = null; Exception? error = null;
        try { result = await CreateAtomicCaseAsync(db, actor, input, route, publisher); } catch (Exception caught) { error = caught; }
        Assert.True(result is ConflictObjectResult, $"result={result?.GetType().Name}; error={error?.GetType().Name}; storedCases={await db.ClientCases.CountAsync()}"); Assert.Same(transaction, db.Database.CurrentTransaction); Assert.Equal(0, publisher.Count);
        if (pending) { Assert.Equal(EntityState.Modified, db.Entry(task).State); Assert.Equal("Сохранённый результат", (await db.Entry(task).GetDatabaseValuesAsync())![nameof(TaskJournalItem.Result)]); }
        await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CaseCreateAtomic_RejectsADisabledResponsible(int route)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); var responsible = await CreateUserAsync(db, "create-disabled@example.local", "Отключённый"); await db.Users.Where(row => row.Id == responsible.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        var input = new ClientCase { ClientId = await db.Clients.Select(row => row.Id).SingleAsync(), ResponsibleUserId = responsible.Id, Title = "Не назначать отключённому" }; var publisher = new BulkPublicationBoundary(db);
        var result = await CreateAtomicCaseAsync(db, actor, input, route, publisher); var count = await db.ClientCases.CountAsync();
        Assert.True(result is BadRequestObjectResult && count == 2, $"result={result.GetType().Name}; storedCases={count}; disabledResponsible={responsible.Id}"); Assert.Equal(0, publisher.Count); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    public static IEnumerable<object[]> CreateAtomicFailureCases() => from route in new[] { 0, 1, 2 } from postgres in new[] { false, true } from cancel in new[] { false, true } select new object[] { route, postgres, cancel };
    [Theory]
    [MemberData(nameof(CreateAtomicFailureCases))]
    public async Task CaseCreateAtomic_RollsBackAnInsertedCaseAfterSqlFailureOrCancellation(int route, bool postgres, bool cancel)
    {
        var fault = new ConfirmationAfterSaveFailure(cancel); await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options) : await CreateContextAsync(fault);
        var (actor, ids) = await SeedBulkCasesAsync(db); var input = new ClientCase { ClientId = await db.Clients.Select(row => row.Id).SingleAsync(), Title = "Не сохранять частично" }; var publisher = new BulkPublicationBoundary(db);
        using var cancellation = new CancellationTokenSource(); fault.Cancel = cancellation.Cancel; fault.Armed = true;
        Task Write() => CreateAtomicCaseAsync(db, actor, input, route, publisher, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<ConfirmationSaveFailureException>(Write);
        Assert.True(fault.Called); Assert.Null(db.Database.CurrentTransaction); Assert.Equal(0, publisher.Count); db.ChangeTracker.Clear();
        var count = await db.ClientCases.CountAsync(); Assert.True(count == 2, $"storedCasesAfterFailure={count}"); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task CaseCreateAtomic_RollsBackTheCaseAndDeadlineTasksTogether(int route, bool postgres)
    {
        var fault = new CreateDeadlineAfterSaveFailure(); await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options) : await CreateContextAsync(fault);
        var (actor, ids) = await SeedBulkCasesAsync(db); var input = new ClientCase { ClientId = await db.Clients.Select(row => row.Id).SingleAsync(), Title = "Дело и срок атомарны", ResponsibleUserId = actor.Id, CourtName = "Суд", NextHearingAt = DateTime.UtcNow.AddDays(5) }; var publisher = new BulkPublicationBoundary(db); fault.Armed = true;
        await Assert.ThrowsAsync<ConfirmationSaveFailureException>(() => CreateAtomicCaseAsync(db, actor, input, route, publisher));
        Assert.True(fault.TaskInsertObserved); Assert.Null(db.Database.CurrentTransaction); Assert.Equal(0, publisher.Count); db.ChangeTracker.Clear();
        Assert.True(await db.ClientCases.CountAsync() == 2 && await db.TaskJournalItems.CountAsync() == 3, $"storedCases={await db.ClientCases.CountAsync()}; tasks={await db.TaskJournalItems.CountAsync()}"); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    private sealed class CreateDeadlineAfterSaveFailure : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool TaskInsertObserved { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<TaskJournalItem>().Any(entry => entry.State == EntityState.Added)) TaskInsertObserved = true;
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (TaskInsertObserved) throw new ConfirmationSaveFailureException();
            return ValueTask.FromResult(result);
        }
    }
}
