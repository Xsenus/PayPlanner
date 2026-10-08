using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers.V2;
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
    public async Task FinancialActionWaitsForCopiesAndReplaysItsOriginalCompleteResult(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var originalActorName = f.User.FullName;
        var controller = f.Controller(); var key = Guid.NewGuid();
        controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var pending = Assert.IsType<ObjectResult>(await InvokeFinancialActionAsync(controller, f.Case.Id, kind));
        Assert.Equal(503, pending.StatusCode);
        Assert.Equal(key.ToString("D"), controller.Response.Headers["X-Upload-Key"]);
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        var attempt = await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync();
        var content = (await f.Db.GeneratedFinancialDocumentContents.AsNoTracking().SingleAsync()).Bytes;
        var order = await f.Db.ClientCaseWorkOrders.Include(o => o.Items).SingleAsync();
        order.Title = "После принятия"; order.Items.Single().Quantity = 100;
        f.User.FullName = "Имя после принятия"; await f.Db.SaveChangesAsync();
        await f.CopyPendingAsync(); f.Db.ChangeTracker.Clear();
        controller = f.Controller(); controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var created = Assert.IsType<CreatedResult>(await InvokeFinancialActionAsync(controller, f.Case.Id, kind));
        var documentId = FinancialActionDocumentId(created.Value!);
        Assert.Null(attempt.PublishedDocumentId);
        var replay = Assert.IsType<CreatedResult>(await InvokeFinancialActionAsync(controller, f.Case.Id, kind));
        Assert.Equal(documentId, FinancialActionDocumentId(replay.Value!)); Assert.Equal(created.Location, replay.Location);
        await using var observer = f.Observer();
        Assert.Equal(kind == GeneratedFinancialDocumentKind.Invoice ? 2 : 1, await FinancialRowsAsync(observer));
        Assert.Equal(documentId, (await observer.GeneratedFinancialDocumentAttempts.SingleAsync()).PublishedDocumentId);
        Assert.Equal(attempt.EntityId, kind switch
        {
            GeneratedFinancialDocumentKind.Invoice => Assert.IsType<CasesV2Controller.ClientCaseWorkOrderInvoiceDto>(created.Value).PaymentId,
            GeneratedFinancialDocumentKind.Act => Assert.IsType<CasesV2Controller.ClientCaseWorkOrderActDto>(created.Value).ActId,
            _ => Assert.IsType<CasesV2Controller.ClientCaseWorkOrderContractDto>(created.Value).ContractId
        });
        var document = Assert.Single(await observer.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(originalActorName, document.UploadedByName);
        Assert.Single(await observer.StorageProductBindings.ToArrayAsync());
        Assert.Equal(35m, created.Value switch
        {
            CasesV2Controller.ClientCaseWorkOrderInvoiceDto invoice => invoice.Amount,
            CasesV2Controller.ClientCaseWorkOrderActDto act => act.Amount,
            CasesV2Controller.ClientCaseWorkOrderContractDto contract => contract.Amount,
            _ => throw new InvalidOperationException("Unknown financial response.")
        });
        Assert.Equal(content, (await observer.GeneratedFinancialDocumentContents.SingleAsync()).Bytes);
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
        var changed = Assert.IsType<ConflictObjectResult>(await InvokeFinancialActionAsync(controller, f.Case.Id, kind, "OTHER-NUMBER"));
        Assert.Equal(409, changed.StatusCode); Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task FinancialActionRegistryOnlyAttemptBlocksLegacyFallback(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var receipt = await CaptureFinancialPublicationAsync(f, kind);
        Assert.Empty(await f.Db.StorageOperations.ToArrayAsync());
        Assert.Empty(await f.Db.StorageCapacityReservations.ToArrayAsync());
        var legacy = f.Controller(useManagedStorage: false);
        legacy.Request.Headers["X-Upload-Key"] = receipt.Proof.UploadKey.ToString("D");
        var blocked = Assert.IsType<ObjectResult>(await InvokeFinancialActionAsync(legacy, f.Case.Id, kind));
        Assert.Equal(503, blocked.StatusCode);
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
        Assert.Single(await f.Db.GeneratedFinancialDocumentAttempts.ToArrayAsync());
    }

    private static int FinancialActionDocumentId(object value) => value switch
    {
        CasesV2Controller.ClientCaseWorkOrderInvoiceDto invoice => invoice.DocumentId!.Value,
        CasesV2Controller.ClientCaseWorkOrderActDto act => act.DocumentId!.Value,
        CasesV2Controller.ClientCaseWorkOrderContractDto contract => contract.DocumentId!.Value,
        _ => throw new InvalidOperationException("Unknown financial response.")
    };
    private static async Task<ActionResult> InvokeFinancialActionAsync(CasesV2Controller controller, int caseId,
        GeneratedFinancialDocumentKind kind, string number = "OWNED-ACTION", CancellationToken ct = default) => kind switch
        {
            GeneratedFinancialDocumentKind.Invoice => (await controller.CreateInvoiceFromWorkOrder(caseId, new() { Number = number }, ct)).Result!,
            GeneratedFinancialDocumentKind.Act => (await controller.CreateActFromWorkOrder(caseId, new() { Number = number }, ct)).Result!,
            _ => (await controller.CreateContractFromWorkOrder(caseId, new() { Number = number }, ct)).Result!
        };
}
