using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static bool SelectionLockFailure(Exception error)
    {
        Exception? cause = error; while (cause is not null && cause is not PostgresException) cause = cause.InnerException;
        return cause is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable };
    }

    public static IEnumerable<object[]> LegalSelectionNoParentCases() => from route in new[] { 0, 1, 2 } from create in new[] { false, true } select new object[] { route, create };

    [Theory]
    [MemberData(nameof(LegalSelectionNoParentCases))]
    public async Task ClientLegalSelection_PgNoParentOrUnlinkDoesNotLockSettings(int route, bool create)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var setup = Open(); var seed = await SeedLegalSelectionAsync(setup, "hidden", retain: true);
        await using var writer = Open(); await using var tx = await writer.Database.BeginTransactionAsync(); writer.SystemSettings.Add(new SystemSetting { Key = "selection.unrelated", Value = "busy" }); await writer.SaveChangesAsync();
        await using var request = Open(); var result = await WriteRequiredClientAsync(request, seed.Actor, seed.Client, LegalSelectionInput(seed, null), route, create);
        var value = create ? Assert.IsType<CreatedResult>(result).Value : Assert.IsType<OkObjectResult>(result).Value; var row = Assert.IsType<Client>(value); Assert.Null(row.LegalEntityId); Assert.Null(request.Database.CurrentTransaction);
    }

    private sealed class SelectionBeforeActor(Func<CancellationToken, Task> action) : DbCommandInterceptor
    {
        public bool Called { get; private set; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!Called && command.CommandText.StartsWith("SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = ", StringComparison.Ordinal) && command.CommandText.Contains("FOR SHARE NOWAIT", StringComparison.Ordinal)) { Called = true; await action(ct); }
            return result;
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public async Task ClientLegalSelection_PgPinsFirstRegistryInsertionBeforeActorSnapshot(int route, bool create)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var setup = Open(); var seed = await SeedLegalSelectionAsync(setup, "visible"); var blocked = false; var committed = false;
        var hook = new SelectionBeforeActor(async ct =>
        {
            await using var writer = Open(); await using var tx = await writer.Database.BeginTransactionAsync(ct); await writer.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '250ms'", ct);
            try { writer.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{seed.Legal.Id}]" }); await writer.SaveChangesAsync(ct); await tx.CommitAsync(ct); committed = true; }
            catch (Exception error) when (SelectionLockFailure(error)) { blocked = true; }
        });
        await using var request = Open(hook); var result = await WriteRequiredClientAsync(request, seed.Actor, seed.Client, LegalSelectionInput(seed, seed.Legal.Id), route, create);
        Assert.True(hook.Called && blocked && !committed, $"route={route};called={hook.Called};blocked={blocked};committed={committed};result={result.GetType().Name}"); if (create) Assert.IsType<CreatedResult>(result); else Assert.IsType<OkObjectResult>(result); Assert.Null(request.Database.CurrentTransaction);
        await using var next = Open(); next.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{seed.Legal.Id}]" }); await next.SaveChangesAsync(); var count = await next.Clients.CountAsync();
        Assert.IsType<BadRequestObjectResult>(await WriteRequiredClientAsync(next, seed.Actor, seed.Client, LegalSelectionInput(seed, seed.Legal.Id), route, true)); Assert.Equal(count, await next.Clients.CountAsync());
    }

    public static IEnumerable<object[]> LegalSelectionPinCases() =>
        (from route in new[] { 0, 2 } from target in new[] { "parent", "department", "registry" } select new object[] { route, route == 0, target })
        .Concat(new[] { new object[] { 2, false, "legal-permission" }, new object[] { 0, true, "profile-permission" }, new object[] { 1, false, "assignment" } });

    [Theory]
    [MemberData(nameof(LegalSelectionPinCases))]
    public async Task ClientLegalSelection_PgPinsSelectedAuthorityUntilCommit(int route, bool create, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var setup = Open(); var state = target is "profile-permission" or "assignment" ? "own-assigned" : target == "department" ? "department-allowed" : "visible"; var seed = await SeedLegalSelectionAsync(setup, state);
        if (target == "registry") { setup.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = "[]" }); await setup.SaveChangesAsync(); }
        var called = false; var blocked = false; var committed = false;
        Func<CancellationToken, Task> mutate = async ct =>
        {
            called = true; await using var writer = Open(); await using var tx = await writer.Database.BeginTransactionAsync(ct); await writer.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '250ms'", ct);
            try
            {
                if (target == "parent") await writer.LegalEntities.Where(x => x.Id == seed.Legal.Id).ExecuteUpdateAsync(s => s.SetProperty(x => EF.Property<bool>(x, "IsDeleted"), true), ct);
                if (target == "department") await writer.Users.Where(x => x.Id == seed.Other.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DepartmentId, (int?)null).SetProperty(x => x.Department, "Чужой отдел"), ct);
                if (target == "registry") await writer.SystemSettings.Where(x => x.Key == "legalEntities.mainCompanyIds").ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, $"[{seed.Legal.Id}]"), ct);
                if (target == "assignment") await writer.UserCompanyAccesses.Where(x => x.UserId == seed.Actor.Id && x.LegalEntityId == seed.Legal.Id).ExecuteDeleteAsync(ct);
                if (target is "legal-permission" or "profile-permission") { var section = target == "legal-permission" ? RolePermissionCatalog.LegalEntities : RolePermissionCatalog.CompanyProfile; await writer.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == section).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false), ct); }
                await tx.CommitAsync(ct); committed = true;
            }
            catch (Exception error) when (SelectionLockFailure(error)) { blocked = true; }
        };
        IInterceptor hook = create ? new ClientCreateBeforeSave(mutate) : new ClientUpdateBeforeSave(mutate);
        await using var request = Open(hook); var result = await WriteRequiredClientAsync(request, seed.Actor, seed.Client, LegalSelectionInput(seed, seed.Legal.Id), route, create);
        Assert.True(called && blocked && !committed, $"target={target};called={called};blocked={blocked};committed={committed};result={result.GetType().Name}"); if (create) Assert.IsType<CreatedResult>(result); else Assert.IsType<OkObjectResult>(result); Assert.Null(request.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("department")]
    [InlineData("settings")]
    public async Task ClientLegalSelection_PgBusyRequiredDependenciesReturnConflictWithoutSaving(string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var setup = Open(); var seed = await SeedLegalSelectionAsync(setup, target == "department" ? "department-allowed" : "visible"); var before = await LegalSelectionSnapshotAsync(setup);
        await using var writer = Open(); await using var tx = await writer.Database.BeginTransactionAsync();
        if (target == "parent") await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = {seed.Legal.Id} FOR UPDATE");
        if (target == "department") await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {seed.Other.Id} FOR UPDATE");
        if (target == "settings") { writer.SystemSettings.Add(new SystemSetting { Key = "selection.unrelated", Value = "busy" }); await writer.SaveChangesAsync(); }
        await using var request = Open(); Assert.IsType<ConflictObjectResult>(await WriteRequiredClientAsync(request, seed.Actor, seed.Client, LegalSelectionInput(seed, seed.Legal.Id), 0, true)); Assert.Null(request.Database.CurrentTransaction); Assert.Equal(before, await LegalSelectionSnapshotAsync(request));
        await tx.RollbackAsync(); Assert.IsType<CreatedResult>(await WriteRequiredClientAsync(request, seed.Actor, seed.Client, LegalSelectionInput(seed, seed.Legal.Id), 0, true));
    }

    private sealed class SelectionSqlFault : Exception;
    private sealed class SelectionAfterSql(bool cancel, CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public bool Called { get; private set; }
        public string? Executed { get; private set; }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default)
        {
            Called = true; Executed = await LegalSelectionSnapshotAsync((PaymentContext)data.Context!);
            if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            throw new SelectionSqlFault();
        }
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(0, true, true)]
    [InlineData(2, false, false)]
    [InlineData(2, false, true)]
    public async Task ClientLegalSelection_PgSqlFailureOrCancelRollsBackAndReleasesSettings(int route, bool create, bool cancel)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var setup = Open(); var seed = await SeedLegalSelectionAsync(setup, "visible"); var before = await LegalSelectionSnapshotAsync(setup); using var cancellation = new CancellationTokenSource(); var fault = new SelectionAfterSql(cancel, cancellation);
        await using var request = Open(fault); var input = LegalSelectionInput(seed, seed.Legal.Id);
        var call = () => create ? CreateAtomicClientAsync(request, seed.Actor, input, route, cancellation.Token)
            : UpdateAtomicClientAsync(request, seed.Actor, seed.Client, input, route, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(call); else await Assert.ThrowsAsync<SelectionSqlFault>(call);
        Assert.True(fault.Called); Assert.NotEqual(before, fault.Executed); Assert.Null(request.Database.CurrentTransaction); Assert.False(request.ChangeTracker.HasChanges()); Assert.Equal(before, await LegalSelectionSnapshotAsync(request));
        await using var next = Open(); next.SystemSettings.Add(new SystemSetting { Key = "selection.after-rollback", Value = "released" }); await next.SaveChangesAsync(); Assert.Equal(3, await next.Clients.CountAsync());
    }

    [Fact]
    public async Task ClientLegalSelection_PgAllScopeDoesNotPinIrrelevantOwnerDepartment()
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var setup = Open(); var seed = await SeedLegalSelectionAsync(setup, "visible"); var committed = false;
        var hook = new ClientCreateBeforeSave(async ct => { await using var writer = Open(); await using var tx = await writer.Database.BeginTransactionAsync(ct); await writer.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '250ms'", ct); await writer.Users.Where(x => x.Id == seed.Other.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Department, "Свободное изменение"), ct); await tx.CommitAsync(ct); committed = true; });
        await using var request = Open(hook); Assert.IsType<CreatedResult>(await WriteRequiredClientAsync(request, seed.Actor, seed.Client, LegalSelectionInput(seed, seed.Legal.Id), 0, true)); Assert.True(committed);
    }
}
