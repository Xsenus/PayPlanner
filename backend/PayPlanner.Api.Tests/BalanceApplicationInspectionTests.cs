using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class BalanceApplicationInspectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inspect_ProductionNullOmissionKeepsAnExplicitResult(bool committed)
    {
        var key = Guid.NewGuid();
        var value = new BalanceApplicationInspectionDto
        {
            RequestId = key,
            PaymentId = 9,
            State = committed ? "Committed" : "Unconfirmed",
            Result = committed ? new BalanceApplicationResultDto { RequestId = key, AppliedAmount = 25m, Balance = 75m, IsReplay = true } : null,
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        var json = JsonSerializer.SerializeToElement(value, options);
        Assert.Equal(committed ? JsonValueKind.Object : JsonValueKind.Null, json.GetProperty("result").ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inspect_CommittedReturnsOriginalOutcomeWithoutChangingMoney(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var request = Request(fixture);
        var original = Result(await ApplyAsync(db, fixture, request));
        Result(await ApplyAsync(db, fixture, Request(fixture)));
        var historyCount = await db.ObjectAuditEvents.CountAsync();
        var status = InspectResult(await InspectAsync(db, fixture, request.RequestId!.Value));
        Assert.Equal("Committed", status.State); Assert.Equal(fixture.PaymentId, status.PaymentId);
        Assert.NotNull(status.Result); Assert.True(status.Result.IsReplay);
        Assert.Equal(original.RequestId, status.RequestId);
        Assert.Equal(original.AppliedAmount, status.Result.AppliedAmount); Assert.Equal(original.Balance, status.Result.Balance);
        Assert.Equal(50m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Equal(2, await db.BalanceApplicationReceipts.CountAsync());
        Assert.Equal(3, await db.ClientBalanceTransactions.CountAsync());
        Assert.Equal(historyCount, await db.ObjectAuditEvents.CountAsync());
        Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inspect_UnknownIsUnconfirmedAndDoesNotFenceALateOriginal(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var request = Request(fixture);
        var status = Assert.IsType<BalanceApplicationInspectionDto>(Assert.IsType<AcceptedResult>(await InspectAsync(db, fixture, request.RequestId!.Value)).Value);
        Assert.Equal("Unconfirmed", status.State); Assert.Null(status.Result);
        Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
        Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.False(Result(await ApplyAsync(db, fixture, request)).IsReplay);
        Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
    }

    [Theory]
    [InlineData(false, "actor")]
    [InlineData(true, "actor")]
    [InlineData(false, "target")]
    [InlineData(true, "target")]
    public async Task Inspect_OtherActorOrTargetDoesNotRevealTheReceipt(bool legalEntity, string mismatch)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity); var request = Request(fixture);
        Result(await ApplyAsync(db, fixture, request));
        if (mismatch == "actor")
        {
            var first = await db.Users.SingleAsync();
            var other = new User { Email = "receipt-reader@test.local", FullName = "Other synthetic reader", PasswordHash = "unused", RoleId = first.RoleId, IsActive = true, IsApproved = true, IsEmployee = true };
            db.Users.Add(other); await db.SaveChangesAsync(); fixture = fixture with { ActorId = other.Id };
        }
        else
        {
            var other = PaymentFor(fixture); db.Payments.Add(other); await db.SaveChangesAsync(); fixture = fixture with { PaymentId = other.Id };
        }
        var status = Assert.IsType<BalanceApplicationInspectionDto>(Assert.IsType<AcceptedResult>(await InspectAsync(db, fixture, request.RequestId!.Value)).Value);
        Assert.Equal("Unconfirmed", status.State); Assert.Null(status.Result);
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
        Assert.Equal(25m, await db.Payments.SumAsync(p => p.PaidAmount));
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
    public async Task Inspect_RechecksCurrentAccess(bool legalEntity, string revoked)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity); var request = Request(fixture);
        Result(await ApplyAsync(db, fixture, request));
        var actor = await db.Users.SingleAsync();
        var permission = await db.RolePermissions.SingleAsync(p => p.RoleId == actor.RoleId && p.Section == "payments");
        if (revoked == "edit") permission.CanEdit = false;
        if (revoked == "scope") permission.ViewScope = "own";
        if (revoked == "active") actor.IsActive = false;
        if (revoked == "approved") actor.IsApproved = false;
        await db.SaveChangesAsync();
        var denied = await InspectAsync(db, fixture, request.RequestId!.Value);
        if (revoked is "edit" or "scope") Assert.IsType<ForbidResult>(denied); else Assert.IsType<UnauthorizedResult>(denied);
        Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Inspect_InaccessibleOwnerAndMissingOwnerHaveTheSameResult(bool legalEntity, bool ownScope)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity); var request = Request(fixture);
        Result(await ApplyAsync(db, fixture, request));
        var section = legalEntity ? "legalEntities" : "clients";
        var permission = await db.RolePermissions.SingleAsync(p => p.Section == section);
        if (ownScope) permission.ViewScope = "own"; else permission.CanView = false;
        await db.SaveChangesAsync();
        var existing = await InspectAsync(db, fixture, request.RequestId!.Value);
        var missing = await InspectAsync(db, fixture with { OwnerId = int.MaxValue }, request.RequestId!.Value);
        if (ownScope) { Assert.IsType<NotFoundResult>(existing); Assert.IsType<NotFoundResult>(missing); }
        else { Assert.IsType<UnauthorizedResult>(existing); Assert.IsType<UnauthorizedResult>(missing); }
    }

    [Theory]
    [InlineData("key")]
    [InlineData("payment")]
    [InlineData("actor")]
    public async Task Inspect_InvalidIdentityDoesNotReadOrWriteAResult(string invalid)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        var key = invalid == "key" ? Guid.Empty : Guid.NewGuid();
        if (invalid == "payment") fixture = fixture with { PaymentId = 0 };
        if (invalid == "actor") fixture = fixture with { ActorId = 0 };
        var result = await InspectAsync(db, fixture, key);
        if (invalid == "actor") Assert.IsType<UnauthorizedResult>(result); else Assert.IsType<BadRequestResult>(result);
        Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Fact]
    public async Task Inspect_DirtyAndExternalContextRemainOwnedByTheCaller()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        var pending = new Client { Name = "Unrelated pending input" }; db.Clients.Add(pending);
        Assert.IsType<ConflictObjectResult>(await InspectAsync(db, fixture, Guid.NewGuid()));
        Assert.Equal(EntityState.Added, db.Entry(pending).State);
        db.Entry(pending).State = EntityState.Detached;
        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.IsType<ConflictObjectResult>(await InspectAsync(db, fixture, Guid.NewGuid()));
        Assert.Same(transaction, db.Database.CurrentTransaction);
        await transaction.RollbackAsync();
    }

    internal static BalanceApplicationInspectionDto InspectResult(IActionResult result)
        => Assert.IsType<BalanceApplicationInspectionDto>(Assert.IsType<OkObjectResult>(result).Value);

    internal static Task<IActionResult> InspectAsync(PaymentContext db, Fixture fixture, Guid key)
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, fixture.ActorId.ToString())], "synthetic")) };
        var controllerContext = new ControllerContext { HttpContext = context };
        return fixture.LegalEntity
            ? new LegalEntityBalancesController(db) { ControllerContext = controllerContext }.GetApplication(fixture.OwnerId, key, fixture.PaymentId, CancellationToken.None)
            : new ClientBalancesController(db) { ControllerContext = controllerContext }.GetApplication(fixture.OwnerId, key, fixture.PaymentId, CancellationToken.None);
    }
}
