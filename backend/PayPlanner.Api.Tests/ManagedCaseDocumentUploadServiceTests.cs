using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ManagedCaseDocumentUploadServiceTests(PostgreSqlModelTemplate modelTemplate) : IClassFixture<PostgreSqlModelTemplate>
{
    private Task<Fixture> CreateFixtureAsync(bool postgres = false, int requiredCopies = 1, bool migrated = false)
        => Fixture.CreateAsync(postgres, requiredCopies, migrated, modelTemplate);
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InitialTaskSourceUploadKeepsSourceAcrossVersionsAndReplay(bool postgres, bool managed)
    {
        await using var f = await CreateFixtureAsync(postgres);
        if (!managed) f.Options.ProductWriteEnabledClasses.Clear();
        f.User.Role!.Permissions.Add(new RolePermission
        {
            Section = RolePermissionCatalog.Tasks,
            CanView = true,
            ViewScope = DataVisibilityScope.Own
        });
        var task = new TaskJournalItem
        {
            ClientCaseId = f.Case.Id,
            Title = "Источник документа",
            AuthorUserId = f.User.Id,
            AssigneeUserId = f.User.Id,
            DueAt = DateTime.UtcNow.AddDays(1)
        };
        f.Db.TaskJournalItems.Add(task);
        await f.Db.SaveChangesAsync();
        var sourceKey = $"task-{task.Id}-evidence";
        var attempt = Guid.NewGuid();
        var first = f.Created(await f.Upload(attempt, documentKey: sourceKey));
        Assert.Equal(sourceKey, first.DocumentKey);
        Assert.Equal(task.Id, first.SourceTaskId);
        Assert.Equal(1, first.Version);
        if (managed)
            Assert.Equal(first.Id, f.Created(await f.Upload(attempt, documentKey: sourceKey)).Id);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Equal("Created", (await f.Db.ObjectAuditEvents.SingleAsync(a => a.ObjectType == "client-case-document")).Action);
        var second = f.Created(await f.Upload(Guid.NewGuid(), body: "revised document", versionOf: first.Id));
        Assert.Equal(sourceKey, second.DocumentKey);
        Assert.Equal(task.Id, second.SourceTaskId);
        Assert.Equal(2, second.Version);
        Assert.Equal(2, await f.Db.ClientCaseDocuments.CountAsync());
        Assert.IsType<NotFoundResult>((await f.Upload(Guid.NewGuid(), versionOf: int.MaxValue)).Result);
        Assert.Equal(2, await f.Db.ClientCaseDocuments.CountAsync());
    }

    public static IEnumerable<object[]> UnavailableInitialTaskSources()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var managed in new[] { false, true })
                foreach (var reason in new[] { "permission", "visibility", "other-case", "missing", "empty-suffix" })
                    yield return [postgres, managed, reason];
    }

    [Theory]
    [MemberData(nameof(UnavailableInitialTaskSources))]
    public async Task InitialTaskSourceUploadRejectsUnavailableSourceBeforeStorage(bool postgres, bool managed, string reason)
    {
        await using var f = await CreateFixtureAsync(postgres);
        if (!managed) f.Options.ProductWriteEnabledClasses.Clear();
        if (reason != "permission")
            f.User.Role!.Permissions.Add(new RolePermission
            {
                Section = RolePermissionCatalog.Tasks,
                CanView = true,
                ViewScope = DataVisibilityScope.Own
            });
        var task = new TaskJournalItem
        {
            ClientCaseId = reason == "other-case" ? null : f.Case.Id,
            Title = "Синтетический источник",
            DueAt = DateTime.UtcNow.AddDays(1),
            AuthorUserId = reason == "visibility" ? null : f.User.Id,
            AssigneeUserId = reason == "visibility" ? null : f.User.Id
        };
        f.Db.TaskJournalItems.Add(task);
        await f.Db.SaveChangesAsync();
        var sourceId = reason == "missing" ? int.MaxValue : task.Id;
        var sourceKey = $"task-{sourceId}-" + (reason == "empty-suffix" ? "" : "evidence");
        Assert.IsType<BadRequestObjectResult>((await f.Upload(Guid.NewGuid(), documentKey: sourceKey)).Result);
        Assert.Equal(0, f.Local.PutCount);
        Assert.Equal(0, f.Mirror.PutCount);
        Assert.Empty(await f.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageCapacityReservations.AsNoTracking().ToArrayAsync());
        Assert.Empty(Directory.EnumerateFiles(f.Root, "*.txt", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ManagedWorkOrderHttpFeedsAutomaticQueueWithFrozenSource(bool postgres, bool automaticAtWrite)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        var order = await f.SeedGeneratedWorkOrderAsync();
        f.Options.AutoCaseDocumentPublicationEnabled = automaticAtWrite;
        var key = Guid.NewGuid();
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.GenerateWorkOrderHttp(key)).Result).StatusCode);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        await f.CasePublisher().EnsureScheduledAsync(CancellationToken.None);
        Assert.Single(await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishCaseDocument).ToArrayAsync());
        await f.CopyPendingAsync();
        await f.Db.ClientCaseWorkOrders.Where(o => o.Id == order.Id).ExecuteUpdateAsync(update => update.SetProperty(o => o.Title, "REPLACEMENT ORDER"));
        await f.RecreateContext();
        Assert.Equal(1, (await f.RunCaseQueueAsync()).Succeeded);
        Assert.Equal(0, (await f.RunCaseQueueAsync()).Claimed);
        var document = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        var binding = await f.Db.StorageProductBindings.AsNoTracking().SingleAsync();
        await using (var stored = await f.ReadStoredGeneratedAsync(binding.StorageObjectId))
        {
            Assert.NotNull(stored);
            using var reader = new StreamReader(stored.Content);
            var html = await reader.ReadToEndAsync();
            Assert.Contains("Исходный наряд", html);
            Assert.DoesNotContain("REPLACEMENT ORDER", html);
        }
        var reads = f.Local.ReadCount;
        Assert.Equal(document.Id, f.Created(await f.GenerateWorkOrderHttp(key)).Id);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Equal("Generated", (await f.Db.ObjectAuditEvents.SingleAsync(a => a.ObjectType == "client-case-document")).Action);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, "before-commit")]
    [InlineData(false, "lost-commit")]
    [InlineData(true, "before-commit")]
    [InlineData(true, "lost-commit")]
    public async Task ManagedWorkOrderHttpRecoversRollbackAndLostCommitResponse(bool postgres, string stage)
    {
        await using var f = await CreateFixtureAsync(postgres);
        await f.SeedGeneratedWorkOrderAsync();
        var key = Guid.NewGuid();
        f.Fault.Enabled = true;
        f.Fault.Stage = stage;
        await Assert.ThrowsAsync<IOException>(() => f.GenerateWorkOrderHttp(key));
        f.Fault.Enabled = false;
        await f.RecreateContext();
        var expected = stage == "lost-commit" ? 1 : 0;
        Assert.Equal(expected, await f.Db.ClientCaseDocuments.CountAsync());
        Assert.Equal(expected, await f.Db.StorageProductBindings.CountAsync());
        Assert.Equal(expected, await f.Db.ObjectAuditEvents.CountAsync(a => a.ObjectType == "client-case-document"));
        Assert.Equal(1, f.Created(await f.GenerateWorkOrderHttp(key)).Version);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Equal(2, await f.Db.CaseDocumentNotificationDispatches.CountAsync());
        Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ManagedWorkOrderHttpRollsBackSourceOrFlagChangeAfterDomainSave(bool postgres, bool disabled)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var order = await f.SeedGeneratedWorkOrderAsync();
        var key = Guid.NewGuid();
        f.Fault.Enabled = true;
        f.Fault.Stage = "source-change";
        f.Fault.AfterDomainSave = () =>
        {
            if (disabled) f.Options.UploadCapacityChecksEnabled = false;
            else f.Db.ClientCaseWorkOrders.Where(o => o.Id == order.Id).ExecuteDelete();
        };
        var first = await f.GenerateWorkOrderHttp(key);
        if (disabled) Assert.Equal(503, Assert.IsType<ObjectResult>(first.Result).StatusCode);
        else Assert.IsType<ForbidResult>(first.Result);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.ObjectAuditEvents.AsNoTracking().Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Empty(await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().ToArrayAsync());
        f.Fault.Enabled = false;
        f.Options.UploadCapacityChecksEnabled = true;
        await f.RecreateContext();
        Assert.Equal(1, f.Created(await f.GenerateWorkOrderHttp(key)).Version);
        Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task ManagedWorkOrderHttpFreezesSourceAndReplaysExactReceipt(bool postgres, int copies)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: copies);
        var order = await f.SeedGeneratedWorkOrderAsync();
        var key = Guid.NewGuid();
        var first = await f.GenerateWorkOrderHttp(key);
        if (copies == 2)
        {
            Assert.Equal(503, Assert.IsType<ObjectResult>(first.Result).StatusCode);
            Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
            Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToArrayAsync());
            await f.CopyPendingAsync();
        }
        await f.Db.ClientCaseWorkOrders.Where(o => o.Id == order.Id).ExecuteUpdateAsync(update =>
            update.SetProperty(o => o.Title, "REPLACEMENT ORDER").SetProperty(o => o.Status, "draft"));
        await f.RecreateContext();
        var saved = f.Created(await f.GenerateWorkOrderHttp(key));
        Assert.Equal(1, saved.Version);
        Assert.Equal("Согласован", saved.Status);
        Assert.Equal($"work-order-{order.Id}", saved.DocumentKey);
        var binding = await f.Db.StorageProductBindings.AsNoTracking().SingleAsync();
        await using (var stored = await f.ReadStoredGeneratedAsync(binding.StorageObjectId))
        {
            Assert.NotNull(stored);
            using var reader = new StreamReader(stored.Content);
            var html = await reader.ReadToEndAsync();
            Assert.Contains("Исходный наряд", html);
            Assert.DoesNotContain("REPLACEMENT ORDER", html);
        }
        var audit = await f.Db.ObjectAuditEvents.AsNoTracking().SingleAsync(a => a.ObjectType == "client-case-document");
        var dispatches = await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().OrderBy(d => d.Id).Select(d => d.Id).ToArrayAsync();
        Assert.Equal(2, dispatches.Length);
        var clientRecipient = await f.Db.Clients.Where(c => c.Id == f.Case.ClientId).Select(c => c.ResponsibleUserId).SingleAsync();
        Assert.Equal(new[] { f.User.Id, clientRecipient!.Value }.OrderBy(id => id),
            await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().OrderBy(d => d.UserNotification.RecipientUserId).Select(d => d.UserNotification.RecipientUserId).ToArrayAsync());
        var history = (await f.Db.ClientCases.AsNoTracking().SingleAsync(c => c.Id == f.Case.Id)).EventHistory;
        var reads = f.Local.ReadCount;
        Assert.Equal(saved.Id, f.Created(await f.GenerateWorkOrderHttp(key)).Id);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Equal(audit.Id, (await f.Db.ObjectAuditEvents.AsNoTracking().SingleAsync(a => a.ObjectType == "client-case-document")).Id);
        Assert.Equal(dispatches, await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().OrderBy(d => d.Id).Select(d => d.Id).ToArrayAsync());
        Assert.Equal(history, (await f.Db.ClientCases.AsNoTracking().SingleAsync(c => c.Id == f.Case.Id)).EventHistory);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedWorkOrderHttpRecoversReservedAttemptWithoutAnotherWrite(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.SeedGeneratedWorkOrderAsync();
        var key = Guid.NewGuid();
        f.Local.LoseAcknowledgement = true;
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.GenerateWorkOrderHttp(key)).Result).StatusCode);
        Assert.Empty(await f.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        await f.RecreateContext();
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.GenerateWorkOrderHttp(key)).Result).StatusCode);
        Assert.Equal(1, f.Local.PutCount);
        await f.CopyPendingAsync();
        await f.RecreateContext();
        Assert.Equal(1, f.Created(await f.GenerateWorkOrderHttp(key)).Version);
        Assert.Single(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedWorkOrderHttpRejectsMissingSourceBeforeProviderRead(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        var order = await f.SeedGeneratedWorkOrderAsync();
        var key = Guid.NewGuid();
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.GenerateWorkOrderHttp(key)).Result).StatusCode);
        await f.CopyPendingAsync();
        await f.Db.ClientCaseWorkOrders.Where(o => o.Id == order.Id).ExecuteDeleteAsync();
        await f.RecreateContext();
        var reads = f.Local.ReadCount;
        Assert.IsType<ForbidResult>((await f.GenerateWorkOrderHttp(key)).Result);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedWorkOrderHttpRequiresKeyAndNeverFallsBackForOwnedAttempt(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.SeedGeneratedWorkOrderAsync();
        Assert.IsType<BadRequestObjectResult>((await f.Controller().GenerateWorkOrderDocument(f.Case.Id, CancellationToken.None)).Result);
        var key = Guid.NewGuid();
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.GenerateWorkOrderHttp(key)).Result).StatusCode);
        f.Options.Enabled = false;
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.GenerateWorkOrderHttp(key)).Result).StatusCode);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ManagedTemplateStatusDiscoversExactOwnedSourceWithoutProviderRead(bool postgres, bool reserved)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.SeedGeneratedTemplateAsync();
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        f.Options.WorkerEnabled = true;
        f.Local.LoseAcknowledgement = reserved;
        var key = Guid.NewGuid();
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.GenerateTemplateHttp(key)).Result).StatusCode);
        var reads = f.Local.ReadCount;
        var reader = new CaseDocumentUploadStatusReader(f.Db, f.Monitor, new StorageWorkerAvailability(true));
        var state = Assert.Single((await reader.ReadAsync(f.Case.Id, f.User.Id, [key], CancellationToken.None))!);
        Assert.Equal(reserved ? "manualRetry" : "waiting", state.Status);
        Assert.Null(state.DocumentId);
        Assert.Null(state.Sha256);
        Assert.Contains("Исходный шаблон", state.FileName!);
        var generation = Assert.IsType<CaseDocumentGenerationState>(state.Generation);
        Assert.True(generation.SourceVerified);
        Assert.Equal("template", generation.Kind);
        Assert.Matches("^[a-f0-9]{64}$", generation.RequestSha256);
        Assert.Equal("custom", generation.TemplateKey);
        Assert.Equal("Иск", generation.DocumentType);
        Assert.Equal("Подготовлен", generation.Status);
        var discovered = Assert.Single((await reader.ListPageAsync(f.Case.Id, f.User.Id, CancellationToken.None))!.Items);
        Assert.Equal(state, discovered);
        var other = await f.Db.Users.SingleAsync(u => u.Id != f.User.Id);
        var foreign = Assert.Single((await reader.ReadAsync(f.Case.Id, other.Id, [key], CancellationToken.None))!);
        Assert.Equal("unknown", foreign.Status);
        Assert.Null(foreign.FileName);
        Assert.Null(foreign.Generation);
        Assert.Empty((await reader.ListPageAsync(f.Case.Id, other.Id, CancellationToken.None))!.Items);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ManagedTemplateStatusProvesPublishedOrRetiredAttemptWithoutProviderRead(bool postgres, bool retired)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.SeedGeneratedTemplateAsync();
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        var key = Guid.NewGuid();
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.GenerateTemplateHttp(key)).Result).StatusCode);
        if (retired) Assert.IsType<OkObjectResult>((await f.Controller().RetireDocumentUpload(f.Case.Id, key, CancellationToken.None)).Result);
        else
        {
            await f.CopyPendingAsync();
            Assert.Equal(1, (await f.RunCaseQueueAsync()).Succeeded);
        }
        var reads = f.Local.ReadCount;
        var reader = new CaseDocumentUploadStatusReader(f.Db, f.Monitor);
        var state = Assert.Single((await reader.ReadAsync(f.Case.Id, f.User.Id, [key], CancellationToken.None))!);
        Assert.Equal(retired ? "retired" : "uploaded", state.Status);
        Assert.True(state.Generation!.SourceVerified);
        Assert.Equal("template", state.Generation.Kind);
        Assert.Equal("custom", state.Generation.TemplateKey);
        if (retired)
        {
            Assert.Null(state.DocumentId);
            await f.Db.StorageOperations.Where(o => o.OperationType == "commit").ExecuteUpdateAsync(u => u.SetProperty(o => o.PayloadJson, (string?)null));
            var afterCleanup = Assert.Single((await reader.ReadAsync(f.Case.Id, f.User.Id, [key], CancellationToken.None))!);
            Assert.Equal("retired", afterCleanup.Status);
            Assert.Null(afterCleanup.Generation);
        }
        else
        {
            Assert.Equal((await f.Db.ClientCaseDocuments.SingleAsync()).Id, state.DocumentId);
            Assert.Equal(1, state.Version);
            Assert.NotNull(state.Sha256);
        }
        Assert.Equal(reads, f.Local.ReadCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ManagedTemplateStatusMasksUnprovenPendingSource(bool postgres, bool reserved)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.SeedGeneratedTemplateAsync();
        f.Local.LoseAcknowledgement = reserved;
        var key = Guid.NewGuid();
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.GenerateTemplateHttp(key)).Result).StatusCode);
        var capacityKey = StorageObjectService.GetCapacityReservationKey($"case-document:{f.Case.Id}:{f.User.Id}:{key:N}");
        var localResource = System.Text.Json.JsonSerializer.Serialize(new string?[] { "local", null });
        var json = reserved
            ? (await f.Db.StorageCapacityReservations.SingleAsync(r => r.IdempotencyKey == capacityKey && r.ResourceKey == localResource)).ProductContextJson
            : (await f.Db.StorageOperations.SingleAsync(o => o.OperationType == "commit")).PayloadJson;
        var payload = System.Text.Json.Nodes.JsonNode.Parse(json!)!;
        payload["generation"]!["kind"] = "invoice";
        if (reserved) (await f.Db.StorageCapacityReservations.SingleAsync(r => r.IdempotencyKey == capacityKey && r.ResourceKey == localResource)).ProductContextJson = payload.ToJsonString();
        else (await f.Db.StorageOperations.SingleAsync(o => o.OperationType == "commit")).PayloadJson = payload.ToJsonString();
        await f.Db.SaveChangesAsync();
        var reads = f.Local.ReadCount;
        var reader = new CaseDocumentUploadStatusReader(f.Db, f.Monitor);
        var state = Assert.Single((await reader.ReadAsync(f.Case.Id, f.User.Id, [key], CancellationToken.None))!);
        Assert.Equal("unknown", state.Status);
        Assert.Null(state.FileName);
        Assert.Null(state.Generation);
        Assert.Empty((await reader.ListPageAsync(f.Case.Id, f.User.Id, CancellationToken.None))!.Items);
        Assert.Equal(reads, f.Local.ReadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedWorkOrderRetirementSupportsExactSavedSource(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        var order = await f.SeedGeneratedWorkOrderAsync();
        var key = Guid.NewGuid();
        Assert.Equal("pending", (await f.StageGeneratedWorkOrderAsync(order, key)).Status);
        var reads = f.Local.ReadCount;
        var retired = Assert.IsType<CaseDocumentUploadState>(Assert.IsType<OkObjectResult>(
            (await f.Controller().RetireDocumentUpload(f.Case.Id, key, CancellationToken.None)).Result).Value);
        Assert.Equal("retired", retired.Status);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Single(await f.Db.CaseDocumentUploadRetirements.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Equal("retired", (await f.StageGeneratedWorkOrderAsync(order, key)).Status);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(0, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, "catalog")]
    [InlineData(false, "reserved")]
    [InlineData(false, "published")]
    [InlineData(true, "catalog")]
    [InlineData(true, "reserved")]
    [InlineData(true, "published")]
    public async Task ManagedTemplateRetirementPreservesCopiesAndPublishedDocuments(bool postgres, string state)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: state == "published" ? 1 : 2);
        await f.SeedGeneratedTemplateAsync();
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        f.Local.LoseAcknowledgement = state == "reserved";
        var key = Guid.NewGuid();
        var generated = await f.GenerateTemplateHttp(key);
        int? publishedId = state == "published" ? f.Created(generated).Id : null;
        if (state != "published") Assert.Equal(503, Assert.IsType<ObjectResult>(generated.Result).StatusCode);
        await f.RecreateContext();
        var reads = f.Local.ReadCount;
        var retired = Assert.IsType<CaseDocumentUploadState>(Assert.IsType<OkObjectResult>(
            (await f.Controller().RetireDocumentUpload(f.Case.Id, key, CancellationToken.None)).Result).Value);
        Assert.Equal(state == "published" ? "uploaded" : "retired", retired.Status);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Equal(1, f.Local.PutCount);
        if (state == "published")
        {
            Assert.Equal(publishedId, retired.DocumentId);
            Assert.Empty(await f.Db.CaseDocumentUploadRetirements.AsNoTracking().ToArrayAsync());
            Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
            Assert.Equal(publishedId, f.Created(await f.GenerateTemplateHttp(key)).Id);
        }
        else
        {
            Assert.Single(await f.Db.CaseDocumentUploadRetirements.AsNoTracking().ToArrayAsync());
            Assert.IsType<ConflictObjectResult>((await f.GenerateTemplateHttp(key)).Result);
            Assert.Equal(reads, f.Local.ReadCount);
            if (state == "catalog")
            {
                await f.CopyPendingAsync();
                var readsAfterCopy = f.Local.ReadCount;
                Assert.Equal(1, (await f.RunCaseQueueAsync()).DeadLettered);
                Assert.Equal(readsAfterCopy, f.Local.ReadCount);
                Assert.Equal(1, f.Mirror.PutCount);
            }
            Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
            Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
            Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToArrayAsync());
        }
    }

    [Theory]
    [InlineData(false, false, "kind")]
    [InlineData(false, false, "source")]
    [InlineData(false, true, "kind")]
    [InlineData(false, true, "source")]
    [InlineData(true, false, "kind")]
    [InlineData(true, false, "source")]
    [InlineData(true, true, "kind")]
    [InlineData(true, true, "source")]
    [InlineData(false, true, "unhandled-kind")]
    [InlineData(true, true, "unhandled-kind")]
    public async Task ManagedTemplateRetirementRejectsUnprovenGeneratedSource(bool postgres, bool reserved, string field)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.SeedGeneratedTemplateAsync();
        f.Local.LoseAcknowledgement = reserved;
        var key = Guid.NewGuid();
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.GenerateTemplateHttp(key)).Result).StatusCode);
        var localResource = System.Text.Json.JsonSerializer.Serialize(new string?[] { "local", null });
        var capacityKey = StorageObjectService.GetCapacityReservationKey($"case-document:{f.Case.Id}:{f.User.Id}:{key:N}");
        var json = reserved
            ? (await f.Db.StorageCapacityReservations.SingleAsync(r => r.IdempotencyKey == capacityKey && r.ResourceKey == localResource)).ProductContextJson
            : (await f.Db.StorageOperations.SingleAsync(o => o.OperationType == "commit")).PayloadJson;
        var payload = System.Text.Json.Nodes.JsonNode.Parse(json!)!;
        var generation = payload["generation"]!;
        if (field == "kind") generation["kind"] = "unsupported";
        else if (field == "unhandled-kind") generation["kind"] = "invoice";
        else
        {
            var source = System.Text.Json.Nodes.JsonNode.Parse(generation["frozenDomainJson"]!.GetValue<string>())!;
            source["fileName"] = "foreign.html";
            generation["frozenDomainJson"] = source.ToJsonString();
        }
        if (reserved) (await f.Db.StorageCapacityReservations.SingleAsync(r => r.IdempotencyKey == capacityKey && r.ResourceKey == localResource)).ProductContextJson = payload.ToJsonString();
        else (await f.Db.StorageOperations.SingleAsync(o => o.OperationType == "commit")).PayloadJson = payload.ToJsonString();
        await f.Db.SaveChangesAsync();
        await f.RecreateContext();
        var reads = f.Local.ReadCount;
        Assert.IsType<NotFoundResult>((await f.Controller().RetireDocumentUpload(f.Case.Id, key, CancellationToken.None)).Result);
        Assert.Empty(await f.Db.CaseDocumentUploadRetirements.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ManagedTemplateHttpProducerFeedsQueueAndPreservesReceipt(bool postgres, bool automaticAtWrite)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.SeedGeneratedTemplateAsync();
        f.Options.AutoCaseDocumentPublicationEnabled = automaticAtWrite;
        var key = Guid.NewGuid();
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.GenerateTemplateHttp(key)).Result).StatusCode);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToArrayAsync());
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        await f.CasePublisher().EnsureScheduledAsync(CancellationToken.None);
        Assert.Single(await f.Db.StorageOperations.AsNoTracking().Where(o => o.OperationType == StorageOperationTypes.PublishCaseDocument).ToArrayAsync());
        await f.CopyPendingAsync();
        await f.Db.SystemSettings.Where(s => s.Key == "cases.documentTemplates").ExecuteUpdateAsync(update =>
            update.SetProperty(s => s.Value, "{\"templates\":[{\"key\":\"custom\",\"label\":\"Заменённый шаблон\",\"content\":\"replacement bytes\",\"caseTypes\":[\"Гражданское\"]}]}"));
        await f.Db.ClientCases.Where(c => c.Id == f.Case.Id).ExecuteUpdateAsync(update => update.SetProperty(c => c.Title, "NEW CASE TITLE"));
        await f.RecreateContext();
        Assert.Equal(1, (await f.RunCaseQueueAsync()).Succeeded);
        Assert.Equal(0, (await f.RunCaseQueueAsync()).Claimed);
        var document = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.Contains("Исходный шаблон", document.OriginalFileName);
        var binding = await f.Db.StorageProductBindings.AsNoTracking().SingleAsync();
        await using (var stored = await f.ReadStoredGeneratedAsync(binding.StorageObjectId))
        {
            Assert.NotNull(stored);
            using var reader = new StreamReader(stored.Content);
            var html = await reader.ReadToEndAsync();
            Assert.Contains("Синтетическое дело", html);
            Assert.DoesNotContain("NEW CASE TITLE", html);
            Assert.DoesNotContain("replacement bytes", html);
        }
        var dispatch = await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().SingleAsync();
        var history = (await f.Db.ClientCases.AsNoTracking().SingleAsync(c => c.Id == f.Case.Id)).EventHistory;
        var reads = f.Local.ReadCount;
        var replay = f.Created(await f.GenerateTemplateHttp(key));
        Assert.Equal(document.Id, replay.Id);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Equal(dispatch.Id, (await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(history, (await f.Db.ClientCases.AsNoTracking().SingleAsync(c => c.Id == f.Case.Id)).EventHistory);
        Assert.Equal("Generated", (await f.Db.ObjectAuditEvents.AsNoTracking().SingleAsync(a => a.ObjectType == "client-case-document")).Action);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, "before-commit")]
    [InlineData(false, "lost-commit")]
    [InlineData(true, "before-commit")]
    [InlineData(true, "lost-commit")]
    public async Task ManagedTemplateHttpRecoversRollbackAndLostCommitResponse(bool postgres, string stage)
    {
        await using var f = await CreateFixtureAsync(postgres);
        await f.SeedGeneratedTemplateAsync();
        var key = Guid.NewGuid();
        f.Fault.Enabled = true;
        f.Fault.Stage = stage;
        await Assert.ThrowsAsync<IOException>(() => f.GenerateTemplateHttp(key));
        f.Fault.Enabled = false;
        await f.RecreateContext();
        var expected = stage == "lost-commit" ? 1 : 0;
        Assert.Equal(expected, await f.Db.ClientCaseDocuments.CountAsync());
        Assert.Equal(expected, await f.Db.StorageProductBindings.CountAsync());
        Assert.Equal(expected, await f.Db.ObjectAuditEvents.CountAsync(a => a.ObjectType == "client-case-document"));
        var saved = f.Created(await f.GenerateTemplateHttp(key));
        Assert.Equal(1, saved.Version);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.ObjectAuditEvents.AsNoTracking().Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Equal(2, await f.Db.CaseDocumentNotificationDispatches.CountAsync());
        Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedTemplateHttpRecoversLostLocalAcknowledgementWithoutSecondPut(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.SeedGeneratedTemplateAsync();
        var key = Guid.NewGuid();
        f.Local.LoseAcknowledgement = true;
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.GenerateTemplateHttp(key)).Result).StatusCode);
        Assert.Empty(await f.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        await f.RecreateContext();
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.GenerateTemplateHttp(key)).Result).StatusCode);
        Assert.Equal(1, f.Local.PutCount);
        await f.CopyPendingAsync();
        await f.RecreateContext();
        Assert.Equal(1, f.Created(await f.GenerateTemplateHttp(key)).Version);
        Assert.Single(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedTemplateHttpRejectsRevokedSourceBeforeReadingSavedBytes(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.SeedGeneratedTemplateAsync();
        var key = Guid.NewGuid();
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.GenerateTemplateHttp(key)).Result).StatusCode);
        await f.CopyPendingAsync();
        await f.Db.ClientCases.Where(c => c.Id == f.Case.Id).ExecuteUpdateAsync(update => update.SetProperty(c => c.CaseType, "Другой тип"));
        await f.RecreateContext();
        var reads = f.Local.ReadCount;
        Assert.IsType<ForbidResult>((await f.GenerateTemplateHttp(key)).Result);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task ManagedTemplateHttpCopiesAndExactReplay(bool postgres, int copies)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: copies);
        await f.SeedGeneratedTemplateAsync();
        var key = Guid.NewGuid();
        var request = new CasesV2Controller.GenerateCaseDocumentTemplateRequest
        { TemplateKey = "custom", DocumentType = "Иск", Status = "Подготовлен" };
        var controller = f.Controller();
        controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var first = await controller.GenerateDocumentTemplate(f.Case.Id, request, CancellationToken.None);
        if (copies == 2)
        {
            Assert.Equal(503, Assert.IsType<ObjectResult>(first.Result).StatusCode);
            Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
            Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
            Assert.Empty(await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().ToArrayAsync());
            await f.Db.SystemSettings.Where(s => s.Key == "cases.documentTemplates").ExecuteUpdateAsync(update =>
                update.SetProperty(s => s.Value, "{\"templates\":[{\"key\":\"custom\",\"label\":\"Заменённый шаблон\",\"content\":\"replacement bytes\",\"caseTypes\":[\"Гражданское\"]}]}"));
            await f.CopyPendingAsync();
            await f.RecreateContext();
            controller = f.Controller();
            controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
            first = await controller.GenerateDocumentTemplate(f.Case.Id, request, CancellationToken.None);
        }
        var document = f.Created(first);
        Assert.Equal("template-custom", document.DocumentKey);
        Assert.Equal(1, document.Version);
        Assert.Contains("Исходный шаблон", document.OriginalFileName);
        Assert.Equal(key.ToString("D"), controller.Response.Headers["X-Upload-Key"].ToString());
        var binding = await f.Db.StorageProductBindings.AsNoTracking().SingleAsync();
        await using (var stored = await f.ReadStoredGeneratedAsync(binding.StorageObjectId))
        {
            Assert.NotNull(stored);
            using var reader = new StreamReader(stored.Content);
            var html = await reader.ReadToEndAsync();
            Assert.DoesNotContain("replacement bytes", html);
            Assert.Contains("Исходный шаблон", html);
        }
        var history = (await f.Db.ClientCases.AsNoTracking().SingleAsync(c => c.Id == f.Case.Id)).EventHistory;
        var originalDispatches = await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().OrderBy(d => d.Id).Select(d => d.Id).ToArrayAsync();
        Assert.Equal(2, originalDispatches.Length);
        var clientRecipient = await f.Db.Clients.Where(c => c.Id == f.Case.ClientId).Select(c => c.ResponsibleUserId).SingleAsync();
        Assert.Equal(new[] { f.User.Id, clientRecipient!.Value }.OrderBy(id => id),
            await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().OrderBy(d => d.UserNotification.RecipientUserId).Select(d => d.UserNotification.RecipientUserId).ToArrayAsync());
        var reads = f.Local.ReadCount;
        await f.RecreateContext();
        controller = f.Controller();
        controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var replay = f.Created(await controller.GenerateDocumentTemplate(f.Case.Id, request, CancellationToken.None));
        Assert.Equal(document.Id, replay.Id);
        Assert.Equal(document.OriginalFileName, replay.OriginalFileName);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(copies - 1, f.Mirror.PutCount);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.ObjectAuditEvents.AsNoTracking().Where(e => e.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Equal(originalDispatches, await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().OrderBy(d => d.Id).Select(d => d.Id).ToArrayAsync());
        Assert.Equal(history, (await f.Db.ClientCases.AsNoTracking().SingleAsync(c => c.Id == f.Case.Id)).EventHistory);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "invalid")]
    [InlineData(false, "00000000-0000-0000-0000-000000000000")]
    [InlineData(true, "")]
    [InlineData(true, "invalid")]
    [InlineData(true, "00000000-0000-0000-0000-000000000000")]
    public async Task ManagedTemplateHttpRequiresIdentityBeforeStorage(bool postgres, string header)
    {
        await using var f = await CreateFixtureAsync(postgres);
        await f.SeedGeneratedTemplateAsync();
        var controller = f.Controller();
        if (header.Length > 0) controller.Request.Headers["X-Upload-Key"] = header;
        Assert.IsType<BadRequestObjectResult>((await controller.GenerateDocumentTemplate(f.Case.Id,
            new() { TemplateKey = "custom" }, CancellationToken.None)).Result);
        Assert.Equal(0, f.Local.PutCount);
        Assert.Equal(0, f.Local.ReadCount);
        Assert.Empty(await f.Db.StorageOperations.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageCapacityReservations.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedTemplateHttpBlocksChangedRequestAndDisabledFallback(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.SeedGeneratedTemplateAsync();
        var controller = f.Controller();
        var key = Guid.NewGuid();
        controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        var request = new CasesV2Controller.GenerateCaseDocumentTemplateRequest
        { TemplateKey = "custom", DocumentType = "Иск", Status = "Подготовлен" };
        Assert.Equal(503, Assert.IsType<ObjectResult>((await controller.GenerateDocumentTemplate(f.Case.Id, request, CancellationToken.None)).Result).StatusCode);
        var reads = f.Local.ReadCount;
        request.Status = "Изменён";
        Assert.IsType<ConflictObjectResult>((await controller.GenerateDocumentTemplate(f.Case.Id, request, CancellationToken.None)).Result);
        request.Status = "Подготовлен";
        f.Options.Enabled = false;
        Assert.Equal(503, Assert.IsType<ObjectResult>((await controller.GenerateDocumentTemplate(f.Case.Id, request, CancellationToken.None)).Result).StatusCode);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        f.Options.Enabled = true;
        await f.CopyPendingAsync();
        await f.RecreateContext();
        controller = f.Controller();
        controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
        Assert.Equal(1, f.Created(await controller.GenerateDocumentTemplate(f.Case.Id, request, CancellationToken.None)).Version);
        Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TemplateKeyMigrationRefusesDowngradeOfPublishedOrPendingLongIdentity(bool postgres, bool published)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2, migrated: true);
        await f.SeedGeneratedTemplateAsync(new string('a', 64));
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        Assert.Equal("pending", (await f.StageGeneratedTemplateAsync(Guid.NewGuid(), new string('a', 64))).Status);
        if (published)
        {
            await f.CopyPendingAsync();
            Assert.Equal(1, (await f.RunCaseQueueAsync()).Succeeded);
        }
        var migrations = f.Db.Database.GetMigrations().ToArray();
        var keyMigration = Assert.Single(migrations, m => m.Contains("_ExpandGeneratedCaseDocumentKeys", StringComparison.Ordinal));
        var precedingKeyMigration = migrations[Array.IndexOf(migrations, keyMigration) - 1];
        // Миграции, добавленные после метки корня стадии, проверяются своими тестами: снимаем их заранее, чтобы
        // проверка относилась именно к откату метки корня стадии (каждый откат SQLite — отдельная транзакция).
        var stageRootMigration = Assert.Single(migrations, m => m.Contains("_AddStageRootIdentity", StringComparison.Ordinal));
        if (!string.Equals(migrations[^1], stageRootMigration, StringComparison.Ordinal))
        {
            await f.Db.Database.MigrateAsync(stageRootMigration);
        }

        var appliedBefore = (await f.Db.Database.GetAppliedMigrationsAsync()).ToArray();
        // Exercise this migration itself without discarding newer durable storage proof.
        if (postgres)
        {
            var keyFailure = await Assert.ThrowsAsync<PostgresException>(() => ApplyOnlyDocumentKeyMigrationAsync(f.Db, keyMigration, precedingKeyMigration));
            Assert.Equal(PostgresErrorCodes.CheckViolation, keyFailure.SqlState);
            Assert.Contains("Cannot downgrade document keys", keyFailure.MessageText);
        }
        else
        {
            var keyFailure = await Assert.ThrowsAsync<SqliteException>(() => ApplyOnlyDocumentKeyMigrationAsync(f.Db, keyMigration, precedingKeyMigration));
            Assert.Equal(19, keyFailure.SqliteErrorCode);
            Assert.Contains("Blocked", keyFailure.Message);
        }
        Assert.Equal(appliedBefore, (await f.Db.Database.GetAppliedMigrationsAsync()).ToArray());
        // The full current downgrade must still refuse losing durable root identities.
        Assert.True(await f.Db.StorageCapacityReservations.AnyAsync(r => r.StageRootIdentity != null));
        if (postgres)
        {
            var latestFailure = await Assert.ThrowsAsync<PostgresException>(() => f.Db.Database.MigrateAsync(precedingKeyMigration));
            Assert.Equal(PostgresErrorCodes.RaiseException, latestFailure.SqlState);
            Assert.Equal("Cannot discard durable stage root identities", latestFailure.MessageText);
        }
        else
        {
            var latestFailure = await Assert.ThrowsAsync<SqliteException>(() => f.Db.Database.MigrateAsync(precedingKeyMigration));
            Assert.Equal(19, latestFailure.SqliteErrorCode);
            Assert.Contains("Identities", latestFailure.Message);
        }
        Assert.Equal(appliedBefore, (await f.Db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Contains(keyMigration, await f.Db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(published ? 1 : 0, await f.Db.ClientCaseDocuments.CountAsync());
        Assert.All(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync(), d => Assert.Equal(73, d.DocumentKey.Length));
        Assert.Equal(published ? 1 : 0, await f.Db.StorageProductBindings.CountAsync());
        Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemplateKeyMigrationPreservesShortDocumentAndBindingAcrossDowngradeAndUpgrade(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2, migrated: true);
        // Exercise the preceding schema's exact 64-character boundary.
        var templateKey = new string('a', 55);
        await f.SeedGeneratedTemplateAsync(templateKey);
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        Assert.Equal("pending", (await f.StageGeneratedTemplateAsync(Guid.NewGuid(), templateKey)).Status);
        await f.CopyPendingAsync();
        Assert.Equal(1, (await f.RunCaseQueueAsync()).Succeeded);
        var before = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.Equal(64, before.DocumentKey.Length);
        var binding = await f.Db.StorageProductBindings.AsNoTracking().SingleAsync();
        var migrations = f.Db.Database.GetMigrations().ToArray();
        var keyMigration = Assert.Single(migrations, m => m.Contains("_ExpandGeneratedCaseDocumentKeys", StringComparison.Ordinal));
        var precedingKeyMigration = migrations[Array.IndexOf(migrations, keyMigration) - 1];
        var appliedBefore = (await f.Db.Database.GetAppliedMigrationsAsync()).ToArray();
        var rootProofBefore = await f.Db.StorageCapacityReservations.AsNoTracking().OrderBy(r => r.IdempotencyKey).ThenBy(r => r.ResourceKey).Select(r => r.StageRootIdentity).ToArrayAsync();
        Assert.Contains(rootProofBefore, identity => identity != null);
        // Isolate the document-key change; a full latest downgrade cannot discard this proof.
        await ApplyOnlyDocumentKeyMigrationAsync(f.Db, keyMigration, precedingKeyMigration);
        Assert.DoesNotContain(keyMigration, await f.Db.Database.GetAppliedMigrationsAsync());
        await ApplyOnlyDocumentKeyMigrationAsync(f.Db, precedingKeyMigration, keyMigration);
        Assert.Equal(appliedBefore, (await f.Db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal(rootProofBefore, await f.Db.StorageCapacityReservations.AsNoTracking().OrderBy(r => r.IdempotencyKey).ThenBy(r => r.ResourceKey).Select(r => r.StageRootIdentity).ToArrayAsync());
        Assert.False(f.Db.Database.HasPendingModelChanges());
        var after = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.DocumentKey, after.DocumentKey);
        Assert.Equal(before.StoragePath, after.StoragePath);
        Assert.Equal(binding.StorageObjectId, (await f.Db.StorageProductBindings.AsNoTracking().SingleAsync()).StorageObjectId);
        Assert.Equal(1, f.Local.PutCount);
    }

    private static async Task ApplyOnlyDocumentKeyMigrationAsync(DbContext db, string from, string to)
    {
        var script = db.GetService<IMigrator>().GenerateScript(from, to, MigrationsSqlGenerationOptions.NoTransactions);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(script);
        await transaction.CommitAsync();
    }
    public static IEnumerable<object[]> InvalidCaseDocumentKeys()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var key in new[] { new string('a', 74), new string('ш', 74), "../document", "folder/document", "folder\\document", "", "   " })
                yield return [postgres, key];
    }

    [Theory]
    [MemberData(nameof(InvalidCaseDocumentKeys))]
    public async Task TemplateKeyBoundaryRejectsInvalidManualTargetBeforeStorage(bool postgres, string documentKey)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var key = Guid.NewGuid();
        Assert.IsType<BadRequestObjectResult>((await f.Upload(key, documentKey: documentKey)).Result);
        Assert.Equal(0, f.Local.PutCount);
        Assert.Equal(0, f.Local.ReadCount);
        Assert.Equal(0, f.Mirror.PutCount);
        Assert.Empty(await f.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageOperations.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageCapacityReservations.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemplateKeyBoundaryCannotCreateArbitraryManualDocumentAtNewLimit(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var key = Guid.NewGuid();
        Assert.IsType<BadRequestObjectResult>((await f.Upload(key, documentKey: new string('ш', 73))).Result);
        Assert.Equal(0, f.Local.PutCount);
        Assert.Equal(0, f.Mirror.PutCount);
        Assert.Empty(await f.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageOperations.AsNoTracking().ToArrayAsync());
        Assert.DoesNotContain(await f.Db.StorageCapacityReservations.AsNoTracking().ToArrayAsync(),
            r => r.Status == StorageCapacityReservationStatuses.Pending);
    }
    public static IEnumerable<object[]> GeneratedTemplateKeyBoundaries()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var templateKey in new[] { new string('a', 64), new string('ш', 64), "короткий-шаблон" })
                yield return [postgres, templateKey];
    }

    [Theory]
    [MemberData(nameof(GeneratedTemplateKeyBoundaries))]
    public async Task GeneratedTemplateQueueSupportsCompleteKeyReceiptReplayAndNewVersion(bool postgres, string templateKey)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2, migrated: true);
        await f.SeedGeneratedTemplateAsync(templateKey);
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        var originalKey = Guid.NewGuid();
        var expectedDocumentKey = "template-" + templateKey;
        Assert.Equal("pending", (await f.StageGeneratedTemplateAsync(originalKey, templateKey)).Status);
        var pendingStates = await new CaseDocumentUploadStatusReader(f.Db, f.Monitor)
            .ReadAsync(f.Case.Id, f.User.Id, [originalKey], CancellationToken.None);
        Assert.NotNull(pendingStates);
        var pending = Assert.Single(pendingStates);
        // The supported template source is known, but this fixture has no
        // registered hosted worker and must not promise automatic completion.
        Assert.Equal("paused", pending.Status);
        Assert.Null(pending.DocumentId);
        Assert.Null(pending.DocumentKey);
        Assert.Equal("template", pending.Generation!.Kind);
        Assert.True(pending.Generation.SourceVerified);
        Assert.Equal(templateKey, pending.Generation.TemplateKey);
        Assert.Equal("Иск", pending.Generation.DocumentType);
        Assert.Equal("Подготовлен", pending.Generation.Status);
        await f.CopyPendingAsync();
        await f.RecreateContext();
        Assert.Equal(1, (await f.RunCaseQueueAsync()).Succeeded);
        var printed = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.Equal(expectedDocumentKey, printed.DocumentKey);
        Assert.Equal(1, printed.Version);
        var publishedStates = await new CaseDocumentUploadStatusReader(f.Db, f.Monitor)
            .ReadAsync(f.Case.Id, f.User.Id, [originalKey], CancellationToken.None);
        Assert.NotNull(publishedStates);
        var published = Assert.Single(publishedStates);
        Assert.Equal("uploaded", published.Status);
        Assert.Equal(printed.Id, published.DocumentId);
        Assert.Equal(expectedDocumentKey, published.DocumentKey);

        var versionKey = Guid.NewGuid();
        var versionResponse = await f.Upload(versionKey, documentKey: expectedDocumentKey);
        // The existing upload contract reports pending required copies as 503.
        Assert.Equal(503, Assert.IsAssignableFrom<ObjectResult>(versionResponse.Result).StatusCode);
        var versionState = Assert.Single((await new CaseDocumentUploadStatusReader(f.Db, f.Monitor)
            .ReadAsync(f.Case.Id, f.User.Id, [versionKey], CancellationToken.None))!);
        // No hosted worker is registered in this fixture; do not promise automatic completion.
        Assert.Equal("paused", versionState.Status);
        Assert.Equal("evidence.txt", versionState.FileName);
        Assert.Null(versionState.DocumentId);
        Assert.Equal(2, f.Local.PutCount);
        Assert.Equal(2, await f.Db.StorageOperations.CountAsync(o =>
            o.OperationType == StorageOperationTypes.PublishCaseDocument));
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        await f.CopyPendingAsync();
        await f.RecreateContext();
        Assert.Equal(1, (await f.RunCaseQueueAsync()).Succeeded);
        var versions = await f.Db.ClientCaseDocuments.AsNoTracking().OrderBy(d => d.Version).ToArrayAsync();
        Assert.Equal(2, versions.Length);
        Assert.All(versions, d => Assert.Equal(expectedDocumentKey, d.DocumentKey));
        Assert.Equal(new[] { 1, 2 }, versions.Select(d => d.Version));
        Assert.Equal(2, await f.Db.StorageProductBindings.CountAsync());
        Assert.Equal(2, await f.Db.CaseDocumentNotificationDispatches.CountAsync());
        var reads = f.Local.ReadCount;
        var replay = await f.ResumeGenerated(originalKey);
        Assert.Equal("uploaded", replay!.Status);
        Assert.True(replay.IsReplay);
        Assert.Equal(printed.Id, replay.DocumentId);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Equal(2, f.Local.PutCount);
        Assert.Equal(2, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseDocumentModelAccommodatesCompleteTemplateKey(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres);
        Assert.Equal(73, f.Db.Model.FindEntityType(typeof(ClientCaseDocument))!
            .FindProperty(nameof(ClientCaseDocument.DocumentKey))!.GetMaxLength());
    }

    [Fact]
    public async Task ModelFixtureClonesKeepSeedsRootsAndRecreatedFaultInterceptorsIndependent()
    {
        await using var first = await CreateFixtureAsync(postgres: true, requiredCopies: 2);
        await using var second = await CreateFixtureAsync(postgres: true, requiredCopies: 2);
        Assert.NotEqual(first.Db.Database.GetDbConnection().Database, second.Db.Database.GetDbConnection().Database);
        Assert.NotEqual(first.Root, second.Root);
        Assert.Equal(first.User.Id, second.User.Id);
        Assert.Equal(first.Case.Id, second.Case.Id);
        Assert.NotSame(first.Fault, second.Fault);
        Assert.NotSame(first.StatusHook, second.StatusHook);
        Assert.Equal("false", (await first.Db.SystemSettings.SingleAsync(row => row.Key == "notifications.email.enabled")).Value);

        first.Fault.Enabled = first.Fault.Armed = true;
        first.Fault.Stage = "before-save";
        first.Db.Clients.Add(new Client { Name = "rejected first clone" });
        await Assert.ThrowsAsync<IOException>(() => first.Db.SaveChangesAsync());
        first.Fault.Enabled = false;
        first.Db.ChangeTracker.Clear();
        first.Db.Clients.Add(new Client { Name = "first clone only" });
        await first.Db.SaveChangesAsync();
        await first.RecreateContext();
        Assert.True(await first.Db.Clients.AnyAsync(row => row.Name == "first clone only"));
        Assert.False(await second.Db.Clients.AnyAsync(row => row.Name == "first clone only"));

        first.Fault.Enabled = first.Fault.Armed = true;
        first.Db.Clients.Add(new Client { Name = "recreated rejected clone" });
        await Assert.ThrowsAsync<IOException>(() => first.Db.SaveChangesAsync());
        first.Fault.Enabled = false;
        second.Db.Clients.Add(new Client { Name = "independent second clone" });
        await second.Db.SaveChangesAsync();
        await File.WriteAllTextAsync(Path.Combine(first.Root, "first-only.txt"), "synthetic");
        Assert.False(File.Exists(Path.Combine(second.Root, "first-only.txt")));
        Assert.Equal(2, await second.Db.Clients.CountAsync());

        string? failedRoot = null;
        string? failedDatabase = null;
        var setupFailure = await Assert.ThrowsAsync<IOException>(() => Fixture.CreateAsync(postgres: true, modelTemplate: modelTemplate,
            beforeReady: async fixture =>
            {
                failedRoot = fixture.Root;
                failedDatabase = fixture.Db.Database.GetDbConnection().Database;
                fixture.Fault.Enabled = fixture.Fault.Armed = true;
                fixture.Fault.Stage = "before-save";
                fixture.Db.Clients.Add(new Client { Name = "failed setup clone" });
                await fixture.Db.SaveChangesAsync();
            }));
        Assert.Equal("Owned managed case save failure", setupFailure.Message);
        Assert.NotNull(failedRoot);
        Assert.NotNull(failedDatabase);
        Assert.False(Directory.Exists(failedRoot));
        Assert.StartsWith("pp_bank_model_", failedDatabase, StringComparison.Ordinal);
        await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(System.Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES"))
        { Database = "postgres", Pooling = false }.ConnectionString);
        await observer.OpenAsync();
        await using var exists = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @name", observer);
        exists.Parameters.AddWithValue("name", failedDatabase);
        Assert.Equal(0L, (long)(await exists.ExecuteScalarAsync())!);
        Assert.Equal(2, await second.Db.Clients.CountAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GeneratedTemplateQueuePreservesFrozenHistoryAuditAndNotification(bool postgres, bool automaticAtWrite)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.SeedGeneratedTemplateAsync();
        f.Options.AutoCaseDocumentPublicationEnabled = automaticAtWrite;
        Assert.Equal("pending", (await f.StageGeneratedTemplateAsync(Guid.NewGuid())).Status);
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        await f.CasePublisher().EnsureScheduledAsync(CancellationToken.None);
        var queue = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == StorageOperationTypes.PublishCaseDocument);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToArrayAsync());
        await f.CopyPendingAsync();
        await f.RecreateContext();
        await f.Db.SystemSettings.Where(s => s.Key == "cases.documentTemplates").ExecuteUpdateAsync(update =>
            update.SetProperty(s => s.Value, "{\"templates\":[{\"key\":\"custom\",\"label\":\"Изменённый шаблон\",\"caseTypes\":[\"Гражданское\"],\"content\":\"новый текст\"}]}"));
        await f.Db.ClientCases.Where(c => c.Id == f.Case.Id).ExecuteUpdateAsync(update =>
            update.SetProperty(c => c.EventHistory, "Предшествующее событие"));
        Assert.Equal(1, (await f.RunCaseQueueAsync()).Succeeded);
        Assert.Equal(0, (await f.RunCaseQueueAsync()).Claimed);
        var doc = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.Equal("template-custom", doc.DocumentKey);
        Assert.Equal("Исходный шаблон.html", doc.OriginalFileName);
        Assert.Equal("Иск", doc.DocumentType);
        Assert.Equal("Подготовлен", doc.Status);
        var audit = await f.Db.ObjectAuditEvents.AsNoTracking().SingleAsync(a => a.ObjectType == "client-case-document");
        Assert.Equal("Generated", audit.Action);
        Assert.Equal("Сформирован документ из шаблона «Исходный шаблон.html».", audit.Description);
        var history = (await f.Db.ClientCases.AsNoTracking().SingleAsync(c => c.Id == f.Case.Id)).EventHistory;
        Assert.Contains("Предшествующее событие", history);
        Assert.Contains("Сформирован шаблон «Исходный шаблон.html»: тип «Иск», статус «Подготовлен», версия 1.", history);
        Assert.DoesNotContain("Изменённый шаблон", history);
        var notice = await f.Db.UserNotifications.AsNoTracking().SingleAsync();
        Assert.Equal("сформировал(а) документ: Исходный шаблон.html", notice.Title);
        Assert.Single(await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        await using var bytes = await f.ReadStoredGeneratedAsync((await f.Db.StorageObjects.AsNoTracking().SingleAsync()).Id);
        Assert.NotNull(bytes);
        using var reader = new StreamReader(bytes.Content, Encoding.UTF8, leaveOpen: true);
        Assert.Equal("<html>original template</html>", await reader.ReadToEndAsync());
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(1, f.Mirror.PutCount);
        Assert.Equal(StorageOperationStatuses.Succeeded,
            (await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == queue.Id)).Status);
    }

    public static IEnumerable<object[]> GeneratedTemplateSourceBoundaries()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var boundary in new[] { "inactive", "case-type", "permission" })
                foreach (var afterRead in new[] { false, true })
                    yield return [postgres, boundary, afterRead];
    }

    public static IEnumerable<object[]> GeneratedTemplateSnapshotBoundaries()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var boundary in new[] { "version", "template-version", "case", "template-key", "requested-key", "name", "type", "status" })
                yield return [postgres, boundary];
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GeneratedTemplateTypedResumePreservesFrozenBytesAndExactReceipt(bool postgres, bool reservedOnly)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.SeedGeneratedTemplateAsync();
        var key = Guid.NewGuid();
        f.Local.LoseAcknowledgement = reservedOnly;
        Assert.Equal(reservedOnly ? "unavailable" : "pending", (await f.StageGeneratedTemplateAsync(key)).Status);
        await f.Db.SystemSettings.Where(s => s.Key == "cases.documentTemplates").ExecuteUpdateAsync(update =>
            update.SetProperty(s => s.Value, "{\"templates\":[{\"key\":\"custom\",\"label\":\"Новое название\",\"content\":\"changed template\",\"caseTypes\":[\"Гражданское\"]}]}"));
        await f.RecreateContext();
        Assert.Equal("pending", (await f.ResumeTemplate(key))!.Status);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        await f.CopyPendingAsync();
        await f.RecreateContext();
        var result = await f.ResumeTemplate(key);
        Assert.Equal("uploaded", result!.Status);
        var document = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.Equal(result.DocumentId, document.Id);
        Assert.Equal("template-custom", document.DocumentKey);
        Assert.Equal("Исходный шаблон.html", document.OriginalFileName);
        Assert.Equal(1, document.Version);
        var binding = await f.Db.StorageProductBindings.AsNoTracking().SingleAsync();
        await using (var stored = await f.ReadStoredGeneratedAsync(binding.StorageObjectId))
        {
            Assert.NotNull(stored);
            using var body = new StreamReader(stored.Content);
            Assert.Equal("<html>original template</html>", await body.ReadToEndAsync());
        }
        var reads = f.Local.ReadCount;
        var replay = await f.ResumeTemplate(key);
        Assert.Equal("uploaded", replay!.Status);
        Assert.True(replay.IsReplay);
        Assert.Equal(document.Id, replay.DocumentId);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(1, f.Mirror.PutCount);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.ObjectAuditEvents.AsNoTracking().Where(e => e.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Single(await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().ToArrayAsync());
        var history = (await f.Db.ClientCases.AsNoTracking().SingleAsync(c => c.Id == f.Case.Id)).EventHistory;
        Assert.Contains("Сформирован шаблон «Исходный шаблон.html»", history);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedTemplateTypedResumeRejectsReservedSourceBeforeProviderRead(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.SeedGeneratedTemplateAsync();
        var key = Guid.NewGuid();
        f.Local.LoseAcknowledgement = true;
        Assert.Equal("unavailable", (await f.StageGeneratedTemplateAsync(key)).Status);
        await f.Db.ClientCases.Where(c => c.Id == f.Case.Id).ExecuteUpdateAsync(update =>
            update.SetProperty(c => c.CaseType, "Уголовное"));
        await f.RecreateContext();
        var reads = f.Local.ReadCount;
        Assert.Equal("forbidden", (await f.ResumeGenerated(key, authorizeSource: f.TemplatePublication().CanPublishAsync))!.Status);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Empty(await f.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.NotEmpty(await f.Db.StorageCapacityReservations.AsNoTracking().Where(r =>
            r.Status == StorageCapacityReservationStatuses.Pending && r.ProductContextJson != null).ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GeneratedTemplateTypedResumeRollsBackLateSourceOrFlagChange(bool postgres, bool disabled)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.SeedGeneratedTemplateAsync();
        var key = Guid.NewGuid();
        Assert.Equal("pending", (await f.StageGeneratedTemplateAsync(key)).Status);
        await f.CopyPendingAsync();
        f.Fault.Enabled = true;
        f.Fault.Stage = "source-change";
        f.Fault.AfterDomainSave = () =>
        {
            if (disabled) f.Options.UploadCapacityChecksEnabled = false;
            else f.Db.ClientCases.Where(c => c.Id == f.Case.Id).ExecuteUpdate(update =>
                update.SetProperty(c => c.CaseType, "Уголовное"));
        };
        Assert.Equal(disabled ? "disabled" : "forbidden",
            (await f.ResumeTemplate(key))!.Status);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.ObjectAuditEvents.AsNoTracking().Where(e => e.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Empty(await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().ToArrayAsync());
        Assert.DoesNotContain("Сформирован шаблон", (await f.Db.ClientCases.AsNoTracking().SingleAsync(c => c.Id == f.Case.Id)).EventHistory ?? "");
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(1, f.Mirror.PutCount);
        // A rejected transaction preserves the original copies and can be resumed.
        f.Fault.Enabled = false;
        f.Options.UploadCapacityChecksEnabled = true;
        await f.RecreateContext();
        Assert.Equal("Гражданское", (await f.Db.ClientCases.AsNoTracking().SingleAsync(c => c.Id == f.Case.Id)).CaseType);
        Assert.Equal("uploaded", (await f.ResumeTemplate(key))!.Status);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GeneratedTemplateInitialPublicationRollsBackLateSourceOrFlagChange(bool postgres, bool disabled)
    {
        await using var f = await CreateFixtureAsync(postgres);
        await f.SeedGeneratedTemplateAsync();
        var key = Guid.NewGuid();
        f.Fault.Enabled = true;
        f.Fault.Stage = "source-change";
        f.Fault.AfterDomainSave = () =>
        {
            if (disabled) f.Options.UploadCapacityChecksEnabled = false;
            else f.Db.ClientCases.Where(c => c.Id == f.Case.Id).ExecuteUpdate(update =>
                update.SetProperty(c => c.CaseType, "Уголовное"));
        };
        Assert.Equal(disabled ? "disabled" : "forbidden",
            (await f.StageGeneratedTemplateAsync(key, publishTemplate: true)).Status);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.ObjectAuditEvents.AsNoTracking().Where(e => e.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Empty(await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().ToArrayAsync());
        Assert.DoesNotContain("Сформирован шаблон", (await f.Db.ClientCases.AsNoTracking().SingleAsync(c => c.Id == f.Case.Id)).EventHistory ?? "");
        f.Fault.Enabled = false;
        f.Options.UploadCapacityChecksEnabled = true;
        await f.RecreateContext();
        Assert.Equal("uploaded", (await f.StageGeneratedTemplateAsync(key, publishTemplate: true)).Status);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(0, f.Mirror.PutCount);
    }

    [Theory]
    [MemberData(nameof(GeneratedTemplateSnapshotBoundaries))]
    public async Task GeneratedTemplateTypedResumeRejectsUnboundSourceBeforeProviderRead(bool postgres, string boundary)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.SeedGeneratedTemplateAsync();
        var key = Guid.NewGuid();
        Assert.Equal("pending", (await f.StageGeneratedTemplateAsync(key)).Status);
        await f.CopyPendingAsync();
        var commit = await f.Db.StorageOperations.SingleAsync(o => o.OperationType == "commit");
        var payload = System.Text.Json.Nodes.JsonNode.Parse(commit.PayloadJson!)!;
        var generation = payload["generation"]!;
        var snapshot = System.Text.Json.Nodes.JsonNode.Parse(generation["frozenDomainJson"]!.GetValue<string>())!;
        if (boundary == "version") snapshot["version"] = 0;
        if (boundary == "template-version") snapshot["templateVersion"] = 0;
        if (boundary == "case") snapshot["caseId"] = f.Case.Id + 100;
        if (boundary == "template-key") snapshot["templateKey"] = "foreign";
        if (boundary == "requested-key") snapshot["requestedTemplateKey"] = "foreign";
        if (boundary == "name") snapshot["fileName"] = "Чужой.html";
        if (boundary == "type") snapshot["documentType"] = "Договор";
        if (boundary == "status") snapshot["documentStatus"] = "Согласован";
        generation["frozenDomainJson"] = snapshot.ToJsonString();
        commit.PayloadJson = payload.ToJsonString();
        await f.Db.SaveChangesAsync();
        var reads = f.Local.ReadCount;
        Assert.Equal("forbidden", (await f.ResumeGenerated(key, authorizeSource: f.TemplatePublication().CanPublishAsync))!.Status);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(1, f.Mirror.PutCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [MemberData(nameof(GeneratedTemplateSourceBoundaries))]
    public async Task GeneratedTemplateTypedResumeRechecksSourceAndRightsAcrossRead(bool postgres, string boundary, bool afterRead)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.SeedGeneratedTemplateAsync();
        var key = Guid.NewGuid();
        Assert.Equal("pending", (await f.StageGeneratedTemplateAsync(key)).Status);
        await f.CopyPendingAsync();
        async Task ChangeSource()
        {
            if (boundary == "inactive") await f.Db.SystemSettings.Where(s => s.Key == "cases.documentTemplates").ExecuteUpdateAsync(update =>
                update.SetProperty(s => s.Value, "{\"templates\":[{\"key\":\"custom\",\"label\":\"Исходный шаблон\",\"isActive\":false}]}"));
            if (boundary == "case-type") await f.Db.ClientCases.Where(c => c.Id == f.Case.Id).ExecuteUpdateAsync(update =>
                update.SetProperty(c => c.CaseType, "Уголовное"));
            if (boundary == "permission") await f.Db.RolePermissions.Where(p => p.RoleId == f.User.RoleId).ExecuteUpdateAsync(update =>
                update.SetProperty(p => p.CanEdit, false));
        }
        if (afterRead) f.Local.AfterRead = async () => { f.Local.AfterRead = null; await ChangeSource(); };
        else await ChangeSource();
        var reads = f.Local.ReadCount;
        Assert.Equal("forbidden", (await f.ResumeGenerated(key, authorizeSource: f.TemplatePublication().CanPublishAsync))!.Status);
        Assert.Equal(reads + (afterRead ? 1 : 0), f.Local.ReadCount);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(1, f.Mirror.PutCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [MemberData(nameof(GeneratedTemplateSnapshotBoundaries))]
    public async Task GeneratedTemplateQueueRejectsUnboundSnapshotBeforeProviderRead(bool postgres, string boundary)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.SeedGeneratedTemplateAsync();
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        Assert.Equal("pending", (await f.StageGeneratedTemplateAsync(Guid.NewGuid())).Status);
        await f.CopyPendingAsync();
        var commit = await f.Db.StorageOperations.SingleAsync(o => o.OperationType == "commit");
        var payload = System.Text.Json.Nodes.JsonNode.Parse(commit.PayloadJson!)!;
        var generation = payload["generation"]!;
        var snapshot = System.Text.Json.Nodes.JsonNode.Parse(generation["frozenDomainJson"]!.GetValue<string>())!;
        if (boundary == "version") snapshot["version"] = 0;
        if (boundary == "template-version") snapshot["templateVersion"] = 0;
        if (boundary == "case") snapshot["caseId"] = f.Case.Id + 100;
        if (boundary == "template-key") snapshot["templateKey"] = "foreign";
        if (boundary == "requested-key") snapshot["requestedTemplateKey"] = "foreign";
        if (boundary == "name") snapshot["fileName"] = "Чужой.html";
        if (boundary == "type") snapshot["documentType"] = "Договор";
        if (boundary == "status") snapshot["documentStatus"] = "Согласован";
        generation["frozenDomainJson"] = snapshot.ToJsonString();
        commit.PayloadJson = payload.ToJsonString();
        await f.Db.SaveChangesAsync();
        var reads = f.Local.ReadCount;
        Assert.Equal(1, (await f.RunCaseQueueAsync()).DeadLettered);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.ObjectAuditEvents.AsNoTracking().Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(1, f.Mirror.PutCount);
        Assert.Equal(2, await f.Db.StorageReplicas.CountAsync(r => r.Status == StorageReplicaStatuses.Available));
    }

    [Theory]
    [MemberData(nameof(GeneratedTemplateSourceBoundaries))]
    public async Task GeneratedTemplateQueueRechecksCurrentSelectionApplicabilityAndRights(bool postgres, string boundary, bool afterRead)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.SeedGeneratedTemplateAsync();
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        Assert.Equal("pending", (await f.StageGeneratedTemplateAsync(Guid.NewGuid())).Status);
        await f.CopyPendingAsync();
        async Task ChangeSource()
        {
            if (boundary == "inactive") await f.Db.SystemSettings.Where(s => s.Key == "cases.documentTemplates").ExecuteUpdateAsync(update =>
                update.SetProperty(s => s.Value, "{\"templates\":[{\"key\":\"custom\",\"label\":\"Исходный шаблон\",\"isActive\":false}]}"));
            if (boundary == "case-type") await f.Db.ClientCases.Where(c => c.Id == f.Case.Id).ExecuteUpdateAsync(update =>
                update.SetProperty(c => c.CaseType, "Уголовное"));
            if (boundary == "permission") await f.Db.RolePermissions.Where(p => p.RoleId == f.User.RoleId).ExecuteUpdateAsync(update =>
                update.SetProperty(p => p.CanEdit, false));
        }
        if (afterRead) f.Local.AfterRead = async () => { f.Local.AfterRead = null; await ChangeSource(); };
        else await ChangeSource();
        var reads = f.Local.ReadCount;
        Assert.Equal(1, (await f.RunCaseQueueAsync()).DeadLettered);
        Assert.Equal(reads + (afterRead ? 1 : 0), f.Local.ReadCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.ObjectAuditEvents.AsNoTracking().Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Equal(2, await f.Db.StorageReplicas.CountAsync(r => r.Status == StorageReplicaStatuses.Available));
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, "before-commit")]
    [InlineData(true, "before-commit")]
    [InlineData(false, "lost-commit")]
    [InlineData(true, "lost-commit")]
    public async Task GeneratedTemplateQueueRecoversAtomicFailureWithoutDuplicateRecords(bool postgres, string stage)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.SeedGeneratedTemplateAsync();
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        Assert.Equal("pending", (await f.StageGeneratedTemplateAsync(Guid.NewGuid())).Status);
        await f.CopyPendingAsync();
        await f.RecreateContext();
        f.Fault.Stage = stage;
        f.Fault.Enabled = true;
        Assert.Equal(1, (await f.RunCaseQueueAsync()).Retried);
        var committed = stage == "lost-commit";
        Assert.Equal(committed ? 1 : 0, await f.Db.ClientCaseDocuments.CountAsync());
        Assert.Equal(committed ? 1 : 0, await f.Db.StorageProductBindings.CountAsync());
        Assert.Equal(committed ? 1 : 0, await f.Db.UserNotifications.CountAsync());
        Assert.Equal(committed ? 1 : 0, await f.Db.CaseDocumentNotificationDispatches.CountAsync());
        f.Fault.Enabled = false;
        await f.RecreateContext();
        var due = DateTime.UtcNow.AddSeconds(-1);
        await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishCaseDocument).ExecuteUpdateAsync(update =>
            update.SetProperty(o => o.NextAttemptAt, (DateTime?)due));
        Assert.Equal(1, (await f.RunCaseQueueAsync()).Succeeded);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.ObjectAuditEvents.AsNoTracking().Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Single(await f.Db.UserNotifications.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(1, f.Mirror.PutCount);
    }

    public static IEnumerable<object[]> GeneratedReplayProofBoundaries()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var boundary in new[] { "owner", "owner-type", "class", "generation", "soft-delete", "unknown", "commit-generation", "size", "sha" })
                yield return [postgres, boundary];
    }

    [Theory]
    [MemberData(nameof(GeneratedReplayProofBoundaries))]
    public async Task GeneratedReplayRejectsUnprovenOwnerAndGenerationBeforeProviderRead(bool postgres, string boundary)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var key = Guid.NewGuid();
        Assert.Equal("pending", (await f.Generate(key, new("template", new string('a', 64), "{}"))).Status);
        var obj = await f.Db.StorageObjects.SingleAsync();
        var commit = await f.Db.StorageOperations.SingleAsync(o => o.OperationType == "commit");
        var generation = await f.Db.StorageObjectGenerations.SingleAsync();
        switch (boundary)
        {
            case "owner": obj.OwnerId = "foreign-owner"; break;
            case "owner-type": obj.OwnerType = "task-file"; break;
            case "class": obj.DataClass = "task-file"; break;
            case "generation": obj.CurrentGeneration++; break;
            case "soft-delete": obj.IsDeleted = true; break;
            case "unknown": commit.Status = StorageOperationStatuses.Unknown; break;
            case "commit-generation": commit.StorageObjectGenerationId = null; break;
            case "size": generation.SizeBytes++; break;
            case "sha": generation.Sha256 = new string('f', 64); break;
        }
        await f.Db.SaveChangesAsync();
        var reads = f.Local.ReadCount;
        Assert.Equal(boundary == "unknown" ? "unavailable" : "conflict", (await f.ResumeGenerated(key))!.Status);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedReplayProvesConcurrentPublishedWinnerBeforeProviderRead(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var key = Guid.NewGuid();
        var generation = new ManagedCaseDocumentGeneration("template", new string('a', 64), "{\"source\":\"original\"}");
        await f.Generate(key, generation);
        await f.CopyPendingAsync();
        var reads = f.Local.ReadCount;
        int? winner = null;
        await using var sibling = f.OpenSiblingContext();
        Assert.True(await sibling.RolePermissions.AsNoTracking().AnyAsync());
        f.StatusHook.BeforeGeneratedProofRead = async () =>
        {
            f.StatusHook.BeforeGeneratedProofRead = null;
            var result = await f.Generate(key, generation, scope: sibling);
            Assert.Equal("uploaded", result.Status);
            winner = result.DocumentId;
        };
        var replay = await f.ResumeGenerated(key);
        Assert.NotNull(winner);
        Assert.Equal("uploaded", replay!.Status);
        Assert.True(replay.IsReplay);
        Assert.Equal(winner, replay.DocumentId);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Single(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Single(await f.Db.StorageProductBindings.ToArrayAsync());
        Assert.Single(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedReplayRejectsChangedFrozenMetadataDuringProviderRead(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var key = Guid.NewGuid();
        await f.Generate(key, new("template", new string('a', 64), "{}"));
        await f.CopyPendingAsync();
        f.Local.AfterRead = async () =>
        {
            f.Local.AfterRead = null;
            var commit = await f.Db.StorageOperations.SingleAsync(o => o.OperationType == "commit");
            var json = System.Text.Json.Nodes.JsonNode.Parse(commit.PayloadJson!)!;
            json["actorName"] = "Changed frozen actor";
            commit.PayloadJson = json.ToJsonString();
            await f.Db.SaveChangesAsync();
        };
        Assert.Equal("conflict", (await f.ResumeGenerated(key))!.Status);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false, "unknown")]
    [InlineData(true, "unknown")]
    [InlineData(false, "foreign-generation")]
    [InlineData(true, "foreign-generation")]
    public async Task GeneratedReplayRequiresExactPublishedCommitScopeWithoutProviderRead(bool postgres, string boundary)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var key = Guid.NewGuid();
        Assert.Equal("uploaded", (await f.Generate(key, new("template", new string('a', 64), "{}"))).Status);
        var commit = await f.Db.StorageOperations.SingleAsync(o => o.OperationType == "commit");
        if (boundary == "unknown") commit.Status = StorageOperationStatuses.Unknown;
        else
        {
            var original = await f.Db.StorageObjectGenerations.SingleAsync();
            var foreign = new StorageObject
            {
                DataClass = "case-document",
                OwnerType = "case-document",
                OwnerId = "foreign",
                LogicalName = "foreign.html",
                CurrentGeneration = original.Generation
            };
            var generation = new StorageObjectGeneration
            {
                StorageObject = foreign,
                Generation = original.Generation,
                SizeBytes = original.SizeBytes,
                Sha256 = original.Sha256
            };
            f.Db.StorageObjectGenerations.Add(generation);
            commit.StorageObjectGeneration = generation;
        }
        await f.Db.SaveChangesAsync();
        var reads = f.Local.ReadCount;
        Assert.Equal(boundary == "unknown" ? "unavailable" : "conflict", (await f.ResumeGenerated(key))!.Status);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Single(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Single(await f.Db.StorageProductBindings.ToArrayAsync());
    }


    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task GeneratedFoundationPreservesFrozenDataAndSeparatesUploadConsumer(bool postgres, int copies)
    {
        await using var f = await CreateFixtureAsync(postgres, copies);
        var key = Guid.NewGuid();
        var generation = new ManagedCaseDocumentGeneration("template", new string('a', 64), "{\"source\":\"original\"}");
        Assert.Null(await f.ResumeGenerated(key));
        var result = await f.Generate(key, generation);
        Assert.Equal(copies == 1 ? "uploaded" : "pending", result.Status);
        Assert.Equal(copies == 1 ? 1 : 0, await f.Db.ClientCaseDocuments.CountAsync());
        var commit = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == "commit");
        Assert.Contains("original", commit.PayloadJson);
        Assert.Empty(await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishCaseDocument).ToArrayAsync());
        Assert.Equal("invalid", (await f.ResumeAsync(commit.Id)).Status);
        Assert.Equal("conflict", (await f.ResumeGenerated(key, hash: new string('b', 64)))!.Status);
        Assert.Equal("conflict", (await f.ResumeGenerated(key, kind: "work-order"))!.Status);
        Assert.Equal("conflict", (await f.Managed.UploadAsync(f.GeneratedRequest(key),
            (_, token) => f.Db.SaveChangesAsync(token), f.GeneratedAtomic, CancellationToken.None)).Status);
        if (copies == 2)
        {
            f.Options.AutoCaseDocumentPublicationEnabled = true;
            await f.RunCaseQueueAsync();
            Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
            Assert.Empty(await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishCaseDocument).ToArrayAsync());
        }
        await f.RecreateContext();
        var resumed = await f.ResumeGenerated(key);
        Assert.Equal("uploaded", resumed!.Status);
        Assert.Equal(resumed.DocumentId, (await f.ResumeGenerated(key))!.DocumentId);
        Assert.Single(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Single(await f.Db.StorageProductBindings.ToArrayAsync());
        var audit = Assert.Single(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Equal("Generated", audit.Action);
        Assert.Equal(generation.FrozenDomainJson, audit.Description);
        Assert.Equal(1, f.Local.PutCount);
        var document = await f.Db.ClientCaseDocuments.SingleAsync();
        Assert.Equal("original generated bytes", await File.ReadAllTextAsync(f.CaseStorage.ResolvePath(document.StoragePath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedFoundationRechecksAuthorityInsidePublication(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var calls = 0;
        var result = await f.Generate(Guid.NewGuid(), new("template", new string('a', 64), "{}"),
            (_, _, _) => Task.FromResult(++calls == 1));
        Assert.Equal("forbidden", result.Status);
        Assert.Equal(2, calls);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.ObjectAuditEvents.AsNoTracking().Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedFoundationKeepsUncertainReservationAndCorruptionFromNewRendering(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var key = Guid.NewGuid();
        f.Local.LoseAcknowledgement = true;
        Assert.Equal("unavailable", (await f.Generate(key, new("template", new string('a', 64), "{}"))).Status);
        f.Local.BadHead = true;
        Assert.Equal("unavailable", (await f.ResumeGenerated(key))!.Status);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        await f.Db.StorageCapacityReservations.Where(r => r.ProductContextJson != null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ProductContextJson, (string?)null));
        Assert.Equal("conflict", (await f.ResumeGenerated(key))!.Status);
        Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task GeneratedReservedReplayRecoversExactBytesAfterLostAcknowledgement(bool postgres, int copies)
    {
        await using var f = await CreateFixtureAsync(postgres, copies);
        var key = Guid.NewGuid();
        f.Local.LoseAcknowledgement = true;
        var frozen = new ManagedCaseDocumentGeneration("template", new string('a', 64), "{\"source\":\"original\"}");
        Assert.Equal("unavailable", (await f.Generate(key, frozen)).Status);
        var before = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync(r => r.ProductContextJson != null);
        await f.RecreateContext();
        var resumed = await f.ResumeGenerated(key);
        Assert.Equal(copies == 1 ? "uploaded" : "pending", resumed!.Status);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(before.ObjectId, (await f.Db.StorageObjects.AsNoTracking().SingleAsync()).Id);
        if (copies == 2)
        {
            Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
            await f.CopyPendingAsync();
            resumed = await f.ResumeGenerated(key);
        }
        Assert.Equal("uploaded", resumed!.Status);
        Assert.Equal(resumed.DocumentId, (await f.ResumeGenerated(key))!.DocumentId);
        var audit = await f.Db.ObjectAuditEvents.AsNoTracking().SingleAsync(a => a.ObjectType == "client-case-document");
        Assert.Equal(frozen.FrozenDomainJson, audit.Description);
        Assert.Single(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        await using var read = await f.ReadStoredGeneratedAsync(before.ObjectId);
        Assert.NotNull(read);
        using var body = new StreamReader(read.Content);
        Assert.Equal("original generated bytes", await body.ReadToEndAsync());
        Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedReservedReplayRejectsChangedFinalReceipt(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var key = Guid.NewGuid();
        f.Local.LoseAcknowledgement = true;
        Assert.Equal("unavailable", (await f.Generate(key, new("template", new string('a', 64), "{}"))).Status);
        f.AfterGeneratedPublication = async () =>
        {
            f.AfterGeneratedPublication = null;
            var commit = await f.Db.StorageOperations.SingleAsync(o => o.OperationType == "commit");
            var payload = System.Text.Json.Nodes.JsonNode.Parse(commit.PayloadJson!)!;
            payload["actorName"] = "Changed frozen actor";
            commit.PayloadJson = payload.ToJsonString();
            await f.Db.SaveChangesAsync();
        };
        Assert.Equal("conflict", (await f.ResumeGenerated(key))!.Status);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false, "head")]
    [InlineData(true, "head")]
    [InlineData(false, "body")]
    [InlineData(true, "body")]
    [InlineData(false, "metadata")]
    [InlineData(true, "metadata")]
    public async Task GeneratedReservedReplayRetainsUncertainOrChangedSource(bool postgres, string boundary)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var key = Guid.NewGuid();
        f.Local.LoseAcknowledgement = true;
        Assert.Equal("unavailable", (await f.Generate(key, new("template", new string('a', 64), "{}"))).Status);
        f.Local.BadHead = boundary == "head";
        f.Local.BadRead = boundary == "body";
        if (boundary == "metadata") f.Local.AfterRead = async () =>
        {
            f.Local.AfterRead = null;
            await f.Db.StorageCapacityReservations.Where(r => r.ProductContextJson != null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ProductContextJson, (string?)null));
        };
        Assert.Equal("unavailable", (await f.ResumeGenerated(key))!.Status);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Empty(await f.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.NotEmpty(await f.Db.StorageCapacityReservations.AsNoTracking()
            .Where(r => r.Status == StorageCapacityReservationStatuses.Pending).ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GeneratedWorkOrderQueuePreservesFrozenHistoryAuditAndNotification(bool postgres, bool automaticAtWrite)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        var order = await f.SeedGeneratedWorkOrderAsync();
        f.Options.AutoCaseDocumentPublicationEnabled = automaticAtWrite;
        Assert.Equal("pending", (await f.StageGeneratedWorkOrderAsync(order, Guid.NewGuid())).Status);
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        await f.CasePublisher().EnsureScheduledAsync(CancellationToken.None);
        var queue = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == StorageOperationTypes.PublishCaseDocument);
        Assert.Equal(0, queue.AttemptCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToArrayAsync());
        await f.CopyPendingAsync();
        await f.RecreateContext();
        await f.Db.ClientCaseWorkOrders.Where(o => o.Id == order.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Title, "Новое название после подготовки"));
        await f.Db.ClientCases.Where(c => c.Id == f.Case.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.EventHistory, "Предшествующее событие"));
        Assert.Equal(1, (await f.RunCaseQueueAsync()).Succeeded);
        Assert.Equal(0, (await f.RunCaseQueueAsync()).Claimed);
        var doc = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.Equal($"work-order-{order.Id}", doc.DocumentKey);
        Assert.Equal($"case-{f.Case.Id}-work-order.html", doc.OriginalFileName);
        Assert.Equal("Согласован", doc.Status);
        var audit = await f.Db.ObjectAuditEvents.AsNoTracking().SingleAsync(a => a.ObjectType == "client-case-document");
        Assert.Equal("Generated", audit.Action);
        Assert.Equal($"Сформирована печатная форма заказ-наряда «{order.Title}».", audit.Description);
        var history = (await f.Db.ClientCases.AsNoTracking().SingleAsync(c => c.Id == f.Case.Id)).EventHistory;
        Assert.Contains("Предшествующее событие", history);
        Assert.Contains($"Заказ-наряд «{order.Title}» сохранен в документы дела.", history);
        Assert.DoesNotContain("Новое название после подготовки", history);
        Assert.DoesNotContain("загружен", history);
        var notice = await f.Db.UserNotifications.AsNoTracking().SingleAsync();
        Assert.Equal($"сформировал(а) документ: {doc.OriginalFileName}", notice.Title);
        using var metadata = System.Text.Json.JsonDocument.Parse(notice.MetadataJson!);
        Assert.Equal("generated", metadata.RootElement.GetProperty("action").GetString());
        Assert.Equal(doc.Id, metadata.RootElement.GetProperty("clientCaseDocumentId").GetInt32());
        Assert.Equal(f.User.Id, metadata.RootElement.GetProperty("actorUserId").GetInt32());
        Assert.Single(await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(1, f.Mirror.PutCount);
        Assert.Equal(StorageOperationStatuses.Succeeded,
            (await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == queue.Id)).Status);
    }

    [Theory]
    [InlineData(false, "deleted")]
    [InlineData(true, "deleted")]
    [InlineData(false, "foreign-case")]
    [InlineData(true, "foreign-case")]
    [InlineData(false, "permission")]
    [InlineData(true, "permission")]
    public async Task GeneratedWorkOrderQueueRequiresCurrentSourceAndPermissionBeforeRead(bool postgres, string boundary)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        var order = await f.SeedGeneratedWorkOrderAsync();
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        Assert.Equal("pending", (await f.StageGeneratedWorkOrderAsync(order, Guid.NewGuid())).Status);
        await f.CopyPendingAsync();
        if (boundary == "deleted") await f.Db.ClientCaseWorkOrders.Where(o => o.Id == order.Id).ExecuteDeleteAsync();
        if (boundary == "permission") await f.Db.RolePermissions.Where(p => p.RoleId == f.User.RoleId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CanEdit, false));
        if (boundary == "foreign-case")
        {
            var other = new ClientCase { ClientId = f.Case.ClientId, ResponsibleUserId = f.User.Id, Title = "Другое дело", Status = ClientCaseStatus.Open };
            f.Db.ClientCases.Add(other);
            await f.Db.SaveChangesAsync();
            await f.Db.ClientCaseWorkOrders.Where(o => o.Id == order.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.ClientCaseId, other.Id));
        }
        var reads = f.Local.ReadCount;
        Assert.Equal(1, (await f.RunCaseQueueAsync()).DeadLettered);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.ObjectAuditEvents.AsNoTracking().Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToArrayAsync());
        Assert.Equal(2, await f.Db.StorageReplicas.CountAsync(r => r.Status == StorageReplicaStatuses.Available));
        Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false, "source")]
    [InlineData(true, "source")]
    [InlineData(false, "lease")]
    [InlineData(true, "lease")]
    [InlineData(false, "flag")]
    [InlineData(true, "flag")]
    public async Task GeneratedWorkOrderQueueRechecksSourceLeaseAndFlagAfterRead(bool postgres, string boundary)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        var order = await f.SeedGeneratedWorkOrderAsync();
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        Assert.Equal("pending", (await f.StageGeneratedWorkOrderAsync(order, Guid.NewGuid())).Status);
        await f.CopyPendingAsync();
        f.Local.AfterRead = async () =>
        {
            f.Local.AfterRead = null;
            if (boundary == "source") await f.Db.ClientCaseWorkOrders.Where(o => o.Id == order.Id).ExecuteDeleteAsync();
            if (boundary == "flag") f.Options.AutoCaseDocumentPublicationEnabled = false;
            if (boundary == "lease")
            {
                var until = DateTime.UtcNow.AddMinutes(3);
                await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishCaseDocument)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.LeaseOwner, "replacement-worker").SetProperty(o => o.LeaseExpiresAt, (DateTime?)until));
            }
        };
        await f.RunCaseQueueAsync();
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToArrayAsync());
        var queue = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == StorageOperationTypes.PublishCaseDocument);
        Assert.Equal(boundary == "source" ? StorageOperationStatuses.DeadLetter
            : boundary == "flag" ? StorageOperationStatuses.Pending : StorageOperationStatuses.Running, queue.Status);
        if (boundary == "flag") Assert.Equal(0, queue.AttemptCount);
        if (boundary == "lease") Assert.Equal("replacement-worker", queue.LeaseOwner);
        Assert.Equal(2, await f.Db.StorageReplicas.CountAsync(r => r.Status == StorageReplicaStatuses.Available));
        Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false, "before-commit")]
    [InlineData(true, "before-commit")]
    [InlineData(false, "lost-commit")]
    [InlineData(true, "lost-commit")]
    public async Task GeneratedWorkOrderQueueRecoversAtomicFailureWithoutDuplicateRecords(bool postgres, string stage)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        var order = await f.SeedGeneratedWorkOrderAsync();
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        Assert.Equal("pending", (await f.StageGeneratedWorkOrderAsync(order, Guid.NewGuid())).Status);
        await f.CopyPendingAsync();
        await f.RecreateContext();
        f.Fault.Stage = stage;
        f.Fault.Enabled = true;
        await f.RunCaseQueueAsync();
        var committed = stage == "lost-commit";
        Assert.Equal(committed ? 1 : 0, await f.Db.ClientCaseDocuments.CountAsync());
        Assert.Equal(committed ? 1 : 0, await f.Db.StorageProductBindings.CountAsync());
        Assert.Equal(committed ? 1 : 0, await f.Db.UserNotifications.CountAsync());
        Assert.Equal(committed ? 1 : 0, await f.Db.CaseDocumentNotificationDispatches.CountAsync());
        f.Fault.Enabled = false;
        await f.RecreateContext();
        var due = DateTime.UtcNow.AddSeconds(-1);
        await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishCaseDocument)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, (DateTime?)due));
        Assert.Equal(1, (await f.RunCaseQueueAsync()).Succeeded);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.ObjectAuditEvents.AsNoTracking().Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Single(await f.Db.UserNotifications.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task GeneratedReplayRejectsTombstoneRecordBeforeProviderRead(bool postgres, bool published)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: published ? 1 : 2);
        var key = Guid.NewGuid();
        Assert.Equal(published ? "uploaded" : "pending", (await f.Generate(key, new("template", new string('a', 64), "{}"))).Status);
        var obj = await f.Db.StorageObjects.AsNoTracking().SingleAsync();
        Assert.False(obj.IsDeleted);
        f.Db.StorageTombstones.Add(new StorageTombstone { StorageObjectId = obj.Id, Generation = obj.CurrentGeneration });
        await f.Db.SaveChangesAsync();
        var reads = f.Local.ReadCount;
        var result = await f.ResumeGenerated(key);
        Assert.Equal("conflict", result!.Status);
        Assert.Equal(reads, f.Local.ReadCount);
        await using var read = await f.ReadStoredGeneratedAsync(obj.Id);
        Assert.Null(read);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Equal(published ? 1 : 0, await f.Db.ClientCaseDocuments.CountAsync());
        Assert.Equal(published ? 1 : 0, await f.Db.StorageProductBindings.CountAsync());
        Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false, "version")]
    [InlineData(true, "version")]
    [InlineData(false, "source-id")]
    [InlineData(true, "source-id")]
    [InlineData(false, "case-id")]
    [InlineData(true, "case-id")]
    [InlineData(false, "document-key")]
    [InlineData(true, "document-key")]
    [InlineData(false, "document-type")]
    [InlineData(true, "document-type")]
    [InlineData(false, "unsupported-kind")]
    [InlineData(true, "unsupported-kind")]
    [InlineData(false, "kind-snapshot-mismatch")]
    [InlineData(true, "kind-snapshot-mismatch")]
    public async Task GeneratedWorkOrderQueueRejectsUnboundSnapshotBeforeProviderRead(bool postgres, string boundary)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        var order = await f.SeedGeneratedWorkOrderAsync();
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        Assert.Equal("pending", (await f.StageGeneratedWorkOrderAsync(order, Guid.NewGuid())).Status);
        await f.CopyPendingAsync();
        var commit = await f.Db.StorageOperations.SingleAsync(o => o.OperationType == "commit");
        var payload = System.Text.Json.Nodes.JsonNode.Parse(commit.PayloadJson!)!;
        var generation = payload["generation"]!;
        var snapshot = System.Text.Json.Nodes.JsonNode.Parse(generation["frozenDomainJson"]!.GetValue<string>())!;
        if (boundary == "version") snapshot["version"] = 0;
        if (boundary == "source-id") snapshot["workOrderId"] = 0;
        if (boundary == "case-id") snapshot["caseId"] = f.Case.Id + 100;
        if (boundary == "document-key") payload["documentKey"] = "foreign-work-order";
        if (boundary == "document-type") payload["documentType"] = "Иск";
        if (boundary == "unsupported-kind") generation["kind"] = "invoice"; // Legacy kind has no typed consumer.
        if (boundary == "kind-snapshot-mismatch") generation["kind"] = "court";
        generation["frozenDomainJson"] = snapshot.ToJsonString();
        commit.PayloadJson = payload.ToJsonString();
        await f.Db.SaveChangesAsync();
        var reads = f.Local.ReadCount;
        await f.RunCaseQueueAsync();
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Single(await f.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Equal(2, await f.Db.StorageReplicas.CountAsync(r => r.Status == StorageReplicaStatuses.Available));
        var queue = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == StorageOperationTypes.PublishCaseDocument);
        Assert.Equal(boundary == "unsupported-kind" ? StorageOperationStatuses.Pending : StorageOperationStatuses.DeadLetter, queue.Status);
        if (boundary == "unsupported-kind") Assert.Equal(0, queue.AttemptCount);
        Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData("uploaded", "{}")]
    [InlineData("template", "[]")]
    [InlineData("template", "broken")]
    public async Task GeneratedFoundationRejectsInvalidServerMetadataBeforeWriting(string kind, string json)
    {
        await using var f = await CreateFixtureAsync();
        Assert.Equal("invalid", (await f.Generate(Guid.NewGuid(), new(kind, new string('a', 64), json))).Status);
        Assert.Equal(0, f.Local.PutCount);
        Assert.Empty(await f.Db.StorageOperations.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementEndpointStopsOnlyOwnedAttemptAndIsIdempotent(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        var key = Guid.NewGuid(); await f.Upload(key);
        var controller = f.Controller();
        Assert.IsType<BadRequestObjectResult>((await controller.RetireDocumentUpload(f.Case.Id, Guid.Empty, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await controller.RetireDocumentUpload(f.Case.Id, Guid.NewGuid(), CancellationToken.None)).Result);
        var anonymous = f.Controller(); anonymous.HttpContext.User = new ClaimsPrincipal();
        Assert.IsType<UnauthorizedResult>((await anonymous.RetireDocumentUpload(f.Case.Id, key, CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await f.Controller(new User { Id = int.MaxValue })
            .RetireDocumentUpload(f.Case.Id, key, CancellationToken.None)).Result);
        var other = new User
        {
            Email = "other-retirement@example.local",
            FullName = "Другой автор",
            PasswordHash = "synthetic",
            IsActive = true,
            IsApproved = true,
            RoleId = f.User.RoleId
        };
        f.Db.Users.Add(other); await f.Db.SaveChangesAsync();
        await f.Db.RolePermissions.ExecuteUpdateAsync(s => s.SetProperty(p => p.ViewScope, DataVisibilityScope.All));
        // Even an editor allowed to view this case cannot retire the author's key.
        Assert.IsType<NotFoundResult>((await f.Controller(other).RetireDocumentUpload(f.Case.Id, key, CancellationToken.None)).Result);
        var unavailable = new CasesV2Controller(f.Db) { ControllerContext = controller.ControllerContext };
        Assert.Equal(503, Assert.IsType<StatusCodeResult>((await unavailable.RetireDocumentUpload(f.Case.Id, key, CancellationToken.None)).Result).StatusCode);
        Assert.Empty(await f.Db.CaseDocumentUploadRetirements.AsNoTracking().ToArrayAsync());
        for (var i = 0; i < 2; i++)
        {
            var state = Assert.IsType<CaseDocumentUploadState>(Assert.IsType<OkObjectResult>(
                (await controller.RetireDocumentUpload(f.Case.Id, key, CancellationToken.None)).Result).Value);
            Assert.Equal(key, state.UploadKey); Assert.Equal("retired", state.Status);
            Assert.Null(state.DocumentId); Assert.Null(state.Version); Assert.Null(state.Sha256);
        }
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        Assert.Single(await f.Db.CaseDocumentUploadRetirements.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(0, f.Local.ReadCount);
        await f.Db.RolePermissions.ExecuteUpdateAsync(s => s.SetProperty(p => p.CanEdit, false));
        Assert.IsType<ForbidResult>((await controller.RetireDocumentUpload(f.Case.Id, key, CancellationToken.None)).Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementEndpointRequiresVerifiedPublishedReceipt(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var key = Guid.NewGuid(); var document = f.Created(await f.Upload(key));
        var controller = f.Controller();
        var receipt = Assert.IsType<CaseDocumentUploadState>(Assert.IsType<OkObjectResult>(
            (await controller.RetireDocumentUpload(f.Case.Id, key, CancellationToken.None)).Result).Value);
        Assert.Equal("uploaded", receipt.Status); Assert.Equal(document.Id, receipt.DocumentId);
        Assert.Equal(document.Version, receipt.Version); Assert.Equal(64, receipt.Sha256!.Length);
        var binding = await f.Db.StorageProductBindings.SingleAsync();
        f.Db.StorageProductBindings.Remove(binding); await f.Db.SaveChangesAsync();
        var uncertain = Assert.IsType<ConflictObjectResult>((await controller.RetireDocumentUpload(f.Case.Id, key, CancellationToken.None)).Result);
        Assert.Equal("failed", Assert.IsType<CaseDocumentUploadState>(uncertain.Value).Status);
        Assert.Empty(await f.Db.CaseDocumentUploadRetirements.AsNoTracking().ToArrayAsync());
        Assert.Equal(document.Id, (await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync()).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementEndpointLostCommitResponseCanBeProvedWithoutAnotherUpload(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        var key = Guid.NewGuid(); await f.Upload(key);
        var pending = new Client { Name = "Owned pending endpoint retirement" }; f.Db.Clients.Add(pending);
        f.Fault.Enabled = true; f.Fault.Armed = true; f.Fault.Stage = "lost-commit";
        var controller = f.Controller();
        await Assert.ThrowsAsync<IOException>(() => controller.RetireDocumentUpload(f.Case.Id, key, CancellationToken.None));
        f.Fault.Enabled = false; f.Fault.Armed = false;
        var read = Assert.IsType<OkObjectResult>((await controller.GetDocumentUploads(f.Case.Id, [key], CancellationToken.None)).Result);
        Assert.Equal("retired", Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<CaseDocumentUploadState>>(read.Value)).Status);
        Assert.IsType<OkObjectResult>((await controller.RetireDocumentUpload(f.Case.Id, key, CancellationToken.None)).Result);
        Assert.Single(await f.Db.CaseDocumentUploadRetirements.AsNoTracking().ToArrayAsync());
        Assert.Equal(EntityState.Added, f.Db.Entry(pending).State);
        Assert.Empty(await f.Db.Clients.AsNoTracking().Where(c => c.Name == pending.Name).ToArrayAsync());
        using var aborted = new CancellationTokenSource(); aborted.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.RetireDocumentUpload(f.Case.Id, Guid.NewGuid(), aborted.Token));
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(0, f.Local.ReadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementStatusUsesExactOwnedFenceWithoutIntentAndRechecksLiveAccess(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var key = Guid.NewGuid(); var foreignKey = Guid.NewGuid();
        f.Db.CaseDocumentUploadRetirements.AddRange(
            new CaseDocumentUploadRetirement { CaseId = f.Case.Id, ActorId = f.User.Id, UploadKey = key, StorageObjectId = Guid.NewGuid(), RetiredAt = DateTime.UtcNow },
            new CaseDocumentUploadRetirement { CaseId = f.Case.Id, ActorId = int.MaxValue, UploadKey = foreignKey, StorageObjectId = Guid.NewGuid(), RetiredAt = DateTime.UtcNow },
            new CaseDocumentUploadRetirement { CaseId = int.MaxValue, ActorId = f.User.Id, UploadKey = foreignKey, StorageObjectId = Guid.NewGuid(), RetiredAt = DateTime.UtcNow });
        await f.Db.SaveChangesAsync();
        var reader = new CaseDocumentUploadStatusReader(f.Db, f.Monitor);
        var states = (await reader.ReadAsync(f.Case.Id, f.User.Id, [key, foreignKey], CancellationToken.None))!;
        Assert.Equal("retired", states.Single(s => s.UploadKey == key).Status);
        Assert.Equal("unknown", states.Single(s => s.UploadKey == foreignKey).Status);
        Assert.All(states, s => { Assert.Null(s.FileName); Assert.Null(s.DocumentId); Assert.Null(s.Sha256); });
        Assert.IsType<OkObjectResult>((await f.Controller().RetireDocumentUpload(f.Case.Id, key, CancellationToken.None)).Result);
        await using var sibling = f.OpenSiblingContext();
        // Initialize SQLite's EF functions before the other context opens its
        // reader; registering functions during an active statement is forbidden.
        Assert.True(await sibling.RolePermissions.AsNoTracking().AnyAsync());
        f.StatusHook.AfterRetirementRead = async () =>
        {
            f.StatusHook.AfterRetirementRead = null;
            await sibling.RolePermissions.ExecuteUpdateAsync(s => s.SetProperty(p => p.CanEdit, false));
        };
        Assert.Null(await reader.ReadAsync(f.Case.Id, f.User.Id, [key], CancellationToken.None));
        Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Local.ReadCount);
    }

    [Theory]
    [InlineData(false, "retired")]
    [InlineData(true, "retired")]
    [InlineData(false, "managed")]
    [InlineData(true, "managed")]
    [InlineData(false, "permission")]
    [InlineData(true, "permission")]
    public async Task LegacyUploadRechecksFenceAfterFileSaveAndPreservesUnrelatedState(bool postgres, string boundary)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        var protectedPath = f.CaseStorage.BuildRelativePath(f.Case.Id, "owned-protected.txt");
        await f.CaseStorage.SaveAsync(protectedPath, Encoding.UTF8.GetBytes("protected bytes"), CancellationToken.None);
        var pending = new Client { Name = "Owned unsaved legacy-fence client" };
        f.Db.Clients.Add(pending);
        var history = (await f.Db.ClientCases.AsNoTracking().SingleAsync()).EventHistory;
        var key = Guid.NewGuid();
        var files = new CallbackCaseStorage(f.CaseStorage);
        files.AfterSave = async () =>
        {
            await using var sibling = f.OpenSiblingContext();
            if (boundary == "permission")
                await sibling.RolePermissions.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CanEdit, false));
            else
            {
                f.Options.Enabled = true;
                try
                {
                    var producer = f.Controller(scope: sibling);
                    producer.Request.Headers["X-Upload-Key"] = key.ToString();
                    Assert.Equal(503, Assert.IsType<ObjectResult>((await producer.UploadDocument(f.Case.Id,
                        CreateFile("owned case bytes"), "Документ", "Подготовлен", null, CancellationToken.None)).Result).StatusCode);
                    if (boundary == "retired") Assert.Equal("retired", await new CaseDocumentUploadRetirementService(sibling)
                        .RetireAsync(f.Case.Id, f.User.Id, key, CancellationToken.None));
                }
                finally { f.Options.Enabled = false; }
            }
        };
        f.Options.Enabled = false;
        var controller = f.Controller(caseFiles: files);
        controller.Request.Headers["X-Upload-Key"] = key.ToString();
        var result = await controller.UploadDocument(f.Case.Id, CreateFile("owned case bytes"), "Документ", "Подготовлен", null, CancellationToken.None);
        if (boundary == "retired") Assert.IsType<ConflictObjectResult>(result.Result);
        else if (boundary == "permission") Assert.IsType<ForbidResult>(result.Result);
        else Assert.Equal(503, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.NotNull(files.SavedPath);
        Assert.False(f.CaseStorage.Exists(files.SavedPath));
        Assert.Equal("protected bytes", await File.ReadAllTextAsync(f.CaseStorage.ResolvePath(protectedPath)));
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Equal(history, (await f.Db.ClientCases.AsNoTracking().SingleAsync()).EventHistory);
        Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToArrayAsync());
        Assert.Equal(EntityState.Added, f.Db.Entry(pending).State);
        Assert.Empty(await f.Db.Clients.AsNoTracking().Where(item => item.Name == pending.Name).ToArrayAsync());
        Assert.Equal(boundary == "retired" ? 1 : 0, await f.Db.CaseDocumentUploadRetirements.AsNoTracking().CountAsync());
        Assert.Equal(boundary == "permission" ? 0 : 1, await f.Db.StorageObjects.AsNoTracking().CountAsync());
    }

    private sealed class CallbackCaseStorage(ICaseDocumentStorage inner) : ICaseDocumentStorage
    {
        public Func<Task>? AfterSave { get; set; }
        public string? SavedPath { get; private set; }
        public string BuildRelativePath(int caseId, string name) => inner.BuildRelativePath(caseId, name);
        public string ResolvePath(string path) => inner.ResolvePath(path);
        public bool Exists(string path) => inner.Exists(path);
        public void DeleteIfExists(string path) => inner.DeleteIfExists(path);
        public async Task SaveAsync(string path, Stream content, CancellationToken ct)
        {
            await inner.SaveAsync(path, content, ct);
            SavedPath = path;
            if (AfterSave is not null) await AfterSave();
        }
        public async Task SaveAsync(string path, byte[] content, CancellationToken ct)
        {
            await inner.SaveAsync(path, content, ct);
            SavedPath = path;
            if (AfterSave is not null) await AfterSave();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredAttemptCannotFallBackToLegacyWhenManagedRoutingIsDisabled(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        var key = Guid.NewGuid(); await f.Upload(key);
        Assert.Equal("retired", await new CaseDocumentUploadRetirementService(f.Db)
            .RetireAsync(f.Case.Id, f.User.Id, key, CancellationToken.None));
        f.Options.Enabled = false;
        Assert.IsType<ConflictObjectResult>((await f.Upload(key)).Result);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.CaseDocumentUploadRetirements.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingManagedAttemptCannotFallBackToLegacyWhenRoutingIsDisabled(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        var key = Guid.NewGuid(); await f.Upload(key);
        f.Options.Enabled = false;
        var blocked = Assert.IsType<ObjectResult>((await f.Upload(key)).Result);
        Assert.Equal(503, blocked.StatusCode);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.True(f.Created(await f.Upload(Guid.NewGuid())).Id > 0);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task PostgreSqlRetirementTimestampKeepsUtcClockFieldsInDifferentSessionTimeZone()
    {
        await using var f = await CreateFixtureAsync(postgres: true, requiredCopies: 2);
        var key = Guid.NewGuid(); await f.Upload(key);
        await f.Db.Database.OpenConnectionAsync();
        await f.Db.Database.ExecuteSqlRawAsync("SET TIME ZONE 'Asia/Novosibirsk'");
        var before = DateTime.UtcNow;
        Assert.Equal("retired", await new CaseDocumentUploadRetirementService(f.Db)
            .RetireAsync(f.Case.Id, f.User.Id, key, CancellationToken.None));
        var after = DateTime.UtcNow;
        var saved = await f.Db.CaseDocumentUploadRetirements.AsNoTracking().SingleAsync();
        Assert.InRange(saved.RetiredAt.Ticks, before.Ticks - TimeSpan.TicksPerSecond, after.Ticks + TimeSpan.TicksPerSecond);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSqlRetirementAndPublicationSerializeAtTheCaseLock(bool retirementWins)
    {
        await using var f = await CreateFixtureAsync(postgres: true, requiredCopies: 2);
        var key = Guid.NewGuid(); await f.Upload(key); await f.CopyPendingAsync();
        var barrier = new RetirementPublicationLockBarrier();
        await using var first = f.OpenSiblingContext(barrier);
        await using var second = f.OpenSiblingContext();
        var retireDb = retirementWins ? first : second;
        var publishDb = retirementWins ? second : first;
        var controller = f.Controller(scope: publishDb);
        controller.Request.Headers["X-Upload-Key"] = key.ToString();
        Task<string>? retirement = null;
        Task<ActionResult<ClientCaseDocumentDto>>? publication = null;
        try
        {
            if (retirementWins) retirement = new CaseDocumentUploadRetirementService(retireDb)
                .RetireAsync(f.Case.Id, f.User.Id, key, CancellationToken.None);
            else publication = controller.UploadDocument(f.Case.Id, CreateFile("owned case bytes"),
                "Документ", "Подготовлен", null, CancellationToken.None);
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (retirementWins) publication = controller.UploadDocument(f.Case.Id, CreateFile("owned case bytes"),
                "Документ", "Подготовлен", null, CancellationToken.None);
            else retirement = new CaseDocumentUploadRetirementService(retireDb)
                .RetireAsync(f.Case.Id, f.User.Id, key, CancellationToken.None);
            await f.Db.Database.OpenConnectionAsync();
            await using var query = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity
                WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%UPDATE "ClientCases"%'
                """, (NpgsqlConnection)f.Db.Database.GetDbConnection());
            var until = DateTime.UtcNow.AddSeconds(10);
            long waiting;
            do { waiting = (long)(await query.ExecuteScalarAsync())!; if (waiting > 0) break; await Task.Delay(25); }
            while (DateTime.UtcNow < until);
            Assert.True(waiting > 0, "The competing owned transaction must actually wait for the case row lock.");
        }
        finally
        {
            barrier.Release.TrySetResult();
            // Drain both owned transactions even if the blocking assertion
            // fails, before contexts and their temporary database are closed.
            var active = new List<Task>();
            if (retirement is not null) active.Add(retirement);
            if (publication is not null) active.Add(publication);
            await Task.WhenAll(active).WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.NotNull(retirement); Assert.NotNull(publication);
        await Task.WhenAll(retirement, publication).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(retirementWins ? "retired" : "published", await retirement);
        if (retirementWins) Assert.IsType<ConflictObjectResult>((await publication).Result);
        else Assert.True(f.Created(await publication).Id > 0);
        Assert.Equal(retirementWins ? 1 : 0, await f.Db.CaseDocumentUploadRetirements.AsNoTracking().CountAsync());
        Assert.Equal(retirementWins ? 0 : 1, await f.Db.ClientCaseDocuments.AsNoTracking().CountAsync());
        Assert.Equal(retirementWins ? 0 : 1, await f.Db.StorageProductBindings.AsNoTracking().CountAsync());
        Assert.Single(await f.Db.StorageObjects.AsNoTracking().ToArrayAsync());
    }

    private sealed class RetirementPublicationLockBarrier : DbCommandInterceptor
    {
        private int used;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE \"ClientCases\"", StringComparison.Ordinal)
                && command.CommandText.Contains("\"Title\"", StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref used, 1, 0) == 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredAttemptCannotPublishThroughHttpResumeOrWorkerButNewKeyCan(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        var key = Guid.NewGuid();
        await f.Upload(key);
        var objectId = await f.Db.StorageObjects.Select(item => item.Id).SingleAsync();
        var commit = await f.Db.StorageOperations.AsNoTracking().SingleAsync(item => item.OperationType == "commit");
        var retire = new CaseDocumentUploadRetirementService(f.Db);
        Assert.Equal("retired", await retire.RetireAsync(f.Case.Id, f.User.Id, key, CancellationToken.None));
        Assert.Equal("retired", await retire.RetireAsync(f.Case.Id, f.User.Id, key, CancellationToken.None));
        Assert.Single(await f.Db.CaseDocumentUploadRetirements.AsNoTracking().ToArrayAsync());
        Assert.Equal(objectId, (await f.Db.CaseDocumentUploadRetirements.AsNoTracking().SingleAsync()).StorageObjectId);
        Assert.IsType<ConflictObjectResult>((await f.Upload(key)).Result);
        Assert.Equal("retired", (await f.ResumeAsync(commit.Id)).Status);
        await f.CopyPendingAsync();
        var reads = f.Local.ReadCount;
        await f.RunCaseQueueAsync();
        Assert.Equal(reads, f.Local.ReadCount);
        var queue = await f.Db.StorageOperations.AsNoTracking().SingleAsync(item => item.OperationType == StorageOperationTypes.PublishCaseDocument);
        Assert.Equal(StorageOperationStatuses.DeadLetter, queue.Status);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        var retiredQueueRow = (await new StorageCasePublicationQueueReader(f.Db).ReadAsync(null, null, 50, CancellationToken.None)).Items.Single();
        Assert.False(retiredQueueRow.CanRetry); Assert.True(retiredQueueRow.IsRetired);
        var admin = new PayPlanner.Api.Controllers.StorageAdminController(null!, null!, null!, null!, null!, null!, null!, f.Db, null!);
        Assert.IsType<ConflictObjectResult>(await admin.RetryOperation(queue.Id, CancellationToken.None));
        var preservedQueue = await f.Db.StorageOperations.AsNoTracking().SingleAsync(item => item.Id == queue.Id);
        Assert.Equal(queue.Status, preservedQueue.Status);
        Assert.Equal(queue.AttemptCount, preservedQueue.AttemptCount);
        Assert.Equal(queue.CompletedAt, preservedQueue.CompletedAt);
        Assert.Single(await f.Db.StorageObjects.AsNoTracking().ToArrayAsync());
        await f.RecreateContext();
        var newKey = Guid.NewGuid();
        await f.Upload(newKey);
        await f.CopyPendingAsync();
        var document = f.Created(await f.Upload(newKey));
        Assert.Equal(f.Case.Id, document.ClientCaseId);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.CaseDocumentUploadRetirements.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementRejectsUnknownAndForeignAttemptsAndPreservesPublishedDocument(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var retire = new CaseDocumentUploadRetirementService(f.Db);
        Assert.Equal("unknown", await retire.RetireAsync(f.Case.Id, f.User.Id, Guid.NewGuid(), CancellationToken.None));
        Assert.Equal("forbidden", await retire.RetireAsync(f.Case.Id, int.MaxValue, Guid.NewGuid(), CancellationToken.None));
        var key = Guid.NewGuid();
        var document = f.Created(await f.Upload(key));
        Assert.Equal("published", await retire.RetireAsync(f.Case.Id, f.User.Id, key, CancellationToken.None));
        Assert.Empty(await f.Db.CaseDocumentUploadRetirements.AsNoTracking().ToArrayAsync());
        Assert.Equal(document.Id, (await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync()).Id);
        Assert.Single(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false, "before-commit")]
    [InlineData(true, "before-commit")]
    [InlineData(false, "lost-commit")]
    [InlineData(true, "lost-commit")]
    public async Task RetirementRollbackAndLostAckRetainUnrelatedTrackedChanges(bool postgres, string stage)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        var key = Guid.NewGuid(); await f.Upload(key);
        var pending = new Client { Name = "Owned unsaved retirement client" };
        f.Db.Clients.Add(pending);
        f.Fault.Enabled = true; f.Fault.Armed = true; f.Fault.Stage = stage;
        var retire = new CaseDocumentUploadRetirementService(f.Db);
        await Assert.ThrowsAsync<IOException>(() => retire.RetireAsync(f.Case.Id, f.User.Id, key, CancellationToken.None));
        f.Fault.Enabled = false; f.Fault.Armed = false;
        Assert.Equal(stage == "lost-commit" ? 1 : 0, await f.Db.CaseDocumentUploadRetirements.AsNoTracking().CountAsync());
        Assert.Equal(EntityState.Added, f.Db.Entry(pending).State);
        Assert.Empty(await f.Db.Clients.AsNoTracking().Where(item => item.Name == pending.Name).ToArrayAsync());
        Assert.Equal("retired", await retire.RetireAsync(f.Case.Id, f.User.Id, key, CancellationToken.None));
        Assert.Single(await f.Db.CaseDocumentUploadRetirements.AsNoTracking().ToArrayAsync());
        Assert.Equal(EntityState.Added, f.Db.Entry(pending).State);
        Assert.Empty(await f.Db.Clients.AsNoTracking().Where(item => item.Name == pending.Name).ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementDuringResumeReadPreventsDomainPublication(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        var key = Guid.NewGuid(); await f.Upload(key); await f.CopyPendingAsync(); await f.RecreateContext();
        var commit = await f.Db.StorageOperations.AsNoTracking().SingleAsync(item => item.OperationType == "commit");
        f.Local.AfterRead = async () => Assert.Equal("retired", await new CaseDocumentUploadRetirementService(f.Db)
            .RetireAsync(f.Case.Id, f.User.Id, key, CancellationToken.None));
        Assert.Equal("retired", (await f.ResumeAsync(commit.Id)).Status);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.StorageObjects.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AutoCaseQueueWaitsForCopiesThenPublishesWithoutHttpOrBrowserFile(bool postgres, bool lateEnabled)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        f.Options.AutoCaseDocumentPublicationEnabled = !lateEnabled;
        var key = Guid.NewGuid();
        await f.Upload(key);
        Assert.Equal(lateEnabled ? 0 : 1, await f.Db.StorageOperations.CountAsync(o => o.OperationType == StorageOperationTypes.PublishCaseDocument));
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        await f.RecreateContext();
        var consumer = f.CasePublisher();
        await consumer.EnsureScheduledAsync(CancellationToken.None);
        await consumer.EnsureScheduledAsync(CancellationToken.None);
        var queue = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == StorageOperationTypes.PublishCaseDocument);
        var until = DateTime.UtcNow.AddMinutes(2);
        await f.Db.StorageOperations.Where(o => o.Id == queue.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, StorageOperationStatuses.Running)
            .SetProperty(o => o.LeaseOwner, "owned-case-publication").SetProperty(o => o.LeaseExpiresAt, (DateTime?)until));
        Assert.Equal(StorageOperationStatuses.Pending, await consumer.ProcessClaimedAsync(queue.Id, "owned-case-publication", CancellationToken.None));
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Equal(0, (await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == queue.Id)).AttemptCount);
        await f.CopyPendingAsync();
        await f.RecreateContext();
        var due = DateTime.UtcNow.AddSeconds(-1);
        await f.Db.StorageOperations.Where(o => o.Id == queue.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, (DateTime?)due));
        Assert.Equal(1, (await f.RunCaseQueueAsync()).Succeeded);
        var document = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        var intent = ReadStoredIntent((await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == "commit")).PayloadJson);
        Assert.Equal(intent.ActorName, document.UploadedByName);
        Assert.Equal(intent.UploadedAt.Ticks, document.UploadedAt.Ticks);
        Assert.Single(await f.Db.StorageProductBindings.AsNoTracking().ToListAsync());
        Assert.Equal(StorageOperationStatuses.Succeeded, (await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == queue.Id)).Status);
        Assert.Equal(0, (await f.RunCaseQueueAsync()).Claimed);
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutoCaseQueueAcknowledgesRacingHttpPublicationWithoutDuplicateRecords(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        var key = Guid.NewGuid(); await f.Upload(key); await f.CopyPendingAsync();
        var document = f.Created(await f.Upload(key));
        var history = (await f.Db.ClientCases.AsNoTracking().SingleAsync()).EventHistory;
        var audit = await f.Db.ObjectAuditEvents.CountAsync(a => a.ObjectType == "client-case-document");
        await f.RecreateContext();
        var reads = f.Local.ReadCount;
        Assert.Equal(1, (await f.RunCaseQueueAsync()).Succeeded);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Equal(document.Id, (await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(history, (await f.Db.ClientCases.AsNoTracking().SingleAsync()).EventHistory);
        Assert.Equal(audit, await f.Db.ObjectAuditEvents.CountAsync(a => a.ObjectType == "client-case-document"));
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("lease")]
    [InlineData("flag")]
    public async Task AutoCaseQueueRechecksAuthorityAfterProviderReadAndRetainsCopies(string boundary)
    {
        await using var f = await CreateFixtureAsync(requiredCopies: 2);
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        await f.Upload(Guid.NewGuid()); await f.CopyPendingAsync(); await f.RecreateContext();
        f.Local.AfterRead = async () =>
        {
            if (boundary == "flag") f.Options.AutoCaseDocumentPublicationEnabled = false;
            if (boundary == "permission")
            {
                (await f.Db.Users.SingleAsync(u => u.Id == f.User.Id)).IsActive = false; await f.Db.SaveChangesAsync();
            }
            if (boundary == "lease")
            {
                var until = DateTime.UtcNow.AddMinutes(3);
                await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishCaseDocument)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.LeaseOwner, "replacement-worker").SetProperty(o => o.LeaseExpiresAt, (DateTime?)until));
            }
        };
        await f.RunCaseQueueAsync();
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToListAsync());
        var queue = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == StorageOperationTypes.PublishCaseDocument);
        Assert.Equal(boundary == "permission" ? StorageOperationStatuses.DeadLetter : boundary == "flag" ? StorageOperationStatuses.Pending : StorageOperationStatuses.Running, queue.Status);
        if (boundary == "lease") Assert.Equal("replacement-worker", queue.LeaseOwner);
        if (boundary == "flag") Assert.Equal(0, queue.AttemptCount);
        Assert.Equal(2, await f.Db.StorageReplicas.CountAsync(r => r.Status == StorageReplicaStatuses.Available));
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, "before-commit")]
    [InlineData(true, "before-commit")]
    [InlineData(false, "lost-commit")]
    [InlineData(true, "lost-commit")]
    [InlineData(false, "expired-before-commit")]
    public async Task AutoCaseQueueRecoversDomainFailureOrLostAcknowledgementAndFencesExpiredLease(bool postgres, string stage)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        await f.Upload(Guid.NewGuid()); await f.CopyPendingAsync(); await f.RecreateContext();
        var clock = new MutableCaseClock();
        f.Fault.Stage = stage; f.Fault.Enabled = true;
        if (stage == "expired-before-commit") f.Fault.AfterDomainSave = () => clock.Advance(TimeSpan.FromMinutes(3));
        await f.RunCaseQueueAsync(clock);
        Assert.Equal(stage == "lost-commit" ? 1 : 0, await f.Db.ClientCaseDocuments.CountAsync());
        var queue = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == StorageOperationTypes.PublishCaseDocument);
        Assert.Equal(stage == "expired-before-commit" ? StorageOperationStatuses.Running : StorageOperationStatuses.Failed, queue.Status);
        f.Fault.Enabled = false; await f.RecreateContext();
        var due = clock.GetUtcNow().UtcDateTime.AddSeconds(-1);
        await f.Db.StorageOperations.Where(o => o.Id == queue.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, (DateTime?)due));
        Assert.Equal(1, (await f.RunCaseQueueAsync(clock)).Succeeded);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.StorageProductBindings.AsNoTracking().ToListAsync());
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Fact]
    public async Task AutoCaseQueueBackfillIsBoundedAndExcludesTaskCaseOwners()
    {
        await using var f = await CreateFixtureAsync(requiredCopies: 2);
        await f.Upload(Guid.NewGuid()); await f.Upload(Guid.NewGuid());
        f.Options.AutoCaseDocumentPublicationEnabled = true; f.Options.WorkerBatchSize = 1;
        var consumer = f.CasePublisher();
        await consumer.EnsureScheduledAsync(CancellationToken.None);
        Assert.Equal(1, await f.Db.StorageOperations.CountAsync(o => o.OperationType == StorageOperationTypes.PublishCaseDocument));
        await consumer.EnsureScheduledAsync(CancellationToken.None);
        Assert.Equal(2, await f.Db.StorageOperations.CountAsync(o => o.OperationType == StorageOperationTypes.PublishCaseDocument));
        await consumer.EnsureScheduledAsync(CancellationToken.None);
        Assert.Equal(2, await f.Db.StorageOperations.CountAsync(o => o.OperationType == StorageOperationTypes.PublishCaseDocument));
        Assert.Equal(3, f.StatusHook.PublicationScheduleReads);
        await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishCaseDocument).ExecuteDeleteAsync();
        await f.Db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.OwnerId, "upload:synthetic-task-owner"));
        await consumer.EnsureScheduledAsync(CancellationToken.None);
        Assert.Equal(0, await f.Db.StorageOperations.CountAsync(o => o.OperationType == StorageOperationTypes.PublishCaseDocument));
    }

    private sealed class MutableCaseClock : TimeProvider
    {
        private DateTimeOffset current = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan by) => current += by;
    }

    [Fact]
    public async Task AutoCaseQueueConcurrentPostgreSqlSchedulersCreateOneDurableJob()
    {
        await using var f = await CreateFixtureAsync(postgres: true, requiredCopies: 2);
        await f.Upload(Guid.NewGuid());
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        var barrier = new CaseSchedulingBarrier();
        await using var first = f.OpenSiblingContext(barrier);
        await using var second = f.OpenSiblingContext(barrier);
        await Task.WhenAll(f.CasePublisher(scope: first).EnsureScheduledAsync(CancellationToken.None),
            f.CasePublisher(scope: second).EnsureScheduledAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Single(await f.Db.StorageOperations.AsNoTracking().Where(o => o.OperationType == StorageOperationTypes.PublishCaseDocument).ToListAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Equal(1, f.Local.PutCount);
        await f.CasePublisher(scope: second).EnsureScheduledAsync(CancellationToken.None);
        Assert.Single(await second.StorageOperations.AsNoTracking().Where(o => o.OperationType == StorageOperationTypes.PublishCaseDocument).ToListAsync());
    }

    private sealed class CaseSchedulingBarrier : DbCommandInterceptor
    {
        private int arrivals;
        private readonly TaskCompletionSource both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("case-document-publication-schedule", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref arrivals) == 2) both.TrySetResult();
                await both.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }
    private static StoredCaseIntent ReadStoredIntent(string? json) => System.Text.Json.JsonSerializer.Deserialize<StoredCaseIntent>(json!,
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    private sealed record StoredCaseIntent(int CaseId, int ActorId, string ActorName, Guid UploadKey, string? RequestedDocumentKey,
        string DocumentKey, int Version, string FileName, string ContentType, string DocumentType, string Status,
        long SizeBytes, string Sha256, DateTime UploadedAt, int? DocumentId = null);
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StoredCaseUploadResumesWithoutBrowserFileAndPreservesOriginalIntent(bool postgres, bool version)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        var receiver = new User
        {
            Email = "case-resume-receiver@example.local",
            FullName = "Получатель",
            PasswordHash = "synthetic",
            IsActive = true,
            IsApproved = true,
            RoleId = f.User.RoleId
        };
        f.Db.Users.Add(receiver);
        (await f.Db.RolePermissions.SingleAsync(p => p.Section == RolePermissionCatalog.Cases)).ViewScope = DataVisibilityScope.All;
        await f.Db.SaveChangesAsync();
        (await f.Db.Clients.SingleAsync(c => c.Id == f.Case.ClientId)).ResponsibleUserId = receiver.Id;
        await f.Db.SaveChangesAsync();
        ClientCaseDocumentDto? previous = null;
        if (version)
        {
            var initialKey = Guid.NewGuid();
            await f.Upload(initialKey);
            await f.CopyPendingAsync();
            previous = f.Created(await f.Upload(initialKey));
        }
        var key = Guid.NewGuid();
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.Upload(key, body: "stored resume bytes", versionOf: previous?.Id)).Result).StatusCode);
        var operation = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.IdempotencyKey.EndsWith(key.ToString("N") + ":commit"));
        var intent = ReadStoredIntent(operation.PayloadJson)!;
        Assert.Equal("pending", (await f.ResumeAsync(operation.Id)).Status);
        Assert.Equal(version ? 1 : 0, await f.Db.ClientCaseDocuments.CountAsync());
        await f.CopyPendingAsync();
        var author = await f.Db.Users.SingleAsync(u => u.Id == f.User.Id);
        author.FullName = "Имя автора после загрузки";
        await f.Db.SaveChangesAsync();
        await f.RecreateContext();
        var previousNotifications = await f.Db.UserNotifications.CountAsync();
        var result = await f.ResumeAsync(operation.Id);
        Assert.Equal("uploaded", result.Status);
        var document = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync(d => d.Id == result.DocumentId);
        Assert.Equal(intent.DocumentKey, document.DocumentKey);
        Assert.Equal(intent.Version, document.Version);
        Assert.Equal(intent.ActorName, document.UploadedByName);
        Assert.Equal(intent.UploadedAt.Ticks, document.UploadedAt.Ticks);
        Assert.Equal("stored resume bytes", await File.ReadAllTextAsync(f.CaseStorage.ResolvePath(document.StoragePath)));
        Assert.Equal(version ? 2 : 1, f.Local.PutCount);
        Assert.Equal(version ? 2 : 1, f.Mirror.PutCount);
        var before = new
        {
            Documents = await f.Db.ClientCaseDocuments.CountAsync(),
            Notifications = await f.Db.UserNotifications.CountAsync(),
            Audit = await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").CountAsync(),
            History = (await f.Db.ClientCases.AsNoTracking().SingleAsync()).EventHistory
        };
        Assert.Equal(previousNotifications + 1, before.Notifications);
        Assert.True((await f.ResumeAsync(operation.Id)).IsReplay);
        Assert.Equal(before.Documents, await f.Db.ClientCaseDocuments.CountAsync());
        Assert.Equal(before.Notifications, await f.Db.UserNotifications.CountAsync());
        Assert.Equal(before.Audit, await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").CountAsync());
        Assert.Equal(before.History, (await f.Db.ClientCases.AsNoTracking().SingleAsync()).EventHistory);
        var reads = f.Local.ReadCount;
        Assert.Equal(result.DocumentId, (await f.VerifyAsync(operation.Id)).DocumentId);
        Assert.Equal(reads, f.Local.ReadCount);
    }

    [Theory]
    [InlineData("initial")]
    [InlineData("after-read")]
    [InlineData("under-lock")]
    [InlineData("role-during-read")]
    [InlineData("intent-during-read")]
    public async Task StoredCaseResumeCannotPublishAfterAuthorityOrIntentChanges(string boundary)
    {
        await using var f = await CreateFixtureAsync(requiredCopies: 2);
        var key = Guid.NewGuid(); await f.Upload(key); await f.CopyPendingAsync();
        await f.RecreateContext();
        var operation = await f.Db.StorageOperations.SingleAsync(o => o.OperationType == "commit");
        var beforeReads = f.Local.ReadCount;
        if (boundary == "role-during-read") f.Local.AfterRead = async () =>
        {
            (await f.Db.RolePermissions.SingleAsync(p => p.Section == RolePermissionCatalog.Cases)).CanEdit = false;
            await f.Db.SaveChangesAsync();
        };
        if (boundary == "intent-during-read") f.Local.AfterRead = async () =>
        {
            var current = await f.Db.StorageOperations.SingleAsync(o => o.Id == operation.Id);
            var original = ReadStoredIntent(current.PayloadJson)!;
            current.PayloadJson = System.Text.Json.JsonSerializer.Serialize(original with { ActorName = "Подменённый автор" }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            await f.Db.SaveChangesAsync();
        };
        var calls = 0;
        var deniedAt = boundary == "initial" ? 1 : boundary == "after-read" ? 2 : boundary == "under-lock" ? 3 : int.MaxValue;
        var result = await f.ResumeAsync(operation.Id, (actor, caseId, ct) =>
        { Assert.Equal(f.User.Id, actor); Assert.Equal(f.Case.Id, caseId); return Task.FromResult(++calls != deniedAt); });
        Assert.Equal(boundary == "intent-during-read" ? "conflict" : "forbidden", result.Status);
        if (boundary == "initial") Assert.Equal(beforeReads, f.Local.ReadCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").AsNoTracking().ToListAsync());
        Assert.Equal("", (await f.Db.ClientCases.AsNoTracking().SingleAsync()).EventHistory);
        Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData("filename")]
    [InlineData("size")]
    [InlineData("sha")]
    [InlineData("actor")]
    [InlineData("key")]
    [InlineData("owner")]
    [InlineData("operation-key")]
    [InlineData("metadata-size")]
    [InlineData("metadata-sha")]
    public async Task StoredCaseResumeRejectsInvalidIntentBeforeReadingProvider(string field)
    {
        await using var f = await CreateFixtureAsync(requiredCopies: 2);
        await f.Upload(Guid.NewGuid()); await f.CopyPendingAsync();
        var operation = await f.Db.StorageOperations.SingleAsync(o => o.OperationType == "commit");
        var intent = ReadStoredIntent(operation.PayloadJson)!;
        var changed = field switch
        {
            "filename" => intent with { FileName = "../escape.txt" },
            "size" => intent with { SizeBytes = long.MaxValue },
            "sha" => intent with { Sha256 = "bad" },
            "actor" => intent with { ActorId = 0 },
            "key" => intent with { UploadKey = Guid.Empty },
            "metadata-size" => intent with { SizeBytes = intent.SizeBytes + 1 },
            "metadata-sha" => intent with { Sha256 = new string('f', 64) },
            _ => intent
        };
        operation.PayloadJson = System.Text.Json.JsonSerializer.Serialize(changed, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        if (field == "owner") (await f.Db.StorageObjects.SingleAsync()).OwnerId = "foreign-owner";
        if (field == "operation-key") operation.IdempotencyKey += "-foreign";
        await f.Db.SaveChangesAsync();
        var reads = f.Local.ReadCount;
        Assert.Equal(field is "owner" or "operation-key" or "metadata-size" or "metadata-sha" ? "conflict" : "invalid", (await f.ResumeAsync(operation.Id)).Status);
        Assert.Equal(reads, f.Local.ReadCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(false, "before-commit")]
    [InlineData(true, "before-commit")]
    [InlineData(false, "lost-commit")]
    [InlineData(true, "lost-commit")]
    public async Task StoredCaseResumeSurvivesPublicationFailureAndLostCommitAcknowledgement(bool postgres, string stage)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        await f.Upload(Guid.NewGuid()); await f.CopyPendingAsync();
        var operation = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == "commit");
        f.Fault.Stage = stage; f.Fault.Enabled = true;
        await Assert.ThrowsAsync<IOException>(() => f.ResumeAsync(operation.Id));
        f.Fault.Enabled = false;
        await f.RecreateContext();
        Assert.Equal(stage == "lost-commit" ? 1 : 0, await f.Db.ClientCaseDocuments.CountAsync());
        var result = await f.ResumeAsync(operation.Id);
        Assert.Equal("uploaded", result.Status);
        Assert.Equal(stage == "lost-commit", result.IsReplay);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.StorageProductBindings.AsNoTracking().ToListAsync());
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("generation")]
    [InlineData("version")]
    [InlineData("author-name")]
    [InlineData("clock")]
    public async Task StoredCaseReceiptRejectsChangedPublicationWithoutProviderRead(string field)
    {
        await using var f = await CreateFixtureAsync();
        var document = f.Created(await f.Upload(Guid.NewGuid()));
        await f.RecreateContext();
        var operation = await f.Db.StorageOperations.SingleAsync(o => o.OperationType == "commit");
        Assert.Equal("uploaded", (await f.VerifyAsync(operation.Id)).Status);
        if (field == "binding") f.Db.StorageProductBindings.Remove(await f.Db.StorageProductBindings.SingleAsync());
        if (field == "generation") (await f.Db.StorageObjects.SingleAsync()).CurrentGeneration++;
        if (field == "version") (await f.Db.ClientCaseDocuments.SingleAsync(d => d.Id == document.Id)).Version++;
        if (field == "author-name") (await f.Db.ClientCaseDocuments.SingleAsync(d => d.Id == document.Id)).UploadedByName = "Подменённый автор";
        if (field == "clock") (await f.Db.ClientCaseDocuments.SingleAsync(d => d.Id == document.Id)).UploadedAt = DateTime.UtcNow.AddDays(-1);
        await f.Db.SaveChangesAsync();
        var reads = f.Local.ReadCount;
        Assert.Equal("conflict", (await f.VerifyAsync(operation.Id)).Status);
        Assert.Equal(reads, f.Local.ReadCount);
    }

    [Fact]
    public async Task CaseDocumentNotificationsAreNotSentBeforeOuterCommit()
    {
        await using var f = await CreateFixtureAsync();
        var actor = new User
        {
            Email = "notification-author@example.local",
            FullName = "Автор",
            PasswordHash = "synthetic",
            IsActive = true,
            IsApproved = true,
            RoleId = f.User.RoleId
        };
        f.Db.Users.Add(actor); await f.Db.SaveChangesAsync();
        var gateway = new RecordingRealtimePublisher();
        var notificationService = new NotificationService(f.Db, f.Configuration, new NoopEmailService(),
            new NoopTelegramNotificationService(), NullLogger<NotificationService>.Instance, gateway);
        var records = new CaseDocumentPublicationRecords(f.Db, new CaseDocumentNotificationService(f.Db, notificationService));
        await using var tx = await f.Db.Database.BeginTransactionAsync();
        var document = new ClientCaseDocument
        {
            ClientCaseId = f.Case.Id,
            DocumentKey = "rollback-notification",
            Version = 1,
            OriginalFileName = "rollback.txt",
            StoredFileName = "rollback.txt",
            StoragePath = "owned/rollback.txt",
            UploadedByUserId = actor.Id,
            UploadedByName = actor.FullName,
            UploadedAt = DateTime.UtcNow
        };
        f.Db.ClientCaseDocuments.Add(document);
        await records.PersistUploadAsync(f.Case, document, true, actor.Id, actor.FullName, DateTime.Now, CancellationToken.None);
        Assert.Single(await f.Db.UserNotifications.AsNoTracking().ToListAsync());
        Assert.Empty(gateway.DeliveredIds);
        Assert.Single(await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().ToListAsync());
        await tx.RollbackAsync();
        Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().ToListAsync());
        Assert.Empty(gateway.DeliveredIds);
    }

    [Theory]
    [InlineData(false, "success")]
    [InlineData(true, "success")]
    [InlineData(false, "revoke")]
    [InlineData(false, "revoke-role")]
    [InlineData(false, "hidden")]
    [InlineData(false, "deleted-document")]
    [InlineData(false, "malformed-metadata")]
    [InlineData(false, "lost-transport")]
    [InlineData(false, "push-silent")]
    [InlineData(false, "expired-lease")]
    [InlineData(false, "lease-lost-before-send")]
    [InlineData(true, "lease-lost-before-send")]
    [InlineData(false, "lost-commit")]
    [InlineData(true, "lost-commit")]
    public async Task CommittedCaseNotificationRecoversWithoutHttpAndRechecksRecipient(bool postgres, string scenario)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var actor = new User
        {
            Email = "recovery-author@example.local",
            FullName = "Автор",
            PasswordHash = "synthetic",
            IsActive = true,
            IsApproved = true,
            RoleId = f.User.RoleId
        };
        f.Db.Users.Add(actor); await f.Db.SaveChangesAsync();
        var gateway = new RecordingRealtimePublisher();
        var push = new RecordingWebPush();
        var usePush = scenario == "push-silent";
        if (usePush)
        {
            f.Db.UserSettings.Add(new UserSetting { UserId = f.User.Id, Key = UserNotificationPreferenceSettings.SettingKey, Value = "{}" });
            await f.Db.SaveChangesAsync();
        }
        NotificationService Service() => new(f.Db, f.Configuration, new NoopEmailService(),
            new NoopTelegramNotificationService(), NullLogger<NotificationService>.Instance, gateway,
            webPushNotificationService: usePush ? push : null);
        var notifications = Service();
        await using (var tx = await f.Db.Database.BeginTransactionAsync())
        {
            var document = new ClientCaseDocument
            {
                ClientCaseId = f.Case.Id,
                DocumentKey = "recovery-notification",
                Version = 1,
                OriginalFileName = "recovery.txt",
                StoredFileName = "recovery.txt",
                StoragePath = "owned/recovery.txt",
                UploadedByUserId = actor.Id,
                UploadedByName = actor.FullName,
                UploadedAt = DateTime.UtcNow
            };
            f.Db.ClientCaseDocuments.Add(document);
            await new CaseDocumentPublicationRecords(f.Db, new CaseDocumentNotificationService(f.Db, notifications))
                .PersistUploadAsync(f.Case, document, true, actor.Id, actor.FullName, DateTime.Now, CancellationToken.None);
            Assert.Empty(gateway.DeliveredIds);
            Assert.Empty(push.DeliveredIds);
            Assert.Equal("realtime", (await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().SingleAsync()).Channel);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new CaseDocumentNotificationDispatchProcessor(f.Db, notifications)
                .ProcessBatchAsync(CancellationToken.None));
            if (scenario == "lost-commit")
            {
                f.Fault.Stage = "lost-commit"; f.Fault.Enabled = true; f.Fault.Armed = true;
                await Assert.ThrowsAsync<IOException>(() => tx.CommitAsync());
                f.Fault.Enabled = false;
            }
            else await tx.CommitAsync();
        }
        await f.RecreateContext();
        if (scenario == "revoke")
        {
            var receiver = await f.Db.Users.SingleAsync(u => u.Id == f.User.Id);
            receiver.IsActive = false; await f.Db.SaveChangesAsync();
        }
        if (scenario == "revoke-role")
        {
            var permission = await f.Db.RolePermissions.SingleAsync(p => p.RoleId == f.User.RoleId && p.Section == RolePermissionCatalog.Cases);
            permission.CanView = false; await f.Db.SaveChangesAsync();
        }
        if (scenario == "hidden")
        {
            var item = await f.Db.ClientCases.SingleAsync(); item.IsHiddenFromExecutor = true; await f.Db.SaveChangesAsync();
        }
        if (scenario == "deleted-document")
        {
            f.Db.ClientCaseDocuments.Remove(await f.Db.ClientCaseDocuments.SingleAsync()); await f.Db.SaveChangesAsync();
        }
        if (scenario == "malformed-metadata")
        {
            var item = await f.Db.UserNotifications.SingleAsync(); item.MetadataJson = "{\"clientCaseId\":\"wrong-kind\"}"; await f.Db.SaveChangesAsync();
        }
        if (scenario == "push-silent")
        {
            // A stale or invalid channel row must not bypass the catalog's silent policy.
            f.Db.CaseDocumentNotificationDispatches.Add(new CaseDocumentNotificationDispatch
            { UserNotificationId = (await f.Db.UserNotifications.SingleAsync()).Id, Channel = NotificationChannels.WebPush });
            await f.Db.SaveChangesAsync();
        }
        if (scenario == "expired-lease")
        {
            var stale = await f.Db.CaseDocumentNotificationDispatches.SingleAsync();
            stale.Status = "running"; stale.LeaseOwner = "previous-worker"; stale.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await f.Db.SaveChangesAsync();
        }
        gateway.FailAfterDelivery = scenario == "lost-transport";
        if (scenario == "lease-lost-before-send")
        {
            Assert.Equal("failed", await Service().DispatchCaseDocumentAsync(
                (await f.Db.UserNotifications.SingleAsync()).Id, "realtime", false,
                _ => Task.FromResult(false), CancellationToken.None));
            Assert.Empty(gateway.DeliveredIds);
        }
        var processor = new CaseDocumentNotificationDispatchProcessor(f.Db, Service());
        var delivered = await processor.ProcessBatchAsync(CancellationToken.None);
        if (scenario is "revoke" or "revoke-role" or "hidden" or "deleted-document" or "malformed-metadata")
        {
            Assert.Equal(0, delivered); Assert.Empty(gateway.DeliveredIds);
            Assert.Equal("skipped", (await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().SingleAsync()).Status);
        }
        else
        {
            if (scenario == "lost-transport")
            {
                Assert.Equal(0, delivered);
                var pending = await f.Db.CaseDocumentNotificationDispatches.SingleAsync();
                Assert.Equal("failed", pending.Status);
                pending.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1); await f.Db.SaveChangesAsync();
                gateway.FailAfterDelivery = false;
                Assert.Equal(1, await processor.ProcessBatchAsync(CancellationToken.None));
            }
            else Assert.Equal(1, delivered);
            var deliveries = await f.Db.CaseDocumentNotificationDispatches.AsNoTracking().ToListAsync();
            Assert.All(deliveries, row => Assert.Equal(row.Channel == NotificationChannels.WebPush ? "skipped" : "completed", row.Status));
            Assert.Empty(push.DeliveredIds);
            Assert.Single(gateway.DeliveredIds.Distinct());
            Assert.Equal(0, await processor.ProcessBatchAsync(CancellationToken.None));
        }
        Assert.Single(await f.Db.UserNotifications.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("competing")]
    [InlineData("replaced-before-send")]
    [InlineData("replaced-after-send")]
    public async Task CaseNotificationDispatchLeaseProtectsConcurrentWorkersOnPostgreSql(string scenario)
    {
        await using var f = await CreateFixtureAsync(postgres: true);
        var notification = new UserNotification
        {
            RecipientUserId = f.User.Id,
            Type = UserNotificationTypes.CaseDocument,
            EventKey = "owned-lease",
            Title = "Документ",
            BodyPreview = "Синтетическая проверка",
            ObjectType = "case",
            ObjectId = f.Case.Id
        };
        f.Db.UserNotifications.Add(notification); await f.Db.SaveChangesAsync();
        var row = new CaseDocumentNotificationDispatch { UserNotificationId = notification.Id };
        f.Db.CaseDocumentNotificationDispatches.Add(row); await f.Db.SaveChangesAsync();
        await using var sibling = f.OpenSiblingContext();
        var first = new BarrierCaseDispatcher(block: true, sendBeforeBarrier: scenario == "replaced-after-send");
        var second = new BarrierCaseDispatcher(block: false);
        var pending = new CaseDocumentNotificationDispatchProcessor(f.Db, first).ProcessAsync(row.Id, CancellationToken.None);
        try
        {
            await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var competitor = new CaseDocumentNotificationDispatchProcessor(sibling, second);
            Assert.False(await competitor.ProcessAsync(row.Id, CancellationToken.None));
            Assert.Equal(0, second.Deliveries);
            if (scenario != "competing")
            {
                // Emulate a stopped worker's expired lease, then let another context recover it.
                var expiredAt = DateTime.UtcNow.AddMinutes(-1);
                await sibling.CaseDocumentNotificationDispatches.Where(item => item.Id == row.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(item => item.LeaseExpiresAt, (DateTime?)expiredAt));
                Assert.True(await competitor.ProcessAsync(row.Id, CancellationToken.None));
            }
        }
        finally
        {
            first.Release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(scenario == "competing", await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        var receipt = await sibling.CaseDocumentNotificationDispatches.AsNoTracking().SingleAsync();
        Assert.Equal("completed", receipt.Status);
        Assert.Null(receipt.LeaseOwner);
        Assert.Equal(scenario == "competing" ? 1 : 2, receipt.AttemptCount);
        Assert.Equal(scenario == "replaced-before-send" ? 0 : 1, first.Deliveries);
        Assert.Equal(scenario == "competing" ? 0 : 1, second.Deliveries);
        Assert.Single(await sibling.UserNotifications.AsNoTracking().ToListAsync());
    }

    private sealed class BarrierCaseDispatcher(bool block, bool sendBeforeBarrier = false) : ICaseDocumentNotificationLiveDispatcher
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Deliveries { get; private set; }
        public async Task<string> DispatchCaseDocumentAsync(int id, string channel, bool toast,
            Func<CancellationToken, Task<bool>> ownsLease, CancellationToken ct)
        {
            if (sendBeforeBarrier)
            {
                Assert.True(await ownsLease(ct));
                Deliveries++;
            }
            Entered.TrySetResult();
            if (block) await Release.Task.WaitAsync(ct);
            if (!sendBeforeBarrier)
            {
                if (!await ownsLease(ct)) return "failed";
                Deliveries++;
            }
            return "completed";
        }
    }

    private sealed class RecordingWebPush : IWebPushNotificationService
    {
        public List<int> DeliveredIds { get; } = [];
        public WebPushPublicKeyStatus GetPublicKeyStatus() => new(true, true, "owned", true, true, true);
        public Task SendUserNotificationAsync(UserNotification notification, CancellationToken cancellationToken = default)
        {
            DeliveredIds.Add(notification.Id); return Task.CompletedTask;
        }
        public Task<NotificationDelivery?> RetryDeliveryAsync(int deliveryId, CancellationToken cancellationToken = default)
            => Task.FromResult<NotificationDelivery?>(null);
    }

    private sealed class RecordingRealtimePublisher : IRealtimeNotificationPublisher
    {
        public List<int> DeliveredIds { get; } = [];
        public bool FailAfterDelivery { get; set; }
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true,
            CancellationToken cancellationToken = default)
        {
            DeliveredIds.Add(notification.Id);
            if (FailAfterDelivery) throw new IOException("Synthetic lost transport acknowledgement.");
            return Task.CompletedTask;
        }
        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SharedPublicationRecordsKeepExplicitAuthorClockAndCurrentHistoryWithoutHttp(bool firstVersion)
    {
        await using var f = await CreateFixtureAsync();
        var actor = new User
        {
            Email = "publication-author@example.local",
            FullName = "Новое имя автора",
            PasswordHash = "synthetic",
            IsActive = true,
            IsApproved = true,
            RoleId = f.User.RoleId
        };
        f.Db.Users.Add(actor);
        f.Case.EventHistory = "Сохранённая история до публикации";
        await f.Db.SaveChangesAsync();
        f.Case.EventHistory = "Устаревшая история в памяти";
        var bytes = Encoding.UTF8.GetBytes("owned shared publication records");
        var path = f.CaseStorage.BuildRelativePath(f.Case.Id, "shared-records.txt");
        var document = new ClientCaseDocument
        {
            ClientCaseId = f.Case.Id,
            DocumentKey = "shared-records",
            Version = firstVersion ? 1 : 2,
            DocumentType = "Документ",
            Status = "Подготовлен",
            OriginalFileName = "shared-records.txt",
            StoredFileName = "shared-records.txt",
            StoragePath = path,
            ContentType = "text/plain",
            SizeBytes = bytes.Length,
            UploadedByUserId = actor.Id,
            UploadedByName = "Исходное имя автора",
            UploadedAt = DateTime.UtcNow
        };
        var notificationService = new NotificationService(f.Db, f.Configuration, new NoopEmailService(),
            new NoopTelegramNotificationService(), NullLogger<NotificationService>.Instance);
        var records = new CaseDocumentPublicationRecords(f.Db, new CaseDocumentNotificationService(f.Db, notificationService));
        var id = await CaseDocumentAtomicSave.SaveAsync(f.Db, f.CaseStorage, path,
            () => f.CaseStorage.SaveAsync(path, bytes, CancellationToken.None), async () =>
            {
                f.Db.ClientCaseDocuments.Add(document);
                await records.PersistUploadAsync(f.Case, document, firstVersion, actor.Id, document.UploadedByName,
                    new DateTime(2026, 9, 30, 14, 12, 0), CancellationToken.None);
                return document.Id;
            }, CancellationToken.None);
        Assert.True(f.CaseStorage.Exists(path));
        Assert.Equal(id, Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync()).Id);
        var history = (await f.Db.ClientCases.AsNoTracking().SingleAsync()).EventHistory;
        Assert.StartsWith("Сохранённая история до публикации", history);
        Assert.DoesNotContain("Устаревшая история", history);
        Assert.Contains("30.09.2026 14:12:", history);
        Assert.Contains("Добавил: Исходное имя автора.", history);
        var audit = Assert.Single(await f.Db.ObjectAuditEvents.AsNoTracking()
            .Where(item => item.ObjectType == "client-case-document").ToListAsync());
        Assert.Equal(id, audit.ObjectId);
        Assert.Equal(actor.Id, audit.ActorUserId);
        Assert.Equal("Исходное имя автора", audit.ActorUserName);
        Assert.Equal(firstVersion ? "Created" : "VersionCreated", audit.Action);
        var notification = Assert.Single(await f.Db.UserNotifications.AsNoTracking().ToListAsync());
        Assert.Equal(f.User.Id, notification.RecipientUserId);
        Assert.Contains("Исходное имя автора", notification.BodyPreview);
        Assert.Contains($"\"clientCaseDocumentId\":{id}", notification.MetadataJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedPublicationRecordsRejectMissingTransactionOrMismatchedCaseBeforeSaving(bool transaction)
    {
        await using var f = await CreateFixtureAsync();
        var history = f.Case.EventHistory;
        var records = new CaseDocumentPublicationRecords(f.Db, new CaseDocumentNotificationService(f.Db));
        var document = new ClientCaseDocument { ClientCaseId = transaction ? f.Case.Id + 1 : f.Case.Id };
        await using var tx = transaction ? await f.Db.Database.BeginTransactionAsync() : null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => records.PersistUploadAsync(f.Case, document,
            true, f.User.Id, f.User.FullName, DateTime.Now, CancellationToken.None));
        Assert.Equal(history, f.Case.EventHistory);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.ObjectAuditEvents.AsNoTracking().Where(item => item.ObjectType == "client-case-document").ToListAsync());
        Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadAndExactReplayPublishOneCaseDocumentAndManagedDownload(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var key = Guid.NewGuid();
        var saved = f.Created(await f.Upload(key));
        var replay = f.Created(await f.Upload(key));
        Assert.Equal(saved.Id, replay.Id);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.StorageProductBindings.AsNoTracking().ToListAsync());
        Assert.Equal(saved.Id.ToString(CultureInfo.InvariantCulture), (await f.Db.StorageObjects.AsNoTracking().SingleAsync()).OwnerId);
        Assert.Single(await f.Db.ObjectAuditEvents.AsNoTracking().Where(a => a.ObjectType == "client-case-document").ToListAsync());
        Assert.Single(await f.Db.UserNotifications.AsNoTracking().ToListAsync());
        var row = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        File.Delete(f.CaseStorage.ResolvePath(row.StoragePath));
        var download = Assert.IsType<FileStreamResult>(await f.Controller().DownloadDocumentById(row.Id, CancellationToken.None));
        await using (download.FileStream)
        {
            using var bytes = new MemoryStream();
            await download.FileStream.CopyToAsync(bytes);
            Assert.Equal("owned case bytes", Encoding.UTF8.GetString(bytes.ToArray()));
        }
        Assert.Equal(0, await f.Db.StorageCapacityReservations.CountAsync(r => r.Status == StorageCapacityReservationStatuses.Pending));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactStatusBatchConfirmsReceiptWithoutProviderIoOrDomainChanges(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var key = Guid.NewGuid();
        var uploaded = f.Created(await f.Upload(key));
        var missing = Guid.NewGuid();
        var states = await new CaseDocumentUploadStatusReader(f.Db, f.Monitor)
            .ReadAsync(f.Case.Id, f.User.Id, [key, key, missing], CancellationToken.None);
        Assert.Equal(2, states!.Count);
        var state = Assert.Single(states, s => s.UploadKey == key);
        Assert.Equal("uploaded", state.Status);
        Assert.Equal(uploaded.Id, state.DocumentId);
        Assert.Equal(1, state.Version);
        Assert.Equal("Документ", state.DocumentType);
        Assert.Equal("Подготовлен", state.RequestedStatus);
        Assert.Null(state.RequestedDocumentKey);
        Assert.Equal("text/plain", state.ContentType);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("owned case bytes"))).ToLowerInvariant(), state.Sha256);
        Assert.Equal("unknown", Assert.Single(states, s => s.UploadKey == missing).Status);
        Assert.Null(states.Single(s => s.UploadKey == missing).FileName);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Single(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Single(await f.Db.UserNotifications.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryPagesDurableReservationsAndAdvancesAcrossCorruptPayloads(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var firstKey = Guid.ParseExact(1.ToString("x32"), "N");
        f.Local.LoseAcknowledgement = true;
        await f.Upload(firstKey);
        var seed = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync(r => r.ProductContextJson != null);
        var intent = ReadStoredIntent(seed.ProductContextJson);
        for (var i = 2; i <= 52; i++)
        {
            var key = Guid.ParseExact(i.ToString("x32"), "N");
            f.Db.StorageCapacityReservations.Add(new StorageCapacityReservation
            {
                IdempotencyKey = StorageObjectService.GetCapacityReservationKey($"case-document:{f.Case.Id}:{f.User.Id}:{key:N}"),
                ResourceKey = seed.ResourceKey,
                GroupId = "owned-discovery-" + key.ToString("N"),
                ObjectId = Guid.NewGuid(),
                BindingHash = seed.BindingHash,
                Bytes = seed.Bytes,
                LocalLocator = "owned/metadata-only",
                ProductContextJson = i == 2 ? "{broken" : System.Text.Json.JsonSerializer.Serialize(intent with { UploadKey = key },
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            });
        }
        await f.Db.SaveChangesAsync();
        var reader = new CaseDocumentUploadStatusReader(f.Db, f.Monitor);
        var first = (await reader.ListPageAsync(f.Case.Id, f.User.Id, CancellationToken.None))!;
        Assert.Equal(49, first.Items.Count); Assert.NotNull(first.NextCursor);
        Assert.All(first.Items, s => { Assert.Equal("manualRetry", s.Status); Assert.Equal("evidence.txt", s.FileName); });
        var second = (await reader.ListPageAsync(f.Case.Id, f.User.Id, CancellationToken.None, first.NextCursor))!;
        Assert.Equal(2, second.Items.Count); Assert.Null(second.NextCursor);
        Assert.Equal(51, first.Items.Concat(second.Items).Select(s => s.UploadKey).Distinct().Count());
        Assert.Equal(first.NextCursor, (await reader.ListPageAsync(f.Case.Id, f.User.Id, CancellationToken.None))!.NextCursor);
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(0, f.Local.ReadCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToListAsync());
        var foreignCaseCursor = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
            Encoding.UTF8.GetBytes($"write:case-document:{f.Case.Id + 1}:{f.User.Id}:{firstKey:N}"));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ListPageAsync(f.Case.Id, f.User.Id, CancellationToken.None, foreignCaseCursor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryDeduplicatesReserveAndCommitAndRefreshesPublishedAttempt(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        f.Options.AutoCaseDocumentPublicationEnabled = true;
        var key = Guid.NewGuid(); await f.Upload(key);
        var commit = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == "commit");
        // A retained primary reservation can overlap its committed catalog entry after lost ACK.
        var reserve = await f.Db.StorageCapacityReservations.AsNoTracking().FirstAsync(r => r.ProductContextJson != null);
        await f.Db.StorageCapacityReservations.Where(r => r.IdempotencyKey == reserve.IdempotencyKey && r.ResourceKey == reserve.ResourceKey)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, StorageCapacityReservationStatuses.Pending)
                .SetProperty(r => r.ProductContextJson, commit.PayloadJson));
        var reader = new CaseDocumentUploadStatusReader(f.Db, f.Monitor);
        Assert.Equal(key, Assert.Single((await reader.ListPageAsync(f.Case.Id, f.User.Id, CancellationToken.None))!.Items).UploadKey);
        var controller = f.Controller();
        var page = Assert.IsType<OkObjectResult>((await controller.GetPendingDocumentUploads(f.Case.Id, CancellationToken.None)).Result);
        Assert.Single(Assert.IsType<CaseDocumentUploadPage>(page.Value).Items);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        Assert.IsType<BadRequestObjectResult>((await controller.GetPendingDocumentUploads(f.Case.Id, CancellationToken.None, "invalid-cursor")).Result);
        await f.CopyPendingAsync();
        f.Created(await f.Upload(key));
        var after = (await reader.ListPageAsync(f.Case.Id, f.User.Id, CancellationToken.None))!;
        Assert.All(after.Items, s => Assert.Equal("uploaded", s.Status));
        Assert.Null(after.NextCursor);
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryCursorAdvancesPastInvalidUnicodeKeysWithoutPublishingOrReadingFiles(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var prefix = $"write:case-document:{f.Case.Id}:{f.User.Id}:";
        var resource = System.Text.Json.JsonSerializer.Serialize(new string?[] { "local", null });
        var suffixes = Enumerable.Range(1, 49).Select(i => i.ToString("x32"))
            .Concat(new[] { string.Concat(Enumerable.Repeat("🧪", 32)), string.Concat(Enumerable.Repeat("🦁", 32)) }).ToArray();
        foreach (var suffix in suffixes) f.Db.StorageCapacityReservations.Add(new StorageCapacityReservation
        {
            IdempotencyKey = prefix + suffix + ":capacity",
            ResourceKey = resource,
            GroupId = "owned-unicode-discovery",
            ObjectId = Guid.NewGuid(),
            BindingHash = new string('a', 64),
            Bytes = 1,
            LocalLocator = "owned/no-file",
            ProductContextJson = "{broken"
        });
        await f.Db.SaveChangesAsync();
        var reader = new CaseDocumentUploadStatusReader(f.Db, f.Monitor);
        var first = (await reader.ListPageAsync(f.Case.Id, f.User.Id, CancellationToken.None))!;
        Assert.Empty(first.Items); Assert.NotNull(first.NextCursor);
        var second = (await reader.ListPageAsync(f.Case.Id, f.User.Id, CancellationToken.None, first.NextCursor))!;
        Assert.Empty(second.Items); Assert.Null(second.NextCursor);
        var unicodeCursor = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(prefix + suffixes[^1]));
        var afterUnicode = (await reader.ListPageAsync(f.Case.Id, f.User.Id, CancellationToken.None, unicodeCursor))!;
        Assert.Empty(afterUnicode.Items);
        Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Local.ReadCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusRejectsAnotherObjectsGenerationEvenWhenBytesAndGenerationNumberMatch(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var key = Guid.NewGuid(); f.Created(await f.Upload(key)); f.Created(await f.Upload(Guid.NewGuid()));
        var first = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.IdempotencyKey ==
            StorageObjectService.GetCommitOperationKey($"case-document:{f.Case.Id}:{f.User.Id}:{key:N}"));
        var other = await f.Db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == "commit" && o.Id != first.Id);
        await f.Db.StorageOperations.Where(o => o.Id == first.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.StorageObjectGenerationId, other.StorageObjectGenerationId));
        var state = Assert.Single((await new CaseDocumentUploadStatusReader(f.Db, f.Monitor)
            .ReadAsync(f.Case.Id, f.User.Id, [key], CancellationToken.None))!);
        Assert.Equal("unknown", state.Status); Assert.Null(state.DocumentId); Assert.Null(state.FileName);
        Assert.Equal(2, f.Local.PutCount);
    }

    [Fact]
    public async Task PostgreSqlDiscoveryRevocationAfterWindowReadReturnsNoNames()
    {
        await using var f = await CreateFixtureAsync(true, requiredCopies: 2);
        await f.Upload(Guid.NewGuid());
        await using var sibling = f.OpenSiblingContext();
        f.StatusHook.AfterDiscoveryRead = async () =>
            await sibling.RolePermissions.ExecuteUpdateAsync(s => s.SetProperty(p => p.CanEdit, false));
        Assert.Null(await new CaseDocumentUploadStatusReader(f.Db, f.Monitor)
            .ListPageAsync(f.Case.Id, f.User.Id, CancellationToken.None));
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Equal(1, f.Local.PutCount);
    }

    [Fact]
    public async Task PostgreSqlDiscoveryUsesScopedIndexesWithUnrelatedPendingHistory()
    {
        await using var f = await CreateFixtureAsync(true, requiredCopies: 2);
        await f.Upload(Guid.NewGuid());
        var obj = await f.Db.StorageObjects.AsNoTracking().Select(o => o.Id).SingleAsync();
        var localResource = System.Text.Json.JsonSerializer.Serialize(new string?[] { "local", null });
        await f.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "StorageOperations" ("Id", "StorageObjectId", "OperationType", "Status", "IdempotencyKey", "AttemptCount", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), {obj}, 'commit', 'succeeded', 'unrelated-case-discovery:' || i, 0, now(), now()
            FROM generate_series(1, 20000) AS i
            """);
        await f.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "StorageCapacityReservations" ("IdempotencyKey", "ResourceKey", "GroupId", "BindingHash", "Status", "Bytes", "ObjectId", "LocalLocator", "ProductContextJson", "CreatedAt", "UpdatedAt")
            SELECT 'unrelated-reserve-discovery:' || i, {localResource}, 'synthetic-noise', {new string('a', 64)}, 'pending', 1,
                gen_random_uuid(), 'owned/no-file', {"{}"}, now(), now()
            FROM generate_series(1, 20000) AS i
            """);
        await f.Db.Database.ExecuteSqlRawAsync("ANALYZE \"StorageOperations\"; ANALYZE \"StorageCapacityReservations\";");
        var page = (await new CaseDocumentUploadStatusReader(f.Db, f.Monitor)
            .ListPageAsync(f.Case.Id, f.User.Id, CancellationToken.None))!;
        Assert.Single(page.Items);
        Assert.NotNull(f.StatusHook.DiscoverySql);
        await f.Db.Database.OpenConnectionAsync();
        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + f.StatusHook.DiscoverySql,
            (NpgsqlConnection)f.Db.Database.GetDbConnection());
        foreach (var parameter in f.StatusHook.DiscoveryParameters) explain.Parameters.Add(parameter);
        using var plan = System.Text.Json.JsonDocument.Parse((string)(await explain.ExecuteScalarAsync())!);
        Console.WriteLine("Owned case discovery query plan: " + plan.RootElement.GetRawText());
        var nodes = DiscoveryPlanNodes(plan.RootElement[0].GetProperty("Plan")).ToArray();
        foreach (var relation in new[] { "StorageOperations", "StorageCapacityReservations" })
        {
            var reads = nodes.Where(n => n.TryGetProperty("Relation Name", out var r) && r.GetString() == relation).ToArray();
            Assert.NotEmpty(reads);
            Assert.DoesNotContain(reads, n => n.GetProperty("Node Type").GetString() == "Seq Scan");
            Assert.All(reads, n => Assert.True(n.GetProperty("Actual Rows").GetInt32() <= 51));
        }
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(0, f.Local.ReadCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
    }

    private static IEnumerable<System.Text.Json.JsonElement> DiscoveryPlanNodes(System.Text.Json.JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray())
                foreach (var descendant in DiscoveryPlanNodes(child)) yield return descendant;
    }

    [Theory]
    [InlineData(false, "pending", "waiting")]
    [InlineData(true, "pending", "waiting")]
    [InlineData(false, "running", "waiting")]
    [InlineData(true, "running", "waiting")]
    [InlineData(false, "failed", "retrying")]
    [InlineData(true, "failed", "retrying")]
    [InlineData(false, "deadLetter", "failed")]
    [InlineData(true, "deadLetter", "failed")]
    [InlineData(false, "succeeded", "failed")]
    [InlineData(true, "succeeded", "failed")]
    public async Task QueueAwareStatusReportsOnlyTheBoundPublicationWithoutProviderIo(bool postgres, string queueStatus, string expected)
    {
        await using var f = await CreateFixtureAsync(postgres, requiredCopies: 2);
        f.Options.AutoCaseDocumentPublicationEnabled = true; f.Options.WorkerEnabled = true;
        var key = Guid.NewGuid();
        await f.Upload(key);
        await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishCaseDocument)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, queueStatus));
        await f.RecreateContext();
        var reads = f.Local.ReadCount;
        f.StatusHook.ResetMetadataReads();
        var keys = Enumerable.Range(0, 49).Select(_ => Guid.NewGuid()).Append(key).ToArray();
        var states = await new CaseDocumentUploadStatusReader(f.Db, f.Monitor, new StorageWorkerAvailability(true))
            .ReadAsync(f.Case.Id, f.User.Id, keys, CancellationToken.None);
        Assert.Equal(50, states!.Count);
        var state = Assert.Single(states, s => s.UploadKey == key);
        Assert.Equal(expected, state.Status);
        Assert.Equal("evidence.txt", state.FileName);
        Assert.Null(state.DocumentId); Assert.Null(state.Sha256);
        Assert.Equal(49, states.Count(s => s.Status == "unknown" && s.FileName is null));
        var storageReads = queueStatus is "deadLetter" or "succeeded" ? 5 : 4;
        Assert.Equal(storageReads, f.StatusHook.MetadataReads - f.StatusHook.FinancialProofReads);
        AssertFinancialStatusBatch(f.StatusHook, postgres, 49);
        f.StatusHook.ResetMetadataReads();
        var known = Assert.Single((await new CaseDocumentUploadStatusReader(f.Db, f.Monitor, new StorageWorkerAvailability(true))
            .ReadAsync(f.Case.Id, f.User.Id, [key], CancellationToken.None))!);
        Assert.Equal(expected, known.Status); Assert.Equal(0, f.StatusHook.FinancialProofReads);
        Assert.Equal(storageReads, f.StatusHook.MetadataReads);
        Assert.Equal(reads, f.Local.ReadCount); Assert.Equal(1, f.Local.PutCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("workerAbsent", "paused")]
    [InlineData("workerDisabled", "paused")]
    [InlineData("autoDisabled", "paused")]
    [InlineData("writeDisabled", "paused")]
    [InlineData("capacityDisabled", "paused")]
    [InlineData("wrongGeneration", "manualRetry")]
    [InlineData("wrongKey", "manualRetry")]
    public async Task QueueAwareStatusNeverPromisesAnUnavailableOrForeignWorker(string change, string expected)
    {
        await using var f = await CreateFixtureAsync(requiredCopies: 2);
        f.Options.AutoCaseDocumentPublicationEnabled = true; f.Options.WorkerEnabled = true;
        var key = Guid.NewGuid(); await f.Upload(key);
        if (change == "workerDisabled") f.Options.WorkerEnabled = false;
        if (change == "autoDisabled") f.Options.AutoCaseDocumentPublicationEnabled = false;
        if (change == "writeDisabled") f.Options.ProductWriteEnabledClasses.Clear();
        if (change == "capacityDisabled") f.Options.UploadCapacityChecksEnabled = false;
        if (change == "wrongGeneration") await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishCaseDocument)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.StorageObjectGenerationId, (Guid?)null));
        if (change == "wrongKey") await f.Db.StorageOperations.Where(o => o.OperationType == StorageOperationTypes.PublishCaseDocument)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.IdempotencyKey, "foreign-publication"));
        var state = Assert.Single((await new CaseDocumentUploadStatusReader(f.Db, f.Monitor,
            new StorageWorkerAvailability(change != "workerAbsent"))
            .ReadAsync(f.Case.Id, f.User.Id, [key], CancellationToken.None))!);
        Assert.Equal(expected, state.Status);
        Assert.Null(state.DocumentId); Assert.Equal(1, f.Local.PutCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task QueueAwareStatusRefreshesAReceiptPublishedBetweenMetadataReadsOnPostgreSql()
    {
        await using var f = await CreateFixtureAsync(true, requiredCopies: 2);
        var receiver = new User
        {
            Email = "case-status-receiver@example.local",
            FullName = "Получатель",
            PasswordHash = "synthetic",
            IsActive = true,
            IsApproved = true,
            RoleId = f.User.RoleId
        };
        f.Db.Users.Add(receiver);
        (await f.Db.RolePermissions.SingleAsync(p => p.Section == RolePermissionCatalog.Cases)).ViewScope = DataVisibilityScope.All;
        await f.Db.SaveChangesAsync();
        (await f.Db.Clients.SingleAsync(c => c.Id == f.Case.ClientId)).ResponsibleUserId = receiver.Id;
        await f.Db.SaveChangesAsync();
        f.Options.AutoCaseDocumentPublicationEnabled = true; f.Options.WorkerEnabled = true;
        var key = Guid.NewGuid(); await f.Upload(key); await f.CopyPendingAsync();
        await using var sibling = f.OpenSiblingContext();
        f.StatusHook.BeforePublicationRead = async () =>
        {
            f.StatusHook.BeforePublicationRead = null;
            var worker = new StorageOperationProcessor(sibling, f.Registry, f.Monitor, TimeProvider.System,
                NullLogger<StorageOperationProcessor>.Instance, casePublications: f.CasePublisher(scope: sibling));
            Assert.Equal(1, (await worker.ProcessBatchAsync("owned-status-publication-race", CancellationToken.None)).Succeeded);
        };
        f.StatusHook.ResetMetadataReads();
        var reader = new CaseDocumentUploadStatusReader(f.Db, f.Monitor, new StorageWorkerAvailability(true));
        var state = Assert.Single((await reader.ReadAsync(f.Case.Id, f.User.Id, [key], CancellationToken.None))!);
        Assert.Equal("uploaded", state.Status);
        var document = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.Equal(document.Id, state.DocumentId);
        Assert.Equal(document.Version, state.Version);
        Assert.Equal(6, f.StatusHook.MetadataReads);
        Assert.Equal(1, f.Local.PutCount); Assert.Equal(1, f.Mirror.PutCount);
        Assert.Single(await f.Db.UserNotifications.AsNoTracking().ToListAsync());
        Assert.Equal("uploaded", Assert.Single((await reader.ReadAsync(f.Case.Id, f.User.Id, [key], CancellationToken.None))!).Status);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusReadsDurableReservationBeforeCatalogAndPausesWithoutAutomaticPromise(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var key = Guid.NewGuid();
        f.Local.LoseAcknowledgement = true;
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.Upload(key)).Result).StatusCode);
        await f.RecreateContext();
        var reader = new CaseDocumentUploadStatusReader(f.Db, f.Monitor);
        var state = Assert.Single((await reader.ReadAsync(f.Case.Id, f.User.Id, [key], CancellationToken.None))!);
        Assert.Equal("manualRetry", state.Status);
        Assert.Equal("evidence.txt", state.FileName);
        Assert.Null(state.DocumentId);
        Assert.Null(state.Sha256);
        f.Options.ProductWriteEnabledClasses.Clear();
        Assert.Equal("paused", Assert.Single((await reader.ReadAsync(f.Case.Id, f.User.Id, [key], CancellationToken.None))!).Status);
        Assert.Equal(1, f.Local.PutCount);
        f.Options.ProductWriteEnabledClasses.Add("case-document");
        f.Created(await f.Upload(key));
        f.Options.ProductWriteEnabledClasses.Clear();
        Assert.Equal("uploaded", Assert.Single((await reader.ReadAsync(f.Case.Id, f.User.Id, [key], CancellationToken.None))!).Status);
    }

    [Theory]
    [InlineData("document")]
    [InlineData("binding")]
    [InlineData("owner")]
    [InlineData("generation")]
    [InlineData("hash")]
    [InlineData("case")]
    [InlineData("actor")]
    [InlineData("payload")]
    public async Task StatusNeverConfirmsMissingOrChangedReceipt(string change)
    {
        await using var f = await CreateFixtureAsync();
        var key = Guid.NewGuid();
        f.Created(await f.Upload(key));
        if (change == "document") await f.Db.ClientCaseDocuments.ExecuteDeleteAsync();
        else if (change == "binding") await f.Db.StorageProductBindings.ExecuteDeleteAsync();
        else if (change == "owner") await f.Db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.OwnerId, "other-owner"));
        else if (change == "generation") await f.Db.StorageObjects.ExecuteUpdateAsync(s => s.SetProperty(o => o.CurrentGeneration, 99));
        else if (change == "hash") await f.Db.StorageObjectGenerations.ExecuteUpdateAsync(s => s.SetProperty(g => g.Sha256, new string('a', 64)));
        else if (change == "case") await f.Db.StorageOperations.ExecuteUpdateAsync(s => s.SetProperty(o => o.PayloadJson,
            o => o.PayloadJson!.Replace("\"caseId\":1", "\"caseId\":999")));
        else if (change == "actor") await f.Db.StorageOperations.ExecuteUpdateAsync(s => s.SetProperty(o => o.PayloadJson,
            o => o.PayloadJson!.Replace("\"actorId\":1", "\"actorId\":999")));
        else await f.Db.StorageOperations.ExecuteUpdateAsync(s => s.SetProperty(o => o.PayloadJson, "{broken"));
        var state = Assert.Single((await new CaseDocumentUploadStatusReader(f.Db, f.Monitor)
            .ReadAsync(f.Case.Id, f.User.Id, [key], CancellationToken.None))!);
        Assert.NotEqual("uploaded", state.Status);
        Assert.Null(state.DocumentId);
        Assert.Null(state.FileName);
        Assert.Null(state.Sha256);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("unapproved")]
    [InlineData("permission")]
    [InlineData("visibility")]
    public async Task StatusRevokedAccessReturnsNoNamesOrMetadata(string change)
    {
        await using var f = await CreateFixtureAsync();
        var key = Guid.NewGuid();
        f.Created(await f.Upload(key));
        if (change == "inactive") await f.Db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        else if (change == "unapproved") await f.Db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.IsApproved, false));
        else if (change == "permission") await f.Db.RolePermissions.ExecuteUpdateAsync(s => s.SetProperty(p => p.CanEdit, false));
        else
        {
            await f.Db.ClientCases.ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsibleUserId, (int?)null));
            await f.Db.Clients.ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsibleUserId, (int?)null));
        }
        Assert.Null(await new CaseDocumentUploadStatusReader(f.Db, f.Monitor).ReadAsync(f.Case.Id, f.User.Id, [key], CancellationToken.None));
    }

    [Fact]
    public async Task StatusKeysAreScopedToActorAndCaseAndBoundBeforeQuerying()
    {
        await using var f = await CreateFixtureAsync();
        var key = Guid.NewGuid();
        f.Created(await f.Upload(key));
        var other = new User
        {
            Email = "other-status@example.local",
            FullName = "Другой",
            PasswordHash = "synthetic",
            IsActive = true,
            IsApproved = true,
            RoleId = f.User.RoleId
        };
        f.Db.Users.Add(other);
        await f.Db.SaveChangesAsync();
        var reader = new CaseDocumentUploadStatusReader(f.Db, f.Monitor);
        Assert.Null(await reader.ReadAsync(f.Case.Id, other.Id, [key], CancellationToken.None));
        Assert.Null(await reader.ReadAsync(f.Case.Id + 999, f.User.Id, [key], CancellationToken.None));
        var secondCase = new ClientCase { ClientId = f.Case.ClientId, ResponsibleUserId = other.Id, Title = "Другое дело" };
        f.Db.ClientCases.Add(secondCase);
        await f.Db.SaveChangesAsync();
        await f.Db.RolePermissions.ExecuteUpdateAsync(s => s.SetProperty(p => p.ViewScope, DataVisibilityScope.All));
        var foreignActor = Assert.Single((await reader.ReadAsync(f.Case.Id, other.Id, [key], CancellationToken.None))!);
        Assert.Equal("unknown", foreignActor.Status);
        Assert.Null(foreignActor.FileName);
        var foreignCase = Assert.Single((await reader.ReadAsync(secondCase.Id, f.User.Id, [key], CancellationToken.None))!);
        Assert.Equal("unknown", foreignCase.Status);
        Assert.Null(foreignCase.FileName);
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(f.Case.Id, f.User.Id, [], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(f.Case.Id, f.User.Id, [Guid.Empty], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(f.Case.Id, f.User.Id, Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray(), CancellationToken.None));
    }

    [Theory]
    [InlineData("content")]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("status")]
    [InlineData("content-type")]
    [InlineData("version")]
    public async Task ReusingKeyForDifferentRequestCannotOverwriteOrDuplicate(string field)
    {
        await using var f = await CreateFixtureAsync();
        var key = Guid.NewGuid();
        var first = f.Created(await f.Upload(key));
        var changed = await f.Upload(key, body: field == "content" ? "other case bytes" : "owned case bytes",
            name: field == "name" ? "other.txt" : "evidence.txt", type: field == "type" ? "Другой тип" : "Документ",
            status: field == "status" ? "Утверждён" : "Подготовлен", contentType: field == "content-type" ? "text/csv" : "text/plain",
            documentKey: field == "version" ? first.DocumentKey : null);
        Assert.IsType<ConflictObjectResult>(changed.Result);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        var row = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.Equal("owned case bytes", await File.ReadAllTextAsync(f.CaseStorage.ResolvePath(row.StoragePath)));
    }

    [Fact]
    public async Task NewVersionAndReplayPreserveOriginalBytes()
    {
        await using var f = await CreateFixtureAsync();
        var first = f.Created(await f.Upload(Guid.NewGuid()));
        var key = Guid.NewGuid();
        var version = f.Created(await f.Upload(key, body: "new case version", versionOf: first.Id));
        var replay = f.Created(await f.Upload(key, body: "new case version", versionOf: first.Id));
        Assert.Equal(2, version.Version);
        Assert.Equal(first.DocumentKey, version.DocumentKey);
        Assert.Equal(version.Id, replay.Id);
        Assert.Equal(2, f.Local.PutCount);
        var original = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync(d => d.Id == first.Id);
        Assert.Equal("owned case bytes", await File.ReadAllTextAsync(f.CaseStorage.ResolvePath(original.StoragePath)));
        Assert.Equal(2, await f.Db.StorageProductBindings.CountAsync());
    }

    [Fact]
    public async Task MutableDocumentMetadataIsNotRevertedByExactReceiptReplay()
    {
        await using var f = await CreateFixtureAsync();
        var key = Guid.NewGuid();
        var first = f.Created(await f.Upload(key));
        var row = await f.Db.ClientCaseDocuments.SingleAsync(d => d.Id == first.Id);
        row.DocumentType = "Проверенный документ";
        row.Status = "Утверждён";
        await f.Db.SaveChangesAsync();
        var receipt = f.Created(await f.Upload(key));
        Assert.Equal(first.Id, receipt.Id);
        Assert.Equal("Утверждён", receipt.Status);
        Assert.Equal("Проверенный документ", receipt.DocumentType);
        Assert.Equal(1, f.Local.PutCount);
    }

    [Fact]
    public async Task RequiredCopiesGateKeepsDomainEmptyUntilMirrorIsVerified()
    {
        await using var f = await CreateFixtureAsync(requiredCopies: 2);
        var key = Guid.NewGuid();
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.Upload(key)).Result).StatusCode);
        Assert.Empty(await f.Db.ClientCaseDocuments.ToListAsync());
        Assert.Empty(await f.Db.StorageProductBindings.ToListAsync());
        Assert.Empty(await f.Db.UserNotifications.ToListAsync());
        Assert.Equal("", (await f.Db.ClientCases.AsNoTracking().SingleAsync()).EventHistory);
        var processor = new StorageOperationProcessor(f.Db, f.Registry, f.Monitor, TimeProvider.System, NullLogger<StorageOperationProcessor>.Instance);
        Assert.Equal(1, (await processor.ProcessBatchAsync("owned-case-mirror", CancellationToken.None)).Succeeded);
        f.Created(await f.Upload(key));
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(1, f.Mirror.PutCount);
        Assert.Single(await f.Db.ClientCaseDocuments.ToListAsync());
        Assert.Equal(2, await f.Db.StorageReplicas.CountAsync(r => r.Status == StorageReplicaStatuses.Available));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task LostLocalAcknowledgementResumesWithoutSecondPut(bool postgres, bool badHead)
    {
        await using var f = await CreateFixtureAsync(postgres);
        f.Local.LoseAcknowledgement = true;
        var key = Guid.NewGuid();
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.Upload(key)).Result).StatusCode);
        Assert.Empty(await f.Db.ClientCaseDocuments.ToListAsync());
        var context = await f.Db.StorageCapacityReservations.AsNoTracking().Where(r => r.ProductContextJson != null)
            .Select(r => r.ProductContextJson).SingleAsync();
        using var intent = System.Text.Json.JsonDocument.Parse(context!);
        var documentKey = intent.RootElement.GetProperty("documentKey").GetString();
        var uploadedAt = intent.RootElement.GetProperty("uploadedAt").GetDateTime();
        await f.RecreateContext();
        f.Local.BadHead = badHead;
        var retry = await f.Upload(key);
        if (badHead) Assert.Equal(503, Assert.IsType<ObjectResult>(retry.Result).StatusCode);
        else
        {
            var saved = f.Created(retry);
            var row = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync(d => d.Id == saved.Id);
            Assert.Equal(documentKey, row.DocumentKey);
            Assert.Equal(uploadedAt, row.UploadedAt);
        }
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(badHead ? 0 : 1, await f.Db.ClientCaseDocuments.CountAsync());
    }

    [Theory]
    [InlineData(false, "before-save")]
    [InlineData(false, "after-save")]
    [InlineData(false, "before-commit")]
    [InlineData(false, "lost-commit")]
    [InlineData(false, "rollback")]
    [InlineData(true, "before-save")]
    [InlineData(true, "before-commit")]
    [InlineData(true, "lost-commit")]
    public async Task PublicationFailurePreservesDurableIntentAndExactContextRetry(bool postgres, string stage)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var key = Guid.NewGuid();
        f.Fault.Stage = stage;
        f.Fault.Enabled = true;
        await Assert.ThrowsAsync<IOException>(() => f.Upload(key));
        f.Fault.Enabled = false;
        var committed = stage == "lost-commit";
        Assert.Equal(committed ? 1 : 0, await f.Db.ClientCaseDocuments.AsNoTracking().CountAsync());
        Assert.Equal(committed ? 1 : 0, await f.Db.StorageProductBindings.AsNoTracking().CountAsync());
        await f.Db.SaveChangesAsync();
        Assert.Equal(committed ? 1 : 0, await f.Db.ClientCaseDocuments.AsNoTracking().CountAsync());
        var saved = f.Created(await f.Upload(key));
        Assert.Equal(1, f.Local.PutCount);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.UserNotifications.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.ObjectAuditEvents.AsNoTracking().Where(a => a.ObjectType == "client-case-document").ToListAsync());
        var row = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync(d => d.Id == saved.Id);
        Assert.Equal("owned case bytes", await File.ReadAllTextAsync(f.CaseStorage.ResolvePath(row.StoragePath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSqlOverlappingRequestsRespectReservedIntentAndPublishedReceipt(bool conflictingBytes)
    {
        await using var f = await CreateFixtureAsync(postgres: true);
        await using var sibling = f.OpenSiblingContext();
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Local.AfterPut = async () =>
        {
            written.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        };
        var key = Guid.NewGuid();
        var first = f.Upload(key);
        try
        {
            await written.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var controller = f.Controller(scope: sibling);
            controller.Request.Headers["X-Upload-Key"] = key.ToString();
            var second = await controller.UploadDocument(f.Case.Id,
                CreateFile(conflictingBytes ? "other case bytes" : "owned case bytes"), "Документ", "Подготовлен", null, CancellationToken.None);
            if (conflictingBytes) Assert.IsType<ConflictObjectResult>(second.Result);
            else Assert.True(f.Created(second).Id > 0);
        }
        finally { release.TrySetResult(); }
        var saved = f.Created(await first);
        var replay = f.Created(await f.Upload(key));
        Assert.Equal(saved.Id, replay.Id);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.UserNotifications.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.ObjectAuditEvents.AsNoTracking().Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Equal("owned case bytes", await File.ReadAllTextAsync(f.CaseStorage.ResolvePath(
            (await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync()).StoragePath)));
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("unapproved")]
    [InlineData("permission")]
    [InlineData("visibility")]
    [InlineData("flag")]
    public async Task PublicationRechecksLiveRightsAndFlagsAfterProviderWrite(string change)
    {
        await using var f = await CreateFixtureAsync();
        f.Local.AfterPut = async () =>
        {
            if (change == "flag") f.Options.ProductWriteEnabledClasses.Clear();
            else if (change == "inactive") await f.Db.Users.Where(u => u.Id == f.User.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
            else if (change == "unapproved") await f.Db.Users.Where(u => u.Id == f.User.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsApproved, false));
            else if (change == "permission") await f.Db.RolePermissions.ExecuteUpdateAsync(s => s.SetProperty(p => p.CanEdit, false));
            else
            {
                await f.Db.ClientCases.Where(c => c.Id == f.Case.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsibleUserId, (int?)null));
                await f.Db.Clients.Where(c => c.Id == f.Case.ClientId).ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsibleUserId, (int?)null));
            }
        };
        var result = await f.Upload(Guid.NewGuid());
        if (change == "flag") Assert.Equal(503, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        else Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(await f.Db.ClientCaseDocuments.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.StorageProductBindings.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.UserNotifications.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.StorageObjects.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task ManagedEndpointRequiresAttemptIdentityBeforeReadingBytes(string header)
    {
        await using var f = await CreateFixtureAsync();
        var controller = f.Controller();
        if (header.Length > 0) controller.Request.Headers["X-Upload-Key"] = header;
        var result = await controller.UploadDocument(f.Case.Id, CreateFile("owned case bytes"), "Документ", "Подготовлен", null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(0, f.Local.PutCount);
        Assert.Empty(await f.Db.StorageObjects.ToListAsync());
    }

    [Fact]
    public async Task CapacityPrerequisiteCannotFallBackToLegacyOrPublishDocument()
    {
        await using var f = await CreateFixtureAsync();
        f.Options.UploadCapacityChecksEnabled = false;
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.Upload(Guid.NewGuid())).Result).StatusCode);
        Assert.Equal(0, f.Local.PutCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.ToListAsync());
        Assert.Empty(Directory.EnumerateFiles(f.Root, "*.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DisabledManagedRouteKeepsExistingLegacyUpload()
    {
        await using var f = await CreateFixtureAsync();
        f.Options.ProductWriteEnabledClasses.Clear();
        f.Created(await f.Upload(Guid.NewGuid()));
        Assert.Equal(0, f.Local.PutCount);
        Assert.Empty(await f.Db.StorageObjects.ToListAsync());
        Assert.Single(await f.Db.ClientCaseDocuments.ToListAsync());
    }

    [Fact]
    public async Task ExactGuidIsScopedByCaseAndActor()
    {
        await using var f = await CreateFixtureAsync();
        var key = Guid.NewGuid();
        f.Created(await f.Upload(key));
        var otherCase = new ClientCase { ClientId = f.Case.ClientId, ResponsibleUserId = f.User.Id, Title = "Другое дело" };
        f.Db.ClientCases.Add(otherCase);
        var role = await f.Db.Roles.SingleAsync();
        var other = new User { Email = "other-managed-case@example.local", FullName = "Другой автор", PasswordHash = "synthetic", IsActive = true, IsApproved = true, RoleId = role.Id };
        f.Db.Users.Add(other);
        var permission = await f.Db.RolePermissions.SingleAsync();
        permission.ViewScope = DataVisibilityScope.All;
        await f.Db.SaveChangesAsync();
        f.Created(await f.Upload(key, caseId: otherCase.Id));
        f.Created(await f.Upload(key, actor: other));
        Assert.Equal(3, await f.Db.ClientCaseDocuments.CountAsync());
        Assert.Equal(3, await f.Db.StorageObjects.CountAsync());
        Assert.Equal(3, f.Local.PutCount);
        Assert.Equal(3, await f.Db.ClientCaseDocuments.Select(d => d.DocumentKey).Distinct().CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedDomainOrCatalogCannotBeResurrectedByReceiptReplay(bool deleteCatalog)
    {
        await using var f = await CreateFixtureAsync();
        var key = Guid.NewGuid();
        var first = f.Created(await f.Upload(key));
        // Administrative deletion is a later request with fresh concurrency tokens.
        await f.RecreateContext();
        if (deleteCatalog)
        {
            var storageObject = await f.Db.StorageObjects.SingleAsync();
            storageObject.IsDeleted = true;
        }
        else f.Db.ClientCaseDocuments.Remove(await f.Db.ClientCaseDocuments.SingleAsync(d => d.Id == first.Id));
        await f.Db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>((await f.Upload(key)).Result);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(deleteCatalog ? 1 : 0, await f.Db.ClientCaseDocuments.CountAsync());
    }

    [Fact]
    public async Task CapacityCannotAdmitStageWhenSafetyMarginExceedsKnownDiskSpace()
    {
        await using var f = await CreateFixtureAsync();
        f.Options.UploadMinimumFreeBytes = long.MaxValue;
        Assert.Equal(503, Assert.IsType<ObjectResult>((await f.Upload(Guid.NewGuid())).Result).StatusCode);
        Assert.Equal(0, f.Local.PutCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.ToListAsync());
        Assert.Empty(Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SharedTaskWriterRecoversReservedMetadataAfterLocalAckLoss()
    {
        await using var f = await CreateFixtureAsync();
        f.Options.ProductWriteEnabledClasses.Add("task-file");
        var task = new TaskJournalItem
        {
            Title = "Синтетическая задача",
            AuthorUserId = f.User.Id,
            AssigneeUserId = f.User.Id,
            ClientCaseId = f.Case.Id,
            DueAt = DateTime.UtcNow.AddDays(1),
            UpdatedAt = DateTime.UtcNow
        };
        f.Db.TaskJournalItems.Add(task); await f.Db.SaveChangesAsync();
        var key = Guid.NewGuid();
        f.Local.LoseAcknowledgement = true;
        var first = await f.TaskWriter().UploadAsync(new(task.Id, f.User.Id, f.User.FullName, key, CreateFile("owned case bytes"), "evidence.txt", null), CancellationToken.None);
        Assert.Equal("unavailable", first.Status);
        Assert.Empty(await f.Db.TaskJournalFiles.ToListAsync());
        await f.RecreateContext();
        var retried = await f.TaskWriter().UploadAsync(new(task.Id, f.User.Id, f.User.FullName, key, CreateFile("owned case bytes"), "evidence.txt", null), CancellationToken.None);
        Assert.Equal("uploaded", retried.Status);
        Assert.Equal(2, f.Local.PutCount); // one task object, one linked case object
        Assert.Single(await f.Db.TaskJournalFiles.ToListAsync());
        Assert.Single(await f.Db.ClientCaseDocuments.ToListAsync());
    }

    [Fact]
    public async Task SharedPhotoWriterRetainsOriginalPointerAcrossAckLossAndConcurrentPhotoChange()
    {
        await using var f = await CreateFixtureAsync();
        f.Options.ProductWriteEnabledClasses.Add("profile-photo");
        var key = Guid.NewGuid();
        f.Local.LoseAcknowledgement = true;
        Assert.Equal("unavailable", (await f.PhotoWriter().UploadAsync(f.User.Id, key,
            CreateFile("owned photo bytes", "photo.png", "image/png"), "https://owned.example.local", CancellationToken.None)).Status);
        await f.RecreateContext();
        var user = await f.Db.Users.SingleAsync();
        var previousPhoto = user.PhotoUrl;
        user.PhotoUrl = "https://owned.example.local/user-photos/other-photo.png";
        await f.Db.SaveChangesAsync();
        var conflict = await f.PhotoWriter().UploadAsync(f.User.Id, key,
            CreateFile("owned photo bytes", "photo.png", "image/png"), "https://owned.example.local", CancellationToken.None);
        Assert.Equal("conflict", conflict.Status);
        Assert.Equal(1, f.Local.PutCount);
        Assert.Empty(await f.Db.StorageProductBindings.ToListAsync());
        // A failed publication does not authorize replacing the independently changed pointer.
        await f.RecreateContext();
        user = await f.Db.Users.SingleAsync();
        Assert.Equal("https://owned.example.local/user-photos/other-photo.png", user.PhotoUrl);
        user.PhotoUrl = previousPhoto;
        await f.Db.SaveChangesAsync();
        Assert.Equal("uploaded", (await f.PhotoWriter().UploadAsync(f.User.Id, key,
            CreateFile("owned photo bytes", "photo.png", "image/png"), "https://owned.example.local", CancellationToken.None)).Status);
        Assert.Equal(1, f.Local.PutCount);
    }

    [Fact]
    public async Task PostgreSqlStatusRevocationDuringMetadataReadDoesNotDiscloseTheReceipt()
    {
        await using var f = await CreateFixtureAsync(postgres: true);
        await using var sibling = f.OpenSiblingContext();
        var key = Guid.NewGuid();
        f.Created(await f.Upload(key));
        f.StatusHook.AfterCommitRead = async () => await sibling.RolePermissions.ExecuteUpdateAsync(s => s.SetProperty(p => p.CanEdit, false));
        Assert.Null(await new CaseDocumentUploadStatusReader(f.Db, f.Monitor)
            .ReadAsync(f.Case.Id, f.User.Id, [key], CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusReadsFiftyAttemptsInBoundedStorageAndFinancialMetadataQueries(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres);
        var key = Guid.NewGuid();
        f.Created(await f.Upload(key));
        f.StatusHook.ResetMetadataReads();
        var keys = Enumerable.Range(0, 49).Select(_ => Guid.NewGuid()).Append(key).ToArray();
        var states = await new CaseDocumentUploadStatusReader(f.Db, f.Monitor).ReadAsync(f.Case.Id, f.User.Id, keys, CancellationToken.None);
        Assert.Equal(50, states!.Count);
        Assert.Equal(1, states.Count(s => s.Status == "uploaded"));
        Assert.Equal(4, f.StatusHook.MetadataReads - f.StatusHook.FinancialProofReads);
        AssertFinancialStatusBatch(f.StatusHook, postgres, 49);
        f.StatusHook.ResetMetadataReads();
        var known = Assert.Single((await new CaseDocumentUploadStatusReader(f.Db, f.Monitor)
            .ReadAsync(f.Case.Id, f.User.Id, [key], CancellationToken.None))!);
        Assert.Equal("uploaded", known.Status); Assert.Equal(0, f.StatusHook.FinancialProofReads);
        Assert.Equal(4, f.StatusHook.MetadataReads);
    }

    private static void AssertFinancialStatusBatch(StatusReadHook hook, bool postgres, int keys)
    {
        Assert.Equal(1, hook.FinancialProofReads); Assert.Equal(keys, hook.MaxFinancialKeys);
        var sql = Assert.IsType<string>(hook.FinancialProofSql);
        Assert.Contains("\"ClientCaseId\" =", sql); Assert.Contains("\"ActorId\" =", sql);
        Assert.Contains(postgres ? "ANY" : "json_each", sql);
        Assert.DoesNotContain("FrozenDomainJson", sql); Assert.DoesNotContain("GeneratedFinancialDocumentContents", sql);
    }

    private static IFormFile CreateFile(string body, string name = "evidence.txt", string contentType = "text/plain")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name) { Headers = new HeaderDictionary(), ContentType = contentType };
    }

    private sealed class NoopEmailService : IEmailService
    {
        public Task SendEmailAsync(string toEmail, string subject, string body, bool isHtml = false, CancellationToken cancellationToken = default, EmailSendSettings? settings = null)
            => Task.CompletedTask;

        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName)
            => Task.CompletedTask;

        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName)
            => Task.CompletedTask;

        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason)
            => Task.CompletedTask;

        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail)
            => Task.CompletedTask;

        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType)
            => Task.CompletedTask;
    }

    private sealed class NoopTelegramNotificationService : ITelegramNotificationService
    {
        public Task<TelegramSendResult> SendMessageAsync(long chatId, string text, CancellationToken cancellationToken = default)
            => Task.FromResult(new TelegramSendResult(true, "sent"));

        public Task<TelegramSendResult> SendMessageWithTokenAsync(long chatId, string text, string botToken, CancellationToken cancellationToken = default, TelegramBotConnectionSettings? connection = null)
            => Task.FromResult(new TelegramSendResult(true, "sent"));

        public Task<TelegramNotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TelegramNotificationSettingsDto());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; private init; } = "";
        public PaymentContext Db { get; private set; } = null!;
        public User User { get; private init; } = null!;
        public ClientCase Case { get; private init; } = null!;
        public StorageOrchestrationOptions Options { get; private init; } = null!;
        public StaticOptions Monitor { get; private init; } = null!;
        public ObservedProvider Local { get; private init; } = null!;
        public ObservedProvider Mirror { get; private init; } = null!;
        public StorageProviderRegistry Registry { get; private init; } = null!;
        public PublicationFault Fault { get; private init; } = null!;
        public StatusReadHook StatusHook { get; private init; } = null!;
        public LocalCaseDocumentStorage CaseStorage { get; private init; } = null!;
        private TestEnvironment Environment { get; init; } = null!;
        public IConfiguration Configuration { get; init; } = null!;
        private IStorageObjectService Storage { get; set; } = null!;
        public IManagedCaseDocumentUploadService Managed { get; private set; } = null!;
        private PostgreSqlModelTemplate.DatabaseLease? ModelLease { get; init; }
        private SqliteConnection? Sqlite { get; init; }
        private NpgsqlConnection? Admin { get; init; }
        private string? DatabaseName { get; init; }
        private string? ConnectionString { get; init; }

        public CasesV2Controller Controller(User? actor = null, PaymentContext? scope = null, ICaseDocumentStorage? caseFiles = null)
        {
            var db = scope ?? Db;
            var policies = new StoragePolicyService(Monitor, Registry);
            var storage = scope is null ? Storage : new StorageObjectService(db, policies, Registry, Monitor);
            var managed = scope is null ? Managed : new ManagedCaseDocumentUploadService(db, storage,
                new ProductStorageBindingService(db, policies), Environment, Configuration, Monitor,
                new StorageStageCapacityService(db, Registry, Monitor, Environment, Configuration));
            return new(db, Environment, caseDocumentStorage: caseFiles ?? CaseStorage, configuration: Configuration,
                managedCaseUploads: managed, productStorageReader: new ProductStorageReader(db, storage, Monitor),
                caseUploadStatusReader: new CaseDocumentUploadStatusReader(db, Monitor))
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, (actor ?? User).Id.ToString(CultureInfo.InvariantCulture)),
                        new Claim(ClaimTypes.Name, (actor ?? User).FullName)], "owned-fixture"))
                    }
                }
            };
        }
        public PaymentContext OpenSiblingContext(DbCommandInterceptor? observer = null)
        {
            if (Sqlite is not null)
            {
                var sqliteBuilder = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(Sqlite);
                if (observer is not null) sqliteBuilder.AddInterceptors(observer);
                return new PaymentContext(sqliteBuilder.Options, useObjectAuditModel: true);
            }
            Assert.NotNull(ConnectionString);
            var builder = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(ConnectionString);
            if (observer is not null) builder.AddInterceptors(observer);
            return new PostgreSqlPaymentContext(builder.Options);
        }
        public async Task CopyPendingAsync()
        {
            var processor = new StorageOperationProcessor(Db, Registry, Monitor, TimeProvider.System, NullLogger<StorageOperationProcessor>.Instance);
            Assert.Equal(1, (await processor.ProcessBatchAsync("owned-case-resume-copy", CancellationToken.None)).Succeeded);
        }
        public ICaseDocumentPublicationProcessor CasePublisher(TimeProvider? clock = null, PaymentContext? scope = null)
        {
            var db = scope ?? Db;
            var policies = new StoragePolicyService(Monitor, Registry);
            var storage = scope is null ? Storage : new StorageObjectService(db, policies, Registry, Monitor);
            var managed = scope is null ? Managed : new ManagedCaseDocumentUploadService(db, storage,
                new ProductStorageBindingService(db, policies), Environment, Configuration, Monitor,
                new StorageStageCapacityService(db, Registry, Monitor, Environment, Configuration));
            var notifications = new CaseDocumentNotificationService(db, new NotificationService(db, Configuration,
                new NoopEmailService(), new NoopTelegramNotificationService(), NullLogger<NotificationService>.Instance));
            var records = new CaseDocumentPublicationRecords(db, notifications);
            return new CaseDocumentPublicationProcessor(db, managed, records,
                notifications, CaseStorage, policies, Monitor, clock ?? TimeProvider.System,
                NullLogger<CaseDocumentPublicationProcessor>.Instance, new GeneratedWorkOrderDocumentPublication(db, records),
                new GeneratedTemplateDocumentPublication(db, records),
                new GeneratedCourtDocumentPublication(db, records, new CourtDocumentSourceRegistry(db)),
                new GeneratedFinancialDocumentPublication(db, records, new GeneratedFinancialDocumentRegistry(db), policies));
        }
        public Task<StorageOperationBatchResult> RunCaseQueueAsync(TimeProvider? clock = null)
            => new StorageOperationProcessor(Db, Registry, Monitor, clock ?? TimeProvider.System,
                NullLogger<StorageOperationProcessor>.Instance, casePublications: CasePublisher(clock))
                .ProcessBatchAsync("owned-case-queue", CancellationToken.None);
        public Task<ManagedCaseDocumentUploadResult> VerifyAsync(Guid operation) => Managed.VerifyPublishedAsync(operation, CancellationToken.None);
        public ManagedCaseDocumentUploadRequest GeneratedRequest(Guid key) => new(Case.Id, User.Id, User.FullName, key,
            CreateFile("original generated bytes", "generated.html", "text/html"), "generated.html", "Иск", "Подготовлен", "template-claim");
        public Func<Task>? AfterGeneratedPublication { get; set; }
        public async Task<ClientCaseWorkOrder> SeedGeneratedWorkOrderAsync()
        {
            var recipient = new User
            {
                Email = "generated-recipient@example.local",
                FullName = "Получатель",
                PasswordHash = "synthetic",
                IsActive = true,
                IsApproved = true,
                RoleId = User.RoleId
            };
            Db.Users.Add(recipient);
            await Db.SaveChangesAsync();
            var client = await Db.Clients.SingleAsync(c => c.Id == Case.ClientId);
            client.ResponsibleUserId = recipient.Id;
            var order = new ClientCaseWorkOrder { ClientCaseId = Case.Id, Title = "Исходный наряд", Status = "approved" };
            Db.ClientCaseWorkOrders.Add(order);
            await Db.SaveChangesAsync();
            return order;
        }
        public Task<ManagedCaseDocumentUploadResult> StageGeneratedWorkOrderAsync(ClientCaseWorkOrder order, Guid key)
        {
            var name = $"case-{Case.Id}-work-order.html";
            var request = new ManagedCaseDocumentUploadRequest(Case.Id, User.Id, User.FullName, key,
                CreateFile("<html>original work order</html>", name, "text/html; charset=utf-8"),
                name, "Заказ-наряд", "Согласован", $"work-order-{order.Id}");
            var generation = new ManagedCaseDocumentGeneration("work-order", new string('a', 64),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    version = 1,
                    caseId = Case.Id,
                    workOrderId = order.Id,
                    title = order.Title,
                    documentStatus = "Согласован"
                }));
            return Managed.GenerateAsync(request, generation, (_, _, _) => Task.FromResult(true), PersistGenerated,
                GeneratedAtomic, CancellationToken.None);
        }
        public async Task SeedGeneratedTemplateAsync(string templateKey = "custom")
        {
            await SeedGeneratedWorkOrderAsync(); // Existing synthetic notification recipient.
            Case.CaseType = "Гражданское";
            Db.SystemSettings.Add(new SystemSetting
            {
                Key = "cases.documentTemplates",
                Value = System.Text.Json.JsonSerializer.Serialize(new { templates = new[] { new { key = templateKey, label = "Исходный шаблон", caseTypes = new[] { "Гражданское" } } } })
            });
            await Db.SaveChangesAsync();
        }
        public Task<ManagedCaseDocumentUploadResult> StageGeneratedTemplateAsync(Guid key, string templateKey = "custom", bool publishTemplate = false)
        {
            const string name = "Исходный шаблон.html";
            var request = new ManagedCaseDocumentUploadRequest(Case.Id, User.Id, User.FullName, key,
                CreateFile("<html>original template</html>", name, "text/html; charset=utf-8"),
                name, "Иск", "Подготовлен", $"template-{templateKey}");
            var generation = new ManagedCaseDocumentGeneration("template", new string('a', 64),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    version = 1,
                    caseId = Case.Id,
                    templateKey,
                    templateVersion = 1,
                    requestedTemplateKey = templateKey,
                    requestedDocumentType = "Иск",
                    fileName = name,
                    documentType = "Иск",
                    documentStatus = "Подготовлен"
                }));
            if (publishTemplate)
            {
                var templates = TemplatePublication();
                return Managed.GenerateAsync(request, generation, (_, _, _) => Task.FromResult(true), templates.PersistAsync,
                    GeneratedAtomic, CancellationToken.None, templates.CanPublishAsync);
            }
            return Managed.GenerateAsync(request, generation, (_, _, _) => Task.FromResult(true), PersistGenerated,
                GeneratedAtomic, CancellationToken.None);
        }
        public Task<StorageObjectReadResult?> ReadStoredGeneratedAsync(Guid objectId)
            => Storage.OpenReadAsync(objectId, CancellationToken.None);
        public async Task<ManagedCaseDocumentUploadResult> GeneratedAtomic(string path,
            Func<Task<ManagedCaseDocumentUploadResult>> publish, CancellationToken ct)
        {
            var result = await CaseDocumentAtomicSave.SaveAsync(Db, CaseStorage, path, () => Task.CompletedTask, publish, ct, preserveFileOnFailure: true);
            if (AfterGeneratedPublication is not null) await AfterGeneratedPublication();
            return result;
        }
        private async Task PersistGenerated(ClientCaseDocument document, ManagedCaseDocumentGeneration generation, CancellationToken ct)
        {
            await Db.SaveChangesAsync(ct);
            CaseDocumentPublicationRecords.AddAuditEvent(Db, Case, document, "Generated", generation.FrozenDomainJson, User.Id, document.UploadedByName);
            await Db.SaveChangesAsync(ct);
        }
        public Task<ManagedCaseDocumentUploadResult> Generate(Guid key, ManagedCaseDocumentGeneration generation,
            Func<int, int, CancellationToken, Task<bool>>? authority = null, PaymentContext? scope = null)
        {
            if (scope is null) return Managed.GenerateAsync(GeneratedRequest(key), generation,
                authority ?? ((_, _, _) => Task.FromResult(true)), PersistGenerated, GeneratedAtomic, CancellationToken.None);
            var policies = new StoragePolicyService(Monitor, Registry);
            var storage = new StorageObjectService(scope, policies, Registry, Monitor);
            var writer = new ManagedCaseDocumentUploadService(scope, storage, new ProductStorageBindingService(scope, policies),
                Environment, Configuration, Monitor, new StorageStageCapacityService(scope, Registry, Monitor, Environment, Configuration));
            return writer.GenerateAsync(GeneratedRequest(key), generation, authority ?? ((_, _, _) => Task.FromResult(true)),
                async (document, frozen, token) =>
                {
                    await scope.SaveChangesAsync(token);
                    var clientCase = await scope.ClientCases.AsNoTracking().SingleAsync(c => c.Id == Case.Id, token);
                    CaseDocumentPublicationRecords.AddAuditEvent(scope, clientCase, document, "Generated", frozen.FrozenDomainJson,
                        User.Id, document.UploadedByName);
                    await scope.SaveChangesAsync(token);
                }, (path, publish, token) => CaseDocumentAtomicSave.SaveAsync(scope, CaseStorage, path, () => Task.CompletedTask,
                    publish, token, preserveFileOnFailure: true), CancellationToken.None);
        }
        public GeneratedTemplateDocumentPublication TemplatePublication()
        {
            var notifications = new CaseDocumentNotificationService(Db, new NotificationService(Db, Configuration,
                new NoopEmailService(), new NoopTelegramNotificationService(), NullLogger<NotificationService>.Instance));
            return new GeneratedTemplateDocumentPublication(Db, new CaseDocumentPublicationRecords(Db, notifications));
        }
        public Task<ManagedCaseDocumentUploadResult?> ResumeTemplate(Guid key)
        {
            var templates = TemplatePublication();
            return Managed.TryResumeGeneratedAsync(Case.Id, User.Id, key, "template", new string('a', 64),
                (_, _, _) => Task.FromResult(true), templates.PersistAsync, GeneratedAtomic, CancellationToken.None,
                templates.CanPublishAsync);
        }
        public Task<ManagedCaseDocumentUploadResult?> ResumeGenerated(Guid key, string kind = "template", string? hash = null,
            Func<ManagedCaseDocumentGenerationSource, CancellationToken, Task<bool>>? authorizeSource = null)
            => Managed.TryResumeGeneratedAsync(Case.Id, User.Id, key, kind, hash ?? new string('a', 64),
                (_, _, _) => Task.FromResult(true), PersistGenerated, GeneratedAtomic, CancellationToken.None, authorizeSource);
        public async Task<ManagedCaseDocumentUploadResult> ResumeAsync(Guid operation,
            Func<int, int, CancellationToken, Task<bool>>? authority = null)
        {
            var notifications = new CaseDocumentNotificationService(Db, new NotificationService(Db, Configuration,
                new NoopEmailService(), new NoopTelegramNotificationService(), NullLogger<NotificationService>.Instance));
            var records = new CaseDocumentPublicationRecords(Db, notifications);
            var result = await Managed.ResumeAsync(operation, authority ?? ((_, _, _) => Task.FromResult(true)), async (document, ct) =>
            {
                var clientCase = await Db.ClientCases.SingleAsync(c => c.Id == document.ClientCaseId, ct);
                await records.PersistUploadAsync(clientCase, document, document.Version == 1, document.UploadedByUserId!.Value,
                    document.UploadedByName, DateTime.SpecifyKind(document.UploadedAt, DateTimeKind.Utc).ToLocalTime(), ct);
            }, (path, publish, ct) => CaseDocumentAtomicSave.SaveAsync(Db, CaseStorage, path, () => Task.CompletedTask,
                publish, ct, preserveFileOnFailure: true), CancellationToken.None);
            await notifications.FlushCommittedAsync(CancellationToken.None);
            return result;
        }
        public async Task<ActionResult<ClientCaseDocumentDto>> Upload(Guid key, string body = "owned case bytes", string name = "evidence.txt",
            string type = "Документ", string status = "Подготовлен", string contentType = "text/plain", string? documentKey = null, int? versionOf = null,
            int? caseId = null, User? actor = null)
        {
            var controller = Controller(actor);
            controller.Request.Headers["X-Upload-Key"] = key.ToString();
            return versionOf.HasValue
                ? await controller.UploadDocumentVersionById(versionOf.Value, CreateFile(body, name, contentType), type, status, CancellationToken.None)
                : await controller.UploadDocument(caseId ?? Case.Id, CreateFile(body, name, contentType), type, status, documentKey, CancellationToken.None);
        }
        public Task<ActionResult<ClientCaseDocumentDto>> GenerateTemplateHttp(Guid key)
        {
            var controller = Controller();
            controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
            return controller.GenerateDocumentTemplate(Case.Id, new() { TemplateKey = "custom", DocumentType = "Иск", Status = "Подготовлен" }, CancellationToken.None);
        }
        public Task<ActionResult<ClientCaseDocumentDto>> GenerateWorkOrderHttp(Guid key)
        {
            var controller = Controller();
            controller.Request.Headers["X-Upload-Key"] = key.ToString("D");
            return controller.GenerateWorkOrderDocument(Case.Id, CancellationToken.None);
        }
        public ClientCaseDocumentDto Created(ActionResult<ClientCaseDocumentDto> result)
            => Assert.IsType<ClientCaseDocumentDto>(Assert.IsType<CreatedResult>(result.Result).Value);

        public static async Task<Fixture> CreateAsync(bool postgres = false, int requiredCopies = 1, bool migrated = false,
            PostgreSqlModelTemplate? modelTemplate = null, Func<Fixture, Task>? beforeReady = null)
        {
            var setupStarted = Stopwatch.GetTimestamp();
            var root = Path.Combine(Path.GetTempPath(), "pp-managed-case-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fault = new PublicationFault();
            var statusHook = new StatusReadHook();
            var schemaStarted = Stopwatch.GetTimestamp();
            PostgreSqlModelTemplate.DatabaseLease? modelLease = null;
            PaymentContext? db = null;
            SqliteConnection? sqlite = null;
            NpgsqlConnection? admin = null;
            string? databaseName = null;
            string? connectionString = null;
            try
            {
                if (postgres)
                {
                    var raw = System.Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
                    Assert.False(string.IsNullOrWhiteSpace(raw), "Owned PostgreSQL fixture requires PAYPLANNER_TEST_POSTGRES.");
                    var settings = new NpgsqlConnectionStringBuilder(raw) { Pooling = false };
                    Assert.Contains(settings.Host, new[] { "127.0.0.1", "localhost", "::1" });
                    if (!migrated && modelTemplate is not null)
                    {
                        modelLease = await modelTemplate.CreateDatabaseAsync();
                        databaseName = modelLease.Name;
                        connectionString = modelLease.ConnectionString;
                        settings = new NpgsqlConnectionStringBuilder(connectionString);
                    }
                    else
                    {
                        databaseName = "pp_managed_case_" + Guid.NewGuid().ToString("N");
                        Console.WriteLine($"Owned managed case database: {databaseName}");
                        admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(settings.ConnectionString) { Database = "postgres" }.ConnectionString);
                        await admin.OpenAsync();
                        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin)) await create.ExecuteNonQueryAsync();
                        settings.Database = databaseName;
                        connectionString = settings.ConnectionString;
                    }
                    db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString)
                        .AddInterceptors(fault, new PublicationSaveFault(fault), statusHook).Options);
                }
                else
                {
                    sqlite = new SqliteConnection("Data Source=:memory:");
                    await sqlite.OpenAsync();
                    if (!migrated)
                    {
                        // Keep this fixture's connection ownership and all three fault/status
                        // interceptors; clone only the existing factory's empty EnsureCreated schema.
                        await using var schema = await SqliteTestContextFactory.CreateAsync(useObjectAuditModel: true);
                        ((SqliteConnection)schema.Database.GetDbConnection()).BackupDatabase(sqlite);
                    }
                    var sqliteOptions = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(sqlite)
                        .AddInterceptors(fault, new PublicationSaveFault(fault), statusHook);
                    if (migrated) sqliteOptions.UsePayPlannerSqliteMigrations();
                    db = new PaymentContext(sqliteOptions.Options, useObjectAuditModel: true);
                }
                if (migrated) await db.Database.MigrateAsync();
                else if (postgres && modelLease is null) await db.Database.EnsureCreatedAsync();
                var mode = migrated ? postgres ? "migrated-pg" : "migrated-sqlite"
                    : postgres ? modelLease is null ? "native-pg" : "model-pg" : "sqlite-template";
                ReportPhase(mode, "schema", schemaStarted);
                var seedStarted = Stopwatch.GetTimestamp();
                var role = new Role
                {
                    Name = "owned-case-editor",
                    Permissions = [new RolePermission {
                    Section = RolePermissionCatalog.Cases, CanView = true, CanEdit = true, ViewScope = DataVisibilityScope.Own }]
                };
                var user = new User { Email = "managed-case@example.local", FullName = "Автор", PasswordHash = "synthetic", IsActive = true, IsApproved = true, Role = role };
                var client = new Client { Name = "Синтетический клиент", ResponsibleUser = user };
                var clientCase = new ClientCase { Client = client, ResponsibleUser = user, Title = "Синтетическое дело", Status = ClientCaseStatus.Open };
                db.ClientCases.Add(clientCase); await db.SaveChangesAsync();
                ReportPhase(mode, "seed", seedStarted);
                var storageStarted = Stopwatch.GetTimestamp();
                var environment = new TestEnvironment(root);
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FileStorage:RootPath"] = Path.Combine(root, "files"),
                    ["StorageOrchestration:LocalRoot"] = Path.Combine(root, "objects"),
                    ["Email:Enabled"] = "false",
                    ["BackgroundServices:Enabled"] = "false"
                }).Build();
                var mirrorConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["StorageOrchestration:LocalRoot"] = Path.Combine(root, "mirror")
                }).Build();
                var local = new ObservedProvider("local", new LocalStorageProvider(environment, config));
                var mirror = new ObservedProvider("mirror", new LocalStorageProvider(environment, mirrorConfig));
                var registry = new StorageProviderRegistry([local, mirror, new LegacyLocalStorageProvider(environment, config)]);
                var options = new StorageOrchestrationOptions
                {
                    Enabled = true,
                    UploadCapacityChecksEnabled = true,
                    UploadMinimumFreeBytes = 0,
                    ProductWriteEnabledClasses = ["case-document"],
                    ProductReadEnabledClasses = ["case-document"],
                    ProviderConnections = new() { ["mirror"] = "owned-synthetic" },
                    DefaultPolicy = new()
                    {
                        RequiredCopies = requiredCopies,
                        WriteProviders = requiredCopies == 2 ? ["local", "mirror"] : ["local"],
                        ReadProviders = requiredCopies == 2 ? ["local", "mirror"] : ["local"]
                    }
                };
                var monitor = new StaticOptions(options);
                var policies = new StoragePolicyService(monitor, registry);
                var storage = new StorageObjectService(db, policies, registry, monitor);
                var capacity = new StorageStageCapacityService(db, registry, monitor, environment, config);
                var fixture = new Fixture
                {
                    Root = root,
                    Db = db,
                    User = user,
                    Case = clientCase,
                    Options = options,
                    Monitor = monitor,
                    Local = local,
                    Mirror = mirror,
                    Registry = registry,
                    Fault = fault,
                    StatusHook = statusHook,
                    Environment = environment,
                    Configuration = config,
                    CaseStorage = new LocalCaseDocumentStorage(environment, config),
                    Storage = storage,
                    Managed = new ManagedCaseDocumentUploadService(db, storage, new ProductStorageBindingService(db, policies), environment, config, monitor, capacity),
                    ModelLease = modelLease,
                    Sqlite = sqlite,
                    Admin = admin,
                    DatabaseName = databaseName,
                    ConnectionString = connectionString
                };
                ReportPhase(mode, "storage", storageStarted);
                if (beforeReady is not null) await beforeReady(fixture);
                ReportPhase(mode, "setup", setupStarted);
                return fixture;
            }
            catch (Exception error) when (postgres && !migrated && modelTemplate is not null)
            {
                var errors = new List<Exception> { error };
                if (db is not null)
                {
                    try { await db.DisposeAsync(); }
                    catch (Exception cleanup) { errors.Add(cleanup); }
                }
                if (modelLease is not null)
                {
                    try { await modelLease.DisposeAsync(); }
                    catch (Exception cleanup) { errors.Add(cleanup); }
                }
                try
                {
                    Assert.StartsWith("pp-managed-case-", Path.GetFileName(root));
                    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                }
                catch (Exception cleanup) { errors.Add(cleanup); }
                if (errors.Count > 1) throw new AggregateException("Model fixture setup and cleanup failed.", errors);
                throw;
            }
        }

        private static void ReportPhase(string mode, string phase, long started)
        {
            try { Console.WriteLine(FormattableString.Invariant($"PP_FIXTURE_PHASE family=ManagedCaseUpload mode={mode} phase={phase} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}")); }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            { /* Diagnostics must not hide a business failure or lose a lease. */ }
        }

        public IManagedTaskFileUploadService TaskWriter() => new ManagedTaskFileUploadService(Db, Storage,
            new ProductStorageBindingService(Db, new StoragePolicyService(Monitor, Registry)), Environment, Configuration, Monitor,
            new StorageStageCapacityService(Db, Registry, Monitor, Environment, Configuration));
        public IManagedProfilePhotoUploadService PhotoWriter() => new ManagedProfilePhotoUploadService(Db, Storage,
            new ProductStorageBindingService(Db, new StoragePolicyService(Monitor, Registry)), Environment, Configuration, Monitor,
            new StorageStageCapacityService(Db, Registry, Monitor, Environment, Configuration));
        public async Task RecreateContext()
        {
            await Db.DisposeAsync();
            Db = Sqlite is not null
                ? new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(Sqlite)
                    .AddInterceptors(Fault, new PublicationSaveFault(Fault), StatusHook).Options, useObjectAuditModel: true)
                : new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(ConnectionString)
                    .AddInterceptors(Fault, new PublicationSaveFault(Fault), StatusHook).Options);
            var policies = new StoragePolicyService(Monitor, Registry);
            Storage = new StorageObjectService(Db, policies, Registry, Monitor);
            Managed = new ManagedCaseDocumentUploadService(Db, Storage, new ProductStorageBindingService(Db, policies), Environment, Configuration, Monitor,
                new StorageStageCapacityService(Db, Registry, Monitor, Environment, Configuration));
        }

        public async ValueTask DisposeAsync()
        {
            var started = Stopwatch.GetTimestamp();
            await Db.DisposeAsync();
            if (Sqlite is not null) await Sqlite.DisposeAsync();
            if (ModelLease is not null) await ModelLease.DisposeAsync();
            if (Admin is not null)
            {
                await using var sessions = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @name", Admin);
                sessions.Parameters.AddWithValue("name", DatabaseName!);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                long remaining;
                do { remaining = (long)(await sessions.ExecuteScalarAsync())!; if (remaining == 0) break; await Task.Delay(50); }
                while (DateTime.UtcNow < deadline);
                Assert.Equal(0L, remaining);
                await using (var drop = new NpgsqlCommand($"DROP DATABASE \"{DatabaseName}\"", Admin)) await drop.ExecuteNonQueryAsync();
                await Admin.DisposeAsync();
            }
            Assert.StartsWith("pp-managed-case-", Path.GetFileName(Root));
            Directory.Delete(Root, true);
            ReportPhase(ModelLease is not null ? "model-pg" : Admin is not null ? "native-pg" : "sqlite", "cleanup", started);
        }
    }

    private sealed class PublicationFault : DbTransactionInterceptor
    {
        public Action? AfterDomainSave { get; set; }
        public bool Enabled { get; set; }
        public bool Armed { get; set; }
        public string Stage { get; set; } = "";
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Enabled && Armed && Stage == "before-commit") throw new IOException("Owned managed case pre-commit failure");
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Enabled && Armed && Stage == "lost-commit") throw new IOException("Owned managed case lost commit ACK");
            return Task.CompletedTask;
        }
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Enabled && Armed && Stage == "rollback") throw new IOException("Owned managed case unconfirmed rollback");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class PublicationSaveFault(PublicationFault fault) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (fault.Enabled && eventData.Context!.ChangeTracker.Entries<ClientCaseDocument>().Any(e => e.State == EntityState.Added)) fault.Armed = true;
            if (fault.Enabled && fault.Armed && fault.Stage is "before-save" or "rollback") throw new IOException("Owned managed case save failure");
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (fault.Enabled && fault.Armed && fault.AfterDomainSave is { } callback)
            {
                fault.AfterDomainSave = null;
                callback();
            }
            if (fault.Enabled && fault.Armed && fault.Stage == "after-save") throw new IOException("Owned managed case lost save ACK");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class StatusReadHook : DbCommandInterceptor
    {
        public int PublicationScheduleReads { get; set; }
        public int MetadataReads { get; set; }
        public int FinancialProofReads { get; private set; }
        public int MaxFinancialKeys { get; private set; }
        public string? FinancialProofSql { get; private set; }
        public void ResetMetadataReads()
        {
            MetadataReads = 0; FinancialProofReads = 0; MaxFinancialKeys = 0; FinancialProofSql = null;
        }
        public Func<Task>? AfterRetirementRead { get; set; }
        public Func<Task>? AfterCommitRead { get; set; }
        public Func<Task>? BeforeGeneratedProofRead { get; set; }
        public Func<Task>? BeforePublicationRead { get; set; }
        public Func<Task>? AfterDiscoveryRead { get; set; }
        public string? DiscoverySql { get; private set; }
        public NpgsqlParameter[] DiscoveryParameters { get; private set; } = [];
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("case-generated-resume-proof", StringComparison.Ordinal) && BeforeGeneratedProofRead is not null)
                await BeforeGeneratedProofRead();
            if (command.CommandText.Contains("case-upload-status-publications", StringComparison.Ordinal) && BeforePublicationRead is not null)
                await BeforePublicationRead();
            return result;
        }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("case-upload-status-", StringComparison.Ordinal)) MetadataReads++;
            if (command.CommandText.Contains("case-upload-status-financial-proofs", StringComparison.Ordinal))
            {
                FinancialProofReads++; FinancialProofSql = command.CommandText;
                foreach (DbParameter parameter in command.Parameters)
                {
                    if (parameter.Value is Guid[] ids) MaxFinancialKeys = Math.Max(MaxFinancialKeys, ids.Length);
                    else if (parameter.Value is string json && json.StartsWith("[", StringComparison.Ordinal))
                    {
                        using var values = System.Text.Json.JsonDocument.Parse(json);
                        if (values.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                            MaxFinancialKeys = Math.Max(MaxFinancialKeys, values.RootElement.GetArrayLength());
                    }
                }
            }
            if (command.CommandText.Contains("case-upload-status-retirements", StringComparison.Ordinal) && AfterRetirementRead is not null)
                await AfterRetirementRead();
            if (command.CommandText.Contains("case-document-publication-schedule", StringComparison.Ordinal)) PublicationScheduleReads++;
            if (command.CommandText.Contains("case-upload-status-commits", StringComparison.Ordinal) && AfterCommitRead is not null)
                await AfterCommitRead();
            if (command.CommandText.Contains("case-upload-discovery-window", StringComparison.Ordinal) && AfterDiscoveryRead is not null)
                await AfterDiscoveryRead();
            if (command is NpgsqlCommand && command.CommandText.Contains("case-upload-discovery-window", StringComparison.Ordinal))
            {
                DiscoverySql = command.CommandText;
                DiscoveryParameters = command.Parameters.Cast<NpgsqlParameter>().Select(p => (NpgsqlParameter)((ICloneable)p).Clone()).ToArray();
            }
            return result;
        }
    }

    private sealed class ObservedProvider(string key, LocalStorageProvider inner) : IStorageProvider, IStorageCapacityProvider
    {
        public string Key => key;
        public StorageProviderCapabilities Capabilities => inner.Capabilities;
        public int PutCount { get; private set; }
        public int ReadCount { get; private set; }
        public Func<Task>? AfterRead { get; set; }
        public bool LoseAcknowledgement { get; set; }
        public bool BadHead { get; set; }
        public bool BadRead { get; set; }
        public Func<Task>? AfterPut { get; set; }
        public async Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct)
        {
            PutCount++;
            var saved = await inner.PutAsync(request, ct);
            if (AfterPut is not null) await AfterPut();
            if (LoseAcknowledgement && saved.Status == StorageOperationStatus.Succeeded)
            {
                LoseAcknowledgement = false;
                return saved with { Status = StorageOperationStatus.Unknown };
            }
            return saved;
        }
        public async Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct)
        {
            var metadata = await inner.HeadAsync(locator, ct);
            return BadHead && metadata.Status == StorageOperationStatus.Succeeded
                ? metadata with { Status = StorageOperationStatus.Unknown } : metadata;
        }
        public async Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct)
        {
            ReadCount++;
            var result = await inner.OpenReadAsync(locator, ct);
            if (BadRead && result.Content is not null)
            {
                await result.Content.DisposeAsync();
                result = result with { Content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("corrupt frozen bytes")) };
            }
            try
            {
                if (AfterRead is not null) await AfterRead();
                return result;
            }
            catch
            {
                if (result.Content is not null) await result.Content.DisposeAsync();
                throw;
            }
        }
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct) => inner.DeleteAsync(locator, ct);
        public Task<StorageCapacityResource> ObserveCapacityAsync(string resourceKey, string? connectionId, long minimumFreeBytes, CancellationToken ct)
            => inner.ObserveCapacityAsync(resourceKey, null, minimumFreeBytes, ct);
    }
    private sealed class StaticOptions(StorageOrchestrationOptions value) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => value;
        public StorageOrchestrationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }
    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
