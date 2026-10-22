using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Services.LegalIntegrations.Models;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedCaseDocumentWireTests : IClassFixture<SqliteMigratedTemplate>
{
    private readonly SqliteMigratedTemplate sqliteTemplate;

    public ManagedCaseDocumentWireTests(SqliteMigratedTemplate sqliteTemplate)
    {
        this.sqliteTemplate = sqliteTemplate;
    }

    [Theory]
    [InlineData("court-edit")]
    [InlineData("court-overview")]
    public async Task RealBrowserCourtYandexCopiesKeepControlsAndShowPendingThenReadySummary(string flow)
    {
        SudrfCourtCaseData Source(string summary) => new()
        {
            SourceUrl = SyntheticYandexHostingStartup.CourtUrl,
            Instance = "appeal",
            CourtName = "Синтетический суд",
            CourtActs = [new SudrfCourtAct { Title = "Апелляционное определение", ActDate = new DateTime(2026, 10, 1),
                SourceUrl = SyntheticYandexHostingStartup.CourtUrl, Summary = summary }]
        };
        await using var endpoint = await LoopbackYandexDiskEndpoint.StartAsync();
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(template: sqliteTemplate, workerEnabled: true,
            uploadCapacityChecks: true, yandexCopy: endpoint, courtSource: Source("Исходная браузерная сводка"));
        var caseId = await LoginAndCreateCaseAsync(runtime);
        await using (var seed = runtime.OpenDatabase())
            await seed.ClientCases.Where(c => c.Id == caseId).ExecuteUpdateAsync(update => update
                .SetProperty(c => c.CourtUrl, SyntheticYandexHostingStartup.CourtUrl));
        int deadlineId = 0; Guid attemptId = Guid.Empty; byte[] frozenBytes = [];
        await runtime.CheckCaseRetirementBrowserAsync(caseId, Guid.NewGuid(), false, flow, applyBoundary: async () =>
        {
            await using (var db = runtime.OpenDatabase())
            {
                Assert.Empty(await db.ClientCaseDocuments.ToArrayAsync()); Assert.Empty(await db.StorageProductBindings.ToArrayAsync());
                var deadline = await db.ClientCaseProceduralDeadlines.SingleAsync(d => d.ClientCaseId == caseId);
                Assert.Null(deadline.ClientCaseDocumentId); deadlineId = deadline.Id;
                deadline.DueAt = new DateTime(2027, 2, 7); deadline.Notes = "Ручная дата браузерного срока";
                attemptId = (await db.CourtDocumentSourceAttempts.SingleAsync()).Id;
                frozenBytes = (await db.CourtDocumentSourceContents.SingleAsync()).Bytes;
                Assert.Single(await db.TaskJournalItems.Where(t => t.ClientCaseId == caseId).ToArrayAsync());
                Assert.Equal(0, endpoint.PutAttempts); await db.SaveChangesAsync();
            }
            await runtime.SetCourtSourceAsync(Source("Текст после принятия браузерной сводки"));
            for (var retry = 0; retry < 5; retry++)
            {
                await using (var db = runtime.OpenDatabase())
                    await db.StorageOperations.Where(o => o.Status != StorageOperationStatuses.Succeeded && o.Status != StorageOperationStatuses.DeadLetter)
                        .ExecuteUpdateAsync(update => update.SetProperty(o => o.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
                Assert.Equal(0, (await runtime.RunOwnedYandexWorkerAsync()).DeadLettered);
                await using var proof = runtime.OpenDatabase();
                if (await proof.ClientCaseDocuments.AnyAsync()) break;
            }
            await using var completed = runtime.OpenDatabase(); Assert.Single(await completed.ClientCaseDocuments.ToArrayAsync());
        }, courtDocuments: true);
        await using var db = runtime.OpenDatabase();
        var document = await db.ClientCaseDocuments.SingleAsync();
        var deadlineProof = await db.ClientCaseProceduralDeadlines.SingleAsync();
        Assert.Equal(deadlineId, deadlineProof.Id); Assert.Equal(document.Id, deadlineProof.ClientCaseDocumentId);
        Assert.Equal(new DateTime(2027, 2, 7), deadlineProof.DueAt); Assert.Equal("Ручная дата браузерного срока", deadlineProof.Notes);
        Assert.Equal(attemptId, (await db.CourtDocumentSourceAttempts.SingleAsync()).Id);
        Assert.Single(await db.StorageProductBindings.ToArrayAsync());
        Assert.Single(await db.TaskJournalItems.Where(t => t.ClientCaseId == caseId).ToArrayAsync());
        Assert.Single(await db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" && a.Action == "Created").ToArrayAsync());
        Assert.Equal(frozenBytes, await File.ReadAllBytesAsync(Path.Combine(runtime.Root, "court-browser-download.html")));
        Assert.Equal(1, endpoint.PutCount); Assert.Equal(1, endpoint.PutAttempts);
        Assert.Equal(0, (await runtime.RunOwnedYandexWorkerAsync()).Claimed);
        runtime.AssertNoServerErrors();
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("unavailable")]
    [InlineData("lost-ack")]
    [InlineData("low-quota")]
    [InlineData("unknown-quota")]
    public async Task RealHttpCourtYandexCopiesPreserveImmediateControlsAndFrozenSummaryAcrossRestart(string failure)
    {
        SudrfCourtCaseData Source(string summary) => new()
        {
            SourceUrl = SyntheticYandexHostingStartup.CourtUrl,
            Instance = "appeal",
            CourtName = "Синтетический суд",
            CaseNumber = "OWNED-42",
            CourtActs = [new SudrfCourtAct { Title = "Апелляционное определение",
                ActDate = new DateTime(2026, 10, 1), SourceUrl = SyntheticYandexHostingStartup.CourtUrl, Summary = summary }]
        };
        await using var endpoint = await LoopbackYandexDiskEndpoint.StartAsync();
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(template: sqliteTemplate, workerEnabled: true,
            uploadCapacityChecks: true, yandexCopy: endpoint, courtSource: Source("Исходная судебная сводка"));
        var caseId = await LoginAndCreateCaseAsync(runtime);
        await using (var seed = runtime.OpenDatabase())
            await seed.ClientCases.Where(c => c.Id == caseId).ExecuteUpdateAsync(update => update
                .SetProperty(c => c.CourtUrl, SyntheticYandexHostingStartup.CourtUrl));
        async Task<CasesV2Controller.ClientCaseCourtSyncRunDto> SyncAsync()
        {
            using var response = await runtime.Client.PostAsync($"/api/v2/cases/{caseId}/court-sync/run", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<CasesV2Controller.ClientCaseCourtSyncRunDto>())!;
            Assert.True(result.Status == "external-sync-completed", $"Court sync status {result.Status}: {result.Message}");
            Assert.True(result.SyncedFromExternalSource);
            return result;
        }
        async Task AssertPendingAsync()
        {
            await using var db = runtime.OpenDatabase();
            Assert.Empty(await db.ClientCaseDocuments.ToArrayAsync()); Assert.Empty(await db.StorageProductBindings.ToArrayAsync());
            Assert.Empty(await db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
            Assert.Null((await db.ClientCaseProceduralDeadlines.SingleAsync(d => d.ClientCaseId == caseId)).ClientCaseDocumentId);
        }
        async Task MakeQueueDueAsync()
        {
            await using var db = runtime.OpenDatabase();
            await db.StorageOperations.Where(o => o.Status != StorageOperationStatuses.Succeeded && o.Status != StorageOperationStatuses.DeadLetter)
                .ExecuteUpdateAsync(update => update.SetProperty(o => o.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
        }
        endpoint.FailCapacity = failure == "unknown-quota";
        if (failure == "low-quota") endpoint.UsedCapacityBytes = endpoint.TotalCapacityBytes;
        var first = await SyncAsync();
        Assert.True(File.Exists(Path.Combine(runtime.Root, SyntheticYandexHostingStartup.CourtFile + ".read")));
        Assert.Equal(1, first.CreatedProceduralDeadlines); Assert.Equal(0, first.CreatedCourtActDocuments);
        await AssertPendingAsync();
        int deadlineId; Guid attemptId; byte[] frozenBytes; string frozenJson; string actorName;
        await using (var db = runtime.OpenDatabase())
        {
            var deadline = await db.ClientCaseProceduralDeadlines.SingleAsync(d => d.ClientCaseId == caseId);
            deadlineId = deadline.Id; deadline.DueAt = new DateTime(2027, 2, 7); deadline.Notes = "Ручная проверка срока";
            var attempt = await db.CourtDocumentSourceAttempts.SingleAsync();
            attemptId = attempt.Id; frozenJson = attempt.FrozenSnapshotJson;
            frozenBytes = (await db.CourtDocumentSourceContents.SingleAsync()).Bytes;
            actorName = (await db.Users.SingleAsync(u => u.Email == runtime.Email)).FullName;
            Assert.Single(await db.TaskJournalItems.Where(t => t.ClientCaseId == caseId).ToArrayAsync());
            if (failure is "low-quota" or "unknown-quota") Assert.Empty(await db.StorageObjects.ToArrayAsync());
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, endpoint.PutAttempts);
        endpoint.FailCapacity = false; endpoint.UsedCapacityBytes = 0;
        await runtime.SetCourtSourceAsync(Source("Новая сводка после принятия"));
        var pending = await SyncAsync();
        Assert.Equal(0, pending.CreatedProceduralDeadlines); Assert.Equal(0, pending.CreatedCourtActDocuments);
        Assert.Equal(1, pending.PendingCourtActDocuments); await AssertPendingAsync();
        endpoint.FailUploadLink = failure == "unavailable";
        endpoint.DropNextPutResponse = failure == "lost-ack";
        if (failure is "unavailable" or "lost-ack")
        {
            var failed = await runtime.RunOwnedYandexWorkerAsync();
            Assert.True(failed.Retried + failed.Unknown > 0);
            Assert.Equal(failure == "lost-ack" ? 1 : 0, endpoint.PutCount);
            await AssertPendingAsync(); endpoint.FailUploadLink = false;
        }
        await runtime.RestartAsync(TimeSpan.Zero);
        for (var retry = 0; retry < 5; retry++)
        {
            await MakeQueueDueAsync();
            Assert.Equal(0, (await runtime.RunOwnedYandexWorkerAsync()).DeadLettered);
            await using var db = runtime.OpenDatabase();
            if (await db.ClientCaseDocuments.AnyAsync()) break;
        }
        await using (var db = runtime.OpenDatabase())
        {
            var document = await db.ClientCaseDocuments.SingleAsync();
            Assert.Equal(actorName, document.UploadedByName); Assert.Equal("sudrf", document.SourceType);
            Assert.Equal(SyntheticYandexHostingStartup.CourtUrl, document.SourceUrl);
            var deadline = await db.ClientCaseProceduralDeadlines.SingleAsync();
            Assert.Equal(deadlineId, deadline.Id); Assert.Equal(new DateTime(2027, 2, 7), deadline.DueAt);
            Assert.Equal("Ручная проверка срока", deadline.Notes); Assert.Equal(document.Id, deadline.ClientCaseDocumentId);
            var retained = await db.CourtDocumentSourceAttempts.SingleAsync();
            Assert.Equal(attemptId, retained.Id); Assert.Equal(frozenJson, retained.FrozenSnapshotJson);
            var binding = await db.StorageProductBindings.SingleAsync(); Assert.Equal((long)document.Id, binding.SourceId);
            var replicas = await db.StorageReplicas.Where(r => r.StorageObjectGeneration.StorageObjectId == binding.StorageObjectId).ToArrayAsync();
            Assert.Equal(2, replicas.Length); Assert.All(replicas, r => Assert.Equal(StorageReplicaStatuses.Available, r.Status));
            var cloud = Assert.Single(replicas, r => r.ProviderKey == "yandex-disk");
            var bytes = await runtime.Client.GetByteArrayAsync($"/api/v2/cases/{caseId}/documents/{document.Id}/download");
            Assert.Equal(frozenBytes, bytes); Assert.DoesNotContain("Новая сводка", Encoding.UTF8.GetString(bytes));
            Assert.Equal(bytes, endpoint.GetBytes(cloud.Locator[(cloud.Locator.IndexOf('/') + 1)..]));
            Assert.Single(await db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" && a.Action == "Created").ToArrayAsync());
            Assert.Equal("false", await db.SystemSettings.Where(s => s.Key == "notifications.email.enabled").Select(s => s.Value).SingleAsync());
        }
        await runtime.RestartAsync(TimeSpan.Zero);
        var replay = await SyncAsync();
        Assert.Equal(0, replay.CreatedCourtActDocuments); Assert.Equal(0, replay.PendingCourtActDocuments);
        Assert.Equal(0, replay.CreatedProceduralDeadlines);
        await MakeQueueDueAsync(); Assert.Equal(0, (await runtime.RunOwnedYandexWorkerAsync()).Claimed);
        await using (var db = runtime.OpenDatabase())
        {
            Assert.Single(await db.ClientCaseDocuments.ToArrayAsync()); Assert.Single(await db.StorageProductBindings.ToArrayAsync());
            Assert.Single(await db.TaskJournalItems.Where(t => t.ClientCaseId == caseId).ToArrayAsync());
            Assert.Single(await db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishCaseDocument
                && o.Status == StorageOperationStatuses.Succeeded).ToArrayAsync());
        }
        Assert.Equal(1, endpoint.PutCount); Assert.Equal(1, endpoint.PutAttempts);
        runtime.AssertNoServerErrors();
    }

    [Theory]
    [InlineData(false, "normal")]
    [InlineData(true, "normal")]
    [InlineData(false, "unavailable")]
    [InlineData(true, "unavailable")]
    [InlineData(false, "lost-ack")]
    [InlineData(true, "lost-ack")]
    [InlineData(false, "low-quota")]
    [InlineData(true, "low-quota")]
    public async Task RealHttpGeneratedYandexCopiesPublishFrozenDocumentOnceAcrossRestart(bool workOrder, string failure)
    {
        await using var endpoint = await LoopbackYandexDiskEndpoint.StartAsync();
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(template: sqliteTemplate, workerEnabled: true, uploadCapacityChecks: true, yandexCopy: endpoint);
        using (var forbidden = await runtime.Client.PostAsync(SyntheticYandexHostingStartup.WorkerPath, null))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var caseId = await LoginAndCreateCaseAsync(runtime);
        await using (var seed = runtime.OpenDatabase())
        {
            await seed.ClientCases.Where(c => c.Id == caseId).ExecuteUpdateAsync(update => update.SetProperty(c => c.CaseType, "Гражданское"));
            if (workOrder) seed.ClientCaseWorkOrders.Add(new ClientCaseWorkOrder { ClientCaseId = caseId, Title = "Owned original Yandex order", Status = "approved" });
            else seed.SystemSettings.Add(new SystemSetting { Key = "cases.documentTemplates", Value = "{\"templates\":[{\"key\":\"custom\",\"label\":\"Owned original Yandex template\",\"caseTypes\":[\"Гражданское\"]}]}" });
            await seed.SaveChangesAsync();
        }
        var key = Guid.NewGuid();
        var path = $"/api/v2/cases/{caseId}/" + (workOrder ? "work-order/generate-document" : "documents/generate-template");
        async Task<HttpResponseMessage> GenerateAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Add("X-Upload-Key", key.ToString("D"));
            if (!workOrder) request.Content = JsonContent.Create(new { templateKey = "custom", documentType = "Иск", status = "Подготовлен" });
            return await runtime.Client.SendAsync(request);
        }
        async Task AssertNoPublicationAsync()
        {
            await using var db = runtime.OpenDatabase();
            Assert.Empty(await db.ClientCaseDocuments.ToArrayAsync());
            Assert.Empty(await db.StorageProductBindings.ToArrayAsync());
            Assert.Empty(await db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" && a.Action == "Generated").ToArrayAsync());
        }
        async Task MakeQueueDueAsync()
        {
            await using var db = runtime.OpenDatabase();
            await db.StorageOperations.Where(o => o.Status != StorageOperationStatuses.Succeeded && o.Status != StorageOperationStatuses.DeadLetter)
                .ExecuteUpdateAsync(update => update.SetProperty(o => o.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
        }
        if (failure == "low-quota")
        {
            endpoint.UsedCapacityBytes = endpoint.TotalCapacityBytes;
            using (var rejected = await GenerateAsync()) Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            await AssertNoPublicationAsync();
            await using (var db = runtime.OpenDatabase()) Assert.Empty(await db.StorageObjects.ToArrayAsync());
            Assert.Equal(0, endpoint.PutAttempts);
            endpoint.UsedCapacityBytes = 0;
        }
        using (var pending = await GenerateAsync()) Assert.Equal(HttpStatusCode.ServiceUnavailable, pending.StatusCode);
        await AssertNoPublicationAsync();
        Assert.Equal(0, endpoint.PutAttempts);
        string frozenHash;
        await using (var db = runtime.OpenDatabase())
        {
            var generation = Assert.Single(await db.StorageObjectGenerations.ToArrayAsync());
            frozenHash = generation.Sha256;
            Assert.Single(await db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishCaseDocument).ToArrayAsync());
            await db.ClientCases.Where(c => c.Id == caseId).ExecuteUpdateAsync(update => update.SetProperty(c => c.Title, "REPLACEMENT YANDEX CASE"));
            if (workOrder) await db.ClientCaseWorkOrders.Where(o => o.ClientCaseId == caseId).ExecuteUpdateAsync(update => update
                .SetProperty(o => o.Title, "REPLACEMENT YANDEX ORDER").SetProperty(o => o.Status, "draft"));
        }
        endpoint.FailUploadLink = failure == "unavailable";
        endpoint.DropNextPutResponse = failure == "lost-ack";
        if (failure is "unavailable" or "lost-ack")
        {
            var failed = await runtime.RunOwnedYandexWorkerAsync();
            Assert.True(failed.Retried + failed.Unknown > 0);
            Assert.Equal(failure == "lost-ack" ? 1 : 0, endpoint.PutCount);
            await AssertNoPublicationAsync();
            endpoint.FailUploadLink = false;
        }
        await runtime.RestartAsync(TimeSpan.Zero);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await MakeQueueDueAsync();
            var batch = await runtime.RunOwnedYandexWorkerAsync();
            Assert.Equal(0, batch.DeadLettered);
            await using var db = runtime.OpenDatabase();
            if (await db.ClientCaseDocuments.AnyAsync()) break;
        }
        ClientCaseDocumentDto document;
        using (var receipt = await GenerateAsync())
        {
            Assert.Equal(HttpStatusCode.Created, receipt.StatusCode);
            Assert.Equal(key.ToString("D"), Assert.Single(receipt.Headers.GetValues("X-Upload-Key")));
            document = (await receipt.Content.ReadFromJsonAsync<ClientCaseDocumentDto>())!;
            Assert.Equal(1, document.Version);
            Assert.Equal(workOrder ? "Согласован" : "Подготовлен", document.Status);
        }
        var bytes = await runtime.Client.GetByteArrayAsync($"/api/v2/cases/{caseId}/documents/{document.Id}/download");
        Assert.Equal(frozenHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
        var html = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("REPLACEMENT YANDEX", html);
        if (workOrder) Assert.Contains("Owned original Yandex order", html);
        await runtime.RestartAsync(TimeSpan.Zero);
        using (var replay = await GenerateAsync())
        {
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(document.Id, (await replay.Content.ReadFromJsonAsync<ClientCaseDocumentDto>())!.Id);
        }
        await MakeQueueDueAsync();
        Assert.Equal(0, (await runtime.RunOwnedYandexWorkerAsync()).Claimed);
        await using (var db = runtime.OpenDatabase())
        {
            Assert.Single(await db.ClientCaseDocuments.ToArrayAsync());
            var binding = Assert.Single(await db.StorageProductBindings.ToArrayAsync());
            Assert.Equal((long)document.Id, binding.SourceId);
            var replicas = await db.StorageReplicas.Where(r => r.StorageObjectGeneration.StorageObjectId == binding.StorageObjectId).ToArrayAsync();
            Assert.Equal(2, replicas.Length);
            Assert.All(replicas, r => Assert.Equal(StorageReplicaStatuses.Available, r.Status));
            var cloud = Assert.Single(replicas, r => r.ProviderKey == "yandex-disk");
            Assert.Equal(bytes, endpoint.GetBytes(cloud.Locator[(cloud.Locator.IndexOf('/') + 1)..]));
            Assert.Single(await db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" && a.Action == "Generated").ToArrayAsync());
            Assert.Single(await db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishCaseDocument
                && o.Status == StorageOperationStatuses.Succeeded).ToArrayAsync());
            Assert.Equal("false", await db.SystemSettings.Where(s => s.Key == "notifications.email.enabled").Select(s => s.Value).SingleAsync());
        }
        Assert.Equal(1, endpoint.PutCount);
        Assert.Equal(1, endpoint.PutAttempts);
        runtime.AssertNoServerErrors();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealHttpGeneratedTwoCopyUnknownCapacityLeavesNoPartialPublication(bool workOrder)
    {
        await using var endpoint = await LoopbackS3Endpoint.StartAsync();
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(template: sqliteTemplate, secondCopy: endpoint, uploadCapacityChecks: true);
        var caseId = await LoginAndCreateCaseAsync(runtime);
        await using (var seed = runtime.OpenDatabase())
        {
            await seed.ClientCases.Where(c => c.Id == caseId).ExecuteUpdateAsync(update => update.SetProperty(c => c.CaseType, "Гражданское"));
            if (!workOrder) seed.SystemSettings.Add(new SystemSetting { Key = "cases.documentTemplates", Value = "{\"templates\":[{\"key\":\"custom\",\"label\":\"Owned capacity template\",\"caseTypes\":[\"Гражданское\"]}]}" });
            if (workOrder) seed.ClientCaseWorkOrders.Add(new ClientCaseWorkOrder { ClientCaseId = caseId, Title = "Owned capacity order", Status = "approved" });
            await seed.SaveChangesAsync();
        }
        var key = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/cases/{caseId}/" +
            (workOrder ? "work-order/generate-document" : "documents/generate-template"));
        request.Headers.Add("X-Upload-Key", key.ToString("D"));
        if (!workOrder) request.Content = JsonContent.Create(new { templateKey = "custom", documentType = "Иск", status = "Подготовлен" });
        using var response = await runtime.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await using var db = runtime.OpenDatabase();
        Assert.Empty(await db.ClientCaseDocuments.ToArrayAsync());
        Assert.Empty(await db.StorageObjects.ToArrayAsync());
        Assert.Empty(await db.StorageProductBindings.ToArrayAsync());
        Assert.Empty(await db.StorageCapacityReservations.Where(r => r.ProductContextJson != null).ToArrayAsync());
        Assert.Empty(await db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" && a.Action == "Generated").ToArrayAsync());
        using var status = await runtime.Client.GetAsync($"/api/v2/cases/{caseId}/documents/uploads?uploadKeys={key:D}");
        var proof = Assert.Single((await status.Content.ReadFromJsonAsync<PayPlanner.Api.Services.Storage.CaseDocumentUploadState[]>())!);
        Assert.Equal("unknown", proof.Status);
        Assert.Equal(0, endpoint.PutCount);
        runtime.AssertNoServerErrors();
    }

    [Theory]
    [InlineData("generated-edit")]
    [InlineData("generated-modal")]
    [InlineData("generated-order")]
    public async Task RealBrowserGeneratedLostAckReplaysOriginalAttemptWithoutDuplicate(string flow)
    {
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(template: sqliteTemplate, uploadCapacityChecks: true);
        var caseId = await LoginAndCreateCaseAsync(runtime);
        int clientId;
        await using (var seed = runtime.OpenDatabase())
        {
            var clientCase = await seed.ClientCases.SingleAsync(c => c.Id == caseId);
            clientId = Assert.IsType<int>(clientCase.ClientId);
            clientCase.CaseType = "Гражданское";
            clientCase.Title = "Synthetic generated browser case";
            seed.ClientCaseWorkOrders.Add(new ClientCaseWorkOrder { ClientCaseId = caseId, Title = "Owned browser order", Status = "approved" });
            seed.SystemSettings.Add(new SystemSetting { Key = "cases.documentTemplates", Value = "{\"templates\":[{\"key\":\"custom\",\"label\":\"Owned browser template\",\"caseTypes\":[\"Гражданское\"]}]}" });
            await seed.SaveChangesAsync();
        }
        await runtime.CheckCaseRetirementBrowserAsync(caseId, Guid.NewGuid(), true, flow, clientId, generatedDocuments: true);
        await using var db = runtime.OpenDatabase();
        var document = Assert.Single(await db.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(1, document.Version);
        Assert.Single(await db.StorageProductBindings.ToArrayAsync());
        Assert.Single(await db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" && a.Action == "Generated").ToArrayAsync());
        if (flow == "generated-modal") Assert.NotEqual("Unsaved generated strategy", (await db.ClientCases.SingleAsync(c => c.Id == caseId)).Strategy);
        Assert.Equal("false", await db.SystemSettings.Where(s => s.Key == "notifications.email.enabled").Select(s => s.Value).SingleAsync());
        runtime.AssertNoServerErrors();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealHttpRetirementReturnsProofWithoutDeletingPublishedDocumentOrResendingFile(bool pending)
    {
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(template: sqliteTemplate, uploadCapacityChecks: true);
        using (var anonymous = await runtime.Client.PostAsync($"/api/v2/cases/1/documents/uploads/{Guid.NewGuid():D}/retire", null))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var caseId = await LoginAndCreateCaseAsync(runtime);
        var bytes = Encoding.UTF8.GetBytes("Owned retirement HTTP bytes");
        var key = Guid.NewGuid(); var path = $"/api/v2/cases/{caseId}/documents";
        string? retainedPath = null;
        if (pending)
        {
            // A durable primary reservation can outlive a lost provider ACK
            // before catalog commit. Seed exactly that owned synthetic state;
            // S3 admission rejection without intent is tested separately below.
            await using var seed = runtime.OpenDatabase();
            var actor = await seed.Users.SingleAsync(u => u.Email == runtime.Email);
            retainedPath = Path.Combine(runtime.Root, "retirement-owned-pending.bin");
            await File.WriteAllBytesAsync(retainedPath, bytes);
            var resource = System.Text.Json.JsonSerializer.Serialize(new string?[] { "local", null });
            var capacity = PayPlanner.Api.Services.Storage.LocalStorageCapacityProbe.Observe(runtime.Root, resource, 0);
            Assert.NotNull(capacity.AvailableBytes);
            Assert.NotEmpty(capacity.GroupId);
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            var intent = new
            {
                CaseId = caseId,
                ActorId = actor.Id,
                ActorName = actor.FullName,
                UploadKey = key,
                RequestedDocumentKey = (string?)null,
                DocumentKey = Guid.NewGuid().ToString("N"),
                Version = 1,
                FileName = "synthetic-evidence.bin",
                ContentType = "application/octet-stream",
                DocumentType = "Документ",
                Status = "Подготовлен",
                SizeBytes = (long)bytes.Length,
                Sha256 = sha,
                UploadedAt = DateTime.UtcNow
            };
            seed.StorageCapacityReservations.Add(new StorageCapacityReservation
            {
                IdempotencyKey = PayPlanner.Api.Services.Storage.StorageObjectService.GetCapacityReservationKey($"case-document:{caseId}:{actor.Id}:{key:N}"),
                ResourceKey = resource,
                GroupId = capacity.GroupId,
                BindingHash = sha,
                Bytes = bytes.Length,
                ObjectId = Guid.NewGuid(),
                LocalLocator = retainedPath,
                ProductContextJson = System.Text.Json.JsonSerializer.Serialize(intent,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            });
            await seed.SaveChangesAsync();
        }
        else
        {
            using var upload = await UploadAsync(runtime, path, bytes, key);
            Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        }
        using (var invalid = await runtime.Client.PostAsync($"{path}/uploads/{Guid.Empty:D}/retire", null))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var unknown = await runtime.Client.PostAsync($"{path}/uploads/{Guid.NewGuid():D}/retire", null))
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        for (var i = 0; i < 2; i++)
        {
            using var response = await runtime.Client.PostAsync($"{path}/uploads/{key:D}/retire", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
            var state = (await response.Content.ReadFromJsonAsync<PayPlanner.Api.Services.Storage.CaseDocumentUploadState>())!;
            Assert.Equal(key, state.UploadKey); Assert.Equal(pending ? "retired" : "uploaded", state.Status);
            Assert.Equal(pending, state.DocumentId is null); Assert.Equal(pending, state.Sha256 is null);
        }
        using (var status = await runtime.Client.GetAsync($"{path}/uploads?uploadKeys={key:D}"))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode); Assert.True(status.Headers.CacheControl?.NoStore);
            Assert.Equal(pending ? "retired" : "uploaded", Assert.Single(
                (await status.Content.ReadFromJsonAsync<PayPlanner.Api.Services.Storage.CaseDocumentUploadState[]>())!).Status);
        }
        if (pending)
        {
            using var replay = await UploadAsync(runtime, path, bytes, key);
            Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        }
        await using var db = runtime.OpenDatabase();
        Assert.Equal(pending ? 1 : 0, await db.CaseDocumentUploadRetirements.CountAsync());
        Assert.Equal(pending ? 0 : 1, await db.ClientCaseDocuments.CountAsync());
        Assert.Equal(pending ? 0 : 1, await db.StorageProductBindings.CountAsync());
        Assert.Equal(pending ? 0 : 1, await db.StorageObjects.CountAsync());
        if (retainedPath is not null) Assert.Equal(bytes, await File.ReadAllBytesAsync(retainedPath));
        Assert.Equal("false", await db.SystemSettings.Where(s => s.Key == "notifications.email.enabled").Select(s => s.Value).SingleAsync());
        runtime.AssertNoServerErrors();
    }

    [Fact]
    public async Task RealHttpCapacityRejectionWithoutSavedIntentCannotBeRetired()
    {
        await using var endpoint = await LoopbackS3Endpoint.StartAsync();
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(template: sqliteTemplate, secondCopy: endpoint, uploadCapacityChecks: true);
        var caseId = await LoginAndCreateCaseAsync(runtime);
        var key = Guid.NewGuid(); var path = $"/api/v2/cases/{caseId}/documents";
        using (var upload = await UploadAsync(runtime, path, Encoding.UTF8.GetBytes("Owned unknown capacity"), key))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, upload.StatusCode);
        await using var db = runtime.OpenDatabase();
        Assert.Empty(await db.StorageOperations.AsNoTracking().Where(o => o.OperationType == "commit").ToArrayAsync());
        Assert.Empty(await db.StorageCapacityReservations.AsNoTracking().Where(r => r.ProductContextJson != null).ToArrayAsync());
        using var response = await runtime.Client.PostAsync($"{path}/uploads/{key:D}/retire", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await db.CaseDocumentUploadRetirements.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Equal(0, endpoint.PutCount);
        runtime.AssertNoServerErrors();
    }

    [Fact]
    public async Task RealMultipartRequiresIdentityAndReplaysCaseAndVersionWithoutDuplicatePublication()
    {
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(template: sqliteTemplate, uploadCapacityChecks: true);
        var caseId = await LoginAndCreateCaseAsync(runtime);
        var bytes = Encoding.UTF8.GetBytes("Synthetic case document: точные байты");
        var path = $"/api/v2/cases/{caseId}/documents";
        using (var missingIdentity = await UploadAsync(runtime, path, bytes, null))
            Assert.Equal(HttpStatusCode.BadRequest, missingIdentity.StatusCode);
        var key = Guid.NewGuid();
        ClientCaseDocumentDto original;
        using (var uploaded = await UploadAsync(runtime, path, bytes, key))
        {
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            original = (await uploaded.Content.ReadFromJsonAsync<ClientCaseDocumentDto>())!;
            Assert.Equal(1, original.Version);
        }
        using (var replay = await UploadAsync(runtime, path, bytes, key))
        {
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(original.Id, (await replay.Content.ReadFromJsonAsync<ClientCaseDocumentDto>())!.Id);
        }
        using (var receipt = await runtime.Client.GetAsync($"{path}/uploads?uploadKeys={key:D}"))
        {
            Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
            Assert.True(receipt.Headers.CacheControl!.NoStore);
            var state = Assert.Single((await receipt.Content.ReadFromJsonAsync<PayPlanner.Api.Services.Storage.CaseDocumentUploadState[]>())!);
            Assert.Equal("uploaded", state.Status);
            Assert.Equal(original.Id, state.DocumentId);
            Assert.Equal(original.DocumentKey, state.DocumentKey);
            Assert.Equal("Документ", state.DocumentType);
            Assert.Equal("Подготовлен", state.RequestedStatus);
            Assert.Null(state.RequestedDocumentKey);
            Assert.Equal("application/octet-stream", state.ContentType);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), state.Sha256);
        }
        using (var unknown = await runtime.Client.GetAsync($"{path}/uploads?uploadKeys={Guid.NewGuid():D}"))
        {
            Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
            var json = System.Text.Json.JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
            using (json)
            {
                Assert.Equal("unknown", json.RootElement[0].GetProperty("status").GetString());
                Assert.Equal(System.Text.Json.JsonValueKind.Null, json.RootElement[0].GetProperty("fileName").ValueKind);
                Assert.Equal(System.Text.Json.JsonValueKind.Null, json.RootElement[0].GetProperty("sha256").ValueKind);
            }
        }
        using (var invalid = await runtime.Client.GetAsync($"{path}/uploads"))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var conflict = await UploadAsync(runtime, path, Encoding.UTF8.GetBytes("different bytes"), key))
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var versionKey = Guid.NewGuid();
        var versionPath = $"/api/case-documents/{original.Id}/versions";
        ClientCaseDocumentDto version;
        using (var uploaded = await UploadAsync(runtime, versionPath, bytes, versionKey))
        {
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            version = (await uploaded.Content.ReadFromJsonAsync<ClientCaseDocumentDto>())!;
            Assert.Equal(2, version.Version);
            Assert.Equal(original.DocumentKey, version.DocumentKey);
        }
        using (var replay = await UploadAsync(runtime, versionPath, bytes, versionKey))
        {
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(version.Id, (await replay.Content.ReadFromJsonAsync<ClientCaseDocumentDto>())!.Id);
        }
        await using (var db = runtime.OpenDatabase())
        {
            Assert.Equal(2, await db.ClientCaseDocuments.CountAsync());
            Assert.Equal(2, await db.StorageProductBindings.CountAsync());
            Assert.Equal(2, await db.StorageObjects.CountAsync());
            Assert.Equal(2, await db.ObjectAuditEvents.CountAsync(a => a.ObjectType == "client-case-document"));
            // Force the public endpoint through the managed copy rather than the compatibility file.
            foreach (var row in await db.ClientCaseDocuments.ToArrayAsync())
            {
                var compatibility = Path.Combine(runtime.Root, "files", "case-documents", row.StoragePath.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(compatibility));
                File.Delete(compatibility);
                Assert.False(File.Exists(compatibility));
            }
        }
        using var download = await runtime.Client.GetAsync($"/api/case-documents/{version.Id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        runtime.AssertNoServerErrors();
    }

    [Fact]
    public async Task RealMultipartAcceptsExactFileLimitAndRejectsOneByteOverWithoutPublication()
    {
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(template: sqliteTemplate, uploadCapacityChecks: true);
        var caseId = await LoginAndCreateCaseAsync(runtime);
        var path = $"/api/v2/cases/{caseId}/documents";
        var bytes = new byte[25 * 1024 * 1024];
        bytes[0] = 42;
        bytes[^1] = 17;
        using (var accepted = await UploadAsync(runtime, path, bytes, Guid.NewGuid()))
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        using (var rejected = await UploadAsync(runtime, path, new byte[bytes.Length + 1], Guid.NewGuid()))
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        await using var db = runtime.OpenDatabase();
        Assert.Equal(1, await db.ClientCaseDocuments.CountAsync());
        Assert.Equal(1, await db.StorageProductBindings.CountAsync());
        Assert.Equal(1, await db.StorageObjects.CountAsync());
        runtime.AssertNoServerErrors();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RealHttpGeneratedDocumentReplaysExactReceiptAcrossRestart(bool workOrder, bool restart)
    {
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(template: sqliteTemplate, uploadCapacityChecks: true);
        var caseId = await LoginAndCreateCaseAsync(runtime);
        await using (var seed = runtime.OpenDatabase())
        {
            await seed.ClientCases.Where(c => c.Id == caseId).ExecuteUpdateAsync(update => update.SetProperty(c => c.CaseType, "Гражданское"));
            if (workOrder) seed.ClientCaseWorkOrders.Add(new ClientCaseWorkOrder { ClientCaseId = caseId, Title = "Owned original order", Status = "approved" });
            else seed.SystemSettings.Add(new SystemSetting { Key = "cases.documentTemplates", Value = "{\"templates\":[{\"key\":\"custom\",\"label\":\"Owned original template\",\"caseTypes\":[\"Гражданское\"]}]}" });
            await seed.SaveChangesAsync();
        }
        var path = $"/api/v2/cases/{caseId}/" + (workOrder ? "work-order/generate-document" : "documents/generate-template");
        var key = Guid.NewGuid();
        async Task<HttpResponseMessage> GenerateAsync(Guid? attempt)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            if (!workOrder) request.Content = JsonContent.Create(new { templateKey = "custom", documentType = "Иск", status = "Подготовлен" });
            if (attempt.HasValue) request.Headers.Add("X-Upload-Key", attempt.Value.ToString("D"));
            return await runtime.Client.SendAsync(request);
        }
        using (var invalid = await GenerateAsync(null)) Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        ClientCaseDocumentDto original;
        byte[] originalBytes;
        using (var generated = await GenerateAsync(key))
        {
            Assert.Equal(HttpStatusCode.Created, generated.StatusCode);
            Assert.Equal(key.ToString("D"), Assert.Single(generated.Headers.GetValues("X-Upload-Key")));
            original = (await generated.Content.ReadFromJsonAsync<ClientCaseDocumentDto>())!;
            Assert.Equal(1, original.Version);
            originalBytes = await runtime.Client.GetByteArrayAsync($"/api/v2/cases/{caseId}/documents/{original.Id}/download");
        }
        await using (var change = runtime.OpenDatabase())
        {
            await change.ClientCases.Where(c => c.Id == caseId).ExecuteUpdateAsync(update => update.SetProperty(c => c.Title, "Replacement case"));
            if (workOrder) await change.ClientCaseWorkOrders.Where(o => o.ClientCaseId == caseId).ExecuteUpdateAsync(update => update.SetProperty(o => o.Title, "Replacement order").SetProperty(o => o.Status, "draft"));
        }
        if (restart) await runtime.RestartAsync(TimeSpan.Zero);
        using (var replay = await GenerateAsync(key))
        {
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            var saved = (await replay.Content.ReadFromJsonAsync<ClientCaseDocumentDto>())!;
            Assert.Equal(original.Id, saved.Id);
            Assert.Equal(original.Status, saved.Status);
            Assert.Equal(original.DocumentKey, saved.DocumentKey);
            Assert.Equal(originalBytes, await runtime.Client.GetByteArrayAsync($"/api/v2/cases/{caseId}/documents/{saved.Id}/download"));
        }
        using (var status = await runtime.Client.GetAsync($"/api/v2/cases/{caseId}/documents/uploads?uploadKeys={key:D}"))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            Assert.True(status.Headers.CacheControl?.NoStore);
            var proof = Assert.Single((await status.Content.ReadFromJsonAsync<PayPlanner.Api.Services.Storage.CaseDocumentUploadState[]>())!);
            Assert.Equal("uploaded", proof.Status);
            Assert.Equal(original.Id, proof.DocumentId);
            Assert.True(proof.Generation!.SourceVerified);
            Assert.Equal(workOrder ? "work-order" : "template", proof.Generation.Kind);
        }
        await using var db = runtime.OpenDatabase();
        Assert.Single(await db.ClientCaseDocuments.ToArrayAsync());
        Assert.Single(await db.StorageProductBindings.ToArrayAsync());
        Assert.Single(await db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" && a.Action == "Generated").ToArrayAsync());
        Assert.Equal(2, await db.ObjectAuditEvents.CountAsync(a => a.ObjectType == "client-case-document" && a.Action == "Downloaded"));
        Assert.Equal("false", await db.SystemSettings.Where(s => s.Key == "notifications.email.enabled").Select(s => s.Value).SingleAsync());
        runtime.AssertNoServerErrors();
    }

    private static async Task<int> LoginAndCreateCaseAsync(SyntheticWritableBackendProcess runtime)
    {
        using var login = await runtime.Client.PostAsJsonAsync("/api/auth/login", new { email = runtime.Email, password = runtime.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var authentication = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        runtime.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authentication.Token);
        await using var db = runtime.OpenDatabase();
        var client = new Client { Name = "Synthetic managed HTTP client", ResponsibleUserId = authentication.User.Id };
        var clientCase = new ClientCase { Client = client, Title = "Synthetic managed HTTP case", ResponsibleUserId = authentication.User.Id };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        return clientCase.Id;
    }

    private static async Task<HttpResponseMessage> UploadAsync(SyntheticWritableBackendProcess runtime,
        string path, byte[] bytes, Guid? key)
    {
        using var body = new MultipartFormDataContent();
        using var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Add(file, "file", "synthetic-evidence.bin");
        body.Add(new StringContent("Документ"), "documentType");
        body.Add(new StringContent("Подготовлен"), "status");
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body };
        if (key.HasValue) request.Headers.Add("X-Upload-Key", key.Value.ToString("D"));
        return await runtime.Client.SendAsync(request);
    }
}
