using System.Net.Http.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SyntheticWritableBackendProcessTests
{
    [Theory]
    [InlineData("legacy")]
    [InlineData("modern")]
    public async Task BrowserWorkingUploadRecoversLostResponseWithoutDuplicateInEitherCard(string cardMode)
    {
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(cardMode, workerEnabled: true);
        int taskId;
        await using (var db = runtime.OpenDatabase())
        {
            var user = await db.Users.SingleAsync();
            var client = new Client { Name = "Synthetic browser client" };
            var clientCase = new ClientCase { Client = client, Title = "Synthetic browser case" };
            var task = new TaskJournalItem
            {
                Title = "Synthetic browser upload task",
                ClientCase = clientCase,
                Client = client,
                AuthorUserId = user.Id,
                AssigneeUserId = user.Id,
                DueAt = DateTime.UtcNow.AddDays(1),
                UpdatedAt = DateTime.UtcNow,
            };
            db.TaskJournalItems.Add(task);
            await db.SaveChangesAsync();
            taskId = task.Id;
            await SeedPendingIntentAsync(db, task, user.Id);
        }
        await runtime.CheckUploadBrowserAsync(taskId, cardMode);
        await using (var db = runtime.OpenDatabase())
        {
            var file = Assert.Single(await db.TaskJournalFiles.ToArrayAsync());
            Assert.Equal($"browser-{cardMode}-attachment.txt", file.OriginalFileName);
            Assert.Equal(taskId, file.TaskJournalItemId);
            Assert.NotNull(file.ClientCaseDocumentId);
            Assert.Equal(1, await db.ClientCaseDocuments.CountAsync());
            Assert.Equal(2, await db.StorageOperations.CountAsync(o => o.OperationType == StorageOperationTypes.PublishTaskFile));
            Assert.Equal(1, await db.StorageOperations.CountAsync(o => o.OperationType == StorageOperationTypes.PublishTaskFile
                && o.Status == StorageOperationStatuses.Pending && o.StorageObject.LogicalName == "synthetic-paused.txt"));
        }
        runtime.AssertNoServerErrors();
    }

    [Fact]
    public async Task OrdinaryRuntimeMigratesAndAuthenticatesOnlySyntheticDataWithEmailDisabled()
    {
        string root;
        // Even an enabled storage worker flag must not imply that a background service was registered.
        await using (var runtime = await SyntheticWritableBackendProcess.StartAsync(workerEnabled: true))
        {
            root = runtime.Root;
            using var health = await runtime.Client.GetAsync("/api/health");
            Assert.True(health.IsSuccessStatusCode);
            using var login = await runtime.Client.PostAsJsonAsync("/api/auth/login",
                new { email = runtime.Email, password = runtime.Password });
            Assert.True(login.IsSuccessStatusCode, "Synthetic login failed.");
            var authentication = await login.Content.ReadFromJsonAsync<LoginResponse>();
            Assert.NotNull(authentication);
            runtime.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authentication.Token);
            await using var db = runtime.OpenDatabase();
            var user = Assert.Single(await db.Users.AsNoTracking().ToArrayAsync());
            Assert.Equal(runtime.Email, user.Email);
            Assert.True(user.IsActive && user.IsApproved);
            Assert.Empty(await db.Clients.AsNoTracking().ToArrayAsync());
            Assert.Equal("false", await db.SystemSettings.Where(s => s.Key == "notifications.email.enabled")
                .Select(s => s.Value).SingleAsync());
            Assert.False(await db.SystemSettings.AnyAsync(s => s.Key.Contains("smtp")));
            var client = new Client { Name = "Synthetic upload client" };
            db.Clients.Add(client);
            await db.SaveChangesAsync();
            var clientCase = new ClientCase { ClientId = client.Id, Title = "Synthetic upload case" };
            db.ClientCases.Add(clientCase);
            await db.SaveChangesAsync();
            var task = new TaskJournalItem
            {
                Title = "Synthetic upload task",
                ClientCaseId = clientCase.Id,
                ClientId = client.Id,
                AuthorUserId = user.Id,
                AssigneeUserId = user.Id,
                DueAt = DateTime.UtcNow.AddDays(1),
                UpdatedAt = DateTime.UtcNow,
            };
            db.TaskJournalItems.Add(task);
            await db.SaveChangesAsync();
            var uploadKey = Guid.NewGuid();
            var bytes = Encoding.UTF8.GetBytes("Synthetic file: Приложение к задаче");
            var ids = new List<int>();
            // Replay the same accepted key as a browser recovering a lost HTTP acknowledgement.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var body = new MultipartFormDataContent();
                using var file = new ByteArrayContent(bytes);
                file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                body.Add(file, "file", "synthetic-attachment.txt");
                using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/tasks/{task.Id}/files") { Content = body };
                request.Headers.Add("X-Upload-Key", uploadKey.ToString("D"));
                using var response = await runtime.Client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                var saved = await response.Content.ReadFromJsonAsync<TaskJournalFileDto>();
                Assert.NotNull(saved);
                ids.Add(saved.Id);
                Assert.NotNull(saved.ClientCaseDocumentId);
            }
            Assert.Equal(ids[0], ids[1]);
            using var statusResponse = await runtime.Client.GetAsync($"/api/tasks/{task.Id}/files/uploads?uploadKeys={uploadKey:D}");
            Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
            var state = Assert.Single((await statusResponse.Content.ReadFromJsonAsync<TaskFileUploadState[]>())!);
            Assert.Equal("uploaded", state.Status);
            Assert.Equal(ids[0], state.FileId);
            Assert.Equal(bytes, await runtime.Client.GetByteArrayAsync($"/api/tasks/{task.Id}/files/{ids[0]}/download"));
            Assert.Equal(1, await db.TaskJournalFiles.CountAsync());
            Assert.Equal(1, await db.ClientCaseDocuments.CountAsync());
            Assert.Equal(1, await db.StorageOperations.CountAsync(o => o.OperationType == StorageOperationTypes.PublishTaskFile));
            var pendingKey = await SeedPendingIntentAsync(db, task, user.Id);
            using var pausedResponse = await runtime.Client.GetAsync($"/api/tasks/{task.Id}/files/uploads?uploadKeys={pendingKey:D}");
            Assert.Equal(HttpStatusCode.OK, pausedResponse.StatusCode);
            var paused = Assert.Single((await pausedResponse.Content.ReadFromJsonAsync<TaskFileUploadState[]>())!);
            Assert.Equal("paused", paused.Status);
            Assert.Null(paused.FileId);
            Assert.Contains("отправлять файл заново не нужно", paused.Message);
            runtime.AssertNoServerErrors();
        }
        Assert.False(Directory.Exists(root));
    }

    // Synthetic catalog metadata only: this verifies pause presentation, not a real quorum upload.
    private static async Task<Guid> SeedPendingIntentAsync(PaymentContext db, TaskJournalItem task, int actorId)
    {
        var key = Guid.NewGuid();
        var storageObject = new StorageObject
        {
            DataClass = "task-file",
            OwnerType = "task-file",
            CurrentGeneration = 1,
            OwnerId = $"upload:{task.Id}:{actorId}:{key:N}",
            LogicalName = "synthetic-paused.txt",
            ContentType = "text/plain",
        };
        var generation = new StorageObjectGeneration { Generation = 1, SizeBytes = 3, Sha256 = new string('a', 64) };
        storageObject.Generations.Add(generation);
        var commit = new StorageOperation
        {
            OperationType = "commit",
            Status = StorageOperationStatuses.Succeeded,
            StorageObjectGeneration = generation,
            IdempotencyKey = StorageObjectService.GetCommitOperationKey($"task-file:{task.Id}:{actorId}:{key:N}"),
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            { taskId = task.Id, actorId, caseId = task.ClientCaseId, fileName = storageObject.LogicalName }),
        };
        storageObject.Operations.Add(commit);
        storageObject.Operations.Add(new StorageOperation
        {
            OperationType = StorageOperationTypes.PublishTaskFile,
            Status = StorageOperationStatuses.Pending,
            StorageObjectGeneration = generation,
            IdempotencyKey = $"task-file-publication:{commit.Id:N}",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { commitOperationId = commit.Id }),
        });
        db.StorageObjects.Add(storageObject);
        await db.SaveChangesAsync();
        return key;
    }
}
