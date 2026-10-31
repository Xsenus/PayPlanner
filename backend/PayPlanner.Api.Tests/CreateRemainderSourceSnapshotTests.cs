using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Payments;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class CreateRemainderSourceSnapshotTests
{
    public static IEnumerable<object[]> StaleCases()
    {
        foreach (var legal in new[] { false, true })
            foreach (var beforeRegistration in new[] { false, true })
                foreach (var field in new[] { "amount", "paid", "notes", "owner", "date", "type" }) yield return [legal, beforeRegistration, field];
    }
    [Theory]
    [MemberData(nameof(StaleCases))]
    public async Task OldFormSnapshotCannotCreateDebtEvenWhenRegistrationIsNew(bool legal, bool beforeRegistration, string field)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        await AssertStaleAsync(db, await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal), beforeRegistration, field);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewlySavedPrimaryReturnsTheSameSnapshotAsItsAuthoritativeRead(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await AssertRoundTripAsync(db, legal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("number")]
    [InlineData("wrongVersion")]
    [InlineData("length")]
    [InlineData("hex")]
    public async Task NewDraftNeedsAnExplicitWellFormedSnapshot(string invalid)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, false); var id = await RegisterAsync(db, source);
        var command = Command(source, null); if (invalid == "missing") command.Remove("sourceConfirmationToken");
        else command["sourceConfirmationToken"] = invalid switch
        {
            "null" => null,
            "number" => JsonValue.Create(42),
            "empty" => JsonValue.Create(""),
            "wrongVersion" => JsonValue.Create("remainder-v1:" + new string('A', 64)),
            "length" => JsonValue.Create("remainder-v2:" + new string('A', 65)),
            _ => JsonValue.Create("remainder-v2:" + new string('X', 64))
        };
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await CommitAsync(db, source, id, command));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
        Assert.Equal(PaymentCreateAttempt.Draft, (await db.PaymentCreateAttempts.AsNoTracking().SingleAsync()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotDoesNotReplaceCurrentEditPermission(bool ownPrimary)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, false);
        if (ownPrimary) source = await NewPrimaryAsync(db, source);
        var token = await TokenAsync(db, source); var id = await RegisterAsync(db, source);
        (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanEdit = false; await db.SaveChangesAsync();
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        var result = await CommitAsync(db, source, id, Command(source, token));
        if (ownPrimary) Assert.IsType<CreatedResult>(result);
        else { Assert.IsType<ForbidResult>(result); Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedReplayKeepsItsOriginalSnapshotIdentity(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        await AssertReplayAsync(db, await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal));
    }

    [Fact]
    public async Task OldCommittedCommandWithoutSnapshotRemainsReadableWithoutBackfill()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, false);
        var id = await RegisterAsync(db, source); var command = Command(source, null); command.Remove("sourceConfirmationToken");
        var options = Options(); Assert.True(PaymentCreateCommand.TryRead(JsonSerializer.SerializeToElement(command), options, out _, out var identity, out _));
        var child = LegacyRemainderConfirmationTests.Child(source); db.Payments.Add(child); await db.SaveChangesAsync();
        var attempt = await db.PaymentCreateAttempts.SingleAsync(); attempt.State = PaymentCreateAttempt.Committed;
        attempt.CanonicalPaymentId = child.Id; attempt.CommittedAtUtc = DateTimeOffset.UtcNow; attempt.PayloadIdentity = identity;
        await db.SaveChangesAsync(); var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        Assert.Equal(child.Id, Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await CommitAsync(db, source, id, command)).Value).Id);
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db)); Assert.Empty(await db.PaymentRemainderTransfers.ToArrayAsync());
    }

    [Fact]
    public async Task SnapshotIsReadOnlyUnmappedAndNotComputedForTheList()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, false);
        Assert.Null(db.Model.FindEntityType(typeof(Payment))!.FindProperty(nameof(Payment.RemainderConfirmationToken)));
        var controller = PaymentCreateFinancialBoundaryTests.Controller(db, source);
        var list = Assert.IsAssignableFrom<IEnumerable<Payment>>(Assert.IsType<OkObjectResult>(await controller.Get(null, null, null, null)).Value);
        Assert.Null(Assert.Single(list).RemainderConfirmationToken);
        Assert.DoesNotContain("remainderConfirmationToken", JsonSerializer.Serialize(list, Options()));
        var token = await TokenAsync(db, source); Assert.StartsWith("remainder-v2:", token); Assert.Equal(77, token.Length);
        var injected = JsonSerializer.Deserialize<Payment>("{\"remainderConfirmationToken\":\"forged\"}", Options()); Assert.Null(injected!.RemainderConfirmationToken);
    }

    [Fact]
    public void SnapshotMetadataIsIdentityBoundAndSupportsStrictModelBinding()
    {
        var source = new Fixture(1, false, 1, 1); var first = Command(source, "remainder-v2:" + new string('A', 64));
        var second = Command(source, "remainder-v2:" + new string('B', 64)); var options = Options(); options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        Assert.True(PaymentCreateCommand.TryRead(JsonSerializer.SerializeToElement(first), options, out var model, out var a, out _));
        Assert.True(PaymentCreateCommand.TryRead(JsonSerializer.SerializeToElement(second), options, out _, out var b, out _));
        Assert.NotEqual(a, b); Assert.Equal(60m, model!.Amount); Assert.Null(model.RemainderConfirmationToken);
    }

    [Fact]
    public async Task PrimaryCommandCannotReuseARemainderSnapshot()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, false);
        var controller = PaymentCreateFinancialBoundaryTests.Controller(db, source);
        var id = Assert.IsType<PaymentCreateAttemptStateDto>(Assert.IsType<CreatedResult>(await controller.RegisterCreateAttempt(JsonSerializer.SerializeToElement(new { operation = "primary" }), default)).Value).AttemptId;
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await CommitAsync(db, source, id, Command(source, await TokenAsync(db, source))));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
    }

    internal static async Task AssertStaleAsync(PaymentContext db, Fixture source, bool beforeRegistration, string field)
    {
        var token = await TokenAsync(db, source); Guid? id = beforeRegistration ? null : await RegisterAsync(db, source);
        var row = await db.Payments.SingleAsync(p => p.Id == source.PaymentId);
        switch (field)
        {
            case "amount": row.Amount = row.PaidAmount = 55m; break;
            case "paid": row.PaidAmount = 30m; break;
            case "notes": row.Notes = "Edited after primary save"; break;
            case "date": row.Date = row.Date.AddDays(1); break;
            case "type": row.Type = PaymentType.Expense; break;
            case "owner":
                if (source.LegalEntity) { var owner = new LegalEntity { ShortName = "Other source owner" }; db.LegalEntities.Add(owner); await db.SaveChangesAsync(); row.LegalEntityId = owner.Id; }
                else { var owner = new Client { Name = "Other source owner" }; db.Clients.Add(owner); await db.SaveChangesAsync(); row.ClientId = owner.Id; }
                break;
        }
        await db.SaveChangesAsync(); id ??= await RegisterAsync(db, source);
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        Assert.IsType<ConflictObjectResult>(await CommitAsync(db, source, id.Value, Command(source, token)));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db)); Assert.Equal(PaymentCreateAttempt.Draft, (await db.PaymentCreateAttempts.AsNoTracking().SingleAsync()).State);
    }
    internal static async Task AssertRoundTripAsync(PaymentContext db, bool legal)
    {
        var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal);
        var create = await PaymentInputStateTests.RouteAsync(db, source, 3); var model = PaymentFor(source);
        model.Amount = model.PaidAmount = 40.00m; model.Status = PaymentStatus.Completed; model.IsPaid = true;
        model.Date = DateTime.SpecifyKind(model.Date, DateTimeKind.Utc); model.PlannedDate = model.Date;
        var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await create(model)).Value);
        source = source with { PaymentId = saved.Id }; Assert.NotNull(saved.RemainderConfirmationToken);
        Assert.Equal(saved.RemainderConfirmationToken, await TokenAsync(db, source));
        var child = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await CommitAsync(db, source, await RegisterAsync(db, source), Command(source, saved.RemainderConfirmationToken))).Value);
        Assert.NotNull(child.RemainderConfirmationToken); Assert.Equal(source.PaymentId, (await db.PaymentRemainderTransfers.SingleAsync()).SourcePaymentId);
    }
    private static async Task<Fixture> NewPrimaryAsync(PaymentContext db, Fixture source)
    {
        var create = await PaymentInputStateTests.RouteAsync(db, source, 3); var model = PaymentFor(source);
        model.Amount = model.PaidAmount = 40m; model.Status = PaymentStatus.Completed; model.IsPaid = true;
        return source with { PaymentId = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await create(model)).Value).Id };
    }
    internal static async Task AssertReplayAsync(PaymentContext db, Fixture source)
    {
        var token = await TokenAsync(db, source); var id = await RegisterAsync(db, source); var command = Command(source, token);
        var created = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await CommitAsync(db, source, id, command)).Value);
        var row = await db.Payments.SingleAsync(p => p.Id == source.PaymentId); row.Amount = row.PaidAmount = 55m; await db.SaveChangesAsync();
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        Assert.Equal(created.Id, Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await CommitAsync(db, source, id, command)).Value).Id);
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
        command["sourceConfirmationToken"] = await TokenAsync(db, source);
        Assert.IsType<ConflictObjectResult>(await CommitAsync(db, source, id, command)); Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
    }
    internal static async Task<string> TokenAsync(PaymentContext db, Fixture source)
        => Assert.IsType<string>(Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await PaymentCreateFinancialBoundaryTests.Controller(db, source).GetById(source.PaymentId, default)).Value).RemainderConfirmationToken);
    internal static async Task<Guid> RegisterAsync(PaymentContext db, Fixture source)
        => Assert.IsType<PaymentCreateAttemptStateDto>(Assert.IsType<CreatedResult>(await PaymentCreateFinancialBoundaryTests.Controller(db, source).RegisterCreateAttempt(JsonSerializer.SerializeToElement(new { operation = "remainder", primaryPaymentId = source.PaymentId }), default)).Value).AttemptId;
    internal static JsonObject Command(Fixture source, string? token)
    { var command = JsonNode.Parse(PaymentInputStateTests.Command(LegacyRemainderConfirmationTests.Child(source)).GetRawText())!.AsObject(); command["sourceConfirmationToken"] = token; return command; }
    internal static Task<IActionResult> CommitAsync(PaymentContext db, Fixture source, Guid id, JsonObject command)
        => PaymentCreateFinancialBoundaryTests.Controller(db, source).CommitCreateAttempt(id, JsonSerializer.SerializeToElement(command), default);
    private static JsonSerializerOptions Options()
    { var options = new JsonSerializerOptions(JsonSerializerDefaults.Web); options.Converters.Add(new JsonStringEnumConverter()); return options; }
}

public sealed class CreateRemainderSourceSnapshotPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCreatedSourceMatchesItsStoredSnapshot(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        await CreateRemainderSourceSnapshotTests.AssertRoundTripAsync(db, legal);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NativeStaleMoneyNeverBecomesANewDebt(bool legal, bool beforeRegistration)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        await CreateRemainderSourceSnapshotTests.AssertStaleAsync(db, await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal), beforeRegistration, "amount");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeOldSnapshotCannotRebindTheRemainder(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        await CreateRemainderSourceSnapshotTests.AssertStaleAsync(db, await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal), false, "owner");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCommittedReplayDoesNotReevaluateTheOldSnapshot(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        await CreateRemainderSourceSnapshotTests.AssertReplayAsync(db, await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAnotherActorCannotCommitAnOldSnapshotWhileAnEditorOwnsTheSource(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); var pause = new HoldSave();
        await using var editing = Open(database.ConnectionString, pause); await using var creating = Open(database.ConnectionString);
        var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(editing, legal);
        var roleId = await editing.Users.Where(u => u.Id == source.ActorId).Select(u => u.RoleId).SingleAsync();
        var second = new User { Email = "other-snapshot-actor@test.local", FullName = "Other snapshot actor", PasswordHash = "unused", RoleId = roleId, IsActive = true, IsApproved = true, IsEmployee = true };
        editing.Users.Add(second); await editing.SaveChangesAsync(); var other = source with { ActorId = second.Id };
        var token = await CreateRemainderSourceSnapshotTests.TokenAsync(creating, other);
        var id = await CreateRemainderSourceSnapshotTests.RegisterAsync(creating, other); var command = CreateRemainderSourceSnapshotTests.Command(other, token);
        var model = await VersionedPaymentWriterTests.ModelAsync(editing, source); model.Notes = "Changed by the first actor";
        pause.Enabled = true; var pending = PaymentInputStateTests.UpdateAsync(editing, source, 0, model);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsType<ConflictObjectResult>(await CreateRemainderSourceSnapshotTests.CommitAsync(creating, other, id, command).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(creating);
        Assert.IsType<ConflictObjectResult>(await CreateRemainderSourceSnapshotTests.CommitAsync(creating, other, id, command));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(creating)); Assert.Empty(await creating.PaymentRemainderTransfers.ToArrayAsync());
        command["sourceConfirmationToken"] = await CreateRemainderSourceSnapshotTests.TokenAsync(creating, other);
        Assert.IsType<CreatedResult>(await CreateRemainderSourceSnapshotTests.CommitAsync(creating, other, id, command));
        Assert.Single(await creating.PaymentRemainderTransfers.ToArrayAsync());
    }
    private static PostgreSqlPaymentContext Open(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection); if (interceptor is not null) options.AddInterceptors(interceptor); return new(options.Options);
    }
    private sealed class HoldSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        { if (Enabled) { Enabled = false; Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); } return result; }
    }
}
