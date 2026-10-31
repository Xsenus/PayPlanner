using Xunit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers.V1;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class VersionedPaymentWriterTests
{
    public static IEnumerable<object[]> ClosedCases()
    {
        foreach (var version in new[] { 1, 2 })
            foreach (var legalEntity in new[] { false, true })
                foreach (var refund in new[] { false, true })
                    foreach (var operation in new[] { "update", "delete", "create", "notes" })
                        yield return [version, legalEntity, refund, operation];
    }

    [Theory]
    [MemberData(nameof(ClosedCases))]
    public async Task ClosedReceipt_PreservesCompensationAndAllowsOnlyNotes(int version, bool legalEntity, bool refund, string operation)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var seed = await CompensatedPaymentWriterTests.SeedCancelledAsync(db, legalEntity, refund, false);
        var before = await CompensatedPaymentWriterTests.SnapshotAsync(db);
        var model = await ModelAsync(db, seed.Receipt);
        model.Notes = "Versioned permitted note";
        if (operation == "update") { model.Amount = 90m; model.PaidAmount = 0m; model.Status = PaymentStatus.Pending; }
        if (operation == "create") { model.Id = 0; model.PaidAmount = 10m; model.Status = PaymentStatus.Processing; }
        var result = await WriteAsync(db, seed.Receipt, version, operation, model);
        if (operation == "notes")
        {
            Assert.IsType<OkObjectResult>(result);
            Assert.Equal(model.Notes, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.Receipt.PaymentId)).Notes);
        }
        else Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(db));
        Assert.False(db.ChangeTracker.HasChanges()); Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(1, false, "update")]
    [InlineData(2, false, "update")]
    [InlineData(1, true, "update")]
    [InlineData(2, true, "update")]
    [InlineData(1, false, "delete")]
    [InlineData(2, false, "delete")]
    [InlineData(1, true, "delete")]
    [InlineData(2, true, "delete")]
    public async Task AppliedCredit_CannotBeReducedOrLoseItsPayment(int version, bool legalEntity, string operation)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legalEntity);
        (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanDelete = true; await db.SaveChangesAsync();
        Result(await ApplyAsync(db, fixture, Request(fixture))); var before = await CompensatedPaymentWriterTests.SnapshotAsync(db);
        var model = await ModelAsync(db, fixture); model.Amount = 10m; model.PaidAmount = 0m; model.Status = PaymentStatus.Pending;
        Assert.IsType<BadRequestObjectResult>(await WriteAsync(db, fixture, version, operation, model));
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData(1, "update")]
    [InlineData(2, "update")]
    [InlineData(1, "delete")]
    [InlineData(2, "delete")]
    [InlineData(1, "create")]
    [InlineData(2, "create")]
    public async Task DirtyAndExternalContext_ArePreserved(int version, string operation)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        var model = await ModelAsync(db, fixture); var client = await db.Clients.SingleAsync(); client.Name = "Pending caller edit";
        Assert.IsType<ConflictObjectResult>(await WriteAsync(db, fixture, version, operation, model));
        Assert.Equal(EntityState.Modified, db.Entry(client).State); db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.IsType<ConflictObjectResult>(await WriteAsync(db, fixture, version, operation, model));
        Assert.Same(transaction, db.Database.CurrentTransaction); await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task Delete_UsesDeletePermissionIndependentlyOfEdit(int version, bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legalEntity);
        var permission = await db.RolePermissions.SingleAsync(p => p.Section == "payments"); permission.CanDelete = true; permission.CanEdit = false; await db.SaveChangesAsync();
        Assert.IsType<NoContentResult>(await WriteAsync(db, fixture, version, "delete", await ModelAsync(db, fixture)));
        Assert.Empty(await db.Payments.ToArrayAsync()); Assert.Single(await db.ClientBalanceTransactions.ToArrayAsync());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task HiddenParent_RejectsNotesAndNewLinkedPayment(int version, bool create)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await CompensatedPaymentWriterTests.SeedCancelledAsync(db, false, false, false);
        var model = await ModelAsync(db, seed.Receipt); var permission = await db.RolePermissions.SingleAsync(p => p.Section == "payments"); permission.ViewScope = "own";
        db.PaymentResponsibles.Add(new PaymentResponsible { PaymentId = seed.Receipt.PaymentId, UserId = seed.Receipt.ActorId });
        await db.SaveChangesAsync(); var before = await CompensatedPaymentWriterTests.SnapshotAsync(db);
        if (create) model.Id = 0;
        Assert.IsType<NotFoundResult>(await WriteAsync(db, seed.Receipt, version, create ? "create" : "notes", model));
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(db));
    }

    public static IEnumerable<object[]> AccessCases()
    {
        foreach (var version in new[] { 1, 2 })
            foreach (var operation in new[] { "create", "update", "delete" })
                foreach (var denial in new[] { "permission", "inactive", "unapproved", "missingActor" }) yield return [version, operation, denial];
    }
    [Theory]
    [MemberData(nameof(AccessCases))]
    public async Task ActualActorAndPermissionAreRequired(int version, string operation, string denial)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false); var model = await ModelAsync(db, fixture);
        var user = await db.Users.SingleAsync(); var permission = await db.RolePermissions.SingleAsync(p => p.Section == "payments");
        permission.CanDelete = true;
        if (denial == "permission") { permission.CanCreate = false; permission.CanEdit = false; permission.CanDelete = false; }
        if (denial == "inactive") user.IsActive = false;
        if (denial == "unapproved") user.IsApproved = false;
        await db.SaveChangesAsync(); if (denial == "missingActor") fixture = fixture with { ActorId = int.MaxValue };
        var before = await CompensatedPaymentWriterTests.SnapshotAsync(db);
        var result = await WriteAsync(db, fixture, version, operation, model);
        if (denial == "permission") Assert.IsType<ForbidResult>(result); else Assert.IsType<UnauthorizedResult>(result);
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task CreditHistory_CannotChangeOwnerEvenIfPaidAmountIsPreserved(int version, bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legalEntity);
        Result(await ApplyAsync(db, fixture, Request(fixture))); var model = await ModelAsync(db, fixture);
        if (legalEntity) { var owner = new LegalEntity { FullName = "Other synthetic owner", ShortName = "Other" }; db.LegalEntities.Add(owner); await db.SaveChangesAsync(); model.LegalEntityId = owner.Id; }
        else { var owner = new Client { Name = "Other synthetic owner" }; db.Clients.Add(owner); await db.SaveChangesAsync(); model.ClientId = owner.Id; }
        var before = await CompensatedPaymentWriterTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await WriteAsync(db, fixture, version, "update", model));
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData(1, "create")]
    [InlineData(2, "create")]
    [InlineData(1, "update")]
    [InlineData(2, "update")]
    [InlineData(1, "delete")]
    [InlineData(2, "delete")]
    public async Task MissingHttpAuthentication_IsUnauthorized(int version, string operation)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        var model = await ModelAsync(db, fixture);
        IActionResult result;
        if (version == 1)
        {
            var controller = new PaymentsV1Controller(db);
            result = operation == "create" ? await controller.Create(model) : operation == "delete" ? await controller.Delete(fixture.PaymentId) : await controller.Update(fixture.PaymentId, model);
        }
        else
        {
            var controller = new PaymentsV2Controller(db);
            result = operation == "create" ? await controller.Create(model) : operation == "delete" ? await controller.Delete(fixture.PaymentId) : await controller.Update(fixture.PaymentId, model);
        }
        Assert.IsType<UnauthorizedResult>(result); Assert.Single(await db.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelinkingAnOpenPaymentToAClosedInvoice_IsRejected(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await CompensatedPaymentWriterTests.SeedCancelledAsync(db, legalEntity, false, false);
        var target = await ModelAsync(db, seed.Receipt); var standalone = new Payment { ClientId = target.ClientId, LegalEntityId = target.LegalEntityId, Amount = 100m, Date = target.Date, Type = target.Type, DocumentKind = DocumentKind.Payment };
        db.Payments.Add(standalone); await db.SaveChangesAsync(); var fixture = seed.Receipt with { PaymentId = standalone.Id };
        var model = await ModelAsync(db, fixture); model.InvoiceId = target.InvoiceId; var before = await CompensatedPaymentWriterTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await WriteAsync(db, fixture, 2, "update", model));
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(1, true, false)]
    [InlineData(2, true, false)]
    [InlineData(1, false, true)]
    [InlineData(2, false, true)]
    [InlineData(1, true, true)]
    [InlineData(2, true, true)]
    public async Task CreatingLinkedPayment_RejectsDifferentOwnerOrDirection(int version, bool legalEntity, bool changeDirection)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await CompensatedPaymentWriterTests.SeedOpenAsync(db, legalEntity);
        var model = await ModelAsync(db, seed.Receipt); model.Id = 0;
        if (changeDirection) model.Type = PaymentType.Expense;
        else if (legalEntity) { var owner = new LegalEntity { FullName = "Other synthetic owner", ShortName = "Other" }; db.LegalEntities.Add(owner); await db.SaveChangesAsync(); model.LegalEntityId = owner.Id; }
        else { var owner = new Client { Name = "Other synthetic owner" }; db.Clients.Add(owner); await db.SaveChangesAsync(); model.ClientId = owner.Id; }
        var before = await CompensatedPaymentWriterTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await WriteAsync(db, seed.Receipt, version, "create", model));
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(db));
    }

    internal static async Task<Payment> ModelAsync(PaymentContext db, Fixture fixture)
        => Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await BalanceApplicationPaymentWriterTests.Controller(db, fixture).GetById(fixture.PaymentId, CancellationToken.None)).Value);

    internal static Task<IActionResult> WriteAsync(PaymentContext db, Fixture fixture, int version, string operation, Payment model)
    {
        // HTTP creation accepts scalar references. Do not re-add read-model navigation graphs.
        if (operation == "create") model = new Payment
        {
            Id = model.Id,
            InvoiceId = model.InvoiceId,
            Amount = model.Amount,
            PaidAmount = model.PaidAmount,
            Date = model.Date,
            PlannedDate = model.PlannedDate,
            PaidDate = model.PaidDate,
            LastPaymentDate = model.LastPaymentDate,
            ClientId = model.ClientId,
            LegalEntityId = model.LegalEntityId,
            ExpenseClientId = model.ExpenseClientId,
            ExpenseLegalEntityId = model.ExpenseLegalEntityId,
            Type = model.Type,
            Status = model.Status,
            DocumentKind = model.DocumentKind,
            IsPaid = model.IsPaid,
            Description = model.Description,
            Notes = model.Notes,
            SystemNotes = model.SystemNotes,
        };
        var context = BalanceApplicationPaymentWriterTests.Controller(db, fixture).ControllerContext;
        if (version == 1)
        {
            var controller = new PaymentsV1Controller(db) { ControllerContext = context };
            return operation == "create" ? controller.Create(model) : operation == "delete" ? controller.Delete(fixture.PaymentId) : controller.Update(fixture.PaymentId, model);
        }
        else
        {
            var controller = new PaymentsV2Controller(db) { ControllerContext = context };
            return operation == "create" ? controller.Create(model) : operation == "delete" ? controller.Delete(fixture.PaymentId) : controller.Update(fixture.PaymentId, model);
        }
    }
}

public sealed class VersionedPaymentWriterPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    public static IEnumerable<object[]> WriterCases()
    {
        foreach (var version in new[] { 1, 2 })
            foreach (var legalEntity in new[] { false, true })
                foreach (var operation in new[] { "update", "delete", "create", "notes" }) yield return [version, legalEntity, operation];
    }

    [Theory]
    [MemberData(nameof(WriterCases))]
    public async Task CancellationHoldingReceipt_ConflictsThenProtectsHistory(int version, bool legalEntity, string operation)
    {
        await using var database = await template.CreateDatabaseAsync(); CompensatedPaymentWriterTests.Seed seed;
        await using (var db = Open(database.ConnectionString)) seed = await CompensatedPaymentWriterTests.SeedOpenAsync(db, legalEntity);
        var barrier = new HoldSave(); await using var cancelling = Open(database.ConnectionString, barrier); await using var editor = Open(database.ConnectionString);
        var model = await VersionedPaymentWriterTests.ModelAsync(editor, seed.Receipt); model.Notes = "Concurrent versioned note";
        if (operation == "create") { model.Id = 0; model.PaidAmount = 10m; }
        if (operation == "update") model.Amount = 90m;
        var root = seed.Receipt with { PaymentId = seed.RootId };
        var pending = InvoiceCancellationCreditBoundaryTests.Controller(cancelling, root).Cancel(root.PaymentId,
            new() { Reason = "Synthetic concurrent cancellation", ReceivedMoneyAction = InvoiceCancellationMoneyAction.LeaveAsAdvance }, CancellationToken.None);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.IsType<ConflictObjectResult>(await VersionedPaymentWriterTests.WriteAsync(editor, seed.Receipt, version, operation, model).WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>((await pending.WaitAsync(TimeSpan.FromSeconds(20))).Result);
        var before = await CompensatedPaymentWriterTests.SnapshotAsync(editor);
        var result = await VersionedPaymentWriterTests.WriteAsync(editor, seed.Receipt, version, operation, model);
        if (operation == "notes") Assert.IsType<OkObjectResult>(result); else Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(editor));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task UpdateHoldingPayment_ApplyConflictsAndLaterCreditIsPreserved(int version, bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture;
        await using (var db = Open(database.ConnectionString)) fixture = await SeedAsync(db, legalEntity);
        var barrier = new HoldSave(); await using var editor = Open(database.ConnectionString, barrier); await using var applying = Open(database.ConnectionString);
        var model = await VersionedPaymentWriterTests.ModelAsync(editor, fixture); model.Amount = 90m;
        var pending = VersionedPaymentWriterTests.WriteAsync(editor, fixture, version, "update", model);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.IsType<ConflictObjectResult>(await ApplyAsync(applying, fixture, Request(fixture)).WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Result(await ApplyAsync(applying, fixture, Request(fixture))); var before = await CompensatedPaymentWriterTests.SnapshotAsync(applying);
        model = await VersionedPaymentWriterTests.ModelAsync(editor, fixture); model.Amount = 10m;
        Assert.IsType<BadRequestObjectResult>(await VersionedPaymentWriterTests.WriteAsync(editor, fixture, version, "update", model));
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(applying));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task CreateSecondSaveFailure_RollsBackInsertedPaymentAndInvoice(int version, bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync(); CompensatedPaymentWriterTests.Seed seed;
        await using (var db = Open(database.ConnectionString)) seed = await CompensatedPaymentWriterTests.SeedOpenAsync(db, legalEntity);
        await using var writer = Open(database.ConnectionString, new FailSecondSave());
        var model = await VersionedPaymentWriterTests.ModelAsync(writer, seed.Receipt); model.Id = 0; model.PaidAmount = 10m;
        var before = await CompensatedPaymentWriterTests.SnapshotAsync(writer);
        await Assert.ThrowsAsync<IOException>(() => VersionedPaymentWriterTests.WriteAsync(writer, seed.Receipt, version, "create", model));
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(writer)); Assert.Null(writer.Database.CurrentTransaction); Assert.False(writer.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task DeleteSecondSaveFailure_RollsBackRemovedPaymentAndInvoice(int version, bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync(); CompensatedPaymentWriterTests.Seed seed;
        await using (var db = Open(database.ConnectionString)) seed = await CompensatedPaymentWriterTests.SeedOpenAsync(db, legalEntity);
        await using var writer = Open(database.ConnectionString, new FailSecondSave());
        var model = await VersionedPaymentWriterTests.ModelAsync(writer, seed.Receipt);
        var before = await CompensatedPaymentWriterTests.SnapshotAsync(writer);
        await Assert.ThrowsAsync<IOException>(() => VersionedPaymentWriterTests.WriteAsync(writer, seed.Receipt, version, "delete", model));
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(writer)); Assert.Null(writer.Database.CurrentTransaction); Assert.False(writer.ChangeTracker.HasChanges());
    }

    private static PostgreSqlPaymentContext Open(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor); return new(options.Options);
    }
    private sealed class HoldSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); return result; }
    }
    private sealed class FailSecondSave : SaveChangesInterceptor
    {
        private int saves;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var pending = eventData.Context!.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
            if (pending.Length > 0 && pending.All(e => e.Entity is ObjectAuditEvent)) return ValueTask.FromResult(result);
            if (++saves == 2) throw new IOException("Synthetic second versioned save failure");
            return ValueTask.FromResult(result);
        }
    }
}
