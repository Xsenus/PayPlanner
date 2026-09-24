using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class InternalChatControllerTests
{
    [Fact]
    public async Task OpenTaskChat_AllowsOnlyTaskParticipants_AndSynchronizesMembers()
    {
        await using var fixture = await Fixture.CreateAsync();
        var author = await fixture.AddUserAsync("author@example.test", "Автор");
        var assignee = await fixture.AddUserAsync("assignee@example.test", "Исполнитель");
        var observer = await fixture.AddUserAsync("observer@example.test", "Наблюдатель");
        var outsider = await fixture.AddUserAsync("outsider@example.test", "Посторонний");
        var task = await fixture.AddTaskAsync(author.Id, assignee.Id, observer.Id);

        var outsiderResult = await fixture.Controller(outsider).OpenTaskChat(task.Id, CancellationToken.None);
        Assert.IsType<ForbidResult>(outsiderResult.Result);

        var result = await fixture.Controller(assignee).OpenTaskChat(task.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<InternalChatThreadDto>(ok.Value);
        Assert.Equal(InternalChatThreadKinds.Task, dto.Kind);
        Assert.Equal(task.Id, dto.TaskJournalItemId);

        var members = await fixture.Db.InternalChatMembers.Where(item => item.ThreadId == dto.Id && item.IsActive).Select(item => item.UserId).ToListAsync();
        Assert.Equal([author.Id, assignee.Id, observer.Id], members.Order().ToArray());
    }

    [Fact]
    public async Task TaskGuest_CanReadOnlyWhileInvited_AndCannotBeAddedByExecutor()
    {
        await using var fixture = await Fixture.CreateAsync();
        var author = await fixture.AddUserAsync("guest-author@example.test", "Постановщик");
        var assignee = await fixture.AddUserAsync("guest-assignee@example.test", "Исполнитель");
        var observer = await fixture.AddUserAsync("guest-observer@example.test", "Наблюдатель");
        var accountant = await fixture.AddUserAsync("guest-accountant@example.test", "Бухгалтер");
        var task = await fixture.AddTaskAsync(author.Id, assignee.Id, observer.Id);
        var opened = await fixture.Controller(author).OpenTaskChat(task.Id, CancellationToken.None);
        var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<OkObjectResult>(opened.Result).Value);

        Assert.IsType<ForbidResult>(await fixture.Controller(assignee).UpdateTaskGuests(thread.Id,
            new UpdateInternalChatGuestsRequest { GuestUserIds = [accountant.Id] }, CancellationToken.None));
        Assert.IsType<NoContentResult>(await fixture.Controller(author).UpdateTaskGuests(thread.Id,
            new UpdateInternalChatGuestsRequest { GuestUserIds = [accountant.Id] }, CancellationToken.None));
        Assert.DoesNotContain(task.Participants, participant => participant.UserId == accountant.Id);
        Assert.IsType<OkObjectResult>((await fixture.Controller(accountant).OpenTaskChat(task.Id, CancellationToken.None)).Result);
        Assert.IsType<OkObjectResult>((await fixture.Controller(accountant).GetMessages(thread.Id,
            null, null, null, null, null, false, 50, CancellationToken.None)).Result);

        var attachmentMessage = new InternalChatMessage { ThreadId = thread.Id, AuthorUserId = author.Id, Text = "Документ" };
        fixture.Db.InternalChatMessages.Add(attachmentMessage);
        await fixture.Db.SaveChangesAsync();
        var attachment = new InternalChatAttachment
        {
            MessageId = attachmentMessage.Id, FileName = "report.txt", StoredFileName = "report.txt",
            UploadedByUserId = author.Id, Size = 1
        };
        fixture.Db.InternalChatAttachments.Add(attachment);
        await fixture.Db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>(await fixture.Controller(accountant).DownloadAttachment(attachment.Id, CancellationToken.None));

        Assert.IsType<NoContentResult>(await fixture.Controller(author).UpdateTaskGuests(thread.Id,
            new UpdateInternalChatGuestsRequest(), CancellationToken.None));
        Assert.IsType<ForbidResult>((await fixture.Controller(accountant).GetMessages(thread.Id,
            null, null, null, null, null, false, 50, CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await fixture.Controller(accountant).OpenTaskChat(task.Id, CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>(await fixture.Controller(accountant).DownloadAttachment(attachment.Id, CancellationToken.None));
    }

    [Fact]
    public async Task RemovedTaskParticipant_LosesChatAndAttachmentAccessWithoutReopeningTask()
    {
        await using var fixture = await Fixture.CreateAsync();
        var author = await fixture.AddUserAsync("removed-author@example.test", "Постановщик");
        var assignee = await fixture.AddUserAsync("removed-assignee@example.test", "Исполнитель");
        var observer = await fixture.AddUserAsync("removed-observer@example.test", "Наблюдатель");
        var task = await fixture.AddTaskAsync(author.Id, assignee.Id, observer.Id);
        var opened = await fixture.Controller(author).OpenTaskChat(task.Id, CancellationToken.None);
        var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<OkObjectResult>(opened.Result).Value);
        var sent = await fixture.Controller(author).SendMessage(thread.Id,
            new SendInternalChatMessageRequest { Text = "Конфиденциальная переписка" }, CancellationToken.None);
        Assert.IsType<CreatedAtActionResult>(sent.Result);

        fixture.Db.TaskJournalParticipants.RemoveRange(task.Participants);
        await fixture.Db.SaveChangesAsync();
        var list = Assert.IsAssignableFrom<IReadOnlyList<InternalChatThreadDto>>(Assert.IsType<OkObjectResult>(
            (await fixture.Controller(observer).GetThreads(CancellationToken.None)).Result).Value);
        Assert.DoesNotContain(list, item => item.Id == thread.Id);
        Assert.IsType<ForbidResult>((await fixture.Controller(observer).GetMessages(thread.Id,
            null, null, null, null, null, false, 50, CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await fixture.Controller(observer).SendMessage(thread.Id,
            new SendInternalChatMessageRequest { Text = "Ответ после исключения" }, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task PersonalHideAndArchive_AffectOnlyOwnerAndHiddenChatReturnsOnNewMessage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.AddUserAsync("hide-owner@example.test", "Владелец");
        var colleague = await fixture.AddUserAsync("hide-colleague@example.test", "Коллега");
        var created = await fixture.Controller(owner).CreateChannel(new CreateInternalChatChannelRequest
        {
            Name = "Обсуждение", MemberUserIds = [colleague.Id]
        }, CancellationToken.None);
        var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value);
        Assert.IsType<NoContentResult>(await fixture.Controller(owner).SetPersonalState(thread.Id,
            new UpdateInternalChatPersonalStateRequest { State = "hidden" }, CancellationToken.None));
        var hidden = Assert.IsAssignableFrom<IReadOnlyList<InternalChatThreadDto>>(Assert.IsType<OkObjectResult>(
            (await fixture.Controller(owner).GetThreads(CancellationToken.None)).Result).Value);
        Assert.DoesNotContain(hidden, item => item.Id == thread.Id);
        var colleagueList = Assert.IsAssignableFrom<IReadOnlyList<InternalChatThreadDto>>(Assert.IsType<OkObjectResult>(
            (await fixture.Controller(colleague).GetThreads(CancellationToken.None)).Result).Value);
        Assert.Contains(colleagueList, item => item.Id == thread.Id);

        await fixture.Controller(colleague).SendMessage(thread.Id,
            new SendInternalChatMessageRequest { Text = "Новая сверка" }, CancellationToken.None);
        var restored = Assert.IsAssignableFrom<IReadOnlyList<InternalChatThreadDto>>(Assert.IsType<OkObjectResult>(
            (await fixture.Controller(owner).GetThreads(CancellationToken.None)).Result).Value);
        Assert.Contains(restored, item => item.Id == thread.Id && item.UnreadCount == 1);

        Assert.IsType<NoContentResult>(await fixture.Controller(owner).SetPersonalState(thread.Id,
            new UpdateInternalChatPersonalStateRequest { State = "archived" }, CancellationToken.None));
        var archived = Assert.IsAssignableFrom<IReadOnlyList<InternalChatThreadDto>>(Assert.IsType<OkObjectResult>(
            (await fixture.Controller(owner).GetThreads(CancellationToken.None)).Result).Value);
        Assert.Contains(archived, item => item.Id == thread.Id && item.IsArchived);
        Assert.IsType<BadRequestObjectResult>(await fixture.Controller(owner).SetPersonalState(thread.Id,
            new UpdateInternalChatPersonalStateRequest { State = "deleted" }, CancellationToken.None));
    }

    [Fact]
    public async Task OpenDirect_ReusesSingleThreadRegardlessOfParticipantOrder()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.AddUserAsync("first@example.test", "Первый");
        var second = await fixture.AddUserAsync("second@example.test", "Второй");

        var firstResult = await fixture.Controller(first).OpenDirect(second.Id, CancellationToken.None);
        var created = Assert.IsType<CreatedAtActionResult>(firstResult.Result);
        var firstDto = Assert.IsType<InternalChatThreadDto>(created.Value);
        var secondResult = await fixture.Controller(second).OpenDirect(first.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(secondResult.Result);
        var secondDto = Assert.IsType<InternalChatThreadDto>(ok.Value);

        Assert.Equal(firstDto.Id, secondDto.Id);
        Assert.Equal(1, await fixture.Db.InternalChatThreads.CountAsync(item => item.Kind == InternalChatThreadKinds.Direct));
        Assert.Equal(2, await fixture.Db.InternalChatMembers.CountAsync(item => item.ThreadId == firstDto.Id));
    }

    [Fact]
    public async Task Messages_EnforceOwnershipMembershipAndPinLimit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.AddUserAsync("owner@example.test", "Владелец");
        var member = await fixture.AddUserAsync("member@example.test", "Участник");
        var outsider = await fixture.AddUserAsync("outside@example.test", "Посторонний");
        var channelResult = await fixture.Controller(owner).CreateChannel(new CreateInternalChatChannelRequest
        {
            Name = "Рабочий канал",
            MemberUserIds = [member.Id]
        }, CancellationToken.None);
        var channel = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(channelResult.Result).Value);

        var outsiderRead = await fixture.Controller(outsider).GetMessages(channel.Id, null, null, null, null, null, false, 50, CancellationToken.None);
        Assert.IsType<ForbidResult>(outsiderRead.Result);

        var sent = new List<InternalChatMessageDto>();
        for (var index = 1; index <= 4; index++)
        {
            var result = await fixture.Controller(member).SendMessage(channel.Id, new SendInternalChatMessageRequest { Text = $"Сообщение {index}" }, CancellationToken.None);
            sent.Add(Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value));
        }

        for (var index = 0; index < 3; index++)
            Assert.IsType<NoContentResult>(await fixture.Controller(owner).TogglePin(sent[index].Id, new PinInternalChatMessageRequest { Pinned = true }, CancellationToken.None));
        Assert.IsType<ConflictObjectResult>(await fixture.Controller(owner).TogglePin(sent[3].Id, new PinInternalChatMessageRequest { Pinned = true }, CancellationToken.None));

        var forbiddenEdit = await fixture.Controller(owner).EditMessage(sent[0].Id, new EditInternalChatMessageRequest { Text = "Чужая правка" }, CancellationToken.None);
        Assert.IsType<ForbidResult>(forbiddenEdit.Result);
        var ownEdit = await fixture.Controller(member).EditMessage(sent[0].Id, new EditInternalChatMessageRequest { Text = "Исправлено" }, CancellationToken.None);
        Assert.Equal("Исправлено", Assert.IsType<InternalChatMessageDto>(Assert.IsType<OkObjectResult>(ownEdit.Result).Value).Text);
    }

    [Fact]
    public async Task GetMessages_UsesFiftyItemCursorPage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.AddUserAsync("page@example.test", "Автор");
        var channelResult = await fixture.Controller(owner).CreateChannel(new CreateInternalChatChannelRequest { Name = "История" }, CancellationToken.None);
        var channel = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(channelResult.Result).Value);
        fixture.Db.InternalChatMessages.AddRange(Enumerable.Range(1, 55).Select(index => new InternalChatMessage
        {
            ThreadId = channel.Id,
            AuthorUserId = owner.Id,
            Text = $"Текст {index}",
            CreatedAt = DateTime.UtcNow.AddSeconds(index)
        }));
        await fixture.Db.SaveChangesAsync();

        var firstResult = await fixture.Controller(owner).GetMessages(channel.Id, null, null, null, null, null, false, 50, CancellationToken.None);
        var first = Assert.IsType<InternalChatMessagePageDto>(Assert.IsType<OkObjectResult>(firstResult.Result).Value);
        Assert.Equal(50, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextBeforeId);

        var secondResult = await fixture.Controller(owner).GetMessages(channel.Id, first.NextBeforeId, null, null, null, null, false, 50, CancellationToken.None);
        var second = Assert.IsType<InternalChatMessagePageDto>(Assert.IsType<OkObjectResult>(secondResult.Result).Value);
        Assert.Equal(5, second.Items.Count);
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task ChatDates_ReadFromDatabase_AreSerializedAsUtc()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.AddUserAsync("dates@example.test", "Автор");
        var created = await fixture.Controller(owner).CreateChannel(
            new CreateInternalChatChannelRequest { Name = "Время сообщений" }, CancellationToken.None);
        var channel = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value);
        var storedUtc = new DateTime(2026, 9, 23, 18, 30, 0, DateTimeKind.Unspecified);
        var thread = await fixture.Db.InternalChatThreads.SingleAsync(item => item.Id == channel.Id);
        thread.CreatedAt = storedUtc;
        thread.LastMessageAt = storedUtc;
        fixture.Db.InternalChatMessages.Add(new InternalChatMessage
        {
            ThreadId = channel.Id,
            AuthorUserId = owner.Id,
            Text = "Сообщение около полуночи",
            CreatedAt = storedUtc
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var threads = Assert.IsAssignableFrom<IReadOnlyList<InternalChatThreadDto>>(Assert.IsType<OkObjectResult>(
            (await fixture.Controller(owner).GetThreads(CancellationToken.None)).Result).Value);
        var listed = Assert.Single(threads);
        Assert.Equal(DateTimeKind.Utc, listed.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, listed.LastMessageAt?.Kind);
        Assert.EndsWith("Z", JsonSerializer.Serialize(listed.CreatedAt).Trim('"'));

        var page = Assert.IsType<InternalChatMessagePageDto>(Assert.IsType<OkObjectResult>(
            (await fixture.Controller(owner).GetMessages(channel.Id, null, null, null, null, null, false, 50, CancellationToken.None)).Result).Value);
        var message = Assert.Single(page.Items);
        Assert.Equal(DateTimeKind.Utc, message.CreatedAt.Kind);
        Assert.Equal(new DateTime(2026, 9, 23, 18, 30, 0, DateTimeKind.Utc), message.CreatedAt);
        Assert.EndsWith("Z", JsonSerializer.Serialize(message.CreatedAt).Trim('"'));
    }

    [Fact]
    public async Task CreateTaskFromMessage_RequiresTaskCreationPermissionInAdditionToChatMembership()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.AddUserAsync("task-owner@example.test", "Автор");
        var outsider = await fixture.AddUserAsync("task-outsider@example.test", "Посторонний");
        var channelResult = await fixture.Controller(owner).CreateChannel(
            new CreateInternalChatChannelRequest { Name = "Обсуждение" }, CancellationToken.None);
        var channel = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(channelResult.Result).Value);
        var sent = await fixture.Controller(owner).SendMessage(channel.Id,
            new SendInternalChatMessageRequest { Text = "Подготовить документ" }, CancellationToken.None);
        var message = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(sent.Result).Value);
        var request = new CreateTaskFromInternalChatMessageRequest
        {
            Title = "Подготовить документ",
            DueAt = DateTime.UtcNow.AddDays(1)
        };
        var permission = new RolePermission
        {
            RoleId = owner.RoleId,
            Section = RolePermissionCatalog.Tasks,
            CanView = true,
            CanCreate = false
        };
        fixture.Db.RolePermissions.Add(permission);
        await fixture.Db.SaveChangesAsync();

        var denied = await fixture.Controller(owner).CreateTaskFromMessage(message.Id, request, CancellationToken.None);
        Assert.IsType<ForbidResult>(denied.Result);
        Assert.Equal(0, await fixture.Db.TaskJournalItems.CountAsync());

        permission.CanCreate = true;
        await fixture.Db.SaveChangesAsync();
        var nonMember = await fixture.Controller(outsider).CreateTaskFromMessage(message.Id, request, CancellationToken.None);
        Assert.IsType<ForbidResult>(nonMember.Result);

        var allowed = await fixture.Controller(owner).CreateTaskFromMessage(message.Id, request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(allowed.Result);
        Assert.Equal(1, await fixture.Db.TaskJournalItems.CountAsync());
    }

    [Fact]
    public async Task MarkRead_RejectsOtherThreadAndUnknownMessagesWithoutClearingUnreadCount()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.AddUserAsync("read-owner@example.test", "Автор");
        var member = await fixture.AddUserAsync("read-member@example.test", "Сотрудник");
        var first = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(owner).CreateChannel(new CreateInternalChatChannelRequest
            {
                Name = "Первый чат",
                MemberUserIds = [member.Id]
            }, CancellationToken.None)).Result).Value);
        var second = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(owner).CreateChannel(new CreateInternalChatChannelRequest
            {
                Name = "Второй чат",
                MemberUserIds = [member.Id]
            }, CancellationToken.None)).Result).Value);
        var firstMessage = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(member).SendMessage(first.Id,
                new SendInternalChatMessageRequest { Text = "Новый документ" }, CancellationToken.None)).Result).Value);
        var otherMessage = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(member).SendMessage(second.Id,
                new SendInternalChatMessageRequest { Text = "Другое обсуждение" }, CancellationToken.None)).Result).Value);

        Assert.IsType<BadRequestObjectResult>(await fixture.Controller(owner).MarkRead(first.Id,
            new MarkInternalChatReadRequest { MessageId = otherMessage.Id }, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await fixture.Controller(owner).MarkRead(first.Id,
            new MarkInternalChatReadRequest { MessageId = otherMessage.Id + 100 }, CancellationToken.None));
        var unread = Assert.IsAssignableFrom<IReadOnlyList<InternalChatThreadDto>>(Assert.IsType<OkObjectResult>(
            (await fixture.Controller(owner).GetThreads(CancellationToken.None)).Result).Value);
        Assert.Equal(1, unread.Single(item => item.Id == first.Id).UnreadCount);

        Assert.IsType<NoContentResult>(await fixture.Controller(owner).MarkRead(first.Id,
            new MarkInternalChatReadRequest { MessageId = firstMessage.Id }, CancellationToken.None));
        var read = Assert.IsAssignableFrom<IReadOnlyList<InternalChatThreadDto>>(Assert.IsType<OkObjectResult>(
            (await fixture.Controller(owner).GetThreads(CancellationToken.None)).Result).Value);
        Assert.Equal(0, read.Single(item => item.Id == first.Id).UnreadCount);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly Role _role;
        public PaymentContext Db { get; }

        private Fixture(SqliteConnection connection, PaymentContext db, Role role)
        {
            _connection = connection;
            Db = db;
            _role = role;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(connection)
                .Options;
            var db = new PaymentContext(options);
            await db.Database.EnsureCreatedAsync();
            var role = new Role { Name = "employee", Description = "Сотрудник" };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            return new Fixture(connection, db, role);
        }

        public async Task<User> AddUserAsync(string email, string name)
        {
            var user = new User
            {
                Email = email,
                FullName = name,
                PasswordHash = "test",
                RoleId = _role.Id,
                IsActive = true,
                IsApproved = true
            };
            Db.Users.Add(user);
            await Db.SaveChangesAsync();
            return user;
        }

        public async Task<TaskJournalItem> AddTaskAsync(int authorId, int assigneeId, int observerId)
        {
            var task = new TaskJournalItem
            {
                ObjectName = "Объект",
                Title = "Задача",
                AuthorUserId = authorId,
                AuthorName = "Автор",
                AssigneeUserId = assigneeId,
                AssigneeName = "Исполнитель",
                DueAt = DateTime.UtcNow.AddDays(1),
                Participants = [new TaskJournalParticipant { UserId = observerId, DisplayName = "Наблюдатель", Role = "observer" }]
            };
            Db.TaskJournalItems.Add(task);
            await Db.SaveChangesAsync();
            return task;
        }

        public InternalChatController Controller(User user)
        {
            var notifications = DispatchProxy.Create<INotificationService, NotificationProxy>();
            var controller = new InternalChatController(Db, notifications, NoOpRealtimeNotificationPublisher.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                            new Claim(ClaimTypes.Name, user.FullName)
                        ], "test"))
                    }
                }
            };
            return controller;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private class NotificationProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            var returnType = targetMethod.ReturnType;
            if (returnType == typeof(Task)) return Task.CompletedTask;
            if (returnType == typeof(Task<bool>)) return Task.FromResult(true);
            if (returnType == typeof(Task<IReadOnlyList<NotificationDelivery>>))
                return Task.FromResult<IReadOnlyList<NotificationDelivery>>([]);
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GenericTypeArguments[0];
                var defaultValue = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, [defaultValue]);
            }
            return null;
        }
    }
}
