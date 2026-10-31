using System.Data.Common;
using System.Security.Claims;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class BalanceApplicationPaymentWriterTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NativeApplyHoldingPayment_OrdinaryWriterConflictsWithoutLosingCredit(bool legalEntity, bool status)
    {
        await using var database = await template.CreateDatabaseAsync();
        Fixture fixture;
        await using (var seed = Open(database.ConnectionString)) fixture = await SeedAsync(seed, legalEntity);
        var barrier = new HoldSave();
        await using var applier = Open(database.ConnectionString, barrier);
        await using var editor = Open(database.ConnectionString);
        var pending = ApplyAsync(applier, fixture, Request(fixture));
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        IActionResult attempted;
        try
        {
            var controller = Controller(editor, fixture);
            attempted = await (status
                ? controller.UpdateStatus(fixture.PaymentId, new UpdatePaymentStatusRequest { Status = "Cancelled", Reason = "Synthetic race" }, CancellationToken.None)
                : controller.Update(fixture.PaymentId, PaymentFor(fixture), CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<ConflictObjectResult>(attempted);
        Result(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        var retry = status
            ? await Controller(editor, fixture).UpdateStatus(fixture.PaymentId, new UpdatePaymentStatusRequest { Status = "Cancelled", Reason = "Synthetic race retry" }, CancellationToken.None)
            : await Controller(editor, fixture).Update(fixture.PaymentId, PaymentFor(fixture), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(retry);
        await using var fresh = Open(database.ConnectionString);
        var payment = await fresh.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(25m, payment.PaidAmount);
        Assert.Contains("Зачтено с баланса контрагента", payment.SystemNotes);
        Assert.Single(await fresh.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeOrdinaryUpdate_RacingWithApply_PreservesTheAppliedCreditAndItsNote(bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync();
        Fixture fixture;
        await using (var seed = Open(database.ConnectionString)) fixture = await SeedAsync(seed, legalEntity);
        var barrier = new HoldAppliedCreditRead();
        await using var editor = Open(database.ConnectionString, barrier);
        await using var applier = Open(database.ConnectionString);
        var model = PaymentFor(fixture); model.Id = fixture.PaymentId; model.Description = "Synthetic ordinary edit"; model.PaidAmount = 10m;
        var pending = Controller(editor, fixture).Update(fixture.PaymentId, model, CancellationToken.None);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        IActionResult raced;
        var request = Request(fixture);
        try { raced = await ApplyAsync(applier, fixture, request).WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { barrier.Release.TrySetResult(); }
        var edited = await pending.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(edited is OkObjectResult or ConflictObjectResult or BadRequestObjectResult, $"Unexpected editor result: {edited.GetType().Name}");
        Assert.True(raced is OkObjectResult or ConflictObjectResult, $"Unexpected Apply result: {raced.GetType().Name}");
        await using var fresh = Open(database.ConnectionString);
        var payment = await fresh.Payments.AsNoTracking().SingleAsync();
        var debit = await ClientBalanceService.GetAppliedCreditAmountAsync(fresh, fixture.PaymentId);
        Assert.True(payment.PaidAmount >= debit, $"Ordinary edit lost committed credit: paid={payment.PaidAmount}, debit={debit}");
        if (raced is ConflictObjectResult) Assert.False(Result(await ApplyAsync(fresh, fixture, request)).IsReplay);
        payment = await fresh.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(raced is ConflictObjectResult ? 35m : 25m, payment.PaidAmount);
        Assert.Contains("Зачтено с баланса контрагента", payment.SystemNotes);
        Assert.Single(await fresh.BalanceApplicationReceipts.ToArrayAsync());
        Assert.Single(await fresh.ClientBalanceTransactions.Where(row => row.Type == ClientBalanceTransactionType.CreditApplied).ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeStatusCancellation_CannotEraseAnAppliedCredit(bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync();
        await using var seed = Open(database.ConnectionString);
        var fixture = await SeedAsync(seed, legalEntity);
        var applied = Result(await ApplyAsync(seed, fixture, Request(fixture)));
        var previousStatus = (await seed.Payments.AsNoTracking().SingleAsync()).Status;
        await using var editor = Open(database.ConnectionString);
        var result = await Controller(editor, fixture).UpdateStatus(fixture.PaymentId,
            new UpdatePaymentStatusRequest { Status = "Cancelled", Reason = "Synthetic cancellation boundary" }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
        await using var fresh = Open(database.ConnectionString);
        var payment = await fresh.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(applied.AppliedAmount, payment.PaidAmount);
        Assert.Equal(previousStatus, payment.Status);
        Assert.Equal(25m, await ClientBalanceService.GetAppliedCreditAmountAsync(fresh, fixture.PaymentId));
        Assert.Single(await fresh.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeStatusChange_ViewWithoutEdit_CannotModifyThePayment(bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync();
        Fixture fixture;
        await using (var seed = Open(database.ConnectionString))
        {
            fixture = await SeedAsync(seed, legalEntity);
            var permission = await seed.RolePermissions.SingleAsync(row => row.Section == "payments");
            permission.CanEdit = false; await seed.SaveChangesAsync();
        }
        await using var editor = Open(database.ConnectionString);
        var result = await Controller(editor, fixture).UpdateStatus(fixture.PaymentId,
            new UpdatePaymentStatusRequest { Status = "Completed", Reason = "Synthetic permission boundary" }, CancellationToken.None);
        Assert.IsType<ForbidResult>(result);
        await using var fresh = Open(database.ConnectionString);
        var payment = await fresh.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(0m, payment.PaidAmount); Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Empty(await fresh.BalanceApplicationReceipts.ToArrayAsync());
    }

    internal static PaymentsController Controller(PaymentContext db, Fixture fixture, INotificationService? notifications = null)
        => new(db, new NoopRequirements(), notifications)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, fixture.ActorId.ToString())], "synthetic")) }
            }
        };

    private static PostgreSqlPaymentContext Open(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options);
    }

    private sealed class NoopRequirements : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }

    private sealed class HoldAppliedCreditRead : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("ClientBalanceTransactions", StringComparison.Ordinal)
                && command.CommandText.Contains("sum", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("PaymentId", StringComparison.Ordinal)
                && Interlocked.Increment(ref arrivals) == 1)
            {
                Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            return result;
        }
    }

    private sealed class HoldSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            return result;
        }
    }
}

public sealed class BalanceApplicationPaymentWriterSqliteTests
{
    [Theory]
    [InlineData(false, "amount")]
    [InlineData(true, "amount")]
    [InlineData(false, "owner")]
    [InlineData(true, "owner")]
    [InlineData(false, "type")]
    [InlineData(true, "type")]
    public async Task AppliedCredit_CannotBeMovedOrClampedAway(bool legalEntity, string change)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        Result(await ApplyAsync(db, fixture, Request(fixture)));
        var model = PaymentFor(fixture); model.PaidAmount = 25m;
        if (change == "amount") model.Amount = 10m;
        if (change == "owner") { model.ClientId = null; model.LegalEntityId = null; }
        if (change == "type") model.Type = PaymentType.Expense;
        var action = await BalanceApplicationPaymentWriterTests.Controller(db, fixture).Update(fixture.PaymentId, model, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(action);
        var payment = await db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(100m, payment.Amount); Assert.Equal(25m, payment.PaidAmount);
        Assert.Equal(PaymentType.Income, payment.Type);
        Assert.Equal(legalEntity ? null : (int?)fixture.OwnerId, payment.ClientId);
        Assert.Equal(legalEntity ? (int?)fixture.OwnerId : null, payment.LegalEntityId);
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryWrite_FailureAfterSave_RollsBackAllChanges(bool status)
    {
        var failure = new FailAfterSave();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: failure);
        var fixture = await SeedAsync(db, false);
        var model = PaymentFor(fixture); model.PaidAmount = 25m;
        var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture);
        failure.Enabled = true;
        await Assert.ThrowsAsync<SyntheticWriteFailure>(() => status
            ? controller.UpdateStatus(fixture.PaymentId, new UpdatePaymentStatusRequest { Status = "Completed" }, CancellationToken.None)
            : controller.Update(fixture.PaymentId, model, CancellationToken.None));
        Assert.Null(db.Database.CurrentTransaction);
        Assert.False(db.ChangeTracker.HasChanges());
        var payment = await db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(0m, payment.PaidAmount); Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Single(await db.ClientBalanceTransactions.ToArrayAsync());
        Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Notifications_RunAfterCommit_AndFailureIsNotAWriteConflict(bool status, bool failNotification)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        db.PaymentResponsibles.Add(new PaymentResponsible { PaymentId = fixture.PaymentId, UserId = fixture.ActorId });
        await db.SaveChangesAsync();
        var notifications = DispatchProxy.Create<INotificationService, NotificationProbe>();
        var probe = (NotificationProbe)(object)notifications;
        probe.OnCall = () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            if (failNotification) throw new SyntheticWriteFailure();
        };
        var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture, notifications);
        var model = PaymentFor(fixture); model.PaidAmount = 25m; model.Amount = 110m;
        Task<IActionResult> Write() => status
            ? controller.UpdateStatus(fixture.PaymentId, new UpdatePaymentStatusRequest { Status = "Completed" }, CancellationToken.None)
            : controller.Update(fixture.PaymentId, model, CancellationToken.None);
        if (failNotification) await Assert.ThrowsAsync<SyntheticWriteFailure>(Write); else Assert.IsType<OkObjectResult>(await Write());
        Assert.True(probe.Calls > 0);
        Assert.Equal(status ? 100m : 25m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, "Completed", true)]
    [InlineData(true, "Completed", true)]
    [InlineData(false, "Processing", true)]
    [InlineData(true, "Processing", true)]
    [InlineData(false, "Pending", true)]
    [InlineData(true, "Pending", true)]
    [InlineData(false, "Cancelled", false)]
    [InlineData(true, "Cancelled", false)]
    public async Task Status_WithAppliedCredit_RespectsTheFloor(bool legalEntity, string status, bool allowed)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        Result(await ApplyAsync(db, fixture, Request(fixture)));
        var action = await BalanceApplicationPaymentWriterTests.Controller(db, fixture).UpdateStatus(fixture.PaymentId,
            new UpdatePaymentStatusRequest { Status = status, Reason = "Synthetic status transition" }, CancellationToken.None);
        if (allowed) Assert.IsType<OkObjectResult>(action); else Assert.IsType<BadRequestObjectResult>(action);
        var payment = await db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(status == "Completed" ? 100m : 25m, payment.PaidAmount);
        Assert.Contains("Зачтено с баланса контрагента", payment.SystemNotes);
        Assert.Equal(25m, await ClientBalanceService.GetAppliedCreditAmountAsync(db, fixture.PaymentId));
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OrdinaryUpdate_RejectsStaleAmountButAllowsTheCurrentCredit(bool legalEntity, bool current)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        Result(await ApplyAsync(db, fixture, Request(fixture)));
        var model = PaymentFor(fixture); model.PaidAmount = current ? 25m : 10m;
        var action = await BalanceApplicationPaymentWriterTests.Controller(db, fixture).Update(fixture.PaymentId, model, CancellationToken.None);
        if (current) Assert.IsType<OkObjectResult>(action); else Assert.IsType<BadRequestObjectResult>(action);
        var payment = await db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(25m, payment.PaidAmount);
        Assert.Contains("Зачтено с баланса контрагента", payment.SystemNotes);
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullyPaidWithCredit_CannotResetToPending(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var request = Request(fixture); request.Amount = 100m;
        Result(await ApplyAsync(db, fixture, request));
        var action = await BalanceApplicationPaymentWriterTests.Controller(db, fixture).UpdateStatus(fixture.PaymentId,
            new UpdatePaymentStatusRequest { Status = "Pending", Reason = "Synthetic reset" }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(action);
        var payment = await db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(100m, payment.PaidAmount); Assert.Equal(PaymentStatus.Completed, payment.Status);
    }

    [Theory]
    [InlineData(false, "no-edit")]
    [InlineData(true, "no-edit")]
    [InlineData(false, "inactive")]
    [InlineData(true, "inactive")]
    [InlineData(false, "unapproved")]
    [InlineData(true, "unapproved")]
    [InlineData(false, "anonymous")]
    [InlineData(true, "anonymous")]
    [InlineData(false, "missing-actor")]
    [InlineData(true, "missing-actor")]
    public async Task OrdinaryWriter_RejectsUnqualifiedActor(bool status, string restriction)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        var actor = await db.Users.SingleAsync();
        if (restriction == "no-edit") (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanEdit = false;
        if (restriction == "inactive") actor.IsActive = false;
        if (restriction == "unapproved") actor.IsApproved = false;
        await db.SaveChangesAsync();
        var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture);
        if (restriction == "anonymous") controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        if (restriction == "missing-actor") controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "999999"), new Claim(ClaimTypes.Role, "admin")], "synthetic"));
        var action = status
            ? await controller.UpdateStatus(fixture.PaymentId, new UpdatePaymentStatusRequest { Status = "Completed" }, CancellationToken.None)
            : await controller.Update(fixture.PaymentId, PaymentFor(fixture), CancellationToken.None);
        if (restriction == "no-edit") Assert.IsType<ForbidResult>(action); else Assert.IsType<UnauthorizedResult>(action);
        var payment = await db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(0m, payment.PaidAmount); Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryWriter_DirtyContext_IsNotSavedOrDiscarded(bool status)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        var actor = await db.Users.SingleAsync(); actor.FullName = "Unsaved unrelated edit";
        var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture);
        var action = status
            ? await controller.UpdateStatus(fixture.PaymentId, new UpdatePaymentStatusRequest { Status = "Completed" }, CancellationToken.None)
            : await controller.Update(fixture.PaymentId, PaymentFor(fixture), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(action);
        Assert.True(db.ChangeTracker.HasChanges()); Assert.Equal("Unsaved unrelated edit", actor.FullName);
        Assert.NotEqual(actor.FullName, (await db.Users.AsNoTracking().SingleAsync()).FullName);
        Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryWriter_ExternalTransaction_IsNotCommittedOrDisposed(bool status)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture);
        var action = status
            ? await controller.UpdateStatus(fixture.PaymentId, new UpdatePaymentStatusRequest { Status = "Completed" }, CancellationToken.None)
            : await controller.Update(fixture.PaymentId, PaymentFor(fixture), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(action);
        Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        await transaction.RollbackAsync();
    }

    private sealed class SyntheticWriteFailure : Exception;
    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
            => Enabled ? throw new SyntheticWriteFailure() : ValueTask.FromResult(result);
    }

    public class NotificationProbe : DispatchProxy
    {
        public Action? OnCall { get; set; }
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls++; OnCall?.Invoke(); return Task.CompletedTask;
        }
    }
}
