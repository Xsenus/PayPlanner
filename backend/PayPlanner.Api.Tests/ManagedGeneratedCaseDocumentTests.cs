using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalIntegrations;
using PayPlanner.Api.Services.LegalIntegrations.Models;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedGeneratedCaseDocumentTests
{
    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task FinancialIdentityReserveRemainsInvisibleAndCoexistsWithOrdinaryInserts(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1);
        GeneratedFinancialIdentity reserved;
        await using (var transaction = await f.Db.Database.BeginTransactionAsync())
        {
            reserved = await new GeneratedFinancialIdentityAllocator(f.Db).AllocateAsync(kind, default);
            Assert.True(reserved.EntityId > 0);
            Assert.Equal(kind == GeneratedFinancialDocumentKind.Invoice, reserved.InvoiceEntityId.HasValue);
            Assert.Equal(0, await FinancialRowsAsync(f.Db));
            await transaction.CommitAsync();
        }
        Assert.Equal(0, await FinancialRowsAsync(f.Db));
        var ordinary = await InsertFinancialAsync(f.Db, kind);
        Assert.True(ordinary.EntityId > reserved.EntityId);
        if (reserved.InvoiceEntityId.HasValue) Assert.True(ordinary.InvoiceEntityId > reserved.InvoiceEntityId);
        var published = await InsertFinancialAsync(f.Db, kind, reserved);
        Assert.Equal(reserved, published);
        Assert.Equal(kind == GeneratedFinancialDocumentKind.Invoice ? 4 : 2, await FinancialRowsAsync(f.Db));
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialIdentityRollbackDoesNotPublishOrFlushTrackedRecords(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1);
        var pending = new Act { Number = "owned-unsaved-act", Date = DateTime.UtcNow.Date, Amount = 1m };
        f.Db.Acts.Add(pending);
        GeneratedFinancialIdentity reserved;
        await using (var transaction = await f.Db.Database.BeginTransactionAsync())
        {
            reserved = await new GeneratedFinancialIdentityAllocator(f.Db).AllocateAsync(GeneratedFinancialDocumentKind.Invoice, default);
            Assert.Equal(EntityState.Added, f.Db.Entry(pending).State); Assert.Equal(0, await FinancialRowsAsync(f.Db));
            await transaction.RollbackAsync();
        }
        Assert.Equal(EntityState.Added, f.Db.Entry(pending).State); f.Db.Entry(pending).State = EntityState.Detached;
        var ordinary = await InsertFinancialAsync(f.Db, GeneratedFinancialDocumentKind.Invoice);
        if (postgres) { Assert.True(ordinary.EntityId > reserved.EntityId); Assert.True(ordinary.InvoiceEntityId > reserved.InvoiceEntityId); }
        else Assert.Equal(reserved, ordinary);
        Assert.Empty(await f.Db.Acts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialIdentityRequiresTransactionAndKnownKind(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1);
        var allocator = new GeneratedFinancialIdentityAllocator(f.Db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => allocator.AllocateAsync(GeneratedFinancialDocumentKind.Invoice, default));
        await using var transaction = await f.Db.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => allocator.AllocateAsync((GeneratedFinancialDocumentKind)999, default));
        Assert.Equal(0, await FinancialRowsAsync(f.Db));
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialIdentityConcurrentTransactionsNeverAllocateSameId(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1);
        f.PrepareIdentityObservers();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            await using var db = f.IdentityObserver();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var allocator = new GeneratedFinancialIdentityAllocator(db);
            var ids = new List<GeneratedFinancialIdentity>();
            foreach (var kind in Enum.GetValues<GeneratedFinancialDocumentKind>()) ids.Add(await allocator.AllocateAsync(kind, default));
            Assert.Equal(0, await FinancialRowsAsync(db));
            await transaction.CommitAsync(); return ids.ToArray();
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(workers);
        for (var index = 0; index < 3; index++) Assert.Equal(4, results.Select(r => r[index].EntityId).Distinct().Count());
        Assert.Equal(4, results.Select(r => r[0].InvoiceEntityId).Distinct().Count());
        await using var proof = f.IdentityObserver(); Assert.Equal(0, await FinancialRowsAsync(proof));
    }

    private static async Task<int> FinancialRowsAsync(PaymentContext db)
        => await db.Payments.CountAsync() + await db.Invoices.CountAsync() + await db.Acts.CountAsync() + await db.Contracts.CountAsync();

    private static async Task<GeneratedFinancialIdentity> InsertFinancialAsync(PaymentContext db, GeneratedFinancialDocumentKind kind,
        GeneratedFinancialIdentity? reserved = null)
    {
        var date = DateTime.UtcNow.Date;
        if (kind == GeneratedFinancialDocumentKind.Invoice)
        {
            var invoice = new Invoice { Id = reserved?.InvoiceEntityId ?? 0, Number = "owned-identity-invoice", Date = date };
            var payment = new Payment { Id = reserved?.EntityId ?? 0, Invoice = invoice, Amount = 1m, Date = date, Account = invoice.Number };
            db.Payments.Add(payment); await db.SaveChangesAsync(); return new(payment.Id, invoice.Id);
        }
        if (kind == GeneratedFinancialDocumentKind.Act)
        {
            var act = new Act { Id = reserved?.EntityId ?? 0, Number = "owned-identity-act", Date = date, Amount = 1m };
            db.Acts.Add(act); await db.SaveChangesAsync(); return new(act.Id);
        }
        var contract = new Contract { Id = reserved?.EntityId ?? 0, Number = "owned-identity-contract", Date = date, Amount = 1m };
        db.Contracts.Add(contract); await db.SaveChangesAsync(); return new(contract.Id);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CourtQueueExpiredLeaseRollsBackAndFreshWorkerPublishesSameAttempt(bool postgres, bool afterRecords)
    {
        var clock = new AdvancingClock();
        var fence = new CourtRecordsSavedInterceptor(() => clock.Advance(TimeSpan.FromHours(2)));
        await using var f = await Fixture.CreateAsync(postgres, 2, autoPublication: true, fence);
        f.UseNotificationService = true;
        (await f.Db.RolePermissions.SingleAsync(p => p.RoleId == f.User.RoleId && p.Section == RolePermissionCatalog.Cases)).ViewScope = DataVisibilityScope.All;
        var recipient = new User
        {
            Email = "court-lease-recipient@example.local",
            FullName = "Получатель сводки",
            PasswordHash = "synthetic",
            IsActive = true,
            IsApproved = true,
            RoleId = f.User.RoleId
        };
        f.Db.Users.Add(recipient);
        (await f.Db.Clients.SingleAsync(c => c.Id == f.Case.ClientId)).ResponsibleUser = recipient;
        await f.Db.SaveChangesAsync();
        Assert.Equal(1, (await GenerateCourtAsync(f, CourtSourceWithSummary(f.Case.CourtUrl, "Сводка до потери аренды"))).PendingCourtActDocuments);
        var attempt = await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync();
        var frozen = await f.Db.CourtDocumentSourceContents.AsNoTracking().SingleAsync();
        var deadline = await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync();
        var history = (await f.Db.ClientCases.AsNoTracking().SingleAsync(c => c.Id == f.Case.Id)).EventHistory;
        var controlNotifications = await f.Db.UserNotifications.AsNoTracking().Select(n => n.Id).OrderBy(id => id).ToArrayAsync();
        await f.CopyPendingAsync();
        f.Db.ChangeTracker.Clear();
        var readFenceTriggered = false;
        if (afterRecords) fence.Armed = true;
        else f.Local.BeforeRead = () =>
        {
            if (readFenceTriggered) return;
            readFenceTriggered = true;
            clock.Advance(TimeSpan.FromHours(2));
        };
        var interrupted = await f.RunCourtQueueAsync(clock);
        Assert.Equal(1, interrupted.Claimed); Assert.Equal(0, interrupted.Succeeded); Assert.Equal(1, interrupted.Retried);
        Assert.Equal(afterRecords, fence.Triggered); Assert.Equal(!afterRecords, readFenceTriggered);
        f.Db.ChangeTracker.Clear();
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.ToArrayAsync());
        Assert.Empty(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Equal(controlNotifications, await f.Db.UserNotifications.AsNoTracking().Select(n => n.Id).OrderBy(id => id).ToArrayAsync());
        Assert.Equal(history, (await f.Db.ClientCases.AsNoTracking().SingleAsync(c => c.Id == f.Case.Id)).EventHistory);
        Assert.Null((await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).ClientCaseDocumentId);
        var operation = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == StorageOperationTypes.PublishCaseDocument);
        Assert.Equal(StorageOperationStatuses.Running, operation.Status); Assert.NotNull(operation.LeaseOwner);
        Assert.True(operation.LeaseExpiresAt <= clock.GetUtcNow().UtcDateTime); Assert.Null(operation.CompletedAt);
        Assert.Equal(0, operation.AttemptCount);
        f.Local.BeforeRead = null; fence.Armed = false;
        Assert.Equal(1, (await f.RunCourtQueueAsync(clock)).Succeeded);
        f.Db.ChangeTracker.Clear();
        var document = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        var restored = await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync();
        Assert.Equal(deadline.Id, restored.Id); Assert.Equal(deadline.DueAt, restored.DueAt);
        Assert.Equal(document.Id, restored.ClientCaseDocumentId);
        Assert.Equal(attempt.Id, (await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(attempt.FrozenSnapshotJson, (await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync()).FrozenSnapshotJson);
        Assert.Single(await f.Db.StorageProductBindings.ToArrayAsync());
        Assert.Single(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Equal(recipient.Id, (await f.Db.UserNotifications.SingleAsync(n => n.Type == UserNotificationTypes.CaseDocument)).RecipientUserId);
        var read = await f.Reader.OpenReadAsync(LegacyStorageSources.CaseDocuments, document.Id, document.OriginalFileName, document.ContentType, default);
        await using var file = Assert.IsType<StorageObjectReadResult>(read.File);
        using var output = new MemoryStream(); await file.Content.CopyToAsync(output);
        Assert.Equal(frozen.Bytes, output.ToArray());
        Assert.Equal(0, (await f.RunCourtQueueAsync(clock)).Claimed);
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CourtHttpRestoresControlsAndOriginalSummaryAfterSourceReturns(bool postgres, bool disabledDuringReturn)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2, autoPublication: true);
        f.UseNotificationService = true;
        var casePermission = await f.Db.RolePermissions.SingleAsync(p => p.RoleId == f.User.RoleId && p.Section == RolePermissionCatalog.Cases);
        casePermission.ViewScope = DataVisibilityScope.All;
        var recipient = new User
        {
            Email = "court-return-recipient@example.local",
            FullName = "Получатель сводки",
            PasswordHash = "synthetic",
            IsActive = true,
            IsApproved = true,
            RoleId = f.User.RoleId
        };
        f.Db.Users.Add(recipient);
        (await f.Db.Clients.SingleAsync(c => c.Id == f.Case.ClientId)).ResponsibleUser = recipient;
        await f.Db.SaveChangesAsync();
        var originalActorName = f.User.FullName;
        Assert.Equal(1, (await GenerateCourtAsync(f, CourtSourceWithSummary(f.Case.CourtUrl, "Первое описание"))).PendingCourtActDocuments);
        var attempt = await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync();
        var content = await f.Db.CourtDocumentSourceContents.AsNoTracking().SingleAsync();
        var deadline = await f.Db.ClientCaseProceduralDeadlines.SingleAsync();
        deadline.DueAt = new DateTime(2027, 2, 7); deadline.Notes = "Ручная проверка срока";
        f.User.FullName = "Имя после принятия источника";
        await f.Db.SaveChangesAsync();
        var taskId = (await f.Db.TaskJournalItems.AsNoTracking().SingleAsync()).Id;
        await f.CopyPendingAsync();
        f.Db.ChangeTracker.Clear();
        await GenerateCourtAsync(f, new SudrfCourtCaseData
        { SourceUrl = f.Case.CourtUrl, Instance = "appeal", CourtName = "Синтетический суд", CaseNumber = "OWNED-42" });
        Assert.Equal("archived", (await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(1, (await f.RunCourtQueueAsync()).DeadLettered);
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        f.SetManagedEnabled(!disabledDuringReturn);
        var reads = f.Local.ReadCount + f.Mirror.ReadCount;
        f.Db.ChangeTracker.Clear();
        var restored = await GenerateCourtAsync(f, CourtSourceWithSummary(f.Case.CourtUrl, "Текущее описание"));
        Assert.Equal(0, restored.CreatedProceduralDeadlines);
        var control = await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync();
        Assert.Equal(deadline.Id, control.Id); Assert.Equal("active", control.Status);
        Assert.Equal(deadline.DueAt, control.DueAt); Assert.Equal("Ручная проверка срока", control.Notes);
        var restoredTasks = await f.Db.TaskJournalItems.AsNoTracking().OrderBy(t => t.Id).ToArrayAsync();
        Assert.Equal(2, restoredTasks.Length);
        Assert.Equal("cancelled", Assert.Single(restoredTasks, t => t.Id == taskId).Status);
        var activeTask = Assert.Single(restoredTasks, t => t.Status != "cancelled" && t.Status != "done");
        Assert.Contains($"case-deadline:entity-{deadline.Id}", activeTask.TagsJson, StringComparison.Ordinal);
        if (disabledDuringReturn)
        {
            Assert.Equal(0, restored.CreatedCourtActDocuments); Assert.Equal(1, restored.PendingCourtActDocuments);
            Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync()); Assert.Null(control.ClientCaseDocumentId);
            Assert.Equal(reads, f.Local.ReadCount + f.Mirror.ReadCount);
            Assert.Equal(0, (await f.RunCourtQueueAsync()).Claimed);
            f.SetManagedEnabled(true);
        }
        else
        {
            Assert.Equal(1, restored.CreatedCourtActDocuments); Assert.Equal(0, restored.PendingCourtActDocuments);
        }
        Assert.Equal(1, (await f.RunCourtQueueAsync()).Succeeded);
        f.Db.ChangeTracker.Clear();
        var document = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.Equal(originalActorName, document.UploadedByName);
        Assert.Equal(document.Id, (await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).ClientCaseDocumentId);
        var retained = await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(attempt.Id, retained.Id); Assert.Equal(attempt.FrozenSnapshotJson, retained.FrozenSnapshotJson);
        Assert.Equal(content.Bytes, (await f.Db.CourtDocumentSourceContents.AsNoTracking().SingleAsync()).Bytes);
        var read = await f.Reader.OpenReadAsync(LegacyStorageSources.CaseDocuments, document.Id,
            document.OriginalFileName, document.ContentType, default);
        await using var file = Assert.IsType<StorageObjectReadResult>(read.File);
        using var output = new MemoryStream(); await file.Content.CopyToAsync(output);
        var html = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("Первое описание", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Текущее описание", html, StringComparison.Ordinal);
        Assert.Single(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        var notification = Assert.Single(await f.Db.UserNotifications.Where(n => n.Type == UserNotificationTypes.CaseDocument).ToArrayAsync());
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        var replay = await GenerateCourtAsync(f, CourtSourceWithSummary(f.Case.CourtUrl, "Ещё одно описание"));
        Assert.Equal(0, replay.CreatedCourtActDocuments); Assert.Equal(0, replay.PendingCourtActDocuments);
        Assert.Equal(restoredTasks.Select(t => t.Id), await f.Db.TaskJournalItems.AsNoTracking().OrderBy(t => t.Id).Select(t => t.Id).ToArrayAsync());
        Assert.Equal(notification.Id, (await f.Db.UserNotifications.AsNoTracking().SingleAsync(n => n.Type == UserNotificationTypes.CaseDocument)).Id);
        Assert.Equal(0, (await f.RunCourtQueueAsync()).Claimed);
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, "recover")]
    [InlineData(true, "recover")]
    [InlineData(false, "retired")]
    [InlineData(true, "retired")]
    [InlineData(false, "malformed")]
    [InlineData(true, "malformed")]
    [InlineData(false, "revoked")]
    [InlineData(true, "revoked")]
    [InlineData(false, "leased")]
    [InlineData(true, "leased")]
    [InlineData(false, "stale")]
    [InlineData(true, "stale")]
    [InlineData(false, "tombstone")]
    [InlineData(true, "tombstone")]
    public async Task CourtReappearingSourceRecoversOnlyTrustedForbiddenQueueAttempt(bool postgres, string scenario)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2, autoPublication: true);
        var acceptingActor = f.User;
        if (scenario == "revoked")
        {
            (await f.Db.RolePermissions.SingleAsync(p => p.RoleId == f.User.RoleId && p.Section == RolePermissionCatalog.Cases))
                .ViewScope = DataVisibilityScope.All;
            acceptingActor = new User
            {
                Email = "new-court-observer@example.local",
                FullName = "Новый проверяющий",
                PasswordHash = "synthetic",
                IsActive = true,
                IsApproved = true,
                RoleId = f.User.RoleId
            };
            f.Db.Users.Add(acceptingActor); await f.Db.SaveChangesAsync();
        }
        var input = new CourtDocumentSourceInput("sudrf-act-" + new string('b', 32), "reappearing.html",
            "Возврат источника", f.Case.CourtUrl, "<html>Original queued court event</html>");
        var registry = new CourtDocumentSourceRegistry(f.Db);
        var receipt = Assert.Single((await registry.RecordAcceptedBatchAsync(f.Case.Id, f.User.Id, f.User.FullName,
            f.Case.CourtUrl, DateTime.UtcNow, [input], default)).Sources);
        Assert.Equal("pending", (await f.PublishCourt(receipt)).Status);
        await f.CopyPendingAsync();
        Assert.Equal("recorded", (await registry.RecordAcceptedBatchAsync(f.Case.Id, f.User.Id, f.User.FullName,
            f.Case.CourtUrl, DateTime.UtcNow, [], default)).Status);
        Assert.Equal(1, (await f.RunCourtQueueAsync()).DeadLettered);
        var blocked = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == StorageOperationTypes.PublishCaseDocument);
        Assert.Equal("Case publication: forbidden.", blocked.LastError);
        Assert.Null(blocked.LeaseOwner); Assert.Null(blocked.LeaseExpiresAt);
        if (scenario == "retired")
            Assert.Equal("retired", await new CaseDocumentUploadRetirementService(f.Db).RetireAsync(f.Case.Id,
                f.User.Id, receipt.Snapshot.AttemptId, default));
        if (scenario == "malformed")
        {
            var commit = await f.Db.StorageOperations.SingleAsync(o => o.OperationType == "commit");
            var json = System.Text.Json.Nodes.JsonNode.Parse(commit.PayloadJson!)!;
            json["sha256"] = new string('0', 64); commit.PayloadJson = json.ToJsonString();
            await f.Db.SaveChangesAsync();
        }
        if (scenario == "leased")
        {
            var leased = await f.Db.StorageOperations.SingleAsync(o => o.Id == blocked.Id);
            leased.Status = StorageOperationStatuses.Running; leased.CompletedAt = null;
            leased.LeaseOwner = "foreign-court-worker"; leased.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(30);
            await f.Db.SaveChangesAsync();
        }
        if (scenario is "stale" or "tombstone")
        {
            var storageObject = await f.Db.StorageObjects.SingleAsync(o => o.Id == blocked.StorageObjectId);
            if (scenario == "stale") storageObject.CurrentGeneration++;
            else f.Db.StorageTombstones.Add(new StorageTombstone
            {
                StorageObjectId = storageObject.Id,
                Generation = storageObject.CurrentGeneration,
                Reason = "Owned recovery deletion fence"
            });
            await f.Db.SaveChangesAsync();
        }
        var expectedOperation = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == blocked.Id);
        if (scenario == "revoked")
        {
            (await f.Db.Users.SingleAsync(u => u.Id == f.User.Id)).IsActive = false;
            await f.Db.SaveChangesAsync();
        }
        f.Db.ChangeTracker.Clear();
        var reads = f.Local.ReadCount + f.Mirror.ReadCount;
        var changed = input with { RenderedHtml = "<html>Changed after reappearance</html>" };
        var returned = Assert.Single((await registry.RecordAcceptedBatchAsync(f.Case.Id, acceptingActor.Id,
            acceptingActor.FullName, f.Case.CourtUrl, DateTime.UtcNow, [changed], default)).Sources);
        Assert.Equal(receipt, returned);
        Assert.Equal(reads, f.Local.ReadCount + f.Mirror.ReadCount);
        Assert.Single(await f.Db.CourtDocumentSourceAttempts.AsNoTracking().ToArrayAsync());
        Assert.Equal(Encoding.UTF8.GetBytes(input.RenderedHtml), (await f.Db.CourtDocumentSourceContents.AsNoTracking().SingleAsync()).Bytes);
        var operation = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == blocked.Id);
        if (scenario == "recover")
        {
            Assert.Equal(StorageOperationStatuses.Pending, operation.Status);
            Assert.Equal(blocked.AttemptCount, operation.AttemptCount);
            Assert.Null(operation.CompletedAt); Assert.Null(operation.LastError);
            Assert.Equal(1, (await f.RunCourtQueueAsync()).Succeeded);
            var document = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
            Assert.Equal(receipt.Snapshot.ActorId, document.UploadedByUserId);
            Assert.Equal(receipt.Snapshot.ActorName, document.UploadedByName);
            var read = await f.Reader.OpenReadAsync(LegacyStorageSources.CaseDocuments, document.Id,
                document.OriginalFileName, document.ContentType, default);
            await using var file = Assert.IsType<StorageObjectReadResult>(read.File);
            using var output = new MemoryStream(); await file.Content.CopyToAsync(output);
            Assert.Equal(input.RenderedHtml, Encoding.UTF8.GetString(output.ToArray()));
            Assert.Equal(0, (await f.RunCourtQueueAsync()).Claimed);
            Assert.Single(await f.Db.StorageProductBindings.ToArrayAsync());
            Assert.Single(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        }
        else
        {
            Assert.Equal(expectedOperation.Status, operation.Status);
            Assert.Equal(expectedOperation.CompletedAt, operation.CompletedAt);
            Assert.Equal(expectedOperation.LeaseOwner, operation.LeaseOwner);
            Assert.Equal(expectedOperation.LeaseExpiresAt, operation.LeaseExpiresAt);
            Assert.Equal(0, (await f.RunCourtQueueAsync()).Claimed);
            Assert.Equal(reads, f.Local.ReadCount + f.Mirror.ReadCount);
            Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
            Assert.Empty(await f.Db.StorageProductBindings.ToArrayAsync());
        }
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CourtAmbiguousLegacyFileNameRequiresExactSourceIdentity(bool postgres, bool exactLegacyKey)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1);
        const string originalTitle = "Апелляционное определение: вариант";
        const string fileName = "2026-10-01 - Сводка - Апелляционное определение_ вариант.html";
        var raw = $"{originalTitle}|2026-10-01|Результат|{f.Case.CourtUrl}";
        var legacyKey = "sudrf-act-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant()[..24];
        var bytes = Encoding.UTF8.GetBytes("<html>Прежняя сводка</html>");
        var path = f.LocalCaseStorage.BuildRelativePath(f.Case.Id, "ambiguous-legacy-summary.html");
        await f.LocalCaseStorage.SaveAsync(path, bytes, default);
        var legacy = new ClientCaseDocument
        {
            ClientCaseId = f.Case.Id,
            DocumentKey = legacyKey,
            Version = 1,
            DocumentType = CourtSyncNormalization.DocumentTypes.CourtEventSummary,
            Status = "received",
            OriginalFileName = fileName,
            StoredFileName = "ambiguous-legacy-summary.html",
            StoragePath = path,
            ContentType = "text/html; charset=utf-8",
            SizeBytes = bytes.Length,
            SourceType = "sudrf",
            SourceUrl = f.Case.CourtUrl,
            SourceCheckedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            SourceConfidence = "official-source",
            UploadedByUserId = f.User.Id,
            UploadedByName = f.User.FullName,
            UploadedAt = DateTime.UtcNow
        };
        f.Db.ClientCaseDocuments.Add(legacy); await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var source = new SudrfCourtCaseData
        {
            SourceUrl = f.Case.CourtUrl,
            Instance = "appeal",
            CourtActs = [new SudrfCourtAct
            {
                Title = exactLegacyKey ? originalTitle : "Апелляционное определение? вариант",
                ActDate = new DateTime(2026, 10, 1), SourceUrl = f.Case.CourtUrl,
                Result = "Результат", Summary = "Новое событие"
            }]
        };
        var result = await GenerateCourtAsync(f, source);
        Assert.Equal(exactLegacyKey ? 0 : 1, result.CreatedCourtActDocuments);
        Assert.Equal(0, result.PendingCourtActDocuments);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(f.LocalCaseStorage.ResolvePath(path)));
        var documents = await f.Db.ClientCaseDocuments.AsNoTracking().OrderBy(d => d.Id).ToArrayAsync();
        Assert.Equal(exactLegacyKey ? 1 : 2, documents.Length);
        if (exactLegacyKey)
        {
            Assert.Equal(legacy.Id, documents[0].Id);
            Assert.Equal(42, documents[0].DocumentKey.Length);
            Assert.Equal(0, f.Local.PutCount);
            Assert.Equal(legacy.Id, (await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).ClientCaseDocumentId);
        }
        else
        {
            Assert.Equal(legacyKey, documents[0].DocumentKey);
            Assert.Equal(fileName, documents[0].OriginalFileName);
            Assert.Equal(legacy.SourceCheckedAt, documents[0].SourceCheckedAt);
            Assert.Equal("archived", documents[0].Status);
            Assert.Equal(fileName, documents[1].OriginalFileName);
            Assert.Equal(documents[1].Id, (await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).ClientCaseDocumentId);
            Assert.Equal(1, f.Local.PutCount);
            var read = await f.Reader.OpenReadAsync(LegacyStorageSources.CaseDocuments, documents[1].Id,
                documents[1].OriginalFileName, documents[1].ContentType, default);
            await using var file = Assert.IsType<StorageObjectReadResult>(read.File);
            using var output = new MemoryStream(); await file.Content.CopyToAsync(output);
            Assert.Contains("Новое событие", Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtSameFileNameForDifferentSourcesKeepsSeparateDocuments(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1);
        var firstUrl = f.Case.CourtUrl + "#first-act";
        var secondUrl = f.Case.CourtUrl + "#second-act";
        Assert.Equal(1, (await GenerateCourtAsync(f, CourtSourceWithSummary(f.Case.CourtUrl, "Первое событие", firstUrl))).CreatedCourtActDocuments);
        var original = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        f.Db.ChangeTracker.Clear();
        var second = await GenerateCourtAsync(f, CourtSourceWithSummary(f.Case.CourtUrl, "Второе событие", secondUrl));
        Assert.Equal(1, second.CreatedCourtActDocuments);
        Assert.Equal(0, second.PendingCourtActDocuments);
        var documents = await f.Db.ClientCaseDocuments.AsNoTracking().OrderBy(d => d.Id).ToArrayAsync();
        Assert.Equal(2, documents.Length);
        Assert.Equal(original.Id, documents[0].Id);
        Assert.Equal(original.DocumentKey, documents[0].DocumentKey);
        Assert.Equal(firstUrl, documents[0].SourceUrl);
        Assert.Equal(original.SourceCheckedAt, documents[0].SourceCheckedAt);
        Assert.Equal(original.OriginalFileName, documents[1].OriginalFileName);
        Assert.NotEqual(original.DocumentKey, documents[1].DocumentKey);
        Assert.Equal(secondUrl, documents[1].SourceUrl);
        var activeDeadline = await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync(d => d.SourceUrl == secondUrl && d.Status != "archived");
        Assert.Equal(documents[1].Id, activeDeadline.ClientCaseDocumentId);
        for (var index = 0; index < documents.Length; index++)
        {
            var document = documents[index];
            var read = await f.Reader.OpenReadAsync(LegacyStorageSources.CaseDocuments, document.Id,
                document.OriginalFileName, document.ContentType, default);
            await using var file = Assert.IsType<StorageObjectReadResult>(read.File);
            using var output = new MemoryStream(); await file.Content.CopyToAsync(output);
            var html = Encoding.UTF8.GetString(output.ToArray());
            Assert.Contains(index == 0 ? "Первое событие" : "Второе событие", html, StringComparison.Ordinal);
            Assert.DoesNotContain(index == 0 ? "Второе событие" : "Первое событие", html, StringComparison.Ordinal);
        }
        var puts = f.Local.PutCount;
        f.Db.ChangeTracker.Clear();
        Assert.Equal(0, (await GenerateCourtAsync(f, CourtSourceWithSummary(f.Case.CourtUrl, "Повтор второго события", secondUrl))).CreatedCourtActDocuments);
        Assert.Equal(puts, f.Local.PutCount);
        Assert.Equal(2, await f.Db.CourtDocumentSourceAttempts.CountAsync());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task CourtSurvivingVersionPreventsUnnecessaryRenewal(bool postgres, bool legacyKey, bool changedResult)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1);
        var source = CourtSourceWithSummary(f.Case.CourtUrl, "Исходная сводка");
        Assert.Equal(1, (await GenerateCourtAsync(f, source)).CreatedCourtActDocuments);
        var original = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        var attempt = await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync();
        var content = await f.Db.CourtDocumentSourceContents.AsNoTracking().SingleAsync();
        var act = Assert.Single(source.CourtActs);
        var raw = $"{act.Title}|{act.ActDate:yyyy-MM-dd}|{act.Result}|{act.SourceUrl}";
        var key = legacyKey ? "sudrf-act-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant()[..24]
            : original.DocumentKey;
        var bytes = Encoding.UTF8.GetBytes("<html>Сохранённая версия</html>");
        var path = f.LocalCaseStorage.BuildRelativePath(f.Case.Id, "surviving-court-version.html");
        await f.LocalCaseStorage.SaveAsync(path, bytes, default);
        var version = new ClientCaseDocument
        {
            ClientCaseId = f.Case.Id,
            DocumentKey = key,
            Version = 2,
            DocumentType = original.DocumentType,
            Status = "received",
            OriginalFileName = original.OriginalFileName,
            StoredFileName = "surviving-court-version.html",
            StoragePath = path,
            ContentType = original.ContentType,
            SizeBytes = bytes.Length,
            SourceType = "sudrf",
            SourceUrl = original.SourceUrl,
            SourceCheckedAt = original.SourceCheckedAt,
            SourceConfidence = "official-source",
            UploadedByUserId = f.User.Id,
            UploadedByName = f.User.FullName,
            UploadedAt = DateTime.UtcNow
        };
        f.Db.ClientCaseDocuments.Add(version); await f.Db.SaveChangesAsync();
        await f.Db.ClientCaseDocuments.Where(d => d.Id == original.Id).ExecuteDeleteAsync();
        f.Db.ChangeTracker.Clear();
        var puts = f.Local.PutCount;
        var reads = f.Local.ReadCount;
        var result = await GenerateCourtAsync(f, CourtSourceWithSummary(f.Case.CourtUrl, "Новое описание события",
            actResult: changedResult ? "Уточнённый результат" : null));
        Assert.Equal(0, result.CreatedCourtActDocuments); Assert.Equal(0, result.PendingCourtActDocuments);
        Assert.Equal(1, (await f.Db.CourtDocumentSources.AsNoTracking().SingleAsync()).CurrentAttemptVersion);
        var retained = await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(attempt.Id, retained.Id); Assert.Equal(attempt.FrozenSnapshotJson, retained.FrozenSnapshotJson);
        Assert.Equal(content.Bytes, (await f.Db.CourtDocumentSourceContents.AsNoTracking().SingleAsync()).Bytes);
        var saved = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.Equal(version.Id, saved.Id); Assert.Equal(original.DocumentKey, saved.DocumentKey);
        Assert.Equal(version.Id, (await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).ClientCaseDocumentId);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(f.LocalCaseStorage.ResolvePath(path)));
        Assert.Equal(puts, f.Local.PutCount); Assert.Equal(reads, f.Local.ReadCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CourtManualDocumentCollisionKeepsControlsWithoutTouchingManualFile(bool postgres, bool alreadyPending)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2, autoPublication: true);
        var source = CourtSourceWithSummary(f.Case.CourtUrl, "Судебный источник при конфликте файла");
        if (alreadyPending) Assert.Equal(1, (await GenerateCourtAsync(f, source)).PendingCourtActDocuments);
        var name = "2026-10-01 - Сводка - Апелляционное определение.html".ToUpperInvariant();
        var bytes = Encoding.UTF8.GetBytes("<html>Не трогать ручной файл</html>");
        var path = f.LocalCaseStorage.BuildRelativePath(f.Case.Id, "owned-manual-summary.html");
        await f.LocalCaseStorage.SaveAsync(path, bytes, default);
        var manual = new ClientCaseDocument
        {
            ClientCaseId = f.Case.Id,
            DocumentKey = "owned-manual-summary",
            DocumentType = CourtSyncNormalization.DocumentTypes.CourtEventSummary,
            Status = "received",
            OriginalFileName = name,
            StoredFileName = "owned-manual-summary.html",
            StoragePath = path,
            ContentType = "text/html; charset=utf-8",
            SizeBytes = bytes.LongLength,
            UploadedByUserId = f.User.Id,
            UploadedByName = f.User.FullName
        };
        f.Db.ClientCaseDocuments.Add(manual); await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        var report = await GenerateCourtAsync(f, source);
        Assert.Equal(alreadyPending ? 0 : 1, report.CreatedProceduralDeadlines);
        Assert.Contains(report.ConflictItems, item => item.Contains("ручной документ", StringComparison.Ordinal));
        Assert.Equal(0, report.CreatedCourtActDocuments); Assert.Equal(0, report.PendingCourtActDocuments);
        var deadline = await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync();
        Assert.Equal("active", deadline.Status); Assert.Null(deadline.ClientCaseDocumentId);
        Assert.NotNull(deadline.SourceDocumentKey);
        Assert.Single(await f.Db.TaskJournalItems.Where(t => t.ClientCaseId == f.Case.Id
            && t.TagsJson.Contains($"case-deadline:entity-{deadline.Id}")).ToArrayAsync());
        var notificationIds = await f.Db.UserNotifications.Where(n => n.Type == UserNotificationTypes.CaseProceduralDeadline)
            .Select(n => n.Id).OrderBy(id => id).ToArrayAsync();
        Assert.Single(notificationIds);
        f.Db.ChangeTracker.Clear();
        var repeated = await GenerateCourtAsync(f, source);
        Assert.Equal(0, repeated.CreatedProceduralDeadlines);
        Assert.Equal("active", (await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(notificationIds, await f.Db.UserNotifications.Where(n => n.Type == UserNotificationTypes.CaseProceduralDeadline)
            .Select(n => n.Id).OrderBy(id => id).ToArrayAsync());
        if (alreadyPending)
        {
            await f.CopyPendingAsync();
            var reads = f.Local.ReadCount + f.Mirror.ReadCount;
            Assert.Equal(1, (await f.RunCourtQueueAsync()).DeadLettered);
            Assert.Equal(reads, f.Local.ReadCount + f.Mirror.ReadCount);
        }
        else
        {
            Assert.Empty(await f.Db.StorageOperations.ToArrayAsync());
            Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
        }
        var unchanged = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.Equal(manual.Id, unchanged.Id); Assert.Equal(name, unchanged.OriginalFileName);
        Assert.Equal("manual", unchanged.SourceType); Assert.Equal("manual", unchanged.SourceConfidence);
        Assert.Null(unchanged.SourceCheckedAt);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(f.LocalCaseStorage.ResolvePath(path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtHttpRenewsDeletedPublishedSummaryOnlyAfterFreshSourceAndRequiredCopies(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2, autoPublication: true);
        Assert.Equal(1, (await GenerateCourtAsync(f, CourtSourceWithSummary(f.Case.CourtUrl, "Прежняя сводка"))).PendingCourtActDocuments);
        await f.CopyPendingAsync();
        Assert.Equal(1, (await f.RunCourtQueueAsync()).Succeeded);
        var oldDocument = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        var oldAttempt = await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync();
        var oldContent = await f.Db.CourtDocumentSourceContents.AsNoTracking().SingleAsync();
        var deadline = await f.Db.ClientCaseProceduralDeadlines.SingleAsync();
        deadline.DueAt = new DateTime(2027, 2, 7);
        deadline.Notes = "Ручное уточнение";
        deadline.Status = "closed";
        await f.Db.SaveChangesAsync();
        var notificationIds = await f.Db.UserNotifications.Where(n => n.Type == UserNotificationTypes.CaseProceduralDeadline)
            .Select(n => n.Id).OrderBy(id => id).ToArrayAsync();
        await f.Db.ClientCaseDocuments.Where(d => d.Id == oldDocument.Id).ExecuteDeleteAsync();
        f.Db.ChangeTracker.Clear();
        var renewed = await GenerateCourtAsync(f, CourtSourceWithSummary(f.Case.CourtUrl, "Новая подтверждённая сводка"));
        Assert.Equal(0, renewed.CreatedProceduralDeadlines);
        Assert.Equal(0, renewed.CreatedCourtActDocuments);
        Assert.Equal(1, renewed.PendingCourtActDocuments);
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        var source = await f.Db.CourtDocumentSources.AsNoTracking().SingleAsync();
        Assert.Equal(oldAttempt.SourceId, source.Id);
        Assert.Equal(2, source.CurrentAttemptVersion);
        var newAttempt = await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync(a => a.Version == 2);
        Assert.NotEqual(oldAttempt.Id, newAttempt.Id);
        Assert.Equal(oldAttempt.FrozenSnapshotJson, (await f.Db.CourtDocumentSourceAttempts.AsNoTracking()
            .SingleAsync(a => a.Id == oldAttempt.Id)).FrozenSnapshotJson);
        Assert.Equal(oldContent.Bytes, (await f.Db.CourtDocumentSourceContents.AsNoTracking()
            .SingleAsync(c => c.AttemptId == oldAttempt.Id)).Bytes);
        Assert.Equal(2, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
        f.Db.ChangeTracker.Clear();
        Assert.Equal(1, (await GenerateCourtAsync(f, CourtSourceWithSummary(f.Case.CourtUrl, "Изменение после новой попытки"))).PendingCourtActDocuments);
        Assert.Equal(2, await f.Db.CourtDocumentSourceAttempts.CountAsync());
        Assert.Equal(2, f.Local.PutCount);
        await f.CopyPendingAsync(expectedAvailableReplicas: 4);
        Assert.Equal(1, (await f.RunCourtQueueAsync()).Succeeded);
        f.Db.ChangeTracker.Clear();
        var replay = await GenerateCourtAsync(f, CourtSourceWithSummary(f.Case.CourtUrl, "Изменение после новой попытки"));
        Assert.Equal(0, replay.CreatedCourtActDocuments); Assert.Equal(0, replay.PendingCourtActDocuments);
        var replacement = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.NotEqual(oldDocument.Id, replacement.Id);
        Assert.Equal(oldDocument.DocumentKey, replacement.DocumentKey);
        var preserved = await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync();
        Assert.Equal(deadline.Id, preserved.Id); Assert.Equal(deadline.DueAt, preserved.DueAt);
        Assert.Equal("Ручное уточнение", preserved.Notes); Assert.Equal("closed", preserved.Status);
        Assert.Equal(replacement.Id, preserved.ClientCaseDocumentId);
        Assert.Equal(notificationIds, await f.Db.UserNotifications.Where(n => n.Type == UserNotificationTypes.CaseProceduralDeadline)
            .Select(n => n.Id).OrderBy(id => id).ToArrayAsync());
        var read = await f.Reader.OpenReadAsync(LegacyStorageSources.CaseDocuments, replacement.Id,
            replacement.OriginalFileName, replacement.ContentType, default);
        await using var file = Assert.IsType<StorageObjectReadResult>(read.File);
        using var output = new MemoryStream(); await file.Content.CopyToAsync(output);
        Assert.Contains("Новая подтверждённая сводка", Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
        Assert.DoesNotContain("Изменение после новой попытки", Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
        Assert.Equal(2, f.Local.PutCount); Assert.Equal(2, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtBindingReportsPendingSummaryUntilQueuePublishesCopies(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2, autoPublication: true);
        var source = CourtSourceWithSummary(f.Case.CourtUrl, "Сводка привязанной карточки");
        var request = new CasesV2Controller.ClientCaseCourtSyncBindRequest
        { SourceUrl = f.Case.CourtUrl, Instance = "appeal", ConfirmDifferences = true };
        var first = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncBindingDto>(Assert.IsType<OkObjectResult>(
            (await f.Controller(source).BindCourtSyncCase(f.Case.Id, request, default)).Result).Value);
        Assert.True(first.SyncedFromExternalSource);
        Assert.Equal(1, first.CreatedProceduralDeadlines);
        Assert.Equal(0, first.CreatedCourtActDocuments);
        Assert.Equal(1, first.PendingCourtActDocuments);
        Assert.Contains("ожидают обязательных копий", first.Message, StringComparison.Ordinal);
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Null((await f.Db.ClientCaseProceduralDeadlines.SingleAsync()).ClientCaseDocumentId);
        await f.CopyPendingAsync();
        Assert.Equal(1, (await f.RunCourtQueueAsync()).Succeeded);
        f.Db.ChangeTracker.Clear();
        var repeated = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncBindingDto>(Assert.IsType<OkObjectResult>(
            (await f.Controller(source).BindCourtSyncCase(f.Case.Id, request, default)).Result).Value);
        Assert.Equal(0, repeated.PendingCourtActDocuments);
        Assert.Equal(0, repeated.CreatedCourtActDocuments);
        Assert.Equal(0, repeated.CreatedProceduralDeadlines);
        Assert.Single(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CourtHttpPreservesImmediateControlsAndFrozenSummaryAcrossCopyGate(bool postgres, bool queuePublication)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2, autoPublication: true);
        var source = CourtSourceWithSummary(f.Case.CourtUrl, "Исходное содержание до копирования");
        var first = await GenerateCourtAsync(f, source);
        Assert.Equal(1, first.CreatedProceduralDeadlines);
        Assert.Equal(0, first.CreatedCourtActDocuments);
        Assert.Equal(1, first.PendingCourtActDocuments);
        Assert.Contains("ожидают обязательных копий", first.Message, StringComparison.Ordinal);
        var deadline = await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync();
        Assert.Null(deadline.ClientCaseDocumentId);
        Assert.Single(await f.Db.UserNotifications.Where(n => n.Type == UserNotificationTypes.CaseProceduralDeadline).ToArrayAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.ToArrayAsync());
        Assert.Empty(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        var attempt = await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync();
        var frozen = await f.Db.CourtDocumentSourceContents.AsNoTracking().SingleAsync();
        source = CourtSourceWithSummary(f.Case.CourtUrl, "Изменённое содержание после принятия");
        f.Db.ChangeTracker.Clear();
        var repeated = await GenerateCourtAsync(f, source);
        Assert.Equal(0, repeated.CreatedProceduralDeadlines);
        Assert.Equal(0, repeated.CreatedCourtActDocuments);
        Assert.Equal(1, repeated.PendingCourtActDocuments);
        Assert.Equal(attempt.Id, (await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(1, f.Local.PutCount);
        await f.CopyPendingAsync();
        if (queuePublication) Assert.Equal(1, (await f.RunCourtQueueAsync()).Succeeded);
        f.Db.ChangeTracker.Clear();
        var completed = await GenerateCourtAsync(f, source);
        Assert.Equal(queuePublication ? 0 : 1, completed.CreatedCourtActDocuments);
        Assert.Equal(0, completed.PendingCourtActDocuments);
        Assert.Equal(0, completed.CreatedProceduralDeadlines);
        var document = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        var linked = await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync();
        Assert.Equal(deadline.Id, linked.Id);
        Assert.Equal(deadline.DueAt, linked.DueAt);
        Assert.Equal(document.Id, linked.ClientCaseDocumentId);
        Assert.Equal(1, document.Version);
        var read = await f.Reader.OpenReadAsync(LegacyStorageSources.CaseDocuments, document.Id,
            document.OriginalFileName, document.ContentType, default);
        await using var file = Assert.IsType<StorageObjectReadResult>(read.File);
        using var output = new MemoryStream();
        await file.Content.CopyToAsync(output);
        Assert.Equal(frozen.Bytes, output.ToArray());
        Assert.Contains("Исходное содержание до копирования", Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
        Assert.DoesNotContain("Изменённое содержание после принятия", Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
        Assert.Single(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtHttpDoesNotFallbackToLegacyWhenDurableAttemptRoutingIsDisabled(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        var source = CourtDeadlineSource(f.Case.CourtUrl);
        Assert.Equal(1, (await GenerateCourtAsync(f, source)).PendingCourtActDocuments);
        f.SetManagedEnabled(false);
        f.Db.ChangeTracker.Clear();
        var paused = await GenerateCourtAsync(f, source);
        Assert.Equal(1, paused.PendingCourtActDocuments);
        Assert.Equal(0, paused.CreatedCourtActDocuments);
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Single(await f.Db.ClientCaseProceduralDeadlines.ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
        f.SetManagedEnabled(true);
        await f.CopyPendingAsync();
        f.Db.ChangeTracker.Clear();
        Assert.Equal(1, (await GenerateCourtAsync(f, source)).CreatedCourtActDocuments);
        Assert.Single(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtSchedulerUsesManagedStorageAndPublishesOriginalSummaryAfterCopies(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2, autoPublication: true);
        var source = CourtSourceWithSummary(f.Case.CourtUrl, "Сводка планировщика");
        Assert.Equal(1, await f.RunCourtSchedulerAsync(source));
        var attempt = await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(f.User.Id, attempt.ActorId);
        Assert.Equal("Система", attempt.ActorName);
        Assert.Single(await f.Db.ClientCaseProceduralDeadlines.ToArrayAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
        await f.CopyPendingAsync();
        Assert.Equal(1, (await f.RunCourtQueueAsync()).Succeeded);
        f.Db.ChangeTracker.Clear();
        Assert.Equal(1, await f.RunCourtSchedulerAsync(CourtSourceWithSummary(f.Case.CourtUrl, "Новая сводка")));
        var document = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.Equal("Система", document.UploadedByName);
        Assert.Equal(document.Id, (await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).ClientCaseDocumentId);
        Assert.Equal(attempt.Id, (await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
        Assert.Single(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
    }

    private static SudrfCourtCaseData CourtSourceWithSummary(string url, string summary, string? actSourceUrl = null, string? actResult = null) => new()
    {
        SourceUrl = url,
        Instance = "appeal",
        CourtActs = [new SudrfCourtAct
        { Title = "Апелляционное определение", ActDate = new DateTime(2026, 10, 1), SourceUrl = actSourceUrl ?? url, Summary = summary, Result = actResult ?? string.Empty }]
    };

    [Theory]
    [InlineData(false, "publish")]
    [InlineData(true, "publish")]
    [InlineData(false, "backfill")]
    [InlineData(true, "backfill")]
    [InlineData(false, "removed")]
    [InlineData(true, "removed")]
    [InlineData(false, "retired")]
    [InlineData(true, "retired")]
    [InlineData(false, "digest")]
    [InlineData(true, "digest")]
    public async Task CourtPublicationQueueUsesOriginalProofAndRejectsStaleOrRetiredSources(bool postgres, string scenario)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2, autoPublication: scenario != "backfill");
        var key = "sudrf-act-" + new string('f', 32);
        var input = new CourtDocumentSourceInput(key, "court-queue.html", "Сводка для очереди", f.Case.CourtUrl,
            "<html>Original queued court event</html>");
        var registry = new CourtDocumentSourceRegistry(f.Db);
        var receipt = Assert.Single((await registry.RecordAcceptedBatchAsync(f.Case.Id, f.User.Id, f.User.FullName,
            f.Case.CourtUrl, DateTime.UtcNow, [input], default)).Sources);
        Assert.Equal("pending", (await f.PublishCourt(receipt)).Status);
        await f.CopyPendingAsync();
        var pendingStates = await f.UploadStates(receipt);
        Assert.NotNull(pendingStates);
        var pending = Assert.Single(pendingStates);
        Assert.NotNull(pending.Generation);
        Assert.Equal("court", pending.Generation!.Kind);
        Assert.True(pending.Generation.SourceVerified);
        if (scenario == "removed")
            Assert.Equal("recorded", (await registry.RecordAcceptedBatchAsync(f.Case.Id, f.User.Id, f.User.FullName,
                f.Case.CourtUrl, DateTime.UtcNow, [], default)).Status);
        if (scenario == "retired")
            Assert.Equal("retired", await new CaseDocumentUploadRetirementService(f.Db).RetireAsync(f.Case.Id,
                f.User.Id, receipt.Snapshot.AttemptId, default));
        if (scenario == "digest")
        {
            var commit = await f.Db.StorageOperations.SingleAsync(o => o.OperationType == "commit");
            var json = System.Text.Json.Nodes.JsonNode.Parse(commit.PayloadJson!)!;
            json["sha256"] = new string('0', 64);
            commit.PayloadJson = json.ToJsonString(); await f.Db.SaveChangesAsync();
        }
        var reads = f.Local.ReadCount + f.Mirror.ReadCount;
        var result = await f.RunCourtQueueAsync();
        if (scenario is "publish" or "backfill")
        {
            Assert.Equal(1, result.Succeeded);
            Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
            var publishedStates = await f.UploadStates(receipt);
            Assert.NotNull(publishedStates);
            var state = Assert.Single(publishedStates);
            Assert.Equal("uploaded", state.Status); Assert.NotNull(state.DocumentId);
            Assert.True(state.Generation!.SourceVerified);
            Assert.Equal(0, (await f.RunCourtQueueAsync()).Claimed);
            Assert.Single(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        }
        else
        {
            Assert.Equal(1, result.DeadLettered);
            Assert.Equal(reads, f.Local.ReadCount + f.Mirror.ReadCount);
            Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
            Assert.Empty(await f.Db.StorageProductBindings.ToArrayAsync());
            Assert.Empty(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        }
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
        Assert.Equal(2, await f.Db.StorageReplicas.CountAsync(r => r.Status == StorageReplicaStatuses.Available));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedCourtPublicationRejectsDifferentBytesWithMatchingMetadata(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        var input = new CourtDocumentSourceInput("sudrf-act-" + new string('e', 32), "proof.html", "Доказательство",
            f.Case.CourtUrl, "<html>Original court content</html>");
        var receipt = Assert.Single((await new CourtDocumentSourceRegistry(f.Db).RecordAcceptedBatchAsync(f.Case.Id,
            f.User.Id, f.User.FullName, f.Case.CourtUrl, DateTime.UtcNow, [input], default)).Sources);
        Assert.Equal("pending", (await f.PublishCourt(receipt, corruptBytes: true)).Status);
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        await f.CopyPendingAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.PublishCourt(receipt, corruptBytes: true));
        await using var observer = f.Observer();
        Assert.Empty(await observer.ClientCaseDocuments.ToArrayAsync());
        Assert.Empty(await observer.StorageProductBindings.ToArrayAsync());
        Assert.Empty(await observer.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Empty(await observer.UserNotifications.Where(n => n.Type == UserNotificationTypes.CaseDocument).ToArrayAsync());
        Assert.DoesNotContain(input.Title, (await observer.ClientCases.SingleAsync()).EventHistory ?? "");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TypedCourtPublicationLinksExistingControlsAndRollsBackAllRecords(bool postgres, bool failLate)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        var rolePermission = await f.Db.RolePermissions.SingleAsync(p => p.RoleId == f.User.RoleId && p.Section == RolePermissionCatalog.Cases);
        rolePermission.ViewScope = DataVisibilityScope.All;
        var actor = new User
        {
            Email = "court-clerk@example.local",
            FullName = "Секретарь суда",
            PasswordHash = "synthetic",
            IsActive = true,
            IsApproved = true,
            RoleId = f.User.RoleId
        };
        f.Db.Users.Add(actor);
        var key = "sudrf-act-" + new string('d', 32);
        var deadline = new ClientCaseProceduralDeadline
        {
            ClientCaseId = f.Case.Id,
            Title = "Прежний срок",
            DeadlineType = "appeal",
            DueAt = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc),
            Notes = "Ручная дата и примечание",
            SourceType = "sudrf",
            SourceDocumentKey = key,
            Status = "closed"
        };
        f.Db.ClientCaseProceduralDeadlines.Add(deadline);
        await f.Db.SaveChangesAsync();
        var due = deadline.DueAt;
        var input = new CourtDocumentSourceInput(key, "court-original.html", "Исходное событие", f.Case.CourtUrl,
            "<!doctype html><html><body>Исходный текст суда</body></html>");
        var receipt = Assert.Single((await new CourtDocumentSourceRegistry(f.Db).RecordAcceptedBatchAsync(f.Case.Id,
            actor.Id, actor.FullName, f.Case.CourtUrl, DateTime.UtcNow, [input], default)).Sources);
        Assert.Equal("pending", (await f.PublishCourt(receipt)).Status);
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.ToArrayAsync());
        Assert.Empty(await f.Db.UserNotifications.Where(n => n.Type == UserNotificationTypes.CaseDocument).ToArrayAsync());
        Assert.Null((await f.Db.ClientCaseProceduralDeadlines.SingleAsync()).ClientCaseDocumentId);
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
        await f.CopyPendingAsync();
        if (failLate)
        {
            await Assert.ThrowsAsync<IOException>(() => f.PublishCourt(receipt, failLate: true));
            await using var observer = f.Observer();
            Assert.Empty(await observer.ClientCaseDocuments.ToArrayAsync());
            Assert.Empty(await observer.StorageProductBindings.ToArrayAsync());
            Assert.Empty(await observer.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
            Assert.Empty(await observer.UserNotifications.Where(n => n.Type == UserNotificationTypes.CaseDocument).ToArrayAsync());
            Assert.Empty(await observer.CaseDocumentNotificationDispatches.ToArrayAsync());
            Assert.Null((await observer.ClientCaseProceduralDeadlines.SingleAsync()).ClientCaseDocumentId);
            Assert.DoesNotContain("Исходное событие", (await observer.ClientCases.SingleAsync()).EventHistory ?? "");
        }
        f.Db.ChangeTracker.Clear();
        Assert.Equal("uploaded", (await f.PublishCourt(receipt)).Status);
        var document = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        var linked = await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync();
        Assert.Equal(deadline.Id, linked.Id); Assert.Equal(document.Id, linked.ClientCaseDocumentId);
        Assert.Equal(due, linked.DueAt); Assert.Equal("Ручная дата и примечание", linked.Notes); Assert.Equal("closed", linked.Status);
        Assert.Equal("sudrf", document.SourceType); Assert.Equal(input.SourceUrl, document.SourceUrl);
        Assert.Equal(receipt.Snapshot.CheckedAt, document.SourceCheckedAt);
        Assert.Single(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" && a.Action == "Created").ToArrayAsync());
        var notifications = await f.Db.UserNotifications.Where(n => n.Type == UserNotificationTypes.CaseDocument).Select(n => n.Id).ToArrayAsync();
        Assert.Single(notifications);
        Assert.NotEmpty(await f.Db.CaseDocumentNotificationDispatches.ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
        Assert.Equal("uploaded", (await f.PublishCourt(receipt)).Status);
        Assert.Equal(document.Id, (await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(notifications, await f.Db.UserNotifications.Where(n => n.Type == UserNotificationTypes.CaseDocument).Select(n => n.Id).ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CourtDeadlineControlsAreCommittedBeforeFirstSummaryWriteFailureAndSurviveRetry(bool postgres, bool duplicateSource)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1);
        var firstAct = CourtDeadlineSource(f.Case.CourtUrl).CourtActs[0];
        var source = new SudrfCourtCaseData
        {
            SourceUrl = f.Case.CourtUrl,
            Instance = "appeal",
            CourtActs = [firstAct, duplicateSource ? firstAct : new SudrfCourtAct
            { Title = "Второе апелляционное определение", ActDate = new DateTime(2026, 10, 3), SourceUrl = f.Case.CourtUrl }]
        };
        var expectedCount = duplicateSource ? 1 : 2;
        var realtime = new CourtControlRealtimePublisher();
        int? deadlinesBeforeWrite = null, notificationsBeforeWrite = null, tasksBeforeWrite = null;
        var transactionDuringWrite = false;
        var failingFiles = new RejectingCaseDocumentStorage(f.LocalCaseStorage, async () =>
        {
            transactionDuringWrite = f.Db.Database.CurrentTransaction is not null;
            await using var observer = f.Observer();
            deadlinesBeforeWrite = await observer.ClientCaseProceduralDeadlines.CountAsync(d => d.ClientCaseId == f.Case.Id);
            notificationsBeforeWrite = await observer.UserNotifications.CountAsync(n => n.Type == UserNotificationTypes.CaseProceduralDeadline);
            tasksBeforeWrite = await observer.TaskJournalItems.CountAsync(t => t.ClientCaseId == f.Case.Id
                && t.TagsJson.Contains("case-deadline:entity-"));
        });
        await Assert.ThrowsAsync<IOException>(() => f.Controller(source, failingFiles, useManagedStorage: false, realtime: realtime)
            .RunCourtSync(f.Case.Id, null, CancellationToken.None));
        Assert.Equal(1, failingFiles.WriteAttempts);
        Assert.False(transactionDuringWrite);
        Assert.Equal(expectedCount, deadlinesBeforeWrite);
        Assert.Equal(expectedCount, notificationsBeforeWrite);
        Assert.Equal(expectedCount, tasksBeforeWrite);
        Assert.Equal(1, realtime.TaskChanges);
        await using var committed = f.Observer();
        var deadlines = await committed.ClientCaseProceduralDeadlines.AsNoTracking().OrderBy(d => d.Id).ToArrayAsync();
        Assert.Equal(expectedCount, deadlines.Length);
        Assert.All(deadlines, d => { Assert.Null(d.ClientCaseDocumentId); Assert.NotNull(d.SourceDocumentKey); Assert.Equal("active", d.Status); });
        Assert.Empty(await committed.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        var notificationIds = await committed.UserNotifications.AsNoTracking()
            .Where(n => n.Type == UserNotificationTypes.CaseProceduralDeadline).Select(n => n.Id).OrderBy(id => id).ToArrayAsync();
        Assert.Equal("false", await committed.SystemSettings.Where(s => s.Key == "notifications.email.enabled").Select(s => s.Value).SingleAsync());

        f.Db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<IOException>(() => f.Controller(source, failingFiles, useManagedStorage: false, realtime: realtime)
            .RunCourtSync(f.Case.Id, null, CancellationToken.None));
        Assert.Equal(2, failingFiles.WriteAttempts);
        Assert.Equal(1, realtime.TaskChanges);
        Assert.Equal(deadlines.Select(d => d.Id), await committed.ClientCaseProceduralDeadlines.AsNoTracking().OrderBy(d => d.Id).Select(d => d.Id).ToArrayAsync());
        Assert.Equal(notificationIds, await committed.UserNotifications.AsNoTracking()
            .Where(n => n.Type == UserNotificationTypes.CaseProceduralDeadline).Select(n => n.Id).OrderBy(id => id).ToArrayAsync());
        f.Db.ChangeTracker.Clear();
        var completed = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(Assert.IsType<OkObjectResult>(
            (await f.Controller(source, useManagedStorage: false, realtime: realtime).RunCourtSync(f.Case.Id, null, CancellationToken.None)).Result).Value);
        Assert.Equal(0, completed.CreatedProceduralDeadlines);
        Assert.Equal(expectedCount, completed.CreatedCourtActDocuments);
        Assert.Equal(2, realtime.TaskChanges);
        Assert.All(await committed.ClientCaseProceduralDeadlines.AsNoTracking().ToArrayAsync(), d => Assert.NotNull(d.ClientCaseDocumentId));
        Assert.Equal(notificationIds, await committed.UserNotifications.AsNoTracking()
            .Where(n => n.Type == UserNotificationTypes.CaseProceduralDeadline).Select(n => n.Id).OrderBy(id => id).ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CourtDeadlineIdentitySurvivesSummaryLinkRemovalWithoutReplacingManualValues(bool postgres, bool legacyKey)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1);
        var source = CourtDeadlineSource(f.Case.CourtUrl);
        await GenerateCourtAsync(f, source);
        var original = await f.Db.ClientCaseProceduralDeadlines.SingleAsync();
        var document = await f.Db.ClientCaseDocuments.SingleAsync();
        var canonicalKey = document.DocumentKey;
        Assert.Equal(canonicalKey, original.SourceDocumentKey);
        var notificationIds = await f.Db.UserNotifications.AsNoTracking()
            .Where(n => n.Type == UserNotificationTypes.CaseProceduralDeadline).Select(n => n.Id).OrderBy(id => id).ToArrayAsync();
        Assert.Single(notificationIds);
        var deadlineId = original.Id;
        var manualDueAt = new DateTime(2027, 2, 7);
        original.DueAt = manualDueAt;
        original.Notes = "Owned manual deadline notes";
        if (legacyKey)
        {
            var act = source.CourtActs.Single();
            var raw = $"{act.Title}|{act.ActDate:yyyy-MM-dd}|{act.Result}|{act.SourceUrl}";
            document.DocumentKey = "sudrf-act-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant()[..24];
            original.SourceDocumentKey = document.DocumentKey;
        }
        await f.Db.SaveChangesAsync();
        await f.Db.ClientCaseDocuments.Where(d => d.Id == document.Id).ExecuteDeleteAsync();
        f.Db.ChangeTracker.Clear();
        Assert.Null((await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).ClientCaseDocumentId);

        var repeated = await GenerateCourtAsync(f, source);
        Assert.Equal(0, repeated.CreatedProceduralDeadlines);
        var preserved = await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync();
        Assert.Equal(deadlineId, preserved.Id);
        Assert.Equal(canonicalKey, preserved.SourceDocumentKey);
        Assert.Equal(manualDueAt, preserved.DueAt);
        Assert.Equal("Owned manual deadline notes", preserved.Notes);
        Assert.Equal(notificationIds, await f.Db.UserNotifications.AsNoTracking()
            .Where(n => n.Type == UserNotificationTypes.CaseProceduralDeadline).Select(n => n.Id).OrderBy(id => id).ToArrayAsync());
        var replacement = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.Equal(replacement.Id, preserved.ClientCaseDocumentId);
        Assert.Equal(canonicalKey, replacement.DocumentKey);
        Assert.Single(await f.Db.TaskJournalItems.AsNoTracking().Where(t => t.ClientCaseId == f.Case.Id
            && t.TagsJson.Contains($"case-deadline:entity-{deadlineId}")).ToArrayAsync());

        await f.Db.ClientCaseDocuments.Where(d => d.Id == replacement.Id).ExecuteDeleteAsync();
        f.Db.ChangeTracker.Clear();
        var missing = await GenerateCourtAsync(f, new SudrfCourtCaseData { SourceUrl = source.SourceUrl, Instance = "appeal" });
        Assert.Contains(missing.RemovedItems, item => item.Contains("процессуальный срок", StringComparison.Ordinal));
        Assert.Equal("archived", (await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).Status);
        Assert.Equal("cancelled", (await f.Db.TaskJournalItems.AsNoTracking().SingleAsync(t => t.ClientCaseId == f.Case.Id
            && t.TagsJson.Contains($"case-deadline:entity-{deadlineId}"))).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtDeadlineIdentityDoesNotSilentlyClaimAmbiguousLegacyDeadlines(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1);
        var source = CourtDeadlineSource(f.Case.CourtUrl);
        await GenerateCourtAsync(f, source);
        var original = await f.Db.ClientCaseProceduralDeadlines.SingleAsync();
        original.SourceDocumentKey = null;
        f.Db.ClientCaseProceduralDeadlines.Add(new ClientCaseProceduralDeadline
        {
            ClientCaseId = f.Case.Id,
            ClientCaseDocumentId = original.ClientCaseDocumentId,
            SourceType = "sudrf",
            SourceUrl = source.SourceUrl,
            DeadlineType = original.DeadlineType,
            Title = "Owned ambiguous legacy deadline",
            DueAt = original.DueAt
        });
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        var report = await GenerateCourtAsync(f, source);
        Assert.Equal(0, report.CreatedProceduralDeadlines);
        Assert.Contains(report.ConflictItems, item => item.Contains("процессуаль", StringComparison.Ordinal));
        var rows = await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().ToArrayAsync();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => { Assert.Null(row.SourceDocumentKey); Assert.Equal("active", row.Status); });
    }

    private static SudrfCourtCaseData CourtDeadlineSource(string url) => new()
    {
        SourceUrl = url,
        Instance = "appeal",
        CourtActs = [new SudrfCourtAct
        { Title = "Апелляционное определение", ActDate = new DateTime(2026, 10, 1), SourceUrl = url }]
    };

    private static async Task<CasesV2Controller.ClientCaseCourtSyncRunDto> GenerateCourtAsync(Fixture fixture, SudrfCourtCaseData source)
        => Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(Assert.IsType<OkObjectResult>(
            (await fixture.Controller(source).RunCourtSync(fixture.Case.Id, null, CancellationToken.None)).Result).Value);

    public static IEnumerable<object[]> GeneratedPaths()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var path in new[] { "template", "work-order", "invoice", "act", "contract", "court" })
                foreach (var copies in new[] { 1, 2 })
                    yield return [postgres, path, copies];
    }

    [Theory]
    [MemberData(nameof(GeneratedPaths))]
    public async Task GenerationPublishesOnlyWithRequiredCopiesAndExactManagedBinding(bool postgres, string path, int copies)
    {
        await using var f = await Fixture.CreateAsync(postgres, copies);
        var controller = f.Controller();
        controller.Request.Headers["X-Upload-Key"] = Guid.NewGuid().ToString();
        var result = await GenerateAsync(controller, f.Case.Id, path);

        if (copies == 2)
        {
            // Only the local replica can be written synchronously. The required
            // mirror remains queued; neither document nor finance may be visible.
            // Check financial visibility first so a baseline failure identifies
            // a partially committed business operation rather than only a file.
            Assert.Empty(await f.Db.Payments.AsNoTracking().ToArrayAsync());
            Assert.Empty(await f.Db.Invoices.AsNoTracking().ToArrayAsync());
            Assert.Empty(await f.Db.Acts.AsNoTracking().ToArrayAsync());
            Assert.Empty(await f.Db.Contracts.AsNoTracking().ToArrayAsync());
            Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
            Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
            Assert.Empty(await f.Db.ObjectAuditEvents.AsNoTracking()
                .Where(e => e.ObjectType == "client-case-document").ToArrayAsync());
            Assert.Empty(await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().ToArrayAsync());
            Assert.Equal("approved", (await f.Db.ClientCaseWorkOrders.AsNoTracking().SingleAsync()).Status);
            if (path == "court")
            {
                var report = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(Assert.IsType<OkObjectResult>(result).Value);
                Assert.Equal(0, report.CreatedCourtActDocuments);
            }
            else
            {
                Assert.IsNotType<CreatedResult>(result);
            }
            Assert.Equal(1, f.Local.PutCount);
            Assert.Equal(0, f.Mirror.PutCount);
            Assert.Single(await f.Db.StorageOperations.AsNoTracking()
                .Where(o => o.OperationType == "commit").ToArrayAsync());
            return;
        }

        if (path == "court")
        {
            var report = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncRunDto>(Assert.IsType<OkObjectResult>(result).Value);
            Assert.Equal(1, report.CreatedCourtActDocuments);
        }
        else Assert.IsType<CreatedResult>(result);

        var document = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        var binding = await f.Db.StorageProductBindings.AsNoTracking().SingleAsync();
        Assert.Equal(LegacyStorageSources.CaseDocuments, binding.Source);
        Assert.Equal(document.Id, binding.SourceId);
        Assert.Equal(1, binding.Version);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(0, f.Mirror.PutCount);
        var read = await f.Reader.OpenReadAsync(LegacyStorageSources.CaseDocuments, document.Id,
            document.OriginalFileName, document.ContentType, CancellationToken.None);
        Assert.Equal(ProductStorageReadStatus.Available, read.Status);
        Assert.NotNull(read.File);
        await using var file = read.File;
        using var output = new MemoryStream();
        await file.Content.CopyToAsync(output);
        var bytes = output.ToArray();
        Assert.Equal(document.SizeBytes, bytes.LongLength);
        Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Contains("Контроль генерации", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal("text/html; charset=utf-8", document.ContentType);
        Assert.Equal(f.User.Id, document.UploadedByUserId);
        var audit = await f.Db.ObjectAuditEvents.AsNoTracking()
            .SingleAsync(e => e.ObjectType == "client-case-document" && e.ObjectId == document.Id);
        Assert.Equal(path == "court" ? "Created" : "Generated", audit.Action);
        if (path == "invoice")
        {
            var payment = await f.Db.Payments.AsNoTracking().SingleAsync();
            Assert.Equal(35m, payment.Amount);
            Assert.Equal($"invoice-{payment.Id}", document.DocumentKey);
            Assert.Equal($"case-{f.Case.Id}-invoice-{payment.Id}.html", document.OriginalFileName);
        }
        else if (path == "act")
        {
            var act = await f.Db.Acts.AsNoTracking().SingleAsync();
            Assert.Equal(35m, act.Amount);
            Assert.Equal($"act-{act.Id}", document.DocumentKey);
            Assert.Equal($"case-{f.Case.Id}-act-{act.Id}.html", document.OriginalFileName);
        }
        else if (path == "contract")
        {
            var contract = await f.Db.Contracts.AsNoTracking().SingleAsync();
            Assert.Equal(35m, contract.Amount);
            Assert.Equal($"contract-{contract.Id}", document.DocumentKey);
            Assert.Equal($"case-{f.Case.Id}-contract-{contract.Id}.html", document.OriginalFileName);
        }
        else if (path == "court")
        {
            Assert.Equal("sudrf", document.SourceType);
            Assert.Equal(f.Case.CourtUrl, document.SourceUrl);
            Assert.NotNull(document.SourceCheckedAt);
        }
    }

    private static async Task<IActionResult> GenerateAsync(CasesV2Controller controller, int caseId, string path)
        => path switch
        {
            "template" => (await controller.GenerateDocumentTemplate(caseId,
                new() { TemplateKey = "claim", Status = "Подготовлен" }, CancellationToken.None)).Result!,
            "work-order" => (await controller.GenerateWorkOrderDocument(caseId, CancellationToken.None)).Result!,
            "invoice" => (await controller.CreateInvoiceFromWorkOrder(caseId,
                new() { Number = "OWNED-GENERATED-INVOICE", Date = new DateTime(2026, 10, 1) }, CancellationToken.None)).Result!,
            "act" => (await controller.CreateActFromWorkOrder(caseId,
                new() { Number = "OWNED-GENERATED-ACT", Date = new DateTime(2026, 10, 1) }, CancellationToken.None)).Result!,
            "contract" => (await controller.CreateContractFromWorkOrder(caseId,
                new() { Number = "OWNED-GENERATED-CONTRACT", Date = new DateTime(2026, 10, 1) }, CancellationToken.None)).Result!,
            "court" => (await controller.RunCourtSync(caseId, null, CancellationToken.None)).Result!,
            _ => throw new ArgumentOutOfRangeException(nameof(path))
        };

    private sealed class NoopEmailService : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;
        public Task SendEmailAsync(string toEmail, string subject, string body, bool isHtml = false,
            CancellationToken cancellationToken = default, EmailSendSettings? settings = null) => Task.CompletedTask;
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName,
            byte[] attachmentBytes, string attachmentContentType) => Task.CompletedTask;
    }

    private sealed class NoopTelegramNotificationService : ITelegramNotificationService
    {
        public Task<TelegramSendResult> SendMessageAsync(long chatId, string text, CancellationToken cancellationToken = default)
            => Task.FromResult(new TelegramSendResult(true, "sent"));
        public Task<TelegramSendResult> SendMessageWithTokenAsync(long chatId, string text, string botToken,
            CancellationToken cancellationToken = default, TelegramBotConnectionSettings? connection = null)
            => Task.FromResult(new TelegramSendResult(true, "sent"));
        public Task<TelegramNotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TelegramNotificationSettingsDto());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "pp-generated-case-" + Guid.NewGuid().ToString("N"));
        private SqliteConnection? sqlite;
        private NpgsqlConnection? admin;
        private string? databaseName;
        private TestEnvironment environment = null!;
        private IConfiguration configuration = null!;
        private IManagedCaseDocumentUploadService managed = null!;
        private IStorageProviderRegistry providers = null!;
        private IOptionsMonitor<StorageOrchestrationOptions> monitor = null!;
        public PaymentContext Db { get; private set; } = null!;
        public User User { get; private set; } = null!;
        public ClientCase Case { get; private set; } = null!;
        public ObservedProvider Local { get; private set; } = null!;
        public ObservedProvider Mirror { get; private set; } = null!;
        public IProductStorageReader Reader { get; private set; } = null!;
        public bool UseNotificationService { get; set; }

        public static async Task<Fixture> CreateAsync(bool postgres, int copies, bool autoPublication = false, params IInterceptor[] interceptors)
        {
            var f = new Fixture();
            try
            {
                Directory.CreateDirectory(f.root);
                if (postgres)
                {
                    var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
                    Assert.False(string.IsNullOrWhiteSpace(raw), "Owned generated-document baseline requires local PostgreSQL.");
                    var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
                    Assert.Contains(settings.Host, new[] { "127.0.0.1", "localhost", "::1" });
                    f.admin = new NpgsqlConnection(settings.ConnectionString);
                    await f.admin.OpenAsync();
                    var name = "pp_generated_case_" + Guid.NewGuid().ToString("N");
                    await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", f.admin)) await create.ExecuteNonQueryAsync();
                    f.databaseName = name;
                    Console.WriteLine($"Owned generated case database: {name}");
                    settings.Database = name;
                    f.Db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                        .UseNpgsql(settings.ConnectionString).AddInterceptors(interceptors).Options);
                }
                else
                {
                    f.sqlite = new SqliteConnection("Data Source=:memory:");
                    await f.sqlite.OpenAsync();
                    f.Db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                        .UseSqlite(f.sqlite).UsePayPlannerSqliteMigrations().AddInterceptors(interceptors).Options);
                }
                await f.Db.Database.MigrateAsync();
                Assert.False(f.Db.Database.HasPendingModelChanges());
                var email = await f.Db.SystemSettings.SingleOrDefaultAsync(s => s.Key == "notifications.email.enabled");
                if (email is null) f.Db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
                else email.Value = "false";
                var role = new Role
                {
                    Name = "owned-generated-case-editor",
                    Permissions = new[] { RolePermissionCatalog.Cases, RolePermissionCatalog.Accounts,
                        RolePermissionCatalog.Acts, RolePermissionCatalog.Contracts }.Select(section => new RolePermission
                        { Section = section, CanView = true, CanCreate = true, CanEdit = true, ViewScope = DataVisibilityScope.Own }).ToList()
                };
                f.User = new User
                {
                    Email = "generated-case@example.local",
                    FullName = "Автор генерации",
                    PasswordHash = "synthetic",
                    IsActive = true,
                    IsApproved = true,
                    Role = role
                };
                var client = new Client { Name = "Контроль генерации", IsActive = true, ResponsibleUser = f.User };
                f.Case = new ClientCase
                {
                    Client = client,
                    ResponsibleUser = f.User,
                    Title = "Контроль генерации",
                    Status = ClientCaseStatus.Open,
                    CourtUrl = "https://owned--test.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42"
                };
                f.Db.ClientCaseWorkOrders.Add(new ClientCaseWorkOrder
                {
                    ClientCase = f.Case,
                    Title = "Контроль генерации",
                    Status = "approved",
                    Currency = "RUB",
                    TotalAmount = 35m,
                    Items = [new ClientCaseWorkOrderItem
                    { ServiceName = "Синтетическая услуга", Quantity = 2m, UnitPrice = 17.5m, TotalAmount = 35m, SortOrder = 1 }]
                });
                await f.Db.SaveChangesAsync();
                f.environment = new TestEnvironment(f.root);
                f.configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FileStorage:RootPath"] = Path.Combine(f.root, "files"),
                    ["StorageOrchestration:LocalRoot"] = Path.Combine(f.root, "objects"),
                    ["Email:Enabled"] = "false",
                    ["BackgroundServices:Enabled"] = "false"
                }).Build();
                var mirrorConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["StorageOrchestration:LocalRoot"] = Path.Combine(f.root, "mirror") }).Build();
                f.Local = new ObservedProvider("local", new LocalStorageProvider(f.environment, f.configuration));
                f.Mirror = new ObservedProvider("mirror", new LocalStorageProvider(f.environment, mirrorConfiguration));
                var registry = new StorageProviderRegistry([f.Local, f.Mirror, new LegacyLocalStorageProvider(f.environment, f.configuration)]);
                var options = new StaticOptions(new StorageOrchestrationOptions
                {
                    Enabled = true,
                    UploadCapacityChecksEnabled = true,
                    AutoCaseDocumentPublicationEnabled = autoPublication,
                    UploadMinimumFreeBytes = 0,
                    ProductWriteEnabledClasses = ["case-document"],
                    ProductReadEnabledClasses = ["case-document"],
                    ProviderConnections = new() { ["mirror"] = "owned-synthetic" },
                    DefaultPolicy = new()
                    {
                        RequiredCopies = copies,
                        WriteProviders = copies == 2 ? ["local", "mirror"] : ["local"],
                        ReadProviders = copies == 2 ? ["local", "mirror"] : ["local"]
                    }
                });
                var policies = new StoragePolicyService(options, registry);
                f.providers = registry;
                f.monitor = options;
                Assert.True(policies.Evaluate("case-document").IsValid);
                var storage = new StorageObjectService(f.Db, policies, registry, options);
                f.managed = new ManagedCaseDocumentUploadService(f.Db, storage, new ProductStorageBindingService(f.Db, policies),
                    f.environment, f.configuration, options, new StorageStageCapacityService(f.Db, registry, options, f.environment, f.configuration));
                Assert.True(f.managed.Enabled);
                f.Reader = new ProductStorageReader(f.Db, storage, options);
                return f;
            }
            catch (Exception error)
            {
                try { await f.DisposeAsync(); }
                catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
                throw;
            }
        }

        public ICaseDocumentStorage LocalCaseStorage => new LocalCaseDocumentStorage(environment, configuration);
        public void SetManagedEnabled(bool enabled) => monitor.CurrentValue.Enabled = enabled;
        public async Task<int> RunCourtSchedulerAsync(SudrfCourtCaseData source)
        {
            var role = await Db.Roles.SingleAsync(r => r.Id == User.RoleId);
            role.Name = "admin";
            var clientCase = await Db.ClientCases.SingleAsync(c => c.Id == Case.Id);
            clientCase.CourtSyncMonitoringEnabled = true;
            await Db.SaveChangesAsync();
            var services = new ServiceCollection();
            services.AddSingleton(Db);
            services.AddSingleton(configuration);
            services.AddSingleton<IWebHostEnvironment>(environment);
            services.AddSingleton<ICaseDocumentStorage>(LocalCaseStorage);
            services.AddSingleton<ISudrfCourtSyncService>(new FixedCourtSource(Case.CourtUrl, source));
            services.AddSingleton(managed);
            services.AddOptions();
            services.Configure<AppFeatureFlagsOptions>(o => o.Legal.ExternalCourtSync = true);
            await using var provider = services.BuildServiceProvider();
            return await new ClientCaseCourtSyncSchedulerService(provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IOptionsMonitor<AppFeatureFlagsOptions>>(), configuration,
                NullLogger<ClientCaseCourtSyncSchedulerService>.Instance).RunOnceAsync(default);
        }
        public PaymentContext Observer() => Db.Database.IsNpgsql()
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(Db.Database.GetConnectionString()!).Options)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(sqlite!).UsePayPlannerSqliteMigrations().Options);

        public void PrepareIdentityObservers()
        {
            if (Db.Database.IsNpgsql()) return;
            using var target = new SqliteConnection($"Data Source={Path.Combine(root, "identity-concurrency.db")};Pooling=False");
            target.Open(); sqlite!.BackupDatabase(target);
        }
        public PaymentContext IdentityObserver() => Db.Database.IsNpgsql() ? Observer()
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "identity-concurrency.db")};Pooling=False;Default Timeout=30")
                .UsePayPlannerSqliteMigrations().Options);

        public async Task CopyPendingAsync(int expectedAvailableReplicas = 2)
        {
            var result = await new StorageOperationProcessor(Db, providers, monitor, TimeProvider.System,
                NullLogger<StorageOperationProcessor>.Instance).ProcessBatchAsync("owned-court-copy", default);
            Assert.Equal(1, result.Succeeded);
            Assert.Equal(expectedAvailableReplicas, await Db.StorageReplicas.CountAsync(r => r.Status == StorageReplicaStatuses.Available));
        }

        public Task<IReadOnlyList<CaseDocumentUploadState>?> UploadStates(CourtDocumentSourceReceipt receipt)
            => new CaseDocumentUploadStatusReader(Db, monitor)
                .ReadAsync(receipt.Snapshot.CaseId, receipt.Snapshot.ActorId, [receipt.Snapshot.AttemptId], default);

        public Task<IReadOnlyList<CaseDocumentUploadState>?> FinancialUploadStates(Guid[] keys)
            => new CaseDocumentUploadStatusReader(Db, monitor)
                .ReadAsync(Case.Id, User.Id, keys, default);

        public StorageOrchestrationOptions FinancialStorageOptions => monitor.CurrentValue;

        public GeneratedFinancialAttemptRecovery FinancialRecovery(PaymentContext? scope = null, TimeProvider? clock = null)
        {
            var context = scope ?? Db;
            var policies = new StoragePolicyService(monitor, providers);
            var storage = new StorageObjectService(context, policies, providers, monitor);
            var uploads = new ManagedCaseDocumentUploadService(context, storage, new ProductStorageBindingService(context, policies),
                environment, configuration, monitor, new StorageStageCapacityService(context, providers, monitor, environment, configuration));
            var notifications = new CaseDocumentNotificationService(context, new NotificationService(context, configuration,
                new NoopEmailService(), new NoopTelegramNotificationService(), NullLogger<NotificationService>.Instance));
            var registry = new GeneratedFinancialDocumentRegistry(context);
            var publication = new GeneratedFinancialDocumentPublication(context, new CaseDocumentPublicationRecords(context, notifications), registry, policies);
            return new(context, registry, publication, uploads, LocalCaseStorage, monitor, clock ?? TimeProvider.System,
                NullLogger<GeneratedFinancialAttemptRecovery>.Instance);
        }

        public GeneratedCaseReservationRecovery ReservationRecovery(PaymentContext? scope = null, TimeProvider? clock = null)
        {
            var context = scope ?? Db;
            var policies = new StoragePolicyService(monitor, providers);
            var storage = new StorageObjectService(context, policies, providers, monitor);
            var uploads = new ManagedCaseDocumentUploadService(context, storage, new ProductStorageBindingService(context, policies),
                environment, configuration, monitor, new StorageStageCapacityService(context, providers, monitor, environment, configuration));
            var notifications = new CaseDocumentNotificationService(context, new NotificationService(context, configuration,
                new NoopEmailService(), new NoopTelegramNotificationService(), NullLogger<NotificationService>.Instance));
            var records = new CaseDocumentPublicationRecords(context, notifications);
            return new(context, uploads, LocalCaseStorage, monitor, clock ?? TimeProvider.System,
                NullLogger<GeneratedCaseReservationRecovery>.Instance, new GeneratedWorkOrderDocumentPublication(context, records),
                new GeneratedTemplateDocumentPublication(context, records),
                new GeneratedCourtDocumentPublication(context, records, new CourtDocumentSourceRegistry(context)));
        }

        public Task<StorageOperationBatchResult> RunCourtQueueAsync(TimeProvider? clock = null)
        {
            monitor.CurrentValue.AutoCaseDocumentPublicationEnabled = true;
            var policies = new StoragePolicyService(monitor, providers);
            var notifications = new CaseDocumentNotificationService(Db, new NotificationService(Db, configuration,
                new NoopEmailService(), new NoopTelegramNotificationService(), NullLogger<NotificationService>.Instance));
            var records = new CaseDocumentPublicationRecords(Db, notifications);
            var publisher = new CaseDocumentPublicationProcessor(Db, managed, records, notifications, LocalCaseStorage,
                policies, monitor, clock ?? TimeProvider.System, NullLogger<CaseDocumentPublicationProcessor>.Instance,
                new GeneratedWorkOrderDocumentPublication(Db, records), new GeneratedTemplateDocumentPublication(Db, records),
                new GeneratedCourtDocumentPublication(Db, records, new CourtDocumentSourceRegistry(Db)),
                new GeneratedFinancialDocumentPublication(Db, records, new GeneratedFinancialDocumentRegistry(Db), policies));
            return new StorageOperationProcessor(Db, providers, monitor, clock ?? TimeProvider.System,
                NullLogger<StorageOperationProcessor>.Instance, casePublications: publisher).ProcessBatchAsync("owned-court-queue", default);
        }

        public async Task<ManagedCaseDocumentUploadResult> PublishCourt(CourtDocumentSourceReceipt receipt, bool failLate = false,
            bool corruptBytes = false)
        {
            var registry = new CourtDocumentSourceRegistry(Db);
            var snapshot = receipt.Snapshot;
            var source = new ManagedCaseDocumentGenerationSource(snapshot.CaseId, snapshot.ActorId, snapshot.AttemptId,
                snapshot.DocumentKey, snapshot.DocumentKey, snapshot.FileName, "text/html; charset=utf-8",
                CourtSyncNormalization.DocumentTypes.CourtEventSummary, "received", receipt.Generation);
            var bytes = await registry.ReadContentAsync(source, default) ?? throw new InvalidOperationException("Owned court content unavailable");
            if (corruptBytes) bytes[0] ^= 1;
            var notifications = new CaseDocumentNotificationService(Db, new NotificationService(Db, configuration,
                new NoopEmailService(), new NoopTelegramNotificationService(), NullLogger<NotificationService>.Instance));
            var publication = new GeneratedCourtDocumentPublication(Db, new CaseDocumentPublicationRecords(Db, notifications), registry);
            var atomic = new ManagedCaseDocumentPublication((path, publish, ct) => CaseDocumentAtomicSave.SaveAsync(Db,
                LocalCaseStorage, path, () => Task.CompletedTask, async () =>
                {
                    var result = await publish();
                    if (failLate) throw new IOException("Owned late court publication failure");
                    return result;
                }, ct, preserveFileOnFailure: true));
            var resumed = await managed.TryResumeGeneratedAsync(source.CaseId, source.ActorId, source.UploadKey,
                source.Generation.Kind, source.Generation.RequestSha256,
                (actor, caseId, ct) => publication.CanPublishAsync(actor, caseId, receipt.Generation, ct),
                publication.PersistAsync, atomic, default, publication.CanPublishAsync);
            if (resumed is not null) return resumed;
            using var stream = new MemoryStream(bytes, writable: false);
            var file = new FormFile(stream, 0, bytes.Length, "file", source.FileName)
            { Headers = new HeaderDictionary(), ContentType = source.ContentType };
            return await managed.GenerateAsync(new(source.CaseId, source.ActorId, receipt.Snapshot.ActorName, source.UploadKey,
                    file, source.FileName, source.DocumentType, source.Status, source.DocumentKey), receipt.Generation,
                (actor, caseId, ct) => publication.CanPublishAsync(actor, caseId, receipt.Generation, ct),
                publication.PersistAsync, atomic, default, publication.CanPublishAsync);
        }

        public async Task<ManagedCaseDocumentUploadResult> PublishFinancial(GeneratedFinancialDocumentReceipt receipt,
            bool failLate = false, bool corruptBytes = false)
        {
            var source = FinancialSource(receipt);
            var registry = new GeneratedFinancialDocumentRegistry(Db);
            var notifications = new CaseDocumentNotificationService(Db, new NotificationService(Db, configuration,
                new NoopEmailService(), new NoopTelegramNotificationService(), NullLogger<NotificationService>.Instance));
            var publication = new GeneratedFinancialDocumentPublication(Db, new CaseDocumentPublicationRecords(Db, notifications),
                registry, new StoragePolicyService(monitor, providers));
            var atomic = new ManagedCaseDocumentPublication((path, publish, ct) => CaseDocumentAtomicSave.SaveAsync(Db,
                LocalCaseStorage, path, () => Task.CompletedTask, async () =>
                {
                    var result = await publish();
                    if (failLate) throw new IOException("Owned late financial publication failure");
                    if (!await publication.CanPublishAsync(source, ct)) throw new InvalidOperationException("Owned final financial authority failure");
                    return result;
                }, ct, preserveFileOnFailure: true));
            var resumed = await managed.TryResumeGeneratedAsync(source.CaseId, source.ActorId, source.UploadKey,
                source.Generation.Kind, source.Generation.RequestSha256,
                (actor, caseId, ct) => publication.CanPublishAsync(actor, caseId, receipt.Generation, ct),
                publication.PersistAsync, atomic, default, publication.CanPublishAsync);
            if (resumed is not null) return resumed;
            var frozen = await registry.ReadFrozenAsync(receipt, default) ?? throw new InvalidOperationException("Owned financial bytes unavailable");
            if (corruptBytes) frozen.Bytes[0] ^= 1;
            using var stream = new MemoryStream(frozen.Bytes, writable: false);
            var file = new FormFile(stream, 0, frozen.Bytes.Length, "file", source.FileName)
            { Headers = new HeaderDictionary(), ContentType = source.ContentType };
            return await managed.GenerateAsync(new(source.CaseId, source.ActorId, frozen.Domain.ActorName, source.UploadKey,
                file, source.FileName, source.DocumentType, source.Status, source.DocumentKey), receipt.Generation,
                (actor, caseId, ct) => publication.CanPublishAsync(actor, caseId, receipt.Generation, ct),
                publication.PersistAsync, atomic, default, publication.CanPublishAsync);
        }

        public CasesV2Controller Controller(SudrfCourtCaseData? courtData = null, ICaseDocumentStorage? caseFiles = null,
            bool useManagedStorage = true, IRealtimeNotificationPublisher? realtime = null)
            => new(Db, environment, configuration: configuration, caseDocumentStorage: caseFiles,
            notificationService: UseNotificationService ? new NotificationService(Db, configuration,
                new NoopEmailService(), new NoopTelegramNotificationService(), NullLogger<NotificationService>.Instance) : null,
            managedCaseUploads: useManagedStorage ? managed : null, productStorageReader: Reader, realtimePublisher: realtime,
            financialDocumentPublication: new GeneratedFinancialDocumentPublication(Db,
                new CaseDocumentPublicationRecords(Db, new CaseDocumentNotificationService(Db,
                    new NotificationService(Db, configuration, new NoopEmailService(), new NoopTelegramNotificationService(),
                        NullLogger<NotificationService>.Instance))), new GeneratedFinancialDocumentRegistry(Db),
                new StoragePolicyService(monitor, providers)),
            sudrfCourtSyncService: new FixedCourtSource(Case.CourtUrl, courtData))
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, User.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, User.FullName)], "owned-generated-fixture"))
                    }
                }
            };

        public CasesV2Controller LegacyController(PaymentContext context)
            => new(context, environment, configuration: configuration)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, User.Id.ToString(CultureInfo.InvariantCulture)),
                    new Claim(ClaimTypes.Name, User.FullName)], "owned-generated-fixture"))
                    }
                }
            };

        public async ValueTask DisposeAsync()
        {
            if (Db is not null) await Db.DisposeAsync();
            if (sqlite is not null) await sqlite.DisposeAsync();
            if (admin is not null)
            {
                try
                {
                    if (databaseName is not null)
                    {
                        await using var sessions = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @name", admin);
                        sessions.Parameters.AddWithValue("name", databaseName);
                        var deadline = DateTime.UtcNow.AddSeconds(5);
                        long remaining;
                        do { remaining = (long)(await sessions.ExecuteScalarAsync())!; if (remaining == 0) break; await Task.Delay(50); }
                        while (DateTime.UtcNow < deadline);
                        Assert.Equal(0L, remaining);
                        Assert.StartsWith("pp_generated_case_", databaseName);
                        await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\"", admin);
                        await drop.ExecuteNonQueryAsync();
                    }
                }
                finally { await admin.DisposeAsync(); }
            }
            var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            Assert.Equal(expectedParent, Path.GetDirectoryName(Path.GetFullPath(root)), ignoreCase: OperatingSystem.IsWindows());
            Assert.StartsWith("pp-generated-case-", Path.GetFileName(root));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class FixedCourtSource(string sourceUrl, SudrfCourtCaseData? courtData = null) : ISudrfCourtSyncService
    {
        public bool CanHandle(string? url) => url == sourceUrl;
        public Task<SudrfCourtCaseData> FetchCaseAsync(string url, CancellationToken ct, string? courtCaseNumber = null)
            => Task.FromResult(courtData ?? new SudrfCourtCaseData
            {
                SourceUrl = sourceUrl,
                CourtName = "Синтетический суд",
                CaseNumber = "OWNED-42",
                CourtActs = [new SudrfCourtAct { Title = "Контроль генерации", ActDate = new DateTime(2026, 10, 1),
                    SourceUrl = sourceUrl, Summary = "Синтетическая судебная сводка" }]
            });
    }

    private sealed class CourtControlRealtimePublisher : IRealtimeNotificationPublisher
    {
        public int TaskChanges { get; private set; }
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task PublishTaskJournalChangedAsync(CancellationToken ct = default) { TaskChanges++; return Task.CompletedTask; }
    }

    private sealed class RejectingCaseDocumentStorage(ICaseDocumentStorage inner, Func<Task> observe) : ICaseDocumentStorage
    {
        public int WriteAttempts { get; private set; }
        public string BuildRelativePath(int caseId, string name) => inner.BuildRelativePath(caseId, name);
        public string ResolvePath(string path) => inner.ResolvePath(path);
        public bool Exists(string path) => inner.Exists(path);
        public void DeleteIfExists(string path) => inner.DeleteIfExists(path);
        public Task SaveAsync(string path, Stream content, CancellationToken ct) => RejectAsync(ct);
        public Task SaveAsync(string path, byte[] content, CancellationToken ct) => RejectAsync(ct);
        private async Task RejectAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            WriteAttempts++;
            await observe();
            throw new IOException("Owned synthetic summary write failure.");
        }
    }

    private sealed class ObservedProvider(string key, LocalStorageProvider inner) : IStorageProvider, IStorageCapacityProvider
    {
        public string Key => key;
        public StorageProviderCapabilities Capabilities => inner.Capabilities;
        public int PutCount { get; private set; }
        public int ReadCount { get; private set; }
        public Action? BeforeRead { get; set; }
        public Func<CancellationToken, Task>? BeforeReadAsync { get; set; }
        public Func<CancellationToken, Task>? BeforePutAsync { get; set; }
        public bool LosePutAcknowledgement { get; set; }
        public async Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct)
        {
            PutCount++;
            if (BeforePutAsync is not null) await BeforePutAsync(ct);
            var result = await inner.PutAsync(request, ct);
            if (LosePutAcknowledgement) return result with { Status = StorageOperationStatus.Unknown, Error = "Owned generated local PUT response lost." };
            return result;
        }
        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct) => inner.HeadAsync(locator, ct);
        public async Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct)
        {
            ReadCount++; BeforeRead?.Invoke();
            if (BeforeReadAsync is not null) await BeforeReadAsync(ct);
            return await inner.OpenReadAsync(locator, ct);
        }
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct) => inner.DeleteAsync(locator, ct);
        public Task<StorageCapacityResource> ObserveCapacityAsync(string resourceKey, string? connectionId, long minimumFreeBytes, CancellationToken ct)
            => inner.ObserveCapacityAsync(resourceKey, null, minimumFreeBytes, ct);
    }
    private sealed class AdvancingClock : TimeProvider
    {
        private TimeSpan offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + offset;
        public void Advance(TimeSpan duration) => offset += duration;
    }
    private sealed class CourtRecordsSavedInterceptor(Action expireLease) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Triggered { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && !Triggered && eventData.Context is PaymentContext db && db.Database.CurrentTransaction is not null
                && db.ChangeTracker.Entries<ObjectAuditEvent>().Any(e => e.Entity.ObjectType == "client-case-document")
                && db.ChangeTracker.Entries<UserNotification>().Any(e => e.Entity.Type == UserNotificationTypes.CaseDocument))
            {
                Triggered = true;
                expireLease();
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class StaticOptions(StorageOrchestrationOptions value) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => value;
        public StorageOrchestrationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }
    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
