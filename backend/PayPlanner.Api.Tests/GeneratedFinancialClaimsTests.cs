using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;
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
    public async Task FinancialClaimsSoftDeletedCaseRetainsNumberAndOriginalBytesWithoutPublication(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var captured = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, kind);
        var receipt = Assert.IsType<GeneratedFinancialDocumentReceipt>(captured.Receipt);
        var original = Assert.IsType<GeneratedFinancialFrozenContent>(await new GeneratedFinancialDocumentRegistry(f.Db).ReadFrozenAsync(receipt, default));
        f.Db.Entry(f.Case).Property<bool>("IsDeleted").CurrentValue = true;
        await f.Db.SaveChangesAsync();

        await using var observer = f.Observer();
        Assert.Empty(await observer.ClientCases.ToArrayAsync());
        var retained = await observer.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(receipt.Proof.AttemptId, retained.Id);
        Assert.Equal(original.Bytes, await observer.GeneratedFinancialDocumentContents.Select(c => c.Bytes).SingleAsync());
        Assert.Single(await observer.GeneratedFinancialNumberClaims.ToArrayAsync());
        var registry = new GeneratedFinancialDocumentRegistry(observer);
        Assert.False(await registry.CanPublishAsync(FinancialSource(receipt), default));
        Assert.Null(await registry.ReadFrozenAsync(receipt, default));
        Assert.Equal(0, f.Local.ReadCount); Assert.Equal(0, f.Mirror.ReadCount);
        Assert.Equal(0, await FinancialRowsAsync(observer));
        Assert.Empty(await observer.ClientCaseDocuments.ToArrayAsync());
        Assert.False(observer.Database.HasPendingModelChanges());
        await using (var transaction = await observer.Database.BeginTransactionAsync())
        {
            var number = await new GeneratedFinancialClaims(observer).NextNumberAsync(kind, f.Case.Id, orderId,
                original.Domain.Date, original.Domain.Number, default);
            Assert.Equal(original.Domain.Number + "-2", number);
            await transaction.RollbackAsync();
        }

        f.Db.Entry(f.Case).Property<bool>("IsDeleted").CurrentValue = false;
        await f.Db.SaveChangesAsync();
        var restored = Assert.IsType<GeneratedFinancialFrozenContent>(await registry.ReadFrozenAsync(receipt, default));
        Assert.Equal(original.Bytes, restored.Bytes);
        Assert.Equal(original.Domain.Identity, restored.Domain.Identity);
        Assert.True(await registry.CanPublishAsync(FinancialSource(receipt), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialClaimsInvoiceSourceAllowsOnlyOneActiveAttemptAcrossAuthors(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var role = new Role
        {
            Name = "second-invoice-author",
            Permissions = new[]
            { RolePermissionCatalog.Cases, RolePermissionCatalog.Accounts }.Select(section => new RolePermission
            { Section = section, CanView = true, CanCreate = true, CanEdit = true, ViewScope = DataVisibilityScope.All }).ToList()
        };
        var other = new User
        {
            Email = "second-invoice-author@example.local",
            FullName = "Другой автор",
            PasswordHash = "synthetic",
            IsActive = true,
            IsApproved = true,
            Role = role
        };
        f.Db.Users.Add(other); await f.Db.SaveChangesAsync();
        var first = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Invoice);
        Assert.Equal("recorded", first.Status);
        var replay = await new GeneratedFinancialDocumentRegistry(f.Db).CaptureAsync(f.Case.Id, orderId, f.User.Id,
            f.User.FullName, first.Receipt!.Proof.UploadKey, GeneratedFinancialDocumentKind.Invoice, new string('a', 64),
            (_, _, _, _) => throw new InvalidOperationException("Replay rendered a second invoice."), default);
        Assert.Equal("recorded", replay.Status); Assert.True(replay.IsReplay); Assert.Equal(first.Receipt, replay.Receipt);
        var sameAuthor = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Invoice);
        var otherAuthor = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, other.Id, other.FullName, GeneratedFinancialDocumentKind.Invoice);
        Assert.Equal("conflict", sameAuthor.Status); Assert.Equal("conflict", otherAuthor.Status);
        Assert.Single(await f.Db.GeneratedFinancialDocumentAttempts.ToArrayAsync());
        Assert.Single(await f.Db.GeneratedFinancialDocumentContents.ToArrayAsync());
        Assert.Single(await f.Db.GeneratedFinancialNumberClaims.ToArrayAsync());
        // This is a trusted retirement marker fixture, not qualification of the
        // registry-only retirement endpoint, whose lifecycle is a separate stage.
        f.Db.CaseDocumentUploadRetirements.Add(FinancialRetirement(first.Receipt)); await f.Db.SaveChangesAsync();
        var replacement = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, other.Id, other.FullName, GeneratedFinancialDocumentKind.Invoice);
        Assert.Equal("recorded", replacement.Status);
        Assert.Equal("CLAIM", (await new GeneratedFinancialDocumentRegistry(f.Db).ReadFrozenAsync(replacement.Receipt!, default))!.Domain.Number);
        Assert.Equal(2, await f.Db.GeneratedFinancialDocumentAttempts.CountAsync());
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialClaimsConcurrentInvoiceSourceAcceptsExactlyOneAttempt(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync(); f.PrepareIdentityObservers();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await start.Task; await using var db = f.IdentityObserver();
            return await CaptureWithClaimsAsync(db, f.Case.Id, orderId, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Invoice);
        })).ToArray();
        start.SetResult(); var results = await Task.WhenAll(tasks);
        Assert.Single(results, r => r.Status == "recorded"); Assert.Equal(3, results.Count(r => r.Status == "conflict"));
        await using var proof = f.IdentityObserver();
        Assert.Single(await proof.GeneratedFinancialDocumentAttempts.ToArrayAsync());
        Assert.Single(await proof.GeneratedFinancialDocumentContents.ToArrayAsync());
        Assert.Single(await proof.GeneratedFinancialNumberClaims.ToArrayAsync());
        Assert.Equal(0, await FinancialRowsAsync(proof)); Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task FinancialClaimsNumberPreservesHolesDateDefaultAndLength(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await CreateFixtureAsync(postgres, 2); var date = new DateTime(2026, 10, 2);
        for (var ordinal = 1; ordinal <= 129; ordinal++)
        {
            if (ordinal == 7) continue;
            var number = ordinal == 1 ? "CLAIM" : $"CLAIM-{ordinal}";
            if (kind == GeneratedFinancialDocumentKind.Invoice) f.Db.Invoices.Add(new Invoice { Number = number, Date = date });
            else if (kind == GeneratedFinancialDocumentKind.Act) f.Db.Acts.Add(new Act { Number = number, Date = date, Amount = 1m });
            else f.Db.Contracts.Add(new Contract { Number = number, Date = date, Amount = 1m });
        }
        await f.Db.SaveChangesAsync();
        await using var tx = await f.Db.Database.BeginTransactionAsync(); var claims = new GeneratedFinancialClaims(f.Db);
        Assert.Equal("CLAIM-7", await claims.NextNumberAsync(kind, f.Case.Id, 42, date, " CLAIM ", default));
        Assert.Equal("CLAIM", await claims.NextNumberAsync(kind, f.Case.Id, 42, date.AddDays(1), "CLAIM", default));
        Assert.Equal(new string('x', 120), await claims.NextNumberAsync(kind, f.Case.Id, 42, date, new string('x', 130), default));
        var prefix = kind switch { GeneratedFinancialDocumentKind.Invoice => "WO", GeneratedFinancialDocumentKind.Act => "WO-ACT", _ => "WO-CONTRACT" };
        Assert.Equal($"{prefix}-{f.Case.Id}-42", await claims.NextNumberAsync(kind, f.Case.Id, 42, date, null, default));
        Assert.Empty(await f.Db.GeneratedFinancialNumberClaims.ToArrayAsync()); await tx.RollbackAsync();
    }

    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task FinancialClaimsPendingNumberRemainsReservedUntilExplicitRetirement(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var first = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, kind);
        // Invoice source policy allows one pending attempt per order. Number
        // reservations must still apply globally, including a different case.
        var secondCaseId = f.Case.Id; var secondOrderId = orderId;
        if (kind == GeneratedFinancialDocumentKind.Invoice)
        {
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
            secondCaseId = other.ClientCaseId; secondOrderId = other.Id;
        }
        var second = await CaptureWithClaimsAsync(f.Db, secondCaseId, secondOrderId, f.User.Id, f.User.FullName, kind);
        Assert.Equal("recorded", first.Status); Assert.Equal("recorded", second.Status);
        var registry = new GeneratedFinancialDocumentRegistry(f.Db);
        Assert.Equal("CLAIM", (await registry.ReadFrozenAsync(first.Receipt!, default))!.Domain.Number);
        Assert.Equal("CLAIM-2", (await registry.ReadFrozenAsync(second.Receipt!, default))!.Domain.Number);
        f.Db.CaseDocumentUploadRetirements.Add(FinancialRetirement(first.Receipt!)); await f.Db.SaveChangesAsync();
        var replacement = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, kind);
        Assert.Equal("recorded", replacement.Status);
        Assert.Equal("CLAIM", (await registry.ReadFrozenAsync(replacement.Receipt!, default))!.Domain.Number);
        Assert.Null(await registry.ReadFrozenAsync(first.Receipt!, default));
        Assert.Equal(3, await f.Db.GeneratedFinancialDocumentAttempts.CountAsync()); Assert.Equal(3, await f.Db.GeneratedFinancialDocumentContents.CountAsync());
        Assert.Equal(2, await f.Db.GeneratedFinancialNumberClaims.CountAsync()); Assert.Equal(0, await FinancialRowsAsync(f.Db));
        Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialClaimsActPreventsOverlapAndKeepsCancelledLedgerRules(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var order = await f.Db.ClientCaseWorkOrders.Include(o => o.Items).SingleAsync();
        order.Items.Add(new ClientCaseWorkOrderItem { ServiceName = "Вторая", Quantity = 1m, UnitPrice = 10m, SortOrder = 2 });
        order.Items.Add(new ClientCaseWorkOrderItem { ServiceName = "Третья", Quantity = 1m, UnitPrice = 20m, SortOrder = 3 });
        await f.Db.SaveChangesAsync(); var items = order.Items.OrderBy(i => i.SortOrder).Select(i => i.Id).ToArray();
        var first = await CaptureWithClaimsAsync(f.Db, f.Case.Id, order.Id, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Act, [items[0], items[1]]);
        Assert.Equal("recorded", first.Status);
        var overlap = await CaptureWithClaimsAsync(f.Db, f.Case.Id, order.Id, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Act, [items[1], items[2]]);
        Assert.Equal("conflict", overlap.Status);
        Assert.Single(await f.Db.GeneratedFinancialDocumentAttempts.ToArrayAsync()); Assert.Equal(2, await f.Db.GeneratedFinancialActLineClaims.CountAsync());
        var third = await CaptureWithClaimsAsync(f.Db, f.Case.Id, order.Id, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Act, [items[2]]);
        Assert.Equal("recorded", third.Status);
        var keys = items.Select(i => GeneratedFinancialClaims.SourceKey(order.Id, i)).ToArray(); var claims = new GeneratedFinancialClaims(f.Db);
        Assert.Equal(3, (await claims.UsedActSourceKeysAsync(keys, default)).Length);
        f.Db.CaseDocumentUploadRetirements.Add(FinancialRetirement(first.Receipt!)); await f.Db.SaveChangesAsync();
        Assert.Equal([keys[2]], await claims.UsedActSourceKeysAsync(keys, default));
        var normal = new Act
        {
            Number = "NORMAL-ACT",
            Date = new DateTime(2026, 10, 2),
            Amount = 1m,
            LineItems = [new ActLineItem { SourceKey = keys[0], Name = "Выполненная услуга", Quantity = 1m, Price = 1m }]
        };
        f.Db.Acts.Add(normal); await f.Db.SaveChangesAsync(); Assert.Contains(keys[0], await claims.UsedActSourceKeysAsync(keys, default));
        normal.WorkflowStatus = ActWorkflowStatus.Cancelled; await f.Db.SaveChangesAsync(); Assert.DoesNotContain(keys[0], await claims.UsedActSourceKeysAsync(keys, default));
        normal.WorkflowStatus = ActWorkflowStatus.Draft; normal.Status = ActStatus.Terminated; await f.Db.SaveChangesAsync();
        Assert.DoesNotContain(keys[0], await claims.UsedActSourceKeysAsync(keys, default));
        var reused = await CaptureWithClaimsAsync(f.Db, f.Case.Id, order.Id, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Act, [items[0], items[1]]);
        Assert.Equal("recorded", reused.Status); Assert.Equal(5, await f.Db.GeneratedFinancialActLineClaims.CountAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync()); Assert.Empty(await f.Db.StorageProductBindings.ToArrayAsync()); Assert.Equal(0, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task FinancialClaimsConcurrentDifferentCasesReceiveDifferentNumbers(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        for (var i = 0; i < 3; i++) f.Db.ClientCaseWorkOrders.Add(new ClientCaseWorkOrder
        {
            ClientCase = new ClientCase { Title = "Другое дело " + i, ClientId = f.Case.ClientId, ResponsibleUserId = f.User.Id, Status = ClientCaseStatus.Open },
            Title = "Другая работа " + i,
            Status = "approved",
            Currency = "RUB",
            TotalAmount = 35m,
            Items = [new ClientCaseWorkOrderItem { ServiceName = "Услуга", Quantity = 2m, UnitPrice = 17.5m, SortOrder = 1 }]
        });
        await f.Db.SaveChangesAsync();
        var sources = await f.Db.ClientCaseWorkOrders.Select(o => new { o.Id, o.ClientCaseId }).ToArrayAsync(); f.PrepareIdentityObservers();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = sources.Select(source => Task.Run(async () =>
        { await start.Task; await using var db = f.IdentityObserver(); return await CaptureWithClaimsAsync(db, source.ClientCaseId, source.Id, f.User.Id, f.User.FullName, kind); })).ToArray();
        start.SetResult(); var results = await Task.WhenAll(tasks); Assert.All(results, r => Assert.Equal("recorded", r.Status));
        await using var proof = f.IdentityObserver();
        Assert.Equal(4, await proof.GeneratedFinancialDocumentAttempts.CountAsync()); Assert.Equal(4, await proof.GeneratedFinancialNumberClaims.CountAsync());
        var numbers = await proof.GeneratedFinancialNumberClaims.Select(c => c.Number).ToArrayAsync();
        Assert.Equal(new[] { "CLAIM", "CLAIM-2", "CLAIM-3", "CLAIM-4" }, numbers.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(0, await FinancialRowsAsync(proof)); Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialClaimsUpgradeBackfillsAcceptedAttemptWithoutChangingFrozenEvidence(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        await f.Db.GetService<IMigrator>().MigrateAsync(PreviousFinancialMigration(postgres));
        var receipt = await InsertPreviousFinancialAttemptAsync(f);
        var jsonBefore = await f.Db.GeneratedFinancialDocumentAttempts.Select(a => a.FrozenDomainJson).SingleAsync();
        var bytesBefore = await f.Db.GeneratedFinancialDocumentContents.Select(c => c.Bytes).SingleAsync();
        await f.Db.Database.MigrateAsync(); Assert.False(f.Db.Database.HasPendingModelChanges());
        Assert.Single(await f.Db.GeneratedFinancialNumberClaims.ToArrayAsync()); Assert.Single(await f.Db.GeneratedFinancialActLineClaims.ToArrayAsync());
        Assert.Equal(3, await f.Db.GeneratedFinancialNumberScopes.CountAsync());
        var frozen = Assert.IsType<GeneratedFinancialFrozenContent>(await new GeneratedFinancialDocumentRegistry(f.Db).ReadFrozenAsync(receipt, default));
        Assert.Equal(bytesBefore, frozen.Bytes); Assert.Equal(jsonBefore, (await f.Db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleAsync()).FrozenDomainJson);
        Assert.Equal(0, await FinancialRowsAsync(f.Db));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Db.GetService<IMigrator>().MigrateAsync(PreviousFinancialMigration(postgres)));
        Assert.Single(await f.Db.GeneratedFinancialNumberClaims.ToArrayAsync());
        Assert.Equal(jsonBefore, await f.Db.GeneratedFinancialDocumentAttempts.Select(a => a.FrozenDomainJson).SingleAsync());
        Assert.Equal(bytesBefore, await f.Db.GeneratedFinancialDocumentContents.Select(c => c.Bytes).SingleAsync());
        // A later lifecycle migration may have downgraded before the claims
        // guard refused its own downgrade. Restore the current model explicitly.
        await f.Db.Database.MigrateAsync();
        Assert.Equal(bytesBefore, (await new GeneratedFinancialDocumentRegistry(f.Db).ReadFrozenAsync(receipt, default))!.Bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialClaimsUpgradeRejectsDuplicateAcceptedNumbersWithoutRewritingAttempts(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        await f.Db.GetService<IMigrator>().MigrateAsync(PreviousFinancialMigration(postgres));
        await InsertPreviousFinancialAttemptAsync(f); await InsertPreviousFinancialAttemptAsync(f);
        var before = await f.Db.GeneratedFinancialDocumentAttempts.OrderBy(a => a.Id).Select(a => a.FrozenDomainJson).ToArrayAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Db.Database.MigrateAsync());
        Assert.Equal(before, await f.Db.GeneratedFinancialDocumentAttempts.OrderBy(a => a.Id).Select(a => a.FrozenDomainJson).ToArrayAsync());
        Assert.Equal(2, await f.Db.GeneratedFinancialDocumentContents.CountAsync());
        var pending = (await f.Db.Database.GetPendingMigrationsAsync()).ToArray();
        // The failed claims migration must leave its entire known suffix pending,
        // including migrations added after the financial registry.
        var expectedPending = f.Db.Database.GetMigrations()
            .SkipWhile(migration => !migration.Contains("AddGeneratedFinancialClaims", StringComparison.Ordinal)).ToArray();
        Assert.True(expectedPending.Length >= 4);
        Assert.Equal(expectedPending, pending);
        Assert.Contains("AddGeneratedFinancialClaims", pending[0]);
        Assert.Contains("AddGeneratedFinancialRegistryLifecycle", pending[1]);
        Assert.Contains("AddGeneratedFinancialAttemptRecovery", pending[2]);
        Assert.Contains("AddGeneratedCaseReservationRecovery", pending[3]);
        Assert.Equal(0, await FinancialRowsAsync(f.Db));
    }

    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task FinancialClaimsLateLedgerNumberCollisionBlocksSourceBeforeFileRead(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await CreateFixtureAsync(postgres, 2); var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var result = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, kind);
        var receipt = result.Receipt!; var date = new DateTime(2026, 10, 2);
        if (kind == GeneratedFinancialDocumentKind.Invoice) f.Db.Invoices.Add(new Invoice { Number = "CLAIM", Date = date });
        else if (kind == GeneratedFinancialDocumentKind.Act) f.Db.Acts.Add(new Act { Number = "CLAIM", Date = date, Amount = 1m });
        else f.Db.Contracts.Add(new Contract { Number = "CLAIM", Date = date, Amount = 1m });
        await f.Db.SaveChangesAsync();
        await using var proof = f.Observer(); var registry = new GeneratedFinancialDocumentRegistry(proof);
        Assert.False(await registry.CanPublishAsync(FinancialSource(receipt), default)); Assert.Null(await registry.ReadFrozenAsync(receipt, default));
        Assert.Empty(await proof.ClientCaseDocuments.ToArrayAsync()); Assert.Single(await proof.GeneratedFinancialDocumentAttempts.ToArrayAsync());
        Assert.Equal(0, f.Local.ReadCount); Assert.Equal(0, f.Mirror.ReadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialClaimsLostClaimCannotBeReplacedByRenderingAgain(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 2); var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var result = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Act);
        var receipt = result.Receipt!;
        f.Db.GeneratedFinancialNumberClaims.Remove(await f.Db.GeneratedFinancialNumberClaims.SingleAsync()); await f.Db.SaveChangesAsync();
        await using var proof = f.Observer(); var registry = new GeneratedFinancialDocumentRegistry(proof);
        var replay = await registry.CaptureAsync(f.Case.Id, orderId, f.User.Id, f.User.FullName, receipt.Proof.UploadKey,
            GeneratedFinancialDocumentKind.Act, new string('a', 64), (_, _, _, _) => throw new InvalidOperationException("Missing claim caused another render."), default);
        Assert.Equal("conflict", replay.Status); Assert.Null(await registry.ReadFrozenAsync(receipt, default));
        Assert.Single(await proof.GeneratedFinancialDocumentAttempts.ToArrayAsync()); Assert.Single(await proof.GeneratedFinancialDocumentContents.ToArrayAsync());
        Assert.Equal(0, await FinancialRowsAsync(proof));
    }

    private static Task<GeneratedFinancialCaptureResult> CaptureWithClaimsAsync(PaymentContext db, int caseId, int workOrderId,
        int actorId, string actorName, GeneratedFinancialDocumentKind kind, int[]? itemIds = null)
        => new GeneratedFinancialDocumentRegistry(db).CaptureAsync(caseId, workOrderId, actorId, actorName, Guid.NewGuid(), kind, new string('a', 64),
            async (c, o, identity, token) =>
            {
                var initial = FinancialCapture(c, o, identity, kind, actorId, actorName);
                var lines = initial.Domain.Lines.Where(l => itemIds is null || itemIds.Contains(l.WorkOrderItemId)).ToArray();
                var amount = kind == GeneratedFinancialDocumentKind.Act ? decimal.Round(lines.Sum(l => l.Quantity * l.Price), 2, MidpointRounding.AwayFromZero)
                    : decimal.Round(lines.Sum(l => decimal.Round(l.Quantity * l.Price, 2, MidpointRounding.AwayFromZero)), 2, MidpointRounding.AwayFromZero);
                var number = await new GeneratedFinancialClaims(db).NextNumberAsync(kind, caseId, workOrderId, initial.Domain.Date, "CLAIM", token);
                return initial with
                {
                    Domain = initial.Domain with { Number = number, Lines = lines, Amount = amount },
                    Html = $"<html>{c.Title}; {o.Title}; ID={identity.EntityId}; №{number}; сумма={amount}</html>"
                };
            }, default);
    private static CaseDocumentUploadRetirement FinancialRetirement(GeneratedFinancialDocumentReceipt receipt)
        => new()
        {
            CaseId = receipt.Proof.CaseId,
            ActorId = receipt.Proof.ActorId,
            UploadKey = receipt.Proof.UploadKey,
            RetiredAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified)
        };
    private static string PreviousFinancialMigration(bool postgres) => postgres
        ? "20261002053002_AddGeneratedFinancialDocumentAttemptsPostgres" : "20261002052938_AddGeneratedFinancialDocumentAttempts";
    private static async Task<GeneratedFinancialDocumentReceipt> InsertPreviousFinancialAttemptAsync(Fixture f)
    {
        await using var transaction = await f.Db.Database.BeginTransactionAsync();
        var order = await f.Db.ClientCaseWorkOrders.AsNoTracking().Include(o => o.Items).SingleAsync();
        var identity = await new GeneratedFinancialIdentityAllocator(f.Db).AllocateAsync(GeneratedFinancialDocumentKind.Act, default);
        var capture = FinancialCapture(f.Case, order, identity, GeneratedFinancialDocumentKind.Act, f.User.Id, f.User.FullName);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web); var json = JsonSerializer.Serialize(capture.Domain, jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(capture.Html); var id = Guid.NewGuid(); var key = Guid.NewGuid(); var request = new string('a', 64);
        var domainSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        var contentSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await f.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "GeneratedFinancialDocumentAttempts" ("Id", "UploadKey", "ClientCaseId", "WorkOrderId", "ActorId", "ActorName", "Kind", "EntityId", "InvoiceEntityId", "RequestSha256", "FrozenDomainJson", "FrozenDomainSha256", "ContentSizeBytes", "ContentSha256")
            VALUES ({id}, {key}, {f.Case.Id}, {order.Id}, {f.User.Id}, {f.User.FullName}, {"act"}, {identity.EntityId}, NULL, {request}, {json}, {domainSha}, {bytes.LongLength}, {contentSha});
            """, default);
        await f.Db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "GeneratedFinancialDocumentContents" ("AttemptId", "Bytes") VALUES ({id}, {bytes});""", default);
        await transaction.CommitAsync();
        var proof = new GeneratedFinancialDocumentProof(1, id, key, f.Case.Id, order.Id, f.User.Id, "act", identity.EntityId, null, domainSha, bytes.LongLength, contentSha);
        return new(proof, new("financial", request, JsonSerializer.Serialize(proof, jsonOptions)));
    }
}
