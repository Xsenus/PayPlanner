using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Invoices;
using PayPlanner.Api.Services.Storage;
using Xunit;
using CreateClientCaseWorkOrderActRequest = PayPlanner.Api.Controllers.V2.CasesV2Controller.CreateClientCaseWorkOrderActRequest;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedGeneratedCaseDocumentTests
{
    private const string AnnualHistoricalRequestHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_ManagedCaptureAndOrdinaryCreateShareSeriesBeforePublication(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        await AnnualGrantOrdinaryAccessAsync(f);
        var key = Guid.NewGuid();
        var request = new CreateClientCaseWorkOrderActRequest { Number = "INVOICE-774", Date = new DateTime(2017, 4, 5) };
        var managed = f.Controller();
        managed.Request.Headers["X-Upload-Key"] = key.ToString("D");
        Assert.Equal(503, Assert.IsType<ObjectResult>((await managed.CreateActFromWorkOrder(f.Case.Id, request, default)).Result).StatusCode);
        var attempt = await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync();
        var claim = await f.Db.GeneratedFinancialNumberClaims.AsNoTracking().SingleAsync();
        Assert.Equal("1", claim.Number);
        Assert.Equal(1, (await f.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        Assert.Empty(await f.Db.Acts.ToArrayAsync());
        var frozenBytes = await f.Db.GeneratedFinancialDocumentContents.Select(item => item.Bytes).SingleAsync();

        var ordinary = AnnualOrdinaryController(f, f.Db);
        ordinary.Request.Headers["X-Upload-Key"] = Guid.NewGuid().ToString("D");
        var ordinaryAct = Assert.IsType<ActDto>(Assert.IsType<CreatedResult>((await ordinary.Create(AnnualOrdinaryRequest(f, 2017), default)).Result).Value);
        Assert.Equal("2", ordinaryAct.Number);

        // A later imported duplicate on another date retains its existing rules.
        // Publication must not introduce a new annual uniqueness constraint.
        await using (var transaction = await f.Db.Database.BeginTransactionAsync())
        {
            f.Db.Acts.Add(new Act { Number = "1", Date = request.Date!.Value.AddDays(1), ClientId = f.Case.ClientId });
            await new ActAnnualNumberingService(f.Db).RaiseFloorAsync(request.Date.Value.AddDays(1), "1", default);
            await f.Db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await f.CopyPendingAsync();
        f.Db.ChangeTracker.Clear();
        var replay = f.Controller();
        replay.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var published = Assert.IsType<CasesV2Controller.ClientCaseWorkOrderActDto>(Assert.IsType<CreatedResult>(
            (await replay.CreateActFromWorkOrder(f.Case.Id, request, default)).Result).Value);

        Assert.Equal(attempt.EntityId, published.ActId);
        Assert.Equal("1", published.Number);
        Assert.Equal(2, (await f.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        Assert.Equal(new[] { "1", "1", "2" }, await f.Db.Acts.OrderBy(item => item.Number).Select(item => item.Number).ToArrayAsync());
        Assert.Equal(frozenBytes, await f.Db.GeneratedFinancialDocumentContents.Select(item => item.Bytes).SingleAsync());
        Assert.Single(await f.Db.GeneratedFinancialDocumentAttempts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_ManagedCaptureRollbackRestoresCounterAttemptClaimAndBytes(bool postgres)
    {
        var failure = new AnnualCaptureSavedFailure();
        await using var f = await CreateFixtureAsync(postgres, 2, false, failure);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(item => item.Id).SingleAsync();
        failure.Armed = true;
        await Assert.ThrowsAsync<AnnualCaptureFailure>(() => new GeneratedFinancialDocumentRegistry(f.Db).CaptureAsync(
            f.Case.Id, orderId, f.User.Id, f.User.FullName, Guid.NewGuid(), GeneratedFinancialDocumentKind.Act,
            AnnualHistoricalRequestHash, async (clientCase, order, identity, ct) =>
            {
                var initial = FinancialCapture(clientCase, order, identity, GeneratedFinancialDocumentKind.Act, f.User.Id, f.User.FullName);
                var number = await new ActAnnualNumberingService(f.Db).NextAsync(initial.Domain.Date, ct);
                return initial with { Domain = initial.Domain with { Number = number }, Html = $"<html>Акт {number}</html>" };
            }, default));

        Assert.True(failure.Triggered);
        Assert.Empty(await f.Db.ActAnnualNumberCounters.ToArrayAsync());
        Assert.Empty(await f.Db.GeneratedFinancialDocumentAttempts.ToArrayAsync());
        Assert.Empty(await f.Db.GeneratedFinancialNumberClaims.ToArrayAsync());
        Assert.Empty(await f.Db.GeneratedFinancialDocumentContents.ToArrayAsync());
        Assert.Empty(await f.Db.Acts.ToArrayAsync());
        failure.Armed = false;
        var controller = f.Controller();
        controller.Request.Headers["X-Upload-Key"] = Guid.NewGuid().ToString("D");
        Assert.Equal(503, Assert.IsType<ObjectResult>((await controller.CreateActFromWorkOrder(f.Case.Id,
            new CreateClientCaseWorkOrderActRequest { Date = new DateTime(2026, 10, 2) }, default)).Result).StatusCode);
        Assert.Equal("1", (await f.Db.GeneratedFinancialNumberClaims.SingleAsync()).Number);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AnnualAct_HistoricalNumericManagedClaimRaisesFloorIncludingRetiredCapture(bool postgres, bool retired)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var order = await f.Db.ClientCaseWorkOrders.SingleAsync();
        var captured = await AnnualHistoricalCaptureAsync(f, f.Case.Id, order.Id, Guid.NewGuid(), "90", 2018);
        Assert.Equal("recorded", captured.Status);
        if (retired)
        {
            f.Db.CaseDocumentUploadRetirements.Add(FinancialRetirement(captured.Receipt!));
            await f.Db.SaveChangesAsync();
        }
        Assert.Empty(await f.Db.Acts.ToArrayAsync());
        Assert.Empty(await f.Db.ActAnnualNumberCounters.ToArrayAsync());
        await using var transaction = await f.Db.Database.BeginTransactionAsync();

        Assert.Equal("91", await new ActAnnualNumberingService(f.Db).NextAsync(new DateTime(2018, 12, 31), default));

        await transaction.CommitAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_ExistingFrozenWorkOrderNumberReplaysWithoutAllocation(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(item => item.Id).SingleAsync();
        var key = Guid.NewGuid();
        var captured = await AnnualHistoricalCaptureAsync(f, f.Case.Id, orderId, key, "WO-ACT-HISTORICAL", 2019);
        var bytes = await f.Db.GeneratedFinancialDocumentContents.Select(item => item.Bytes).SingleAsync();

        var replay = await new GeneratedFinancialDocumentRegistry(f.Db).CaptureAsync(f.Case.Id, orderId, f.User.Id, f.User.FullName,
            key, GeneratedFinancialDocumentKind.Act, AnnualHistoricalRequestHash,
            (_, _, _, _) => throw new InvalidOperationException("A frozen historical attempt rendered again."), default);

        Assert.True(replay.IsReplay);
        Assert.Equal(captured.Receipt, replay.Receipt);
        Assert.Equal("WO-ACT-HISTORICAL", (await f.Db.GeneratedFinancialNumberClaims.SingleAsync()).Number);
        Assert.Equal(bytes, await f.Db.GeneratedFinancialDocumentContents.Select(item => item.Bytes).SingleAsync());
        Assert.Empty(await f.Db.ActAnnualNumberCounters.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, "managed")]
    [InlineData(true, "managed")]
    [InlineData(false, "ordinary")]
    [InlineData(true, "ordinary")]
    public async Task AnnualAct_OneActorKeyCannotCrossManagedAndOrdinaryNamespaces(bool postgres, string firstOperation)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        await AnnualGrantOrdinaryAccessAsync(f);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(item => item.Id).SingleAsync();
        var key = Guid.NewGuid();
        var ordinary = AnnualOrdinaryController(f, f.Db);
        ordinary.Request.Headers["X-Upload-Key"] = key.ToString("D");
        if (firstOperation == "managed")
        {
            Assert.Equal("recorded", (await AnnualHistoricalCaptureAsync(f, f.Case.Id, orderId, key, "1", 2020)).Status);
            Assert.IsType<ConflictObjectResult>((await ordinary.Create(AnnualOrdinaryRequest(f, 2020), default)).Result);
            Assert.Empty(await f.Db.ActCreateReceipts.ToArrayAsync());
            Assert.Empty(await f.Db.Acts.ToArrayAsync());
        }
        else
        {
            var act = Assert.IsType<ActDto>(Assert.IsType<CreatedResult>((await ordinary.Create(AnnualOrdinaryRequest(f, 2020), default)).Result).Value);
            Assert.Equal("1", act.Number);
            var capture = await new GeneratedFinancialDocumentRegistry(f.Db).CaptureAsync(f.Case.Id, orderId, f.User.Id, f.User.FullName,
                key, GeneratedFinancialDocumentKind.Act, AnnualHistoricalRequestHash,
                (_, _, _, _) => throw new InvalidOperationException("A known ordinary key entered managed capture."), default);
            Assert.Equal("conflict", capture.Status);
            Assert.Empty(await f.Db.GeneratedFinancialDocumentAttempts.ToArrayAsync());
            Assert.Equal(1, (await f.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_ManagedActorKeyCannotStartAnotherCaseCommand(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var firstOrderId = await f.Db.ClientCaseWorkOrders.Select(item => item.Id).SingleAsync();
        var other = await AnnualOtherOrderAsync(f);
        var key = Guid.NewGuid();
        Assert.Equal("recorded", (await AnnualHistoricalCaptureAsync(f, f.Case.Id, firstOrderId, key, "1", 2021)).Status);

        var second = await new GeneratedFinancialDocumentRegistry(f.Db).CaptureAsync(other.ClientCaseId, other.Id,
            f.User.Id, f.User.FullName, key, GeneratedFinancialDocumentKind.Act, AnnualHistoricalRequestHash,
            (_, _, _, _) => throw new InvalidOperationException("Another case reused a known act creation key."), default);

        Assert.Equal("conflict", second.Status);
        Assert.Single(await f.Db.GeneratedFinancialDocumentAttempts.ToArrayAsync());
        Assert.Single(await f.Db.GeneratedFinancialNumberClaims.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnualAct_LegacyLostFileAndModeFlipRecoverOneActAndOneDocument(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var key = Guid.NewGuid();
        var request = new CreateClientCaseWorkOrderActRequest { Number = "OLD-WO", Date = new DateTime(2022, 3, 4) };
        var rejected = new RejectingCaseDocumentStorage(f.LocalCaseStorage, () => Task.CompletedTask);
        var legacy = f.Controller(caseFiles: rejected, useManagedStorage: false);
        legacy.Request.Headers["X-Upload-Key"] = key.ToString("D");
        await Assert.ThrowsAsync<IOException>(() => legacy.CreateActFromWorkOrder(f.Case.Id, request, default));
        var act = await f.Db.Acts.AsNoTracking().SingleAsync();
        Assert.Equal("1", act.Number);
        Assert.Single(await f.Db.ActCreateReceipts.ToArrayAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        f.Db.ChangeTracker.Clear();
        var managed = f.Controller();
        managed.Request.Headers["X-Upload-Key"] = key.ToString("D");
        Assert.Equal(503, Assert.IsType<ObjectResult>((await managed.CreateActFromWorkOrder(f.Case.Id, request, default)).Result).StatusCode);
        legacy = f.Controller(useManagedStorage: false);
        legacy.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var recovered = Assert.IsType<CasesV2Controller.ClientCaseWorkOrderActDto>(Assert.IsType<CreatedResult>(
            (await legacy.CreateActFromWorkOrder(f.Case.Id, request, default)).Result).Value);
        managed = f.Controller();
        managed.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var replay = Assert.IsType<CasesV2Controller.ClientCaseWorkOrderActDto>(Assert.IsType<CreatedResult>(
            (await managed.CreateActFromWorkOrder(f.Case.Id, request, default)).Result).Value);

        Assert.Equal(act.Id, recovered.ActId);
        Assert.Equal(act.Id, replay.ActId);
        Assert.Equal(recovered.DocumentId, replay.DocumentId);
        Assert.Equal(1, (await f.Db.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        Assert.Single(await f.Db.Acts.ToArrayAsync());
        Assert.Single(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Empty(await f.Db.GeneratedFinancialDocumentAttempts.ToArrayAsync());
    }

    private static ActsController AnnualOrdinaryController(Fixture fixture, PayPlanner.Api.Data.PaymentContext db)
        => new(db, new ActAnnualTestScope.NoopRequirements()) { ControllerContext = ActAnnualTestScope.Context(fixture.User.Id) };

    private static async Task AnnualGrantOrdinaryAccessAsync(Fixture fixture)
    {
        fixture.Db.RolePermissions.Add(new RolePermission
        {
            RoleId = fixture.User.RoleId,
            Section = RolePermissionCatalog.Clients,
            CanView = true,
            ViewScope = DataVisibilityScope.Own
        });
        await fixture.Db.SaveChangesAsync();
    }

    private static UpsertActRequest AnnualOrdinaryRequest(Fixture fixture, int year)
        => new()
        {
            Date = new DateTime(year, 6, 1),
            ClientId = fixture.Case.ClientId,
            LegalEntityId = fixture.Case.LegalEntityId,
            ResponsibleId = fixture.User.Id,
            Amount = 10m,
            CreationMode = ActCreationMode.Manual,
            Status = ActStatus.Created
        };

    private static Task<GeneratedFinancialCaptureResult> AnnualHistoricalCaptureAsync(Fixture fixture, int caseId, int orderId,
        Guid key, string number, int year)
        => new GeneratedFinancialDocumentRegistry(fixture.Db).CaptureAsync(caseId, orderId, fixture.User.Id, fixture.User.FullName,
            key, GeneratedFinancialDocumentKind.Act, AnnualHistoricalRequestHash, (clientCase, order, identity, _) =>
            {
                var initial = FinancialCapture(clientCase, order, identity, GeneratedFinancialDocumentKind.Act, fixture.User.Id, fixture.User.FullName);
                return Task.FromResult(initial with { Domain = initial.Domain with { Number = number, Date = new DateTime(year, 4, 5) }, Html = $"<html>Акт {number}</html>" });
            }, default);

    private static async Task<ClientCaseWorkOrder> AnnualOtherOrderAsync(Fixture fixture)
    {
        var order = new ClientCaseWorkOrder
        {
            ClientCase = new ClientCase { Title = "Другое синтетическое дело", ClientId = fixture.Case.ClientId, ResponsibleUserId = fixture.User.Id, Status = ClientCaseStatus.Open },
            Title = "Другой синтетический наряд",
            Status = "approved",
            Currency = "RUB",
            TotalAmount = 35m,
            Items = [new ClientCaseWorkOrderItem { ServiceName = "Услуга", Quantity = 1m, UnitPrice = 35m, ExecutorUserId = fixture.User.Id }]
        };
        fixture.Db.ClientCaseWorkOrders.Add(order);
        await fixture.Db.SaveChangesAsync();
        return order;
    }

    private sealed class AnnualCaptureSavedFailure : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Triggered { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<GeneratedFinancialDocumentAttempt>().Any())
            {
                Triggered = true;
                throw new AnnualCaptureFailure();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AnnualCaptureFailure : Exception { }
}
