using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class BalanceApplicationCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelBeforeArrival_FencesEveryLateApplyWithoutTouchingMoney(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity); var request = Request(fixture);
        var history = await db.ObjectAuditEvents.CountAsync();
        var status = CancelResult(await CancelAsync(db, fixture, request.RequestId!.Value));
        Assert.Equal("Cancelled", status.State); Assert.Null(status.Result);
        Assert.Equal("Cancelled", CancelResult(await CancelAsync(db, fixture, request.RequestId.Value)).State);
        Assert.IsType<ConflictObjectResult>(await ApplyAsync(db, fixture, request));
        Assert.Equal("Cancelled", BalanceApplicationInspectionTests.InspectResult(await BalanceApplicationInspectionTests.InspectAsync(db, fixture, request.RequestId.Value)).State);
        Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Single(await db.ClientBalanceTransactions.ToArrayAsync());
        var receipt = Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
        Assert.Equal(BalanceApplicationState.Cancelled, receipt.State); Assert.Null(receipt.CommandSha256);
        Assert.Equal(0m, receipt.AppliedAmount); Assert.Equal(0m, receipt.Balance);
        Assert.Equal(history, await db.ObjectAuditEvents.CountAsync());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelAfterCommit_ReturnsOriginalOutcomeWithoutRefundOrHistoryRewrite(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity); var request = Request(fixture);
        var applied = Result(await ApplyAsync(db, fixture, request));
        Result(await ApplyAsync(db, fixture, Request(fixture)));
        var history = await db.ObjectAuditEvents.CountAsync();
        var status = CancelResult(await CancelAsync(db, fixture, request.RequestId!.Value));
        Assert.Equal("Committed", status.State); Assert.NotNull(status.Result); Assert.True(status.Result.IsReplay);
        Assert.Equal(applied.AppliedAmount, status.Result.AppliedAmount); Assert.Equal(applied.Balance, status.Result.Balance);
        Assert.Equal(50m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Equal(3, await db.ClientBalanceTransactions.CountAsync());
        Assert.All(await db.BalanceApplicationReceipts.ToArrayAsync(), row => Assert.Equal(BalanceApplicationState.Committed, row.State));
        Assert.Equal(history, await db.ObjectAuditEvents.CountAsync());
    }

    [Theory]
    [InlineData(false, "actor", false)]
    [InlineData(true, "actor", false)]
    [InlineData(false, "target", false)]
    [InlineData(true, "target", false)]
    [InlineData(false, "actor", true)]
    [InlineData(true, "actor", true)]
    [InlineData(false, "target", true)]
    [InlineData(true, "target", true)]
    public async Task Cancel_OtherActorOrTargetCannotRevealOrReplaceAnOutcome(bool legalEntity, string mismatch, bool committed)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity); var request = Request(fixture);
        if (committed) Result(await ApplyAsync(db, fixture, request)); else CancelResult(await CancelAsync(db, fixture, request.RequestId!.Value));
        if (mismatch == "actor")
        {
            var first = await db.Users.SingleAsync();
            var other = new User { Email = "cancel-other@test.local", FullName = "Other synthetic actor", PasswordHash = "unused", RoleId = first.RoleId, IsActive = true, IsApproved = true, IsEmployee = true };
            db.Users.Add(other); await db.SaveChangesAsync(); fixture = fixture with { ActorId = other.Id };
        }
        else
        {
            var other = PaymentFor(fixture); db.Payments.Add(other); await db.SaveChangesAsync(); fixture = fixture with { PaymentId = other.Id };
        }
        Assert.IsType<NotFoundResult>(await CancelAsync(db, fixture, request.RequestId!.Value));
        var receipt = Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
        Assert.Equal(committed ? BalanceApplicationState.Committed : BalanceApplicationState.Cancelled, receipt.State);
        Assert.Equal(committed ? 25m : 0m, await db.Payments.SumAsync(p => p.PaidAmount));
    }

    [Theory]
    [InlineData(false, "edit")]
    [InlineData(true, "edit")]
    [InlineData(false, "scope")]
    [InlineData(true, "scope")]
    [InlineData(false, "active")]
    [InlineData(true, "active")]
    [InlineData(false, "approved")]
    [InlineData(true, "approved")]
    public async Task Cancel_CurrentAccessIsRequiredBeforeAnyFenceIsWritten(bool legalEntity, string revoked)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var actor = await db.Users.SingleAsync();
        var permission = await db.RolePermissions.SingleAsync(p => p.RoleId == actor.RoleId && p.Section == "payments");
        if (revoked == "edit") permission.CanEdit = false;
        if (revoked == "scope") permission.ViewScope = "own";
        if (revoked == "active") actor.IsActive = false;
        if (revoked == "approved") actor.IsApproved = false;
        await db.SaveChangesAsync();
        var denied = await CancelAsync(db, fixture, Guid.NewGuid());
        if (revoked is "edit" or "scope") Assert.IsType<ForbidResult>(denied); else Assert.IsType<UnauthorizedResult>(denied);
        Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
        Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("payment")]
    [InlineData("actor")]
    public async Task Cancel_InvalidIdentityNeverWritesAFence(string invalid)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        if (invalid == "actor") fixture = fixture with { ActorId = 0 };
        if (invalid == "payment") fixture = fixture with { PaymentId = 0 };
        var denied = await CancelAsync(db, fixture, invalid == "key" ? Guid.Empty : Guid.NewGuid());
        if (invalid == "actor") Assert.IsType<UnauthorizedResult>(denied); else Assert.IsType<BadRequestResult>(denied);
        Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Fact]
    public async Task Cancel_DirtyOrExternalContextRemainsOwnedByTheCaller()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        var pending = new Client { Name = "Unrelated input" }; db.Clients.Add(pending);
        Assert.IsType<ConflictObjectResult>(await CancelAsync(db, fixture, Guid.NewGuid()));
        Assert.Equal(EntityState.Added, db.Entry(pending).State); db.Entry(pending).State = EntityState.Detached;
        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.IsType<ConflictObjectResult>(await CancelAsync(db, fixture, Guid.NewGuid()));
        Assert.Same(transaction, db.Database.CurrentTransaction); await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Cancel_ReceiptFailureDoesNotClaimThatTheOriginalWasFenced()
    {
        var failure = new FailReceipt();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: failure);
        var fixture = await SeedAsync(db, false); var request = Request(fixture);
        await Assert.ThrowsAsync<DbUpdateException>(() => CancelAsync(db, fixture, request.RequestId!.Value));
        Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
        failure.Armed = false;
        Assert.False(Result(await ApplyAsync(db, fixture, request)).IsReplay);
        Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
    }

    [Fact]
    public async Task Cancel_LostCommitAcknowledgementIsRecoveredAsCancelled()
    {
        var failure = new LoseAcknowledgement();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: failure);
        var fixture = await SeedAsync(db, false); var request = Request(fixture); failure.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => CancelAsync(db, fixture, request.RequestId!.Value));
        Assert.Equal("Cancelled", CancelResult(await CancelAsync(db, fixture, request.RequestId!.Value)).State);
        Assert.IsType<ConflictObjectResult>(await ApplyAsync(db, fixture, request));
        Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
    }

    [Theory]
    [InlineData("committed-null-hash")]
    [InlineData("committed-zero")]
    [InlineData("cancelled-hash")]
    [InlineData("cancelled-amount")]
    [InlineData("cancelled-balance")]
    [InlineData("unknown-state")]
    public async Task ReceiptConstraints_RejectAmbiguousTerminalOutcomes(string invalid)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var cancelled = invalid.StartsWith("cancelled", StringComparison.Ordinal);
        var row = new BalanceApplicationReceipt
        {
            RequestId = Guid.NewGuid(),
            ActorUserId = 11,
            ClientId = 22,
            PaymentId = 33,
            State = cancelled ? BalanceApplicationState.Cancelled : BalanceApplicationState.Committed,
            CommandSha256 = cancelled ? null : new string('a', 64),
            AppliedAmount = cancelled ? 0 : 25,
            CreatedAtUtc = DateTime.UtcNow
        };
        if (invalid == "committed-null-hash") row.CommandSha256 = null;
        if (invalid == "committed-zero") row.AppliedAmount = 0;
        if (invalid == "cancelled-hash") row.CommandSha256 = new string('a', 64);
        if (invalid == "cancelled-amount") row.AppliedAmount = 25;
        if (invalid == "cancelled-balance") row.Balance = 1;
        if (invalid == "unknown-state") row.State = (BalanceApplicationState)99;
        db.BalanceApplicationReceipts.Add(row);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    internal static BalanceApplicationInspectionDto CancelResult(IActionResult result)
        => Assert.IsType<BalanceApplicationInspectionDto>(Assert.IsType<OkObjectResult>(result).Value);

    internal static Task<IActionResult> CancelAsync(PaymentContext db, Fixture fixture, Guid key)
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, fixture.ActorId.ToString())], "synthetic")) };
        var controllerContext = new ControllerContext { HttpContext = context };
        var request = new CancelBalanceApplicationRequest { PaymentId = fixture.PaymentId };
        return fixture.LegalEntity
            ? new LegalEntityBalancesController(db) { ControllerContext = controllerContext }.CancelApplication(fixture.OwnerId, key, request, CancellationToken.None)
            : new ClientBalancesController(db) { ControllerContext = controllerContext }.CancelApplication(fixture.OwnerId, key, request, CancellationToken.None);
    }

    private sealed class FailReceipt : DbCommandInterceptor
    {
        public bool Armed { get; set; } = true;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (Armed && command.CommandText.Contains("INSERT INTO \"BalanceApplicationReceipts\"", StringComparison.Ordinal)) throw new IOException("Synthetic cancellation receipt failure");
            return base.ReaderExecutingAsync(command, eventData, result, ct);
        }
    }
    private sealed class LoseAcknowledgement : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            if (Armed) { Armed = false; throw new IOException("Synthetic cancellation acknowledgement loss"); }
            return Task.CompletedTask;
        }
    }
}
