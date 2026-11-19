using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers.V2;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedGeneratedCaseDocumentTests
{
    [Theory]
    [InlineData(false, "managed-request")]
    [InlineData(true, "managed-request")]
    [InlineData(false, "managed-kind")]
    [InlineData(true, "managed-kind")]
    [InlineData(false, "legacy-kind")]
    [InlineData(true, "legacy-kind")]
    public async Task AnnualAct_PendingPublicActionRejectsChangedCommandOrKindBeforeAnyWrite(bool postgres, string scenario)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var key = Guid.NewGuid();
        var date = new DateTime(2031, 6, 7);
        var request = new CasesV2Controller.CreateClientCaseWorkOrderActRequest
        {
            Number = "PENDING-ACT",
            Date = date,
            Comment = "Исходная команда"
        };
        var creator = f.Controller();
        creator.Request.Headers["X-Upload-Key"] = key.ToString("D");
        ActionResult pending;
        if (scenario == "managed-request")
        {
            pending = (await creator.CreateActFromWorkOrder(f.Case.Id, request, default)).Result!;
        }
        else
        {
            pending = (await creator.CreateInvoiceFromWorkOrder(f.Case.Id,
                new CasesV2Controller.CreateClientCaseWorkOrderInvoiceRequest
                {
                    Number = "PENDING-INVOICE",
                    Date = date,
                    DueDate = date.AddDays(1)
                }, default)).Result!;
        }
        Assert.Equal(503, Assert.IsType<ObjectResult>(pending).StatusCode);
        Assert.Equal(key.ToString("D"), creator.Response.Headers["X-Upload-Key"]);
        var accepted = await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(key, accepted.UploadKey);
        Assert.Equal(f.User.Id, accepted.ActorId);
        Assert.Equal(f.Case.Id, accepted.ClientCaseId);
        Assert.Equal(await f.Db.ClientCaseWorkOrders.Select(item => item.Id).SingleAsync(), accepted.WorkOrderId);
        Assert.Equal(scenario == "managed-request" ? "act" : "invoice", accepted.Kind);
        Assert.Null(accepted.PublishedDocumentId);
        Assert.Equal(0, await FinancialRowsAsync(f.Db));
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        if (scenario == "managed-request")
        {
            Assert.Equal(1, (await f.Db.ActAnnualNumberCounters.AsNoTracking().SingleAsync()).LastNumber);
            request.Comment = "Изменённая команда";
        }
        else
        {
            Assert.Empty(await f.Db.ActAnnualNumberCounters.ToArrayAsync());
        }
        var before = await SnapshotPendingAsync();
        var localPutsBefore = f.Local.PutCount;
        var mirrorPutsBefore = f.Mirror.PutCount;
        var localReadsBefore = f.Local.ReadCount;
        var mirrorReadsBefore = f.Mirror.ReadCount;
        var managedMode = scenario != "legacy-kind";
        var retry = f.Controller(useManagedStorage: managedMode);
        retry.Request.Headers["X-Upload-Key"] = key.ToString("D");

        var result = (await retry.CreateActFromWorkOrder(f.Case.Id, request, default)).Result;

        ObjectResult rejected;
        if (managedMode)
        {
            rejected = Assert.IsType<ConflictObjectResult>(result);
            Assert.Equal(409, rejected.StatusCode);
        }
        else
        {
            rejected = Assert.IsType<ObjectResult>(result);
            Assert.Equal(503, rejected.StatusCode);
        }
        var expectedMessage = managedMode
            ? "Данные этой попытки изменились или услуги уже заняты. Проверьте состояние документа перед новой попыткой."
            : "Эта попытка начата в проверенном хранилище. Она не может перейти в прежний режим записи; проверьте её состояние после восстановления режима хранения.";
        Assert.Equal(expectedMessage, JsonSerializer.SerializeToElement(rejected.Value).GetProperty("message").GetString());
        Assert.Equal(before, await SnapshotPendingAsync());
        Assert.Equal(0, await FinancialRowsAsync(f.Db));
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(localPutsBefore, f.Local.PutCount);
        Assert.Equal(mirrorPutsBefore, f.Mirror.PutCount);
        Assert.Equal(localReadsBefore, f.Local.ReadCount);
        Assert.Equal(mirrorReadsBefore, f.Mirror.ReadCount);

        async Task<string> SnapshotPendingAsync()
            => JsonSerializer.Serialize(new
            {
                Attempts = await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().OrderBy(item => item.Id)
                    .Select(item => new
                    {
                        item.Id,
                        item.UploadKey,
                        item.ActorId,
                        item.ClientCaseId,
                        item.WorkOrderId,
                        item.Kind,
                        item.EntityId,
                        item.InvoiceEntityId,
                        item.RequestSha256,
                        item.FrozenDomainJson,
                        item.FrozenDomainSha256,
                        item.ContentSizeBytes,
                        item.ContentSha256,
                        item.PublishedDocumentId
                    }).ToArrayAsync(),
                Contents = await f.Db.GeneratedFinancialDocumentContents.AsNoTracking().OrderBy(item => item.AttemptId)
                    .Select(item => new { item.AttemptId, item.Bytes }).ToArrayAsync(),
                NumberClaims = await f.Db.GeneratedFinancialNumberClaims.AsNoTracking().OrderBy(item => item.AttemptId)
                    .ThenBy(item => item.Kind).ThenBy(item => item.DateKey).ThenBy(item => item.Number)
                    .Select(item => new { item.AttemptId, item.Kind, item.DateKey, item.Number }).ToArrayAsync(),
                ActLineClaims = await f.Db.GeneratedFinancialActLineClaims.AsNoTracking().OrderBy(item => item.AttemptId)
                    .ThenBy(item => item.WorkOrderItemId)
                    .Select(item => new { item.AttemptId, item.WorkOrderItemId, item.SourceKey }).ToArrayAsync(),
                Counters = await f.Db.ActAnnualNumberCounters.AsNoTracking().OrderBy(item => item.Year)
                    .Select(item => new { item.Year, item.LastNumber }).ToArrayAsync(),
                Sources = await f.Db.ClientCaseWorkOrders.AsNoTracking().OrderBy(item => item.Id)
                    .Select(item => new { item.Id, item.ClientCaseId, item.Status, item.Title, item.TotalAmount }).ToArrayAsync()
            });
    }
}
