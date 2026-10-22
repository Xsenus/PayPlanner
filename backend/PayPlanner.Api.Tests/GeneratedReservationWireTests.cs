using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.LegalIntegrations.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedCaseDocumentWireTests
{
    [Theory]
    [InlineData("template")]
    [InlineData("work-order")]
    [InlineData("court")]
    public async Task RealHttpGeneratedReservationLostLocalAckRecoversFrozenBytesWithYandexGateAcrossRestart(string kind)
    {
        SudrfCourtCaseData Court(string summary) => new()
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
            uploadCapacityChecks: true, yandexCopy: endpoint, courtSource: kind == "court" ? Court("Original court summary") : null,
            localAckFault: true);
        var caseId = await LoginAndCreateCaseAsync(runtime);
        await using (var seed = runtime.OpenDatabase())
        {
            var clientCase = await seed.ClientCases.SingleAsync(c => c.Id == caseId);
            clientCase.CaseType = "Гражданское";
            if (kind == "court") clientCase.CourtUrl = SyntheticYandexHostingStartup.CourtUrl;
            if (kind == "work-order") seed.ClientCaseWorkOrders.Add(new ClientCaseWorkOrder
            { ClientCaseId = caseId, Title = "Original reserved work order", Status = "approved" });
            if (kind == "template") seed.SystemSettings.Add(new SystemSetting
            {
                Key = "cases.documentTemplates",
                Value = "{\"templates\":[{\"key\":\"custom\",\"label\":\"Original reserved template\",\"caseTypes\":[\"Гражданское\"]}]}"
            });
            await seed.SaveChangesAsync();
        }
        await File.WriteAllTextAsync(Path.Combine(runtime.Root, SyntheticYandexHostingStartup.LocalFaultFile), "owned");
        var key = Guid.NewGuid();
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/cases/{caseId}/" + (kind switch
        { "court" => "court-sync/run", "work-order" => "work-order/generate-document", _ => "documents/generate-template" })))
        {
            request.Headers.Add("X-Upload-Key", key.ToString("D"));
            if (kind == "template") request.Content = JsonContent.Create(new { templateKey = "custom", documentType = "Иск", status = "Подготовлен" });
            using var response = await runtime.Client.SendAsync(request);
            Assert.Equal(kind == "court" ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, response.StatusCode);
            if (kind == "court")
            {
                var report = (await response.Content.ReadFromJsonAsync<CasesV2Controller.ClientCaseCourtSyncRunDto>())!;
                Assert.True(report.SyncedFromExternalSource); Assert.Equal(1, report.CreatedProceduralDeadlines);
                Assert.Equal(0, report.CreatedCourtActDocuments);
            }
        }
        Guid originalId; byte[] originalBytes; string originalJson;
        var local = System.Text.Json.JsonSerializer.Serialize(new string?[] { "local", null });
        await using (var db = runtime.OpenDatabase())
        {
            Assert.Empty(await db.StorageObjects.ToArrayAsync()); Assert.Empty(await db.ClientCaseDocuments.ToArrayAsync());
            Assert.Empty(await db.StorageProductBindings.ToArrayAsync());
            var row = await db.StorageCapacityReservations.SingleAsync(r => r.ResourceKey == local && r.ProductContextJson != null);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status); originalId = row.ObjectId; originalJson = row.ProductContextJson!;
            var root = Path.GetFullPath(runtime.Configuration["StorageOrchestration:LocalRoot"]!);
            var path = Path.GetFullPath(Path.Combine(root, row.LocalLocator));
            Assert.StartsWith(root + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
            originalBytes = await File.ReadAllBytesAsync(path); Assert.Equal(row.Bytes, originalBytes.LongLength);
            var clientCase = await db.ClientCases.SingleAsync(c => c.Id == caseId); clientCase.Title = "REPLACEMENT AFTER LOCAL ACK";
            if (kind == "work-order") (await db.ClientCaseWorkOrders.SingleAsync()).Title = "REPLACEMENT AFTER LOCAL ACK";
            if (kind == "court") Assert.Null((await db.ClientCaseProceduralDeadlines.SingleAsync()).ClientCaseDocumentId);
            await db.SaveChangesAsync();
        }
        Assert.False(File.Exists(Path.Combine(runtime.Root, SyntheticYandexHostingStartup.LocalFaultFile)));
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(runtime.Root, SyntheticYandexHostingStartup.LocalPutFile)));
        Assert.Equal(0, endpoint.PutAttempts);
        if (kind == "court") await runtime.SetCourtSourceAsync(Court("REPLACEMENT AFTER LOCAL ACK"));
        endpoint.FailUploadLink = true;
        await runtime.RestartAsync(TimeSpan.Zero);
        Assert.Equal(0, (await runtime.RunOwnedYandexWorkerAsync()).DeadLettered);
        await using (var db = runtime.OpenDatabase())
        {
            Assert.Empty(await db.ClientCaseDocuments.ToArrayAsync()); Assert.Empty(await db.StorageProductBindings.ToArrayAsync());
            Assert.Empty(await db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
            Assert.Equal(originalId, Assert.Single(await db.StorageObjects.ToArrayAsync()).Id);
            var reservation = await db.StorageCapacityReservations.SingleAsync(r => r.ResourceKey == local && r.ProductContextJson != null);
            Assert.Equal(originalJson, reservation.ProductContextJson); Assert.Equal(1, reservation.GeneratedRecoveryAttemptCount);
            Assert.Equal(StorageCapacityReservationStatuses.Released, reservation.Status);
            if (kind == "court") Assert.Null((await db.ClientCaseProceduralDeadlines.SingleAsync()).ClientCaseDocumentId);
        }
        endpoint.FailUploadLink = false;
        await runtime.RestartAsync(TimeSpan.Zero);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using (var db = runtime.OpenDatabase())
                await db.StorageOperations.Where(o => o.Status != StorageOperationStatuses.Succeeded && o.Status != StorageOperationStatuses.DeadLetter)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
            Assert.Equal(0, (await runtime.RunOwnedYandexWorkerAsync()).DeadLettered);
            await using var proof = runtime.OpenDatabase(); if (await proof.ClientCaseDocuments.AnyAsync()) break;
        }
        await using (var db = runtime.OpenDatabase())
        {
            var document = Assert.Single(await db.ClientCaseDocuments.ToArrayAsync());
            var binding = Assert.Single(await db.StorageProductBindings.ToArrayAsync()); Assert.Equal(originalId, binding.StorageObjectId);
            Assert.Equal((long)document.Id, binding.SourceId);
            var bytes = await runtime.Client.GetByteArrayAsync($"/api/v2/cases/{caseId}/documents/{document.Id}/download");
            Assert.Equal(originalBytes, bytes); Assert.DoesNotContain("REPLACEMENT AFTER LOCAL ACK", Encoding.UTF8.GetString(bytes));
            var replicas = await db.StorageReplicas.Where(r => r.StorageObjectGeneration.StorageObjectId == originalId).ToArrayAsync();
            Assert.Equal(2, replicas.Length); Assert.All(replicas, r => Assert.Equal(StorageReplicaStatuses.Available, r.Status));
            var cloud = Assert.Single(replicas, r => r.ProviderKey == "yandex-disk");
            Assert.Equal(bytes, endpoint.GetBytes(cloud.Locator[(cloud.Locator.IndexOf('/') + 1)..]));
            var audits = await db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync();
            Assert.Equal(2, audits.Length);
            var audit = Assert.Single(audits, a => a.Action == (kind == "court" ? "Created" : "Generated"));
            Assert.Equal(document.Id, audit.ObjectId);
            Assert.Equal(document.Id, Assert.Single(audits, a => a.Action == "Downloaded").ObjectId);
            Assert.Equal(0, await db.Payments.CountAsync() + await db.Invoices.CountAsync() + await db.Acts.CountAsync() + await db.Contracts.CountAsync());
            if (kind == "court")
            {
                Assert.Equal(document.Id, (await db.ClientCaseProceduralDeadlines.SingleAsync()).ClientCaseDocumentId);
                Assert.Single(await db.TaskJournalItems.Where(t => t.ClientCaseId == caseId).ToArrayAsync());
            }
            Assert.Equal("false", await db.SystemSettings.Where(s => s.Key == "notifications.email.enabled").Select(s => s.Value).SingleAsync());
        }
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(runtime.Root, SyntheticYandexHostingStartup.LocalPutFile)));
        Assert.Equal(1, endpoint.PutCount); Assert.Equal(1, endpoint.PutAttempts);
        Assert.Equal(0, (await runtime.RunOwnedYandexWorkerAsync()).Claimed);
        runtime.AssertNoServerErrors();
    }
}
