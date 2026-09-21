using System.Reflection;
using System.Security.Claims;
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
