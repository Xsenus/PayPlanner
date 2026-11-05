using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedGeneratedCaseDocumentTests
{
    public static IEnumerable<object[]> DeferredCourtControlCases()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var queue in new[] { false, true })
                foreach (var change in new[] { "legacy", "deadline-link", "deadline-source", "deadline-status",
                    "deadline-key", "deadline-type", "moved", "malformed", "wrong-attempt", "wrong-version", "closed",
                    "post-read-link", "post-read-source" })
                    yield return [postgres, queue, change];
    }

    [Theory]
    [MemberData(nameof(DeferredCourtControlCases))]
    public async Task DeferredCourtControlsRequireAcceptedProvenance(bool postgres, bool queue, string change)
    {
        await using var f = await CreateFixtureAsync(postgres, 2, autoPublication: true);
        Assert.Equal(1, (await GenerateCourtAsync(f, CourtDeadlineSource(f.Case.CourtUrl))).PendingCourtActDocuments);
        var attempt = await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync();
        var snapshot = JsonSerializer.Deserialize<GeneratedCourtDocumentSnapshot>(attempt.FrozenSnapshotJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var receipt = new CourtDocumentSourceReceipt(snapshot,
            new ManagedCaseDocumentGeneration("court", attempt.RequestSha256, attempt.FrozenSnapshotJson));
        var originalBytes = (await f.Db.CourtDocumentSourceContents.AsNoTracking().SingleAsync()).Bytes;
        Assert.NotNull((await f.Db.CourtDocumentSources.AsNoTracking().SingleAsync()).AcceptedControlsJson);
        await f.CopyPendingAsync();
        f.Db.ChangeTracker.Clear();
        var before = await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync();
        var called = false;
        async Task ChangeAsync()
        {
            if (called) return;
            called = true;
            await using var editor = f.Observer();
            var mode = change.Replace("post-read-", "deadline-", StringComparison.Ordinal);
            if (mode is "deadline-link" or "deadline-source" or "deadline-status")
                await ChangeCourtManagedAdmissionAsync(editor, f.User, f.Case, mode);
            else if (mode == "legacy")
                await editor.CourtDocumentSources.ExecuteUpdateAsync(set => set.SetProperty(row => row.AcceptedControlsJson, (string?)null));
            else if (mode == "deadline-key")
                await editor.ClientCaseProceduralDeadlines.ExecuteUpdateAsync(set => set.SetProperty(row => row.SourceDocumentKey, "sudrf-act-" + new string('f', 32)));
            else if (mode == "deadline-type")
                await editor.ClientCaseProceduralDeadlines.ExecuteUpdateAsync(set => set.SetProperty(row => row.SourceType, "manual"));
            else if (mode == "closed")
                await editor.ClientCaseProceduralDeadlines.ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "closed"));
            else if (mode == "moved")
            {
                var destination = new ClientCase { ClientId = f.Case.ClientId, Title = "Другое синтетическое дело" };
                editor.ClientCases.Add(destination);
                await editor.SaveChangesAsync();
                await editor.ClientCaseProceduralDeadlines.ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, destination.Id));
            }
            else
            {
                var proof = JsonNode.Parse((await editor.CourtDocumentSources.AsNoTracking().SingleAsync()).AcceptedControlsJson!)!;
                if (mode == "wrong-attempt") proof["attemptId"] = Guid.NewGuid();
                else if (mode == "wrong-version") proof["scopeVersion"] = proof["scopeVersion"]!.GetValue<long>() + 1;
                else Assert.Equal("malformed", mode);
                var json = mode == "malformed" ? "{" : proof.ToJsonString();
                await editor.CourtDocumentSources.ExecuteUpdateAsync(set => set.SetProperty(row => row.AcceptedControlsJson, json));
            }
            before = await editor.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync();
        }
        if (change.StartsWith("post-read-", StringComparison.Ordinal)) f.Local.BeforeReadAsync = _ => ChangeAsync();
        else await ChangeAsync();
        var tasksBefore = JsonSerializer.Serialize(await f.Db.TaskJournalItems.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync());
        if (queue) Assert.Equal(1, (await f.RunCourtQueueAsync()).Succeeded);
        else Assert.Equal("uploaded", (await f.PublishCourt(receipt)).Status);
        Assert.True(called);
        await using var observer = f.Observer();
        var after = await observer.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync();
        Assert.Equal(attempt.FrozenSnapshotJson,
            (await observer.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync()).FrozenSnapshotJson);
        Assert.Equal(originalBytes, (await observer.CourtDocumentSourceContents.AsNoTracking().SingleAsync()).Bytes);
        var document = await observer.ClientCaseDocuments.AsNoTracking().SingleAsync(row => row.SourceType == "sudrf");
        Assert.Equal(snapshot.ActorId, document.UploadedByUserId);
        Assert.Equal(snapshot.ActorName, document.UploadedByName);
        Assert.Equal(snapshot.FileName, document.OriginalFileName);
        Assert.Equal(snapshot.ContentSha256, Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant());
        Assert.Equal(tasksBefore, JsonSerializer.Serialize(await observer.TaskJournalItems.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()));
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(1, f.Mirror.PutCount);
        Assert.Null(f.Db.Database.CurrentTransaction);
        Assert.Equal(before.ClientCaseDocumentId, after.ClientCaseDocumentId);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DeferredCourtFreshCheckLinksLegacyWithoutReplacingItsFileOrAuthor(bool postgres, bool publishFirst)
    {
        await using var f = await CreateFixtureAsync(postgres, 2, autoPublication: true);
        var source = CourtDeadlineSource(f.Case.CourtUrl);
        await GenerateCourtAsync(f, source);
        var attempt = await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync();
        var originalBytes = (await f.Db.CourtDocumentSourceContents.AsNoTracking().SingleAsync()).Bytes;
        await f.Db.CourtDocumentSources.ExecuteUpdateAsync(set => set.SetProperty(row => row.AcceptedControlsJson, (string?)null));
        await f.CopyPendingAsync();
        f.Db.ChangeTracker.Clear();
        if (publishFirst)
        {
            Assert.Equal(1, (await f.RunCourtQueueAsync()).Succeeded);
            Assert.Null((await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).ClientCaseDocumentId);
        }
        await GenerateCourtAsync(f, source);
        var document = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.Equal(document.Id, (await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).ClientCaseDocumentId);
        Assert.NotNull((await f.Db.CourtDocumentSources.AsNoTracking().SingleAsync()).AcceptedControlsJson);
        Assert.Equal(attempt.ActorId, document.UploadedByUserId);
        Assert.Equal(attempt.ActorName, document.UploadedByName);
        Assert.Equal(attempt.FrozenSnapshotJson, (await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync()).FrozenSnapshotJson);
        Assert.Equal(originalBytes, (await f.Db.CourtDocumentSourceContents.AsNoTracking().SingleAsync()).Bytes);
        var count = await f.Db.ObjectAuditEvents.CountAsync(row => row.ObjectType == "client-case-document");
        await GenerateCourtAsync(f, source);
        Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Single(await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().ToArrayAsync());
        Assert.Equal(count, await f.Db.ObjectAuditEvents.CountAsync(row => row.ObjectType == "client-case-document"));
        Assert.Equal(1, f.Local.PutCount);
        Assert.Equal(1, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DeferredCourtPublicationLinksOnlyAcceptedIds(bool postgres, bool queue)
    {
        await using var f = await CreateFixtureAsync(postgres, 2, autoPublication: true);
        await GenerateCourtAsync(f, CourtDeadlineSource(f.Case.CourtUrl));
        var accepted = await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync();
        Assert.Equal("cassation", accepted.DeadlineType);
        await f.CopyPendingAsync();
        var added = new ClientCaseProceduralDeadline
        {
            ClientCaseId = accepted.ClientCaseId,
            SourceDocumentKey = accepted.SourceDocumentKey,
            SourceType = accepted.SourceType,
            SourceUrl = accepted.SourceUrl,
            Status = accepted.Status,
            Title = "Поздняя строка с тем же ключом",
            DeadlineType = "appeal",
            DueAt = accepted.DueAt,
        };
        f.Db.ClientCaseProceduralDeadlines.Add(added);
        await f.Db.SaveChangesAsync();
        var attempt = await f.Db.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync();
        var snapshot = JsonSerializer.Deserialize<GeneratedCourtDocumentSnapshot>(attempt.FrozenSnapshotJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var receipt = new CourtDocumentSourceReceipt(snapshot,
            new ManagedCaseDocumentGeneration("court", attempt.RequestSha256, attempt.FrozenSnapshotJson));
        if (queue) Assert.Equal(1, (await f.RunCourtQueueAsync()).Succeeded);
        else Assert.Equal("uploaded", (await f.PublishCourt(receipt)).Status);
        var document = await f.Db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.Equal(document.Id, (await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync(row => row.Id == accepted.Id)).ClientCaseDocumentId);
        Assert.Null((await f.Db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync(row => row.Id == added.Id)).ClientCaseDocumentId);
    }
}
