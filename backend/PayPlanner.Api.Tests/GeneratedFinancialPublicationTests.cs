using System.Text;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
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
    public async Task TypedFinancialPublicationCopiesFrozenGraphAndOriginalActorExactlyOnce(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var receipt = await CaptureFinancialPublicationAsync(f, kind);
        var frozen = Assert.IsType<GeneratedFinancialFrozenContent>(await new GeneratedFinancialDocumentRegistry(f.Db).ReadFrozenAsync(receipt, default));
        Assert.Equal("pending", (await f.PublishFinancial(receipt)).Status);
        Assert.Equal(0, await FinancialRowsAsync(f.Db));
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Null((await f.Db.GeneratedFinancialDocumentAttempts.SingleAsync()).PublishedDocumentId);
        var order = await f.Db.ClientCaseWorkOrders.Include(o => o.Items).SingleAsync();
        order.Title = "Изменённый наряд"; order.Items.Single().Quantity = 100m;
        f.Case.Title = "Изменённое дело"; f.User.FullName = "Имя после принятия";
        await f.Db.SaveChangesAsync();
        await f.CopyPendingAsync();
        f.Db.ChangeTracker.Clear();
        var result = await f.PublishFinancial(receipt);
        Assert.Equal("uploaded", result.Status);
        await using var observer = f.Observer();
        var document = await observer.ClientCaseDocuments.SingleAsync();
        Assert.Equal(result.DocumentId, document.Id); Assert.Equal(frozen.Domain.ActorName, document.UploadedByName);
        Assert.Equal(document.Id, (await observer.GeneratedFinancialDocumentAttempts.SingleAsync()).PublishedDocumentId);
        Assert.Single(await observer.StorageProductBindings.ToArrayAsync());
        await AssertPublishedFinancialGraphAsync(observer, frozen.Domain);
        var auditType = kind switch { GeneratedFinancialDocumentKind.Invoice => "payment", GeneratedFinancialDocumentKind.Act => "act", _ => "contract" };
        var financeAudit = await observer.ObjectAuditEvents.Where(a => a.ObjectType == auditType && a.ObjectId == receipt.Proof.EntityId && a.Action == "Created").SingleAsync();
        Assert.Equal(f.User.Id, financeAudit.ActorUserId); Assert.Equal(frozen.Domain.ActorName, financeAudit.ActorUserName);
        Assert.Single(await observer.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" && a.Action == "Generated").ToArrayAsync());
        Assert.Single(await observer.UserNotifications.Where(n => n.Type == UserNotificationTypes.CaseDocument).ToArrayAsync());
        Assert.NotEmpty(await observer.CaseDocumentNotificationDispatches.ToArrayAsync());
        var read = await f.Reader.OpenReadAsync(LegacyStorageSources.CaseDocuments, document.Id, document.OriginalFileName, document.ContentType, default);
        await using (var file = Assert.IsType<StorageObjectReadResult>(read.File))
        { using var bytes = new MemoryStream(); await file.Content.CopyToAsync(bytes); Assert.Equal(frozen.Bytes, bytes.ToArray()); }
        var history = (await observer.ClientCases.SingleAsync()).EventHistory;
        Assert.Contains("Контроль генерации", history); Assert.Contains("Автор генерации", history);
        var replay = await f.PublishFinancial(receipt);
        Assert.Equal("uploaded", replay.Status); Assert.True(replay.IsReplay); Assert.Equal(document.Id, replay.DocumentId);
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
        await AssertPublishedFinancialGraphAsync(observer, frozen.Domain);
        Assert.Single(await observer.ClientCaseDocuments.ToArrayAsync());
        // The original actor must not leak into ordinary saves on this context.
        var client = new Client { Name = "Сохранение после публикации" };
        f.Db.Clients.Add(client); await f.Db.SaveChangesAsync();
        Assert.Null(f.Db.Entry(client).Property<int?>("CreatedByUserId").CurrentValue);
        Assert.Null(f.Db.Entry(client).Property<string?>("CreatedByUserName").CurrentValue);
    }

    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task TypedFinancialPublicationLateFailureRollsBackFinanceDocumentAuditAndWinner(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var receipt = await CaptureFinancialPublicationAsync(f, kind);
        var originalHistory = f.Case.EventHistory;
        Assert.Equal("pending", (await f.PublishFinancial(receipt)).Status); await f.CopyPendingAsync();
        await Assert.ThrowsAsync<IOException>(() => f.PublishFinancial(receipt, failLate: true));
        await using (var observer = f.Observer())
        {
            Assert.Equal(0, await FinancialRowsAsync(observer));
            Assert.Empty(await observer.ClientCaseDocuments.ToArrayAsync());
            Assert.Empty(await observer.StorageProductBindings.ToArrayAsync());
            Assert.Empty(await observer.ObjectAuditEvents.Where(a => a.ObjectType == "payment" || a.ObjectType == "act" || a.ObjectType == "contract" || a.ObjectType == "client-case-document").ToArrayAsync());
            Assert.Empty(await observer.UserNotifications.Where(n => n.Type == UserNotificationTypes.CaseDocument).ToArrayAsync());
            Assert.Empty(await observer.CaseDocumentNotificationDispatches.ToArrayAsync());
            Assert.Null((await observer.GeneratedFinancialDocumentAttempts.SingleAsync()).PublishedDocumentId);
            Assert.Single(await observer.GeneratedFinancialNumberClaims.ToArrayAsync());
            Assert.Equal(originalHistory, (await observer.ClientCases.SingleAsync()).EventHistory);
            Assert.Equal("approved", (await observer.ClientCaseWorkOrders.SingleAsync()).Status);
        }
        var client = new Client { Name = "Сохранение после отката" }; f.Db.Clients.Add(client); await f.Db.SaveChangesAsync();
        Assert.Null(f.Db.Entry(client).Property<int?>("CreatedByUserId").CurrentValue);
        f.Db.ChangeTracker.Clear();
        Assert.Equal("uploaded", (await f.PublishFinancial(receipt)).Status);
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task TypedFinancialPublicationLostCommitAckReturnsOriginalWinnerWithoutSecondFinance(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        var fence = new FinancialCaptureCommitAckFailure();
        await using var f = await CreateFixtureAsync(postgres, 2, false, fence);
        var receipt = await CaptureFinancialPublicationAsync(f, kind);
        Assert.Equal("pending", (await f.PublishFinancial(receipt)).Status); await f.CopyPendingAsync();
        fence.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.PublishFinancial(receipt)); Assert.True(fence.Triggered);
        fence.Armed = false; f.Db.ChangeTracker.Clear();
        await using var observer = f.Observer();
        var document = await observer.ClientCaseDocuments.SingleAsync();
        var replay = await f.PublishFinancial(receipt);
        Assert.Equal("uploaded", replay.Status); Assert.True(replay.IsReplay); Assert.Equal(document.Id, replay.DocumentId);
        Assert.Equal(kind == GeneratedFinancialDocumentKind.Invoice ? 2 : 1, await FinancialRowsAsync(observer));
        Assert.Single(await observer.StorageProductBindings.ToArrayAsync());
        Assert.Single(await observer.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" && a.Action == "Generated").ToArrayAsync());
        Assert.Single(await observer.UserNotifications.Where(n => n.Type == UserNotificationTypes.CaseDocument).ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
        // Deleting a winner never authorizes a replacement for this attempt.
        observer.ClientCaseDocuments.Remove(document); await observer.SaveChangesAsync();
        var rejected = await f.PublishFinancial(receipt);
        Assert.NotEqual("uploaded", rejected.Status);
        Assert.Equal(kind == GeneratedFinancialDocumentKind.Invoice ? 2 : 1, await FinancialRowsAsync(observer));
        Assert.Empty(await observer.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(document.Id, (await observer.GeneratedFinancialDocumentAttempts.SingleAsync()).PublishedDocumentId);
    }

    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task TypedFinancialPublicationPermissionLostDuringReadCannotPublish(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var receipt = await CaptureFinancialPublicationAsync(f, kind);
        Assert.Equal("pending", (await f.PublishFinancial(receipt)).Status); await f.CopyPendingAsync();
        var fired = false;
        f.Local.BeforeRead = () =>
        {
            if (fired) return; fired = true;
            using var changed = f.Observer();
            var section = kind switch { GeneratedFinancialDocumentKind.Invoice => "accounts", GeneratedFinancialDocumentKind.Act => "acts", _ => "contracts" };
            var permission = changed.RolePermissions.Single(p => p.RoleId == f.User.RoleId && p.Section == section);
            permission.CanCreate = false; changed.SaveChanges();
        };
        Assert.Equal("forbidden", (await f.PublishFinancial(receipt)).Status); Assert.True(fired);
        await using var observer = f.Observer();
        Assert.Equal(0, await FinancialRowsAsync(observer)); Assert.Empty(await observer.ClientCaseDocuments.ToArrayAsync());
        Assert.Empty(await observer.StorageProductBindings.ToArrayAsync());
        Assert.Null((await observer.GeneratedFinancialDocumentAttempts.SingleAsync()).PublishedDocumentId);
    }

    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task TypedFinancialPublicationRejectsDifferentBytesThanAcceptedCapture(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await CreateFixtureAsync(postgres, 1);
        var receipt = await CaptureFinancialPublicationAsync(f, kind);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.PublishFinancial(receipt, corruptBytes: true));
        await using var observer = f.Observer();
        Assert.Equal(0, await FinancialRowsAsync(observer)); Assert.Empty(await observer.ClientCaseDocuments.ToArrayAsync());
        Assert.Empty(await observer.StorageProductBindings.ToArrayAsync());
        Assert.Null((await observer.GeneratedFinancialDocumentAttempts.SingleAsync()).PublishedDocumentId);
    }

    private static async Task<GeneratedFinancialDocumentReceipt> CaptureFinancialPublicationAsync(Fixture f, GeneratedFinancialDocumentKind kind)
    {
        var recipient = new User
        {
            Email = "financial-recipient@example.local",
            FullName = "Получатель документа",
            PasswordHash = "synthetic",
            IsActive = true,
            IsApproved = true,
            RoleId = f.User.RoleId
        };
        f.Db.Users.Add(recipient); f.Case.ResponsibleUser = recipient;
        await f.Db.SaveChangesAsync();
        var order = await f.Db.ClientCaseWorkOrders.SingleAsync();
        var captured = await new GeneratedFinancialDocumentRegistry(f.Db).CaptureAsync(f.Case.Id, order.Id, f.User.Id,
            f.User.FullName, Guid.NewGuid(), kind, new string('a', 64), (c, o, identity, _) =>
                Task.FromResult(FinancialCapture(c, o, identity, kind, f.User.Id, f.User.FullName)), default);
        Assert.Equal("recorded", captured.Status);
        return Assert.IsType<GeneratedFinancialDocumentReceipt>(captured.Receipt);
    }

    private static async Task AssertPublishedFinancialGraphAsync(PayPlanner.Api.Data.PaymentContext db, GeneratedFinancialDomainSnapshot d)
    {
        switch (d.Kind)
        {
            case GeneratedFinancialDocumentKind.Invoice:
                var payment = await db.Payments.Include(p => p.Invoice).Include(p => p.InvoiceLineItems)
                    .Include(p => p.InvoiceClientCases).Include(p => p.Responsibles).SingleAsync();
                Assert.Equal(d.Identity.EntityId, payment.Id); Assert.Equal(d.Identity.InvoiceEntityId, payment.InvoiceId);
                Assert.Equal(d.Number, payment.Account); Assert.Equal(d.Number, payment.Invoice!.Number);
                Assert.Equal(d.Date, payment.AccountDate); Assert.Equal(d.DueDate, payment.Date); Assert.Equal(d.DueDate, payment.PlannedDate);
                Assert.Equal(d.Amount, payment.Amount); Assert.Equal(0m, payment.PaidAmount); Assert.Equal(d.Description, payment.Description);
                Assert.Equal(d.ClientId, payment.ClientId); Assert.Equal(d.LegalEntityId, payment.LegalEntityId);
                Assert.Equal(d.CaseReference, payment.ClientCaseReference); Assert.Equal(d.CreatedAt, payment.CreatedAt);
                Assert.Equal(d.CaseId, Assert.Single(payment.InvoiceClientCases).ClientCaseId);
                var invoiceLines = payment.InvoiceLineItems.OrderBy(l => l.SortOrder).ToArray();
                Assert.Equal(d.Lines.Length, invoiceLines.Length);
                for (var i = 0; i < invoiceLines.Length; i++)
                {
                    var line = invoiceLines[i]; Assert.Equal(d.Lines[i].Name, line.Name);
                    Assert.Equal(d.Lines[i].Quantity, line.Quantity); Assert.Equal(d.Lines[i].Price, line.Price);
                    Assert.Equal("шт", line.Unit); Assert.Equal(i + 1, line.SortOrder);
                }
                Assert.Equal("invoice_created", (await db.ClientCaseWorkOrders.SingleAsync()).Status);
                break;
            case GeneratedFinancialDocumentKind.Act:
                var act = await db.Acts.Include(a => a.LineItems).SingleAsync();
                Assert.Equal(d.Identity.EntityId, act.Id); Assert.Equal(d.Number, act.Number); Assert.Equal(d.Title, act.Title);
                Assert.Equal(d.Date, act.Date); Assert.Equal(d.Amount, act.Amount); Assert.Equal(d.Description, act.Comment);
                Assert.Equal(d.ClientId, act.ClientId); Assert.Equal(d.LegalEntityId, act.LegalEntityId);
                var actLines = act.LineItems.OrderBy(l => l.SortOrder).ToArray();
                Assert.Equal(d.Lines.Length, actLines.Length);
                for (var i = 0; i < actLines.Length; i++)
                {
                    var line = actLines[i]; Assert.Equal(d.Lines[i].Name, line.Name);
                    Assert.Equal($"work-order:{d.WorkOrderId}:item:{d.Lines[i].WorkOrderItemId}", line.SourceKey);
                    Assert.Equal(d.Lines[i].SourceSortOrder, line.SourceSortOrder); Assert.Equal(d.Lines[i].Quantity, line.Quantity);
                    Assert.Equal(d.Lines[i].Price, line.Price); Assert.Equal(i + 1, line.SortOrder); Assert.Equal("шт", line.Unit);
                }
                Assert.Equal(ActWorkflowStatus.Draft, act.WorkflowStatus);
                break;
            case GeneratedFinancialDocumentKind.Contract:
                var contract = await db.Contracts.Include(c => c.ClientContracts).Include(c => c.LegalEntityContracts)
                    .Include(c => c.ClientCaseContracts).SingleAsync();
                Assert.Equal(d.Identity.EntityId, contract.Id); Assert.Equal(d.Number, contract.Number); Assert.Equal(d.Title, contract.Title);
                Assert.Equal(d.Date, contract.Date); Assert.Equal(d.StartDate, contract.StartDate); Assert.Equal(d.ValidUntil, contract.ValidUntil);
                Assert.Equal(d.Amount, contract.Amount); Assert.Equal(d.Description, contract.Description); Assert.Equal(d.IsOpenEnded, contract.IsOpenEnded);
                Assert.Equal(d.CaseId, Assert.Single(contract.ClientCaseContracts).ClientCaseId);
                if (d.ClientId.HasValue) Assert.Equal(d.ClientId, Assert.Single(contract.ClientContracts).ClientId);
                if (d.LegalEntityId.HasValue) Assert.Equal(d.LegalEntityId, Assert.Single(contract.LegalEntityContracts).LegalEntityId);
                Assert.Equal(ContractBillingMode.Fixed, contract.BillingMode); Assert.Equal(ContractStatus.Draft, contract.Status);
                break;
        }
    }
}
