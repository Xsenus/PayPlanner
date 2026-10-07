using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
    public async Task FinancialCaptureFreezesOriginalGraphAndBytesBeforeAnyFinancialPublication(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var key = Guid.NewGuid(); var requestSha = new string('a', 64); var calls = 0;
        var recorded = await new GeneratedFinancialDocumentRegistry(f.Db).CaptureAsync(f.Case.Id, orderId, f.User.Id,
            f.User.FullName, key, kind, requestSha, (c, o, identity, _) =>
            { calls++; return Task.FromResult(FinancialCapture(c, o, identity, kind, f.User.Id, f.User.FullName)); }, default);
        Assert.Equal("recorded", recorded.Status); Assert.False(recorded.IsReplay); Assert.Equal(1, calls);
        var receipt = Assert.IsType<GeneratedFinancialDocumentReceipt>(recorded.Receipt);
        var original = Assert.IsType<GeneratedFinancialFrozenContent>(await new GeneratedFinancialDocumentRegistry(f.Db).ReadFrozenAsync(receipt, default));
        Assert.Equal(35m, original.Domain.Amount); Assert.Equal("OWNED-FROZEN-NUMBER", original.Domain.Number);
        Assert.True(receipt.Generation.FrozenDomainJson.Length < 2000);
        Assert.Contains("Контроль генерации", Encoding.UTF8.GetString(original.Bytes));
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Single(await f.Db.GeneratedFinancialDocumentAttempts.ToArrayAsync());
        Assert.Single(await f.Db.GeneratedFinancialDocumentContents.ToArrayAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync()); Assert.Empty(await f.Db.StorageProductBindings.ToArrayAsync());
        Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
        var order = await f.Db.ClientCaseWorkOrders.Include(o => o.Items).SingleAsync();
        order.Title = "Название после принятия"; order.Status = "invoice_created";
        order.Items.Single().Quantity = 100m; order.Items.Single().UnitPrice = 200m;
        f.Case.Title = "Дело после принятия"; f.User.FullName = "Автор после принятия";
        await f.Db.SaveChangesAsync();
        await using var restarted = f.Observer();
        var registry = new GeneratedFinancialDocumentRegistry(restarted);
        var replay = await registry.CaptureAsync(f.Case.Id, orderId, f.User.Id, f.User.FullName,
            key, kind, requestSha, (_, _, _, _) => throw new InvalidOperationException("Replay rendered another graph."), default);
        Assert.Equal("recorded", replay.Status); Assert.True(replay.IsReplay); Assert.Equal(receipt, replay.Receipt);
        var recovered = Assert.IsType<GeneratedFinancialFrozenContent>(await registry.ReadFrozenAsync(receipt, default));
        Assert.Equal(original.Bytes, recovered.Bytes); Assert.Equal(original.Domain.Identity, recovered.Domain.Identity);
        Assert.Equal("Автор генерации", recovered.Domain.ActorName); Assert.Equal("Контроль генерации", recovered.Domain.WorkOrderTitle);
        Assert.Equal(35m, recovered.Domain.Amount); Assert.Equal(2m, recovered.Domain.Lines.Single().Quantity);
        Assert.Equal(0, await FinancialRowsAsync(restarted)); Assert.Equal(1, calls);
        Assert.True(await registry.CanPublishAsync(FinancialSource(receipt), default));
    }

    [Theory]
    [InlineData(false, "request")]
    [InlineData(true, "request")]
    [InlineData(false, "kind")]
    [InlineData(true, "kind")]
    [InlineData(false, "domain")]
    [InlineData(true, "domain")]
    [InlineData(false, "permission")]
    [InlineData(true, "permission")]
    [InlineData(false, "source")]
    [InlineData(true, "source")]
    [InlineData(false, "content")]
    [InlineData(true, "content")]
    public async Task FinancialCaptureRejectsChangedRequestOrDamagedAttemptWithoutAllocatingAgain(bool postgres, string scenario)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var key = Guid.NewGuid(); var requestSha = new string('a', 64);
        var result = await new GeneratedFinancialDocumentRegistry(f.Db).CaptureAsync(f.Case.Id, orderId, f.User.Id,
            f.User.FullName, key, GeneratedFinancialDocumentKind.Invoice, requestSha,
            (c, o, identity, _) => Task.FromResult(FinancialCapture(c, o, identity, GeneratedFinancialDocumentKind.Invoice, f.User.Id, f.User.FullName)), default);
        var receipt = Assert.IsType<GeneratedFinancialDocumentReceipt>(result.Receipt);
        if (scenario == "domain") (await f.Db.GeneratedFinancialDocumentAttempts.SingleAsync()).FrozenDomainJson = "{}";
        if (scenario == "permission") (await f.Db.RolePermissions.SingleAsync(p => p.RoleId == f.User.RoleId && p.Section == RolePermissionCatalog.Accounts)).CanCreate = false;
        if (scenario == "source") f.Db.ClientCaseWorkOrders.Remove(await f.Db.ClientCaseWorkOrders.SingleAsync());
        if (scenario == "content")
        {
            var content = await f.Db.GeneratedFinancialDocumentContents.SingleAsync();
            var bytes = content.Bytes.ToArray(); bytes[0] ^= 1; content.Bytes = bytes;
        }
        await f.Db.SaveChangesAsync();
        await using var restarted = f.Observer(); var registry = new GeneratedFinancialDocumentRegistry(restarted);
        var replay = await registry.CaptureAsync(f.Case.Id, orderId, f.User.Id, f.User.FullName, key,
            scenario == "kind" ? GeneratedFinancialDocumentKind.Act : GeneratedFinancialDocumentKind.Invoice,
            scenario == "request" ? new string('b', 64) : requestSha,
            (_, _, _, _) => throw new InvalidOperationException("Damaged replay allocated another identity."), default);
        Assert.Equal(scenario == "content" ? "recorded" : scenario is "permission" or "source" ? "forbidden" : "conflict", replay.Status);
        Assert.Single(await restarted.GeneratedFinancialDocumentAttempts.ToArrayAsync()); Assert.Equal(0, await FinancialRowsAsync(restarted));
        if (scenario is "permission" or "source" or "domain" or "content") Assert.Null(await registry.ReadFrozenAsync(receipt, default));
        if (scenario is "permission" or "source" or "domain") Assert.False(await registry.CanPublishAsync(FinancialSource(receipt), default));
        Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, "callback")]
    [InlineData(true, "callback")]
    [InlineData(false, "dirty")]
    [InlineData(true, "dirty")]
    [InlineData(false, "saved")]
    [InlineData(true, "saved")]
    public async Task FinancialCaptureFailureRollsBackAttemptBytesAndTrackedChanges(bool postgres, string boundary)
    {
        var fence = new FinancialCaptureSavedFailure();
        await using var f = await Fixture.CreateAsync(postgres, 2, false, fence);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var titleBefore = f.Case.Title; fence.Armed = boundary == "saved";
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GeneratedFinancialDocumentRegistry(f.Db).CaptureAsync(
            f.Case.Id, orderId, f.User.Id, f.User.FullName, Guid.NewGuid(), GeneratedFinancialDocumentKind.Invoice, new string('a', 64),
            (c, o, identity, _) =>
            {
                if (boundary == "callback") throw new InvalidOperationException("Owned financial capture failure.");
                if (boundary == "dirty") f.Case.Title = "Uncommitted tracked mutation";
                return Task.FromResult(FinancialCapture(c, o, identity, GeneratedFinancialDocumentKind.Invoice, f.User.Id, f.User.FullName));
            }, default));
        Assert.False(f.Db.ChangeTracker.HasChanges()); Assert.Equal(titleBefore, f.Case.Title);
        await using var proof = f.Observer(); Assert.Empty(await proof.GeneratedFinancialDocumentAttempts.ToArrayAsync());
        Assert.Empty(await proof.GeneratedFinancialDocumentContents.ToArrayAsync()); Assert.Equal(0, await FinancialRowsAsync(proof));
        Assert.Equal(titleBefore, (await proof.ClientCases.SingleAsync()).Title);
        Assert.Empty(await proof.ClientCaseDocuments.ToArrayAsync()); Assert.Equal(0, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialCaptureLostCommitAckRecoversOriginalIdentityWithoutAnotherRender(bool postgres)
    {
        var fence = new FinancialCaptureCommitAckFailure();
        await using var f = await Fixture.CreateAsync(postgres, 2, false, fence);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var key = Guid.NewGuid(); fence.Armed = true; GeneratedFinancialIdentity? frozenIdentity = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GeneratedFinancialDocumentRegistry(f.Db).CaptureAsync(
            f.Case.Id, orderId, f.User.Id, f.User.FullName, key, GeneratedFinancialDocumentKind.Act, new string('a', 64),
            (c, o, identity, _) => { frozenIdentity = identity; return Task.FromResult(FinancialCapture(c, o, identity, GeneratedFinancialDocumentKind.Act, f.User.Id, f.User.FullName)); }, default));
        Assert.True(fence.Triggered); fence.Armed = false;
        await using var restarted = f.Observer(); var registry = new GeneratedFinancialDocumentRegistry(restarted);
        var replay = await registry.CaptureAsync(f.Case.Id, orderId, f.User.Id, f.User.FullName, key,
            GeneratedFinancialDocumentKind.Act, new string('a', 64), (_, _, _, _) => throw new InvalidOperationException("Lost ACK rendered twice."), default);
        Assert.Equal("recorded", replay.Status); Assert.True(replay.IsReplay);
        var frozen = Assert.IsType<GeneratedFinancialFrozenContent>(await registry.ReadFrozenAsync(replay.Receipt!, default));
        Assert.Equal(frozenIdentity, frozen.Domain.Identity); Assert.Equal(0, await FinancialRowsAsync(restarted));
        Assert.Single(await restarted.GeneratedFinancialDocumentAttempts.ToArrayAsync()); Assert.Single(await restarted.GeneratedFinancialDocumentContents.ToArrayAsync());
        Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialCaptureConcurrentSameRequestUsesOneAttemptAndOneRender(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2); f.PrepareIdentityObservers();
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var key = Guid.NewGuid(); var calls = 0; var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await start.Task; await using var db = f.IdentityObserver();
            return await new GeneratedFinancialDocumentRegistry(db).CaptureAsync(f.Case.Id, orderId, f.User.Id, f.User.FullName,
                key, GeneratedFinancialDocumentKind.Contract, new string('a', 64), (c, o, identity, _) =>
                { Interlocked.Increment(ref calls); return Task.FromResult(FinancialCapture(c, o, identity, GeneratedFinancialDocumentKind.Contract, f.User.Id, f.User.FullName)); }, default);
        })).ToArray();
        start.SetResult(); var results = await Task.WhenAll(workers);
        Assert.All(results, r => Assert.Equal("recorded", r.Status)); Assert.Equal(1, calls);
        Assert.Equal(1, results.Count(r => !r.IsReplay)); Assert.Single(results.Select(r => r.Receipt).Distinct());
        await using var proof = f.IdentityObserver(); Assert.Single(await proof.GeneratedFinancialDocumentAttempts.ToArrayAsync());
        Assert.Single(await proof.GeneratedFinancialDocumentContents.ToArrayAsync()); Assert.Equal(0, await FinancialRowsAsync(proof));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialCaptureLargeUnicodeGraphKeepsSmallExactQueueProof(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var description = string.Concat(Enumerable.Repeat("Условия договора 🧾; ", 5000));
        var result = await new GeneratedFinancialDocumentRegistry(f.Db).CaptureAsync(f.Case.Id, orderId, f.User.Id,
            f.User.FullName, Guid.NewGuid(), GeneratedFinancialDocumentKind.Contract, new string('a', 64), (c, o, identity, _) =>
            {
                var capture = FinancialCapture(c, o, identity, GeneratedFinancialDocumentKind.Contract, f.User.Id, f.User.FullName);
                return Task.FromResult(capture with { Domain = capture.Domain with { Description = description }, Html = capture.Html + description });
            }, default);
        Assert.Equal("recorded", result.Status); var receipt = result.Receipt!;
        Assert.True((await f.Db.GeneratedFinancialDocumentAttempts.SingleAsync()).FrozenDomainJson.Length > 48000);
        Assert.True(receipt.Generation.FrozenDomainJson.Length < 2000);
        await using var restarted = f.Observer();
        var frozen = Assert.IsType<GeneratedFinancialFrozenContent>(await new GeneratedFinancialDocumentRegistry(restarted).ReadFrozenAsync(receipt, default));
        Assert.Equal(description, frozen.Domain.Description); Assert.EndsWith(description, Encoding.UTF8.GetString(frozen.Bytes));
        Assert.Equal(0, await FinancialRowsAsync(restarted));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialCaptureRejectsForeignOrMalleablePublicationProof(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync(); var registry = new GeneratedFinancialDocumentRegistry(f.Db);
        var result = await registry.CaptureAsync(f.Case.Id, orderId, f.User.Id, f.User.FullName, Guid.NewGuid(),
            GeneratedFinancialDocumentKind.Invoice, new string('a', 64),
            (c, o, identity, _) => Task.FromResult(FinancialCapture(c, o, identity, GeneratedFinancialDocumentKind.Invoice, f.User.Id, f.User.FullName)), default);
        var source = FinancialSource(result.Receipt!); Assert.True(await registry.CanPublishAsync(source, default));
        var mutations = new[] { source with { ActorId = source.ActorId + 1 }, source with { CaseId = source.CaseId + 1 },
            source with { UploadKey = Guid.NewGuid() }, source with { DocumentKey = "invoice-999999" },
            source with { RequestedDocumentKey = "invoice-999999" }, source with { FileName = "wrong.html" },
            source with { DocumentType = "Акт" }, source with { Status = "received" }, source with { ContentType = "text/html" },
            source with { Generation = source.Generation with { Kind = "work-order" } },
            source with { Generation = source.Generation with { RequestSha256 = new string('b', 64) } },
            source with { Generation = source.Generation with { FrozenDomainJson = source.Generation.FrozenDomainJson + " " } } };
        foreach (var mutation in mutations) Assert.False(await registry.CanPublishAsync(mutation, default));
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Equal(0, f.Local.ReadCount); Assert.Equal(0, f.Mirror.ReadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialCaptureFinalAuthorityLossRollsBackAttemptAndPermissionChange(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GeneratedFinancialDocumentRegistry(f.Db).CaptureAsync(
            f.Case.Id, orderId, f.User.Id, f.User.FullName, Guid.NewGuid(), GeneratedFinancialDocumentKind.Invoice, new string('a', 64),
            async (c, o, identity, token) =>
            {
                await f.Db.RolePermissions.Where(p => p.RoleId == f.User.RoleId && p.Section == RolePermissionCatalog.Accounts)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.CanCreate, false), token);
                return FinancialCapture(c, o, identity, GeneratedFinancialDocumentKind.Invoice, f.User.Id, f.User.FullName);
            }, default));
        await using var proof = f.Observer(); Assert.Empty(await proof.GeneratedFinancialDocumentAttempts.ToArrayAsync());
        Assert.Empty(await proof.GeneratedFinancialDocumentContents.ToArrayAsync()); Assert.Equal(0, await FinancialRowsAsync(proof));
        Assert.True((await proof.RolePermissions.SingleAsync(p => p.RoleId == f.User.RoleId && p.Section == RolePermissionCatalog.Accounts)).CanCreate);
        Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialCaptureRetiredKeyCannotRenderAnotherAttempt(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync(); var key = Guid.NewGuid();
        f.Db.CaseDocumentUploadRetirements.Add(new CaseDocumentUploadRetirement { CaseId = f.Case.Id, ActorId = f.User.Id, UploadKey = key });
        await f.Db.SaveChangesAsync();
        var result = await new GeneratedFinancialDocumentRegistry(f.Db).CaptureAsync(f.Case.Id, orderId, f.User.Id, f.User.FullName,
            key, GeneratedFinancialDocumentKind.Invoice, new string('a', 64), (_, _, _, _) => throw new InvalidOperationException("Retired capture rendered."), default);
        Assert.Equal("retired", result.Status); Assert.Empty(await f.Db.GeneratedFinancialDocumentAttempts.ToArrayAsync());
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Equal(0, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialCaptureMigrationUpgradesExistingCaseWithoutChangingItsData(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        var caseId = f.Case.Id; var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var previous = postgres ? "20261001213238_AddCourtDocumentSourceContentPostgres" : "20261001213231_AddCourtDocumentSourceContent";
        await f.Db.GetService<IMigrator>().MigrateAsync(previous);
        var pending = (await f.Db.Database.GetPendingMigrationsAsync()).ToArray();
        var financial = FinancialMigrations(postgres);
        Assert.Equal(financial, pending.Take(financial.Length));
        var expectedPending = f.Db.Database.GetMigrations()
            .SkipWhile(migration => migration != previous).Skip(1).ToArray();
        Assert.Equal(expectedPending, pending);
        await f.Db.Database.MigrateAsync(); Assert.Empty(await f.Db.Database.GetPendingMigrationsAsync());
        Assert.False(f.Db.Database.HasPendingModelChanges());
        Assert.Equal(caseId, (await f.Db.ClientCases.SingleAsync()).Id); Assert.Equal(orderId, (await f.Db.ClientCaseWorkOrders.SingleAsync()).Id);
        Assert.Empty(await f.Db.GeneratedFinancialDocumentAttempts.ToArrayAsync()); Assert.Empty(await f.Db.GeneratedFinancialDocumentContents.ToArrayAsync());
        Assert.Equal(0, await FinancialRowsAsync(f.Db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialCaptureMigrationRejectsDowngradeThatWouldLoseAcceptedAttempt(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var result = await new GeneratedFinancialDocumentRegistry(f.Db).CaptureAsync(f.Case.Id, orderId, f.User.Id, f.User.FullName,
            Guid.NewGuid(), GeneratedFinancialDocumentKind.Act, new string('a', 64),
            (c, o, identity, _) => Task.FromResult(FinancialCapture(c, o, identity, GeneratedFinancialDocumentKind.Act, f.User.Id, f.User.FullName)), default);
        var receipt = result.Receipt!; var previous = postgres ? "20261001213238_AddCourtDocumentSourceContentPostgres" : "20261001213231_AddCourtDocumentSourceContent";
        var original = await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().Select(a => new
        {
            a.Id,
            a.UploadKey,
            a.ClientCaseId,
            a.WorkOrderId,
            a.ActorId,
            a.ActorName,
            a.Kind,
            a.EntityId,
            a.InvoiceEntityId,
            a.RequestSha256,
            a.FrozenDomainJson,
            a.FrozenDomainSha256,
            a.ContentSizeBytes,
            a.ContentSha256,
            a.PublishedDocumentId
        }).SingleAsync();
        var originalBytes = await f.Db.GeneratedFinancialDocumentContents.Select(c => c.Bytes).SingleAsync();
        var originalClaims = await f.Db.GeneratedFinancialActLineClaims.AsNoTracking()
            .OrderBy(c => c.WorkOrderItemId).Select(c => new { c.AttemptId, c.WorkOrderItemId, c.SourceKey }).ToArrayAsync();
        var error = await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => f.Db.GetService<IMigrator>().MigrateAsync(previous));
        Assert.Contains(postgres ? "Cannot remove financial claims while accepted attempts exist" : "CHECK constraint failed", error.Message);
        await using var proof = f.Observer();
        // SQLite rebuilds can commit preceding Down steps before the claims guard rejects the older schema.
        // Read only columns present on both sides, then restore the current model before using its registry.
        var preserved = await proof.GeneratedFinancialDocumentAttempts.AsNoTracking().Select(a => new
        {
            a.Id,
            a.UploadKey,
            a.ClientCaseId,
            a.WorkOrderId,
            a.ActorId,
            a.ActorName,
            a.Kind,
            a.EntityId,
            a.InvoiceEntityId,
            a.RequestSha256,
            a.FrozenDomainJson,
            a.FrozenDomainSha256,
            a.ContentSizeBytes,
            a.ContentSha256,
            a.PublishedDocumentId
        }).SingleAsync();
        Assert.Equal(original, preserved);
        Assert.Equal(originalBytes, await proof.GeneratedFinancialDocumentContents.Select(c => c.Bytes).SingleAsync());
        Assert.Equal(originalClaims, await proof.GeneratedFinancialActLineClaims.AsNoTracking()
            .OrderBy(c => c.WorkOrderItemId).Select(c => new { c.AttemptId, c.WorkOrderItemId, c.SourceKey }).ToArrayAsync());
        Assert.Equal(0, await FinancialRowsAsync(proof));
        if (postgres) Assert.Empty(await proof.Database.GetPendingMigrationsAsync());
        await proof.Database.MigrateAsync();
        Assert.Empty(await proof.Database.GetPendingMigrationsAsync());
        Assert.False(proof.Database.HasPendingModelChanges());
        var restored = Assert.Single(await proof.GeneratedFinancialDocumentAttempts.AsNoTracking().ToArrayAsync());
        Assert.Equal(original.Id, restored.Id); Assert.Equal(original.UploadKey.ToString("N"), restored.UploadKeyText);
        Assert.Equal(0, restored.RecoveryAttemptCount); Assert.Null(restored.RecoveryLeaseToken);
        var frozen = await new GeneratedFinancialDocumentRegistry(proof).ReadFrozenAsync(receipt, default);
        Assert.NotNull(frozen); Assert.Equal(originalBytes, frozen.Bytes); Assert.Equal(0, await FinancialRowsAsync(proof));
    }

    private static string[] FinancialMigrations(bool postgres) => postgres
        ? ["20261002053002_AddGeneratedFinancialDocumentAttemptsPostgres", "20261002063941_AddGeneratedFinancialClaimsPostgres",
            "20261002124144_AddGeneratedFinancialRegistryLifecyclePostgres", "20261002134415_AddGeneratedFinancialAttemptRecoveryPostgres",
            "20261002203603_AddGeneratedCaseReservationRecoveryPostgres"]
        : ["20261002052938_AddGeneratedFinancialDocumentAttempts", "20261002063909_AddGeneratedFinancialClaims",
            "20261002124136_AddGeneratedFinancialRegistryLifecycle", "20261002134407_AddGeneratedFinancialAttemptRecovery",
            "20261002203555_AddGeneratedCaseReservationRecovery"];

    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task FinancialCapturePreservesExistingStatusAliasesAndRejectsUnapprovedOrder(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        var order = await f.Db.ClientCaseWorkOrders.SingleAsync();
        var approved = new[] { "approved", " СОГЛАСОВАН ", "согласовано" };
        var aliases = kind == GeneratedFinancialDocumentKind.Invoice ? approved : approved.Concat(new[] { "invoice_created", "invoice-created", "счет создан", "счёт создан" });
        foreach (var alias in aliases)
        {
            order.Status = alias; await f.Db.SaveChangesAsync();
            var result = await new GeneratedFinancialDocumentRegistry(f.Db).CaptureAsync(f.Case.Id, order.Id, f.User.Id,
                f.User.FullName, Guid.NewGuid(), kind, new string('a', 64),
                (c, o, identity, _) => Task.FromResult(FinancialCapture(c, o, identity, kind, f.User.Id, f.User.FullName)), default);
            Assert.Equal("recorded", result.Status);
            f.Db.CaseDocumentUploadRetirements.Add(FinancialRetirement(result.Receipt!));
            await f.Db.SaveChangesAsync();
        }
        foreach (var status in new[] { "draft", "Отменён", "на согласовании" })
        {
            order.Status = status; await f.Db.SaveChangesAsync();
            var result = await new GeneratedFinancialDocumentRegistry(f.Db).CaptureAsync(f.Case.Id, order.Id, f.User.Id,
                f.User.FullName, Guid.NewGuid(), kind, new string('a', 64), (_, _, _, _) => throw new InvalidOperationException("Unapproved source rendered."), default);
            Assert.Equal("conflict", result.Status);
        }
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
    }

    private static GeneratedFinancialDocumentCapture FinancialCapture(ClientCase clientCase, ClientCaseWorkOrder order,
        GeneratedFinancialIdentity identity, GeneratedFinancialDocumentKind kind, int actorId, string actorName)
    {
        var lines = order.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id).Select(i => new GeneratedFinancialLine(
            i.Id, i.ServiceName, i.Quantity, i.UnitPrice, i.SortOrder, i.ExecutorUserId)).ToArray();
        var date = new DateTime(2026, 10, 2);
        return new(new GeneratedFinancialDomainSnapshot(1, clientCase.Id, order.Id, actorId, actorName, kind, identity,
            "OWNED-FROZEN-NUMBER", date, kind == GeneratedFinancialDocumentKind.Invoice ? date.AddDays(10) : null,
            kind == GeneratedFinancialDocumentKind.Contract ? date : null, null, kind == GeneratedFinancialDocumentKind.Contract,
            "Исходный заголовок", "Исходное описание", 35m, clientCase.ClientId, clientCase.LegalEntityId,
            string.IsNullOrWhiteSpace(clientCase.CaseNumber) ? clientCase.Title : clientCase.CaseNumber.Trim(), clientCase.Title, order.Title,
            order.Currency, new DateTime(2026, 10, 2, 1, 2, 3, DateTimeKind.Utc), lines),
            $"<html>{clientCase.Title}: {order.Title}; {kind}; ID={identity.EntityId}; 35,00</html>");
    }
    private static ManagedCaseDocumentGenerationSource FinancialSource(GeneratedFinancialDocumentReceipt receipt)
        => new(receipt.Proof.CaseId, receipt.Proof.ActorId, receipt.Proof.UploadKey,
            $"{receipt.Proof.Kind}-{receipt.Proof.EntityId}", $"{receipt.Proof.Kind}-{receipt.Proof.EntityId}",
            $"case-{receipt.Proof.CaseId}-{receipt.Proof.Kind}-{receipt.Proof.EntityId}.html", "text/html; charset=utf-8",
            receipt.Proof.Kind switch { "invoice" => "Счет", "act" => "Акт", "contract" => "Договор", _ => throw new InvalidOperationException() },
            "Черновик", receipt.Generation);

    private sealed class FinancialCaptureSavedFailure : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<GeneratedFinancialDocumentAttempt>().Any())
                throw new InvalidOperationException("Owned failure after financial attempt and bytes were saved.");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class FinancialCaptureCommitAckFailure : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool Triggered { get; private set; }
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Armed && !Triggered && eventData.Context!.ChangeTracker.Entries<GeneratedFinancialDocumentAttempt>().Any())
            { Triggered = true; throw new InvalidOperationException("Owned lost financial capture commit ACK."); }
            return Task.CompletedTask;
        }
    }
}
