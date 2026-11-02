using System.Reflection;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

// Real relational state/locks plus an in-memory TestServer wire-shape fixture; no external transport.
public sealed class PaymentCreateAttemptTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameAttempt_ReplaysCurrentCanonicalWithoutOverwritingOrDuplicating(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        var actor = await SeedActorAsync(db);
        var controller = Controller(db, actor);
        var attempt = await RegisterAsync(controller);
        var command = Command();
        var first = Assert.IsType<CreatedResult>(await controller.CommitCreateAttempt(attempt.AttemptId, command, default));
        var payment = Assert.IsType<Payment>(first.Value);
        var canonical = payment.Id;
        payment.Description = "Изменено после создания";
        await db.SaveChangesAsync();
        var replay = Assert.IsType<OkObjectResult>(await controller.CommitCreateAttempt(attempt.AttemptId, command, default));
        var current = Assert.IsType<Payment>(replay.Value);
        Assert.Equal(canonical, current.Id);
        Assert.Equal("Изменено после создания", current.Description);
        Assert.Single(await db.Payments.AsNoTracking().ToArrayAsync());
        Assert.Equal(canonical, (await StateAsync(controller, attempt.AttemptId)).CanonicalPaymentId);
        Assert.IsType<ConflictObjectResult>(await controller.CommitCreateAttempt(attempt.AttemptId, Command(description: "Другой запрос"), default));
        var cancelled = Assert.IsType<OkObjectResult>(await controller.CancelCreateAttempt(attempt.AttemptId, default));
        var outcome = Assert.IsType<PaymentCreateAttemptCancelOutcomeDto>(cancelled.Value);
        Assert.Equal(PaymentCreateAttempt.Committed, outcome.State);
        Assert.Equal(canonical, outcome.CanonicalPaymentId);
        var wireOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ConfigureProductionJson(wireOptions);
        AssertOpaqueCancelJson(JsonSerializer.SerializeToElement(outcome, wireOptions), attempt.AttemptId,
            PaymentCreateAttempt.Committed, canonical);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelTombstone_BlocksQueuedCommitAndNeverLazilyRegisters(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        var actor = await SeedActorAsync(db);
        var controller = Controller(db, actor);
        var attempt = await RegisterAsync(controller);
        var cancelled = Assert.IsType<OkObjectResult>(await controller.CancelCreateAttempt(attempt.AttemptId, default));
        Assert.Equal(PaymentCreateAttempt.Cancelled, Assert.IsType<PaymentCreateAttemptCancelOutcomeDto>(cancelled.Value).State);
        Assert.Equal(410, Assert.IsType<ObjectResult>(await controller.CommitCreateAttempt(attempt.AttemptId, Command(), default)).StatusCode);
        var unknown = Guid.NewGuid();
        Assert.Equal(410, Assert.IsType<ObjectResult>(await controller.CommitCreateAttempt(unknown, Command(), default)).StatusCode);
        Assert.Equal(410, Assert.IsType<ObjectResult>(await controller.CancelCreateAttempt(unknown, default)).StatusCode);
        Assert.Empty(await db.Payments.ToArrayAsync());
        Assert.Single(await db.PaymentCreateAttempts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameUuidRace_CommitAndCancelShareOwnedLease_ThenRecoverWinner(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        var hold = new HoldOutcomeSave();
        await using var first = fixture.Open(hold);
        var actor = await SeedActorAsync(first);
        var attempt = await RegisterAsync(Controller(first, actor));
        hold.Enabled = true;
        var running = Controller(first, actor).CommitCreateAttempt(attempt.AttemptId, Command(), default);
        IActionResult? winningResult = null;
        try
        {
            await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await using var second = fixture.Open();
            var contender = Controller(second, actor);
            Assert.IsType<ConflictObjectResult>(await contender.CommitCreateAttempt(attempt.AttemptId, Command(), default));
            Assert.IsType<ConflictObjectResult>(await contender.CancelCreateAttempt(attempt.AttemptId, default));
        }
        finally
        {
            hold.Release.TrySetResult(true);
            winningResult = await running;
        }
        var winner = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(winningResult).Value);
        await using var observer = fixture.Open();
        Assert.Single(await observer.Payments.ToArrayAsync());
        var resolved = Assert.IsType<PaymentCreateAttemptCancelOutcomeDto>(Assert.IsType<OkObjectResult>(
            await Controller(observer, actor).CancelCreateAttempt(attempt.AttemptId, default)).Value);
        Assert.Equal(PaymentCreateAttempt.Committed, resolved.State);
        Assert.Equal(winner.Id, resolved.CanonicalPaymentId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DraftExpiryDoesNotReopenCommitted_AndExpiredDraftCanBeCancelled(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        var actor = await SeedActorAsync(db);
        var time = new ControlledTime();
        var controller = Controller(db, actor, time: time);
        var draft = await RegisterAsync(controller);
        var committed = await RegisterAsync(controller);
        var payment = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await controller.CommitCreateAttempt(committed.AttemptId, Command(), default)).Value);
        time.Now = time.Now.AddHours(25);
        Assert.Equal(410, Assert.IsType<ObjectResult>(await controller.GetCreateAttempt(draft.AttemptId, default)).StatusCode);
        Assert.Equal(410, Assert.IsType<ObjectResult>(await controller.CommitCreateAttempt(draft.AttemptId, Command(), default)).StatusCode);
        Assert.Equal(PaymentCreateAttempt.Cancelled, Assert.IsType<PaymentCreateAttemptCancelOutcomeDto>(Assert.IsType<OkObjectResult>(
            await controller.CancelCreateAttempt(draft.AttemptId, default)).Value).State);
        var replay = Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await controller.CommitCreateAttempt(committed.AttemptId, Command(), default)).Value);
        Assert.Equal(payment.Id, replay.Id);
        Assert.Single(await db.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnershipAndFreshPermissions_NeverDiscloseForeignOutcome(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        var actor = await SeedActorAsync(db);
        var other = await SeedActorAsync(db);
        var owned = Controller(db, actor);
        var attempt = await RegisterAsync(owned);
        Assert.Equal(410, Assert.IsType<ObjectResult>(await Controller(db, other).GetCreateAttempt(attempt.AttemptId, default)).StatusCode);
        Assert.Equal(410, Assert.IsType<ObjectResult>(await Controller(db, other).CancelCreateAttempt(attempt.AttemptId, default)).StatusCode);
        Assert.Equal(410, Assert.IsType<ObjectResult>(await Controller(db, other).CommitCreateAttempt(attempt.AttemptId, Command(), default)).StatusCode);
        var roleId = await db.Users.Where(u => u.Id == actor).Select(u => u.RoleId).SingleAsync();
        var permission = await db.RolePermissions.SingleAsync(p => p.RoleId == roleId && p.Section == RolePermissionCatalog.Payments);
        permission.CanCreate = false;
        await db.SaveChangesAsync();
        Assert.IsType<ForbidResult>(await owned.GetCreateAttempt(attempt.AttemptId, default));
        Assert.IsType<ForbidResult>(await owned.CancelCreateAttempt(attempt.AttemptId, default));
        Assert.IsType<ForbidResult>(await owned.CommitCreateAttempt(attempt.AttemptId, Command(), default));
        Assert.Empty(await db.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SellerSelectionIsNotCounterparty_AndCannotBeSwitchedByCommit(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        var actor = await SeedActorAsync(db);
        var company = new LegalEntity { ShortName = "Своя", Inn = "7707083893", ProfileStatus = CompanyProfileStatuses.Active };
        var foreign = new LegalEntity { ShortName = "Контрагент", Inn = "7700000000", ProfileStatus = CompanyProfileStatuses.Active };
        db.LegalEntities.AddRange(company, foreign);
        await db.SaveChangesAsync();
        var branch = new CompanyBranch { LegalEntityId = company.Id, Name = "Офис", Status = CompanyProfileStatuses.Active };
        db.CompanyBranches.Add(branch);
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" });
        await db.SaveChangesAsync();
        var controller = Controller(db, actor);
        Assert.IsType<BadRequestObjectResult>(await controller.RegisterCreateAttempt(RegisterBody(company: foreign.Id), default));
        var attempt = await RegisterAsync(controller, company: company.Id, branch: branch.Id);
        Assert.IsType<ConflictObjectResult>(await controller.CommitCreateAttempt(attempt.AttemptId, Command(), default));
        var selected = Command(company: company.Id, branch: branch.Id);
        var payment = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await controller.CommitCreateAttempt(attempt.AttemptId, selected, default)).Value);
        Assert.Equal(company.Id, payment.SellerCompanyId);
        Assert.Equal(branch.Id, payment.SellerBranchId);
        Assert.Null(payment.SellerSnapshotJson);
        Assert.Null(payment.LegalEntityId);
        var retired = await db.LegalEntities.SingleAsync(c => c.Id == company.Id);
        retired.ProfileStatus = CompanyProfileStatuses.Archived;
        await db.SaveChangesAsync();
        Assert.IsType<ForbidResult>(await controller.GetCreateAttempt(attempt.AttemptId, default));
        Assert.IsType<ForbidResult>(await controller.CommitCreateAttempt(attempt.AttemptId, selected, default));
        Assert.IsType<ForbidResult>(await controller.CancelCreateAttempt(attempt.AttemptId, default));
        Assert.Single(await db.Payments.ToArrayAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingPrimaryDeleteWins_NoNewRemainder_AndMetadataDoesNotRestrictDelete(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        var actor = await SeedActorAsync(db);
        var controller = Controller(db, actor);
        // Historical source without ownership metadata; creation is fixture setup,
        // not the key-required route under test in the remainder scenarios.
        var primary = new Payment
        { Date = DateTime.Today, PlannedDate = DateTime.Today, Amount = 100m, Type = PaymentType.Income, Status = PaymentStatus.Pending };
        db.Payments.Add(primary); await db.SaveChangesAsync();
        var attempt = await RegisterAsync(controller, "remainder", primary: primary.Id);
        var hold = new HoldPrimaryDelete();
        await using (var deleter = fixture.Open(hold))
        {
            var deleting = Controller(deleter, actor).Delete(primary.Id, default);
            IActionResult? deleted = null;
            try
            {
                await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.IsType<ConflictObjectResult>(await controller.CommitCreateAttempt(attempt.AttemptId, Command(), default));
            }
            finally { hold.Release.TrySetResult(); deleted = await deleting; }
            Assert.IsType<NoContentResult>(deleted);
        }
        Assert.IsType<NotFoundResult>(await controller.CommitCreateAttempt(attempt.AttemptId, Command(), default));
        Assert.Empty(await db.Payments.AsNoTracking().ToArrayAsync());
        Assert.Single(await db.PaymentCreateAttempts.AsNoTracking().ToArrayAsync());
        var ordinary = await RegisterAsync(controller);
        var created = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await controller.CommitCreateAttempt(ordinary.AttemptId, Command(), default)).Value);
        Assert.IsType<NoContentResult>(await controller.Delete(created.Id, default));
        Assert.IsType<NotFoundResult>(await controller.GetCreateAttempt(ordinary.AttemptId, default));
        Assert.Equal(created.Id, (await db.PaymentCreateAttempts.AsNoTracking().SingleAsync(a => a.Id == ordinary.AttemptId)).CanonicalPaymentId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingPrewriteValidation400_LeavesDraftUntilAuthoritativeCancel(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        var actor = await SeedActorAsync(db);
        var controller = Controller(db, actor);
        var attempt = await RegisterAsync(controller);
        var invalid = JsonNode.Parse(Command().GetRawText())!.AsObject();
        invalid["incomeTypeId"] = int.MaxValue;
        Assert.IsType<BadRequestObjectResult>(await controller.CommitCreateAttempt(attempt.AttemptId, Element(invalid.ToJsonString()), default));
        Assert.Equal(PaymentCreateAttempt.Draft, (await StateAsync(controller, attempt.AttemptId)).State);
        Assert.Empty(await db.Payments.ToArrayAsync());
        Assert.Equal(PaymentCreateAttempt.Cancelled, Assert.IsType<PaymentCreateAttemptCancelOutcomeDto>(Assert.IsType<OkObjectResult>(
            await controller.CancelCreateAttempt(attempt.AttemptId, default)).Value).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutcomeSaveFault_RollsBackPaymentBalanceCashOrderAndAuditTogether(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        var fault = new RejectOutcomeSave();
        await using var db = fixture.Open(fault);
        var actor = await SeedActorAsync(db);
        var client = new Client { Name = "Плательщик", ResponsibleUserId = actor };
        var source = new PaymentSource { Name = "Наличные" };
        db.Clients.Add(client); db.PaymentSources.Add(source);
        await db.SaveChangesAsync();
        var controller = Controller(db, actor);
        var attempt = await RegisterAsync(controller);
        var priorAudit = await db.ObjectAuditEvents.CountAsync();
        fault.PriorAuditCount = priorAudit;
        fault.Enabled = true;
        await Assert.ThrowsAsync<OutcomeSaveFault>(() => controller.CommitCreateAttempt(attempt.AttemptId,
            Command(paid: true, client: client.Id, source: source.Id, acceptedAdvance: true), default));
        Assert.True(fault.Triggered);
        await using var observer = fixture.Open();
        Assert.Empty(await observer.Payments.ToArrayAsync());
        Assert.Empty(await observer.ClientBalanceTransactions.ToArrayAsync());
        Assert.Empty(await observer.CashOrders.ToArrayAsync());
        Assert.Equal(priorAudit, await observer.ObjectAuditEvents.CountAsync());
        Assert.Equal(PaymentCreateAttempt.Draft, (await observer.PaymentCreateAttempts.SingleAsync()).State);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AdvanceNotify_AfterCommittedDispose_FailureRecoversSameFinancialFact(bool postgres, bool failNotify)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        var actor = await SeedActorAsync(db);
        var client = new Client { Name = "Аванс", ResponsibleUserId = actor };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var api = CheckedNotificationProxy.Create();
        var check = (CheckedNotificationProxy)api;
        var controller = Controller(db, actor, api);
        var attempt = await RegisterAsync(controller);
        check.Before = async method =>
        {
            Assert.Equal(nameof(INotificationService.NotifyAsync), method);
            Assert.Null(db.Database.CurrentTransaction);
            await using var observer = fixture.Open();
            var outcome = await observer.PaymentCreateAttempts.SingleAsync(a => a.Id == attempt.AttemptId);
            Assert.Equal(PaymentCreateAttempt.Committed, outcome.State);
            var payment = await observer.Payments.SingleAsync();
            Assert.Equal(outcome.CanonicalPaymentId, payment.Id);
            Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, payment.AdvanceReviewStatus);
            Assert.Empty(await observer.ClientBalanceTransactions.ToArrayAsync());
            if (failNotify) throw new NotifyFault();
        };
        var command = Command(paid: true, client: client.Id);
        if (failNotify) await Assert.ThrowsAsync<NotifyFault>(() => controller.CommitCreateAttempt(attempt.AttemptId, command, default));
        else Assert.IsType<CreatedResult>(await controller.CommitCreateAttempt(attempt.AttemptId, command, default));
        Assert.Equal(new[] { nameof(INotificationService.NotifyAsync) }, check.Calls);
        var committed = await StateAsync(controller, attempt.AttemptId);
        Assert.Equal(PaymentCreateAttempt.Committed, committed.State);
        var replay = Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await controller.CommitCreateAttempt(attempt.AttemptId, command, default)).Value);
        Assert.Equal(committed.CanonicalPaymentId, replay.Id);
        Assert.Single(check.Calls);
        Assert.Single(await db.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InvoiceNotify_SeesCommittedAggregateAndDisposedTransaction(bool postgres, bool failNotify)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        var actor = await SeedActorAsync(db);
        var invoice = new Invoice { Number = "ATTEMPT-INVOICE", Date = DateTime.Today };
        var invoicePayment = new Payment
        {
            DocumentKind = DocumentKind.Invoice,
            Invoice = invoice,
            Date = DateTime.Today,
            PlannedDate = DateTime.Today,
            Amount = 100m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Responsibles = [new PaymentResponsible { UserId = actor }]
        };
        db.Payments.Add(invoicePayment);
        await db.SaveChangesAsync();
        var api = CheckedNotificationProxy.Create();
        var check = (CheckedNotificationProxy)api;
        var controller = Controller(db, actor, api);
        var attempt = await RegisterAsync(controller);
        check.Before = async method =>
        {
            Assert.Equal(nameof(INotificationService.NotifyInvoiceChangedAsync), method);
            Assert.Null(db.Database.CurrentTransaction);
            await using var observer = fixture.Open();
            Assert.Equal(PaymentCreateAttempt.Committed, (await observer.PaymentCreateAttempts.SingleAsync()).State);
            var aggregate = await observer.Payments.SingleAsync(p => p.Id == invoicePayment.Id);
            Assert.Equal(100m, aggregate.PaidAmount);
            Assert.Equal(PaymentStatus.Completed, aggregate.Status);
            if (failNotify) throw new NotifyFault();
        };
        var command = Command(paid: true, invoice: invoice.Id);
        if (failNotify) await Assert.ThrowsAsync<NotifyFault>(() => controller.CommitCreateAttempt(attempt.AttemptId, command, default));
        else Assert.IsType<CreatedResult>(await controller.CommitCreateAttempt(attempt.AttemptId, command, default));
        Assert.Equal(new[] { nameof(INotificationService.NotifyInvoiceChangedAsync) }, check.Calls);
        Assert.IsType<OkObjectResult>(await controller.CommitCreateAttempt(attempt.AttemptId, command, default));
        Assert.Single(check.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirtyOrAmbientContext_IsRejectedWithoutSavingOrDiscardingForeignChanges(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        var actor = await SeedActorAsync(db);
        var pending = new Client { Name = "Не сохранять" };
        db.Clients.Add(pending);
        Assert.IsType<ConflictObjectResult>(await Controller(db, actor).RegisterCreateAttempt(RegisterBody(), default));
        Assert.Equal(EntityState.Added, db.Entry(pending).State);
        await using var observer = fixture.Open();
        Assert.Empty(await observer.Clients.ToArrayAsync());
        Assert.Empty(await observer.PaymentCreateAttempts.ToArrayAsync());
        db.Entry(pending).State = EntityState.Detached;
        await using var ambient = await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Controller(db, actor).RegisterCreateAttempt(RegisterBody(), default));
        Assert.Same(ambient, db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelHasPrivateScalarMetadata_NoDeleteForeignKeysOrFinancialPayload(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        var model = db.Model.FindEntityType(typeof(PaymentCreateAttempt))!;
        Assert.Empty(model.GetForeignKeys());
        Assert.Empty(model.GetReferencingForeignKeys());
        Assert.Empty(model.GetNavigations());
        Assert.DoesNotContain(model.GetProperties(), p => p.ClrType == typeof(decimal));
        Assert.DoesNotContain(model.GetProperties(), p => p.Name is "Description" or "CommandJson" or "PayloadJson" or "Notes");
        Assert.Null(model.FindProperty("IsDeleted"));
        Assert.Null(model.FindProperty("CreatedByUserId"));
        Assert.Null(model.FindProperty("UpdatedByUserId"));
        Assert.Equal(64, model.FindProperty(nameof(PaymentCreateAttempt.PayloadIdentity))!.GetMaxLength());
        Assert.Equal(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never,
            model.FindProperty(nameof(PaymentCreateAttempt.Id))!.ValueGenerated);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DatabaseConstraints_RejectNullRemainderPrimaryOrNullCommittedCanonical(bool postgres, bool committed)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        var actor = await SeedActorAsync(db);
        var item = new PaymentCreateAttempt
        {
            Id = Guid.NewGuid(),
            ActorUserId = actor,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
        };
        if (committed)
        {
            item.State = PaymentCreateAttempt.Committed;
            item.PayloadIdentity = new string('a', 64);
            item.CommittedAtUtc = DateTimeOffset.UtcNow;
        }
        else item.Operation = "remainder";
        db.PaymentCreateAttempts.Add(item);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateAttemptMetadata_DoesNotCreateBusinessHistory_WithAuditOnOrOff(bool audit)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(useObjectAuditModel: audit);
        var count = audit ? await db.ObjectAuditEvents.CountAsync() : 0;
        var item = new PaymentCreateAttempt
        {
            Id = Guid.NewGuid(),
            ActorUserId = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
        };
        db.PaymentCreateAttempts.Add(item);
        await db.SaveChangesAsync();
        item.State = PaymentCreateAttempt.Cancelled;
        item.CancelledAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        if (audit) Assert.Equal(count, await db.ObjectAuditEvents.CountAsync());
        var entity = db.Model.FindEntityType(typeof(PaymentCreateAttempt))!;
        Assert.Null(entity.FindProperty("IsDeleted"));
        Assert.Null(entity.FindProperty("CreatedByUserId"));
        Assert.Null(entity.FindProperty("UpdatedByUserId"));
        Assert.Single(await db.PaymentCreateAttempts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiryDuringFinancialWork_RollsBackBeforeAttemptCommit(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        var clock = new ControlledTime();
        var advance = new AdvanceClockOnPaymentSave(clock);
        await using var db = fixture.Open(advance);
        var actor = await SeedActorAsync(db);
        var controller = Controller(db, actor, time: clock);
        var attempt = await RegisterAsync(controller);
        advance.Enabled = true;
        Assert.Equal(410, Assert.IsType<ObjectResult>(await controller.CommitCreateAttempt(attempt.AttemptId, Command(), default)).StatusCode);
        Assert.True(advance.Triggered);
        await using var observer = fixture.Open();
        Assert.Empty(await observer.Payments.ToArrayAsync());
        Assert.Equal(PaymentCreateAttempt.Draft, (await observer.PaymentCreateAttempts.SingleAsync()).State);
        Assert.Equal(PaymentCreateAttempt.Cancelled, Assert.IsType<PaymentCreateAttemptCancelOutcomeDto>(Assert.IsType<OkObjectResult>(
            await controller.CancelCreateAttempt(attempt.AttemptId, default)).Value).State);
    }

    [Theory]
    [InlineData("{\"operation\":\"primary\",\"operation\":\"remainder\",\"primaryPaymentId\":1}")]
    [InlineData("{\"Operation\":\"primary\"}")]
    [InlineData("{\"operation\":\"primary\",\"id\":1}")]
    [InlineData("{\"operation\":\"remainder\"}")]
    [InlineData("{\"operation\":\"primary\",\"primaryPaymentId\":1}")]
    [InlineData("{\"operation\":\"primary\",\"sellerBranchId\":1}")]
    [InlineData("{\"operation\":\"primary\",\"sellerCompanyId\":0}")]
    public void RegistrationShape_RejectsAmbiguousOrServerOwnedSelection(string json)
        => Assert.False(PayPlanner.Api.Models.Requests.RegisterPaymentCreateAttemptRequest.TryRead(Element(json), out _));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyKeylessCreate_KeepsAssignmentTail_WithoutRegisteringAttempts(bool postgres)
    {
        // Preserve the existing catalog ID. The user's mandatory-key decision
        // replaces keyless success: the notification tail must not execute at all.
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        var actor = await SeedActorAsync(db);
        var api = CheckedNotificationProxy.Create();
        var check = (CheckedNotificationProxy)api;
        check.Before = _ => throw new InvalidOperationException("A keyless request must not dispatch notifications.");
        var payment = new Payment
        {
            Amount = 100m,
            Date = DateTime.Today,
            PlannedDate = DateTime.Today,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
        };
        Assert.IsType<BadRequestObjectResult>(await Controller(db, actor, api).Create(payment, default));
        Assert.Empty(check.Calls);
        Assert.Empty(await db.Payments.ToArrayAsync());
        Assert.Empty(await db.PaymentCreateAttempts.ToArrayAsync());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostCommitAcknowledgement_PreservesCanonicalOutcome_WithoutSecondFinancialFact(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        var fault = new LostCommitAcknowledgement();
        await using var first = fixture.Open(fault);
        var actor = await SeedActorAsync(first);
        var attempt = await RegisterAsync(Controller(first, actor));
        fault.Enabled = true;
        // The provider committed, then its acknowledgement path failed. No status implies safe retry-create.
        await Assert.ThrowsAnyAsync<Exception>(() => Controller(first, actor).CommitCreateAttempt(attempt.AttemptId, Command(), default));
        Assert.True(fault.Triggered);
        await using var observer = fixture.Open();
        var controller = Controller(observer, actor);
        var state = await StateAsync(controller, attempt.AttemptId);
        Assert.Equal(PaymentCreateAttempt.Committed, state.State);
        var canonical = Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await controller.CommitCreateAttempt(attempt.AttemptId, Command(), default)).Value);
        Assert.Equal(state.CanonicalPaymentId, canonical.Id);
        Assert.Single(await observer.Payments.ToArrayAsync());
        Assert.Equal(PaymentCreateAttempt.Committed, Assert.IsType<PaymentCreateAttemptCancelOutcomeDto>(Assert.IsType<OkObjectResult>(
            await controller.CancelCreateAttempt(attempt.AttemptId, default)).Value).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyUserDatabase_StaleAdminClaimIsNotAnAttemptOwnershipGrant(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        Assert.Empty(await db.Users.ToArrayAsync());
        var controller = Controller(db, 123456);
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        { new Claim(ClaimTypes.NameIdentifier, "123456"), new Claim(ClaimTypes.Role, "admin") }, "Fixture"));
        Assert.IsType<ForbidResult>(await controller.RegisterCreateAttempt(RegisterBody(), default));
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.IsType<UnauthorizedResult>(await controller.RegisterCreateAttempt(RegisterBody(), default));
        Assert.Empty(await db.PaymentCreateAttempts.ToArrayAsync());
        Assert.Empty(await db.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OwnDraftCancel_RemainsOpaqueWhenPrimaryIsHiddenOrDeleted(bool postgres, bool deleted)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        var actor = await SeedActorAsync(db);
        var controller = Controller(db, actor);
        // Historical source without ownership metadata; creation is fixture setup,
        // not the key-required route under test in the remainder scenarios.
        var primary = new Payment
        { Date = DateTime.Today, PlannedDate = DateTime.Today, Amount = 100m, Type = PaymentType.Income, Status = PaymentStatus.Pending };
        db.Payments.Add(primary); await db.SaveChangesAsync();
        var attempt = await RegisterAsync(controller, "remainder", primary: primary.Id);
        if (deleted) Assert.IsType<NoContentResult>(await controller.Delete(primary.Id, default));
        else
        {
            var role = await db.Users.Where(u => u.Id == actor).Select(u => u.RoleId).SingleAsync();
            (await db.RolePermissions.SingleAsync(p => p.RoleId == role && p.Section == RolePermissionCatalog.Payments)).ViewScope = DataVisibilityScope.Own;
            await db.SaveChangesAsync();
        }
        Assert.IsType<NotFoundResult>(await controller.GetById(primary.Id, default));
        Assert.IsType<NotFoundResult>(await controller.GetCreateAttempt(attempt.AttemptId, default));
        var beforePayments = await db.Payments.AsNoTracking().Select(p => p.Id).ToArrayAsync();
        var beforeAudit = await db.ObjectAuditEvents.CountAsync();
        AssertOpaqueCancelled(await controller.CancelCreateAttempt(attempt.AttemptId, default), attempt.AttemptId);
        AssertOpaqueCancelled(await controller.CancelCreateAttempt(attempt.AttemptId, default), attempt.AttemptId);
        Assert.Equal(410, Assert.IsType<ObjectResult>(await controller.CommitCreateAttempt(attempt.AttemptId, Command(), default)).StatusCode);
        Assert.Equal(beforePayments, await db.Payments.AsNoTracking().Select(p => p.Id).ToArrayAsync());
        Assert.Equal(beforeAudit, await db.ObjectAuditEvents.CountAsync());
        Assert.Equal(PaymentCreateAttempt.Cancelled, (await db.PaymentCreateAttempts.AsNoTracking().SingleAsync()).State);
    }

    [Theory]
    [InlineData(false, "company-inactive")]
    [InlineData(true, "company-inactive")]
    [InlineData(false, "registry-removed")]
    [InlineData(true, "registry-removed")]
    [InlineData(false, "company-deleted")]
    [InlineData(true, "company-deleted")]
    [InlineData(false, "branch-inactive")]
    [InlineData(true, "branch-inactive")]
    [InlineData(false, "branch-deleted")]
    [InlineData(true, "branch-deleted")]
    public async Task OwnDraftCancel_DoesNotRequireVanishedSellerSelection(bool postgres, string change)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        var actor = await SeedActorAsync(db);
        var company = new LegalEntity { ShortName = "Своя", ProfileStatus = CompanyProfileStatuses.Active };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        var branch = new CompanyBranch { LegalEntityId = company.Id, Name = "Офис", Status = CompanyProfileStatuses.Active };
        var registry = new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" };
        db.CompanyBranches.Add(branch); db.SystemSettings.Add(registry);
        await db.SaveChangesAsync();
        var controller = Controller(db, actor);
        var attempt = await RegisterAsync(controller, company: company.Id, branch: branch.Id);
        switch (change)
        {
            case "company-inactive": (await db.LegalEntities.SingleAsync(c => c.Id == company.Id)).ProfileStatus = CompanyProfileStatuses.Archived; break;
            case "registry-removed": (await db.SystemSettings.SingleAsync(s => s.Key == registry.Key)).Value = "[]"; break;
            case "company-deleted": db.CompanyBranches.Remove(branch); db.LegalEntities.Remove(company); break;
            case "branch-inactive": (await db.CompanyBranches.SingleAsync(b => b.Id == branch.Id)).Status = CompanyProfileStatuses.Archived; break;
            case "branch-deleted": db.CompanyBranches.Remove(branch); break;
            default: throw new InvalidOperationException("Unknown fixture change.");
        }
        await db.SaveChangesAsync();
        Assert.IsType<ForbidResult>(await controller.GetCreateAttempt(attempt.AttemptId, default));
        var beforeAudit = await db.ObjectAuditEvents.CountAsync();
        AssertOpaqueCancelled(await controller.CancelCreateAttempt(attempt.AttemptId, default), attempt.AttemptId);
        AssertOpaqueCancelled(await controller.CancelCreateAttempt(attempt.AttemptId, default), attempt.AttemptId);
        Assert.Equal(410, Assert.IsType<ObjectResult>(await controller.CommitCreateAttempt(attempt.AttemptId,
            Command(company: company.Id, branch: branch.Id), default)).StatusCode);
        Assert.Empty(await db.Payments.ToArrayAsync());
        Assert.Equal(beforeAudit, await db.ObjectAuditEvents.CountAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CommittedCancel_NeverDisclosesHiddenOrDeletedCanonicalId(bool postgres, bool deleted)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        var actor = await SeedActorAsync(db);
        var controller = Controller(db, actor);
        var attempt = await RegisterAsync(controller);
        var payment = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await controller.CommitCreateAttempt(attempt.AttemptId, Command(), default)).Value);
        if (deleted) Assert.IsType<NoContentResult>(await controller.Delete(payment.Id, default));
        else
        {
            var role = await db.Users.Where(u => u.Id == actor).Select(u => u.RoleId).SingleAsync();
            (await db.RolePermissions.SingleAsync(p => p.RoleId == role && p.Section == RolePermissionCatalog.Payments)).ViewScope = DataVisibilityScope.Own;
            await db.SaveChangesAsync();
        }
        Assert.IsType<NotFoundResult>(await controller.GetById(payment.Id, default));
        Assert.IsType<NotFoundResult>(await controller.CancelCreateAttempt(attempt.AttemptId, default));
        var saved = await db.PaymentCreateAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(PaymentCreateAttempt.Committed, saved.State);
        Assert.Equal(payment.Id, saved.CanonicalPaymentId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CommittedCancel_StillRequiresVisibleExistingPrimary(bool postgres, bool deleted)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        var actor = await SeedActorAsync(db);
        var controller = Controller(db, actor);
        // Historical source without ownership metadata; creation is fixture setup,
        // not the key-required route under test in the remainder scenarios.
        var primary = new Payment
        { Date = DateTime.Today, PlannedDate = DateTime.Today, Amount = 100m, Type = PaymentType.Income, Status = PaymentStatus.Pending };
        db.Payments.Add(primary); await db.SaveChangesAsync();
        // A historical committed remainder can lack a structural pair. Keep
        // exercising its opaque cancel/visibility contract, not a new split of
        // this deliberately unpaid source (now rejected by the financial path).
        var attempt = await RegisterAsync(controller);
        var canonical = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await controller.CommitCreateAttempt(attempt.AttemptId, Command(), default)).Value);
        var historical = await db.PaymentCreateAttempts.SingleAsync(p => p.Id == attempt.AttemptId);
        historical.Operation = "remainder"; historical.PrimaryPaymentId = primary.Id; await db.SaveChangesAsync();
        if (deleted) Assert.IsType<NoContentResult>(await controller.Delete(primary.Id, default));
        else
        {
            db.PaymentResponsibles.Add(new PaymentResponsible { PaymentId = canonical.Id, UserId = actor });
            var role = await db.Users.Where(u => u.Id == actor).Select(u => u.RoleId).SingleAsync();
            (await db.RolePermissions.SingleAsync(p => p.RoleId == role && p.Section == RolePermissionCatalog.Payments)).ViewScope = DataVisibilityScope.Own;
            await db.SaveChangesAsync();
        }
        Assert.IsType<OkObjectResult>(await controller.GetById(canonical.Id, default));
        Assert.IsType<NotFoundResult>(await controller.GetById(primary.Id, default));
        Assert.IsType<NotFoundResult>(await controller.CancelCreateAttempt(attempt.AttemptId, default));
        Assert.Equal(PaymentCreateAttempt.Committed, (await db.PaymentCreateAttempts.AsNoTracking().SingleAsync()).State);
    }

    [Theory]
    [InlineData(false, "view")]
    [InlineData(true, "view")]
    [InlineData(false, "create")]
    [InlineData(true, "create")]
    [InlineData(false, "active")]
    [InlineData(true, "active")]
    public async Task OwnUncommittedCancel_StillRequiresCurrentGlobalPermissionAndActiveActor(bool postgres, string revoke)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        var actor = await SeedActorAsync(db);
        var controller = Controller(db, actor);
        var attempt = await RegisterAsync(controller);
        var user = await db.Users.SingleAsync(u => u.Id == actor);
        var permission = await db.RolePermissions.SingleAsync(p => p.RoleId == user.RoleId && p.Section == RolePermissionCatalog.Payments);
        if (revoke == "view") permission.CanView = false;
        else if (revoke == "create") permission.CanCreate = false;
        else if (revoke == "active") user.IsActive = false;
        else throw new InvalidOperationException("Unknown permission change.");
        await db.SaveChangesAsync();
        Assert.IsType<ForbidResult>(await controller.CancelCreateAttempt(attempt.AttemptId, default));
        Assert.Equal(PaymentCreateAttempt.Draft, (await db.PaymentCreateAttempts.AsNoTracking().SingleAsync()).State);
        Assert.Empty(await db.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpWireContract_ExplicitNullsAndOpaqueCancel_WithProductionJsonProfile(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        await using var db = fixture.Open();
        var actor = await SeedActorAsync(db);
        using var server = new TestServer(new WebHostBuilder().UseEnvironment("Testing")
            .UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging(); services.AddSingleton(db);
                services.AddSingleton<IFormRequirementsService>(new NoopFormRequirements());
                services.AddAuthentication("attempt-fixture").AddScheme<AuthenticationSchemeOptions, AttemptFixtureAuthentication>("attempt-fixture", _ => { });
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(PaymentsController).Assembly)
                    .AddJsonOptions(o => ConfigureProductionJson(o.JsonSerializerOptions));
            }).Configure(app =>
            {
                app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            }));
        var actualJson = server.Services.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        Assert.Equal(JsonIgnoreCondition.WhenWritingNull, actualJson.DefaultIgnoreCondition);
        Assert.Same(ReferenceHandler.IgnoreCycles, actualJson.ReferenceHandler);
        Assert.Contains(actualJson.Converters, converter => converter is JsonStringEnumConverter);
        Assert.Empty(JsonSerializer.SerializeToElement(new { optional = (int?)null }, actualJson).EnumerateObject());
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", actor.ToString(CultureInfo.InvariantCulture));
        using var register = await client.PostAsJsonAsync("/api/payments/create-attempts", new
        { operation = "primary", sellerCompanyId = (int?)null, sellerBranchId = (int?)null, primaryPaymentId = (int?)null });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        Assert.True(register.Headers.CacheControl?.NoStore == true);
        var registered = Element(await register.Content.ReadAsStringAsync());
        AssertFullStateNullIds(registered);
        var id = registered.GetProperty("attemptId").GetGuid();
        using var get = await client.GetAsync($"/api/payments/create-attempts/{id:D}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.True(get.Headers.CacheControl?.NoStore == true);
        AssertFullStateNullIds(Element(await get.Content.ReadAsStringAsync()));
        using var cancel = await client.PostAsync($"/api/payments/create-attempts/{id:D}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.True(cancel.Headers.CacheControl?.NoStore == true);
        AssertOpaqueCancelJson(Element(await cancel.Content.ReadAsStringAsync()), id, PaymentCreateAttempt.Cancelled, null);
        Assert.Empty(await db.Payments.ToArrayAsync());
    }

    private static void ConfigureProductionJson(JsonSerializerOptions options)
    {
        // Exact three Program.cs MVC customizations; framework Web naming defaults remain in force.
        options.Converters.Add(new JsonStringEnumConverter());
        options.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    }

    private static void AssertFullStateNullIds(JsonElement state)
    {
        foreach (var name in new[] { "sellerCompanyId", "sellerBranchId", "primaryPaymentId", "canonicalPaymentId" })
        {
            Assert.True(state.TryGetProperty(name, out var value), "Required wire key missing: " + name);
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
        }
    }

    private static void AssertOpaqueCancelled(IActionResult result, Guid id)
    {
        var outcome = Assert.IsType<PaymentCreateAttemptCancelOutcomeDto>(Assert.IsType<OkObjectResult>(result).Value);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ConfigureProductionJson(options);
        AssertOpaqueCancelJson(JsonSerializer.SerializeToElement(outcome, options), id, PaymentCreateAttempt.Cancelled, null);
    }

    private static void AssertOpaqueCancelJson(JsonElement outcome, Guid id, string state, int? canonical)
    {
        Assert.Equal(new[] { "attemptId", "canonicalPaymentId", "state" }, outcome.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(id, outcome.GetProperty("attemptId").GetGuid());
        Assert.Equal(state, outcome.GetProperty("state").GetString());
        Assert.True(outcome.TryGetProperty("canonicalPaymentId", out var value));
        if (canonical.HasValue) Assert.Equal(canonical.Value, value.GetInt32());
        else Assert.Equal(JsonValueKind.Null, value.ValueKind);
    }

    private sealed class AttemptFixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Fixture-User"], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            { new Claim(ClaimTypes.NameIdentifier, id.ToString(CultureInfo.InvariantCulture)), new Claim(ClaimTypes.Name, "Fixture") }, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private async Task<Fixture> CreateFixtureAsync(bool postgres)
    {
        if (postgres)
        {
            var lease = await template.CreateDatabaseAsync();
            return new Fixture(true, lease.ConnectionString, lease);
        }
        var connectionString = $"Data Source=attempt_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=1";
        var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        try
        {
            await using var source = await SqliteTestContextFactory.CreateAsync();
            ((SqliteConnection)source.Database.GetDbConnection()).BackupDatabase(anchor);
            return new Fixture(false, connectionString, anchor);
        }
        catch { await anchor.DisposeAsync(); throw; }
    }

    private sealed class Fixture(bool postgres, string connectionString, IAsyncDisposable lease) : IAsyncDisposable
    {
        public PaymentContext Open(IInterceptor? interceptor = null)
        {
            if (postgres)
            {
                var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connectionString);
                if (interceptor is not null) options.AddInterceptors(interceptor);
                return new PostgreSqlPaymentContext(options.Options);
            }
            var sqlite = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connectionString);
            if (interceptor is not null) sqlite.AddInterceptors(interceptor);
            return new PaymentContext(sqlite.Options);
        }
        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }

    private static async Task<int> SeedActorAsync(PaymentContext db)
    {
        var role = new Role { Name = "attempt-fixture-" + Guid.NewGuid().ToString("N"), Description = "Fixture" };
        db.Roles.Add(role);
        var user = new User
        {
            Email = $"attempt-{Guid.NewGuid():N}@fixture.local",
            FullName = "Пользователь",
            PasswordHash = "unused",
            IsActive = true,
            IsApproved = true,
            Role = role
        };
        db.Users.Add(user);
        foreach (var section in new[] { RolePermissionCatalog.Payments, RolePermissionCatalog.Clients,
                     RolePermissionCatalog.LegalEntities, RolePermissionCatalog.Accounts, RolePermissionCatalog.Cases })
            db.RolePermissions.Add(new RolePermission
            {
                Role = role,
                Section = section,
                CanView = true,
                CanCreate = true,
                CanEdit = true,
                CanDelete = true,
                ViewScope = DataVisibilityScope.All
            });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static PaymentsController Controller(PaymentContext db, int actor, INotificationService? notification = null, TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        services.AddOptions<JsonOptions>().Configure(o => ConfigureProductionJson(o.JsonSerializerOptions));
        if (time is not null) services.AddSingleton(time);
        return new PaymentsController(db, new NoopFormRequirements(), notification)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = services.BuildServiceProvider(),
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                { new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Name, "Fixture") }, "Fixture")),
                }
            },
        };
    }

    private static JsonElement RegisterBody(string operation = "primary", int? company = null, int? branch = null, int? primary = null)
        => Element(JsonSerializer.Serialize(new { operation, sellerCompanyId = company, sellerBranchId = branch, primaryPaymentId = primary }));
    private static async Task<PaymentCreateAttemptStateDto> RegisterAsync(PaymentsController controller,
        string operation = "primary", int? company = null, int? branch = null, int? primary = null)
        => Assert.IsType<PaymentCreateAttemptStateDto>(Assert.IsType<CreatedResult>(await controller.RegisterCreateAttempt(
            RegisterBody(operation, company, branch, primary), default)).Value);
    private static async Task<PaymentCreateAttemptStateDto> StateAsync(PaymentsController controller, Guid attempt)
        => Assert.IsType<PaymentCreateAttemptStateDto>(Assert.IsType<OkObjectResult>(await controller.GetCreateAttempt(attempt, default)).Value);
    private static JsonElement Command(string description = "Платёж", bool paid = false, int? client = null,
        int? source = null, int? invoice = null, int? company = null, int? branch = null, bool acceptedAdvance = false)
    {
        var command = new JsonObject
        {
            ["date"] = "2026-10-26",
            ["plannedDate"] = "2026-10-26",
            ["amount"] = 100m,
            ["paidAmount"] = paid ? 100m : 0m,
            ["type"] = "Income",
            ["status"] = paid ? "Completed" : "Pending",
            ["description"] = description,
            ["isPaid"] = paid,
            ["notes"] = "",
            ["systemNotes"] = "",
            ["rescheduleCount"] = 0,
        };
        if (client.HasValue) command["clientId"] = client;
        if (source.HasValue) command["paymentSourceId"] = source;
        if (invoice.HasValue) command["invoiceId"] = invoice;
        if (company.HasValue) command["sellerCompanyId"] = company;
        if (branch.HasValue) command["sellerBranchId"] = branch;
        if (acceptedAdvance) command["advanceReviewStatus"] = "AcceptedAsAdvance";
        return Element(command.ToJsonString());
    }
    private static JsonElement Element(string raw) { using var document = JsonDocument.Parse(raw); return document.RootElement.Clone(); }

    private sealed class HoldPrimaryDelete : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Payment>().Any(e => e.State == EntityState.Deleted
                || e.State == EntityState.Modified && e.Property<bool>("IsDeleted").CurrentValue))
            { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); }
            return result;
        }
    }

    private sealed class NoopFormRequirements : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }
    private sealed class ControlledTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class HoldOutcomeSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<PaymentCreateAttempt>()
                .Any(e => e.State == EntityState.Modified && e.Entity.State == PaymentCreateAttempt.Committed))
            {
                Entered.TrySetResult(true);
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            }
            return result;
        }
    }
    private sealed class OutcomeSaveFault : Exception { }
    private sealed class NotifyFault : Exception { }
    private sealed class CommitAcknowledgementFault : Exception { }
    private sealed class LostCommitAcknowledgement : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public bool Triggered { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            if (Enabled) { Triggered = true; throw new CommitAcknowledgementFault(); }
            return Task.CompletedTask;
        }
    }
    private sealed class AdvanceClockOnPaymentSave(ControlledTime time) : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public bool Triggered { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
        {
            if (Enabled && !Triggered && eventData.Context!.ChangeTracker.Entries<Payment>().Any(p => p.Entity.Id > 0))
            { Triggered = true; time.Now = time.Now.AddHours(25); }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class RejectOutcomeSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public bool Triggered { get; private set; }
        public int PriorAuditCount { get; set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<PaymentCreateAttempt>()
                .Any(e => e.State == EntityState.Modified && e.Entity.State == PaymentCreateAttempt.Committed))
            {
                var db = Assert.IsAssignableFrom<PaymentContext>(eventData.Context);
                Assert.NotNull(db.Database.CurrentTransaction);
                Assert.Single(await db.Payments.AsNoTracking().ToArrayAsync(ct));
                Assert.Equal(100m, (await db.ClientBalanceTransactions.AsNoTracking().SingleAsync(ct)).Amount);
                Assert.Equal(100m, (await db.CashOrders.AsNoTracking().SingleAsync(ct)).Amount);
                Assert.True(await db.ObjectAuditEvents.CountAsync(ct) > PriorAuditCount);
                Triggered = true;
                throw new OutcomeSaveFault();
            }
            return result;
        }
    }
    public class CheckedNotificationProxy : DispatchProxy
    {
        public Func<string, Task>? Before { get; set; }
        public List<string> Calls { get; } = [];
        public static INotificationService Create() => DispatchProxy.Create<INotificationService, CheckedNotificationProxy>();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var name = method?.Name ?? throw new InvalidOperationException();
            Calls.Add(name);
            if (name == nameof(INotificationService.NotifyAsync)) return CheckBool(name);
            if (name is nameof(INotificationService.NotifyInvoiceChangedAsync) or nameof(INotificationService.NotifyPaymentAssignedAsync)) return Check(name);
            throw new InvalidOperationException("Unexpected notification in attempt fixture: " + name);
        }
        private async Task Check(string name) { if (Before is not null) await Before(name); }
        private async Task<bool> CheckBool(string name) { await Check(name); return true; }
    }
}
