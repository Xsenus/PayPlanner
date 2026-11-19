using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;
using CreateClientCaseWorkOrderActRequest = PayPlanner.Api.Controllers.V2.CasesV2Controller.CreateClientCaseWorkOrderActRequest;
using WorkOrderActDto = PayPlanner.Api.Controllers.V2.CasesV2Controller.ClientCaseWorkOrderActDto;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedGeneratedCaseDocumentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_LegacyReceiptRecoveryRequiresCurrentCaseAccessAndTheOriginalWorkOrder(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 1);
        var key = Guid.NewGuid();
        var request = new CreateClientCaseWorkOrderActRequest { Date = new DateTime(2037, 8, 9) };
        var controller = f.Controller(useManagedStorage: false);
        controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var created = Assert.IsType<WorkOrderActDto>(Assert.IsType<CreatedResult>((await controller.CreateActFromWorkOrder(f.Case.Id, request, default)).Result).Value);
        var recovery = AnnualOrdinaryController(f, f.Db);
        var initial = Assert.IsType<ActDto>(Assert.IsType<OkObjectResult>((await recovery.GetCreateAttempt(key, default)).Result).Value);
        Assert.Equal(created.ActId, initial.Id);
        var casesPermission = await f.Db.RolePermissions.SingleAsync(item => item.RoleId == f.User.RoleId && item.Section == RolePermissionCatalog.Cases);
        casesPermission.CanView = false;
        await f.Db.SaveChangesAsync();

        Assert.IsType<ForbidResult>((await recovery.GetCreateAttempt(key, default)).Result);

        casesPermission.CanView = true;
        f.Case.IsHiddenFromExecutor = true;
        await f.Db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>((await recovery.GetCreateAttempt(key, default)).Result);
        f.Case.IsHiddenFromExecutor = false;
        await f.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>((await recovery.GetCreateAttempt(key, default)).Result);
        var otherOrder = await AnnualOtherOrderAsync(f);
        var receipt = await f.Db.ActCreateReceipts.SingleAsync();
        receipt.WorkOrderId = otherOrder.Id;
        await f.Db.SaveChangesAsync();

        Assert.IsType<NotFoundResult>((await recovery.GetCreateAttempt(key, default)).Result);
        Assert.Equal(created.ActId, (await f.Db.Acts.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(1, (await f.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        Assert.Single(await f.Db.ClientCaseDocuments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_ManagedPublishedReplayKeepsFrozenResponseAfterOrdinaryEdit(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 1);
        await AnnualGrantOrdinaryAccessAsync(f);
        var key = Guid.NewGuid();
        var request = new CreateClientCaseWorkOrderActRequest { Date = new DateTime(2034, 8, 9) };
        var controller = f.Controller(useManagedStorage: true);
        controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var created = Assert.IsType<WorkOrderActDto>(Assert.IsType<CreatedResult>((await controller.CreateActFromWorkOrder(f.Case.Id, request, default)).Result).Value);
        var before = await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync();
        var puts = f.Local.PutCount;
        var reads = f.Local.ReadCount;
        var update = AnnualOrdinaryRequest(f, 2035);
        update.Number = "EDITED-MANUAL";
        update.Amount = 99m;
        update.LineItems =
        [
            new ActLineItemRequest { SourceKey = "edited-first", Name = "Первая изменённая услуга", Quantity = 1m, Price = 40m },
            new ActLineItemRequest { SourceKey = "edited-second", Name = "Вторая изменённая услуга", Quantity = 1m, Price = 59m },
        ];

        var edited = Assert.IsType<ActDto>(Assert.IsType<OkObjectResult>((await AnnualOrdinaryController(f, f.Db).Update(created.ActId, update, default)).Result).Value);

        Assert.Equal(update.Number, edited.Number);
        Assert.Equal(update.Date, edited.Date);
        Assert.Equal(99m, edited.Amount);
        Assert.Equal(2, edited.LineItems.Count);
        Assert.NotEqual(created.Number, edited.Number);
        Assert.NotEqual(created.Date, edited.Date);
        Assert.NotEqual(created.Amount, edited.Amount);
        Assert.NotEqual(created.LineItemCount, edited.LineItems.Count);
        foreach (var retryMode in new[] { true, false })
        {
            var retry = f.Controller(useManagedStorage: retryMode);
            retry.Request.Headers["X-Upload-Key"] = key.ToString("D");
            var replay = Assert.IsType<WorkOrderActDto>(Assert.IsType<CreatedResult>((await retry.CreateActFromWorkOrder(f.Case.Id, request, default)).Result).Value);
            Assert.Equal(created.ActId, replay.ActId);
            Assert.Equal(created.Number, replay.Number);
            Assert.Equal(created.Date, replay.Date);
            Assert.Equal(created.Amount, replay.Amount);
            Assert.Equal(created.LineItemCount, replay.LineItemCount);
            Assert.Equal(created.DocumentId, replay.DocumentId);
        }

        var after = await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(before.FrozenDomainJson, after.FrozenDomainJson);
        Assert.Equal(before.FrozenDomainSha256, after.FrozenDomainSha256);
        Assert.Equal(before.ContentSha256, after.ContentSha256);
        Assert.Single(await f.Db.Acts.ToArrayAsync());
        Assert.Single(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(1, (await f.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        Assert.Equal(puts, f.Local.PutCount);
        Assert.Equal(reads, f.Local.ReadCount);
    }

    [Theory]
    [InlineData(false, "domain")]
    [InlineData(true, "domain")]
    [InlineData(false, "content-metadata")]
    [InlineData(true, "content-metadata")]
    public async Task AnnualAct_ManagedPublishedReplayRejectsDamagedFrozenProof(bool postgres, string damage)
    {
        await using var f = await CreateFixtureAsync(postgres, 1);
        var key = Guid.NewGuid();
        var request = new CreateClientCaseWorkOrderActRequest { Date = new DateTime(2036, 8, 9) };
        var controller = f.Controller(useManagedStorage: true);
        controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var created = Assert.IsType<WorkOrderActDto>(Assert.IsType<CreatedResult>((await controller.CreateActFromWorkOrder(f.Case.Id, request, default)).Result).Value);
        var attempt = await f.Db.GeneratedFinancialDocumentAttempts.SingleAsync();
        if (damage == "domain") attempt.FrozenDomainSha256 = new string('0', 64);
        else attempt.ContentSha256 = new string('z', 64);
        await f.Db.SaveChangesAsync();
        var puts = f.Local.PutCount;
        var reads = f.Local.ReadCount;

        foreach (var retryMode in new[] { true, false })
        {
            var retry = f.Controller(useManagedStorage: retryMode);
            retry.Request.Headers["X-Upload-Key"] = key.ToString("D");
            Assert.IsType<ConflictObjectResult>((await retry.CreateActFromWorkOrder(f.Case.Id, request, default)).Result);
        }

        Assert.Equal(created.ActId, (await f.Db.Acts.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(created.DocumentId, (await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync()).Id);
        Assert.Single(await f.Db.GeneratedFinancialDocumentAttempts.ToArrayAsync());
        Assert.Equal(1, (await f.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        Assert.Equal(puts, f.Local.PutCount);
        Assert.Equal(reads, f.Local.ReadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_LegacyReplayKeepsThePublishedDocumentWhenANewerVersionExists(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 1);
        var key = Guid.NewGuid();
        var request = new CreateClientCaseWorkOrderActRequest { Date = new DateTime(2026, 8, 9) };
        var controller = f.Controller(useManagedStorage: false);
        controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var created = Assert.IsType<WorkOrderActDto>(Assert.IsType<CreatedResult>((await controller.CreateActFromWorkOrder(f.Case.Id, request, default)).Result).Value);
        var original = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        var originalBytes = await File.ReadAllBytesAsync(f.LocalCaseStorage.ResolvePath(original.StoragePath));
        var newer = await AnnualAddLaterDocumentVersionAsync(f, original);
        var rejecting = new RejectingCaseDocumentStorage(f.LocalCaseStorage, () => Task.CompletedTask);

        foreach (var retryMode in new[] { false, true })
        {
            var retry = f.Controller(caseFiles: rejecting, useManagedStorage: retryMode);
            retry.Request.Headers["X-Upload-Key"] = key.ToString("D");
            var replay = Assert.IsType<WorkOrderActDto>(Assert.IsType<CreatedResult>((await retry.CreateActFromWorkOrder(f.Case.Id, request, default)).Result).Value);
            Assert.Equal(created.ActId, replay.ActId);
            Assert.Equal(original.Id, replay.DocumentId);
            Assert.NotEqual(newer.Id, replay.DocumentId);
        }

        Assert.Equal(2, await f.Db.ClientCaseDocuments.CountAsync());
        Assert.Equal(original.Id, (await f.Db.ActCreateReceipts.AsNoTracking().SingleAsync()).PublishedDocumentId);
        Assert.Single(await f.Db.Acts.ToArrayAsync());
        Assert.Equal(1, (await f.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(f.LocalCaseStorage.ResolvePath(original.StoragePath)));
        Assert.Equal(AnnualLaterDocumentBytes, await File.ReadAllBytesAsync(f.LocalCaseStorage.ResolvePath(newer.StoragePath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_DeletedLegacyPublishedDocumentIsGoneEvenWhenAnotherVersionSurvives(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 1);
        var key = Guid.NewGuid();
        var request = new CreateClientCaseWorkOrderActRequest { Date = new DateTime(2027, 8, 9) };
        var controller = f.Controller(useManagedStorage: false);
        controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var created = Assert.IsType<WorkOrderActDto>(Assert.IsType<CreatedResult>((await controller.CreateActFromWorkOrder(f.Case.Id, request, default)).Result).Value);
        var original = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        var newer = await AnnualAddLaterDocumentVersionAsync(f, original);
        var casePermission = await f.Db.RolePermissions.SingleAsync(item =>
            item.RoleId == f.User.RoleId && item.Section == RolePermissionCatalog.Cases);
        Assert.False(casePermission.CanDelete);
        var documentsBeforeDeniedDelete = await f.Db.ClientCaseDocuments.AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.StoragePath, item.Version }).ToArrayAsync();
        var putsBeforeDeniedDelete = f.Local.PutCount;

        Assert.IsType<ForbidResult>(await f.Controller(useManagedStorage: false)
            .DeleteDocument(f.Case.Id, original.Id, default));
        Assert.Equal(documentsBeforeDeniedDelete, await f.Db.ClientCaseDocuments.AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.StoragePath, item.Version }).ToArrayAsync());
        Assert.True(f.LocalCaseStorage.Exists(original.StoragePath));
        Assert.Equal(AnnualLaterDocumentBytes, await File.ReadAllBytesAsync(f.LocalCaseStorage.ResolvePath(newer.StoragePath)));
        Assert.Equal(created.ActId, (await f.Db.Acts.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(1, (await f.Db.ActAnnualNumberCounters.AsNoTracking().SingleAsync()).LastNumber);
        Assert.Equal(putsBeforeDeniedDelete, f.Local.PutCount);

        casePermission.CanDelete = true;
        await f.Db.SaveChangesAsync();
        Assert.IsType<NoContentResult>(await f.Controller(useManagedStorage: false).DeleteDocument(f.Case.Id, original.Id, default));
        var rejecting = new RejectingCaseDocumentStorage(f.LocalCaseStorage, () => Task.CompletedTask);

        foreach (var retryMode in new[] { false, true })
        {
            var retry = f.Controller(caseFiles: rejecting, useManagedStorage: retryMode);
            retry.Request.Headers["X-Upload-Key"] = key.ToString("D");
            Assert.Equal(410, Assert.IsAssignableFrom<IStatusCodeActionResult>((await retry.CreateActFromWorkOrder(f.Case.Id, request, default)).Result).StatusCode);
        }

        Assert.Equal(newer.Id, (await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(original.Id, (await f.Db.ActCreateReceipts.AsNoTracking().SingleAsync()).PublishedDocumentId);
        Assert.Equal(created.ActId, (await f.Db.Acts.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(1, (await f.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        Assert.False(f.LocalCaseStorage.Exists(original.StoragePath));
        Assert.Equal(AnnualLaterDocumentBytes, await File.ReadAllBytesAsync(f.LocalCaseStorage.ResolvePath(newer.StoragePath)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AnnualAct_DeletedPublishedDocumentDoesNotReappearThroughRetryOrModeFlip(bool postgres, bool managedMode)
    {
        await using var f = await CreateFixtureAsync(postgres, 1);
        var key = Guid.NewGuid();
        var request = new CreateClientCaseWorkOrderActRequest { Date = new DateTime(2028, 5, 4) };
        var controller = f.Controller(useManagedStorage: managedMode);
        controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var created = Assert.IsType<WorkOrderActDto>(Assert.IsType<CreatedResult>((await controller.CreateActFromWorkOrder(f.Case.Id, request, default)).Result).Value);
        Assert.NotNull(created.DocumentId);
        var casePermission = await f.Db.RolePermissions.SingleAsync(item =>
            item.RoleId == f.User.RoleId && item.Section == RolePermissionCatalog.Cases);
        Assert.False(casePermission.CanDelete);
        var documentBeforeDeniedDelete = await f.Db.ClientCaseDocuments.AsNoTracking()
            .Where(item => item.Id == created.DocumentId.Value)
            .Select(item => new { item.Id, item.StoragePath, item.Version }).SingleAsync();
        var putsBeforeDeniedDelete = f.Local.PutCount;

        Assert.IsType<ForbidResult>(await f.Controller(useManagedStorage: managedMode)
            .DeleteDocument(f.Case.Id, created.DocumentId.Value, default));
        Assert.Equal(documentBeforeDeniedDelete, await f.Db.ClientCaseDocuments.AsNoTracking()
            .Where(item => item.Id == created.DocumentId.Value)
            .Select(item => new { item.Id, item.StoragePath, item.Version }).SingleAsync());
        Assert.Equal(created.ActId, (await f.Db.Acts.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(1, (await f.Db.ActAnnualNumberCounters.AsNoTracking().SingleAsync()).LastNumber);
        Assert.Equal(putsBeforeDeniedDelete, f.Local.PutCount);

        casePermission.CanDelete = true;
        await f.Db.SaveChangesAsync();
        Assert.IsType<NoContentResult>(await f.Controller(useManagedStorage: managedMode).DeleteDocument(f.Case.Id, created.DocumentId.Value, default));
        var puts = f.Local.PutCount;

        foreach (var retryMode in new[] { managedMode, !managedMode })
        {
            var retry = f.Controller(useManagedStorage: retryMode);
            retry.Request.Headers["X-Upload-Key"] = key.ToString("D");
            Assert.Equal(410, Assert.IsAssignableFrom<IStatusCodeActionResult>((await retry.CreateActFromWorkOrder(f.Case.Id, request, default)).Result).StatusCode);
        }

        Assert.Equal(created.ActId, (await f.Db.Acts.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(1, (await f.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(puts, f.Local.PutCount);
    }

    [Theory]
    [InlineData(true)]
    public async Task AnnualAct_TwoLegacyFileRecoveryRequestsPublishOnlyOneVersionAndRetainItsBytes(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var key = Guid.NewGuid();
        var request = new CreateClientCaseWorkOrderActRequest { Date = new DateTime(2029, 6, 7) };
        var rejecting = new RejectingCaseDocumentStorage(f.LocalCaseStorage, () => Task.CompletedTask);
        var failed = f.Controller(caseFiles: rejecting, useManagedStorage: false);
        failed.Request.Headers["X-Upload-Key"] = key.ToString("D");
        await Assert.ThrowsAsync<IOException>(() => failed.CreateActFromWorkOrder(f.Case.Id, request, default));
        f.Db.ChangeTracker.Clear();
        var actId = await f.Db.Acts.Select(item => item.Id).SingleAsync();
        Assert.Null((await f.Db.ActCreateReceipts.SingleAsync()).PublishedDocumentId);
        await using var firstDb = f.Observer();
        await using var secondDb = f.Observer();
        var storage = new AnnualConcurrentFileStorage(f.LocalCaseStorage);
        var firstController = AnnualRecoveryController(f, firstDb, storage);
        var secondController = AnnualRecoveryController(f, secondDb, storage);
        firstController.Request.Headers["X-Upload-Key"] = key.ToString("D");
        secondController.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var first = AnnualObserveRecoveryAsync(() => firstController.CreateActFromWorkOrder(f.Case.Id, request, default));
        var second = AnnualObserveRecoveryAsync(() => secondController.CreateActFromWorkOrder(f.Case.Id, request, default));
        ActionResult<WorkOrderActDto>?[] outcomes;
        try
        {
            await storage.BothArrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
            storage.Release.TrySetResult();
            outcomes = await Task.WhenAll(first, second);
        }
        finally
        {
            storage.Release.TrySetResult();
            await Task.WhenAll(first, second);
        }

        Assert.Contains(outcomes, item => item?.Result is CreatedResult);
        foreach (var outcome in outcomes.Where(item => item is not null && item.Result is not CreatedResult))
            Assert.Contains(Assert.IsAssignableFrom<IStatusCodeActionResult>(outcome!.Result).StatusCode, new int?[] { 409, 500 });
        await using var observer = f.Observer();
        var document = await observer.ClientCaseDocuments.AsNoTracking().SingleAsync();
        var receipt = await observer.ActCreateReceipts.AsNoTracking().SingleAsync();
        Assert.Equal(document.Id, receipt.PublishedDocumentId);
        Assert.Equal(1, document.Version);
        Assert.Equal(actId, (await observer.Acts.SingleAsync()).Id);
        Assert.Equal(1, (await observer.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        Assert.Equal(2, storage.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(f.LocalCaseStorage.Exists(document.StoragePath));
        Assert.NotEmpty(await File.ReadAllBytesAsync(f.LocalCaseStorage.ResolvePath(document.StoragePath)));
        Assert.All(storage.Paths.Where(path => path != document.StoragePath), path => Assert.False(f.LocalCaseStorage.Exists(path)));

        var retry = f.Controller(useManagedStorage: false);
        retry.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var canonical = Assert.IsType<WorkOrderActDto>(Assert.IsType<CreatedResult>((await retry.CreateActFromWorkOrder(f.Case.Id, request, default)).Result).Value);
        Assert.Equal(document.Id, canonical.DocumentId);
        Assert.Equal(1, (await observer.ClientCaseDocuments.AsNoTracking().SingleAsync()).Version);
    }

    private static CasesV2Controller AnnualRecoveryController(Fixture fixture, PaymentContext context, ICaseDocumentStorage storage)
        => new(context, new TestEnvironment(Path.GetTempPath()), caseDocumentStorage: storage)
        { ControllerContext = ActAnnualTestScope.Context(fixture.User.Id) };

    private static readonly byte[] AnnualLaterDocumentBytes = Encoding.UTF8.GetBytes("<html>Другой файл второй версии</html>");

    private static async Task<ClientCaseDocument> AnnualAddLaterDocumentVersionAsync(Fixture fixture, ClientCaseDocument original)
    {
        const string fileName = "annual-later-version.html";
        var path = fixture.LocalCaseStorage.BuildRelativePath(fixture.Case.Id, fileName);
        await fixture.LocalCaseStorage.SaveAsync(path, AnnualLaterDocumentBytes, default);
        var newer = new ClientCaseDocument
        {
            ClientCaseId = fixture.Case.Id,
            DocumentKey = original.DocumentKey,
            Version = 2,
            DocumentType = original.DocumentType,
            Status = original.Status,
            OriginalFileName = fileName,
            StoredFileName = fileName,
            StoragePath = path,
            ContentType = original.ContentType,
            SizeBytes = AnnualLaterDocumentBytes.Length,
            UploadedByUserId = fixture.User.Id,
            UploadedByName = fixture.User.FullName,
        };
        fixture.Db.ClientCaseDocuments.Add(newer);
        await fixture.Db.SaveChangesAsync();
        return newer;
    }

    private static async Task<ActionResult<WorkOrderActDto>?> AnnualObserveRecoveryAsync(Func<Task<ActionResult<WorkOrderActDto>>> operation)
    {
        try { return await operation(); }
        catch (InvalidOperationException) { return null; }
    }

    private sealed class AnnualConcurrentFileStorage(ICaseDocumentStorage inner) : ICaseDocumentStorage
    {
        private int arrivals;
        public ConcurrentQueue<string> Paths { get; } = new();
        public TaskCompletionSource BothArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string BuildRelativePath(int caseId, string name) => inner.BuildRelativePath(caseId, name);
        public string ResolvePath(string path) => inner.ResolvePath(path);
        public bool Exists(string path) => inner.Exists(path);
        public void DeleteIfExists(string path) => inner.DeleteIfExists(path);
        public async Task SaveAsync(string path, Stream content, CancellationToken ct)
        {
            await AwaitBothWritersAsync(path, ct);
            await inner.SaveAsync(path, content, ct);
        }
        public async Task SaveAsync(string path, byte[] content, CancellationToken ct)
        {
            await AwaitBothWritersAsync(path, ct);
            await inner.SaveAsync(path, content, ct);
        }
        private async Task AwaitBothWritersAsync(string path, CancellationToken ct)
        {
            Paths.Enqueue(path);
            if (Interlocked.Increment(ref arrivals) == 2) BothArrived.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        }
    }
}
