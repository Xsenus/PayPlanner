using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Storage;
using Xunit;
using CreateClientCaseWorkOrderActRequest = PayPlanner.Api.Controllers.V2.CasesV2Controller.CreateClientCaseWorkOrderActRequest;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedGeneratedCaseDocumentTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AnnualAct_EditAndDeleteRecheckPermissionAfterWaitingForTheAnnualScope(bool postgres, bool update)
    {
        var fence = new AnnualNumberScopeFence(pauseAfterLock: true);
        await using var f = await CreateFixtureAsync(postgres, 2, false, fence);
        await AnnualGrantOrdinaryAccessAsync(f);
        (await f.Db.RolePermissions.SingleAsync(item => item.RoleId == f.User.RoleId && item.Section == RolePermissionCatalog.Acts)).CanDelete = true;
        var historical = new Act { Number = "42", Date = new DateTime(2032, 3, 4), Amount = 10m, ClientId = f.Case.ClientId, ResponsibleId = f.User.Id };
        f.Db.Acts.Add(historical);
        await f.Db.SaveChangesAsync();
        await using var authority = AnnualPostgresObserver(f);
        var controller = AnnualOrdinaryController(f, f.Db);
        var request = AnnualOrdinaryRequest(f, 2033);
        request.Number = "MANUAL";
        async Task<IActionResult> Mutate() => update
            ? (await controller.Update(historical.Id, request, default)).Result!
            : await controller.Delete(historical.Id, default);
        fence.Armed = true;
        var pending = Mutate();
        try
        {
            await fence.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var permission = await authority.RolePermissions.SingleAsync(item => item.RoleId == f.User.RoleId && item.Section == RolePermissionCatalog.Acts);
            if (update) permission.CanEdit = false;
            else permission.CanDelete = false;
            await authority.SaveChangesAsync();
            fence.Release.TrySetResult();

            Assert.IsType<ForbidResult>(await pending);
            var retained = await authority.Acts.AsNoTracking().SingleAsync(item => item.Id == historical.Id);
            Assert.Equal("42", retained.Number);
            Assert.Equal(new DateTime(2032, 3, 4), retained.Date);
            Assert.Empty(await authority.ActAnnualNumberCounters.ToArrayAsync());
        }
        finally { fence.Release.TrySetResult(); await pending; }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AnnualAct_ConcurrentOrdinarySameKeyKeepsOneCanonicalAct(bool postgres, bool changedCommand)
    {
        var firstFence = new AnnualNumberScopeFence(pauseAfterLock: true);
        var secondFence = new AnnualNumberScopeFence(pauseAfterLock: false);
        await using var f = await CreateFixtureAsync(postgres, 2, false, firstFence);
        await AnnualGrantOrdinaryAccessAsync(f);
        var otherClient = new Client { Name = "Другой источник команды", ResponsibleUserId = f.User.Id, IsActive = true };
        f.Db.Clients.Add(otherClient);
        await f.Db.SaveChangesAsync();
        await using var competing = AnnualPostgresObserver(f, secondFence);
        var key = Guid.NewGuid();
        var firstController = AnnualOrdinaryController(f, f.Db);
        var secondController = AnnualOrdinaryController(f, competing);
        firstController.Request.Headers["X-Upload-Key"] = key.ToString("D");
        secondController.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var secondRequest = AnnualOrdinaryRequest(f, 2030);
        if (changedCommand) secondRequest.ClientId = otherClient.Id;
        firstFence.Armed = true;
        secondFence.Armed = true;
        var first = firstController.Create(AnnualOrdinaryRequest(f, 2030), default);
        try
        {
            await firstFence.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var second = secondController.Create(secondRequest, default);
            if (changedCommand)
            {
                await secondFence.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
                firstFence.Release.TrySetResult();
            }
            Assert.IsType<ConflictObjectResult>((await second.WaitAsync(TimeSpan.FromSeconds(10))).Result);
            firstFence.Release.TrySetResult();
            var canonical = Assert.IsType<ActDto>(Assert.IsType<CreatedResult>((await first).Result).Value);
            var replay = Assert.IsType<ActDto>(Assert.IsType<CreatedResult>((await secondController.Create(AnnualOrdinaryRequest(f, 2030), default)).Result).Value);
            Assert.Equal(canonical.Id, replay.Id);
            Assert.Equal("1", replay.Number);
            Assert.Single(await competing.Acts.ToArrayAsync());
            Assert.Single(await competing.ActCreateReceipts.ToArrayAsync());
            Assert.Equal(1, (await competing.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        }
        finally { firstFence.Release.TrySetResult(); await first; }
    }

    [Theory]
    [InlineData(true, "managed")]
    [InlineData(true, "ordinary")]
    [InlineData(true, "other-case")]
    public async Task AnnualAct_GlobalKeyAdmissionIsRecheckedAfterTheSharedScopeLock(bool postgres, string firstOperation)
    {
        var firstFence = new AnnualNumberScopeFence(pauseAfterLock: true);
        var secondFence = new AnnualNumberScopeFence(pauseAfterLock: false);
        await using var f = await CreateFixtureAsync(postgres, 2, false, firstFence);
        await AnnualGrantOrdinaryAccessAsync(f);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(item => item.Id).SingleAsync();
        var other = firstOperation == "other-case" ? await AnnualOtherOrderAsync(f) : null;
        await using var competing = AnnualPostgresObserver(f, secondFence);
        var key = Guid.NewGuid();
        firstFence.Armed = true;
        secondFence.Armed = true;
        if (firstOperation == "ordinary")
        {
            var ordinary = AnnualOrdinaryController(f, f.Db);
            ordinary.Request.Headers["X-Upload-Key"] = key.ToString("D");
            var first = ordinary.Create(AnnualOrdinaryRequest(f, 2023), default);
            try
            {
                await firstFence.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var second = new GeneratedFinancialDocumentRegistry(competing).CaptureAsync(f.Case.Id, orderId, f.User.Id,
                    f.User.FullName, key, GeneratedFinancialDocumentKind.Act, AnnualHistoricalRequestHash,
                    (_, _, _, _) => throw new InvalidOperationException("A committed ordinary key rendered a managed act."), default);
                await secondFence.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
                firstFence.Release.TrySetResult();
                Assert.IsType<CreatedResult>((await first).Result);
                Assert.Equal("conflict", (await second).Status);
                Assert.Single(await competing.Acts.ToArrayAsync());
                Assert.Empty(await competing.GeneratedFinancialDocumentAttempts.ToArrayAsync());
            }
            finally { firstFence.Release.TrySetResult(); await first; }
        }
        else
        {
            var managed = f.Controller();
            managed.Request.Headers["X-Upload-Key"] = key.ToString("D");
            var first = managed.CreateActFromWorkOrder(f.Case.Id, new CreateClientCaseWorkOrderActRequest { Date = new DateTime(2023, 4, 5) }, default);
            try
            {
                await firstFence.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
                if (other is null)
                {
                    var ordinary = AnnualOrdinaryController(f, competing);
                    ordinary.Request.Headers["X-Upload-Key"] = key.ToString("D");
                    var second = ordinary.Create(AnnualOrdinaryRequest(f, 2023), default);
                    await secondFence.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    firstFence.Release.TrySetResult();
                    Assert.Equal(503, Assert.IsType<ObjectResult>((await first).Result).StatusCode);
                    Assert.IsType<ConflictObjectResult>((await second).Result);
                }
                else
                {
                    var second = new GeneratedFinancialDocumentRegistry(competing).CaptureAsync(other.ClientCaseId, other.Id,
                        f.User.Id, f.User.FullName, key, GeneratedFinancialDocumentKind.Act, AnnualHistoricalRequestHash,
                        (_, _, _, _) => throw new InvalidOperationException("Another case reused the first capture's act key."), default);
                    await secondFence.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    firstFence.Release.TrySetResult();
                    Assert.Equal(503, Assert.IsType<ObjectResult>((await first).Result).StatusCode);
                    Assert.Equal("conflict", (await second).Status);
                }
                Assert.Empty(await competing.Acts.ToArrayAsync());
                Assert.Single(await competing.GeneratedFinancialDocumentAttempts.ToArrayAsync());
            }
            finally { firstFence.Release.TrySetResult(); await first; }
        }
        Assert.Equal(1, (await competing.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        Assert.True(firstFence.Triggered);
        Assert.True(secondFence.Triggered);
    }

    [Theory]
    [InlineData(true)]
    public async Task AnnualAct_DifferentActorsAndSourcesUseOneAnnualSeriesUnderConcurrency(bool postgres)
    {
        var firstFence = new AnnualNumberScopeFence(pauseAfterLock: true);
        var secondFence = new AnnualNumberScopeFence(pauseAfterLock: false);
        await using var f = await CreateFixtureAsync(postgres, 2, false, firstFence);
        await AnnualGrantOrdinaryAccessAsync(f);
        var otherActor = new User { Email = "annual-second@example.test", FullName = "Другой автор", PasswordHash = "synthetic", RoleId = f.User.RoleId, IsActive = true, IsApproved = true };
        var otherClient = new Client { Name = "Другой синтетический клиент", IsActive = true, ResponsibleUser = otherActor };
        f.Db.Clients.Add(otherClient);
        await f.Db.SaveChangesAsync();
        await using var competing = AnnualPostgresObserver(f, secondFence);
        var key = Guid.NewGuid();
        var firstController = AnnualOrdinaryController(f, f.Db);
        firstController.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var secondController = new ActsController(competing, new ActAnnualTestScope.NoopRequirements()) { ControllerContext = ActAnnualTestScope.Context(otherActor.Id) };
        secondController.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var secondRequest = AnnualOrdinaryRequest(f, 2024);
        secondRequest.ClientId = otherClient.Id;
        secondRequest.ResponsibleId = otherActor.Id;
        secondRequest.Date = new DateTime(2024, 12, 31);
        firstFence.Armed = true;
        secondFence.Armed = true;
        var first = firstController.Create(AnnualOrdinaryRequest(f, 2024), default);
        try
        {
            await firstFence.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var second = secondController.Create(secondRequest, default);
            await secondFence.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
            firstFence.Release.TrySetResult();
            var firstAct = Assert.IsType<ActDto>(Assert.IsType<CreatedResult>((await first).Result).Value);
            var secondAct = Assert.IsType<ActDto>(Assert.IsType<CreatedResult>((await second).Result).Value);
            Assert.Equal("1", firstAct.Number);
            Assert.Equal("2", secondAct.Number);
            Assert.NotEqual(firstAct.Id, secondAct.Id);
            Assert.Equal(2, await competing.ActCreateReceipts.CountAsync());
            Assert.Equal(2, (await competing.ActAnnualNumberCounters.SingleAsync()).LastNumber);
        }
        finally { firstFence.Release.TrySetResult(); await first; }
    }

    [Theory]
    [InlineData(true)]
    public async Task AnnualAct_LateCreatePermissionRevocationAfterScopeAdmissionRollsBack(bool postgres)
    {
        var fence = new AnnualNumberScopeFence(pauseAfterLock: true);
        await using var f = await CreateFixtureAsync(postgres, 2, false, fence);
        await AnnualGrantOrdinaryAccessAsync(f);
        await using var authority = AnnualPostgresObserver(f);
        var controller = AnnualOrdinaryController(f, f.Db);
        controller.Request.Headers["X-Upload-Key"] = Guid.NewGuid().ToString("D");
        fence.Armed = true;
        var pending = controller.Create(AnnualOrdinaryRequest(f, 2025), default);
        try
        {
            await fence.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var permission = await authority.RolePermissions.SingleAsync(item => item.RoleId == f.User.RoleId && item.Section == RolePermissionCatalog.Acts);
            permission.CanCreate = false;
            await authority.SaveChangesAsync();
            fence.Release.TrySetResult();

            Assert.IsType<ForbidResult>((await pending).Result);
            Assert.Empty(await authority.Acts.ToArrayAsync());
            Assert.Empty(await authority.ActCreateReceipts.ToArrayAsync());
            Assert.Empty(await authority.ActAnnualNumberCounters.ToArrayAsync());
        }
        finally { fence.Release.TrySetResult(); await pending; }
    }

    [Theory]
    [InlineData(true)]
    public async Task AnnualAct_BusyGlobalScopeReturnsConflictAndTheSameKeyCanRetry(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        await AnnualGrantOrdinaryAccessAsync(f);
        await using var holder = AnnualPostgresObserver(f);
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await new GeneratedFinancialClaims(holder).LockNumberScopeAsync(GeneratedFinancialDocumentKind.Act, default);
        var controller = AnnualOrdinaryController(f, f.Db);
        controller.Request.Headers["X-Upload-Key"] = Guid.NewGuid().ToString("D");
        var request = AnnualOrdinaryRequest(f, 2027);

        Assert.IsType<ConflictObjectResult>((await controller.Create(request, default)).Result);
        Assert.Empty(await f.Db.ActAnnualNumberCounters.ToArrayAsync());
        Assert.Empty(await f.Db.ActCreateReceipts.ToArrayAsync());
        await transaction.RollbackAsync();
        Assert.Equal("1", Assert.IsType<ActDto>(Assert.IsType<CreatedResult>((await controller.Create(request, default)).Result).Value).Number);
    }

    private static PostgreSqlPaymentContext AnnualPostgresObserver(Fixture fixture, IInterceptor? interceptor = null)
    {
        Assert.True(fixture.Db.Database.IsNpgsql());
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(fixture.Db.Database.GetConnectionString()!);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new PostgreSqlPaymentContext(options.Options);
    }

    private sealed class AnnualNumberScopeFence(bool pauseAfterLock) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Triggered { get; private set; }
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!pauseAfterLock && Matches(command)) { Triggered = true; Arrived.TrySetResult(); }
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (pauseAfterLock && Matches(command))
            {
                Triggered = true;
                Arrived.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
        private bool Matches(DbCommand command) => Armed && !Triggered
            && command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("\"GeneratedFinancialNumberScopes\"", StringComparison.Ordinal);
    }
}
