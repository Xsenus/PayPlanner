using System.Net;
using System.Data.Common;
using System.Text.Json;
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
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, false)]
    [InlineData(2, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(1, 1, false)]
    [InlineData(2, 1, false)]
    [InlineData(0, 2, false)]
    [InlineData(1, 2, false)]
    [InlineData(2, 2, false)]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, true)]
    [InlineData(2, 0, true)]
    [InlineData(0, 1, true)]
    [InlineData(1, 1, true)]
    [InlineData(2, 1, true)]
    [InlineData(0, 2, true)]
    [InlineData(1, 2, true)]
    [InlineData(2, 2, true)]
    public async Task CourtDeleteOwnership_HttpPreservesForeignComplaintBeforeAnyWrite(int route, int provider, bool archived)
    {
        await using var database = provider == 2 ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = provider == 2
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync(useObjectAuditModel: provider == 1);
        var (user, ids) = await SeedCaseDeleteAsync(db);
        var other = await CreateUserAsync(db, "court-delete-other@synthetic.local", "Другой сотрудник");
        var otherClient = new Client { Name = "Другой клиент", IsActive = true, ResponsibleUserId = other.Id };
        db.Clients.Add(otherClient);
        await db.SaveChangesAsync();
        await db.ClientCases.Where(row => row.Id == ids[1]).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.ClientId, otherClient.Id).SetProperty(row => row.ResponsibleUserId, other.Id));
        await db.Payments.Where(row => row.ClientCaseId == ids[1]).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientId, otherClient.Id));
        await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Own));
        var instance = new ClientCaseCourtInstance { ClientCaseId = ids[0], Instance = "first", CourtName = "Свой суд" };
        db.ClientCaseCourtInstances.Add(instance);
        await db.SaveChangesAsync();
        var foreign = new ClientCaseCourtComplaint
        { ClientCaseId = ids[1], CourtInstanceId = instance.Id, Title = "Жалоба другого дела", Notes = "private-other-case", IsArchived = archived };
        db.ClientCaseCourtComplaints.AddRange(foreign, new ClientCaseCourtComplaint
        { ClientCaseId = ids[0], CourtInstanceId = instance.Id, Title = "Своя жалоба" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await CourtDeleteOwnershipSnapshotAsync(db);
        using var server = CaseWriteServer(db, user);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await http.DeleteAsync($"/api/{(route == 0 ? "cases" : $"v{route}/cases")}/{ids[0]}");
        var body = await response.Content.ReadAsStringAsync();
        db.ChangeTracker.Clear();
        var retained = await db.ClientCaseCourtComplaints.IgnoreQueryFilters().AnyAsync(row => row.Id == foreign.Id);
        var visible = await db.ClientCaseCourtComplaints.AnyAsync(row => row.Id == foreign.Id);
        var after = await CourtDeleteOwnershipSnapshotAsync(db);
        Assert.True(response.StatusCode == HttpStatusCode.Conflict,
            $"Expected409, actual{response.StatusCode}, foreign raw={retained}, visible={visible}, unchanged={before == after}.");
        Assert.DoesNotContain("private-other-case", body);
        Assert.DoesNotContain("Другой клиент", body);
        Assert.True(retained);
        Assert.True(visible);
        Assert.Equal(before, after);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task CourtDeleteOwnership_AlreadyHiddenForeignHistoryRetainsRawLinks(int route, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtMembershipAsync(db);
        await db.ClientCases.Where(row => row.Id == seed.OtherCase.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => EF.Property<bool>(row, "IsDeleted"), true));
        db.ChangeTracker.Clear();
        var before = await CourtDeleteOwnershipSnapshotAsync(db);
        Assert.False(await db.ClientCaseCourtComplaints.AnyAsync(row => row.ClientCaseId == seed.OtherCase.Id));
        Assert.IsType<ConflictObjectResult>(await DeleteCourtOwnershipCaseAsync(db, seed.Actor, seed.Case.Id, route));
        Assert.Null(db.Database.CurrentTransaction);
        db.ChangeTracker.Clear();
        Assert.Equal(before, await CourtDeleteOwnershipSnapshotAsync(db));
        Assert.Equal(7, await db.ClientCaseCourtComplaints.IgnoreQueryFilters().CountAsync());
    }

    public static IEnumerable<object[]> CourtDeleteOwnershipRaces() => from route in new[] { 0, 1, 2 }
                                                                       from target in new[] { "instance", "complaint-case", "new-complaint", "complaint-instance" }
                                                                       select new object[] { route, target };

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task CourtDeleteOwnership_PgExcludesNewReferencesBeforeInstanceLock(int route, bool reparent)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(interceptors).Options);
        await using var seed = Open();
        var (user, ids) = await SeedCaseDeleteAsync(seed);
        var instance = new ClientCaseCourtInstance { ClientCaseId = ids[0], Instance = "first" };
        var otherInstance = new ClientCaseCourtInstance { ClientCaseId = ids[1], Instance = "first" };
        seed.ClientCaseCourtInstances.AddRange(instance, otherInstance);
        await seed.SaveChangesAsync();
        var complaint = new ClientCaseCourtComplaint { ClientCaseId = ids[1], CourtInstanceId = otherInstance.Id, Title = "Другая жалоба" };
        seed.ClientCaseCourtComplaints.Add(complaint);
        await seed.SaveChangesAsync();
        var attempted = false;
        var committed = false;
        string? blocked = null;
        var hook = new CourtDeleteBeforeInstanceLock(async ct =>
        {
            await using var other = Open();
            await using var transaction = await other.Database.BeginTransactionAsync(ct);
            await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
            attempted = true;
            try
            {
                if (reparent) await other.ClientCaseCourtComplaints.Where(row => row.Id == complaint.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtInstanceId, instance.Id), ct);
                else
                {
                    other.ClientCaseCourtComplaints.Add(new ClientCaseCourtComplaint
                    { ClientCaseId = ids[1], CourtInstanceId = instance.Id, Title = "Новая чужая жалоба" });
                    await other.SaveChangesAsync(ct);
                }
                await transaction.CommitAsync(ct);
                committed = true;
            }
            catch (Exception error)
            {
                Exception? current = error;
                while (current is not null && current is not PostgresException) current = current.InnerException;
                if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } conflict) blocked = conflict.SqlState;
                else throw;
            }
        });
        await using var request = Open(hook);
        var result = await DeleteCourtOwnershipCaseAsync(request, user, ids[0], route);
        Assert.True(hook.Called);
        Assert.True(attempted);
        Assert.False(committed);
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked);
        Assert.IsType<NoContentResult>(result);
        await using var verify = Open();
        Assert.Equal(1, await verify.ClientCaseCourtComplaints.IgnoreQueryFilters().CountAsync());
        var retained = await verify.ClientCaseCourtComplaints.SingleAsync();
        Assert.Equal(ids[1], retained.ClientCaseId);
        Assert.Equal(otherInstance.Id, retained.CourtInstanceId);
    }

    private sealed class CourtDeleteBeforeInstanceLock(Func<CancellationToken, Task> action) : DbCommandInterceptor
    {
        public bool Called { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Called && command.CommandText.Contains("SELECT \"Id\" FROM \"ClientCaseCourtInstances\" WHERE \"ClientCaseId\"", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR UPDATE NOWAIT", StringComparison.Ordinal))
            {
                Called = true;
                await action(cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [MemberData(nameof(CourtDeleteOwnershipRaces))]
    public async Task CourtDeleteOwnership_PgPinsInstanceAndComplaintMembership(int route, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(interceptors).Options);
        await using var seed = Open();
        var (user, ids) = await SeedCaseDeleteAsync(seed);
        var instance = new ClientCaseCourtInstance { ClientCaseId = ids[0], Instance = "first" };
        var otherInstance = new ClientCaseCourtInstance { ClientCaseId = ids[1], Instance = "first" };
        seed.ClientCaseCourtInstances.AddRange(instance, otherInstance);
        await seed.SaveChangesAsync();
        var complaint = new ClientCaseCourtComplaint { ClientCaseId = ids[0], CourtInstanceId = instance.Id, Title = "Своя жалоба" };
        var otherComplaint = new ClientCaseCourtComplaint { ClientCaseId = ids[1], CourtInstanceId = otherInstance.Id, Title = "Другая жалоба" };
        seed.ClientCaseCourtComplaints.AddRange(complaint, otherComplaint);
        await seed.SaveChangesAsync();
        seed.ChangeTracker.Clear();
        var before = await CourtDeleteOwnershipSnapshotAsync(seed);
        var attempted = false;
        var committed = false;
        string? blocked = null;
        var hook = new CaseDeleteBeforePaymentSave(async ct =>
        {
            await using var other = Open();
            await using var transaction = await other.Database.BeginTransactionAsync(ct);
            await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
            attempted = true;
            try
            {
                if (target == "instance") await other.ClientCaseCourtInstances.Where(row => row.Id == instance.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, ids[1]), ct);
                else if (target == "complaint-case") await other.ClientCaseCourtComplaints.Where(row => row.Id == complaint.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, ids[1]), ct);
                else if (target == "complaint-instance") await other.ClientCaseCourtComplaints.Where(row => row.Id == otherComplaint.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtInstanceId, instance.Id), ct);
                else
                {
                    other.ClientCaseCourtComplaints.Add(new ClientCaseCourtComplaint
                    { ClientCaseId = ids[1], CourtInstanceId = instance.Id, Title = "Параллельная чужая жалоба" });
                    await other.SaveChangesAsync(ct);
                }
                await transaction.CommitAsync(ct);
                committed = true;
            }
            catch (Exception error)
            {
                Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException;
                if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } conflict) blocked = conflict.SqlState;
                else throw;
            }
        });
        await using var request = Open(hook);
        var result = await DeleteCourtOwnershipCaseAsync(request, user, ids[0], route);
        Assert.True(hook.Called);
        Assert.True(attempted);
        Assert.False(committed);
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked);
        Assert.Null(request.Database.CurrentTransaction);
        await using var verify = Open();
        if (result is ConflictObjectResult) Assert.Equal(before, await CourtDeleteOwnershipSnapshotAsync(verify));
        else
        {
            Assert.IsType<NoContentResult>(result);
            Assert.False(await verify.ClientCases.AnyAsync(row => row.Id == ids[0]));
            Assert.True(await verify.ClientCases.IgnoreQueryFilters().AnyAsync(row => row.Id == ids[0]));
        }
        Assert.Equal(2, await verify.ClientCaseCourtComplaints.IgnoreQueryFilters().CountAsync());
        Assert.Equal(ids[0], await verify.ClientCaseCourtInstances.IgnoreQueryFilters().Where(row => row.Id == instance.Id).Select(row => row.ClientCaseId).SingleAsync());
        Assert.Equal(ids[0], await verify.ClientCaseCourtComplaints.IgnoreQueryFilters().Where(row => row.Id == complaint.Id).Select(row => row.ClientCaseId).SingleAsync());
        Assert.Equal(otherInstance.Id, await verify.ClientCaseCourtComplaints.IgnoreQueryFilters().Where(row => row.Id == otherComplaint.Id).Select(row => row.CourtInstanceId).SingleAsync());
        Assert.Equal("Сохранённый результат", await verify.TaskJournalItems.IgnoreQueryFilters().Where(row => row.Status == "done").Select(row => row.Result).SingleAsync());
        Assert.Equal(new[] { 15.25m, 27.50m }, await verify.Payments.IgnoreQueryFilters().OrderBy(row => row.Id).Select(row => row.Amount).ToArrayAsync());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task CourtDeleteOwnership_PgBusyCourtRowsStopBeforePaymentDetach(int route, bool complaintRow)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var seed = Open();
        var (user, ids) = await SeedCaseDeleteAsync(seed);
        var instance = new ClientCaseCourtInstance { ClientCaseId = ids[0], Instance = "first" };
        seed.ClientCaseCourtInstances.Add(instance);
        await seed.SaveChangesAsync();
        var complaint = new ClientCaseCourtComplaint { ClientCaseId = ids[0], CourtInstanceId = instance.Id, Title = "Своя жалоба" };
        seed.ClientCaseCourtComplaints.Add(complaint);
        await seed.SaveChangesAsync();
        seed.ChangeTracker.Clear();
        var before = await CourtDeleteOwnershipSnapshotAsync(seed);
        await using var other = Open();
        await using var transaction = await other.Database.BeginTransactionAsync();
        if (complaintRow) await other.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseCourtComplaints\" WHERE \"Id\" = {complaint.Id} FOR UPDATE");
        else await other.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseCourtInstances\" WHERE \"Id\" = {instance.Id} FOR UPDATE");
        await using var request = Open();
        Assert.IsType<ConflictObjectResult>(await DeleteCourtOwnershipCaseAsync(request, user, ids[0], route));
        Assert.Null(request.Database.CurrentTransaction);
        Assert.Equal(before, await CourtDeleteOwnershipSnapshotAsync(seed));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    public async Task CourtDeleteOwnership_ValidLinksKeepExistingDeletePolicy(int route, int provider)
    {
        await using var database = provider == 2 ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = provider == 2
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync(useObjectAuditModel: provider == 1);
        var (user, ids) = await SeedCaseDeleteAsync(db);
        var own = new ClientCaseCourtInstance { ClientCaseId = ids[0], Instance = "first" };
        var other = new ClientCaseCourtInstance { ClientCaseId = ids[1], Instance = "first" };
        db.ClientCaseCourtInstances.AddRange(own, other);
        await db.SaveChangesAsync();
        var ownComplaint = new ClientCaseCourtComplaint { ClientCaseId = ids[0], CourtInstanceId = own.Id, Title = "Своя жалоба" };
        var otherComplaint = new ClientCaseCourtComplaint { ClientCaseId = ids[1], CourtInstanceId = other.Id, Title = "Другая жалоба", Notes = "Сохранить", IsArchived = true };
        db.ClientCaseCourtComplaints.AddRange(ownComplaint, otherComplaint);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.IsType<NoContentResult>(await DeleteCourtOwnershipCaseAsync(db, user, ids[0], route));
        db.ChangeTracker.Clear();
        Assert.Null(db.Database.CurrentTransaction);
        Assert.False(await db.ClientCases.AnyAsync(row => row.Id == ids[0]));
        Assert.Equal(provider != 0, await db.ClientCases.IgnoreQueryFilters().AnyAsync(row => row.Id == ids[0]));
        Assert.Equal(provider != 0, await db.ClientCaseCourtComplaints.IgnoreQueryFilters().AnyAsync(row => row.Id == ownComplaint.Id));
        var retained = await db.ClientCaseCourtComplaints.SingleAsync(row => row.Id == otherComplaint.Id);
        Assert.Equal(ids[1], retained.ClientCaseId);
        Assert.Equal(other.Id, retained.CourtInstanceId);
        Assert.Equal("Сохранить", retained.Notes);
        Assert.True(retained.IsArchived);
        var payments = await db.Payments.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        Assert.Null(payments[0].ClientCaseId);
        Assert.Equal(15.25m, payments[0].Amount);
        Assert.Equal(ids[1], payments[1].ClientCaseId);
        Assert.Equal(27.50m, payments[1].Amount);
        Assert.Equal("Сохранённый результат", await db.TaskJournalItems.IgnoreQueryFilters().Where(row => row.Status == "done").Select(row => row.Result).SingleAsync());
    }

    private static Task<IActionResult> DeleteCourtOwnershipCaseAsync(PaymentContext db, User user, int id, int route) => route == 1
        ? DeleteV1CaseAsync(db, user, id, default)
        : DeleteCaseAsync(db, user, id, route == 0, new BulkPublicationBoundary(db), default);

    private static async Task<string> CourtDeleteOwnershipSnapshotAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Cases = await db.ClientCases.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.ClientId, row.Title, row.Status, row.EventHistory, row.CourtClaimAmount }).ToArrayAsync(),
        VisibleCases = await db.ClientCases.OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(),
        Complaints = await db.ClientCaseCourtComplaints.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.ClientCaseId, row.CourtInstanceId, row.Title, row.Notes, row.Status, row.IsArchived }).ToArrayAsync(),
        Instances = await db.ClientCaseCourtInstances.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.ClientCaseId, row.Instance, row.CourtName }).ToArrayAsync(),
        Tasks = await db.TaskJournalItems.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.ClientCaseId, row.Title, row.Status, row.Result }).ToArrayAsync(),
        Payments = await db.Payments.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.ClientId, row.ClientCaseId, row.Status, row.Amount }).ToArrayAsync()
    });
}
