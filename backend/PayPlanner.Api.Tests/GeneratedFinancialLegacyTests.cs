using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Models;
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
    public async Task FinancialLegacyLateManagedEnableRollsBackBeforeCopiesAndSameKeyStartsManagedAttempt(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        Fixture? fixture = null;
        var fence = new FinancialLegacySavedFlagFlip(kind, () => fixture!.FinancialStorageOptions.Enabled = true);
        await using var f = await CreateFixtureAsync(postgres, 2, false, fence); fixture = f;
        f.FinancialStorageOptions.Enabled = false;
        var key = Guid.NewGuid(); var controller = f.Controller();
        controller.Request.Headers["X-Upload-Key"] = key.ToString("D"); fence.Armed = true;
        var interrupted = Assert.IsType<ObjectResult>(await InvokeFinancialActionAsync(controller, f.Case.Id, kind));
        Assert.Equal(503, interrupted.StatusCode); Assert.True(fence.Triggered);
        f.Db.ChangeTracker.Clear();
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Empty(await f.Db.GeneratedFinancialDocumentAttempts.ToArrayAsync()); Assert.Empty(await f.Db.StorageOperations.ToArrayAsync());
        Assert.Empty(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "payment" || a.ObjectType == "act" || a.ObjectType == "contract"
            || a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Equal("approved", (await f.Db.ClientCaseWorkOrders.SingleAsync()).Status);
        Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
        controller = f.Controller(); controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        Assert.Equal(503, Assert.IsType<ObjectResult>(await InvokeFinancialActionAsync(controller, f.Case.Id, kind)).StatusCode);
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Single(await f.Db.GeneratedFinancialDocumentAttempts.ToArrayAsync());
        await f.CopyPendingAsync(); f.Db.ChangeTracker.Clear();
        controller = f.Controller(); controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var ready = Assert.IsType<CreatedResult>(await InvokeFinancialActionAsync(controller, f.Case.Id, kind));
        Assert.True(FinancialActionDocumentId(ready.Value!) > 0);
        Assert.Equal(kind == GeneratedFinancialDocumentKind.Invoice ? 2 : 1, await FinancialRowsAsync(f.Db));
        Assert.Single(await f.Db.ClientCaseDocuments.ToArrayAsync()); Assert.Single(await f.Db.StorageProductBindings.ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task FinancialLegacyPreservesPendingNumberOfAnotherCase(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var captured = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, kind);
        Assert.Equal("recorded", captured.Status); var receipt = captured.Receipt!;
        var other = new ClientCaseWorkOrder
        {
            ClientCase = new ClientCase
            {
                Title = "Другой источник номера",
                ClientId = f.Case.ClientId,
                ResponsibleUserId = f.User.Id,
                Status = ClientCaseStatus.Open
            },
            Title = "Другой наряд",
            Status = "approved",
            Currency = "RUB",
            TotalAmount = 35m,
            Items = [new ClientCaseWorkOrderItem { ServiceName = "Услуга", Quantity = 2m, UnitPrice = 17.5m }]
        };
        f.Db.ClientCaseWorkOrders.Add(other); await f.Db.SaveChangesAsync();
        var controller = f.Controller(useManagedStorage: false); var date = new DateTime(2026, 10, 2);
        ActionResult action = kind switch
        {
            GeneratedFinancialDocumentKind.Invoice => (await controller.CreateInvoiceFromWorkOrder(other.ClientCaseId, new() { Number = "CLAIM", Date = date }, default)).Result!,
            GeneratedFinancialDocumentKind.Act => (await controller.CreateActFromWorkOrder(other.ClientCaseId, new() { Number = "CLAIM", Date = date }, default)).Result!,
            _ => (await controller.CreateContractFromWorkOrder(other.ClientCaseId, new() { Number = "CLAIM", Date = date }, default)).Result!
        };
        var created = Assert.IsType<CreatedResult>(action);
        Assert.Equal("CLAIM-2", created.Value switch
        {
            CasesV2Controller.ClientCaseWorkOrderInvoiceDto invoice => invoice.Number,
            CasesV2Controller.ClientCaseWorkOrderActDto act => act.Number,
            CasesV2Controller.ClientCaseWorkOrderContractDto contract => contract.Number,
            _ => throw new InvalidOperationException("Unknown result.")
        });
        Assert.Equal("pending", (await f.PublishFinancial(receipt)).Status); await f.CopyPendingAsync();
        Assert.Equal("uploaded", (await f.PublishFinancial(receipt)).Status);
        Assert.Equal(kind == GeneratedFinancialDocumentKind.Invoice ? 4 : 2, await FinancialRowsAsync(f.Db));
        Assert.Equal(2, await f.Db.ClientCaseDocuments.CountAsync()); Assert.Single(await f.Db.StorageProductBindings.ToArrayAsync());
        Assert.Equal("CLAIM", (await new GeneratedFinancialDocumentRegistry(f.Db).ReadFrozenAsync(receipt, default))!.Domain.Number);
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice, true)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice, true)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act, true)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act, true)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract, true)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract, true)]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice, false)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice, false)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act, false)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act, false)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract, false)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract, false)]
    public async Task FinancialLegacyCannotBypassAcceptedAttemptWithAnotherOrMissingKey(bool postgres, GeneratedFinancialDocumentKind kind, bool withKey)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var receipt = await CaptureFinancialPublicationAsync(f, kind);
        var legacy = f.Controller(useManagedStorage: false);
        if (withKey) legacy.Request.Headers["X-Upload-Key"] = Guid.NewGuid().ToString("D");
        var blocked = Assert.IsType<ConflictObjectResult>(await InvokeFinancialActionAsync(legacy, f.Case.Id, kind));
        Assert.Equal(409, blocked.StatusCode); Assert.Equal(0, await FinancialRowsAsync(f.Db));
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync()); Assert.Empty(await f.Db.StorageOperations.ToArrayAsync());
        Assert.Single(await f.Db.GeneratedFinancialDocumentAttempts.ToArrayAsync());
        f.Db.CaseDocumentUploadRetirements.Add(FinancialRetirement(receipt)); await f.Db.SaveChangesAsync();
        var result = Assert.IsType<CreatedResult>(await InvokeFinancialActionAsync(legacy, f.Case.Id, kind));
        Assert.True(FinancialActionDocumentId(result.Value!) > 0);
        Assert.Equal(kind == GeneratedFinancialDocumentKind.Invoice ? 2 : 1, await FinancialRowsAsync(f.Db));
        Assert.Single(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Single(await f.Db.GeneratedFinancialDocumentAttempts.ToArrayAsync());
        Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialLegacyInvoiceAndManagedCaptureSerializeToOneAcceptedResult(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync(); f.PrepareIdentityObservers();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var managedTask = Task.Run(async () =>
        {
            await start.Task; await using var db = f.IdentityObserver();
            return await CaptureWithClaimsAsync(db, f.Case.Id, orderId, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Invoice);
        });
        var legacyTask = Task.Run(async () =>
        {
            await start.Task; await using var db = f.IdentityObserver();
            var controller = f.LegacyController(db); controller.Request.Headers["X-Upload-Key"] = Guid.NewGuid().ToString("D");
            return await InvokeFinancialActionAsync(controller, f.Case.Id, GeneratedFinancialDocumentKind.Invoice);
        });
        start.SetResult(); await Task.WhenAll(managedTask, legacyTask);
        var managed = await managedTask; var legacy = await legacyTask;
        await using var proof = f.IdentityObserver();
        if (managed.Status == "recorded")
        {
            Assert.IsType<ConflictObjectResult>(legacy); Assert.Single(await proof.GeneratedFinancialDocumentAttempts.ToArrayAsync());
            Assert.Single(await proof.GeneratedFinancialDocumentContents.ToArrayAsync());
            Assert.Equal(0, await FinancialRowsAsync(proof)); Assert.Empty(await proof.ClientCaseDocuments.ToArrayAsync());
        }
        else
        {
            Assert.Equal("conflict", managed.Status); Assert.IsType<CreatedResult>(legacy);
            Assert.Empty(await proof.GeneratedFinancialDocumentAttempts.ToArrayAsync());
            Assert.Equal(2, await FinancialRowsAsync(proof)); Assert.Single(await proof.ClientCaseDocuments.ToArrayAsync());
        }
        Assert.Empty(await proof.StorageOperations.ToArrayAsync()); Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
    }

    private sealed class FinancialLegacySavedFlagFlip(GeneratedFinancialDocumentKind kind, Action flip) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Triggered { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            var context = eventData.Context;
            var hasFinancial = context is not null && (kind switch
            {
                GeneratedFinancialDocumentKind.Invoice => context.ChangeTracker.Entries<Payment>().Any(),
                GeneratedFinancialDocumentKind.Act => context.ChangeTracker.Entries<Act>().Any(),
                _ => context.ChangeTracker.Entries<Contract>().Any()
            });
            if (Armed && context?.Database.CurrentTransaction is not null && hasFinancial)
            { Armed = false; Triggered = true; flip(); }
            return ValueTask.FromResult(result);
        }
    }
}
