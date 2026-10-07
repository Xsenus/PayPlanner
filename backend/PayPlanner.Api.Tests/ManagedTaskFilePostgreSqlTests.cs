using System.Net;
using System.Data.Common;
using System.Security.Claims;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services.Storage;
using Xunit;
using Xunit.Abstractions;

namespace PayPlanner.Api.Tests;

public sealed class ManagedTaskFilePostgreSqlTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MultipartUploadRetryAndDownloadUseRealBindingsAndRejectAnOutsider()
    {
        var connection = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        const string prefix = "pp_task_upload_pg_";
        var name = prefix + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(root);
        var adminSettings = new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var create = admin.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE \"{name}\"";
                await create.ExecuteNonQueryAsync();
                created = true;
            }
            var settings = new NpgsqlConnectionStringBuilder(adminSettings.ConnectionString) { Database = name };
            var discoveryCapture = new DiscoveryCommandCapture();
            var dbOptions = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString).AddInterceptors(discoveryCapture)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;
            await using var db = new PostgreSqlPaymentContext(dbOptions, useObjectAuditModel: false);
            await db.Database.MigrateAsync();
            var author = new User
            {
                FullName = "Автор",
                Email = "task-upload@example.test",
                PasswordHash = "hash",
                IsActive = true,
                IsApproved = true,
                Role = new Role { Name = "admin" }
            };
            var outsider = new User
            {
                FullName = "Посторонний",
                Email = "outsider@example.test",
                PasswordHash = "hash",
                IsActive = true,
                IsApproved = true,
                Role = new Role { Name = "upload-outsider" }
            };
            var clientRecord = new Client { Name = "Клиент" };
            db.AddRange(author, outsider, clientRecord); await db.SaveChangesAsync();
            var clientCase = new ClientCase { ClientId = clientRecord.Id, Title = "Документы" };
            db.ClientCases.Add(clientCase); await db.SaveChangesAsync();
            var task = new TaskJournalItem
            {
                Title = "Документ",
                ClientId = clientRecord.Id,
                ClientCaseId = clientCase.Id,
                AuthorUserId = author.Id,
                AssigneeUserId = author.Id,
                DueAt = DateTime.UtcNow.AddDays(1),
                UpdatedAt = DateTime.UtcNow
            };
            db.TaskJournalItems.Add(task); await db.SaveChangesAsync();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:RootPath"] = Path.Combine(root, "files"),
                ["StorageOrchestration:LocalRoot"] = Path.Combine(root, "objects"),
                ["Email:Enabled"] = "false",
                ["BackgroundServices:Enabled"] = "false"
            }).Build();
            var environment = new TestEnvironment(root);
            var monitor = new StaticOptions(new()
            {
                Enabled = true,
                ProductReadEnabledClasses = ["task-file", "case-document"],
                ProductWriteEnabledClasses = ["task-file", "case-document"],
                DefaultPolicy = new() { WriteProviders = ["local"], ReadProviders = ["local"] }
            });
            var local = new GatedLocalProvider(new LocalStorageProvider(environment, config));
            var legacy = new LegacyLocalStorageProvider(environment, config);
            var registry = new StorageProviderRegistry([local, legacy, new ManagedCompatibilityStorageProvider(db, legacy)]);
            var policy = new StoragePolicyService(monitor, registry);
            using var server = new TestServer(new WebHostBuilder().ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("fixture", _ => { });
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(TasksController).Assembly).AddControllersAsServices();
                services.AddScoped<PaymentContext>(_ => new PostgreSqlPaymentContext(dbOptions, useObjectAuditModel: false));
                services.AddScoped<IStorageObjectService>(sp => new StorageObjectService(sp.GetRequiredService<PaymentContext>(), policy, registry, monitor));
                services.AddScoped<IProductStorageBindingService>(sp => new ProductStorageBindingService(sp.GetRequiredService<PaymentContext>(), policy));
                services.AddScoped<IManagedTaskFileUploadService>(sp => new ManagedTaskFileUploadService(sp.GetRequiredService<PaymentContext>(),
                    sp.GetRequiredService<IStorageObjectService>(), sp.GetRequiredService<IProductStorageBindingService>(), environment, config, monitor));
                services.AddScoped<ITaskFileUploadStatusReader>(sp => new TaskFileUploadStatusReader(sp.GetRequiredService<PaymentContext>(),
                    new TaskFilePublicationAuthorizationService(sp.GetRequiredService<PaymentContext>()), monitor,
                    new StorageWorkerAvailability(false)));
                services.AddScoped<IProductStorageReader>(sp => new ProductStorageReader(sp.GetRequiredService<PaymentContext>(), sp.GetRequiredService<IStorageObjectService>(), monitor));
                services.AddScoped<IProductStorageDeletionService>(sp => new ProductStorageDeletionService(sp.GetRequiredService<PaymentContext>(),
                    sp.GetRequiredService<IStorageObjectService>(), registry, legacy));
                // Only cancellation is captured. No real transport or hosted
                // background sender is registered in this in-memory host.
                services.AddSingleton(DispatchProxy.Create<INotificationService, CancellationOnlyNotifications>());
                services.AddScoped<TasksController>(sp => new TasksController(sp.GetRequiredService<PaymentContext>(), sp.GetRequiredService<INotificationService>(), environment,
                    configuration: config, productStorageReader: sp.GetRequiredService<IProductStorageReader>(),
                    managedTaskUploads: sp.GetRequiredService<IManagedTaskFileUploadService>(),
                    productStorageDeletion: sp.GetRequiredService<IProductStorageDeletionService>(),
                    taskFileUploadStatusReader: sp.GetRequiredService<ITaskFileUploadStatusReader>()));
                services.AddScoped<CasesV2Controller>(sp => new CasesV2Controller(sp.GetRequiredService<PaymentContext>(), environment,
                    configuration: config, productStorageDeletion: sp.GetRequiredService<IProductStorageDeletionService>()));
            }).Configure(app =>
            {
                app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            }));
            using var client = server.CreateClient();
            client.DefaultRequestHeaders.Add("X-Fixture-User", author.Id.ToString());
            var key = Guid.NewGuid();
            async Task<HttpResponseMessage> Upload(Guid uploadKey, byte[] bytes, string? documentKey = null)
            {
                using var form = new MultipartFormDataContent();
                using var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new("text/plain");
                form.Add(content, "file", "document.txt");
                if (documentKey is not null) form.Add(new StringContent(documentKey), "documentKey");
                using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/tasks/{task.Id}/files") { Content = form };
                request.Headers.Add("X-Upload-Key", uploadKey.ToString());
                return await client.SendAsync(request);
            }
            int fileId;
            string documentKey;
            using (var response = await Upload(key, [1, 2, 3, 4]))
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                fileId = body.RootElement.GetProperty("id").GetInt32();
                documentKey = body.RootElement.GetProperty("documentKey").GetString()!;
                Assert.True(body.RootElement.GetProperty("clientCaseDocumentId").GetInt32() > 0);
            }
            using (var response = await Upload(key, [1, 2, 3, 4]))
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(fileId, body.RootElement.GetProperty("id").GetInt32());
            }
            Assert.Equal(1, await db.TaskJournalFiles.CountAsync());
            Assert.Equal(1, await db.ClientCaseDocuments.CountAsync());
            Assert.Equal(1, await db.TaskJournalComments.CountAsync());
            Assert.Equal(2, await db.StorageProductBindings.CountAsync());
            Assert.Equal(2, await db.StorageObjects.CountAsync());
            using (var response = await client.GetAsync($"/api/tasks/{task.Id}/files/{fileId}/download"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
            }
            var downloadedObjectId = await db.StorageProductBindings.AsNoTracking()
                .Where(b => b.Source == LegacyStorageSources.TaskFiles && b.SourceId == fileId)
                .Select(b => b.StorageObjectId).SingleAsync();
            foreach (var range in new[] { "bytes=1-2", "bytes=-2", "bytes=99-" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/tasks/{task.Id}/files/{fileId}/download");
                request.Headers.Range = System.Net.Http.Headers.RangeHeaderValue.Parse(range);
                using var response = await client.SendAsync(request);
                var unsatisfiable = range == "bytes=99-";
                Assert.Equal(unsatisfiable ? HttpStatusCode.RequestedRangeNotSatisfiable : HttpStatusCode.PartialContent, response.StatusCode);
                Assert.Equal(4, response.Content.Headers.ContentRange!.Length);
                Assert.Equal(unsatisfiable ? Array.Empty<byte>() : range == "bytes=1-2" ? [2, 3] : [3, 4],
                    await response.Content.ReadAsByteArrayAsync());
                Assert.Empty(Directory.EnumerateFiles(Path.GetTempPath(), $"pp_verified_read_{downloadedObjectId:N}_*.tmp"));
            }
            using (var response = await Upload(key, [9, 9, 9, 9])) Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using (var response = await Upload(Guid.NewGuid(), [5, 6, 7, 8], documentKey)) Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using (var response = await Upload(key, [1, 2, 3, 4]))
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.False(body.RootElement.GetProperty("isLatestVersion").GetBoolean());
            }
            local.Arm();
            var concurrentKey = Guid.NewGuid();
            var responses = await Task.WhenAll(Upload(concurrentKey, [10, 11, 12, 13]), Upload(concurrentKey, [10, 11, 12, 13]));
            try
            {
                var ids = new List<int>();
                foreach (var response in responses)
                {
                    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    ids.Add(body.RootElement.GetProperty("id").GetInt32());
                }
                Assert.Equal(ids[0], ids[1]);
            }
            finally { foreach (var response in responses) response.Dispose(); }
            local.Arm();
            var conflictingKey = Guid.NewGuid();
            responses = await Task.WhenAll(Upload(conflictingKey, [20, 21, 22, 23]), Upload(conflictingKey, [30, 31, 32, 33]));
            try
            {
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
                var winnerIndex = responses[0].StatusCode == HttpStatusCode.Created ? 0 : 1;
                using var body = JsonDocument.Parse(await responses[winnerIndex].Content.ReadAsStringAsync());
                var winnerId = body.RootElement.GetProperty("id").GetInt32();
                using var download = await client.GetAsync($"/api/tasks/{task.Id}/files/{winnerId}/download");
                Assert.Equal(HttpStatusCode.OK, download.StatusCode);
                Assert.Equal(winnerIndex == 0 ? new byte[] { 20, 21, 22, 23 } : [30, 31, 32, 33],
                    await download.Content.ReadAsByteArrayAsync());
            }
            finally { foreach (var response in responses) response.Dispose(); }
            await db.TaskJournalItems.Where(t => t.Id == task.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ClientCaseId, (int?)null));
            string lateKey;
            int lateFirstId;
            using (var response = await Upload(Guid.NewGuid(), [40, 41, 42, 43]))
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                lateKey = body.RootElement.GetProperty("documentKey").GetString()!;
                lateFirstId = body.RootElement.GetProperty("id").GetInt32();
                Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("clientCaseDocumentId").ValueKind);
            }
            await db.TaskJournalItems.Where(t => t.Id == task.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ClientCaseId, clientCase.Id));
            int lateSecondId;
            int lateCaseId;
            using (var response = await Upload(Guid.NewGuid(), [50, 51, 52, 53], lateKey))
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                lateSecondId = body.RootElement.GetProperty("id").GetInt32();
                lateCaseId = body.RootElement.GetProperty("clientCaseDocumentId").GetInt32();
                Assert.Equal(2, body.RootElement.GetProperty("version").GetInt32());
            }
            Assert.Equal(1, (await db.ClientCaseDocuments.AsNoTracking().SingleAsync(d => d.Id == lateCaseId)).Version);
            Assert.Equal(lateCaseId, (await db.TaskJournalFiles.AsNoTracking().SingleAsync(f => f.Id == lateSecondId)).ClientCaseDocumentId);
            using (var response = await client.GetAsync($"/api/tasks/{task.Id}/files"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(lateCaseId, body.RootElement.EnumerateArray().Single(f => f.GetProperty("id").GetInt32() == lateSecondId)
                    .GetProperty("clientCaseDocumentId").GetInt32());
                Assert.Equal(JsonValueKind.Null, body.RootElement.EnumerateArray().Single(f => f.GetProperty("id").GetInt32() == lateFirstId)
                    .GetProperty("clientCaseDocumentId").ValueKind);
            }
            client.DefaultRequestHeaders.Remove("X-Fixture-User");
            client.DefaultRequestHeaders.Add("X-Fixture-User", outsider.Id.ToString());
            using (var response = await Upload(Guid.NewGuid(), [1, 2, 3, 4])) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await client.GetAsync($"/api/tasks/{task.Id}/files/{fileId}/download")) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await client.DeleteAsync($"/api/tasks/{task.Id}/files/{fileId}")) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await client.DeleteAsync($"/api/tasks/{task.Id}")) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await client.DeleteAsync($"/api/v2/cases/{clientCase.Id}")) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(6, await db.TaskJournalFiles.CountAsync());
            Assert.Equal(5, await db.ClientCaseDocuments.CountAsync());
            Assert.Equal(6, await db.TaskJournalComments.CountAsync());
            Assert.Equal(11, await db.StorageObjects.CountAsync());
            Assert.Equal(11, Directory.EnumerateFiles(Path.Combine(root, "objects"), "*.txt", SearchOption.AllDirectories).Count());
            client.DefaultRequestHeaders.Remove("X-Fixture-User");
            client.DefaultRequestHeaders.Add("X-Fixture-User", author.Id.ToString());
            var deletedFile = await db.TaskJournalFiles.AsNoTracking().SingleAsync(f => f.Id == fileId);
            var compatibilityPath = Path.Combine(root, "files", "task-files", deletedFile.StoragePath);
            using (var response = await client.DeleteAsync($"/api/tasks/{task.Id}/files/{fileId}")) Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(5, await db.TaskJournalFiles.CountAsync());
            Assert.Equal(5, await db.ClientCaseDocuments.CountAsync());
            Assert.Equal(7, await db.TaskJournalComments.CountAsync());
            Assert.Single(await db.StorageTombstones.ToListAsync());
            Assert.True(File.Exists(compatibilityPath));
            using (var response = await Upload(key, [1, 2, 3, 4])) Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using (var response = await client.GetAsync($"/api/tasks/{task.Id}/files/{fileId}/download")) Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var worker = new StorageOperationProcessor(db, registry, monitor, TimeProvider.System, NullLogger<StorageOperationProcessor>.Instance);
            Assert.Equal(2, (await worker.ProcessBatchAsync("pg-task-delete", CancellationToken.None)).Succeeded);
            Assert.False(File.Exists(compatibilityPath));
            Assert.Equal(10, Directory.EnumerateFiles(Path.Combine(root, "objects"), "*.txt", SearchOption.AllDirectories).Count());
            using (var response = await client.DeleteAsync($"/api/v2/cases/{clientCase.Id}")) Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            db.ChangeTracker.Clear();
            Assert.Equal(5, await db.TaskJournalFiles.CountAsync());
            Assert.Empty(await db.ClientCaseDocuments.ToListAsync());
            Assert.False(await db.ClientCases.AnyAsync(c => c.Id == clientCase.Id));
            Assert.False(await db.TaskJournalFiles.AnyAsync(f => f.ClientCaseDocumentId != null));
            Assert.Equal(10, (await worker.ProcessBatchAsync("pg-case-hard-delete", CancellationToken.None)).Succeeded);
            Assert.Equal(5, Directory.EnumerateFiles(Path.Combine(root, "objects"), "*.txt", SearchOption.AllDirectories).Count());
            using (var response = await client.DeleteAsync($"/api/tasks/{task.Id}")) Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            db.ChangeTracker.Clear();
            Assert.Empty(await db.TaskJournalFiles.ToListAsync());
            Assert.False(await db.TaskJournalItems.AnyAsync(t => t.Id == task.Id));
            Assert.Equal(10, (await worker.ProcessBatchAsync("pg-task-hard-delete", CancellationToken.None)).Succeeded);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "objects"), "*.txt", SearchOption.AllDirectories));
            Assert.False(await db.StorageObjects.AnyAsync(o => !o.IsDeleted));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));

            // Durable publication uses a fresh context and no HTTP/browser File.
            var resumeCase = new ClientCase { ClientId = clientRecord.Id, Title = "Возобновление" };
            db.ClientCases.Add(resumeCase);
            await db.SaveChangesAsync();
            var resumeTask = new TaskJournalItem
            {
                Title = "Сохранённая загрузка",
                ClientId = clientRecord.Id,
                ClientCaseId = resumeCase.Id,
                AuthorUserId = author.Id,
                AssigneeUserId = author.Id,
                DueAt = DateTime.UtcNow.AddDays(1)
            };
            db.TaskJournalItems.Add(resumeTask);
            await db.SaveChangesAsync();
            var resumeKey = Guid.NewGuid();
            var formFile = new Microsoft.AspNetCore.Http.FormFile(new MemoryStream([1, 2, 3, 4]), 0, 4, "file", "resume.txt")
            { Headers = new Microsoft.AspNetCore.Http.HeaderDictionary(), ContentType = "text/plain" };
            var uploader = new ManagedTaskFileUploadService(db, new StorageObjectService(db, policy, registry, monitor),
                new ProductStorageBindingService(db, policy), environment, config, monitor);
            Assert.Equal("forbidden", (await uploader.UploadAsync(new(resumeTask.Id, author.Id, "Автор", resumeKey,
                formFile, "resume.txt", null, _ => Task.FromResult(false)), CancellationToken.None)).Status);
            var resumeOperationId = await db.StorageOperations.Where(o => o.IdempotencyKey ==
                StorageObjectService.GetCommitOperationKey($"task-file:{resumeTask.Id}:{author.Id}:{resumeKey:N}"))
                .Select(o => o.Id).SingleAsync();
            async Task<ManagedTaskFileUploadResult> Resume()
            {
                await using var context = new PostgreSqlPaymentContext(dbOptions, useObjectAuditModel: false);
                var service = new ManagedTaskFileUploadService(context, new StorageObjectService(context, policy, registry, monitor),
                    new ProductStorageBindingService(context, policy), environment, config, monitor);
                var authorization = new TaskFilePublicationAuthorizationService(context);
                return await service.ResumeAsync(resumeOperationId, authorization.CanPublishAsync, CancellationToken.None);
            }
            var resumed = await Task.WhenAll(Resume(), Resume());
            Assert.All(resumed, result => Assert.Equal("uploaded", result.Status));
            Assert.Equal(resumed[0].FileId, resumed[1].FileId);
            Assert.Single(await db.TaskJournalFiles.AsNoTracking().ToListAsync());
            Assert.Single(await db.ClientCaseDocuments.AsNoTracking().ToListAsync());
            Assert.Equal(1, await db.TaskJournalComments.CountAsync(c => c.TaskJournalItemId == resumeTask.Id));
            Assert.Equal(2, await db.StorageObjects.CountAsync(o => !o.IsDeleted));
            await db.Roles.Where(r => r.Id == author.RoleId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Name, "reader"));
            Assert.Equal("forbidden", (await Resume()).Status);
            await db.Roles.Where(r => r.Id == author.RoleId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Name, "admin"));
            await db.Users.Where(u => u.Id == author.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsApproved, false));
            Assert.Equal("forbidden", (await Resume()).Status);
            Assert.Single(await db.TaskJournalFiles.AsNoTracking().ToListAsync());
            Assert.Single(await db.ClientCaseDocuments.AsNoTracking().ToListAsync());
            await using var stored = await new StorageObjectService(db, policy, registry, monitor).OpenReadAsync(
                await db.StorageProductBindings.Where(b => b.Source == LegacyStorageSources.TaskFiles && b.SourceId == resumed[0].FileId)
                    .Select(b => b.StorageObjectId).SingleAsync(), CancellationToken.None);
            Assert.NotNull(stored);
            using var received = new MemoryStream();
            await stored.Content.CopyToAsync(received);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, received.ToArray());
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));

            // Two real workers overlap at the lease claim, then publish without
            // any retained browser request or unconditional authorization callback.
            await db.Users.Where(u => u.Id == author.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsApproved, true));
            monitor.CurrentValue.AutoTaskFilePublicationEnabled = true;
            var queuedTask = new TaskJournalItem
            {
                Title = "Фоновое завершение",
                ClientId = clientRecord.Id,
                ClientCaseId = resumeCase.Id,
                AuthorUserId = author.Id,
                AssigneeUserId = author.Id,
                DueAt = DateTime.UtcNow.AddDays(1)
            };
            db.TaskJournalItems.Add(queuedTask);
            await db.SaveChangesAsync();
            var queuedKey = Guid.NewGuid();
            var queuedFile = new Microsoft.AspNetCore.Http.FormFile(new MemoryStream([4, 3, 2, 1]), 0, 4, "file", "queued.txt")
            { Headers = new Microsoft.AspNetCore.Http.HeaderDictionary(), ContentType = "text/plain" };
            Assert.Equal("forbidden", (await uploader.UploadAsync(new(queuedTask.Id, author.Id, "Автор", queuedKey,
                queuedFile, "queued.txt", null, _ => Task.FromResult(false)), CancellationToken.None)).Status);
            var queuedCommit = await db.StorageOperations.AsNoTracking().SingleAsync(o => o.IdempotencyKey ==
                StorageObjectService.GetCommitOperationKey($"task-file:{queuedTask.Id}:{author.Id}:{queuedKey:N}"));
            var queuedOperation = await db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == StorageOperationTypes.PublishTaskFile);
            Assert.Equal(queuedCommit.StorageObjectId, queuedOperation.StorageObjectId);
            Assert.Equal(queuedCommit.StorageObjectGenerationId, queuedOperation.StorageObjectGenerationId);
            Assert.False(await db.TaskJournalFiles.AnyAsync(f => f.TaskJournalItemId == queuedTask.Id));
            client.DefaultRequestHeaders.Remove("X-Fixture-User");
            client.DefaultRequestHeaders.Add("X-Fixture-User", author.Id.ToString());
            using (var statusResponse = await client.GetAsync($"/api/tasks/{queuedTask.Id}/files/uploads"))
            {
                Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
                Assert.True(statusResponse.Headers.CacheControl?.NoStore);
                var states = JsonSerializer.Deserialize<TaskFileUploadState[]>(await statusResponse.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var state = Assert.Single(states!);
                Assert.Equal(queuedKey, state.UploadKey);
                // This HTTP fixture deliberately registers no background services; manual processors below still publish.
                Assert.Equal("paused", state.Status);
                Assert.Null(state.FileId);
            }
            client.DefaultRequestHeaders.Remove("X-Fixture-User");
            client.DefaultRequestHeaders.Add("X-Fixture-User", outsider.Id.ToString());
            using (var deniedStatus = await client.GetAsync($"/api/tasks/{queuedTask.Id}/files/uploads"))
                Assert.Equal(HttpStatusCode.Forbidden, deniedStatus.StatusCode);
            client.DefaultRequestHeaders.Remove("X-Fixture-User");
            client.DefaultRequestHeaders.Add("X-Fixture-User", author.Id.ToString());
            var claimGate = new PublicationClaimGate();
            var workerOptions = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).AddInterceptors(claimGate).Options;
            async Task<StorageOperationBatchResult> PublishQueued(string workerId)
            {
                await using var context = new PostgreSqlPaymentContext(workerOptions, useObjectAuditModel: false);
                var managed = new ManagedTaskFileUploadService(context, new StorageObjectService(context, policy, registry, monitor),
                    new ProductStorageBindingService(context, policy), environment, config, monitor);
                var publications = new TaskFilePublicationProcessor(context, managed, new TaskFilePublicationAuthorizationService(context),
                    policy, monitor, TimeProvider.System, NullLogger<TaskFilePublicationProcessor>.Instance);
                var processor = new StorageOperationProcessor(context, registry, monitor, TimeProvider.System,
                    NullLogger<StorageOperationProcessor>.Instance, taskFilePublications: publications);
                return await processor.ProcessBatchAsync(workerId, CancellationToken.None);
            }
            var workerResults = await Task.WhenAll(PublishQueued("pg-publication-one"), PublishQueued("pg-publication-two"));
            Assert.Equal(2, claimGate.Arrivals);
            Assert.Equal(1, workerResults.Sum(r => r.Claimed));
            Assert.Equal(1, workerResults.Sum(r => r.Succeeded));
            Assert.Single(await db.TaskJournalFiles.AsNoTracking().Where(f => f.TaskJournalItemId == queuedTask.Id).ToListAsync());
            Assert.Single(await db.TaskJournalComments.AsNoTracking().Where(c => c.TaskJournalItemId == queuedTask.Id).ToListAsync());
            queuedOperation = await db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == queuedOperation.Id);
            Assert.Equal(StorageOperationStatuses.Succeeded, queuedOperation.Status);
            Assert.Null(queuedOperation.LeaseOwner);
            Assert.Equal(1, queuedOperation.AttemptCount);
            var queuedDomainFile = await db.TaskJournalFiles.AsNoTracking().SingleAsync(f => f.TaskJournalItemId == queuedTask.Id);
            using (var completedStatus = await client.GetAsync($"/api/tasks/{queuedTask.Id}/files/uploads"))
            {
                Assert.Equal(HttpStatusCode.OK, completedStatus.StatusCode);
                var states = JsonSerializer.Deserialize<TaskFileUploadState[]>(await completedStatus.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var state = Assert.Single(states!);
                Assert.Equal("uploaded", state.Status);
                Assert.Equal(queuedDomainFile.Id, state.FileId);
            }
            Assert.NotNull(queuedDomainFile.ClientCaseDocumentId);
            Assert.Equal(new byte[] { 4, 3, 2, 1 }, await File.ReadAllBytesAsync(Path.Combine(root, "files", "task-files", queuedDomainFile.StoragePath)));
            var queuedCaseFile = await db.ClientCaseDocuments.AsNoTracking().SingleAsync(d => d.Id == queuedDomainFile.ClientCaseDocumentId);
            Assert.Equal(new byte[] { 4, 3, 2, 1 }, await File.ReadAllBytesAsync(new LocalCaseDocumentStorage(environment, config).ResolvePath(queuedCaseFile.StoragePath)));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
            // The result survives a fresh PostgreSQL context and goes through
            // the actual notification center without any external transports.
            await using (var notificationContext = new PostgreSqlPaymentContext(dbOptions, useObjectAuditModel: false))
            {
                var managed = new ManagedTaskFileUploadService(notificationContext,
                    new StorageObjectService(notificationContext, policy, registry, monitor),
                    new ProductStorageBindingService(notificationContext, policy), environment, config, monitor);
                var notificationService = new NotificationService(notificationContext, config,
                    DispatchProxy.Create<IEmailService, ForbiddenNotificationOutbound>(),
                    DispatchProxy.Create<ITelegramNotificationService, ForbiddenNotificationOutbound>(),
                    NullLogger<NotificationService>.Instance);
                var results = new TaskFilePublicationProcessor(notificationContext, managed,
                    new TaskFilePublicationAuthorizationService(notificationContext), policy, monitor, TimeProvider.System,
                    NullLogger<TaskFilePublicationProcessor>.Instance, notificationService);
                var resultWorker = new StorageOperationProcessor(notificationContext, registry, monitor, TimeProvider.System,
                    NullLogger<StorageOperationProcessor>.Instance, taskFilePublications: results);
                Assert.Equal(1, (await resultWorker.ProcessBatchAsync("pg-persistent-result", CancellationToken.None)).Succeeded);
                Assert.Equal(0, (await resultWorker.ProcessBatchAsync("pg-persistent-result-repeat", CancellationToken.None)).Claimed);
            }
            var persistentResult = await db.UserNotifications.AsNoTracking().SingleAsync();
            Assert.Equal(queuedTask.Id, persistentResult.TaskJournalItemId);
            Assert.Equal(author.Id, persistentResult.RecipientUserId);
            Assert.Equal("Файл прикреплён к задаче", persistentResult.Title);
            Assert.False(persistentResult.IsRead);
            // Synchronous publication already committed before the actor was
            // disabled. The worker must acknowledge it without granting access.
            var receiptKey = Guid.NewGuid();
            var receiptFile = new Microsoft.AspNetCore.Http.FormFile(new MemoryStream([8, 7, 6, 5]), 0, 4, "file", "receipt.txt")
            { Headers = new Microsoft.AspNetCore.Http.HeaderDictionary(), ContentType = "text/plain" };
            var receiptUpload = await uploader.UploadAsync(new(queuedTask.Id, author.Id, "Автор", receiptKey,
                receiptFile, "receipt.txt", null, _ => Task.FromResult(true)), CancellationToken.None);
            Assert.Equal("uploaded", receiptUpload.Status);
            await db.Users.Where(u => u.Id == author.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
            await using (var receiptContext = new PostgreSqlPaymentContext(dbOptions, useObjectAuditModel: false))
            {
                var receiptService = new ManagedTaskFileUploadService(receiptContext,
                    new StorageObjectService(receiptContext, policy, registry, monitor),
                    new ProductStorageBindingService(receiptContext, policy), environment, config, monitor);
                var receipts = new TaskFilePublicationProcessor(receiptContext, receiptService,
                    new TaskFilePublicationAuthorizationService(receiptContext), policy, monitor, TimeProvider.System,
                    NullLogger<TaskFilePublicationProcessor>.Instance);
                var receiptWorker = new StorageOperationProcessor(receiptContext, registry, monitor, TimeProvider.System,
                    NullLogger<StorageOperationProcessor>.Instance, taskFilePublications: receipts);
                Assert.Equal(1, (await receiptWorker.ProcessBatchAsync("pg-receipt-revoked", CancellationToken.None)).Succeeded);
            }
            using (var deniedReceipt = await client.GetAsync($"/api/tasks/{queuedTask.Id}/files/uploads"))
                Assert.Equal(HttpStatusCode.Forbidden, deniedReceipt.StatusCode);
            Assert.Equal(2, await db.TaskJournalFiles.CountAsync(f => f.TaskJournalItemId == queuedTask.Id));
            Assert.Equal(2, await db.TaskJournalComments.CountAsync(c => c.TaskJournalItemId == queuedTask.Id));

            // Qualify cursor SQL and the HTTP envelope on an owned PostgreSQL database.
            await db.Users.Where(u => u.Id == author.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, true));
            var pagedTask = new TaskJournalItem
            {
                Title = "Страницы загрузок",
                ClientId = clientRecord.Id,
                AuthorUserId = author.Id,
                AssigneeUserId = author.Id,
                DueAt = DateTime.UtcNow.AddDays(1)
            };
            db.TaskJournalItems.Add(pagedTask);
            await db.SaveChangesAsync();
            var pageTime = DateTime.UtcNow;
            StorageOperation AddIntent(int index, DateTime timestamp)
            {
                var key = Guid.NewGuid();
                var obj = new StorageObject
                {
                    DataClass = "task-file",
                    OwnerType = "task-file",
                    OwnerId = $"upload:{pagedTask.Id}:{author.Id}:{key:N}",
                    LogicalName = $"page-{index}.txt",
                    ContentType = "text/plain",
                    CurrentGeneration = 1
                };
                var generation = new StorageObjectGeneration { Generation = 1, SizeBytes = 3, Sha256 = new string('a', 64) };
                obj.Generations.Add(generation);
                var operation = new StorageOperation
                {
                    OperationType = "commit",
                    Status = StorageOperationStatuses.Succeeded,
                    StorageObjectGeneration = generation,
                    CreatedAt = timestamp,
                    IdempotencyKey = StorageObjectService.GetCommitOperationKey($"task-file:{pagedTask.Id}:{author.Id}:{key:N}"),
                    PayloadJson = JsonSerializer.Serialize(new { taskId = pagedTask.Id, actorId = author.Id, fileName = $"page-{index}.txt" })
                };
                obj.Operations.Add(operation); db.StorageObjects.Add(obj);
                return operation;
            }
            for (var index = 0; index < 56; index++)
            {
                var intent = AddIntent(index, pageTime);
                db.StorageOperations.Add(new StorageOperation
                {
                    StorageObject = intent.StorageObject,
                    StorageObjectGeneration = intent.StorageObjectGeneration,
                    OperationType = StorageOperationTypes.PublishTaskFile,
                    Status = StorageOperationStatuses.Pending,
                    IdempotencyKey = $"task-file-publication:{intent.Id:N}",
                    CreatedAt = pageTime
                });
            }
            await db.SaveChangesAsync();
            var noiseOwner = await db.StorageOperations.AsNoTracking().Where(o => o.IdempotencyKey.StartsWith($"write:task-file:{pagedTask.Id}:{author.Id}:"))
                .Select(o => o.StorageObjectId).FirstAsync();
            // A large unrelated synthetic history makes a full operation scan visible to the planner.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "StorageOperations" ("Id", "StorageObjectId", "OperationType", "Status", "IdempotencyKey", "AttemptCount", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), {noiseOwner}, 'commit', 'Succeeded', 'unrelated-plan-noise:' || i, 0, now(), now()
                FROM generate_series(1, 20000) AS i
                """);
            // The publication batch must remain selective with a large queue,
            // rather than passing only because this operation type has two rows.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "StorageOperations" ("Id", "StorageObjectId", "OperationType", "Status", "IdempotencyKey", "AttemptCount", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), {noiseOwner}, {StorageOperationTypes.PublishTaskFile}, {StorageOperationStatuses.Succeeded}, 'unrelated-publication-plan-noise:' || i, 0, now(), now()
                FROM generate_series(1, 20000) AS i
                """);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "StorageObjects" ("Id", "DataClass", "OwnerType", "OwnerId", "LogicalName", "ContentType", "CurrentGeneration", "IsDeleted", "Version", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), 'task-file', 'task-file', 'unrelated-owner:' || i, 'noise.txt', 'text/plain', 0, false, 0, now(), now()
                FROM generate_series(1, 20000) AS i
                """);
            await db.Database.ExecuteSqlRawAsync("ANALYZE \"StorageOperations\"; ANALYZE \"StorageObjects\"; ANALYZE \"StorageObjectGenerations\";");
            var pageReader = new TaskFileUploadStatusReader(db, new TaskFilePublicationAuthorizationService(db), monitor);
            discoveryCapture.Enabled = true;
            var pageOne = (await pageReader.ListPageAsync(pagedTask.Id, author.Id, CancellationToken.None))!;
            discoveryCapture.Enabled = false;
            await using (var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + discoveryCapture.Sql,
                (NpgsqlConnection)db.Database.GetDbConnection()))
            {
                foreach (var parameter in discoveryCapture.Parameters) explain.Parameters.Add(parameter);
                await db.Database.OpenConnectionAsync();
                using var plan = JsonDocument.Parse((string)(await explain.ExecuteScalarAsync())!);
                output.WriteLine("Discovery actual PostgreSQL query plan: " + plan.RootElement.GetRawText());
                var operationNodes = PlanNodes(plan.RootElement[0].GetProperty("Plan"))
                    .Where(n => n.TryGetProperty("Relation Name", out var relation) && relation.GetString() == "StorageOperations").ToArray();
                Assert.NotEmpty(operationNodes);
                Assert.DoesNotContain(operationNodes, n => n.GetProperty("Node Type").GetString() == "Seq Scan");
                var discardedOperations = operationNodes.Sum(n =>
                    (n.TryGetProperty("Rows Removed by Filter", out var removed) ? removed.GetDouble() : 0)
                    * n.GetProperty("Actual Loops").GetDouble());
                Assert.True(discardedOperations < 1000, $"Discovery discarded {discardedOperations} unrelated operations.");
                Assert.Contains(PlanNodes(plan.RootElement[0].GetProperty("Plan")), n =>
                    n.TryGetProperty("Index Name", out var index) && index.GetString() == "IX_StorageOperations_CommitIdempotencyPrefix");
                Assert.DoesNotContain(PlanNodes(plan.RootElement[0].GetProperty("Plan")), n =>
                    n.GetProperty("Node Type").GetString() == "Seq Scan"
                    && n.TryGetProperty("Relation Name", out var relation) && relation.GetString() == "StorageObjects");
            }
            await using (var queueExplain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + discoveryCapture.QueueSql,
                (NpgsqlConnection)db.Database.GetDbConnection()))
            {
                Assert.False(string.IsNullOrWhiteSpace(discoveryCapture.QueueSql));
                foreach (var parameter in discoveryCapture.QueueParameters) queueExplain.Parameters.Add(parameter);
                using var queuePlan = JsonDocument.Parse((string)(await queueExplain.ExecuteScalarAsync())!);
                output.WriteLine("Publication batch actual PostgreSQL query plan: " + queuePlan.RootElement.GetRawText());
                var nodes = PlanNodes(queuePlan.RootElement[0].GetProperty("Plan")).ToArray();
                Assert.DoesNotContain(nodes, n => n.GetProperty("Node Type").GetString() == "Seq Scan"
                    && n.TryGetProperty("Relation Name", out var relation) && relation.GetString() == "StorageOperations");
                Assert.Contains(nodes, n => n.TryGetProperty("Index Name", out var index)
                    && index.GetString() == "IX_StorageOperations_IdempotencyKey");
                var queueNodes = nodes.Where(n => n.TryGetProperty("Relation Name", out var relation)
                    && relation.GetString() == "StorageOperations").ToArray();
                Assert.Equal(50d, queueNodes.Sum(n => n.GetProperty("Actual Rows").GetDouble()
                    * n.GetProperty("Actual Loops").GetDouble()));
                var discardedQueues = queueNodes.Sum(n =>
                    (n.TryGetProperty("Rows Removed by Filter", out var removed) ? removed.GetDouble() : 0)
                    * n.GetProperty("Actual Loops").GetDouble());
                Assert.True(discardedQueues < 1000, $"Publication batch discarded {discardedQueues} unrelated operations.");
            }
            Assert.Equal(50, pageOne.Items.Count);
            Assert.NotNull(pageOne.NextCursor);
            var pageTwo = (await pageReader.ListPageAsync(pagedTask.Id, author.Id, CancellationToken.None, pageOne.NextCursor))!;
            Assert.Equal(6, pageTwo.Items.Count);
            Assert.Null(pageTwo.NextCursor);
            Assert.Empty(pageOne.Items.Select(s => s.UploadKey).Intersect(pageTwo.Items.Select(s => s.UploadKey)));
            var topWindow = await db.StorageOperations.Where(o => o.IdempotencyKey.StartsWith($"write:task-file:{pagedTask.Id}:{author.Id}:"))
                .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).Take(50).ToListAsync();
            foreach (var operation in topWindow) operation.PayloadJson = "{broken";
            await db.SaveChangesAsync();
            using (var sparseResponse = await client.GetAsync($"/api/tasks/{pagedTask.Id}/files/uploads?pendingOnly=true&includePageInfo=true"))
            {
                Assert.Equal(HttpStatusCode.OK, sparseResponse.StatusCode);
                Assert.True(sparseResponse.Headers.CacheControl?.NoStore);
                var sparse = JsonSerializer.Deserialize<TaskFileUploadPage>(await sparseResponse.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Assert.Empty(sparse!.Items);
                Assert.Equal(pageOne.NextCursor, sparse.NextCursor);
            }
            var pagePivot = await db.StorageOperations.Include(o => o.StorageObject).SingleAsync(o => o.Id == pageOne.NextCursor);
            pagePivot.StorageObject.OwnerId = "999";
            AddIntent(56, pageTime.AddDays(1));
            await db.SaveChangesAsync();
            using (var olderResponse = await client.GetAsync($"/api/tasks/{pagedTask.Id}/files/uploads?pendingOnly=true&includePageInfo=true&after={pageOne.NextCursor}"))
            {
                Assert.Equal(HttpStatusCode.OK, olderResponse.StatusCode);
                var older = JsonSerializer.Deserialize<TaskFileUploadPage>(await olderResponse.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Assert.Equal(pageTwo.Items.Select(s => s.UploadKey), older!.Items.Select(s => s.UploadKey));
                Assert.Null(older.NextCursor);
            }
            using (var invalidCursor = await client.GetAsync($"/api/tasks/{pagedTask.Id}/files/uploads?pendingOnly=true&includePageInfo=true&after={queuedCommit.Id}"))
                Assert.Equal(HttpStatusCode.BadRequest, invalidCursor.StatusCode);
            client.DefaultRequestHeaders.Remove("X-Fixture-User");
            client.DefaultRequestHeaders.Add("X-Fixture-User", outsider.Id.ToString());
            using (var deniedPage = await client.GetAsync($"/api/tasks/{pagedTask.Id}/files/uploads?pendingOnly=true&includePageInfo=true&after={pageOne.NextCursor}"))
                Assert.Equal(HttpStatusCode.Forbidden, deniedPage.StatusCode);
        }
        finally
        {
            if (created)
            {
                await using var sessions = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @name", admin);
                sessions.Parameters.AddWithValue("name", name);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                long remaining;
                do
                {
                    remaining = (long)(await sessions.ExecuteScalarAsync())!;
                    if (remaining == 0) break;
                    await Task.Delay(50);
                }
                while (DateTime.UtcNow < deadline);
                Assert.Equal(0L, remaining);
                await using var drop = admin.CreateCommand();
                drop.CommandText = $"DROP DATABASE \"{name}\"";
                await drop.ExecuteNonQueryAsync();
            }
            Assert.Equal(name, Path.GetFileName(root));
            Assert.StartsWith(prefix, name, StringComparison.Ordinal);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
    private sealed class DiscoveryCommandCapture : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public string Sql { get; private set; } = string.Empty;
        public NpgsqlParameter[] Parameters { get; private set; } = [];
        public string QueueSql { get; private set; } = string.Empty;
        public NpgsqlParameter[] QueueParameters { get; private set; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("\"PayloadJson\"", StringComparison.Ordinal)
                && command.CommandText.Contains("LIMIT", StringComparison.Ordinal))
            {
                Sql = command.CommandText;
                Parameters = command.Parameters.Cast<NpgsqlParameter>().Select(p => (NpgsqlParameter)((ICloneable)p).Clone()).ToArray();
            }
            if (Enabled && command.CommandText.Contains("'publishTaskFile'", StringComparison.Ordinal))
            {
                QueueSql = command.CommandText;
                QueueParameters = command.Parameters.Cast<NpgsqlParameter>().Select(p => (NpgsqlParameter)((ICloneable)p).Clone()).ToArray();
            }
            return ValueTask.FromResult(result);
        }
    }
    private static IEnumerable<JsonElement> PlanNodes(JsonElement node)
    {
        yield return node;
        if (!node.TryGetProperty("Plans", out var children)) yield break;
        foreach (var child in children.EnumerateArray())
            foreach (var descendant in PlanNodes(child)) yield return descendant;
    }
    private class ForbiddenNotificationOutbound : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException("Local PostgreSQL notification checks must not send external messages.");
    }

    private sealed class PublicationClaimGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public int Arrivals => _arrivals;
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_barrier.Task.IsCompleted && command.CommandText.Contains("UPDATE \"StorageOperations\"", StringComparison.Ordinal)
                && command.CommandText.Contains("LeaseOwner", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _arrivals) == 2) _barrier.TrySetResult();
                await _barrier.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }

    public class CancellationOnlyNotifications : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name == nameof(INotificationService.NotifyTaskCancelledAsync)
                ? Task.FromResult<IReadOnlyList<NotificationDelivery>>([])
                : throw new InvalidOperationException("Unexpected notification in isolated upload/delete host.");
    }

    private sealed class GatedLocalProvider(IStorageProvider inner) : IStorageProvider
    {
        private TaskCompletionSource? barrier;
        private int arrivals;
        public string Key => inner.Key;
        public StorageProviderCapabilities Capabilities => inner.Capabilities;
        public void Arm() { arrivals = 0; barrier = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public async Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct)
        {
            var result = await inner.PutAsync(request, ct);
            var gate = barrier;
            if (gate is not null && request.Locator.StartsWith("task-file/", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref arrivals) == 2) { barrier = null; gate.TrySetResult(); }
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            return result;
        }
        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct) => inner.HeadAsync(locator, ct);
        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct) => inner.OpenReadAsync(locator, ct);
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct) => inner.DeleteAsync(locator, ct);
    }
    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Fixture-User"], out var userId)) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Name, "Автор")], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
    private sealed class StaticOptions(StorageOrchestrationOptions options) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => options;
        public StorageOrchestrationOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }
    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
