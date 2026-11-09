using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class TaskBulkBillingTests
{
    [Theory]
    [InlineData("wrongCase")]
    [InlineData("staleTask")]
    [InlineData("staleInvoice")]
    [InlineData("noAccountPermission")]
    [InlineData("noTaskPermission")]
    [InlineData("success")]
    public async Task ReconciliationLinksTheWholeSelectionOrLeavesItUntouched(string scenario)
    {
        await using var fixture = await Fixture.CreateAsync(admin: scenario is not ("noAccountPermission" or "noTaskPermission"),
            canEditOthers: scenario != "noTaskPermission", canEditAccounts: scenario == "noTaskPermission");
        var first = fixture.AddTask();
        var second = fixture.AddTask("done");
        var invoice = new Payment
        {
            ClientId = first.ClientId,
            ClientCaseId = first.ClientCaseId,
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Account = "SYNTHETIC-RECONCILIATION",
            Amount = 500m,
            Description = "Existing invoice content",
        };
        fixture.Db.Payments.Add(invoice);
        if (scenario == "staleInvoice")
        {
            invoice.Invoice = new Invoice { Number = "SYNTHETIC-RECONCILIATION", Date = DateTime.UtcNow.Date };
        }
        if (scenario == "wrongCase") second.ClientCase = new ClientCase { ClientId = first.ClientId, Title = "Other case" };
        if (scenario == "noTaskPermission") second.AuthorUserId = second.AssigneeUserId = fixture.OtherUserId;
        await fixture.SaveAsync();

        var billingRequest = await fixture.RequestAsync([first.Id, second.Id]);
        var invoiceVersion = await fixture.Db.Payments.AsNoTracking().Where(item => item.Id == invoice.Id)
            .Select(item => EF.Property<DateTime?>(item, "UpdatedAt")).SingleAsync();
        if (scenario == "staleInvoice")
        {
            // An invoice composition change can advance only the aggregate, leaving the payment header untouched.
            await fixture.Db.Invoices.Where(item => item.Id == invoice.InvoiceId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.UpdatedAt, DateTime.UtcNow.AddMinutes(1)));
        }
        var link = new BulkLinkTaskBillingInvoiceRequest
        {
            InvoiceId = invoice.Id,
            ConfirmedIncludedInInvoice = true,
            ExpectedUpdatedAtById = billingRequest.ExpectedUpdatedAtById,
            ExpectedInvoiceUpdatedAt = invoiceVersion,
        };
        if (scenario == "staleTask") link.ExpectedUpdatedAtById![second.Id] = Token(DateTime.UtcNow.AddDays(-30));
        var response = await fixture.Controller().BulkLinkTaskBillingInvoice(link, CancellationToken.None);
        switch (scenario)
        {
            case "success":
                var result = Assert.IsType<TaskJournalItemDto[]>(Assert.IsType<OkObjectResult>(response.Result).Value);
                Assert.Equal(new[] { first.Id, second.Id }, result.Select(item => item.Id));
                Assert.All(result, item => Assert.Equal(invoice.Id, item.BillingInvoiceId));
                fixture.Db.ChangeTracker.Clear();
                Assert.IsType<OkObjectResult>((await fixture.Controller().BulkLinkTaskBillingInvoice(link, CancellationToken.None)).Result);
                Assert.Equal(2, await fixture.Db.TaskJournalComments.CountAsync());
                Assert.All(await fixture.Db.TaskJournalItems.AsNoTracking().ToArrayAsync(), task => Assert.Equal(invoice.Id, task.BillingInvoiceId));
                Assert.Equal("done", (await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync(task => task.Id == second.Id)).Status);
                break;
            case "wrongCase":
                Assert.IsType<BadRequestObjectResult>(response.Result);
                break;
            case "noAccountPermission":
            case "noTaskPermission":
                Assert.IsType<ForbidResult>(response.Result);
                break;
            default:
                Assert.IsType<ConflictObjectResult>(response.Result);
                break;
        }
        if (scenario != "success")
        {
            Assert.Empty(await fixture.Db.TaskJournalComments.ToArrayAsync());
            Assert.All(await fixture.Db.TaskJournalItems.AsNoTracking().ToArrayAsync(), task => Assert.Null(task.BillingInvoiceId));
        }
        var savedInvoice = await fixture.Db.Payments.AsNoTracking().SingleAsync(item => item.Id == invoice.Id);
        Assert.Equal(500m, savedInvoice.Amount);
        Assert.Equal("Existing invoice content", savedInvoice.Description);
    }

    [Theory]
    [InlineData(false, 8, 1)]
    [InlineData(true, 9, 0)]
    public async Task CompletionScopeUsesCurrentStatusesAndNeverChangesCancelledOrUnknown(bool includeCompleted, int changed, int excluded)
    {
        await using var fixture = await Fixture.CreateAsync();
        var statuses = new[] { "new", "inProgress", "waiting", "onApproval", "approved", "revision", "overdueApproval", "done", "cancelled", "unknown", "new" };
        var tasks = statuses.Select(status => fixture.AddTask(status)).ToArray();
        await fixture.SaveAsync();
        var request = await fixture.RequestAsync(tasks.Select(task => task.Id));
        request.IncludeCompleted = includeCompleted;
        var result = await fixture.ApplyAsync(request);

        Assert.Equal(changed, result.Updated);
        Assert.Equal(excluded, result.Items.Count(row => row.Reason == "completedExcluded"));
        Assert.Equal("cancelled", result.Items.Single(row => row.Id == tasks[8].Id).Reason);
        Assert.Equal("unsupportedStatus", result.Items.Single(row => row.Id == tasks[9].Id).Reason);
        Assert.Equal(1, fixture.Realtime.ChangeCount);
        var stored = await fixture.Db.TaskJournalItems.AsNoTracking().OrderBy(task => task.Id).ToArrayAsync();
        Assert.Equal(statuses, stored.Select(task => task.Status));
        Assert.All(stored, task => Assert.Equal("Existing result", task.Result));
        Assert.Equal(changed, await fixture.Db.TaskJournalComments.CountAsync());
    }

    [Theory]
    [InlineData("legacy", "internal")]
    [InlineData("internal", "billable")]
    [InlineData("billable", "internal")]
    public async Task ChangesOnlyClassificationAndDecisionPreservingTaskAndPayoutSettings(string source, string target)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask("done", source);
        task.BillingDecisionAt = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        task.ContractorPaymentEnabled = true;
        task.ContractorPaymentAmount = 987.65m;
        task.ContractorPaymentDueDate = DateTime.UtcNow.Date.AddDays(3);
        task.ContractorPaymentOnResult = false;
        task.ContractorPaymentResultDelayDays = 5;
        await fixture.SaveAsync();
        var before = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        var request = await fixture.RequestAsync([task.Id], target);
        request.IncludeCompleted = true;
        var result = await fixture.ApplyAsync(request);
        var after = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();

        Assert.Equal(1, result.Updated);
        Assert.Equal(target, after.ClientBillingClassification);
        Assert.NotEqual(before.BillingDecisionAt, after.BillingDecisionAt);
        Assert.Equal(before.ClientId, after.ClientId);
        Assert.Equal(before.LegalEntityId, after.LegalEntityId);
        Assert.Equal(before.ClientCaseId, after.ClientCaseId);
        Assert.Equal(before.Title, after.Title);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Result, after.Result);
        Assert.Equal(before.DueAt, after.DueAt);
        Assert.Equal(before.ContractorPaymentId, after.ContractorPaymentId);
        Assert.Equal(before.ContractorPaymentEnabled, after.ContractorPaymentEnabled);
        Assert.Equal(before.ContractorPaymentAmount, after.ContractorPaymentAmount);
        Assert.Equal(before.ContractorPaymentDueDate, after.ContractorPaymentDueDate);
        Assert.Equal(before.ContractorPaymentOnResult, after.ContractorPaymentOnResult);
        Assert.Equal(before.ContractorPaymentResultDelayDays, after.ContractorPaymentResultDelayDays);
        Assert.Empty(await fixture.Db.Payments.ToArrayAsync());
        var history = await fixture.Db.TaskJournalComments.SingleAsync();
        Assert.Contains("previousClassification", history.MetadataJson);
        Assert.Contains("2025-01-02T03:04:05", history.MetadataJson);
        Assert.Equal(Token(after.UpdatedAt), Assert.Single(result.Items).UpdatedAt);
    }

    [Theory]
    [InlineData("invoice")]
    [InlineData("contract")]
    [InlineData("Fixed")]
    [InlineData("Actual")]
    [InlineData("unexpected-mode")]
    [InlineData(" ")]
    public async Task RawFinancialLinksAreProtectedBeforeNoOp(string kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask(classification: TaskClientBillingClassification.Internal);
        if (kind == "invoice") task.BillingInvoice = new Payment { Amount = 1m, Status = PaymentStatus.Cancelled };
        else if (kind == "contract") task.BillingContract = new Contract { Number = "SYNTHETIC" };
        else task.BillingModeSnapshot = kind;
        await fixture.SaveAsync();
        var before = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        var result = await fixture.ApplyAsync(await fixture.RequestAsync([task.Id]));
        var after = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();

        Assert.Equal("skipped", Assert.Single(result.Items).Outcome);
        Assert.Equal(kind == "invoice" ? "invoiced" : "contractual", Assert.Single(result.Items).Reason);
        Assert.Equal(before.ClientBillingClassification, after.ClientBillingClassification);
        Assert.Equal(before.BillingDecisionAt, after.BillingDecisionAt);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.BillingInvoiceId, after.BillingInvoiceId);
        Assert.Equal(before.BillingContractId, after.BillingContractId);
        Assert.Equal(before.BillingModeSnapshot, after.BillingModeSnapshot);
        Assert.Empty(await fixture.Db.TaskJournalComments.ToArrayAsync());
        Assert.Equal(0, fixture.Realtime.ChangeCount);
    }

    [Theory]
    [InlineData("amount", false)]
    [InlineData("flag", false)]
    [InlineData("status", false)]
    [InlineData("date", false)]
    [InlineData("lastDate", false)]
    [InlineData("amount", true)]
    [InlineData("flag", true)]
    [InlineData("status", true)]
    public async Task ExistingPaymentFactSkipsWholeTaskEvenWhenTargetAlreadyMatches(string fact, bool markerOnly)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask("done", TaskClientBillingClassification.Internal);
        await fixture.SaveAsync();
        var payment = new Payment { Amount = 100m, Status = PaymentStatus.Pending, SystemNotes = $"[PayPlanner:TaskContractorPayment]:{task.Id}\nSynthetic payout" };
        if (fact == "amount") payment.PaidAmount = 1m;
        else if (fact == "flag") payment.IsPaid = true;
        else if (fact == "status") payment.Status = PaymentStatus.Completed;
        else if (fact == "date") payment.PaidDate = DateTime.UtcNow.Date;
        else payment.LastPaymentDate = DateTime.UtcNow.Date;
        fixture.Db.Payments.Add(payment);
        if (!markerOnly)
        {
            var tracked = await fixture.Db.TaskJournalItems.SingleAsync();
            tracked.ContractorPayment = payment;
        }
        await fixture.SaveAsync();
        var before = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        var paymentBefore = await fixture.Db.Payments.AsNoTracking().SingleAsync();
        var request = await fixture.RequestAsync([task.Id]);
        request.IncludeCompleted = true;
        var result = await fixture.ApplyAsync(request);
        var after = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        var paymentAfter = await fixture.Db.Payments.AsNoTracking().SingleAsync();

        Assert.Equal("skipped", Assert.Single(result.Items).Outcome);
        Assert.Equal("paid", Assert.Single(result.Items).Reason);
        Assert.Equal(before.ClientBillingClassification, after.ClientBillingClassification);
        Assert.Equal(before.BillingDecisionAt, after.BillingDecisionAt);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.ContractorPaymentId, after.ContractorPaymentId);
        Assert.Equal(paymentBefore.PaidAmount, paymentAfter.PaidAmount);
        Assert.Equal(paymentBefore.IsPaid, paymentAfter.IsPaid);
        Assert.Equal(paymentBefore.Status, paymentAfter.Status);
        Assert.Equal(paymentBefore.PaidDate, paymentAfter.PaidDate);
        Assert.Equal(paymentBefore.LastPaymentDate, paymentAfter.LastPaymentDate);
        Assert.Empty(await fixture.Db.TaskJournalComments.ToArrayAsync());
        Assert.Equal(0, fixture.Realtime.ChangeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingUnpaidPayoutIsExplicitlyOutsideFirstSubset(bool markerOnly)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask();
        await fixture.SaveAsync();
        var payment = new Payment { Amount = 100m, Status = PaymentStatus.Pending, SystemNotes = $"[PayPlanner:TaskContractorPayment]:{task.Id}\nSynthetic payout" };
        fixture.Db.Payments.Add(payment);
        if (!markerOnly) (await fixture.Db.TaskJournalItems.SingleAsync()).ContractorPayment = payment;
        await fixture.SaveAsync();
        var before = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        var result = await fixture.ApplyAsync(await fixture.RequestAsync([task.Id]));
        var after = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        Assert.Equal("contractorPaymentProtected", Assert.Single(result.Items).Reason);
        Assert.Equal(before.ClientBillingClassification, after.ClientBillingClassification);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.BillingDecisionAt, after.BillingDecisionAt);
        Assert.Equal(before.ContractorPaymentId, after.ContractorPaymentId);
        Assert.Empty(await fixture.Db.TaskJournalComments.ToArrayAsync());
        Assert.Equal(0, fixture.Realtime.ChangeCount);
    }

    [Theory]
    [InlineData(false, false, true, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(true, false, false, false, true)]
    [InlineData(false, true, false, true, true)]
    public async Task CompletedResultPayoutSettingsProtectLegacyDateBeforeNoOpAndDelayedBackfill(
        bool includeCompleted, bool targetMatches, bool external, bool nullUpdatedAt, bool invalidSettings)
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = targetMatches ? TaskClientBillingClassification.Internal : TaskClientBillingClassification.Billable;
        var task = fixture.AddTask("done", source);
        task.ContractorPaymentEnabled = true;
        task.ContractorPaymentOnResult = true;
        task.ContractorPaymentAmount = invalidSettings ? null : 123.45m;
        task.ContractorPaymentResultDelayDays = invalidSettings ? 400 : 5;
        if (invalidSettings) task.AssigneeUserId = null;
        (await fixture.Db.Users.SingleAsync(user => user.Id == fixture.UserId)).IsExternalContractor = external;
        await fixture.SaveAsync();
        var createdAt = new DateTime(2023, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        DateTime? updatedAt = nullUpdatedAt ? null : new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"TaskJournalItems\" SET \"CreatedAt\" = {createdAt}, \"UpdatedAt\" = {updatedAt} WHERE \"Id\" = {task.Id}");
        var before = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        var scalarBefore = ScalarSnapshot(fixture.Db, before);
        var request = await fixture.RequestAsync([task.Id]);
        request.IncludeCompleted = includeCompleted;
        var response = await fixture.ApplyAsync(request);
        var after = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();

        Assert.Equal("skipped", Assert.Single(response.Items).Outcome);
        Assert.Equal("contractorPaymentProtected", Assert.Single(response.Items).Reason);
        Assert.Equal(0, response.Updated);
        Assert.Equal(0, response.Unchanged);
        Assert.Equal(scalarBefore, ScalarSnapshot(fixture.Db, after));
        Assert.Empty(await fixture.Db.TaskJournalComments.ToArrayAsync());
        Assert.Empty(await fixture.Db.Payments.ToArrayAsync());
        Assert.Equal(0, fixture.Realtime.ChangeCount);

        if (!invalidSettings)
        {
            // A later actor flag change must not use the attempted bulk decision's date.
            (await fixture.Db.Users.SingleAsync(user => user.Id == fixture.UserId)).IsExternalContractor = true;
            await fixture.SaveAsync();
            Assert.Equal(1, await TaskContractorPaymentBackfillService.EnsureGeneratedPaymentDetailsAsync(fixture.Db));
            var payment = await fixture.Db.Payments.AsNoTracking().SingleAsync();
            var expectedDate = (before.UpdatedAt ?? before.CreatedAt).Date.AddDays(5);
            Assert.Equal(expectedDate, payment.Date);
            Assert.Equal(expectedDate, payment.PlannedDate);
            Assert.Equal(123.45m, payment.Amount);
        }
    }

    [Theory]
    [InlineData("inProgress", true)]
    [InlineData("done", false)]
    public async Task UnfinishedResultPayoutAndCompletedFixedDateRemainInAllowedSubset(string status, bool onResult)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask(status);
        task.ContractorPaymentEnabled = true;
        task.ContractorPaymentOnResult = onResult;
        task.ContractorPaymentAmount = 123.45m;
        task.ContractorPaymentResultDelayDays = onResult ? 5 : null;
        task.ContractorPaymentDueDate = onResult ? null : new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        await fixture.SaveAsync();
        var before = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        var request = await fixture.RequestAsync([task.Id]);
        request.IncludeCompleted = true;
        var response = await fixture.ApplyAsync(request);
        var after = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();

        Assert.Equal(1, response.Updated);
        Assert.Equal(TaskClientBillingClassification.Internal, after.ClientBillingClassification);
        Assert.Equal(before.ContractorPaymentDueDate, after.ContractorPaymentDueDate);
        Assert.Equal(before.ContractorPaymentAmount, after.ContractorPaymentAmount);
        Assert.Equal(before.ContractorPaymentOnResult, after.ContractorPaymentOnResult);
        Assert.Equal(before.ContractorPaymentResultDelayDays, after.ContractorPaymentResultDelayDays);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Result, after.Result);
        Assert.Null(after.ContractorPaymentId);
        Assert.Empty(await fixture.Db.Payments.ToArrayAsync());
        Assert.Single(await fixture.Db.TaskJournalComments.ToArrayAsync());
    }

    [Fact]
    public async Task MarkerParserUsesWholeIdsAndEveryExistingMarkerWithoutLinkingTheTask()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tasks = Enumerable.Range(0, 12).Select(_ => fixture.AddTask()).ToArray();
        await fixture.SaveAsync();
        var one = tasks[0];
        var eleven = tasks.Single(task => task.Id == int.Parse(one.Id.ToString(CultureInfo.InvariantCulture) + "1", CultureInfo.InvariantCulture));
        var another = tasks[1];
        fixture.Db.Payments.Add(new Payment
        {
            Amount = 100m,
            PaidAmount = 1m,
            SystemNotes = $"Legacy note\n[PayPlanner:TaskContractorPayment]:{eleven.Id}\nMore text\n[PayPlanner:TaskContractorPayment]:{another.Id}\n[PayPlanner:TaskContractorPayment]:not-a-number",
        });
        await fixture.SaveAsync();
        var result = await fixture.ApplyAsync(await fixture.RequestAsync([one.Id, eleven.Id, another.Id]));
        Assert.Equal("updated", result.Items.Single(row => row.Id == one.Id).Outcome);
        Assert.Equal("paid", result.Items.Single(row => row.Id == eleven.Id).Reason);
        Assert.Equal("paid", result.Items.Single(row => row.Id == another.Id).Reason);
        Assert.All(await fixture.Db.TaskJournalItems.AsNoTracking().ToArrayAsync(), task => Assert.Null(task.ContractorPaymentId));
    }

    [Fact]
    public async Task DamagedMarkerDoesNotRemoveProtectionFromItsRawPayoutLink()
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask();
        task.ContractorPayment = new Payment { Amount = 100m, PaidAmount = 1m, SystemNotes = "[PayPlanner:TaskContractorPayment]:999999999999999999999-invalid" };
        await fixture.SaveAsync();
        var result = await fixture.ApplyAsync(await fixture.RequestAsync([task.Id]));
        Assert.Equal("paid", Assert.Single(result.Items).Reason);
        Assert.Empty(await fixture.Db.TaskJournalComments.ToArrayAsync());
    }

    [Fact]
    public async Task PartialResultsAndLostAcknowledgementRetryDoNotRepeatHistory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var changed = fixture.AddTask();
        var stale = fixture.AddTask();
        var unknown = fixture.AddTask(classification: "future-class");
        var completed = fixture.AddTask("done");
        await fixture.SaveAsync();
        var request = await fixture.RequestAsync([changed.Id, stale.Id, unknown.Id, completed.Id, int.MaxValue]);
        request.ExpectedUpdatedAtById![stale.Id] = Token(DateTime.UtcNow.AddDays(-30));
        var first = await fixture.ApplyAsync(request);
        var retry = await fixture.ApplyAsync(request);

        Assert.Equal(5, first.Requested);
        Assert.Equal(1, first.Updated);
        Assert.Equal(1, first.Conflicted);
        Assert.Equal(3, first.Skipped);
        Assert.Equal("versionConflict", first.Items.Single(row => row.Id == stale.Id).Reason);
        Assert.Equal("notAvailable", first.Items.Single(row => row.Id == int.MaxValue).Reason);
        Assert.Null(first.Items.Single(row => row.Id == int.MaxValue).UpdatedAt);
        Assert.Equal(0, retry.Updated);
        Assert.Equal(1, retry.Unchanged);
        Assert.Equal(1, await fixture.Db.TaskJournalComments.CountAsync());
        Assert.Equal(1, fixture.Realtime.ChangeCount);
    }

    [Fact]
    public async Task ExistingTrackedTaskDoesNotOverwriteFreshPersistedFields()
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask();
        await fixture.SaveAsync();
        var tracked = await fixture.Db.TaskJournalItems.SingleAsync();
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"TaskJournalItems\" SET \"Title\" = {"Fresh persisted title"} WHERE \"Id\" = {task.Id}");
        Assert.NotEqual("Fresh persisted title", tracked.Title);
        var result = await fixture.ApplyAsync(await fixture.RequestAsync([task.Id]));
        var stored = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        Assert.Equal(1, result.Updated);
        Assert.Equal("Fresh persisted title", stored.Title);
        Assert.Equal("Existing result", stored.Result);
    }

    [Theory]
    [InlineData("missingOwner")]
    [InlineData("missingCase")]
    [InlineData("uppercaseSource")]
    public async Task BillableTargetRequiresExistingOwnerAndCaseAndKnownRawSource(string kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask(classification: kind == "uppercaseSource" ? "INTERNAL" : TaskClientBillingClassification.Internal);
        if (kind == "missingOwner") { task.ClientId = null; task.ClientCaseId = null; }
        else if (kind == "missingCase") task.ClientCaseId = null;
        await fixture.SaveAsync();
        var result = await fixture.ApplyAsync(await fixture.RequestAsync([task.Id], TaskClientBillingClassification.Billable));
        Assert.Equal(kind == "uppercaseSource" ? "unknownClassification" : "clientCaseRequired", Assert.Single(result.Items).Reason);
        Assert.Empty(await fixture.Db.TaskJournalComments.ToArrayAsync());
    }

    [Theory]
    [InlineData("differentClient")]
    [InlineData("deletedClient")]
    [InlineData("deletedCase")]
    [InlineData("bothOwners")]
    public async Task BillableTargetRejectsIncompatibleOrDeletedExistingBindings(string kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask(classification: TaskClientBillingClassification.Internal);
        if (kind == "differentClient") task.Client = new Client { Name = "Unrelated synthetic client" };
        if (kind == "bothOwners") task.LegalEntity = new LegalEntity { ShortName = "Second synthetic owner" };
        await fixture.SaveAsync();
        if (kind == "deletedClient")
        {
            var client = await fixture.Db.Clients.SingleAsync();
            fixture.Db.Entry(client).Property("IsDeleted").CurrentValue = true;
            await fixture.SaveAsync();
        }
        if (kind == "deletedCase")
        {
            var clientCase = await fixture.Db.ClientCases.SingleAsync();
            fixture.Db.Entry(clientCase).Property("IsDeleted").CurrentValue = true;
            await fixture.SaveAsync();
        }
        var before = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        var result = await fixture.ApplyAsync(await fixture.RequestAsync([task.Id], TaskClientBillingClassification.Billable));
        Assert.Equal("clientCaseRequired", Assert.Single(result.Items).Reason);
        var after = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        Assert.Equal(before.ClientId, after.ClientId);
        Assert.Equal(before.LegalEntityId, after.LegalEntityId);
        Assert.Equal(before.ClientCaseId, after.ClientCaseId);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.BillingDecisionAt, after.BillingDecisionAt);
        Assert.Empty(await fixture.Db.TaskJournalComments.ToArrayAsync());
    }

    [Fact]
    public async Task ExistingCoherentLegalEntityCaseCanBecomeBillable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var company = new LegalEntity { ShortName = "Synthetic legal client" };
        var task = fixture.AddTask(classification: TaskClientBillingClassification.Internal);
        task.ClientId = null;
        task.LegalEntity = company;
        task.ClientCase = new ClientCase { LegalEntity = company, Title = "Synthetic legal case" };
        await fixture.SaveAsync();
        var result = await fixture.ApplyAsync(await fixture.RequestAsync([task.Id], TaskClientBillingClassification.Billable));
        Assert.Equal(1, result.Updated);
        Assert.Equal(company.Id, (await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync()).LegalEntityId);
    }

    [Fact]
    public async Task ExplicitNullVersionIsSupportedForPersistedLegacyNullTimestamp()
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask();
        task.UpdatedAt = null;
        await fixture.SaveAsync();
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"TaskJournalItems\" SET \"UpdatedAt\" = NULL WHERE \"Id\" = {task.Id}");
        var request = await fixture.RequestAsync([task.Id]);
        Assert.True(request.ExpectedUpdatedAtById!.ContainsKey(task.Id));
        Assert.Null(request.ExpectedUpdatedAtById[task.Id]);
        var result = await fixture.ApplyAsync(request);
        Assert.Equal(1, result.Updated);
        Assert.NotNull(Assert.Single(result.Items).UpdatedAt);
    }

    [Fact]
    public async Task CompletionPermissionAloneDoesNotAuthorizeBilling()
    {
        await using var fixture = await Fixture.CreateAsync(admin: false, canEdit: false, canEditOthers: false);
        var task = fixture.AddTask();
        await fixture.SaveAsync();
        var result = await fixture.Controller().BulkUpdateTasks(await fixture.RequestAsync([task.Id]), CancellationToken.None);
        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(await fixture.Db.TaskJournalComments.ToArrayAsync());
    }

    [Fact]
    public async Task TaskVisibleOnlyThroughParticipantDoesNotRequireAnUnrelatedCasePermission()
    {
        await using var fixture = await Fixture.CreateAsync(admin: false, canEdit: false, canEditOthers: true, viewScope: DataVisibilityScope.Own);
        var task = fixture.AddTask(classification: TaskClientBillingClassification.Internal);
        task.AuthorUserId = fixture.OtherUserId;
        task.AssigneeUserId = fixture.OtherUserId;
        task.Participants.Add(new TaskJournalParticipant { UserId = fixture.UserId, Role = "observer", DisplayName = "Synthetic participant" });
        await fixture.SaveAsync();
        var result = await fixture.ApplyAsync(await fixture.RequestAsync([task.Id], TaskClientBillingClassification.Billable));
        Assert.Equal(1, result.Updated);
        Assert.DoesNotContain(await fixture.Db.RolePermissions.ToArrayAsync(), permission => permission.Section == RolePermissionCatalog.Cases);
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("modern")]
    public async Task PersistedTokensAllowNextDecisionForEitherGlobalCardConfiguration(string cardMode)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.SystemSettings.Add(new SystemSetting { Key = "tasks.taskCardWindowMode", Value = cardMode });
        var task = fixture.AddTask();
        await fixture.SaveAsync();
        var first = await fixture.ApplyAsync(await fixture.RequestAsync([task.Id]));
        var second = new BulkUpdateTaskJournalItemsRequest
        {
            Ids = [task.Id],
            BillingClassification = TaskClientBillingClassification.Billable,
            ExpectedUpdatedAtById = new() { [task.Id] = Assert.Single(first.Items).UpdatedAt },
        };
        Assert.Equal(1, (await fixture.ApplyAsync(second)).Updated);
        Assert.Equal(cardMode, await fixture.Db.SystemSettings.Where(row => row.Key == "tasks.taskCardWindowMode").Select(row => row.Value).SingleAsync());
    }

    [Theory]
    [InlineData("own", true, false, "all", "updated")]
    [InlineData("other", false, true, "all", "updated")]
    [InlineData("other", true, false, "all", "skipped")]
    [InlineData("other", true, true, "own", "skipped")]
    [InlineData("executor", true, true, "all", "skipped")]
    public async Task UsesFullOwnOrOtherPermissionsAndVisibility(string ownership, bool canEdit, bool canEditOthers, string viewScope, string outcome)
    {
        await using var fixture = await Fixture.CreateAsync(admin: false, canEdit, canEditOthers, viewScope);
        var task = fixture.AddTask();
        if (ownership != "own") task.AuthorUserId = fixture.OtherUserId;
        if (ownership == "other") task.AssigneeUserId = fixture.OtherUserId;
        await fixture.SaveAsync();
        var result = await fixture.ApplyAsync(await fixture.RequestAsync([task.Id]));
        Assert.Equal(outcome, Assert.Single(result.Items).Outcome);
        if (ownership == "executor") Assert.Equal("forbidden", Assert.Single(result.Items).Reason);
        if (viewScope == "own") Assert.Equal("notAvailable", Assert.Single(result.Items).Reason);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("nonpositive")]
    [InlineData("overLimit")]
    [InlineData("repeatedOverLimit")]
    [InlineData("missingVersion")]
    [InlineData("extraVersion")]
    [InlineData("badVersion")]
    [InlineData("badTarget")]
    [InlineData("mixedStatus")]
    [InlineData("mixedResult")]
    [InlineData("missingTarget")]
    public async Task RejectsInvalidFinancialRequestWithoutWriting(string kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask();
        await fixture.SaveAsync();
        var request = await fixture.RequestAsync([task.Id]);
        switch (kind)
        {
            case "empty": request.Ids = []; break;
            case "nonpositive": request.Ids.Add(0); break;
            case "overLimit": request.Ids = Enumerable.Range(1, 501).ToList(); break;
            case "repeatedOverLimit": request.Ids = Enumerable.Repeat(task.Id, 501).ToList(); break;
            case "missingVersion": request.ExpectedUpdatedAtById!.Clear(); break;
            case "extraVersion": request.ExpectedUpdatedAtById![int.MaxValue] = null; break;
            case "badVersion": request.ExpectedUpdatedAtById![task.Id] = "2026-10-24"; break;
            case "badTarget": request.BillingClassification = "legacy"; break;
            case "mixedStatus": request.Status = "done"; break;
            case "mixedResult": request.Result = "Do not overwrite"; break;
            case "missingTarget": request.BillingClassification = null; break;
        }
        Assert.IsType<BadRequestObjectResult>((await fixture.Controller().BulkUpdateTasks(request, CancellationToken.None)).Result);
        Assert.Empty(await fixture.Db.TaskJournalComments.ToArrayAsync());
        Assert.Equal(TaskClientBillingClassification.Billable, (await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync()).ClientBillingClassification);
    }

    [Fact]
    public async Task AcceptsFiveHundredExplicitIdsWithoutExpandingOrSilentlyTruncatingSelection()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tasks = Enumerable.Range(0, 501).Select(_ => fixture.AddTask()).ToArray();
        await fixture.SaveAsync();
        var request = await fixture.RequestAsync(tasks.Take(500).Select(task => task.Id));
        var result = await fixture.ApplyAsync(request);
        Assert.Equal(500, result.Requested);
        Assert.Equal(500, result.Updated);
        Assert.Equal(TaskClientBillingClassification.Billable, await fixture.Db.TaskJournalItems.Where(task => task.Id == tasks[500].Id).Select(task => task.ClientBillingClassification).SingleAsync());
        Assert.Equal(1, fixture.Realtime.ChangeCount);
    }

    [Fact]
    public async Task RepeatedIdInsideRawLimitHasOneResultAndOneHistory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask();
        await fixture.SaveAsync();
        var request = await fixture.RequestAsync([task.Id, task.Id]);
        var result = await fixture.ApplyAsync(request);
        Assert.Equal(1, result.Requested);
        Assert.Equal(1, result.Updated);
        Assert.Single(result.Items);
        Assert.Equal(1, await fixture.Db.TaskJournalComments.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatabaseFailureRollsBackAndOnlyKnownConcurrencyIsReturnedAsConflict(bool concurrency)
    {
        var failure = new SaveFailure();
        await using var fixture = await Fixture.CreateAsync(interceptor: failure);
        var task = fixture.AddTask();
        await fixture.SaveAsync();
        var request = await fixture.RequestAsync([task.Id]);
        failure.Error = concurrency ? new DbUpdateException("synthetic lock", new SqliteException("synthetic busy", 5)) : new DbUpdateException("synthetic unexpected write failure");
        if (concurrency) Assert.IsType<ConflictObjectResult>((await fixture.Controller().BulkUpdateTasks(request, CancellationToken.None)).Result);
        else await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Controller().BulkUpdateTasks(request, CancellationToken.None));
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(TaskClientBillingClassification.Billable, (await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync()).ClientBillingClassification);
        Assert.Empty(await fixture.Db.TaskJournalComments.ToArrayAsync());
        Assert.Equal(0, fixture.Realtime.ChangeCount);
    }

    [Fact]
    public async Task RealtimeFailureAfterCommitIsNotReportedAsRollbackAndRetryIsNoOp()
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask();
        await fixture.SaveAsync();
        var request = await fixture.RequestAsync([task.Id]);
        fixture.Realtime.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Controller().BulkUpdateTasks(request, CancellationToken.None));
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(TaskClientBillingClassification.Internal, (await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync()).ClientBillingClassification);
        fixture.Realtime.Fail = false;
        Assert.Equal(1, (await fixture.ApplyAsync(request)).Unchanged);
        Assert.Equal(1, await fixture.Db.TaskJournalComments.CountAsync());
        Assert.Equal(1, fixture.Realtime.ChangeCount);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("retainContractTasks", true)]
    [InlineData("unknown", true)]
    [InlineData("allowFutureClassification", false)]
    public async Task ContractPolicyAndExplicitRequestOptInAreBothRequired(string? policy, bool optIn)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask();
        await fixture.AttachContractAsync(task, ContractBillingMode.Fixed);
        if (policy is not null) await fixture.SetContractPolicyAsync(policy);
        var before = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        var request = await fixture.RequestAsync([task.Id]);
        request.AllowContractClassification = optIn;
        var result = await fixture.ApplyAsync(request);
        var after = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        Assert.Equal("contractual", Assert.Single(result.Items).Reason);
        Assert.Equal(ScalarSnapshot(fixture.Db, before), ScalarSnapshot(fixture.Db, after));
        Assert.Empty(await fixture.Db.TaskJournalComments.ToArrayAsync());
        Assert.Empty(await fixture.Db.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData("Fixed", "new")]
    [InlineData("Actual", "new")]
    [InlineData("Fixed", "done")]
    [InlineData("Actual", "done")]
    public async Task ContractClassificationIsReversibleWithoutRepricingOrChangingSnapshot(string mode, string status)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask(status);
        var contract = await fixture.AttachContractAsync(task, mode);
        await fixture.SetContractPolicyAsync("allowFutureClassification");
        var oldHistory = new TaskJournalComment { TaskJournalItemId = task.Id, Kind = "history", AuthorName = "Synthetic history", Text = "Previous decision", MetadataJson = "{\"immutable\":true}" };
        fixture.Db.TaskJournalComments.Add(oldHistory);
        await fixture.SaveAsync();
        var before = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        var contractBefore = JsonSerializer.Serialize(await fixture.Db.Contracts.AsNoTracking().SingleAsync());
        var request = await fixture.RequestAsync([task.Id]);
        request.AllowContractClassification = true;
        request.IncludeCompleted = status == "done";
        Assert.Equal(1, (await fixture.ApplyAsync(request)).Updated);
        var internalTask = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        Assert.Equal(TaskClientBillingClassification.Internal, internalTask.ClientBillingClassification);
        Assert.Equal(contract.Id, internalTask.BillingContractId);
        Assert.Equal(mode, internalTask.BillingModeSnapshot);
        Assert.Equal(before.Status, internalTask.Status);
        Assert.Equal(before.Result, internalTask.Result);
        Assert.Equal(before.DueAt, internalTask.DueAt);
        var back = await fixture.RequestAsync([task.Id], TaskClientBillingClassification.Billable);
        back.AllowContractClassification = true;
        back.IncludeCompleted = status == "done";
        var result = await fixture.ApplyAsync(back);
        Assert.Equal(1, result.Updated);
        var after = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        Assert.Equal(TaskClientBillingClassification.Billable, after.ClientBillingClassification);
        Assert.Equal(contract.Id, after.BillingContractId);
        Assert.Equal(mode, after.BillingModeSnapshot);
        Assert.Equal(contractBefore, JsonSerializer.Serialize(await fixture.Db.Contracts.AsNoTracking().SingleAsync()));
        Assert.Empty(await fixture.Db.Payments.ToArrayAsync());
        Assert.Empty(await fixture.Db.ContractInvoices.ToArrayAsync());
        Assert.Empty(await fixture.Db.ContractPayments.ToArrayAsync());
        Assert.Equal("Previous decision", (await fixture.Db.TaskJournalComments.AsNoTracking().SingleAsync(row => row.Id == oldHistory.Id)).Text);
        Assert.Equal("{\"immutable\":true}", (await fixture.Db.TaskJournalComments.AsNoTracking().SingleAsync(row => row.Id == oldHistory.Id)).MetadataJson);
        Assert.Equal(3, await fixture.Db.TaskJournalComments.CountAsync());
        if (mode == ContractBillingMode.Fixed)
            Assert.Contains("по фиксированному договору", (await fixture.Db.TaskJournalComments.AsNoTracking().OrderByDescending(row => row.Id).FirstAsync()).Text);
        Assert.Equal(Token(after.UpdatedAt), Assert.Single(result.Items).UpdatedAt);
        Assert.Equal(1, (await fixture.ApplyAsync(back)).Unchanged);
        Assert.Equal(3, await fixture.Db.TaskJournalComments.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualContractInvoiceOnlyProtectsTasksIncludedInIt(bool singleEditor)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask("done");
        var contract = await fixture.AttachContractAsync(task, ContractBillingMode.Actual);
        await fixture.SetContractPolicyAsync("allowFutureClassification");
        var billedTask = fixture.AddTask("done");
        var invoice = new Payment
        {
            ContractId = contract.Id,
            ClientId = task.ClientId,
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Account = "SYNTHETIC-BILLED",
            Amount = 500m,
            Invoice = new Invoice { Number = "SYNTHETIC-BILLED", Date = DateTime.UtcNow.Date }
        };
        fixture.Db.Payments.Add(invoice);
        billedTask.BillingContractId = contract.Id;
        billedTask.BillingModeSnapshot = ContractBillingMode.Actual;
        billedTask.BillingInvoice = invoice;
        await fixture.SaveAsync();
        var before = await ReadContractBillingStateAsync(fixture.Db, includeTaskChanges: false);
        var request = await fixture.RequestAsync([task.Id, billedTask.Id]);
        request.AllowContractClassification = true;
        request.IncludeCompleted = true;

        if (singleEditor)
        {
            var result = await fixture.Controller().UpdateTaskBillingClassification(task.Id,
                new UpdateTaskBillingClassificationRequest
                {
                    Classification = TaskClientBillingClassification.Internal,
                    BillingContractId = contract.Id,
                    HasExpectedUpdatedAt = true,
                    ExpectedUpdatedAt = request.ExpectedUpdatedAtById![task.Id],
                    AllowContractClassification = true
                }, CancellationToken.None);
            Assert.IsType<OkObjectResult>(result.Result);
        }
        else
        {
            var result = await fixture.ApplyAsync(request);
            Assert.Equal(1, result.Updated);
            Assert.Equal("invoiced", result.Items.Single(row => row.Id == billedTask.Id).Reason);
        }

        var tasks = await fixture.Db.TaskJournalItems.AsNoTracking().ToDictionaryAsync(row => row.Id);
        Assert.Equal(TaskClientBillingClassification.Internal, tasks[task.Id].ClientBillingClassification);
        Assert.Equal(contract.Id, tasks[task.Id].BillingContractId);
        Assert.Null(tasks[task.Id].BillingInvoiceId);
        Assert.Equal("done", tasks[task.Id].Status);
        Assert.Equal(invoice.Id, tasks[billedTask.Id].BillingInvoiceId);
        Assert.Equal(TaskClientBillingClassification.Billable, tasks[billedTask.Id].ClientBillingClassification);
        Assert.Equal(before, await ReadContractBillingStateAsync(fixture.Db, includeTaskChanges: false));
    }

    [Theory]
    [InlineData("missing-mode")]
    [InlineData("unknown-mode")]
    [InlineData("wrong-case")]
    [InlineData("changed-contract-mode")]
    [InlineData("foreign-owner")]
    [InlineData("cancelled")]
    [InlineData("expired")]
    [InlineData("legacy")]
    [InlineData("payment")]
    [InlineData("legacy-invoice")]
    [InlineData("legacy-payment")]
    [InlineData("payout-obligation")]
    public async Task OptInNeverRepairsAmbiguousOrFinanciallyLinkedContractTasks(string protection)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask();
        var contract = await fixture.AttachContractAsync(task, ContractBillingMode.Fixed);
        await fixture.SetContractPolicyAsync("allowFutureClassification");
        var stored = await fixture.Db.TaskJournalItems.SingleAsync();
        var storedContract = await fixture.Db.Contracts.SingleAsync();
        switch (protection)
        {
            case "missing-mode": stored.BillingModeSnapshot = null; break;
            case "unknown-mode": stored.BillingModeSnapshot = "Unexpected"; break;
            case "wrong-case": stored.BillingModeSnapshot = "fixed"; break;
            case "changed-contract-mode": storedContract.BillingMode = ContractBillingMode.Actual; break;
            case "foreign-owner": fixture.Db.ClientContracts.RemoveRange(await fixture.Db.ClientContracts.ToArrayAsync()); break;
            case "cancelled": storedContract.Status = ContractStatus.Cancelled; break;
            case "expired": storedContract.ValidUntil = DateTime.UtcNow.Date.AddDays(-1); break;
            case "legacy": stored.ClientBillingClassification = TaskClientBillingClassification.Legacy; break;
            case "payout-obligation": stored.ContractorPaymentEnabled = true; break;
            default:
                var payment = new Payment { Amount = 100m, Status = PaymentStatus.Cancelled };
                fixture.Db.Payments.Add(payment);
                if (protection == "payment") payment.ContractId = contract.Id;
                else if (protection == "legacy-invoice") storedContract.ContractInvoices.Add(new ContractInvoice { Invoice = payment });
                else storedContract.ContractPayments.Add(new ContractPayment { Payment = payment });
                break;
        }
        await fixture.SaveAsync();
        var before = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        var request = await fixture.RequestAsync([task.Id]); request.AllowContractClassification = true;
        var result = await fixture.ApplyAsync(request);
        Assert.Equal(protection == "payout-obligation" ? "contractorPaymentProtected" : "contractual", Assert.Single(result.Items).Reason);
        Assert.Equal(ScalarSnapshot(fixture.Db, before), ScalarSnapshot(fixture.Db, await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync()));
        Assert.Empty(await fixture.Db.TaskJournalComments.ToArrayAsync());
    }

    [Theory]
    [InlineData("invoice")]
    [InlineData("payment")]
    [InlineData("act")]
    public async Task OptInPreservesSoftDeletedLinkOnlyFinancialHistory(string linkKind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask();
        var contract = await fixture.AttachContractAsync(task, ContractBillingMode.Actual);
        await fixture.SetContractPolicyAsync("allowFutureClassification");
        if (linkKind == "act")
        {
            var act = new Act { Number = "SYNTHETIC-HISTORY", Date = DateTime.UtcNow.Date, Amount = 876.54m };
            fixture.Db.ContractActs.Add(new ContractAct { ContractId = contract.Id, Act = act });
        }
        else
        {
            var payment = new Payment
            {
                Amount = 123.45m,
                Date = DateTime.UtcNow.Date,
                Status = PaymentStatus.Cancelled,
                DocumentKind = linkKind == "invoice" ? DocumentKind.Invoice : DocumentKind.Payment,
                ContractId = null
            };
            if (linkKind == "invoice") fixture.Db.ContractInvoices.Add(new ContractInvoice { ContractId = contract.Id, Invoice = payment });
            else fixture.Db.ContractPayments.Add(new ContractPayment { ContractId = contract.Id, Payment = payment });
        }
        // Added rows receive IsDeleted=false from auditing; delete only the freshly persisted parent.
        await fixture.SaveAsync();
        if (linkKind == "act") fixture.Db.Entry(await fixture.Db.Acts.SingleAsync()).Property("IsDeleted").CurrentValue = true;
        else fixture.Db.Entry(await fixture.Db.Payments.SingleAsync()).Property("IsDeleted").CurrentValue = true;
        fixture.Db.TaskJournalComments.Add(new TaskJournalComment
        {
            TaskJournalItemId = task.Id,
            Kind = "history",
            AuthorName = "Synthetic history",
            Text = "Previous financial decision",
            MetadataJson = "{\"immutable\":true}"
        });
        await fixture.SaveAsync();
        Assert.True(linkKind == "act"
            ? await fixture.Db.Acts.IgnoreQueryFilters().Select(act => EF.Property<bool>(act, "IsDeleted")).SingleAsync()
            : await fixture.Db.Payments.IgnoreQueryFilters().Select(payment => EF.Property<bool>(payment, "IsDeleted")).SingleAsync());
        Assert.Empty(await fixture.Db.ContractInvoices.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.ContractPayments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.ContractActs.AsNoTracking().ToArrayAsync());
        Assert.Equal(linkKind == "invoice" ? 1 : 0, await fixture.Db.ContractInvoices.IgnoreQueryFilters().CountAsync());
        Assert.Equal(linkKind == "payment" ? 1 : 0, await fixture.Db.ContractPayments.IgnoreQueryFilters().CountAsync());
        Assert.Equal(linkKind == "act" ? 1 : 0, await fixture.Db.ContractActs.IgnoreQueryFilters().CountAsync());
        Assert.All(await fixture.Db.Payments.IgnoreQueryFilters().AsNoTracking().ToArrayAsync(), payment => Assert.Null(payment.ContractId));
        Assert.Null((await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync()).BillingInvoiceId);
        var before = await ReadContractBillingStateAsync(fixture.Db);
        var request = await fixture.RequestAsync([task.Id]); request.AllowContractClassification = true;

        var result = await fixture.ApplyAsync(request);

        var row = Assert.Single(result.Items);
        Assert.Equal("skipped", row.Outcome);
        Assert.Equal("contractual", row.Reason);
        Assert.Equal(Assert.IsType<Dictionary<int, string?>>(request.ExpectedUpdatedAtById)[task.Id], row.UpdatedAt);
        Assert.Equal(0, result.Updated);
        Assert.Equal(before, await ReadContractBillingStateAsync(fixture.Db));
        Assert.Equal(0, fixture.Realtime.ChangeCount);
    }

    [Fact]
    public async Task ExplicitContractOptInPreservesRawInvoiceLinkBeforeAnyClassificationChange()
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask();
        await fixture.AttachContractAsync(task, ContractBillingMode.Actual);
        await fixture.SetContractPolicyAsync("allowFutureClassification");
        var stored = await fixture.Db.TaskJournalItems.SingleAsync();
        var invoice = new Payment
        {
            Amount = 654.32m,
            Date = DateTime.UtcNow.Date,
            DocumentKind = DocumentKind.Invoice,
            Status = PaymentStatus.Cancelled,
            ContractId = null
        };
        stored.BillingInvoice = invoice;
        fixture.Db.Payments.Add(invoice);
        await fixture.SaveAsync();
        var storedInvoice = await fixture.Db.Payments.SingleAsync();
        fixture.Db.Entry(storedInvoice).Property("IsDeleted").CurrentValue = true;
        fixture.Db.TaskJournalComments.Add(new TaskJournalComment
        {
            TaskJournalItemId = task.Id,
            Kind = "history",
            AuthorName = "Synthetic history",
            Text = "Existing invoice decision",
            MetadataJson = "{\"immutable\":true}"
        });
        await fixture.SaveAsync();
        Assert.True(await fixture.Db.Payments.IgnoreQueryFilters().Select(payment => EF.Property<bool>(payment, "IsDeleted")).SingleAsync());
        Assert.Empty(await fixture.Db.Payments.AsNoTracking().ToArrayAsync());
        Assert.Null((await fixture.Db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync()).ContractId);
        Assert.Equal(invoice.Id, (await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync()).BillingInvoiceId);
        var before = await ReadContractBillingStateAsync(fixture.Db);
        var request = await fixture.RequestAsync([task.Id]); request.AllowContractClassification = true;

        var result = await fixture.ApplyAsync(request);

        var row = Assert.Single(result.Items);
        Assert.Equal("skipped", row.Outcome);
        Assert.Equal("invoiced", row.Reason);
        Assert.Equal(Assert.IsType<Dictionary<int, string?>>(request.ExpectedUpdatedAtById)[task.Id], row.UpdatedAt);
        Assert.Equal(0, result.Updated);
        Assert.Equal(before, await ReadContractBillingStateAsync(fixture.Db));
        Assert.Equal(0, fixture.Realtime.ChangeCount);
    }

    [Theory]
    [InlineData("contract")]
    [InlineData("client")]
    public async Task RawFinancialExistenceQueryNeverDisablesContractOrClientVisibilityFilters(string deletedParent)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask();
        var contract = await fixture.AttachContractAsync(task, ContractBillingMode.Actual);
        await fixture.SetContractPolicyAsync("allowFutureClassification");
        if (deletedParent == "contract")
        {
            var stored = await fixture.Db.Contracts.SingleAsync();
            fixture.Db.Entry(stored).Property("IsDeleted").CurrentValue = true;
        }
        else
        {
            var stored = await fixture.Db.Clients.SingleAsync();
            fixture.Db.Entry(stored).Property("IsDeleted").CurrentValue = true;
        }
        fixture.Db.TaskJournalComments.Add(new TaskJournalComment
        {
            TaskJournalItemId = task.Id,
            Kind = "history",
            AuthorName = "Synthetic history",
            Text = "Existing protected ownership",
            MetadataJson = "{\"immutable\":true}"
        });
        await fixture.SaveAsync();
        Assert.Equal(deletedParent == "contract" ? 0 : 1, await fixture.Db.Contracts.CountAsync());
        Assert.Equal(deletedParent == "client" ? 0 : 1, await fixture.Db.Clients.CountAsync());
        Assert.Equal(contract.Id, (await fixture.Db.Contracts.IgnoreQueryFilters().AsNoTracking().SingleAsync()).Id);
        Assert.Empty(await fixture.Db.ClientContracts.AsNoTracking().ToArrayAsync());
        Assert.Single(await fixture.Db.ClientContracts.IgnoreQueryFilters().AsNoTracking().ToArrayAsync());
        Assert.Empty(await fixture.Db.ContractInvoices.IgnoreQueryFilters().ToArrayAsync());
        Assert.Empty(await fixture.Db.ContractPayments.IgnoreQueryFilters().ToArrayAsync());
        Assert.Empty(await fixture.Db.ContractActs.IgnoreQueryFilters().ToArrayAsync());
        Assert.Empty(await fixture.Db.Payments.IgnoreQueryFilters().ToArrayAsync());
        var before = await ReadContractBillingStateAsync(fixture.Db);
        var request = await fixture.RequestAsync([task.Id]); request.AllowContractClassification = true;

        var result = await fixture.ApplyAsync(request);

        var row = Assert.Single(result.Items);
        Assert.Equal("skipped", row.Outcome);
        Assert.Equal("contractual", row.Reason);
        Assert.Equal(Assert.IsType<Dictionary<int, string?>>(request.ExpectedUpdatedAtById)[task.Id], row.UpdatedAt);
        Assert.Equal(0, result.Updated);
        Assert.Equal(before, await ReadContractBillingStateAsync(fixture.Db));
        Assert.Equal(0, fixture.Realtime.ChangeCount);
    }

    [Fact]
    public async Task SingleEditorPreservesContractAndRequiresFreshExplicitVersionAndOptIn()
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask();
        var contract = await fixture.AttachContractAsync(task, ContractBillingMode.Fixed);
        await fixture.SetContractPolicyAsync("allowFutureClassification");
        var version = Token((await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync()).UpdatedAt);
        var controller = fixture.Controller();
        Assert.IsType<BadRequestObjectResult>((await controller.UpdateTaskBillingClassification(task.Id,
            new UpdateTaskBillingClassificationRequest { Classification = "internal", BillingContractId = contract.Id }, CancellationToken.None)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.UpdateTaskBillingClassification(task.Id,
            new UpdateTaskBillingClassificationRequest { Classification = "internal", HasExpectedUpdatedAt = true, ExpectedUpdatedAt = version, AllowContractClassification = true }, CancellationToken.None)).Result);
        var request = new UpdateTaskBillingClassificationRequest
        {
            Classification = "internal",
            BillingContractId = contract.Id,
            HasExpectedUpdatedAt = true,
            ExpectedUpdatedAt = Token(DateTime.ParseExact(version!, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).AddTicks(1)),
            AllowContractClassification = true
        };
        Assert.IsType<ConflictObjectResult>((await controller.UpdateTaskBillingClassification(task.Id, request, CancellationToken.None)).Result);
        Assert.Empty(await fixture.Db.TaskJournalComments.ToArrayAsync());
        request.ExpectedUpdatedAt = version;
        Assert.IsType<OkObjectResult>((await controller.UpdateTaskBillingClassification(task.Id, request, CancellationToken.None)).Result);
        var after = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        Assert.Equal(contract.Id, after.BillingContractId); Assert.Equal(ContractBillingMode.Fixed, after.BillingModeSnapshot);
        Assert.Equal("internal", after.ClientBillingClassification);
        await fixture.SetContractPolicyAsync("retainContractTasks");
        request.Classification = "billable"; request.ExpectedUpdatedAt = Token(after.UpdatedAt);
        Assert.IsType<BadRequestObjectResult>((await fixture.Controller().UpdateTaskBillingClassification(task.Id, request, CancellationToken.None)).Result);
        var retained = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        Assert.Equal(contract.Id, retained.BillingContractId); Assert.Equal(ContractBillingMode.Fixed, retained.BillingModeSnapshot);
        Assert.Equal("internal", retained.ClientBillingClassification);
    }

    [Fact]
    public async Task DisablingContractPolicyDoesNotClearGrandfatheredContextOnUnrelatedTaskSave()
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask(classification: "internal");
        var contract = await fixture.AttachContractAsync(task, ContractBillingMode.Fixed);
        await fixture.SetContractPolicyAsync("retainContractTasks");
        var before = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        var result = await fixture.Controller(allowOrdinarySave: true).UpdateTask(task.Id, new UpsertTaskJournalItemRequest
        {
            ClientId = before.ClientId,
            ClientCaseId = before.ClientCaseId,
            AssigneeUserId = before.AssigneeUserId,
            Title = "Unrelated edited title",
            ObjectName = before.ObjectName,
            DueAt = before.DueAt,
            Status = before.Status,
            Result = before.Result,
            ClientBillingClassification = "billable",
            BillingContractId = null,
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result.Result);
        var after = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        Assert.Equal("Unrelated edited title", after.Title);
        Assert.Equal("internal", after.ClientBillingClassification);
        Assert.Equal(contract.Id, after.BillingContractId);
        Assert.Equal(ContractBillingMode.Fixed, after.BillingModeSnapshot);
        Assert.Equal(before.BillingDecisionAt, after.BillingDecisionAt);
        Assert.Empty(await fixture.Db.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyAdminCanChangeContractPolicyWithoutReclassifyingAnyTask(bool admin)
    {
        await using var fixture = await Fixture.CreateAsync(admin: admin);
        var task = fixture.AddTask(); await fixture.AttachContractAsync(task, ContractBillingMode.Fixed);
        var before = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        var response = await fixture.Controller().UpdateSettings(new TaskJournalSettingsRequest { ContractTaskBillingMode = "allowFutureClassification" }, CancellationToken.None);
        if (admin)
        {
            Assert.Equal("allowFutureClassification", Assert.IsType<TaskJournalSettingsDto>(Assert.IsType<OkObjectResult>(response.Result).Value).ContractTaskBillingMode);
            Assert.Equal("allowFutureClassification", (await fixture.Db.SystemSettings.AsNoTracking().SingleAsync(row => row.Key == "tasks.contractTaskBillingMode")).Value);
            // An older settings editor must not silently turn an existing policy off by omitting the new field.
            Assert.IsType<OkObjectResult>((await fixture.Controller().UpdateSettings(new TaskJournalSettingsRequest(), CancellationToken.None)).Result);
            Assert.Equal("allowFutureClassification", (await fixture.Db.SystemSettings.AsNoTracking().SingleAsync(row => row.Key == "tasks.contractTaskBillingMode")).Value);
        }
        else
        {
            Assert.IsType<ForbidResult>(response.Result);
            Assert.False(await fixture.Db.SystemSettings.AnyAsync(row => row.Key == "tasks.contractTaskBillingMode"));
        }
        Assert.Equal(ScalarSnapshot(fixture.Db, before), ScalarSnapshot(fixture.Db, await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync()));
        Assert.Empty(await fixture.Db.TaskJournalComments.ToArrayAsync());
        Assert.Empty(await fixture.Db.Payments.ToArrayAsync());
    }

    [Fact]
    public async Task MissingOrUnknownStoredContractPolicyIsConservativeAndInvalidPutDoesNotMutateSettings()
    {
        await using var fixture = await Fixture.CreateAsync();
        var controller = fixture.Controller();
        Assert.Equal("retainContractTasks", Assert.IsType<TaskJournalSettingsDto>(Assert.IsType<OkObjectResult>((await controller.GetSettings(CancellationToken.None)).Result).Value).ContractTaskBillingMode);
        await fixture.SetContractPolicyAsync("unexpected");
        Assert.Equal("retainContractTasks", Assert.IsType<TaskJournalSettingsDto>(Assert.IsType<OkObjectResult>((await controller.GetSettings(CancellationToken.None)).Result).Value).ContractTaskBillingMode);
        Assert.IsType<BadRequestObjectResult>((await controller.UpdateSettings(new TaskJournalSettingsRequest { ContractTaskBillingMode = "unexpected" }, CancellationToken.None)).Result);
        Assert.Equal("unexpected", (await fixture.Db.SystemSettings.AsNoTracking().SingleAsync(row => row.Key == "tasks.contractTaskBillingMode")).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContractOptInStillRequiresExplicitCompletionScopeAndExactVersion(bool stale)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask(stale ? "new" : "done"); await fixture.AttachContractAsync(task, ContractBillingMode.Actual);
        await fixture.SetContractPolicyAsync("allowFutureClassification");
        var before = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        var request = await fixture.RequestAsync([task.Id]); request.AllowContractClassification = true;
        if (stale) request.ExpectedUpdatedAtById![task.Id] = Token(before.UpdatedAt!.Value.AddTicks(1));
        var result = await fixture.ApplyAsync(request);
        Assert.Equal(stale ? "versionConflict" : "completedExcluded", Assert.Single(result.Items).Reason);
        Assert.Equal(stale ? "conflict" : "skipped", Assert.Single(result.Items).Outcome);
        Assert.Equal(ScalarSnapshot(fixture.Db, before), ScalarSnapshot(fixture.Db, await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync()));
        Assert.Empty(await fixture.Db.TaskJournalComments.ToArrayAsync());
    }

    [Theory]
    [InlineData(0, 0, false, false, true)]
    [InlineData(1, 0, false, false, false)]
    [InlineData(-1, 0, false, false, false)]
    [InlineData(0, 1, false, false, false)]
    [InlineData(0, -1, false, false, false)]
    [InlineData(0, 0, true, false, false)]
    [InlineData(0, 0, false, true, false)]
    public async Task InactiveZeroPayoutMetadataIsNotAnObligationButActiveOrNonzeroSettingsStayProtected(int amount, int delay, bool enabled, bool onResult, bool allowed)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = fixture.AddTask();
        task.ContractorPaymentAmount = amount; task.ContractorPaymentResultDelayDays = delay;
        task.ContractorPaymentEnabled = enabled; task.ContractorPaymentOnResult = onResult;
        await fixture.AttachContractAsync(task, ContractBillingMode.Actual);
        await fixture.SetContractPolicyAsync("allowFutureClassification");
        var before = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        var request = await fixture.RequestAsync([task.Id]); request.AllowContractClassification = true;
        var result = await fixture.ApplyAsync(request);
        Assert.Equal(allowed ? "updated" : "skipped", Assert.Single(result.Items).Outcome);
        Assert.Equal(allowed ? null : "contractorPaymentProtected", Assert.Single(result.Items).Reason);
        var after = await fixture.Db.TaskJournalItems.AsNoTracking().SingleAsync();
        Assert.Equal(before.ContractorPaymentAmount, after.ContractorPaymentAmount);
        Assert.Equal(before.ContractorPaymentResultDelayDays, after.ContractorPaymentResultDelayDays);
        Assert.Equal(before.ContractorPaymentEnabled, after.ContractorPaymentEnabled);
        Assert.Equal(before.ContractorPaymentOnResult, after.ContractorPaymentOnResult);
        Assert.Equal(before.BillingContractId, after.BillingContractId); Assert.Equal(before.BillingModeSnapshot, after.BillingModeSnapshot);
        Assert.Empty(await fixture.Db.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskEditAuthorityDoesNotGrantContractVisibilityOrContractEditingPower(bool mayViewContract)
    {
        await using var fixture = await Fixture.CreateAsync(admin: false);
        var task = fixture.AddTask(); await fixture.AttachContractAsync(task, ContractBillingMode.Fixed);
        await fixture.SetContractPolicyAsync("allowFutureClassification");
        var roleId = (await fixture.Db.Users.AsNoTracking().SingleAsync(user => user.Id == fixture.UserId)).RoleId;
        fixture.Db.RolePermissions.Add(new RolePermission { RoleId = roleId, Section = RolePermissionCatalog.Contracts, CanView = mayViewContract, CanEdit = false, ViewScope = DataVisibilityScope.All });
        await fixture.SaveAsync();
        var request = await fixture.RequestAsync([task.Id]); request.AllowContractClassification = true;
        var before = JsonSerializer.Serialize(await fixture.Db.Contracts.AsNoTracking().SingleAsync());
        var result = await fixture.ApplyAsync(request);
        Assert.Equal(mayViewContract ? "updated" : "skipped", Assert.Single(result.Items).Outcome);
        Assert.Equal(mayViewContract ? null : "contractual", Assert.Single(result.Items).Reason);
        Assert.Equal(before, JsonSerializer.Serialize(await fixture.Db.Contracts.AsNoTracking().SingleAsync()));
        Assert.Empty(await fixture.Db.Payments.ToArrayAsync());
    }

    private static async Task<byte[]> ReadContractBillingStateAsync(PaymentContext db, bool includeTaskChanges = true)
    {
        db.ChangeTracker.Clear();
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, byte[][]>
            {
                ["tasks"] = includeTaskChanges ? await ReadScalarsAsync<TaskJournalItem>() : [],
                ["clients"] = await ReadScalarsAsync<Client>(),
                ["clientCases"] = await ReadScalarsAsync<ClientCase>(),
                ["clientContracts"] = await ReadScalarsAsync<ClientContract>(),
                ["contracts"] = await ReadScalarsAsync<Contract>(),
                ["payments"] = await ReadScalarsAsync<Payment>(),
                ["invoices"] = await ReadScalarsAsync<Invoice>(),
                ["acts"] = await ReadScalarsAsync<Act>(),
                ["contractInvoices"] = await ReadScalarsAsync<ContractInvoice>(),
                ["contractPayments"] = await ReadScalarsAsync<ContractPayment>(),
                ["contractActs"] = await ReadScalarsAsync<ContractAct>(),
                ["history"] = includeTaskChanges ? await ReadScalarsAsync<TaskJournalComment>() : [],
            });
        }
        finally { db.ChangeTracker.Clear(); }

        async Task<byte[][]> ReadScalarsAsync<TEntity>() where TEntity : class
        {
            // Tracking retains shadow audit/deletion values in the exact scalar snapshot.
            var rows = await db.Set<TEntity>().IgnoreQueryFilters().AsTracking().ToArrayAsync();
            return rows.Select(row => ScalarSnapshot(db, row)).OrderBy(row => Convert.ToHexString(row), StringComparer.Ordinal).ToArray();
        }
    }

    private static byte[] ScalarSnapshot<TEntity>(PaymentContext db, TEntity task) where TEntity : class
    {
        var values = db.Entry(task).CurrentValues;
        return JsonSerializer.SerializeToUtf8Bytes(values.Properties.OrderBy(property => property.Name)
            .ToDictionary(property => property.Name, property => values[property.Name]));
    }

    private static string? Token(DateTime? value) => value.HasValue
        ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture) : null;

    private sealed class Fixture(PaymentContext db, int userId, int otherUserId, int clientId, int caseId) : IAsyncDisposable
    {
        public PaymentContext Db { get; } = db;
        public int UserId { get; } = userId;
        public int OtherUserId { get; } = otherUserId;
        public RecordingRealtime Realtime { get; } = new();

        public static async Task<Fixture> CreateAsync(bool admin = true, bool canEdit = true, bool canEditOthers = true,
            string viewScope = DataVisibilityScope.All, IInterceptor? interceptor = null, bool canEditAccounts = false)
        {
            var db = await SqliteTestContextFactory.CreateAsync(interceptor: interceptor);
            var role = new Role { Name = admin ? "admin" : "bulk-billing-editor" };
            role.Permissions.Add(new RolePermission { Section = RolePermissionCatalog.Tasks, CanView = true, CanEdit = canEdit, CanEditOthers = canEditOthers, CanCompleteOthers = true, ViewScope = viewScope });
            if (canEditAccounts)
            {
                role.Permissions.Add(new RolePermission { Section = RolePermissionCatalog.Accounts, CanView = true, CanEdit = true, ViewScope = DataVisibilityScope.All });
            }
            var actor = new User { Role = role, Email = "bulk-billing@example.test", FullName = "Synthetic actor", PasswordHash = "unused", IsActive = true, IsApproved = true };
            var other = new User { Role = role, Email = "bulk-other@example.test", FullName = "Other actor", PasswordHash = "unused", IsActive = true, IsApproved = true };
            var client = new Client { Name = "Synthetic client" };
            var clientCase = new ClientCase { Client = client, Title = "Synthetic case" };
            db.AddRange(actor, other, client, clientCase, new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(db, actor.Id, other.Id, client.Id, clientCase.Id);
        }

        public TaskJournalItem AddTask(string status = "new", string classification = TaskClientBillingClassification.Billable)
        {
            var task = new TaskJournalItem
            {
                AuthorUserId = UserId,
                AssigneeUserId = UserId,
                ClientId = clientId,
                ClientCaseId = caseId,
                Title = "Synthetic task",
                ObjectName = "Synthetic client",
                DueAt = DateTime.UtcNow.AddDays(1),
                Status = status,
                Result = "Existing result",
                ClientBillingClassification = classification,
                UpdatedAt = DateTime.UtcNow.AddDays(-1),
            };
            Db.TaskJournalItems.Add(task);
            return task;
        }

        public async Task SaveAsync() { await Db.SaveChangesAsync(); Db.ChangeTracker.Clear(); }

        public async Task<Contract> AttachContractAsync(TaskJournalItem task, string mode)
        {
            var contract = new Contract { Number = "SYNTHETIC-CONTRACT", BillingMode = mode, Date = DateTime.UtcNow.Date.AddDays(-1), Amount = 12345.67m };
            contract.ClientContracts.Add(new ClientContract { ClientId = clientId });
            Db.Contracts.Add(contract); task.BillingContract = contract; task.BillingModeSnapshot = mode;
            await SaveAsync(); return contract;
        }

        public async Task SetContractPolicyAsync(string mode)
        {
            var setting = await Db.SystemSettings.SingleOrDefaultAsync(item => item.Key == "tasks.contractTaskBillingMode");
            if (setting is null) Db.SystemSettings.Add(new SystemSetting { Key = "tasks.contractTaskBillingMode", Value = mode });
            else setting.Value = mode;
            await SaveAsync();
        }

        public async Task<BulkUpdateTaskJournalItemsRequest> RequestAsync(IEnumerable<int> ids, string target = TaskClientBillingClassification.Internal)
        {
            var selected = ids.ToList();
            var timestamps = await Db.TaskJournalItems.AsNoTracking().Where(task => selected.Contains(task.Id))
                .Select(task => new { task.Id, task.UpdatedAt }).ToDictionaryAsync(task => task.Id, task => task.UpdatedAt);
            return new BulkUpdateTaskJournalItemsRequest
            {
                Ids = selected,
                BillingClassification = target,
                ExpectedUpdatedAtById = selected.Distinct().ToDictionary(id => id, id => Token(timestamps.GetValueOrDefault(id))),
            };
        }

        public TasksController Controller(bool allowOrdinarySave = false) => new(Db,
            allowOrdinarySave ? DispatchProxy.Create<INotificationService, SafeOrdinarySaveNotifications>() : DispatchProxy.Create<INotificationService, RejectNotifications>(), realtimePublisher: Realtime)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId.ToString(CultureInfo.InvariantCulture)), new Claim(ClaimTypes.Name, "Synthetic actor")], "Synthetic")),
                }
            },
        };

        public async Task<TaskBulkBillingResultDto> ApplyAsync(BulkUpdateTaskJournalItemsRequest request)
            => Assert.IsType<TaskBulkBillingResultDto>(Assert.IsType<OkObjectResult>((await Controller().BulkUpdateTasks(request, CancellationToken.None)).Result).Value);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class SaveFailure : SaveChangesInterceptor
    {
        public Exception? Error { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => Error is null ? ValueTask.FromResult(result) : ValueTask.FromException<InterceptionResult<int>>(Error);
    }

    private sealed class RecordingRealtime : IRealtimeNotificationPublisher
    {
        public int ChangeCount { get; private set; }
        public bool Fail { get; set; }
        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default)
        {
            ChangeCount++;
            if (Fail) throw new IOException("Synthetic realtime failure after commit.");
            return Task.CompletedTask;
        }
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Bulk classification must not create user notifications.");
    }

    public class SafeOrdinarySaveNotifications : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.ReturnType == typeof(Task)) return Task.CompletedTask;
            if (targetMethod?.ReturnType == typeof(Task<IReadOnlyList<NotificationDelivery>>)) return Task.FromResult<IReadOnlyList<NotificationDelivery>>([]);
            throw new InvalidOperationException("Unexpected outbound operation in isolated ordinary task save.");
        }
    }

    public class RejectNotifications : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException("Bulk classification must not change status or trigger outbound notifications.");
    }
}
