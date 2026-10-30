using System.Security.Claims;
using System.Text.Json;
using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class BalanceApplicationIdempotencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedAdvance_RetryPreservesSourceAccountingAndCannotSpendItAgain(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var source = PaymentFor(fixture);
        db.Payments.Add(source);
        await db.SaveChangesAsync();
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = source.ClientId,
            LegalEntityId = source.LegalEntityId,
            PaymentId = source.Id,
            Amount = 30m,
            Direction = ClientBalanceDirection.Credit,
            Type = ClientBalanceTransactionType.UnallocatedAdvance,
            Description = "Synthetic selected source",
        });
        await db.SaveChangesAsync();
        var request = Request(fixture); request.SourcePaymentId = source.Id;
        Assert.Equal(25m, Result(await ApplyAsync(db, fixture, request)).AppliedAmount);
        Assert.True(Result(await ApplyAsync(db, fixture, request)).IsReplay);
        var next = Request(fixture); next.SourcePaymentId = source.Id;
        Assert.Equal(5m, Result(await ApplyAsync(db, fixture, next)).AppliedAmount);
        Assert.IsType<BadRequestObjectResult>(await ApplyAsync(db, fixture, RequestWithSource()));
        Assert.Equal(30m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId)).PaidAmount);
        Assert.Equal(2, await db.BalanceApplicationReceipts.CountAsync());
        Assert.All(await db.ClientBalanceTransactions.Where(t => t.Type == ClientBalanceTransactionType.CreditApplied).ToArrayAsync(),
            row => Assert.StartsWith(PayPlanner.Api.Services.ClientBalanceService.BuildUnallocatedAdvanceApplicationKeyPrefix(source.Id), row.ExternalKey));

        ApplyClientBalanceRequest RequestWithSource()
        {
            var result = Request(fixture); result.SourcePaymentId = source.Id; return result;
        }
    }

    [Fact]
    public async Task AnotherActor_CannotReuseTheSameOperationIdentity()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        var firstActor = await db.Users.SingleAsync(user => user.Id == fixture.ActorId);
        var secondActor = new User { Email = "balance-second@test.local", FullName = "Another synthetic actor", PasswordHash = "unused", RoleId = firstActor.RoleId, IsActive = true, IsApproved = true, IsEmployee = true };
        db.Users.Add(secondActor);
        await db.SaveChangesAsync();
        var request = JsonSerializer.Deserialize<ApplyClientBalanceRequest>(JsonSerializer.Serialize(new { fixture.PaymentId, Amount = 25m, RequestId = Guid.NewGuid() }))!;
        Assert.IsType<OkObjectResult>(await ApplyAsync(db, fixture, request));
        db.ChangeTracker.Clear();
        Assert.IsType<NotFoundResult>(await ApplyAsync(db, fixture with { ActorId = secondActor.Id }, request));
        Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostResponse_SameRequestDoesNotApplyTwice(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var key = Guid.NewGuid();
        var request = JsonSerializer.Deserialize<ApplyClientBalanceRequest>(
            JsonSerializer.Serialize(new { fixture.PaymentId, Amount = 25m, RequestId = key }))!;
        Assert.IsType<OkObjectResult>(await ApplyAsync(db, fixture, request));
        db.ChangeTracker.Clear();
        Assert.IsType<OkObjectResult>(await ApplyAsync(db, fixture, request));
        Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Single(await db.ClientBalanceTransactions.AsNoTracking().Where(t => t.Type == ClientBalanceTransactionType.CreditApplied).ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replay_PreservesOriginalOutcomeAfterOtherApplications(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var request = Request(fixture);
        var first = Result(await ApplyAsync(db, fixture, request));
        Assert.False(first.IsReplay);
        Assert.Equal(75m, first.Balance);
        Assert.Equal(25m, first.AppliedAmount);
        Result(await ApplyAsync(db, fixture, Request(fixture)));
        var replay = Result(await ApplyAsync(db, fixture, request));
        Assert.True(replay.IsReplay);
        Assert.Equal(first.RequestId, replay.RequestId);
        Assert.Equal(first.AppliedAmount, replay.AppliedAmount);
        Assert.Equal(first.Balance, replay.Balance);
        Assert.Equal(50m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Equal(2, await db.BalanceApplicationReceipts.CountAsync());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, "amount")]
    [InlineData(true, "amount")]
    [InlineData(false, "description")]
    [InlineData(true, "description")]
    [InlineData(false, "source")]
    [InlineData(true, "source")]
    [InlineData(false, "payment")]
    [InlineData(true, "payment")]
    public async Task ChangedCommand_ConflictsWithoutAnotherLedgerEntry(bool legalEntity, string change)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var request = Request(fixture);
        Result(await ApplyAsync(db, fixture, request));
        if (change == "amount") request.Amount = 26m;
        if (change == "description") request.Description = "Another command";
        if (change == "source") request.SourcePaymentId = fixture.PaymentId;
        if (change == "payment")
        {
            var second = PaymentFor(fixture);
            db.Payments.Add(second); await db.SaveChangesAsync(); request.PaymentId = second.Id;
        }
        Assert.IsType<ConflictObjectResult>(await ApplyAsync(db, fixture, request));
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
        Assert.Single(await db.ClientBalanceTransactions.Where(t => t.Type == ClientBalanceTransactionType.CreditApplied).ToArrayAsync());
        Assert.Equal(25m, await db.Payments.SumAsync(p => p.PaidAmount));
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EquivalentDecimalAndTrimmedDescription_ReplayTheSameCommand(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var request = Request(fixture); request.Description = "  Synthetic credit  ";
        Result(await ApplyAsync(db, fixture, request));
        request.Amount = 25.0000m; request.Description = "Synthetic credit";
        Assert.True(Result(await ApplyAsync(db, fixture, request)).IsReplay);
        Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
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
    public async Task Replay_RechecksCurrentAccess(bool legalEntity, string revoked)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var request = Request(fixture); Result(await ApplyAsync(db, fixture, request));
        var user = await db.Users.SingleAsync(u => u.Id == fixture.ActorId);
        var permission = await db.RolePermissions.SingleAsync(p => p.RoleId == user.RoleId && p.Section == "payments");
        if (revoked == "edit") permission.CanEdit = false;
        if (revoked == "scope") permission.ViewScope = "own";
        if (revoked == "active") user.IsActive = false;
        if (revoked == "approved") user.IsApproved = false;
        await db.SaveChangesAsync();
        var denied = await ApplyAsync(db, fixture, request);
        if (revoked is "edit" or "scope") Assert.IsType<ForbidResult>(denied); else Assert.IsType<UnauthorizedResult>(denied);
        Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyRequest_KeepsItsExistingBehaviorWithoutReceipts(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var request = Request(fixture); request.RequestId = null;
        Assert.IsType<OkObjectResult>(await ApplyAsync(db, fixture, request));
        Assert.IsType<OkObjectResult>(await ApplyAsync(db, fixture, request));
        Assert.Equal(50m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData("key")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("payment")]
    [InlineData("source")]
    [InlineData("description")]
    public async Task InvalidManagedCommand_DoesNotWrite(string invalid)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false); var request = Request(fixture);
        if (invalid == "key") request.RequestId = Guid.Empty;
        if (invalid == "zero") request.Amount = 0;
        if (invalid == "negative") request.Amount = -1;
        if (invalid == "payment") request.PaymentId = 0;
        if (invalid == "source") request.SourcePaymentId = 0;
        if (invalid == "description") request.Description = new string('a', 501);
        Assert.IsType<BadRequestObjectResult>(await ApplyAsync(db, fixture, request));
        Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
        Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
    }

    [Fact]
    public async Task DirtyAndExternalContext_AreNotSavedClearedOrCommitted()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        var client = await db.Clients.SingleAsync(); client.Name = "Unsaved caller edit";
        Assert.IsType<ConflictObjectResult>(await ApplyAsync(db, fixture, Request(fixture)));
        Assert.Equal(EntityState.Modified, db.Entry(client).State);
        Assert.NotEqual(client.Name, (await db.Clients.AsNoTracking().SingleAsync()).Name);
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.IsType<ConflictObjectResult>(await ApplyAsync(db, fixture, Request(fixture)));
        Assert.Same(transaction, db.Database.CurrentTransaction);
        await transaction.RollbackAsync();
        Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Fact]
    public async Task ReceiptSaveFailure_RollsBackTheMoneyWriteToo()
    {
        var failure = new FailReceiptInsert();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: failure);
        var fixture = await SeedAsync(db, false); failure.Armed = true;
        await Assert.ThrowsAsync<DbUpdateException>(() => ApplyAsync(db, fixture, Request(fixture)));
        Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
        Assert.Single(await db.ClientBalanceTransactions.ToArrayAsync());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task LostCommitAcknowledgement_ReplaysTheCommittedOutcome()
    {
        var fault = new LoseCommitAcknowledgement();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: fault);
        var fixture = await SeedAsync(db, false); var request = Request(fixture); fault.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => ApplyAsync(db, fixture, request));
        Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.True(Result(await ApplyAsync(db, fixture, request)).IsReplay);
        Assert.Single(await db.ClientBalanceTransactions.Where(t => t.Type == ClientBalanceTransactionType.CreditApplied).ToArrayAsync());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateReceipt_DoesNotChangeBusinessHistoryOrAddForeignKeys(bool audit)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(useObjectAuditModel: audit);
        var before = audit ? await db.ObjectAuditEvents.CountAsync() : 0;
        var receipt = new BalanceApplicationReceipt { RequestId = Guid.NewGuid(), ActorUserId = 77, ClientId = 88, PaymentId = 99, CommandSha256 = new string('a', 64), AppliedAmount = 1m, Balance = 2m, CreatedAtUtc = DateTime.UtcNow };
        db.BalanceApplicationReceipts.Add(receipt); await db.SaveChangesAsync();
        if (audit) Assert.Equal(before, await db.ObjectAuditEvents.CountAsync());
        var entity = db.Model.FindEntityType(typeof(BalanceApplicationReceipt))!;
        Assert.Empty(entity.GetForeignKeys()); Assert.Null(entity.FindProperty("IsDeleted"));
        Assert.Equal("RequestId", Assert.Single(entity.FindPrimaryKey()!.Properties).Name);
        Assert.Null(entity.FindProperty("UpdatedByUserId"));
    }

    internal static ApplyClientBalanceRequest Request(Fixture fixture)
        => new() { RequestId = Guid.NewGuid(), PaymentId = fixture.PaymentId, Amount = 25m };
    internal static BalanceApplicationResultDto Result(IActionResult result)
        => Assert.IsType<BalanceApplicationResultDto>(Assert.IsType<OkObjectResult>(result).Value);
    internal static Payment PaymentFor(Fixture fixture)
        => new() { ClientId = fixture.LegalEntity ? null : fixture.OwnerId, LegalEntityId = fixture.LegalEntity ? fixture.OwnerId : null, Amount = 100m, Date = new DateTime(2026, 10, 31), PlannedDate = new DateTime(2026, 10, 31), Description = "Another synthetic target", Type = PaymentType.Income, Status = PaymentStatus.Pending, DocumentKind = DocumentKind.Payment, Notes = "", SystemNotes = "" };

    private sealed class FailReceiptInsert : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (Armed && command.CommandText.Contains("INSERT INTO \"BalanceApplicationReceipts\"", StringComparison.Ordinal)) throw new IOException("Synthetic receipt insert failure");
            return base.ReaderExecutingAsync(command, eventData, result, ct);
        }
    }
    private sealed class LoseCommitAcknowledgement : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            if (Armed) { Armed = false; throw new IOException("Synthetic lost acknowledgement"); }
            return Task.CompletedTask;
        }
    }

    internal sealed record Fixture(int ActorId, bool LegalEntity, int OwnerId, int PaymentId);

    internal static async Task<Fixture> SeedAsync(PaymentContext db, bool legalEntity)
    {
        var role = new Role { Name = "balance-idempotency", Description = "Synthetic test role" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.RolePermissions.AddRange(new[] { "clients", "legalEntities", "payments" }.Select(section =>
            new RolePermission { RoleId = role.Id, Section = section, CanView = true, CanEdit = true, CanCreate = true, ViewScope = "all" }));
        var user = new User { Email = "balance-idempotency@test.local", FullName = "Synthetic balance actor", PasswordHash = "unused", RoleId = role.Id, IsActive = true, IsApproved = true, IsEmployee = true };
        db.Users.Add(user);
        var client = new Client { Name = "Synthetic balance client" };
        var company = new LegalEntity { ShortName = "Synthetic balance company", FullName = "Synthetic balance company" };
        if (legalEntity) db.LegalEntities.Add(company); else db.Clients.Add(client);
        await db.SaveChangesAsync();
        var payment = new Payment { ClientId = legalEntity ? null : client.Id, LegalEntityId = legalEntity ? company.Id : null, Amount = 100m, PaidAmount = 0m, Date = new DateTime(2026, 10, 31), PlannedDate = new DateTime(2026, 10, 31), Description = "Synthetic target", Type = PaymentType.Income, Status = PaymentStatus.Pending, DocumentKind = DocumentKind.Payment, Notes = "", SystemNotes = "" };
        db.Payments.Add(payment);
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction { ClientId = payment.ClientId, LegalEntityId = payment.LegalEntityId, Amount = 100m, Direction = ClientBalanceDirection.Credit, Type = ClientBalanceTransactionType.ManualAdjustment, Description = "Synthetic credit" });
        await db.SaveChangesAsync();
        return new(user.Id, legalEntity, legalEntity ? company.Id : client.Id, payment.Id);
    }

    internal static async Task<IActionResult> ApplyAsync(PaymentContext db, Fixture fixture, ApplyClientBalanceRequest request)
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, fixture.ActorId.ToString())], "synthetic")) };
        if (fixture.LegalEntity)
        {
            var controller = new LegalEntityBalancesController(db) { ControllerContext = new ControllerContext { HttpContext = context } };
            return await controller.ApplyCredit(fixture.OwnerId, request, CancellationToken.None);
        }
        else
        {
            var controller = new ClientBalancesController(db) { ControllerContext = new ControllerContext { HttpContext = context } };
            return await controller.ApplyCredit(fixture.OwnerId, request, CancellationToken.None);
        }
    }
}
