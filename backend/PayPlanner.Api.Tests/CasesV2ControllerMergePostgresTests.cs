using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static async Task<User> MergeDepartmentOwnerAsync(PaymentContext db, MergeSeed seed)
    {
        var owner = await CreateUserAsync(db, "merge-department@example.local", "Владелец отдела"); var department = new DepartmentDictionaryItem { Name = "Отдел объединения" }; db.Departments.Add(department); await db.SaveChangesAsync(); await db.Users.Where(x => x.Id == seed.Actor.Id || x.Id == owner.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DepartmentId, department.Id).SetProperty(x => x.Department, department.Name)); await MergeOwnerAsync(db, seed.Input.DuplicateSourceType, seed.Input.DuplicateSourceId, owner.Id); await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Department)); return owner;
    }
    public static IEnumerable<object[]> MergePinCases() => from execute in new[] { false, true } from target in new[] { "actor", "role", "permission", "primary", "duplicate", "owner" } select new object[] { execute, target };
    [Theory]
    [MemberData(nameof(MergePinCases))]
    public async Task MergeBoundary_PgPinsActorPermissionsSourcesAndDepartmentForBothActions(bool execute, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options); await using var seed = Open(); var data = await SeedMergeBoundaryAsync(seed, "client"); var owner = target == "owner" ? await MergeDepartmentOwnerAsync(seed, data) : data.Actor; var called = false; var committed = false; string? blocked = null;
        var hook = new MergeAfterAdmission(async ct => { called = true; await using var concurrent = Open(); await using var tx = await concurrent.Database.BeginTransactionAsync(ct); await concurrent.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct); try { var affected = target == "actor" ? await concurrent.Users.Where(x => x.Id == data.Actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false), ct) : target == "role" ? await concurrent.Roles.Where(x => x.Id == data.Actor.RoleId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "admin"), ct) : target == "permission" ? await concurrent.RolePermissions.Where(x => x.RoleId == data.Actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanDelete, false), ct) : target == "owner" ? await concurrent.Users.Where(x => x.Id == owner.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DepartmentId, (int?)null).SetProperty(x => x.Department, "Другой отдел"), ct) : await concurrent.Clients.Where(x => x.Id == (target == "primary" ? data.Input.PrimarySourceId : data.Input.DuplicateSourceId)).ExecuteUpdateAsync(s => s.SetProperty(x => EF.Property<bool>(x, "IsDeleted"), true), ct); Assert.Equal(1, affected); await tx.CommitAsync(ct); committed = true; } catch (Exception error) { Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException; if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } pg) blocked = pg.SqlState; else throw; } });
        await using var request = Open(hook); var result = await RunMergeAsync(request, data, execute); Assert.True(called); Assert.True(!committed && blocked == PostgresErrorCodes.LockNotAvailable, $"execute={execute}; target={target}; committed={committed}; blocked={blocked}; result={result?.GetType().Name}"); Assert.IsType<OkObjectResult>(result);
    }
    [Theory]
    [InlineData("alias-client")]
    [InlineData("linked-client")]
    [InlineData("linked-legal")]
    [InlineData("parent")]
    public async Task MergeBoundary_PgPinsAdditionalVisibleSources(string dependency)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options); await using var seed = Open(); var (data, id, type) = await SeedMergeDependencyAsync(seed, dependency, false); var called = false; var committed = false; string? blocked = null;
        var hook = new MergeBeforeSave(async ct => { called = true; await using var concurrent = Open(); await using var tx = await concurrent.Database.BeginTransactionAsync(ct); await concurrent.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct); try { var affected = type == "client" ? await concurrent.Clients.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "Изменено"), ct) : type == "legal" ? await concurrent.LegalEntities.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ShortName, "Изменено"), ct) : await concurrent.ExternalContacts.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.FullName, "Изменено"), ct); Assert.Equal(1, affected); await tx.CommitAsync(ct); committed = true; } catch (Exception error) { Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException; if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } pg) blocked = pg.SqlState; else throw; } });
        await using var request = Open(hook); var result = await RunMergeAsync(request, data, true); Assert.True(called); Assert.True(!committed && blocked == PostgresErrorCodes.LockNotAvailable, $"dependency={dependency}; committed={committed}; blocked={blocked}; result={result?.GetType().Name}"); Assert.IsType<OkObjectResult>(result);
    }
    [Theory]
    [InlineData("client")]
    [InlineData("legal")]
    [InlineData("external")]
    [InlineData("alias-client")]
    [InlineData("parent")]
    [InlineData("permission")]
    [InlineData("owner")]
    public async Task MergeBoundary_PgBusyRowsRejectBeforeMerge(string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options); await using var seed = Open(); MergeSeed data; int id; string type;
        if (target is "alias-client" or "parent") (data, id, type) = await SeedMergeDependencyAsync(seed, target, false); else { data = await SeedMergeBoundaryAsync(seed, target == "legal" ? "legal" : target == "external" ? "external" : "client"); id = data.Input.PrimarySourceId; type = data.Input.PrimarySourceType; }
        User? owner = target == "owner" ? await MergeDepartmentOwnerAsync(seed, data) : null; var before = await MergeSnapshotAsync(seed); await using var concurrent = Open(); await using var tx = await concurrent.Database.BeginTransactionAsync();
        if (target == "permission") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = {data.Actor.RoleId} FOR UPDATE"); else if (owner is not null) await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {owner.Id} FOR UPDATE"); else if (type == "client") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = {id} FOR UPDATE"); else if (type == "legal") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = {id} FOR UPDATE"); else await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ExternalContacts\" WHERE \"Id\" = {id} FOR UPDATE");
        await using var request = Open(); Assert.IsType<ConflictObjectResult>(await RunMergeAsync(request, data, true)); Assert.Equal(before, await MergeSnapshotAsync(request)); Assert.Null(request.Database.CurrentTransaction);
    }
    [Fact]
    public async Task MergeBoundary_PgMergeFirstBlocksActualContactDeletion()
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options); await using var seed = Open(); var data = await SeedMergeBoundaryAsync(seed); IActionResult? deletion = null;
        var hook = new MergeBeforeSave(async ct => { await using var concurrent = Open(); deletion = await ClientContactController(concurrent, data.Actor).DeleteExternalContact(data.Input.DuplicateSourceId, ct); }); await using var request = Open(hook); var result = await RunMergeAsync(request, data, true); Assert.True(deletion is ConflictObjectResult && result is OkObjectResult, $"deletion={deletion?.GetType().Name}; merge={result?.GetType().Name}");
    }
    [Fact]
    public async Task MergeBoundary_PgDeleteFirstRejectsMergeBeforeAnyWrites()
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options); await using var seed = Open(); var data = await SeedMergeBoundaryAsync(seed); IActionResult? merge = null;
        var hook = new ExternalWriteBeforeSave(async ct => { await using var concurrent = Open(); merge = await RunMergeAsync(concurrent, data, true, ct); }); await using var request = Open(hook); Assert.IsType<NoContentResult>(await ClientContactController(request, data.Actor).DeleteExternalContact(data.Input.DuplicateSourceId, default)); Assert.IsType<ConflictObjectResult>(merge); await using var verify = Open(); var primary = await verify.ExternalContacts.AsNoTracking().SingleAsync(x => x.Id == data.Input.PrimarySourceId); Assert.Equal("79001110001", primary.Phone); Assert.False(await verify.ObjectAuditEvents.AnyAsync(x => x.ObjectType == "contact-merge"));
    }
    [Theory]
    [InlineData("client")]
    [InlineData("legal")]
    public async Task MergeBoundary_PgPrimaryRowsRetainSelectedValues(string kind)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options); await using var seed = Open(); var data = await SeedMergeBoundaryAsync(seed, kind); var primary = data.Input.PrimarySourceId; var duplicate = data.Input.DuplicateSourceId;
        var main = new AddressRecord { ClientId = kind == "client" ? primary : null, LegalEntityId = kind == "legal" ? primary : null, Type = AddressType.Registration, Value = "Выбранный основной адрес", IsPrimary = true }; var old = new AddressRecord { ClientId = kind == "client" ? duplicate : null, LegalEntityId = kind == "legal" ? duplicate : null, Type = AddressType.Registration, Value = "Адрес дубля", IsPrimary = true }; var extra = new AddressRecord { ClientId = kind == "client" ? duplicate : null, LegalEntityId = kind == "legal" ? duplicate : null, Type = AddressType.Actual, Value = "Дополнительный адрес", IsPrimary = kind == "legal" }; seed.AddressRecords.AddRange(main, old, extra);
        ClientPassport? selectedPassport = null; ClientPassport? duplicatePassport = null; if (kind == "client") { selectedPassport = new ClientPassport { ClientId = primary, Series = "TEST", Number = "1111", IsPrimary = true }; duplicatePassport = new ClientPassport { ClientId = duplicate, Series = "TEST", Number = "2222", IsPrimary = true }; seed.ClientPassports.AddRange(selectedPassport, duplicatePassport); }
        await seed.SaveChangesAsync();
        var fault = new MergeCommandFailure(); await using var request = Open(fault); var result = await RunMergeAsync(request, data, true); Assert.True(result is OkObjectResult, $"kind={kind}; state={fault.SqlState}; error={fault.Message}; result={result?.GetType().Name}"); await using var verify = Open(); var addresses = await verify.AddressRecords.AsNoTracking().Where(x => kind == "client" ? x.ClientId == primary : x.LegalEntityId == primary).OrderBy(x => x.Id).ToArrayAsync(); Assert.Equal(3, addresses.Length); Assert.True(addresses.Single(x => x.Id == main.Id).IsPrimary); Assert.False(addresses.Single(x => x.Id == old.Id).IsPrimary); Assert.Equal(kind == "legal", addresses.Single(x => x.Id == extra.Id).IsPrimary); Assert.Equal(new[] { "Выбранный основной адрес", "Адрес дубля", "Дополнительный адрес" }, addresses.Select(x => x.Value));
        if (kind == "client") { var passports = await verify.ClientPassports.AsNoTracking().Where(x => x.ClientId == primary).OrderBy(x => x.Id).ToArrayAsync(); Assert.Equal(2, passports.Length); Assert.True(passports.Single(x => x.Id == selectedPassport!.Id).IsPrimary); Assert.False(passports.Single(x => x.Id == duplicatePassport!.Id).IsPrimary); Assert.Equal(new[] { "1111", "2222" }, passports.Select(x => x.Number)); }
        Assert.Null(fault.SqlState); Assert.Empty(await verify.Payments.ToArrayAsync());
    }
    private sealed class MergeCommandFailure : DbCommandInterceptor
    {
        public string? SqlState { get; private set; }
        public string? Message { get; private set; }
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData data, CancellationToken ct = default) { Exception? current = data.Exception; while (current is not null && current is not PostgresException) current = current.InnerException; if (current is PostgresException pg) { SqlState = pg.SqlState; Message = pg.MessageText; } return Task.CompletedTask; }
    }
    private sealed class MergeAfterAdmission(Func<CancellationToken, Task> action) : DbCommandInterceptor
    {
        private bool called;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default) { if (!called && command.CommandText.Contains("\"ClientContracts\"", StringComparison.Ordinal)) { called = true; await action(ct); } return result; }
    }
    private sealed class MergeBeforeSave(Func<CancellationToken, Task> action) : SaveChangesInterceptor
    {
        private bool called;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default) { if (!called && data.Context!.ChangeTracker.Entries().Any(x => x.Entity is Client or LegalEntity or ExternalContact && x.State is EntityState.Added or EntityState.Modified)) { called = true; await action(ct); } return result; }
    }
}
