using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedCaseDocumentWireTests
{
    [Theory]
    [InlineData("invoice", false, false)]
    [InlineData("invoice", true, false)]
    [InlineData("act", false, false)]
    [InlineData("act", true, false)]
    [InlineData("contract", false, false)]
    [InlineData("contract", true, false)]
    [InlineData("invoice", false, true)]
    [InlineData("act", false, true)]
    [InlineData("contract", false, true)]
    public async Task RealBrowserFinancialLostReadyAcknowledgementDiscoversOriginalWinnerWithoutNewPost(string kind, bool modal, bool editor)
    {
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(uploadCapacityChecks: true);
        var caseId = await LoginAndCreateCaseAsync(runtime); int clientId;
        await using (var seed = runtime.OpenDatabase())
        {
            var clientCase = await seed.ClientCases.SingleAsync(c => c.Id == caseId);
            clientId = Assert.IsType<int>(clientCase.ClientId); clientCase.Title = "Synthetic financial browser case";
            seed.ClientCaseWorkOrders.Add(new ClientCaseWorkOrder
            {
                ClientCaseId = caseId,
                Title = "Original lost response order",
                Status = "approved",
                Currency = "RUB",
                TotalAmount = 35m,
                Items = [new ClientCaseWorkOrderItem { ServiceName = "Original lost response service", Quantity = 2m,
                    UnitPrice = 17.5m, SortOrder = 1 }]
            });
            await seed.SaveChangesAsync();
        }
        await runtime.CheckCaseRetirementBrowserAsync(caseId, Guid.NewGuid(), true,
            $"financial-{kind}-{(editor ? "editor" : modal ? "modal" : "edit")}-lost", clientId, financialDocuments: true);
        await using var proof = runtime.OpenDatabase();
        var document = Assert.Single(await proof.ClientCaseDocuments.ToArrayAsync());
        var attempt = Assert.Single(await proof.GeneratedFinancialDocumentAttempts.ToArrayAsync());
        Assert.Equal(document.Id, attempt.PublishedDocumentId);
        Assert.Equal(kind == "invoice" ? 2 : 1,
            await proof.Payments.CountAsync() + await proof.Invoices.CountAsync() + await proof.Acts.CountAsync() + await proof.Contracts.CountAsync());
        Assert.Single(await proof.StorageProductBindings.ToArrayAsync());
        Assert.Equal(StorageReplicaStatuses.Available, Assert.Single(await proof.StorageReplicas.ToArrayAsync()).Status);
        Assert.Single(await proof.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" && a.Action == "Generated").ToArrayAsync());
        Assert.Equal((await proof.GeneratedFinancialDocumentContents.SingleAsync()).Bytes,
            await File.ReadAllBytesAsync(Path.Combine(runtime.Root, "financial-browser-download.html")));
        if (modal || editor) Assert.NotEqual("Unsaved financial strategy", (await proof.ClientCases.SingleAsync(c => c.Id == caseId)).Strategy);
        Assert.Equal("false", await proof.SystemSettings.Where(s => s.Key == "notifications.email.enabled").Select(s => s.Value).SingleAsync());
        runtime.AssertNoServerErrors();
    }

    [Theory]
    [InlineData("invoice", false, false)]
    [InlineData("invoice", true, false)]
    [InlineData("act", false, false)]
    [InlineData("act", true, false)]
    [InlineData("contract", false, false)]
    [InlineData("contract", true, false)]
    [InlineData("invoice", false, true)]
    [InlineData("act", false, true)]
    [InlineData("contract", false, true)]
    public async Task RealBrowserFinancialYandexCopiesPreserveAttemptAndUnsavedCaseFields(string kind, bool modal, bool editor)
    {
        await using var endpoint = await LoopbackYandexDiskEndpoint.StartAsync();
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(workerEnabled: true,
            uploadCapacityChecks: true, yandexCopy: endpoint);
        var caseId = await LoginAndCreateCaseAsync(runtime); int clientId;
        await using (var seed = runtime.OpenDatabase())
        {
            var clientCase = await seed.ClientCases.SingleAsync(c => c.Id == caseId);
            clientId = Assert.IsType<int>(clientCase.ClientId); clientCase.Title = "Synthetic financial browser case";
            seed.ClientCaseWorkOrders.Add(new ClientCaseWorkOrder
            {
                ClientCaseId = caseId,
                Title = "Original browser financial order",
                Status = "approved",
                Currency = "RUB",
                TotalAmount = 35m,
                Items = [new ClientCaseWorkOrderItem { ServiceName = "Original browser financial service", Quantity = 2m,
                    UnitPrice = 17.5m, SortOrder = 1 }]
            });
            await seed.SaveChangesAsync();
        }
        byte[] frozen = [];
        await runtime.CheckCaseRetirementBrowserAsync(caseId, Guid.NewGuid(), false,
            $"financial-{kind}-{(editor ? "editor" : modal ? "modal" : "edit")}", clientId, applyBoundary: async () =>
            {
                await using (var db = runtime.OpenDatabase())
                {
                    Assert.Empty(await db.ClientCaseDocuments.ToArrayAsync()); Assert.Empty(await db.StorageProductBindings.ToArrayAsync());
                    Assert.Equal(0, await db.Payments.CountAsync() + await db.Invoices.CountAsync() + await db.Acts.CountAsync() + await db.Contracts.CountAsync());
                    Assert.Single(await db.GeneratedFinancialDocumentAttempts.ToArrayAsync());
                    frozen = (await db.GeneratedFinancialDocumentContents.SingleAsync()).Bytes;
                }
                Assert.Equal(0, endpoint.PutAttempts);
                for (var retry = 0; retry < 5; retry++)
                {
                    await using (var dueDb = runtime.OpenDatabase())
                    {
                        var due = DateTime.SpecifyKind(DateTime.UtcNow.AddSeconds(-1), DateTimeKind.Unspecified);
                        await dueDb.GeneratedFinancialDocumentAttempts.Where(a => a.PublishedDocumentId == null && a.RecoveryLeaseToken == null)
                            .ExecuteUpdateAsync(s => s.SetProperty(a => a.RecoveryNextAttemptAt, due));
                        await dueDb.StorageOperations.Where(o => o.Status != StorageOperationStatuses.Succeeded && o.Status != StorageOperationStatuses.DeadLetter)
                            .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
                    }
                    Assert.Equal(0, (await runtime.RunOwnedYandexWorkerAsync()).DeadLettered);
                    await using var db = runtime.OpenDatabase();
                    if (await db.ClientCaseDocuments.AnyAsync()) break;
                }
                await using var completed = runtime.OpenDatabase(); Assert.Single(await completed.ClientCaseDocuments.ToArrayAsync());
            }, financialDocuments: true);
        await using var proof = runtime.OpenDatabase();
        var document = Assert.Single(await proof.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(document.Id, (await proof.GeneratedFinancialDocumentAttempts.SingleAsync()).PublishedDocumentId);
        Assert.Equal(kind == "invoice" ? 2 : 1,
            await proof.Payments.CountAsync() + await proof.Invoices.CountAsync() + await proof.Acts.CountAsync() + await proof.Contracts.CountAsync());
        Assert.Single(await proof.StorageProductBindings.ToArrayAsync());
        Assert.Single(await proof.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" && a.Action == "Generated").ToArrayAsync());
        Assert.Equal(frozen, await File.ReadAllBytesAsync(Path.Combine(runtime.Root, "financial-browser-download.html")));
        if (modal || editor) Assert.NotEqual("Unsaved financial strategy", (await proof.ClientCases.SingleAsync(c => c.Id == caseId)).Strategy);
        Assert.Equal("false", await proof.SystemSettings.Where(s => s.Key == "notifications.email.enabled").Select(s => s.Value).SingleAsync());
        Assert.Equal(1, endpoint.PutCount); Assert.Equal(1, endpoint.PutAttempts); runtime.AssertNoServerErrors();
    }

    [Theory]
    [InlineData("invoice", "normal")]
    [InlineData("act", "normal")]
    [InlineData("contract", "normal")]
    [InlineData("invoice", "unavailable")]
    [InlineData("act", "unavailable")]
    [InlineData("contract", "unavailable")]
    [InlineData("invoice", "lost-ack")]
    [InlineData("act", "lost-ack")]
    [InlineData("contract", "lost-ack")]
    [InlineData("invoice", "low-quota")]
    [InlineData("act", "low-quota")]
    [InlineData("contract", "low-quota")]
    [InlineData("invoice", "unknown-quota")]
    [InlineData("act", "unknown-quota")]
    [InlineData("contract", "unknown-quota")]
    public async Task RealHttpFinancialYandexCopiesPublishOneFrozenFinancialResultAcrossRestart(string kind, string failure)
    {
        await using var endpoint = await LoopbackYandexDiskEndpoint.StartAsync();
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(workerEnabled: true,
            uploadCapacityChecks: true, yandexCopy: endpoint);
        var caseId = await LoginAndCreateCaseAsync(runtime);
        await using (var seed = runtime.OpenDatabase())
        {
            seed.ClientCaseWorkOrders.Add(new ClientCaseWorkOrder
            {
                ClientCaseId = caseId,
                Title = "Original financial order",
                Status = "approved",
                Currency = "RUB",
                TotalAmount = 35m,
                Items = [new ClientCaseWorkOrderItem { ServiceName = "Original financial service", Quantity = 2m,
                    UnitPrice = 17.5m, SortOrder = 1 }]
            });
            await seed.SaveChangesAsync();
        }
        var key = Guid.NewGuid(); var path = $"/api/v2/cases/{caseId}/work-order/create-{kind}";
        async Task<HttpResponseMessage> GenerateAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            { Content = JsonContent.Create(new { number = "WIRE-" + kind }) };
            request.Headers.Add("X-Upload-Key", key.ToString("D"));
            return await runtime.Client.SendAsync(request);
        }
        async Task AssertNoFinancialPublicationAsync()
        {
            await using var db = runtime.OpenDatabase();
            Assert.Empty(await db.ClientCaseDocuments.ToArrayAsync()); Assert.Empty(await db.StorageProductBindings.ToArrayAsync());
            Assert.Equal(0, await db.Payments.CountAsync() + await db.Invoices.CountAsync() + await db.Acts.CountAsync() + await db.Contracts.CountAsync());
            Assert.Empty(await db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" && a.Action == "Generated").ToArrayAsync());
            Assert.Equal("approved", await db.ClientCaseWorkOrders.Select(o => o.Status).SingleAsync());
        }
        if (failure == "low-quota") endpoint.UsedCapacityBytes = endpoint.TotalCapacityBytes;
        if (failure == "unknown-quota") endpoint.FailCapacity = true;
        using (var pending = await GenerateAsync())
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, pending.StatusCode);
            Assert.Equal(key.ToString("D"), Assert.Single(pending.Headers.GetValues("X-Upload-Key")));
        }
        await AssertNoFinancialPublicationAsync(); Assert.Equal(0, endpoint.PutAttempts);
        Guid attemptId; int entityId; int? invoiceId; byte[] frozenBytes; string frozenJson;
        await using (var db = runtime.OpenDatabase())
        {
            var header = Assert.Single(await db.GeneratedFinancialDocumentAttempts.ToArrayAsync());
            attemptId = header.Id; entityId = header.EntityId; invoiceId = header.InvoiceEntityId;
            frozenJson = header.FrozenDomainJson; frozenBytes = (await db.GeneratedFinancialDocumentContents.SingleAsync()).Bytes;
            if (failure is "low-quota" or "unknown-quota") Assert.Empty(await db.StorageObjects.ToArrayAsync());
            else Assert.Single(await db.StorageObjects.ToArrayAsync());
            var order = await db.ClientCaseWorkOrders.Include(o => o.Items).SingleAsync(); order.Title = "Replacement order after acceptance";
            order.Items.Single().ServiceName = "Replacement service";
            await db.SaveChangesAsync();
        }
        endpoint.UsedCapacityBytes = 0; endpoint.FailCapacity = false;
        endpoint.FailUploadLink = failure == "unavailable"; endpoint.DropNextPutResponse = failure == "lost-ack";
        if (failure is "unavailable" or "lost-ack")
        {
            var failed = await runtime.RunOwnedYandexWorkerAsync();
            Assert.True(failed.Retried + failed.Unknown > 0); await AssertNoFinancialPublicationAsync();
            endpoint.FailUploadLink = false;
        }
        await runtime.RestartAsync(TimeSpan.Zero);
        for (var retry = 0; retry < 5; retry++)
        {
            await using (var db = runtime.OpenDatabase())
            {
                var due = DateTime.SpecifyKind(DateTime.UtcNow.AddSeconds(-1), DateTimeKind.Unspecified);
                await db.GeneratedFinancialDocumentAttempts.Where(a => a.PublishedDocumentId == null && a.RecoveryLeaseToken == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.RecoveryNextAttemptAt, due));
                await db.StorageOperations.Where(o => o.Status != StorageOperationStatuses.Succeeded && o.Status != StorageOperationStatuses.DeadLetter)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
            }
            Assert.Equal(0, (await runtime.RunOwnedYandexWorkerAsync()).DeadLettered);
            await using var proof = runtime.OpenDatabase();
            if (await proof.ClientCaseDocuments.AnyAsync()) break;
        }
        int documentId;
        using (var response = await GenerateAsync())
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var result = json.RootElement;
            documentId = result.GetProperty("documentId").GetInt32(); Assert.True(documentId > 0);
            Assert.Equal(entityId, result.GetProperty(kind switch { "invoice" => "paymentId", "act" => "actId", _ => "contractId" }).GetInt32());
            if (kind == "invoice") Assert.Equal(invoiceId, result.GetProperty("invoiceEntityId").GetInt32());
            Assert.Equal(35m, result.GetProperty("amount").GetDecimal()); Assert.Equal("WIRE-" + kind, result.GetProperty("number").GetString());
        }
        Assert.Equal(frozenBytes, await runtime.Client.GetByteArrayAsync($"/api/v2/cases/{caseId}/documents/{documentId}/download"));
        await runtime.RestartAsync(TimeSpan.Zero);
        using (var response = await GenerateAsync())
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(documentId, json.RootElement.GetProperty("documentId").GetInt32());
        }
        await using (var db = runtime.OpenDatabase())
        {
            var header = Assert.Single(await db.GeneratedFinancialDocumentAttempts.ToArrayAsync());
            Assert.Equal(attemptId, header.Id); Assert.Equal(entityId, header.EntityId); Assert.Equal(invoiceId, header.InvoiceEntityId);
            Assert.Equal(frozenJson, header.FrozenDomainJson); Assert.Equal(documentId, header.PublishedDocumentId);
            Assert.Equal(kind == "invoice" ? 2 : 1,
                await db.Payments.CountAsync() + await db.Invoices.CountAsync() + await db.Acts.CountAsync() + await db.Contracts.CountAsync());
            var document = Assert.Single(await db.ClientCaseDocuments.ToArrayAsync()); Assert.Equal(documentId, document.Id);
            var binding = Assert.Single(await db.StorageProductBindings.ToArrayAsync()); Assert.Equal((long)documentId, binding.SourceId);
            var replicas = await db.StorageReplicas.Where(r => r.StorageObjectGeneration.StorageObjectId == binding.StorageObjectId).ToArrayAsync();
            Assert.Equal(2, replicas.Length); Assert.All(replicas, r => Assert.Equal(StorageReplicaStatuses.Available, r.Status));
            var cloud = Assert.Single(replicas, r => r.ProviderKey == "yandex-disk");
            Assert.Equal(frozenBytes, endpoint.GetBytes(cloud.Locator[(cloud.Locator.IndexOf('/') + 1)..]));
            Assert.Single(await db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document" && a.Action == "Generated").ToArrayAsync());
            Assert.Equal("false", await db.SystemSettings.Where(s => s.Key == "notifications.email.enabled").Select(s => s.Value).SingleAsync());
        }
        Assert.Equal(0, (await runtime.RunOwnedYandexWorkerAsync()).Claimed);
        Assert.Equal(1, endpoint.PutCount); Assert.Equal(1, endpoint.PutAttempts); runtime.AssertNoServerErrors();
    }
}
