using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class LegalEntitiesControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainSettingsVersion_ReadKeepsCompaniesPolicyAndVersionCoherent(bool changeBeforeStateRead)
    {
        var hook = new MainSettingsCoherentReadHook(changeBeforeStateRead);
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: hook);
        var actor = await CreateUserAsync(db, "admin");
        var first = await CreateLegalEntityAsync(db, "Original"); var second = await CreateLegalEntityAsync(db, "Replacement");
        var controller = CreateController(db, actor);
        Assert.IsType<OkObjectResult>((await controller.SetCompanyProfileMain(first, default)).Result);
        var original = await ReadMainSettings(controller);
        await using var writer = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(db.Database.GetDbConnection()).Options);
        hook.Change = async () =>
        {
            foreach (var row in await writer.SystemSettings.Where(item => item.Key == "legalEntities.mainCompanyId" || item.Key == "legalEntities.defaultMainCompanyId" || item.Key == "legalEntities.mainCompanyIds").ToListAsync())
                row.Value = row.Key == "legalEntities.mainCompanyIds" ? $"[{second}]" : second.ToString(System.Globalization.CultureInfo.InvariantCulture);
            writer.SystemSettings.Add(new SystemSetting { Key = DocumentSellerSnapshotPolicy.SettingKey, Value = DocumentSellerSnapshotPolicy.OnCreate });
            await writer.SaveChangesAsync();
        };
        var observed = await ReadMainSettings(controller);
        Assert.Null(hook.Change);
        var latest = await ReadMainSettings(controller);
        var expected = changeBeforeStateRead ? latest : original;
        Assert.Equal(expected.SettingsVersion, observed.SettingsVersion);
        Assert.Equal(expected.SellerSnapshotTiming, observed.SellerSnapshotTiming);
        Assert.Equal(expected.DefaultCompanyId, observed.DefaultCompanyId);
        Assert.Equal(expected.Companies.Select(item => item.Id), observed.Companies.Select(item => item.Id));
        Assert.NotEqual(original.SettingsVersion, latest.SettingsVersion);
        if (!changeBeforeStateRead)
        {
            var before = await CompanyApprovalMutationSnapshot(writer);
            Assert.IsType<ConflictObjectResult>((await controller.UpdateMainCompanySettings(MainSettingsRequest(observed, [first], first), default)).Result);
            Assert.Equal(before, await CompanyApprovalMutationSnapshot(writer));
        }
    }

    private sealed class MainSettingsCoherentReadHook(bool beforeState) : DbCommandInterceptor
    {
        private int registryReads;
        internal Func<Task>? Change { get; set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Change is not null && command.CommandText.StartsWith("SELECT", StringComparison.Ordinal))
            {
                if (command.CommandText.Contains("\"SystemSettings\"", StringComparison.Ordinal)) ++registryReads;
                if (beforeState ? registryReads == 2 : registryReads >= 2 && command.CommandText.Contains("FROM \"LegalEntities\"", StringComparison.Ordinal))
                {
                    var change = Change; Change = null; await change();
                }
            }
            return result;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task MainSettingsVersion_RequiresCapturedVersionEvenForEmptyRegistry(string? version)
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, await CreateUserAsync(db, "admin"));
        var current = await ReadMainSettings(controller);
        Assert.Matches("^[A-F0-9]{64}$", current.SettingsVersion!);
        var before = await CompanyApprovalMutationSnapshot(db);
        var result = await controller.UpdateMainCompanySettings(new UpdateMainCompanySettingsRequest { ExpectedVersion = version, SellerSnapshotTiming = DocumentSellerSnapshotPolicy.OnCreate }, default);
        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.DoesNotContain(current.SettingsVersion!, System.Text.Json.JsonSerializer.Serialize(conflict.Value));
        Assert.Equal(before, await CompanyApprovalMutationSnapshot(db));
    }

    [Theory]
    [InlineData("legalEntities.mainCompanyIds", "[]")]
    [InlineData("legalEntities.mainCompanyId", "0")]
    [InlineData("legalEntities.defaultMainCompanyId", "0")]
    [InlineData(DocumentSellerSnapshotPolicy.SettingKey, DocumentSellerSnapshotPolicy.OnCreate)]
    public async Task MainSettingsVersion_AllRegistryAndPolicyKeysInvalidateOldForm(string key, string value)
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, await CreateUserAsync(db, "admin"));
        var company = await CreateLegalEntityAsync(db, "Own");
        Assert.IsType<OkObjectResult>((await controller.SetCompanyProfileMain(company, default)).Result);
        var old = await ReadMainSettings(controller);
        var row = await db.SystemSettings.SingleOrDefaultAsync(item => item.Key == key);
        if (row is null) db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync();
        var current = await ReadMainSettings(controller);
        Assert.NotEqual(old.SettingsVersion, current.SettingsVersion);
        var before = await CompanyApprovalMutationSnapshot(db);
        Assert.IsType<ConflictObjectResult>((await controller.UpdateMainCompanySettings(MainSettingsRequest(old, [company], company), default)).Result);
        Assert.Equal(before, await CompanyApprovalMutationSnapshot(db));
    }

    [Fact]
    public async Task MainSettingsVersion_CurrentFormSupportsPolicyChangeAndExplicitClear()
    {
        await using var db = await CreateContextAsync();
        var controller = CreateController(db, await CreateUserAsync(db, "admin"));
        var company = await CreateLegalEntityAsync(db, "Own");
        var initial = await ReadMainSettings(controller);
        Assert.Equal(initial.SettingsVersion, (await ReadMainSettings(controller)).SettingsVersion);
        var request = MainSettingsRequest(initial, [company], company);
        request.ExpectedVersion = initial.SettingsVersion!.ToLowerInvariant();
        request.SellerSnapshotTiming = DocumentSellerSnapshotPolicy.OnCreate;
        var changed = Assert.IsType<MainCompanySettingsDto>(Assert.IsType<OkObjectResult>((await controller.UpdateMainCompanySettings(request, default)).Result).Value);
        Assert.Equal(DocumentSellerSnapshotPolicy.OnCreate, changed.SellerSnapshotTiming);
        Assert.NotEqual(initial.SettingsVersion, changed.SettingsVersion);
        var cleared = Assert.IsType<MainCompanySettingsDto>(Assert.IsType<OkObjectResult>((await controller.UpdateMainCompanySettings(MainSettingsRequest(changed, [], null), default)).Result).Value);
        Assert.Empty(cleared.Companies); Assert.Null(cleared.DefaultCompanyId);
        Assert.Equal(DocumentSellerSnapshotPolicy.OnCreate, cleared.SellerSnapshotTiming);
        Assert.NotEqual(changed.SettingsVersion, cleared.SettingsVersion);
    }

    [Fact]
    public async Task MainSettingsVersion_EmployeeReceivesOnlyAssignedCompaniesWithoutAdminEditVersion()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db); var admin = await CreateUserAsync(db, "admin");
        var first = await CreateLegalEntityAsync(db, "Visible"); var second = await CreateLegalEntityAsync(db, "Hidden");
        var controller = CreateController(db, admin);
        Assert.IsType<OkObjectResult>((await controller.UpdateMainCompanySettings(MainSettingsRequest(await ReadMainSettings(controller), [first, second], second), default)).Result);
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = actor, LegalEntityId = first });
        db.RolePermissions.Add(new RolePermission { RoleId = (await db.Users.SingleAsync(user => user.Id == actor)).RoleId, Section = RolePermissionCatalog.CompanyProfile, CanView = true, CanEdit = true });
        await db.SaveChangesAsync();
        var employee = CreateController(db, actor, isAdmin: true);
        var visible = await ReadMainSettings(employee);
        Assert.Null(visible.SettingsVersion); Assert.Equal(first, visible.DefaultCompanyId);
        Assert.Equal(first, Assert.Single(visible.Companies).Id);
        var request = MainSettingsRequest(await ReadMainSettings(controller), [second], second);
        var before = await CompanyApprovalMutationSnapshot(db);
        Assert.IsType<ForbidResult>((await employee.UpdateMainCompanySettings(request, default)).Result);
        Assert.Equal(before, await CompanyApprovalMutationSnapshot(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainSettingsVersion_PostgreSqlOnlyOneWriterCanReplaceCapturedRegistry(bool overlapping)
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        var barrier = new MainSettingsReadBarrier();
        await using var firstDb = CompanyCreatePgContext(database.ConnectionString, new MainSettingsReadHook(overlapping ? barrier : null));
        await using var secondDb = CompanyCreatePgContext(database.ConnectionString, new MainSettingsReadHook(overlapping ? barrier : null));
        var actor = await CreateUserAsync(firstDb, "admin");
        var first = await CreateLegalEntityAsync(firstDb, "First"); var second = await CreateLegalEntityAsync(firstDb, "Second");
        var firstController = CreateController(firstDb, actor); var secondController = CreateController(secondDb, actor);
        var initial = await ReadMainSettings(firstController);
        Assert.Equal(initial.SettingsVersion, (await ReadMainSettings(secondController)).SettingsVersion);
        var a = firstController.UpdateMainCompanySettings(MainSettingsRequest(initial, [first], first), default);
        if (!overlapping) await a;
        var b = secondController.UpdateMainCompanySettings(MainSettingsRequest(initial, [second], second), default);
        var outcomes = await Task.WhenAll(a, b);
        Assert.Single(outcomes.Where(result => result.Result is OkObjectResult));
        Assert.Single(outcomes.Where(result => result.Result is ConflictObjectResult));
        if (overlapping) Assert.Equal(2, barrier.Arrivals);
        var current = await ReadMainSettings(firstController);
        var winner = Assert.IsType<MainCompanySettingsDto>(Assert.IsType<OkObjectResult>(outcomes.Single(result => result.Result is OkObjectResult).Result).Value);
        Assert.Equal(winner.DefaultCompanyId, current.DefaultCompanyId);
        Assert.Equal(winner.SettingsVersion, current.SettingsVersion);
        Assert.Single(current.Companies);
        Assert.Null(firstDb.Database.CurrentTransaction); Assert.Null(secondDb.Database.CurrentTransaction);
        Assert.All(firstDb.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        Assert.All(secondDb.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        Assert.Empty((outcomes[0].Result is ConflictObjectResult ? firstDb : secondDb).ChangeTracker.Entries());
    }

    private sealed class MainSettingsReadBarrier
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

    private sealed class MainSettingsReadHook(MainSettingsReadBarrier? barrier) : DbCommandInterceptor
    {
        private bool arrived;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!arrived && barrier is not null && command.Transaction is not null && command.CommandText.Contains("\"SystemSettings\"", StringComparison.Ordinal))
            {
                arrived = true;
                await barrier.ArriveAsync(cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainSettingsVersion_StaleFormCannotRemoveNewCompanyOrAssignments(bool legacySelection)
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "admin");
        var staff = await CreateUserAsync(db);
        var first = await CreateLegalEntityAsync(db, "Current");
        var second = await CreateLegalEntityAsync(db, "Newly selected");
        var controller = CreateController(db, actor);
        Assert.IsType<OkObjectResult>((await controller.SetCompanyProfileMain(first, default)).Result);
        var old = await ReadMainSettings(controller);
        if (legacySelection)
            Assert.IsType<OkObjectResult>((await controller.SetCompanyProfileMain(second, default)).Result);
        else
            Assert.IsType<OkObjectResult>((await controller.UpdateMainCompanySettings(MainSettingsRequest(old, [first, second], second), default)).Result);
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = staff, LegalEntityId = second });
        await db.SaveChangesAsync();
        var before = await CompanyApprovalMutationSnapshot(db);

        var rejected = await controller.UpdateMainCompanySettings(MainSettingsRequest(old, [first], first), default);

        Assert.IsType<ConflictObjectResult>(rejected.Result);
        Assert.Equal(before, await CompanyApprovalMutationSnapshot(db));
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    private static async Task<MainCompanySettingsDto> ReadMainSettings(LegalEntitiesController controller)
        => Assert.IsType<MainCompanySettingsDto>(Assert.IsType<OkObjectResult>((await controller.GetMainCompanySettings(default)).Result).Value);

    private static UpdateMainCompanySettingsRequest MainSettingsRequest(MainCompanySettingsDto baseline, List<int> ids, int? selected)
    {
        var request = new UpdateMainCompanySettingsRequest { CompanyIds = ids, DefaultCompanyId = selected, SellerSnapshotTiming = baseline.SellerSnapshotTiming };
        request.ExpectedVersion = baseline.SettingsVersion;
        return request;
    }
}
