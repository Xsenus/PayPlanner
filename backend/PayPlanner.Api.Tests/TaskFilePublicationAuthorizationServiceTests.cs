using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class TaskFilePublicationAuthorizationServiceTests
{
    [Theory]
    [InlineData("executor", 2)]
    [InlineData("admin", 2)]
    [InlineData("allEditor", 2)]
    [InlineData("departmentEditor", 3)]
    public async Task PublicationAccessReadsCurrentActorAndPermissionTogether(string mode, int expectedQueries)
    {
        var commands = new CountingCommandInterceptor();
        await using var f = await Fixture.CreateAsync(mode, interceptor: commands);
        commands.Readers = 0;
        Assert.True(await f.Service.CanPublishAsync(f.ActorId, f.TaskId, CancellationToken.None));
        Assert.Equal(expectedQueries, commands.Readers);
    }

    [Theory]
    [InlineData("author", true)]
    [InlineData("executor", true)]
    [InlineData("assignee", true)]
    [InlineData("approver", true)]
    [InlineData("observer", false)]
    [InlineData("OBSERVER", false)]
    [InlineData("outsider", false)]
    [InlineData("allEditor", true)]
    [InlineData("allViewer", false)]
    [InlineData("departmentEditor", true)]
    [InlineData("otherDepartmentEditor", false)]
    [InlineData("legacyRole", false)]
    [InlineData("deniedView", false)]
    [InlineData("inactive", false)]
    [InlineData("unapproved", false)]
    [InlineData("admin", true)]
    public async Task LivePublicationUsesTheSameWorkingRolesAndVisibilityAsTheCard(string mode, bool expected)
    {
        await using var f = await Fixture.CreateAsync(mode);
        Assert.Equal(expected, await f.Service.CanPublishAsync(f.ActorId, f.TaskId, CancellationToken.None));
        Assert.False(await f.Service.CanPublishAsync(f.ActorId, int.MaxValue, CancellationToken.None));
        Assert.False(await f.Service.CanPublishAsync(int.MaxValue, f.TaskId, CancellationToken.None));
        Assert.False(await f.Service.CanPublishAsync(0, f.TaskId, CancellationToken.None));
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("unapproved")]
    [InlineData("permission")]
    [InlineData("reassigned")]
    [InlineData("taskDeleted")]
    [InlineData("roleChanged")]
    public async Task RecheckingDoesNotReuseTrackedUsersRolesPermissionsOrTaskAssignments(string mode)
    {
        await using var f = await Fixture.CreateAsync(mode == "roleChanged" ? "admin" : "executor");
        Assert.True(await f.Service.CanPublishAsync(f.ActorId, f.TaskId, CancellationToken.None));
        if (mode == "inactive") await f.Db.Users.Where(u => u.Id == f.ActorId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        if (mode == "unapproved") await f.Db.Users.Where(u => u.Id == f.ActorId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsApproved, false));
        if (mode == "permission") await f.Db.RolePermissions.Where(p => p.RoleId == f.RoleId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CanView, false));
        if (mode == "reassigned") await f.Db.TaskJournalItems.Where(t => t.Id == f.TaskId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.AssigneeUserId, f.OwnerId));
        if (mode == "taskDeleted") await f.Db.TaskJournalItems.Where(t => t.Id == f.TaskId).ExecuteDeleteAsync();
        if (mode == "roleChanged") await f.Db.Roles.Where(r => r.Id == f.RoleId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Name, "employee"));
        Assert.False(await f.Service.CanPublishAsync(f.ActorId, f.TaskId, CancellationToken.None));
    }

    [Fact]
    public async Task RemovingWorkingParticipantDoesNotAllowPublicationThroughTheRemainingObserverRole()
    {
        await using var f = await Fixture.CreateAsync("approver");
        f.Db.TaskJournalParticipants.Add(new() { TaskJournalItemId = f.TaskId, UserId = f.ActorId, Role = "observer", DisplayName = "Наблюдатель" });
        await f.Db.SaveChangesAsync();
        Assert.True(await f.Service.CanPublishAsync(f.ActorId, f.TaskId, CancellationToken.None));
        await f.Db.TaskJournalParticipants.Where(p => p.TaskJournalItemId == f.TaskId && p.UserId == f.ActorId && p.Role == "approver")
            .ExecuteDeleteAsync();
        Assert.False(await f.Service.CanPublishAsync(f.ActorId, f.TaskId, CancellationToken.None));
    }

    [Fact]
    public async Task SoftDeletedTaskIsNotAuthorizedEvenThoughItsRecoverableRecordRemains()
    {
        await using var f = await Fixture.CreateAsync("executor", useObjectAuditModel: true);
        Assert.True(await f.Service.CanPublishAsync(f.ActorId, f.TaskId, CancellationToken.None));
        f.Db.TaskJournalItems.Remove(await f.Db.TaskJournalItems.SingleAsync(t => t.Id == f.TaskId));
        await f.Db.SaveChangesAsync();
        Assert.True(await f.Db.TaskJournalItems.IgnoreQueryFilters().AnyAsync(t => t.Id == f.TaskId));
        Assert.False(await f.Service.CanPublishAsync(f.ActorId, f.TaskId, CancellationToken.None));
    }

    private sealed class Fixture(SqliteConnection connection, PaymentContext db, int actorId, int ownerId, int roleId, int taskId) : IAsyncDisposable
    {
        public PaymentContext Db => db;
        public int ActorId => actorId;
        public int OwnerId => ownerId;
        public int RoleId => roleId;
        public int TaskId => taskId;
        public TaskFilePublicationAuthorizationService Service { get; } = new(db);

        public static async Task<Fixture> CreateAsync(string mode, bool useObjectAuditModel = false,
            DbCommandInterceptor? interceptor = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            var db = new PaymentContext(options.Options, useObjectAuditModel: useObjectAuditModel);
            await db.Database.EnsureCreatedAsync();
            var role = new Role { Name = mode == "admin" ? "admin" : "employee" };
            var permission = new RolePermission
            {
                Section = RolePermissionCatalog.Tasks,
                CanView = mode != "deniedView",
                CanEdit = mode is "allEditor" or "departmentEditor" or "otherDepartmentEditor",
                ViewScope = mode is "allEditor" or "allViewer" ? DataVisibilityScope.All
                    : mode is "departmentEditor" or "otherDepartmentEditor" ? DataVisibilityScope.Department : DataVisibilityScope.Own
            };
            if (mode != "legacyRole") role.Permissions.Add(permission);
            var actor = new User
            {
                FullName = "Сотрудник",
                Email = "actor@example.test",
                PasswordHash = "hash",
                Role = role,
                IsActive = mode != "inactive",
                IsApproved = mode != "unapproved",
                Department = mode == "otherDepartmentEditor" ? "Бухгалтерия" : "Юристы"
            };
            var owner = new User
            {
                FullName = "Постановщик",
                Email = "owner@example.test",
                PasswordHash = "hash",
                Role = new Role { Name = "owner" },
                IsActive = true,
                IsApproved = true,
                Department = "Юристы"
            };
            db.AddRange(actor, owner);
            await db.SaveChangesAsync();
            var task = new TaskJournalItem
            {
                Title = "Проверка публикации",
                AuthorUserId = mode == "author" ? actor.Id : owner.Id,
                AssigneeUserId = mode is "executor" or "inactive" or "unapproved" or "deniedView" or "legacyRole" ? actor.Id : owner.Id,
                DueAt = DateTime.UtcNow.AddDays(1)
            };
            if (mode is "assignee" or "approver" or "observer" or "OBSERVER")
                task.Participants.Add(new() { UserId = actor.Id, Role = mode, DisplayName = actor.FullName });
            db.TaskJournalItems.Add(task);
            await db.SaveChangesAsync();
            return new(connection, db, actor.Id, owner.Id, role.Id, task.Id);
        }
        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        public int Readers { get; set; }

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Readers++;
            return ValueTask.FromResult(result);
        }
    }
}
