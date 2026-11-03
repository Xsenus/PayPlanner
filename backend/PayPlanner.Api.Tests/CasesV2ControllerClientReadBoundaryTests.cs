using System.Data.Common;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Controllers.V1;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static readonly int[] ClientReadRoutes = Enumerable.Range(0, 9).ToArray();
    private static readonly int[] RepresentativeClientReadRoutes = [1, 6, 7];

    private static string ClientReadPath(int route, int id) => route switch
    {
        0 => "/api/clients",
        1 => $"/api/clients/{id}",
        2 => $"/api/clients/{id}/stats",
        3 => $"/api/clients/{id}/case-finance-summaries",
        4 => $"/api/clients/{id}/case-expense-details",
        5 => "/api/v1/clients",
        6 => $"/api/v1/clients/{id}",
        7 => "/api/v2/clients",
        _ => $"/api/v2/clients/{id}",
    };

    private static Task<IActionResult> ReadClientBoundaryAsync(PaymentContext db, User actor, int id, int route, string? principal = null, CancellationToken ct = default)
    {
        var root = RequiredClientController(db, actor);
        root.Request.Headers["X-Forwarded-For"] = Guid.NewGuid().ToString("N");
        if (principal is not null)
        {
            var claims = principal == "idless" ? Array.Empty<Claim>() : new[] { new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()) };
            root.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims.Append(new Claim(ClaimTypes.Role, "admin")), principal == "anonymous" ? null : "test"));
        }
        var v1 = new ClientsV1Controller(db) { ControllerContext = root.ControllerContext };
        var v2 = new ClientsV2Controller(db) { ControllerContext = root.ControllerContext };
        return route switch
        {
            0 => root.GetAll(ct),
            1 => root.GetById(id, ct),
            2 => root.GetStats(id, null, ct),
            3 => root.GetCaseFinanceSummaries(id, ct),
            4 => root.GetCaseExpenseDetails(id, null, ct: ct),
            5 => v1.Get(null, null, null, null, null, ct),
            6 => v1.GetById(id, ct),
            7 => v2.Get(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, ct: ct),
            _ => v2.GetById(id, ct),
        };
    }

    private static async Task<(User Actor, User Other, int[] Ids)> SeedClientReadAsync(PaymentContext db, string scope = DataVisibilityScope.All)
    {
        var actor = await CreateUserAsync(db, "read-actor@example.test", "Сотрудник", scope);
        var sibling = await CreateUserAsync(db, "read-sibling@example.test", "Коллега");
        var other = await CreateUserAsync(db, "read-other@example.test", "Другой отдел");
        var department = new DepartmentDictionaryItem { Name = "Синтетический отдел чтения" };
        db.Departments.Add(department); await db.SaveChangesAsync();
        actor.DepartmentId = sibling.DepartmentId = department.Id; actor.Department = sibling.Department = department.Name;
        foreach (var section in new[] { RolePermissionCatalog.Clients, RolePermissionCatalog.Payments, RolePermissionCatalog.Cases, RolePermissionCatalog.Accounts, RolePermissionCatalog.Acts, RolePermissionCatalog.Contracts })
            await GrantSectionPermissionAsync(db, actor, section, viewScope: scope);
        var rows = new[] { actor, sibling, other }.Select((owner, i) => new Client { Name = $"Клиент {i}", Notes = $"private-read-marker-{i}", ResponsibleUserId = owner.Id }).ToArray();
        db.Clients.AddRange(rows); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        return (actor, other, rows.Select(x => x.Id).ToArray());
    }

    public static IEnumerable<object[]> ClientReadActorCases() =>
        (from route in ClientReadRoutes from state in new[] { "unapproved", "inactive", "missing", "denied" } select new object[] { route, state, false })
        .Concat(from route in RepresentativeClientReadRoutes select new object[] { route, "unapproved", true });

    [Theory]
    [MemberData(nameof(ClientReadActorCases))]
    public async Task ClientReadBoundary_RequiresCurrentApprovedActorAndClientView(int route, string state, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, _, ids) = await SeedClientReadAsync(db);
        if (state == "missing") actor.Id = int.MaxValue;
        using var server = RequiredClientServer(db, actor, async () =>
        {
            if (state == "unapproved") await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false));
            if (state == "inactive") await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
            if (state == "denied") await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false));
        });
        using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); http.DefaultRequestHeaders.Add("X-Forwarded-For", Guid.NewGuid().ToString("N"));
        using var response = await http.GetAsync(ClientReadPath(route, ids[0]));
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"route={route};state={state};postgres={postgres};HTTP={(int)response.StatusCode};body={await response.Content.ReadAsStringAsync()}");
        Assert.DoesNotContain("private-read-marker", await response.Content.ReadAsStringAsync()); Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges()); Assert.Equal(3, await db.Clients.CountAsync());
    }

    public static IEnumerable<object[]> ClientReadEmptyCases() => from route in ClientReadRoutes select new object[] { route };

    [Theory]
    [MemberData(nameof(ClientReadEmptyCases))]
    public async Task ClientReadBoundary_DoesNotUseBootstrapAdminFallback(int route)
    {
        await using var db = await CreateContextAsync(); var client = new Client { Name = "Синтетический закрытый клиент", Notes = "private-read-marker" }; db.Clients.Add(client); await db.SaveChangesAsync();
        var id = client.Id; db.ChangeTracker.Clear(); var actor = new User { Id = 123 };
        Assert.IsType<UnauthorizedResult>(await ReadClientBoundaryAsync(db, actor, id, route, "stale-admin")); Assert.Equal(0, await db.Users.CountAsync()); Assert.Null(db.Database.CurrentTransaction);
    }

    public static IEnumerable<object[]> ClientReadPrincipalCases() => from route in RepresentativeClientReadRoutes from state in new[] { "anonymous", "idless" } select new object[] { route, state };

    [Theory]
    [MemberData(nameof(ClientReadPrincipalCases))]
    public async Task ClientReadBoundary_RejectsUnauthenticatedOrIdlessPrincipal(int route, string state)
    {
        await using var db = await CreateContextAsync(); var (actor, _, ids) = await SeedClientReadAsync(db);
        Assert.IsType<UnauthorizedResult>(await ReadClientBoundaryAsync(db, actor, ids[0], route, state)); Assert.Null(db.Database.CurrentTransaction);
    }

    public static IEnumerable<object[]> ClientReadScopeCases() =>
        (from route in ClientReadRoutes from scope in new[] { DataVisibilityScope.All, DataVisibilityScope.Own, DataVisibilityScope.Department } select new object[] { route, scope, false })
        .Concat(from route in RepresentativeClientReadRoutes from scope in new[] { DataVisibilityScope.All, DataVisibilityScope.Own, DataVisibilityScope.Department } select new object[] { route, scope, true });

    [Theory]
    [MemberData(nameof(ClientReadScopeCases))]
    public async Task ClientReadBoundary_PreservesAllOwnAndDepartmentVisibility(int route, string scope, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, _, ids) = await SeedClientReadAsync(db, scope); var result = await ReadClientBoundaryAsync(db, actor, ids[2], route);
        if (route is 0 or 5 or 7)
        {
            var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
            var items = route == 7 ? body.GetProperty("items") : body;
            var actual = items.EnumerateArray().Select(x => x.GetProperty("Id").GetInt32()).Order().ToArray();
            Assert.Equal(scope == DataVisibilityScope.All ? ids : scope == DataVisibilityScope.Department ? ids.Take(2).ToArray() : ids.Take(1).ToArray(), actual);
        }
        else if (scope == DataVisibilityScope.All) Assert.IsType<OkObjectResult>(result);
        else Assert.IsType<NotFoundResult>(result);
        Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges()); Assert.Equal(3, await db.Clients.CountAsync());
    }

    public static IEnumerable<object[]> ClientReadForeignWorkCases() => from route in RepresentativeClientReadRoutes from pending in new[] { false, true } select new object[] { route, pending };

    [Theory]
    [MemberData(nameof(ClientReadForeignWorkCases))]
    public async Task ClientReadBoundary_PreservesForeignTransactionAndPendingWork(int route, bool pending)
    {
        await using var db = await CreateContextAsync(); var (actor, _, ids) = await SeedClientReadAsync(db);
        await using var tx = pending ? null : await db.Database.BeginTransactionAsync(); var row = await db.Clients.SingleAsync(x => x.Id == ids[0]); if (pending) row.Name = "Несохранённая чужая работа";
        Assert.IsType<ConflictObjectResult>(await ReadClientBoundaryAsync(db, actor, ids[0], route));
        if (pending) { Assert.True(db.ChangeTracker.HasChanges()); Assert.Equal("Несохранённая чужая работа", row.Name); Assert.Equal("Клиент 0", await db.Clients.AsNoTracking().Where(x => x.Id == ids[0]).Select(x => x.Name).SingleAsync()); }
        else Assert.Same(tx, db.Database.CurrentTransaction);
    }

    private sealed class ClientReadBeforeRows(Func<CancellationToken, Task> action) : DbCommandInterceptor
    {
        public bool Called { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { if (!Called && command.CommandText.Contains("FROM \"Clients\"", StringComparison.Ordinal)) { Called = true; await action(ct); } return result; }
    }

    public static IEnumerable<object[]> ClientReadPinCases() => from route in RepresentativeClientReadRoutes from target in new[] { "approval", "role", "permission" } select new object[] { route, target };

    [Theory]
    [MemberData(nameof(ClientReadPinCases))]
    public async Task ClientReadBoundary_PgPinsApprovalRoleAndPermissionsUntilReadFinishes(int route, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, _, ids) = await SeedClientReadAsync(seed); var blocked = false; var committed = false;
        var hook = new ClientReadBeforeRows(async ct =>
        {
            await using var writer = Open(); await using var tx = await writer.Database.BeginTransactionAsync(ct); await writer.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '250ms'", ct);
            try
            {
                if (target == "approval") await writer.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false), ct);
                if (target == "role") await writer.Roles.Where(x => x.Id == actor.RoleId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "Отозванная роль"), ct);
                if (target == "permission") await writer.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false), ct);
                await tx.CommitAsync(ct); committed = true;
            }
            catch (Exception error) { Exception? cause = error; while (cause is not null && cause is not PostgresException) cause = cause.InnerException; if (cause is not PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable }) throw; blocked = true; }
        });
        await using var request = Open(hook); var result = await ReadClientBoundaryAsync(request, actor, ids[0], route);
        Assert.True(hook.Called && blocked && !committed, $"route={route};target={target};called={hook.Called};blocked={blocked};committed={committed};result={result.GetType().Name}"); Assert.IsType<OkObjectResult>(result); Assert.Null(request.Database.CurrentTransaction);
        await using var after = Open(); Assert.Equal(1, await after.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false))); Assert.IsType<UnauthorizedResult>(await ReadClientBoundaryAsync(after, actor, ids[0], route));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task ClientReadBoundary_PgBusyActorReturnsBoundedConflictAndReleasesTransaction(int route)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var seed = Open(); var (actor, _, ids) = await SeedClientReadAsync(seed);
        await using var writer = Open(); await using var tx = await writer.Database.BeginTransactionAsync(); await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {actor.Id} FOR UPDATE");
        await using var request = Open(); Assert.IsType<ConflictObjectResult>(await ReadClientBoundaryAsync(request, actor, ids[0], route)); Assert.Null(request.Database.CurrentTransaction);
        await tx.RollbackAsync(); Assert.IsType<OkObjectResult>(await ReadClientBoundaryAsync(request, actor, ids[0], route));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(7, false)]
    [InlineData(7, true)]
    public async Task ClientReadBoundary_PgUsesOneSnapshotWithoutBlockingUnrelatedClientWriters(int route, bool transfer)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, other, ids) = await SeedClientReadAsync(seed, DataVisibilityScope.Own); var insertedId = 0;
        var hook = new ClientReadBeforeRows(async ct =>
        {
            await using var writer = Open(); await using var tx = await writer.Database.BeginTransactionAsync(ct); await writer.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '250ms'", ct);
            if (transfer) Assert.Equal(1, await writer.Clients.Where(x => x.Id == ids[0]).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id), ct));
            else { var added = new Client { Name = "Поздний клиент", ResponsibleUserId = actor.Id }; writer.Clients.Add(added); await writer.SaveChangesAsync(ct); insertedId = added.Id; }
            await tx.CommitAsync(ct);
        });
        await using var request = Open(hook); var result = Assert.IsType<OkObjectResult>(await ReadClientBoundaryAsync(request, actor, ids[0], route)); Assert.True(hook.Called);
        var body = JsonSerializer.SerializeToElement(result.Value); var items = route == 7 ? body.GetProperty("items") : body; Assert.Equal(new[] { ids[0] }, items.EnumerateArray().Select(x => x.GetProperty("Id").GetInt32()).ToArray());
        await using var fresh = Open(); var next = Assert.IsType<OkObjectResult>(await ReadClientBoundaryAsync(fresh, actor, ids[0], route)); body = JsonSerializer.SerializeToElement(next.Value); items = route == 7 ? body.GetProperty("items") : body;
        Assert.Equal(transfer ? Array.Empty<int>() : new[] { ids[0], insertedId }, items.EnumerateArray().Select(x => x.GetProperty("Id").GetInt32()).Order().ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task ClientReadBoundary_CancellationReleasesOwnedSnapshotWithoutChanges(int route)
    {
        using var cancellation = new CancellationTokenSource(); var hook = new ClientReadBeforeRows(_ => { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await using var db = await CreateContextAsync(hook); var (actor, _, ids) = await SeedClientReadAsync(db);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadClientBoundaryAsync(db, actor, ids[0], route, ct: cancellation.Token)); Assert.True(hook.Called); Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges()); Assert.Equal(3, await db.Clients.CountAsync());
    }
}
