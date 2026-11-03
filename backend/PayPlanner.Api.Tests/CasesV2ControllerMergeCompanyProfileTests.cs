using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static async Task RegisterMergePrimaryAsync(PaymentContext db, MergeSeed seed, string state = "allowed", string registry = "list")
    {
        var id = seed.Input.PrimarySourceId;
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = registry == "list" ? $"[{id}]" : registry == "empty" ? "[]" : "invalid-json" });
        if (registry != "list") db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyId", Value = id.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        if (state != "missing") await GrantSectionPermissionAsync(db, seed.Actor, RolePermissionCatalog.CompanyProfile, canView: state != "view", canEdit: state != "edit", canCreate: false, canDelete: false, canExport: false);
        if (state != "unassigned") db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = seed.Actor.Id, LegalEntityId = id });
        await db.SaveChangesAsync();
        if (state == "admin") await db.Roles.Where(x => x.Id == seed.Actor.RoleId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "admin"));
        db.ChangeTracker.Clear();
    }

    public static IEnumerable<object[]> MergeProfileDenialCases() => from kind in new[] { "legal", "legal+external" } from state in new[] { "missing", "view", "edit", "unassigned" } from execute in new[] { false, true } from postgres in new[] { false, true } select new object[] { kind, state, execute, postgres };

    [Theory]
    [MemberData(nameof(MergeProfileDenialCases))]
    public async Task MergeProfileBoundary_RequiresExistingProfilePermissionAndCompanyAssignment(string kind, string state, bool execute, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync();
        var seed = await SeedMergeBoundaryAsync(db, kind);
        await RegisterMergePrimaryAsync(db, seed, state);
        var before = await MergeSnapshotAsync(db);
        var result = await RunMergeAsync(db, seed, execute);
        Assert.True(state == "unassigned" ? result is NotFoundResult : result is ForbidResult, $"kind={kind};state={state};execute={execute};result={result?.GetType().Name}");
        Assert.Equal(before, await MergeSnapshotAsync(db));
        Assert.Null(db.Database.CurrentTransaction);
    }

    public static IEnumerable<object[]> MergeProfileAllowedCases() => from kind in new[] { "legal", "legal+external" } from state in new[] { "allowed", "admin" } from execute in new[] { false, true } from postgres in new[] { false, true } select new object[] { kind, state, execute, postgres };

    [Theory]
    [MemberData(nameof(MergeProfileAllowedCases))]
    public async Task MergeProfileBoundary_AllowsAuthorizedProfileWithoutCreateOrDeleteProfileRights(string kind, string state, bool execute, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync();
        var seed = await SeedMergeBoundaryAsync(db, kind);
        await RegisterMergePrimaryAsync(db, seed, state);
        // Administrators use the same profile section, but do not need an employee assignment.
        if (state == "admin") await db.UserCompanyAccesses.Where(x => x.UserId == seed.Actor.Id).ExecuteDeleteAsync();
        var before = await MergeSnapshotAsync(db);
        var reply = Assert.IsType<ContactsController.ContactMergePreview>(Assert.IsType<OkObjectResult>(await RunMergeAsync(db, seed, execute)).Value);
        Assert.True(reply.CanMerge);
        var primary = await db.LegalEntities.AsNoTracking().SingleAsync(x => x.Id == seed.Input.PrimarySourceId);
        if (!execute) Assert.Equal(before, await MergeSnapshotAsync(db));
        else
        {
            Assert.Contains("79001110002", primary.ContactPhonesJson!);
            Assert.True(await db.ObjectAuditEvents.AnyAsync(x => x.ObjectType == "contact-merge" && x.ObjectId == primary.Id));
        }
        Assert.Equal($"[{primary.Id}]", await db.SystemSettings.Where(x => x.Key == "legalEntities.mainCompanyIds").Select(x => x.Value).SingleAsync());
        Assert.Null(db.Database.CurrentTransaction);
    }

    public static IEnumerable<object[]> MergeProfileLegacyCases() => from registry in new[] { "empty", "malformed" } from execute in new[] { false, true } from postgres in new[] { false, true } select new object[] { registry, execute, postgres };

    [Theory]
    [MemberData(nameof(MergeProfileLegacyCases))]
    public async Task MergeProfileBoundary_RequiresProfileRightsForLegacyRegistryFallback(string registry, bool execute, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync();
        var seed = await SeedMergeBoundaryAsync(db, "legal");
        await RegisterMergePrimaryAsync(db, seed, "missing", registry);
        var before = await MergeSnapshotAsync(db);
        Assert.IsType<ForbidResult>(await RunMergeAsync(db, seed, execute));
        Assert.Equal(before, await MergeSnapshotAsync(db));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MergeProfileBoundary_HttpRechecksProfileRightsAndAssignment(bool execute, bool assignment)
    {
        await using var db = await PassportBoundaryContextAsync();
        var seed = await SeedMergeBoundaryAsync(db, "legal");
        await RegisterMergePrimaryAsync(db, seed);
        var before = await MergeSnapshotAsync(db);
        using var server = ClientContactServer(db, async () =>
        {
            if (assignment) await db.UserCompanyAccesses.Where(x => x.UserId == seed.Actor.Id).ExecuteDeleteAsync();
            else await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.CompanyProfile).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanEdit, false));
        });
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString());
        using var response = await http.PostAsJsonAsync(execute ? "/api/contacts/merge" : "/api/contacts/merge/preview", seed.Input);
        Assert.Equal(assignment ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await MergeSnapshotAsync(db));
    }

    private sealed class MergeProfileAfterAdmission(Func<CancellationToken, Task> action) : DbCommandInterceptor
    {
        private bool called;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (!called && command.CommandText.Contains("\"LegalEntityContracts\"", StringComparison.Ordinal)) { called = true; await action(ct); }
            return result;
        }
    }

    private static Task<int> ChangeMergeProfileAuthorityAsync(PaymentContext db, MergeSeed seed, string target, CancellationToken ct) => target switch
    {
        "registry" => db.SystemSettings.Where(x => x.Key == "legalEntities.mainCompanyIds").ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, "[]"), ct),
        "assignment" => db.UserCompanyAccesses.Where(x => x.UserId == seed.Actor.Id && x.LegalEntityId == seed.Input.PrimarySourceId).ExecuteDeleteAsync(ct),
        _ => db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.CompanyProfile).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanEdit, false), ct)
    };

    private async Task AssertMergeProfileAuthorityPinnedAsync(bool execute, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var setup = Open();
        var seed = await SeedMergeBoundaryAsync(setup, "legal");
        await RegisterMergePrimaryAsync(setup, seed);
        var called = false;
        var committed = false;
        var blocked = false;
        var hook = new MergeProfileAfterAdmission(async ct =>
        {
            called = true;
            await using var concurrent = Open();
            await using var tx = await concurrent.Database.BeginTransactionAsync(ct);
            await concurrent.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '250ms'", ct);
            try { Assert.Equal(1, await ChangeMergeProfileAuthorityAsync(concurrent, seed, target, ct)); await tx.CommitAsync(ct); committed = true; }
            catch (Exception error)
            {
                Exception? cause = error;
                while (cause is not null && cause is not PostgresException) cause = cause.InnerException;
                if (cause is not PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable }) throw;
                blocked = true;
                Console.WriteLine($"MERGE_PROFILE_EXPECTED_LOCK outer={error.GetType().FullName};sqlState={PostgresErrorCodes.LockNotAvailable};target={target};execute={execute}");
            }
        });
        await using var request = Open(hook);
        Assert.IsType<OkObjectResult>(await RunMergeAsync(request, seed, execute));
        Assert.True(called);
        Assert.True(blocked && !committed, $"execute={execute};target={target};blocked={blocked};committed={committed}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MergeProfileBoundary_PgPinsRegistryDuringBothActions(bool execute) => await AssertMergeProfileAuthorityPinnedAsync(execute, "registry");

    [Theory]
    [InlineData(false, "assignment")]
    [InlineData(true, "assignment")]
    [InlineData(false, "permission")]
    [InlineData(true, "permission")]
    public async Task MergeProfileBoundary_PgPinsExistingRightsDuringBothActions(bool execute, string target) => await AssertMergeProfileAuthorityPinnedAsync(execute, target);

    private async Task AssertMergeProfileBusyAsync(bool execute, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var setup = Open();
        var seed = await SeedMergeBoundaryAsync(setup, "legal");
        await RegisterMergePrimaryAsync(setup, seed);
        var before = await MergeSnapshotAsync(setup);
        await using var writer = Open();
        await using var tx = await writer.Database.BeginTransactionAsync();
        Assert.Equal(1, await ChangeMergeProfileAuthorityAsync(writer, seed, target, default));
        await using var request = Open();
        Assert.IsType<ConflictObjectResult>(await RunMergeAsync(request, seed, execute));
        await tx.RollbackAsync();
        Assert.Equal(before, await MergeSnapshotAsync(setup));
        Assert.Null(request.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MergeProfileBoundary_PgRejectsBusyRegistry(bool execute) => await AssertMergeProfileBusyAsync(execute, "registry");

    [Theory]
    [InlineData(false, "assignment")]
    [InlineData(true, "assignment")]
    [InlineData(false, "permission")]
    [InlineData(true, "permission")]
    public async Task MergeProfileBoundary_PgRejectsBusyExistingRights(bool execute, string target) => await AssertMergeProfileBusyAsync(execute, target);
}
