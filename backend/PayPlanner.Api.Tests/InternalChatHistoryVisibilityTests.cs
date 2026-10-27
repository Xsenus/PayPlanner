using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class InternalChatControllerTests
{
    [Fact]
    public async Task PersonalHistoryHide_RestoreAndReplyPreview_AffectOnlyTheOwner()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.AddUserAsync("history-owner@example.test", "Владелец");
        var colleague = await fixture.AddUserAsync("history-colleague@example.test", "Коллега");
        var outsider = await fixture.AddUserAsync("history-outsider@example.test", "Посторонний");
        var created = await fixture.Controller(owner).CreateChannel(new CreateInternalChatChannelRequest
        { Name = "Личная история", MemberUserIds = [colleague.Id] }, CancellationToken.None);
        var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value);
        var old = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(colleague).SendMessage(thread.Id,
                new SendInternalChatMessageRequest { Text = "Старая история" }, CancellationToken.None)).Result).Value);
        var initial = State(await fixture.Controller(owner).GetHistoryVisibility(thread.Id, CancellationToken.None));
        Assert.Null(initial.HiddenThroughMessageId);
        Assert.Equal(old.Id, initial.LatestMessageId);
        Assert.IsType<ForbidResult>((await fixture.Controller(outsider).GetHistoryVisibility(thread.Id, CancellationToken.None)).Result);

        var hidden = State(await fixture.Controller(owner).SetHistoryVisibility(thread.Id,
            Body(old.Id, initial.Revision), CancellationToken.None));
        Assert.Equal(old.Id, hidden.HiddenThroughMessageId);
        Assert.Equal(initial.Revision + 1, hidden.Revision);
        Assert.Empty((await Page(fixture.Controller(owner), thread.Id)).Items);
        Assert.Single((await Page(fixture.Controller(colleague), thread.Id)).Items);
        var ownerThreads = Assert.IsAssignableFrom<IReadOnlyList<InternalChatThreadDto>>(
            Assert.IsType<OkObjectResult>((await fixture.Controller(owner).GetThreads(CancellationToken.None)).Result).Value);
        Assert.Equal(0, ownerThreads.Single(t => t.Id == thread.Id).UnreadCount);
        Assert.Null(ownerThreads.Single(t => t.Id == thread.Id).LastMessagePreview);

        var newer = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(colleague).SendMessage(thread.Id,
                new SendInternalChatMessageRequest { Text = "Новое сообщение", ReplyToMessageId = old.Id }, CancellationToken.None)).Result).Value);
        var ownerPage = await Page(fixture.Controller(owner), thread.Id);
        var visible = Assert.Single(ownerPage.Items);
        Assert.Equal(newer.Id, visible.Id);
        Assert.Null(visible.ReplyPreview);
        Assert.Null(visible.ReplyAuthorName);
        Assert.Equal("Старая история", (await Page(fixture.Controller(colleague), thread.Id)).Items.Last().ReplyPreview);

        var replay = State(await fixture.Controller(owner).SetHistoryVisibility(thread.Id,
            Body(old.Id, initial.Revision), CancellationToken.None));
        Assert.Equal(hidden.Revision, replay.Revision);
        Assert.Equal(old.Id, replay.HiddenThroughMessageId);
        Assert.Equal(newer.Id, replay.LatestMessageId);
        Assert.IsType<ConflictObjectResult>((await fixture.Controller(owner).SetHistoryVisibility(thread.Id,
            Body(null, initial.Revision), CancellationToken.None)).Result);
        var restored = State(await fixture.Controller(owner).SetHistoryVisibility(thread.Id,
            Body(null, hidden.Revision), CancellationToken.None));
        Assert.Null(restored.HiddenThroughMessageId);
        Assert.Equal(2, (await Page(fixture.Controller(owner), thread.Id)).Items.Count);
        Assert.Equal(2, await fixture.Db.InternalChatMessages.CountAsync());
    }

    [Fact]
    public async Task PersonalHistory_RejectsForeignBoundaryAndMalformedOrUnauthorizedUpdates()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.AddUserAsync("history-validation-owner@example.test", "Владелец");
        var outsider = await fixture.AddUserAsync("history-validation-outsider@example.test", "Посторонний");
        var first = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(owner).CreateChannel(new CreateInternalChatChannelRequest { Name = "Первый" }, default)).Result).Value);
        var second = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(owner).CreateChannel(new CreateInternalChatChannelRequest { Name = "Второй" }, default)).Result).Value);
        var other = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(owner).SendMessage(second.Id, new SendInternalChatMessageRequest { Text = "Чужая граница" }, default)).Result).Value);
        Assert.IsType<ForbidResult>((await fixture.Controller(outsider).SetHistoryVisibility(first.Id, Body(0, 0), default)).Result);
        Assert.IsType<BadRequestObjectResult>((await fixture.Controller(owner).SetHistoryVisibility(first.Id, Body(other.Id, 0), default)).Result);
        var duplicate = JsonDocument.Parse("{\"hiddenThroughMessageId\":0,\"hiddenThroughMessageId\":null,\"expectedRevision\":0}").RootElement.Clone();
        Assert.IsType<BadRequestObjectResult>((await fixture.Controller(owner).SetHistoryVisibility(first.Id, duplicate, default)).Result);
        Assert.Null(State(await fixture.Controller(owner).GetHistoryVisibility(first.Id, default)).HiddenThroughMessageId);
    }

    [Fact]
    public async Task PersonalHistory_HidingDoesNotMarkMessagesReadAndRestorationPreservesUnread()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.AddUserAsync("history-read-owner@example.test", "Владелец");
        var colleague = await fixture.AddUserAsync("history-read-colleague@example.test", "Коллега");
        var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(owner).CreateChannel(new CreateInternalChatChannelRequest { Name = "Прочтение", MemberUserIds = [colleague.Id] }, default)).Result).Value);
        var sent = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(colleague).SendMessage(thread.Id, new SendInternalChatMessageRequest { Text = "Не прочитано" }, default)).Result).Value);
        var initial = State(await fixture.Controller(owner).GetHistoryVisibility(thread.Id, default));
        var hidden = State(await fixture.Controller(owner).SetHistoryVisibility(thread.Id, Body(sent.Id, initial.Revision), default));
        fixture.Db.ChangeTracker.Clear();
        Assert.IsType<NoContentResult>(await fixture.Controller(owner).MarkRead(thread.Id, new MarkInternalChatReadRequest(), default));
        Assert.Null((await fixture.Db.InternalChatMembers.AsNoTracking().SingleAsync(m => m.ThreadId == thread.Id && m.UserId == owner.Id)).LastReadMessageId);
        State(await fixture.Controller(owner).SetHistoryVisibility(thread.Id, Body(null, hidden.Revision), default));
        var threads = Assert.IsAssignableFrom<IReadOnlyList<InternalChatThreadDto>>(
            Assert.IsType<OkObjectResult>((await fixture.Controller(owner).GetThreads(default)).Result).Value);
        Assert.Equal(1, threads.Single(t => t.Id == thread.Id).UnreadCount);
    }

    [Fact]
    public async Task PersonalHistory_FiltersBeforeSearchAndPaginationWithoutSkippingVisibleMessages()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.AddUserAsync("history-pages-owner@example.test", "Владелец");
        var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(owner).CreateChannel(new CreateInternalChatChannelRequest { Name = "Страницы" }, default)).Result).Value);
        var rows = Enumerable.Range(1, 60).Select(i => new InternalChatMessage
        {
            ThreadId = thread.Id,
            AuthorUserId = owner.Id,
            Text = i <= 30 ? "Скрытый текст" : $"Видимое {i}",
            CreatedAt = DateTime.UtcNow
        }).ToArray();
        fixture.Db.InternalChatMessages.AddRange(rows);
        await fixture.Db.SaveChangesAsync();
        var initial = State(await fixture.Controller(owner).GetHistoryVisibility(thread.Id, default));
        State(await fixture.Controller(owner).SetHistoryVisibility(thread.Id, Body(rows[29].Id, initial.Revision), default));
        var controller = fixture.Controller(owner);
        var first = Assert.IsType<InternalChatMessagePageDto>(Assert.IsType<OkObjectResult>(
            (await controller.GetMessages(thread.Id, null, null, null, null, null, false, 10, default)).Result).Value);
        Assert.Equal(rows.Skip(50).Select(m => m.Id), first.Items.Select(m => m.Id));
        Assert.True(first.HasMore);
        var next = Assert.IsType<InternalChatMessagePageDto>(Assert.IsType<OkObjectResult>(
            (await controller.GetMessages(thread.Id, first.NextBeforeId, null, null, null, null, false, 50, default)).Result).Value);
        Assert.Equal(rows.Skip(30).Take(20).Select(m => m.Id), next.Items.Select(m => m.Id));
        Assert.False(next.HasMore);
        var search = Assert.IsType<InternalChatMessagePageDto>(Assert.IsType<OkObjectResult>(
            (await controller.GetMessages(thread.Id, null, "Скрытый", null, null, null, false, 50, default)).Result).Value);
        Assert.Empty(search.Items);
        fixture.Db.ChangeTracker.Clear();
        Assert.IsType<BadRequestObjectResult>(await controller.MarkRead(thread.Id, new MarkInternalChatReadRequest { MessageId = rows[0].Id }, default));
        Assert.Equal(60, await fixture.Db.InternalChatMessages.CountAsync());
    }

    [Fact]
    public async Task PersonalHistory_DirectActionsRejectHiddenMessagesWithoutChangingSharedHistory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.AddUserAsync("history-action-owner@example.test", "Владелец");
        var colleague = await fixture.AddUserAsync("history-action-colleague@example.test", "Коллега");
        var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(owner).CreateChannel(new CreateInternalChatChannelRequest
            { Name = "Действия", MemberUserIds = [colleague.Id] }, default)).Result).Value);
        var sent = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(owner).SendMessage(thread.Id,
                new SendInternalChatMessageRequest { Text = "Общая история" }, default)).Result).Value);
        State(await fixture.Controller(owner).SetHistoryVisibility(thread.Id, Body(sent.Id, 0), default));
        fixture.Db.ChangeTracker.Clear();
        var controller = fixture.Controller(owner);
        Assert.IsType<NotFoundResult>((await controller.EditMessage(sent.Id, new EditInternalChatMessageRequest { Text = "Изменено" }, default)).Result);
        Assert.IsType<NotFoundResult>(await controller.DeleteMessage(sent.Id, default));
        Assert.IsType<NotFoundResult>((await controller.SetNoReplyReminder(sent.Id, new SetInternalChatNoReplyReminderRequest { Enabled = true }, default)).Result);
        Assert.IsType<NotFoundResult>(await controller.ToggleReaction(sent.Id, new ToggleInternalChatReactionRequest { Emoji = "👍" }, default));
        Assert.IsType<NotFoundResult>(await controller.TogglePin(sent.Id, new PinInternalChatMessageRequest { Pinned = true }, default));
        Assert.IsType<BadRequestObjectResult>((await controller.SendMessage(thread.Id,
            new SendInternalChatMessageRequest { Text = "Ответ", ReplyToMessageId = sent.Id }, default)).Result);
        fixture.Db.ChangeTracker.Clear();
        var shared = Assert.Single((await Page(fixture.Controller(colleague), thread.Id)).Items);
        Assert.Equal("Общая история", shared.Text);
        Assert.False(shared.IsPinned);
        Assert.Empty(shared.Reactions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersonalHistory_BusyThreadReturnsConflictWithoutChangingMember(bool postgres)
    {
        await using var fixture = await CreateMigratedFixtureAsync(postgres, independentSqliteConnections: true);
        var owner = await fixture.AddUserAsync("history-lock-owner@example.test", "Владелец");
        var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(owner).CreateChannel(new CreateInternalChatChannelRequest { Name = "Блокировка" }, default)).Result).Value);
        await using var blocker = CaptureFreshDb(fixture, independentSqlite: true);
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        Assert.Equal(1, await blocker.InternalChatThreads.Where(item => item.Id == thread.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.UpdatedAt, item => item.UpdatedAt)));
        if (fixture.Db.Database.GetDbConnection() is Microsoft.Data.Sqlite.SqliteConnection sqlite) sqlite.DefaultTimeout = 1;
        var result = await fixture.Controller(owner).SetHistoryVisibility(thread.Id, Body(0, 0), default);
        Assert.IsType<ConflictObjectResult>(result.Result);
        await transaction.RollbackAsync();
        var state = State(await fixture.Controller(owner).GetHistoryVisibility(thread.Id, default));
        Assert.Null(state.HiddenThroughMessageId);
        Assert.Equal(0, state.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersonalHistory_ConcurrentPlainSendRemainsVisibleAfterCapturedBoundary(bool postgres)
    {
        await using var fixture = await CreateMigratedFixtureAsync(postgres, independentSqliteConnections: true);
        var owner = await fixture.AddUserAsync("history-race-owner@example.test", "Владелец");
        var colleague = await fixture.AddUserAsync("history-race-colleague@example.test", "Коллега");
        var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(owner).CreateChannel(new CreateInternalChatChannelRequest { Name = "Гонка", MemberUserIds = [colleague.Id] }, default)).Result).Value);
        var old = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(colleague).SendMessage(thread.Id, new SendInternalChatMessageRequest { Text = "Старое" }, default)).Result).Value);
        var captured = State(await fixture.Controller(owner).GetHistoryVisibility(thread.Id, default));
        Assert.Equal(old.Id, captured.LatestMessageId);
        await using var blocker = CaptureFreshDb(fixture, independentSqlite: true);
        await using var writer = CaptureFreshDb(fixture, independentSqlite: true);
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        Assert.Equal(1, await blocker.InternalChatThreads.Where(item => item.Id == thread.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.UpdatedAt, item => item.UpdatedAt)));
        var hide = Task.Run(() => fixture.Controller(owner).SetHistoryVisibility(thread.Id, Body(captured.LatestMessageId, captured.Revision), default));
        var send = Task.Run(() => fixture.Controller(colleague, db: writer).SendMessage(thread.Id, new SendInternalChatMessageRequest { Text = "Новое" }, default));
        await Task.Delay(200);
        Assert.False(hide.IsCompleted && send.IsCompleted);
        await transaction.CommitAsync();
        State(await hide);
        var published = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>((await send).Result).Value);
        Assert.True(published.Id > old.Id);
        fixture.Db.ChangeTracker.Clear();
        var visible = Assert.Single((await Page(fixture.Controller(owner), thread.Id)).Items);
        Assert.Equal(published.Id, visible.Id);
        Assert.Equal("Новое", visible.Text);
    }

    [Fact]
    public async Task PersonalHistory_ConcurrentTaskLinkAndHideKeepConsistentVisibleHistoryOnPostgreSql()
    {
        await using var fixture = await CreateMigratedFixtureAsync(true, independentSqliteConnections: true);
        var owner = await fixture.AddUserAsync("history-task-race@example.test", "Владелец");
        fixture.Db.RolePermissions.Add(new RolePermission { RoleId = owner.RoleId, Section = RolePermissionCatalog.Tasks, CanView = true, CanCreate = true });
        await fixture.Db.SaveChangesAsync();
        var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(owner).CreateChannel(new CreateInternalChatChannelRequest { Name = "Задача" }, default)).Result).Value);
        var old = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(owner).SendMessage(thread.Id, new SendInternalChatMessageRequest { Text = "Старое" }, default)).Result).Value);
        var captured = State(await fixture.Controller(owner).GetHistoryVisibility(thread.Id, default));
        await using var blocker = CaptureFreshDb(fixture, independentSqlite: true);
        await using var writer = CaptureFreshDb(fixture, independentSqlite: true);
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        Assert.Equal(1, await blocker.InternalChatThreads.Where(item => item.Id == thread.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.UpdatedAt, item => item.UpdatedAt)));
        var hide = fixture.Controller(owner).SetHistoryVisibility(thread.Id, Body(captured.LatestMessageId, captured.Revision), default);
        var task = fixture.Controller(owner, db: writer).CreateTaskFromMessage(old.Id,
            new CreateTaskFromInternalChatMessageRequest { Title = "Проверка задачи", DueAt = DateTime.UtcNow.AddDays(1) }, default);
        await Task.Delay(200);
        Assert.False(hide.IsCompleted && task.IsCompleted);
        await transaction.CommitAsync();
        State(await hide);
        var result = await task;
        fixture.Db.ChangeTracker.Clear();
        var visible = (await Page(fixture.Controller(owner), thread.Id)).Items;
        var taskCount = await fixture.Db.TaskJournalItems.CountAsync();
        if (result.Result is NotFoundResult)
        {
            Assert.Equal(0, taskCount);
            Assert.Empty(visible);
        }
        else
        {
            var link = Assert.IsType<InternalChatMessageDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
            Assert.Equal(1, taskCount);
            Assert.True(link.Id > old.Id);
            Assert.Equal(link.Id, Assert.Single(visible).Id);
        }
        Assert.Equal("Старое", (await fixture.Db.InternalChatMessages.AsNoTracking().SingleAsync(m => m.Id == old.Id)).Text);
    }

    [Fact]
    public async Task PersonalHistory_ConcurrentSystemMessageRemainsVisibleOnPostgreSql()
    {
        await using var fixture = await CreateMigratedFixtureAsync(true, independentSqliteConnections: true);
        var owner = await fixture.AddUserAsync("history-system-race@example.test", "Владелец");
        var item = new TaskJournalItem
        {
            ObjectName = "Объект",
            Title = "Задача",
            AuthorUserId = owner.Id,
            AuthorName = owner.FullName,
            DueAt = DateTime.UtcNow.AddDays(1)
        };
        var thread = new InternalChatThread { Kind = InternalChatThreadKinds.Task, Name = "Задача", TaskJournalItem = item };
        thread.Members.Add(new InternalChatMember { UserId = owner.Id, Role = "owner" });
        thread.Messages.Add(new InternalChatMessage { AuthorUserId = owner.Id, Text = "Старое" });
        fixture.Db.InternalChatThreads.Add(thread);
        await fixture.Db.SaveChangesAsync();
        var oldId = thread.Messages.Single().Id;
        var captured = State(await fixture.Controller(owner).GetHistoryVisibility(thread.Id, default));
        Assert.Equal(oldId, captured.LatestMessageId);
        await using var blocker = CaptureFreshDb(fixture, independentSqlite: true);
        await using var writer = CaptureFreshDb(fixture, independentSqlite: true);
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        Assert.Equal(1, await blocker.InternalChatThreads.Where(item => item.Id == thread.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.UpdatedAt, item => item.UpdatedAt)));
        var controller = new TasksController(writer, DispatchProxy.Create<INotificationService, NotificationProxy>());
        var accessType = typeof(TasksController).GetNestedType("TaskAccess", BindingFlags.NonPublic)!;
        var access = Activator.CreateInstance(accessType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [owner.Id, owner.FullName, false, null, null], null)!;
        var method = typeof(TasksController).GetMethod("AppendTaskChatSystemMessagesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var changes = new List<(int TaskId, IReadOnlyList<string> ChangeLabels)> { (item.Id, new[] { "Статус: выполнено" }) };
        var hide = fixture.Controller(owner).SetHistoryVisibility(thread.Id, Body(captured.LatestMessageId, captured.Revision), default);
        var publish = (Task)method.Invoke(controller, [changes, access, CancellationToken.None])!;
        await Task.Delay(200);
        Assert.False(hide.IsCompleted && publish.IsCompleted);
        await transaction.CommitAsync();
        State(await hide);
        await publish;
        fixture.Db.ChangeTracker.Clear();
        var visible = Assert.Single((await Page(fixture.Controller(owner), thread.Id)).Items);
        Assert.True(visible.Id > oldId);
        Assert.Equal(InternalChatMessageKinds.System, visible.Kind);
        Assert.Contains("Статус: выполнено", visible.Text);
        Assert.Equal(2, await fixture.Db.InternalChatMessages.CountAsync());
    }

    private static JsonElement Body(int? cutoff, long revision)
        => JsonSerializer.SerializeToElement(new { hiddenThroughMessageId = cutoff, expectedRevision = revision });

    private static InternalChatHistoryStateDto State(ActionResult<InternalChatHistoryStateDto> result)
        => Assert.IsType<InternalChatHistoryStateDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static async Task<InternalChatMessagePageDto> Page(InternalChatController controller, int threadId)
        => Assert.IsType<InternalChatMessagePageDto>(Assert.IsType<OkObjectResult>(
            (await controller.GetMessages(threadId, null, null, null, null, null, false, 50, CancellationToken.None)).Result).Value);
}
