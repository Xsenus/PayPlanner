using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;
public sealed partial class ManagedTaskFileUploadServiceTests
{
    [Theory]
    [InlineData("user")]
    [InlineData("role")]
    [InlineData("permission")]
    [InlineData("request")]
    [InlineData("department")]
    [InlineData("participant")]
    public async Task ManagedUpload_HoldsActorLockBetweenAuthorityAndPublication(string target)
    {
        await using var template = new PostgreSqlModelTemplate();
        await template.InitializeAsync();
        await using var lease = await template.CreateDatabaseAsync();
        PostgreSqlPaymentContext Context() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options);
        await using var db = Context();
        var actor = new User
        {
            FullName = "Синтетический",
            Email = "managed-revoke@example.test",
            PasswordHash = "test",
            IsActive = true,
            IsApproved = true,
            Role = new Role { Name = "synthetic-worker", Permissions = [new RolePermission { Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.Own }] }
        };
        if (target is "request" or "department")
            actor.Role!.Permissions.Add(new RolePermission { Section = RolePermissionCatalog.Requests, CanView = true, ViewScope = target == "department" ? DataVisibilityScope.Department : DataVisibilityScope.Own });
        if (target == "department") actor.Department = "Отдел А";
        var client = new Client { Name = "Синтетический" };
        var deniedRole = new Role { Name = "synthetic-no-access" };
        var other = new User { FullName = "Другой", Email = "managed-other@example.test", PasswordHash = "test", IsActive = true, IsApproved = true, Role = deniedRole };
        var outside = new User { FullName = "Вне отдела", Email = "managed-outside@example.test", PasswordHash = "test", IsActive = true, IsApproved = true, Role = deniedRole, Department = "Отдел Б" };
        if (target == "department") other.Department = "Отдел А";
        db.AddRange(actor, client, other, outside); await db.SaveChangesAsync();
        var caseItem = new ClientCase { ClientId = client.Id, Title = "Синтетическое дело" };
        db.ClientCases.Add(caseItem); await db.SaveChangesAsync();
        SalesRequest? salesRequest = null;
        if (target is "request" or "department")
        {
            var status = new SalesRequestStatus { Key = "managed-upload", Title = "Тест" };
            db.SalesRequestStatuses.Add(status); await db.SaveChangesAsync();
            salesRequest = new SalesRequest { Summary = "Синтетическая заявка", StatusId = status.Id, ResponsibleUserId = target == "department" ? other.Id : actor.Id, CreatedByUserId = target == "department" ? outside.Id : other.Id };
            db.SalesRequests.Add(salesRequest); await db.SaveChangesAsync();
        }
        var task = new TaskJournalItem { Title = "Синтетическая задача", ClientCaseId = caseItem.Id, SalesRequestId = salesRequest?.Id, AuthorUserId = target == "participant" ? other.Id : actor.Id, AssigneeUserId = target == "participant" ? other.Id : actor.Id, DueAt = DateTime.UtcNow.AddDays(1) };
        db.TaskJournalItems.Add(task); await db.SaveChangesAsync();
        TaskJournalParticipant? participant = null;
        if (target == "participant")
        {
            participant = new TaskJournalParticipant { TaskJournalItemId = task.Id, UserId = actor.Id, Role = "approver", DisplayName = actor.FullName };
            db.TaskJournalParticipants.Add(participant); await db.SaveChangesAsync();
        }
        var permissionId = actor.Role!.Permissions.Single(p => p.Section == RolePermissionCatalog.Tasks).Id;
        async Task Lock(PaymentContext context)
        {
            if (target == "user") await context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {actor.Id} FOR UPDATE NOWAIT");
            else if (target == "role") await context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = {actor.RoleId} FOR UPDATE NOWAIT");
            else if (target == "request") await context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"SalesRequests\" WHERE \"Id\" = {salesRequest!.Id} FOR UPDATE NOWAIT");
            else if (target == "department") await context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {other.Id} FOR UPDATE NOWAIT");
            else if (target == "participant") await context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"TaskJournalParticipants\" WHERE \"Id\" = {participant!.Id} FOR UPDATE NOWAIT");
            else await context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"Id\" = {permissionId} FOR UPDATE NOWAIT");
        }
        var root = Path.Combine(Path.GetTempPath(), "pp-managed-revocation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var environment = new TestEnvironment(root);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:RootPath"] = Path.Combine(root, "files"), ["StorageOrchestration:LocalRoot"] = Path.Combine(root, "objects") }).Build();
            var legacy = new LegacyLocalStorageProvider(environment, config);
            var registry = new StorageProviderRegistry([new LocalStorageProvider(environment, config), legacy, new ManagedCompatibilityStorageProvider(db, legacy)]);
            var monitor = new StaticOptions(new() { Enabled = true, ProductWriteEnabledClasses = ["task-file", "case-document"], ProductReadEnabledClasses = ["task-file", "case-document"], DefaultPolicy = new() { RequiredCopies = 1, WriteProviders = ["local"], ReadProviders = ["local"] } });
            var policy = new StoragePolicyService(monitor, registry);
            var service = new ManagedTaskFileUploadService(db, new StorageObjectService(db, policy, registry, monitor), new ProductStorageBindingService(db, policy), environment, config, monitor);
            var authority = new TaskFilePublicationAuthorizationService(db);
            var checkedAccess = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> Authorize(CancellationToken ct) { var allowed = await authority.CanPublishAsync(actor.Id, task.Id, ct); checkedAccess.TrySetResult(); await release.Task.WaitAsync(ct); return allowed; }
            using var bytes = new MemoryStream([1, 2, 3, 4]);
            var form = new FormFile(bytes, 0, 4, "file", "synthetic.txt") { Headers = new HeaderDictionary(), ContentType = "text/plain" };
            var pending = service.UploadAsync(new(task.Id, actor.Id, actor.FullName, Guid.NewGuid(), form, "synthetic.txt", null, Authorize), CancellationToken.None);
            await checkedAccess.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Exception? lockError;
            try
            {
                await using var revoked = Context();
                await using var transaction = await revoked.Database.BeginTransactionAsync();
                lockError = await Record.ExceptionAsync(() => Lock(revoked));
                await transaction.RollbackAsync();
            }
            finally { release.TrySetResult(); }
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("55P03", Assert.IsType<PostgresException>(lockError).SqlState);
            Assert.Equal("uploaded", result.Status);
            Assert.Single(await db.TaskJournalFiles.AsNoTracking().ToListAsync());
            var retryKey = Guid.NewGuid();
            async Task<ManagedTaskFileUploadResult> Retry()
            {
                using var content = new MemoryStream([1, 2, 3, 4]);
                var next = new FormFile(content, 0, 4, "file", "retry.txt") { Headers = new HeaderDictionary(), ContentType = "text/plain" };
                return await service.UploadAsync(new(task.Id, actor.Id, actor.FullName, retryKey, next, "retry.txt", null,
                    ct => authority.CanPublishAsync(actor.Id, task.Id, ct)), CancellationToken.None);
            }
            await using (var blocker = Context())
            await using (var transaction = await blocker.Database.BeginTransactionAsync())
            {
                await Lock(blocker);
                Assert.Equal("pending", (await Retry()).Status);
                await transaction.RollbackAsync();
            }
            Assert.Equal("uploaded", (await Retry()).Status);
            Assert.Equal(2, await db.TaskJournalFiles.AsNoTracking().CountAsync());
            if (target is "user" or "permission" or "role" or "request" or "department" or "participant")
            {
                var bindingCount = await db.StorageProductBindings.AsNoTracking().CountAsync();
                await using (var revoker = Context())
                await using (var transaction = await revoker.Database.BeginTransactionAsync())
                {
                    await Lock(revoker);
                    if (target == "user")
                        await revoker.Users.Where(u => u.Id == actor.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsActive, false));
                    else if (target == "permission")
                        await revoker.RolePermissions.Where(p => p.Id == permissionId).ExecuteUpdateAsync(x => x.SetProperty(p => p.CanView, false));
                    else if (target == "role")
                        await revoker.Users.Where(u => u.Id == actor.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.RoleId, deniedRole.Id));
                    else if (target == "request")
                        await revoker.SalesRequests.Where(r => r.Id == salesRequest!.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.ResponsibleUserId, other.Id));
                    else if (target == "department")
                        await revoker.Users.Where(u => u.Id == other.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Department, "Отдел Б"));
                    else
                        await revoker.TaskJournalParticipants.Where(p => p.Id == participant!.Id).ExecuteDeleteAsync();
                    await transaction.CommitAsync();
                }
                using var content = new MemoryStream([1, 2, 3, 4]);
                var deniedFile = new FormFile(content, 0, 4, "file", "denied.txt") { Headers = new HeaderDictionary(), ContentType = "text/plain" };
                var denied = await service.UploadAsync(new(task.Id, actor.Id, actor.FullName, Guid.NewGuid(), deniedFile, "denied.txt", null,
                    ct => authority.CanPublishAsync(actor.Id, task.Id, ct)), CancellationToken.None);
                Assert.Equal("forbidden", denied.Status);
                Assert.Equal(2, await db.TaskJournalFiles.AsNoTracking().CountAsync());
                Assert.Equal(2, await db.ClientCaseDocuments.AsNoTracking().CountAsync());
                Assert.Equal(bindingCount, await db.StorageProductBindings.AsNoTracking().CountAsync());
            }
        }
        finally
        {
            Assert.StartsWith("pp-managed-revocation-", Path.GetFileName(root), StringComparison.Ordinal);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
