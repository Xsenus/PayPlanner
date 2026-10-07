using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class InternalChatControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatSendDiscoveryPagesTiedTimestampsWithinCurrentAuthority(bool postgres)
    {
        await using var f = await Fixture.CreateMigratedAsync(postgres);
        var actor = await f.AddUserAsync("chat-discovery@example.test", "Автор");
        var other = await f.AddUserAsync("chat-discovery-other@example.test", "Другой");
        var thread = await CaptureChannelAsync(f, actor, other);
        var anotherThread = await CaptureChannelAsync(f, actor);
        var created = new DateTime(2026, 10, 3, 1, 0, 0, DateTimeKind.Utc);
        InternalChatSendAttempt Seed(int number, int threadId, int actorId, bool published = false)
        {
            var snapshot = CaptureSnapshot(threadId, actorId);
            var header = new InternalChatSendAttempt
            {
                Id = Guid.Parse($"00000000-0000-0000-0000-{number:D12}"),
                ThreadId = threadId,
                ActorId = actorId,
                ClientRequestId = snapshot.ClientRequestId,
                Text = snapshot.Text,
                CreatedAt = created,
                FileCount = snapshot.Files.Length,
                TotalSizeBytes = snapshot.Files.Sum(file => file.SizeBytes),
                AcceptedAt = published ? created : null,
                PublishedMessageId = published ? 987654 : null,
                Files = snapshot.Files.Select((file, index) => new InternalChatSendAttemptFile
                { Index = index, FileName = file.FileName, ContentType = file.ContentType, SizeBytes = file.SizeBytes, Sha256 = file.Sha256 }).ToList()
            };
            header.RequestSha256 = InternalChatSendProof.HashFrozen(snapshot, header.Id, created);
            f.Db.InternalChatSendAttempts.Add(header);
            return header;
        }
        var expected = Enumerable.Range(1, 8).Select(number => Seed(number, thread.Id, actor.Id)).Reverse().ToArray();
        var foreign = Seed(20, thread.Id, other.Id);
        var otherScope = Seed(21, anotherThread.Id, actor.Id);
        Seed(22, thread.Id, actor.Id, true);
        await f.Db.SaveChangesAsync();
        using var root = new ChatOriginalRoot();
        var environment = new ChatOriginalEnvironment(root.Path);
        var config = ChatPublicationConfiguration(root.Path);
        var settings = ChatPublicationSettings();
        settings.ProductWriteEnabledClasses.Clear();
        var monitor = new ChatOriginalOptions(settings);
        var local = new ChatRecoveryLocal(new LocalStorageProvider(environment, config));
        var providers = new StorageProviderRegistry([local]);
        var policy = new StoragePolicyService(monitor, providers);
        var core = ChatPublicationCore(f.Db, providers, policy, monitor, environment, config);
        var first = await core.Writer.DiscoverAsync(thread.Id, actor.Id, 2, null, default);
        Assert.Equal("pending", first.Status);
        Assert.Equal(expected.Take(2).Select(item => item.Id), first.Items.Select(item => item.AttemptId));
        Assert.Equal(expected[1].Id, first.NextAfterAttemptId);
        // A worker may publish the cursor between pages. Its historical row is
        // still a valid scoped boundary, but disappears from pending results.
        var cursor = first.NextAfterAttemptId;
        await f.Db.InternalChatSendAttempts.Where(item => item.Id == cursor)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.AcceptedAt, created).SetProperty(item => item.PublishedMessageId, 987653));
        var seen = first.Items.Select(item => item.AttemptId).ToList();
        while (cursor.HasValue)
        {
            var page = await core.Writer.DiscoverAsync(thread.Id, actor.Id, 2, cursor, default);
            Assert.Equal("pending", page.Status);
            Assert.InRange(page.Items.Count, 1, 2);
            seen.AddRange(page.Items.Select(item => item.AttemptId));
            cursor = page.NextAfterAttemptId;
        }
        Assert.Equal(expected.Select(item => item.Id), seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        foreach (var invalid in new[] { foreign.Id, otherScope.Id, Guid.NewGuid() })
        {
            var result = await core.Writer.DiscoverAsync(thread.Id, actor.Id, 2, invalid, default);
            Assert.Equal("invalidCursor", result.Status);
            Assert.Empty(result.Items);
        }
        foreach (var limit in new[] { 0, 51 }) Assert.Equal("invalid", (await core.Writer.DiscoverAsync(thread.Id, actor.Id, limit, null, default)).Status);
        Assert.Equal("invalid", (await core.Writer.DiscoverAsync(thread.Id, actor.Id, 2, Guid.Empty, default)).Status);
        await f.Db.InternalChatMembers.Where(item => item.ThreadId == thread.Id && item.UserId == actor.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false));
        var revoked = await core.Writer.DiscoverAsync(thread.Id, actor.Id, 2, null, default);
        Assert.Equal("forbidden", revoked.Status);
        Assert.Empty(revoked.Items);
        Assert.Equal(0, local.ReadCalls);
        Assert.Equal(0, local.PutCalls);
        Assert.Empty(await f.Db.InternalChatMessages.ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.ToArrayAsync());
    }
}
