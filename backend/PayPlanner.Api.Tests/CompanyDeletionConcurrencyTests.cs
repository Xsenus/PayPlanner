using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class LegalEntitiesControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompanyDelete_PostgreSqlFailureRestoresAllPersistedValues(bool own)
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        var failure = new CompanyDeleteFailure();
        await using var db = CompanyCreatePgContext(database.ConnectionString, failure);
        var fixture = await CompanyDeleteFixture(db, own); var before = await CompanyDeleteSnapshot(db);
        failure.Point = "company";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateController(db, fixture.Actor).Delete(fixture.Company, default));
        Assert.Equal("Synthetic company-delete failure after a real database save", error.Message);
        Assert.Equal(1, failure.Hits); Assert.Equal(before, await CompanyDeleteSnapshot(db));
        Assert.Null(db.Database.CurrentTransaction); Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("approval", 403)]
    [InlineData("delete-permission", 403)]
    [InlineData("scope", 404)]
    [InlineData("became-own", 403)]
    public async Task CompanyDelete_PostgreSqlRechecksActorScopeAndOwnershipAtAdmission(string change, int status)
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        await using var writer = CompanyCreatePgContext(database.ConnectionString);
        var fixture = await CompanyDeleteFixture(writer, change == "approval");
        var actor = change == "approval" ? fixture.Actor : fixture.Staff;
        await SetLegalEntityPermissionAsync(writer, fixture.Staff, false, false, true);
        writer.ChangeTracker.Clear();
        var roleId = await writer.Users.Where(row => row.Id == actor).Select(row => row.RoleId).SingleAsync();
        string? afterChange = null;
        var hook = new CompanyDeleteAdmissionHook(async () =>
        {
            if (change == "approval") await writer.Users.Where(row => row.Id == actor).ExecuteUpdateAsync(update => update.SetProperty(row => row.IsApproved, false));
            else if (change == "delete-permission") await writer.RolePermissions.Where(row => row.RoleId == roleId && row.Section == RolePermissionCatalog.LegalEntities).ExecuteUpdateAsync(update => update.SetProperty(row => row.CanDelete, false));
            else if (change == "scope") await writer.RolePermissions.Where(row => row.RoleId == roleId && row.Section == RolePermissionCatalog.LegalEntities).ExecuteUpdateAsync(update => update.SetProperty(row => row.ViewScope, DataVisibilityScope.Own));
            else Assert.IsType<OkObjectResult>((await CreateController(writer, fixture.Actor).SetCompanyProfileMain(fixture.Company, default)).Result);
            afterChange = await CompanyDeleteSnapshot(writer);
        });
        await using var db = CompanyCreatePgContext(database.ConnectionString, hook);
        var result = await CreateController(db, actor, isAdmin: true).Delete(fixture.Company, default);
        Assert.Equal(status, result switch { ForbidResult => 403, NotFoundResult => 404, _ => 0 });
        Assert.Equal(1, hook.Changes); Assert.NotNull(afterChange);
        Assert.Equal(afterChange, await CompanyDeleteSnapshot(writer));
        Assert.Null(db.Database.CurrentTransaction); Assert.Empty(db.ChangeTracker.Entries());
    }

    private sealed class CompanyDeleteAdmissionHook(Func<Task> change) : DbCommandInterceptor
    {
        internal int Changes { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Changes == 0 && command.Transaction is not null && command.CommandText.Contains("\"Users\"", StringComparison.Ordinal))
            {
                ++Changes; await change();
            }
            return result;
        }
    }

    [Fact]
    public async Task CompanyDelete_PostgreSqlConcurrentDeletesCommitOnlyOneResult()
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        await using var seed = CompanyCreatePgContext(database.ConnectionString);
        var fixture = await CompanyDeleteFixture(seed, true); var protectedBefore = await CompanyDeleteProtectedSnapshot(seed);
        var gate = new CompanyDeleteReadGate();
        await using var first = CompanyCreatePgContext(database.ConnectionString, new CompanyDeleteReadHook(gate));
        await using var second = CompanyCreatePgContext(database.ConnectionString, new CompanyDeleteReadHook(gate));
        var results = await Task.WhenAll(CreateController(first, fixture.Actor).Delete(fixture.Company, default), CreateController(second, fixture.Actor).Delete(fixture.Company, default));
        Assert.Equal(2, gate.Arrivals); Assert.Single(results.OfType<NoContentResult>()); Assert.Single(results.OfType<ConflictObjectResult>());
        Assert.Equal(protectedBefore, await CompanyDeleteProtectedSnapshot(seed));
        Assert.Equal(1, await seed.ObjectAuditEvents.CountAsync(row => row.ObjectType == "legal-entity" && row.ObjectId == fixture.Company && row.Action == "Deleted"));
        Assert.Null(first.Database.CurrentTransaction); Assert.Null(second.Database.CurrentTransaction);
    }

    private sealed class CompanyDeleteReadGate
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        internal int Arrivals => Volatile.Read(ref arrivals);
        internal async Task ArriveAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref arrivals) == 2) ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
    }

    private sealed class CompanyDeleteReadHook(CompanyDeleteReadGate gate) : DbCommandInterceptor
    {
        private bool arrived;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!arrived && command.Transaction is not null && command.CommandText.Contains("FROM \"LegalEntities\"", StringComparison.Ordinal))
            {
                arrived = true; await gate.ArriveAsync(cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task CompanyDelete_PostgreSqlCannotLeaveDeletedCompanyInOwnRegistry()
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        await using var seed = CompanyCreatePgContext(database.ConnectionString);
        var fixture = await CompanyDeleteFixture(seed, false); var protectedBefore = await CompanyDeleteProtectedSnapshot(seed);
        var pause = new CompanyDeleteSavePause();
        await using var first = CompanyCreatePgContext(database.ConnectionString, pause);
        await using var second = CompanyCreatePgContext(database.ConnectionString);
        var pending = CreateController(first, fixture.Actor).Delete(fixture.Company, default);
        Microsoft.AspNetCore.Mvc.ActionResult<PayPlanner.Api.Models.Responses.LegalEntityDetailDto>? selection = null;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            selection = await CreateController(second, fixture.Actor).SetCompanyProfileMain(fixture.Company, timeout.Token);
        }
        finally { pause.Release.TrySetResult(); }
        var deletion = await pending.WaitAsync(TimeSpan.FromSeconds(25));
        Assert.NotNull(selection);
        Assert.True(deletion is ConflictObjectResult || selection.Result is ConflictObjectResult);
        Assert.True(deletion is NoContentResult or ConflictObjectResult);
        Assert.True(selection.Result is OkObjectResult or ConflictObjectResult);
        var live = await seed.LegalEntities.AsNoTracking().AnyAsync(row => row.Id == fixture.Company);
        var own = (await PayPlanner.Api.Services.Companies.OwnCompanyRegistry.LoadIdsAsync(seed)).Contains(fixture.Company);
        Assert.True(live || !own);
        Assert.Equal(deletion is ConflictObjectResult, live);
        Assert.Equal(selection.Result is OkObjectResult, own);
        Assert.Equal(protectedBefore, await CompanyDeleteProtectedSnapshot(seed));
        Assert.Null(first.Database.CurrentTransaction); Assert.Null(second.Database.CurrentTransaction);
    }

    [Fact]
    public async Task CompanyDelete_TwoSqliteConnectionsRejectBusyDeleteWithoutPartialChanges()
    {
        await using var seed = await CreateContextAsync(); var fixture = await CompanyDeleteFixture(seed, true);
        var before = await CompanyDeleteSnapshot(seed);
        var connectionString = $"Data Source=company_delete_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=1;Pooling=False";
        await using var anchor = new SqliteConnection(connectionString); await anchor.OpenAsync();
        ((SqliteConnection)seed.Database.GetDbConnection()).BackupDatabase(anchor);
        var pause = new CompanyDeleteSavePause();
        await using var first = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connectionString).AddInterceptors(pause).Options);
        await using var second = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connectionString).Options);
        var pending = CreateController(first, fixture.Actor).Delete(fixture.Company, default);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsType<ConflictObjectResult>(await CreateController(second, fixture.Actor).Delete(fixture.Company, default));
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<NoContentResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(1, pause.Pauses);
        Assert.NotEqual(before, await CompanyDeleteSnapshot(first));
        Assert.Null(first.Database.CurrentTransaction); Assert.Null(second.Database.CurrentTransaction);
    }

    private sealed class CompanyDeleteSavePause : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Pauses { get; private set; }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Pauses == 0 && eventData.Context!.ChangeTracker.Entries<LegalEntity>().Any(entry => entry.Property<bool>("IsDeleted").CurrentValue))
            {
                ++Pauses; Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(35), cancellationToken);
            }
            return result;
        }
    }
}
