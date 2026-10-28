using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class TasksControllerTests
{
    [Fact]
    public async Task CompletionRetry_ConfirmsIdenticalExecutorResultWithoutRepeatedEffects_Postgres()
    {
        await using var template = new PostgreSqlModelTemplate();
        await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        var author = await CreateAdminUserAsync(db, "completion-retry-pg-author@example.test", "Автор");
        var executor = await CreateTaskUserAsync(db, "completion-retry-pg-worker@example.test", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var task = await CreateTaskAsync(db, author.Id, client.Id, executor.Id);
        db.SystemSettings.Add(new SystemSetting { Key = "tasks.executorCompletionAllowedUserIds", Value = $"[{executor.Id}]" });
        await db.SaveChangesAsync();
        var notifications = new RecordingNotificationService();
        var controller = CreateController(db, notifications, executor);
        var request = new UpdateTaskJournalStatusRequest { Status = "done", Result = "Результат" };
        Assert.IsType<OkObjectResult>((await controller.UpdateStatus(task.Id, request, CancellationToken.None)).Result);
        var before = await db.TaskJournalItems.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        var history = await db.TaskJournalComments.CountAsync(c => c.TaskJournalItemId == task.Id);
        var payments = await db.Payments.CountAsync();
        db.ChangeTracker.Clear();
        var repeated = await controller.UpdateStatus(task.Id, request, CancellationToken.None);
        Assert.Equal("Результат", Assert.IsType<TaskJournalItemDto>(Assert.IsType<OkObjectResult>(repeated.Result).Value).Result);
        Assert.Equal(before.UpdatedAt, (await db.TaskJournalItems.AsNoTracking().SingleAsync(t => t.Id == task.Id)).UpdatedAt);
        Assert.Equal(history, await db.TaskJournalComments.CountAsync(c => c.TaskJournalItemId == task.Id));
        Assert.Equal(payments, await db.Payments.CountAsync());
        Assert.Single(notifications.CompletedTaskIds);
    }

    [Fact]
    public async Task ApprovalRetry_ConfirmsIdenticalExecutorResultWithoutRepeatedEffects_Postgres()
    {
        await using var template = new PostgreSqlModelTemplate();
        await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        var author = await CreateAdminUserAsync(db, "approval-retry-pg-author@example.test", "Автор");
        var executor = await CreateTaskUserAsync(db, "approval-retry-pg-worker@example.test", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var task = await CreateTaskAsync(db, author.Id, client.Id, executor.Id);
        var notifications = new RecordingNotificationService();
        var controller = CreateController(db, notifications, executor);
        var request = new UpdateTaskJournalStatusRequest { Status = "onApproval", Result = "Результат" };
        Assert.IsType<OkObjectResult>((await controller.UpdateStatus(task.Id, request, CancellationToken.None)).Result);
        var before = await db.TaskJournalItems.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        var history = await db.TaskJournalComments.CountAsync(c => c.TaskJournalItemId == task.Id);
        var participants = await db.TaskJournalParticipants.CountAsync(p => p.TaskJournalItemId == task.Id);
        db.ChangeTracker.Clear();
        var repeated = await controller.UpdateStatus(task.Id, request, CancellationToken.None);
        Assert.Equal("Результат", Assert.IsType<TaskJournalItemDto>(Assert.IsType<OkObjectResult>(repeated.Result).Value).Result);
        Assert.Equal(before.UpdatedAt, (await db.TaskJournalItems.AsNoTracking().SingleAsync(t => t.Id == task.Id)).UpdatedAt);
        Assert.Equal(history, await db.TaskJournalComments.CountAsync(c => c.TaskJournalItemId == task.Id));
        Assert.Equal(participants, await db.TaskJournalParticipants.CountAsync(p => p.TaskJournalItemId == task.Id));
        Assert.Single(notifications.ApprovalRequestedTaskIds);
        Assert.IsType<ForbidResult>((await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest { Status = "onApproval", Result = "Иной" }, CancellationToken.None)).Result);
    }

    private sealed class PausedTaskFileStream(byte[] content) : MemoryStream(content)
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool paused;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!paused) { paused = true; Ready.TrySetResult(); await Release.Task.WaitAsync(ct); }
            return await base.ReadAsync(buffer, ct);
        }
    }

    [Theory]
    [InlineData("user")]
    [InlineData("permission")]
    [InlineData("task")]
    [InlineData("participant")]
    public async Task TaskFileUpload_SerializesRevocationOnPostgres(string target)
    {
        await using var template = new PostgreSqlModelTemplate();
        await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        PostgreSqlPaymentContext Context() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        using var storage = new TaskUploadStorageScope();
        await using var seed = Context();
        var author = await CreateAdminUserAsync(seed, "file-lock-author@example.test", "Автор");
        var actor = await CreateTaskUserAsync(seed, "file-lock-executor@example.test", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(seed, "Синтетический клиент");
        var task = await CreateTaskAsync(seed, author.Id, client.Id, target == "participant" ? author.Id : actor.Id);
        TaskJournalParticipant? participant = null;
        if (target == "participant")
        {
            participant = new TaskJournalParticipant { TaskJournalItemId = task.Id, UserId = actor.Id, Role = "approver", DisplayName = actor.FullName };
            seed.TaskJournalParticipants.Add(participant);
            await seed.SaveChangesAsync();
        }
        var permissionId = actor.Role!.Permissions.Single(x => x.Section == RolePermissionCatalog.Tasks).Id;
        async Task Lock(PaymentContext db)
        {
            if (target == "user") await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {actor.Id} FOR UPDATE NOWAIT");
            else if (target == "permission") await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"Id\" = {permissionId} FOR UPDATE NOWAIT");
            else if (target == "participant") await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"TaskJournalParticipants\" WHERE \"Id\" = {participant!.Id} FOR UPDATE NOWAIT");
            else await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"TaskJournalItems\" WHERE \"Id\" = {task.Id} FOR UPDATE NOWAIT");
        }
        async Task Revoke(PaymentContext db)
        {
            if (target == "user") await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsActive, false));
            else if (target == "permission") await db.RolePermissions.Where(x => x.Id == permissionId).ExecuteUpdateAsync(x => x.SetProperty(p => p.CanView, false));
            else if (target == "participant") await db.TaskJournalParticipants.Where(x => x.Id == participant!.Id).ExecuteDeleteAsync();
            else await db.TaskJournalItems.Where(x => x.Id == task.Id).ExecuteUpdateAsync(x => x.SetProperty(t => t.AssigneeUserId, (int?)null));
        }
        IFormFile Form(Stream stream) => new FormFile(stream, 0, stream.Length, "file", "synthetic.txt") { Headers = new HeaderDictionary(), ContentType = "text/plain" };
        var bytes = System.Text.Encoding.UTF8.GetBytes("synthetic file");
        // Earlier revocation holds its row; the upload must fail closed without touching files.
        await using (var revoker = Context())
        await using (var tx = await revoker.Database.BeginTransactionAsync())
        {
            await Lock(revoker);
            await using var blocked = Context();
            using var file = new MemoryStream(bytes);
            var result = await CreateController(blocked, new RecordingNotificationService(), actor, configuration: storage.Configuration)
                .UploadFile(task.Id, Form(file), null, CancellationToken.None);
            Assert.Equal(423, Assert.IsType<ObjectResult>(result.Result).StatusCode);
            await Revoke(revoker);
            await tx.CommitAsync();
        }
        await using (var afterRevocation = Context())
        {
            using var file = new MemoryStream(bytes);
            var result = await CreateController(afterRevocation, new RecordingNotificationService(), actor, configuration: storage.Configuration)
                .UploadFile(task.Id, Form(file), null, CancellationToken.None);
            Assert.False(result.Result is CreatedResult);
            Assert.Empty(await afterRevocation.TaskJournalFiles.ToListAsync());
            if (target == "user") await afterRevocation.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsActive, true));
            else if (target == "permission") await afterRevocation.RolePermissions.Where(x => x.Id == permissionId).ExecuteUpdateAsync(x => x.SetProperty(p => p.CanView, true));
            else if (target == "participant")
            {
                participant = new TaskJournalParticipant { TaskJournalItemId = task.Id, UserId = actor.Id, Role = "approver", DisplayName = actor.FullName };
                afterRevocation.TaskJournalParticipants.Add(participant);
                await afterRevocation.SaveChangesAsync();
            }
            else await afterRevocation.TaskJournalItems.Where(x => x.Id == task.Id).ExecuteUpdateAsync(x => x.SetProperty(t => t.AssigneeUserId, actor.Id));
        }
        // If upload owns the locks first, revocation cannot overtake it while bytes are read.
        await using var uploadDb = Context();
        await using var revokeDb = Context();
        using var paused = new PausedTaskFileStream(bytes);
        var pending = CreateController(uploadDb, new RecordingNotificationService(), actor, configuration: storage.Configuration)
            .UploadFile(task.Id, Form(paused), null, CancellationToken.None);
        await paused.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            await using var tx = await revokeDb.Database.BeginTransactionAsync();
            var error = await Assert.ThrowsAsync<PostgresException>(() => Lock(revokeDb));
            Assert.Equal("55P03", error.SqlState);
            await tx.RollbackAsync();
        }
        finally { paused.Release.TrySetResult(); }
        Assert.IsType<CreatedResult>((await pending.WaitAsync(TimeSpan.FromSeconds(20))).Result);
        await Revoke(revokeDb);
        await using var denied = Context();
        using var again = new MemoryStream(bytes);
        var rejected = await CreateController(denied, new RecordingNotificationService(), actor, configuration: storage.Configuration)
            .UploadFile(task.Id, Form(again), null, CancellationToken.None);
        Assert.False(rejected.Result is CreatedResult);
        Assert.Equal(1, await denied.TaskJournalFiles.CountAsync(x => x.TaskJournalItemId == task.Id));
        if (target == "participant") Assert.Single(Directory.EnumerateFiles(storage.Root, "*", SearchOption.AllDirectories));
    }
}
