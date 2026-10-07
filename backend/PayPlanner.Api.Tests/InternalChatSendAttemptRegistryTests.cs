using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class InternalChatControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatCaptureFreezesWholeRequestWithoutPublishingMessageAndSurvivesNewContext(bool postgres)
    {
        await using var f = await Fixture.CreateMigratedAsync(postgres);
        var author = await f.AddUserAsync("capture-author@example.test", "Автор");
        var colleague = await f.AddUserAsync("capture-colleague@example.test", "Коллега");
        var thread = await CaptureChannelAsync(f, author, colleague);
        var before = await f.Db.InternalChatThreads.AsNoTracking().SingleAsync(item => item.Id == thread.Id);
        var request = CaptureSnapshot(thread.Id, author.Id) with { MentionUserIds = [colleague.Id] };
        var registry = new InternalChatSendAttemptRegistry(f.Db);
        var captured = await registry.CaptureAsync(request, default);
        Assert.Equal("recorded", captured.Status);
        Assert.False(captured.IsReplay);
        Assert.NotNull(captured.AttemptId);
        Assert.NotNull(captured.RequestSha256);
        Assert.Empty(f.Db.ChangeTracker.Entries<InternalChatSendAttempt>());
        Assert.Empty(f.Db.ChangeTracker.Entries<InternalChatSendAttemptFile>());
        var replay = await registry.CaptureAsync(request, default);
        Assert.True(replay.IsReplay);
        Assert.Equal(captured.AttemptId, replay.AttemptId);
        await using var fresh = CaptureFreshDb(f);
        var stored = await new InternalChatSendAttemptRegistry(fresh).ReadOwnedAsync(thread.Id, author.Id, request.ClientRequestId, default);
        Assert.NotNull(stored);
        Assert.Equal(captured.AttemptId, stored.Id);
        Assert.Equal(InternalChatSendProof.HashFrozen(request, stored.Id, stored.CreatedAt), captured.RequestSha256);
        Assert.Equal("Исходное сообщение 📎", stored.Text);
        Assert.Null(stored.AcceptedAt);
        Assert.Null(stored.PublishedMessageId);
        Assert.Equal(2, stored.FileCount);
        Assert.Equal(6, stored.TotalSizeBytes);
        Assert.All(stored.Files, file => { Assert.Null(file.StorageObjectId); Assert.Null(file.StorageGeneration); });
        Assert.Equal(colleague.Id, Assert.Single(stored.Mentions).UserId);
        request.Files[0] = request.Files[0] with { FileName = "changed.txt" };
        request.MentionUserIds[0] = author.Id;
        Assert.Equal("first.txt", stored.Files.Single(file => file.Index == 0).FileName);
        Assert.Single(await fresh.InternalChatSendAttempts.ToArrayAsync());
        Assert.Empty(await fresh.InternalChatMessages.ToArrayAsync());
        Assert.Empty(await fresh.InternalChatAttachments.ToArrayAsync());
        Assert.Empty(await fresh.StorageObjects.ToArrayAsync());
        Assert.Empty(await fresh.StorageOperations.ToArrayAsync());
        Assert.Empty(await fresh.StorageCapacityReservations.ToArrayAsync());
        var after = await fresh.InternalChatThreads.AsNoTracking().SingleAsync(item => item.Id == thread.Id);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.LastMessageAt, after.LastMessageAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatCaptureRejectsChangedRequestButKeepsIndependentActorsKeysAndThreads(bool postgres)
    {
        await using var f = await Fixture.CreateMigratedAsync(postgres);
        var author = await f.AddUserAsync("capture-identity-author@example.test", "Автор");
        var colleague = await f.AddUserAsync("capture-identity-colleague@example.test", "Коллега");
        var thread = await CaptureChannelAsync(f, author, colleague);
        var otherThread = await CaptureChannelAsync(f, author, colleague);
        var request = CaptureSnapshot(thread.Id, author.Id) with { MentionUserIds = [colleague.Id] };
        var registry = new InternalChatSendAttemptRegistry(f.Db);
        var captured = await registry.CaptureAsync(request, default);
        Assert.Equal("recorded", captured.Status);
        var mutations = new[]
        {
            request with { Text = "Другой текст" },
            request with { ReplyToMessageId = 123456 },
            request with { MentionUserIds = [] },
            request with { Files = request.Files.Reverse().ToArray() },
            request with { Files = [request.Files[0]] },
            request with { Files = [request.Files[0] with { Sha256 = CaptureHash([3, 2, 1]) }, request.Files[1]] },
            request with { Files = [request.Files[0] with { FileName = "other.txt" }, request.Files[1]] },
            request with { Files = [request.Files[0] with { ContentType = "application/pdf" }, request.Files[1]] },
            request with { Files = [request.Files[0] with { SizeBytes = 4 }, request.Files[1]] }
        };
        foreach (var changed in mutations) Assert.Equal("conflict", (await registry.CaptureAsync(changed, default)).Status);
        Assert.Single(await f.Db.InternalChatSendAttempts.ToArrayAsync());
        foreach (var independent in new[]
        {
            request with { ActorId = colleague.Id },
            request with { ClientRequestId = Guid.NewGuid() },
            request with { ThreadId = otherThread.Id }
        })
        {
            var result = await registry.CaptureAsync(independent, default);
            Assert.Equal("recorded", result.Status);
            Assert.NotEqual(captured.AttemptId, result.AttemptId);
        }
        Assert.Equal(4, await f.Db.InternalChatSendAttempts.CountAsync());
        Assert.Empty(await f.Db.InternalChatMessages.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatCaptureChecksFreshAuthorityReplyMentionsAndNeverAdoptsLegacySend(bool postgres)
    {
        await using var f = await Fixture.CreateMigratedAsync(postgres);
        var author = await f.AddUserAsync("capture-scope-author@example.test", "Автор");
        var colleague = await f.AddUserAsync("capture-scope-colleague@example.test", "Коллега");
        var outsider = await f.AddUserAsync("capture-scope-outsider@example.test", "Посторонний");
        var thread = await CaptureChannelAsync(f, author, colleague);
        var other = await CaptureChannelAsync(f, author);
        var foreignReply = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
            (await f.Controller(author).SendMessage(other.Id, new SendInternalChatMessageRequest { Text = "Ответ в другом чате" }, default)).Result).Value);
        var request = CaptureSnapshot(thread.Id, author.Id);
        var registry = new InternalChatSendAttemptRegistry(f.Db);
        Assert.Equal("forbidden", (await registry.CaptureAsync(request with { ActorId = outsider.Id }, default)).Status);
        Assert.Equal("conflict", (await registry.CaptureAsync(request with { ReplyToMessageId = foreignReply.Id }, default)).Status);
        Assert.Equal("conflict", (await registry.CaptureAsync(request with { MentionUserIds = [outsider.Id] }, default)).Status);
        var legacy = await f.Controller(author).SendMessage(thread.Id,
            new SendInternalChatMessageRequest { Text = "Существующее сообщение", ClientRequestId = request.ClientRequestId }, default);
        Assert.IsType<CreatedAtActionResult>(legacy.Result);
        Assert.Equal("legacy", (await registry.CaptureAsync(request, default)).Status);
        request = request with { ClientRequestId = Guid.NewGuid() };
        Assert.Equal("recorded", (await registry.CaptureAsync(request, default)).Status);
        Assert.Null(await registry.ReadOwnedAsync(thread.Id, outsider.Id, request.ClientRequestId, default));
        var member = await f.Db.InternalChatMembers.SingleAsync(item => item.ThreadId == thread.Id && item.UserId == author.Id);
        member.IsActive = false; await f.Db.SaveChangesAsync();
        Assert.Equal("forbidden", (await registry.CaptureAsync(request, default)).Status);
        Assert.Null(await registry.ReadOwnedAsync(thread.Id, author.Id, request.ClientRequestId, default));
        member.IsActive = true; author.IsActive = false; await f.Db.SaveChangesAsync();
        Assert.Equal("forbidden", (await registry.CaptureAsync(request, default)).Status);
        Assert.Null(await registry.ReadOwnedAsync(thread.Id, author.Id, request.ClientRequestId, default));
        Assert.Single(await f.Db.InternalChatSendAttempts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatCaptureRejectsCorruptFrozenRowsWithoutProviderOrMessageWork(bool postgres)
    {
        await using var f = await Fixture.CreateMigratedAsync(postgres);
        var author = await f.AddUserAsync("capture-corrupt-author@example.test", "Автор");
        var colleague = await f.AddUserAsync("capture-corrupt-colleague@example.test", "Коллега");
        var thread = await CaptureChannelAsync(f, author, colleague);
        var registry = new InternalChatSendAttemptRegistry(f.Db);
        foreach (var corruption in new[] { "text", "fileHash", "total", "fileCount", "mention", "createdAt" })
        {
            var request = CaptureSnapshot(thread.Id, author.Id) with { MentionUserIds = [colleague.Id] };
            var result = await registry.CaptureAsync(request, default);
            Assert.Equal("recorded", result.Status);
            var attempt = await f.Db.InternalChatSendAttempts.Include(item => item.Files).Include(item => item.Mentions)
                .AsSplitQuery().SingleAsync(item => item.Id == result.AttemptId);
            switch (corruption)
            {
                case "text": attempt.Text = "Повреждённое содержание"; break;
                case "fileHash": attempt.Files.First().Sha256 = new string('0', 64); break;
                case "total": attempt.TotalSizeBytes++; break;
                case "fileCount": attempt.FileCount--; break;
                case "mention": f.Db.InternalChatSendAttemptMentions.Remove(Assert.Single(attempt.Mentions)); break;
                case "createdAt": attempt.CreatedAt = attempt.CreatedAt.AddSeconds(1); break;
            }
            await f.Db.SaveChangesAsync();
            Assert.Null(await registry.ReadOwnedAsync(thread.Id, author.Id, request.ClientRequestId, default));
            Assert.Equal("conflict", (await registry.CaptureAsync(request, default)).Status);
            f.Db.ChangeTracker.Clear();
        }
        Assert.Empty(await f.Db.InternalChatMessages.ToArrayAsync());
        Assert.Empty(await f.Db.StorageObjects.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatCaptureDoesNotFlushPendingCallerChangesAndFailureCannotResurrectAttempt(bool postgres)
    {
        var interceptor = new RejectChatCaptureSave();
        await using var f = await Fixture.CreateMigratedAsync(postgres, false, interceptor);
        var author = await f.AddUserAsync("capture-rollback-author@example.test", "Автор");
        var thread = await CaptureChannelAsync(f, author);
        var request = CaptureSnapshot(thread.Id, author.Id);
        var registry = new InternalChatSendAttemptRegistry(f.Db);
        author.FullName = "Несохранённые изменения";
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.CaptureAsync(request, default));
        Assert.True(f.Db.Entry(author).Property(item => item.FullName).IsModified);
        Assert.Empty(await f.Db.InternalChatSendAttempts.AsNoTracking().ToArrayAsync());
        author.FullName = "Автор";
        f.Db.Entry(author).State = EntityState.Unchanged;
        interceptor.FailNext = true;
        await Assert.ThrowsAsync<IOException>(() => registry.CaptureAsync(request, default));
        Assert.Empty(f.Db.ChangeTracker.Entries<InternalChatSendAttempt>());
        Assert.Empty(f.Db.ChangeTracker.Entries<InternalChatSendAttemptFile>());
        author.FullName = "После ошибки"; await f.Db.SaveChangesAsync();
        Assert.Empty(await f.Db.InternalChatSendAttempts.AsNoTracking().ToArrayAsync());
        Assert.Equal("recorded", (await registry.CaptureAsync(request, default)).Status);
        Assert.Single(await f.Db.InternalChatSendAttempts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatCaptureConcurrentExactRequestsCreateOneFrozenAggregate(bool postgres)
    {
        await using var f = await Fixture.CreateMigratedAsync(postgres, true);
        var author = await f.AddUserAsync("capture-race-author@example.test", "Автор");
        var thread = await CaptureChannelAsync(f, author);
        var request = CaptureSnapshot(thread.Id, author.Id);
        await using var firstDb = CaptureFreshDb(f, independentSqlite: true);
        await using var secondDb = CaptureFreshDb(f, independentSqlite: true);
        var results = await Task.WhenAll(
            Task.Run(() => new InternalChatSendAttemptRegistry(firstDb).CaptureAsync(request, default)),
            Task.Run(() => new InternalChatSendAttemptRegistry(secondDb).CaptureAsync(request, default)));
        Assert.All(results, result => Assert.Equal("recorded", result.Status));
        Assert.Equal(results[0].AttemptId, results[1].AttemptId);
        Assert.Single(results, result => result.IsReplay);
        Assert.Single(await f.Db.InternalChatSendAttempts.AsNoTracking().ToArrayAsync());
        Assert.Equal(2, await f.Db.InternalChatSendAttemptFiles.CountAsync());
        Assert.Empty(await f.Db.InternalChatMessages.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatCaptureLostCommitAcknowledgementReplaysOriginalAggregate(bool postgres)
    {
        var fault = new ChatCaptureCommitAckFailure();
        await using var f = await Fixture.CreateMigratedAsync(postgres, false, fault);
        var author = await f.AddUserAsync("capture-ack-author@example.test", "Автор");
        var thread = await CaptureChannelAsync(f, author);
        var request = CaptureSnapshot(thread.Id, author.Id);
        fault.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => new InternalChatSendAttemptRegistry(f.Db).CaptureAsync(request, default));
        Assert.True(fault.Triggered);
        Assert.Empty(f.Db.ChangeTracker.Entries<InternalChatSendAttempt>());
        await using var fresh = CaptureFreshDb(f);
        var registry = new InternalChatSendAttemptRegistry(fresh);
        var replay = await registry.CaptureAsync(request, default);
        Assert.Equal("recorded", replay.Status);
        Assert.True(replay.IsReplay);
        var stored = Assert.Single(await fresh.InternalChatSendAttempts.AsNoTracking().ToArrayAsync());
        Assert.Equal(stored.Id, replay.AttemptId);
        Assert.Equal(2, await fresh.InternalChatSendAttemptFiles.CountAsync());
        Assert.Null(stored.AcceptedAt);
        Assert.Empty(await fresh.InternalChatMessages.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatCaptureTaskMembershipChangesAndExplicitGuestScopeRemainFresh(bool postgres)
    {
        await using var f = await Fixture.CreateMigratedAsync(postgres);
        var author = await f.AddUserAsync("capture-task-author@example.test", "Автор");
        var assignee = await f.AddUserAsync("capture-task-assignee@example.test", "Исполнитель");
        var observer = await f.AddUserAsync("capture-task-observer@example.test", "Наблюдатель");
        var task = await f.AddTaskAsync(author.Id, assignee.Id, observer.Id);
        var thread = new InternalChatThread
        {
            Kind = InternalChatThreadKinds.Task,
            Name = "Чат задачи",
            TaskJournalItem = task,
            Members = new[] { author, assignee, observer }.Select(user => new InternalChatMember { UserId = user.Id }).ToList()
        };
        f.Db.InternalChatThreads.Add(thread); await f.Db.SaveChangesAsync();
        var request = CaptureSnapshot(thread.Id, observer.Id);
        var registry = new InternalChatSendAttemptRegistry(f.Db);
        Assert.Equal("recorded", (await registry.CaptureAsync(request, default)).Status);
        f.Db.Remove(Assert.Single(task.Participants)); await f.Db.SaveChangesAsync();
        Assert.Equal("forbidden", (await registry.CaptureAsync(request, default)).Status);
        Assert.Null(await registry.ReadOwnedAsync(thread.Id, observer.Id, request.ClientRequestId, default));
        thread.Members.Single(member => member.UserId == observer.Id).Role = "guest"; await f.Db.SaveChangesAsync();
        var replay = await registry.CaptureAsync(request, default);
        Assert.Equal("recorded", replay.Status);
        Assert.True(replay.IsReplay);
        observer.IsApproved = false; await f.Db.SaveChangesAsync();
        Assert.Equal("forbidden", (await registry.CaptureAsync(request, default)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatCaptureRollsBackAuthorityLostAfterSaveBeforeCommit(bool postgres)
    {
        var fault = new ChatCaptureAuthorityFailure();
        await using var f = await Fixture.CreateMigratedAsync(postgres, false, fault);
        var author = await f.AddUserAsync("capture-authority-author@example.test", "Автор");
        var thread = await CaptureChannelAsync(f, author);
        var request = CaptureSnapshot(thread.Id, author.Id);
        fault.Armed = true;
        var registry = new InternalChatSendAttemptRegistry(f.Db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.CaptureAsync(request, default));
        Assert.True(fault.Triggered);
        Assert.Empty(await f.Db.InternalChatSendAttempts.AsNoTracking().ToArrayAsync());
        Assert.Empty(await f.Db.InternalChatSendAttemptFiles.AsNoTracking().ToArrayAsync());
        Assert.Empty(f.Db.ChangeTracker.Entries<InternalChatSendAttempt>());
        Assert.True(await f.Db.Users.AsNoTracking().Where(user => user.Id == author.Id).Select(user => user.IsActive).SingleAsync());
        Assert.Equal("recorded", (await registry.CaptureAsync(request, default)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatCaptureUpgradePreservesExistingMessagesAndRefusesDestructiveDowngrade(bool postgres)
    {
        await using var f = await Fixture.CreateMigratedAsync(postgres);
        var author = await f.AddUserAsync("capture-migration-author@example.test", "Автор");
        var thread = await CaptureChannelAsync(f, author);
        var legacy = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
            (await f.Controller(author).SendMessage(thread.Id, new SendInternalChatMessageRequest { Text = "До обновления" }, default)).Result).Value);
        var migrations = (await f.Db.Database.GetAppliedMigrationsAsync()).ToArray();
        var index = Array.FindIndex(migrations, name => name.Contains("AddInternalChatSendAttempts", StringComparison.Ordinal));
        Assert.True(index > 0);
        var migrator = f.Db.GetService<IMigrator>();
        await migrator.MigrateAsync(migrations[index - 1]);
        await migrator.MigrateAsync();
        Assert.Equal("До обновления", (await f.Db.InternalChatMessages.AsNoTracking().SingleAsync(item => item.Id == legacy.Id)).Text);
        var request = CaptureSnapshot(thread.Id, author.Id);
        var registry = new InternalChatSendAttemptRegistry(f.Db);
        var captured = await registry.CaptureAsync(request, default);
        Assert.Equal("recorded", captured.Status);
        await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(migrations[index - 1]));
        Assert.Contains(migrations[index], await f.Db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(captured.AttemptId, (await registry.ReadOwnedAsync(thread.Id, author.Id, request.ClientRequestId, default))!.Id);
        Assert.Equal("До обновления", (await f.Db.InternalChatMessages.AsNoTracking().SingleAsync(item => item.Id == legacy.Id)).Text);
        // SQLite may commit a later table-rebuild downgrade before the chat guard rejects its own downgrade.
        await migrator.MigrateAsync();
        Assert.Equal(captured.AttemptId, (await registry.ReadOwnedAsync(thread.Id, author.Id, request.ClientRequestId, default))!.Id);
        Assert.Equal("До обновления", (await f.Db.InternalChatMessages.AsNoTracking().SingleAsync(item => item.Id == legacy.Id)).Text);
        Assert.Empty(await f.Db.Database.GetPendingMigrationsAsync());
        Assert.False(f.Db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void ChatCaptureProofUsesFramingOrderingAndExactUnicodeContent()
    {
        var original = CaptureSnapshot(1, 2);
        var sha = InternalChatSendProof.Hash(original);
        Assert.NotEqual(sha, InternalChatSendProof.Hash(original with { Text = "Исходное сообщение 📁" }));
        Assert.NotEqual(sha, InternalChatSendProof.Hash(original with { Files = original.Files.Reverse().ToArray() }));
        Assert.False(InternalChatSendProof.IsValid(original with { MentionUserIds = [3, 3] }));
        Assert.False(InternalChatSendProof.IsValid(original with { MentionUserIds = [4, 3] }));
        Assert.False(InternalChatSendProof.IsValid(original with { Files = [original.Files[0] with { FileName = "../outside.txt" }] }));
        Assert.False(InternalChatSendProof.IsValid(original with { Files = [original.Files[0] with { SizeBytes = InternalChatSendProof.MaximumFileSize + 1 }] }));
        Assert.False(InternalChatSendProof.IsValid(original with { Files = Enumerable.Repeat(original.Files[0], 11).ToArray() }));
        Assert.NotEqual(InternalChatSendProof.Hash(original with { Text = "é" }), InternalChatSendProof.Hash(original with { Text = "e\u0301" }));
        Assert.False(InternalChatSendProof.IsValid(original with { Text = "\ud800" }));
    }

    private static string CaptureHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static InternalChatSendSnapshot CaptureSnapshot(int threadId, int actorId) => new(threadId, actorId,
        Guid.NewGuid(), "Исходное сообщение 📎", null, [],
        [new("first.txt", "text/plain", 3, CaptureHash([1, 2, 3])), new("second.txt", "text/plain", 3, CaptureHash([4, 5, 6]))]);

    private static async Task<InternalChatThreadDto> CaptureChannelAsync(Fixture f, User author, params User[] others)
        => Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(
            (await f.Controller(author).CreateChannel(new CreateInternalChatChannelRequest
            { Name = "Реестр отправки", MemberUserIds = others.Select(user => user.Id).ToList() }, default)).Result).Value);

    private static PaymentContext CaptureFreshDb(Fixture f, bool independentSqlite = false)
        => f.Db.Database.IsNpgsql()
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(f.Db.Database.GetConnectionString()).Options)
            : new PaymentContext((independentSqlite
                ? new DbContextOptionsBuilder<PaymentContext>().UseSqlite(f.Db.Database.GetConnectionString())
                : new DbContextOptionsBuilder<PaymentContext>().UseSqlite(f.Db.Database.GetDbConnection()))
                .UsePayPlannerSqliteMigrations().Options);

    private sealed class RejectChatCaptureSave : SaveChangesInterceptor
    {
        public bool FailNext { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (FailNext && eventData.Context!.ChangeTracker.Entries<InternalChatSendAttempt>().Any(item => item.State == EntityState.Added))
            {
                FailNext = false;
                throw new IOException("Owned synthetic chat capture failure.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ChatCaptureCommitAckFailure : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool Triggered { get; private set; }
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
            TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Armed && !Triggered && eventData.Context!.ChangeTracker.Entries<InternalChatSendAttempt>().Any())
            { Triggered = true; throw new IOException("Owned lost chat capture commit acknowledgement."); }
            return Task.CompletedTask;
        }
    }

    private sealed class ChatCaptureAuthorityFailure : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Triggered { get; private set; }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (Armed && !Triggered && eventData.Context!.ChangeTracker.Entries<InternalChatSendAttempt>().Any())
            {
                Triggered = true;
                var db = Assert.IsAssignableFrom<PaymentContext>(eventData.Context);
                var actorId = db.ChangeTracker.Entries<InternalChatSendAttempt>().Single().Entity.ActorId;
                await db.Users.Where(user => user.Id == actorId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsActive, false), cancellationToken);
            }
            return result;
        }
    }
}
