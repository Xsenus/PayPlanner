using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class InternalChatControllerTests
{
    [Fact]
    public async Task FailedNotificationRemainsPendingAndCanBeRetriedOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        var author = await fixture.AddUserAsync("recovery-author@example.test", "Автор");
        var colleague = await fixture.AddUserAsync("recovery-colleague@example.test", "Коллега");
        var created = await fixture.Controller(author).CreateChannel(new CreateInternalChatChannelRequest
        { Name = "Восстановление уведомления", MemberUserIds = [colleague.Id] }, CancellationToken.None);
        var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value);
        var failedNotifications = DispatchProxy.Create<INotificationService, NotificationProxy>();
        ((NotificationProxy)(object)failedNotifications).FailNext = true;
        var realtime = new CountingRealtimeUpdate();
        var sent = await fixture.Controller(author, notifications: failedNotifications, realtime: realtime).SendMessage(thread.Id,
            new SendInternalChatMessageRequest { Text = "Ответьте, пожалуйста" }, CancellationToken.None);
        var message = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(sent.Result).Value);
        Assert.Equal(1, realtime.ChangedCount);
        Assert.True((await fixture.Db.InternalChatMessages.AsNoTracking().SingleAsync(item => item.Id == message.Id))
            .NotificationDispatchPending);

        var recoveredNotifications = DispatchProxy.Create<INotificationService, NotificationProxy>();
        Assert.True(await InternalChatNotificationRecoveryService.DispatchMessageAsync(
            fixture.Db, recoveredNotifications, message.Id, CancellationToken.None));
        Assert.False(await InternalChatNotificationRecoveryService.DispatchMessageAsync(
            fixture.Db, recoveredNotifications, message.Id, CancellationToken.None));
        var delivered = Assert.Single(((NotificationProxy)(object)recoveredNotifications).Events);
        Assert.Equal(colleague.Id, delivered.RecipientUserId);
        Assert.False((await fixture.Db.InternalChatMessages.AsNoTracking().SingleAsync(item => item.Id == message.Id))
            .NotificationDispatchPending);
    }

    [Fact]
    public async Task RecoveryWorkerClaimsOnlyDueMessages()
    {
        await using var fixture = await Fixture.CreateAsync();
        var author = await fixture.AddUserAsync("worker-author@example.test", "Автор");
        var colleague = await fixture.AddUserAsync("worker-colleague@example.test", "Коллега");
        var created = await fixture.Controller(author).CreateChannel(new CreateInternalChatChannelRequest
        { Name = "Фоновый повтор", MemberUserIds = [colleague.Id] }, CancellationToken.None);
        var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value);
        var failedNotifications = DispatchProxy.Create<INotificationService, NotificationProxy>();
        ((NotificationProxy)(object)failedNotifications).FailNext = true;
        var sent = await fixture.Controller(author, notifications: failedNotifications).SendMessage(thread.Id,
            new SendInternalChatMessageRequest { Text = "Важный вопрос" }, CancellationToken.None);
        var message = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(sent.Result).Value);
        var recoveredNotifications = DispatchProxy.Create<INotificationService, NotificationProxy>();
        using var services = new ServiceCollection()
            .AddScoped(_ => fixture.NewDb())
            .AddScoped(_ => recoveredNotifications)
            .BuildServiceProvider();
        var worker = new InternalChatNotificationRecoveryService(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InternalChatNotificationRecoveryService>.Instance);
        Assert.Equal(0, await worker.RunOnceAsync());
        await fixture.Db.InternalChatMessages.Where(item => item.Id == message.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LastNotificationDispatchAttemptAt,
                DateTime.UtcNow.AddMinutes(-6)));
        Assert.Equal(1, await worker.RunOnceAsync());
        Assert.Equal(0, await worker.RunOnceAsync());
        Assert.Single(((NotificationProxy)(object)recoveredNotifications).Events);
    }

    [Fact]
    public async Task SendKeyIsScopedToAuthorAndThreadAndCannotResurrectDeletedMessage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var author = await fixture.AddUserAsync("send-key-author@example.test", "Автор");
        var colleague = await fixture.AddUserAsync("send-key-colleague@example.test", "Коллега");
        var outsider = await fixture.AddUserAsync("send-key-outsider@example.test", "Посторонний");
        var created = await fixture.Controller(author).CreateChannel(new CreateInternalChatChannelRequest
        { Name = "Ключ отправки", MemberUserIds = [colleague.Id] }, CancellationToken.None);
        var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value);
        var key = Guid.NewGuid();
        var request = new SendInternalChatMessageRequest { Text = "Сообщение", ClientRequestId = key };
        var first = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(author).SendMessage(thread.Id, request, CancellationToken.None)).Result).Value);
        var second = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(colleague).SendMessage(thread.Id, request, CancellationToken.None)).Result).Value);
        Assert.NotEqual(first.Id, second.Id);
        Assert.IsType<ForbidResult>((await fixture.Controller(outsider).SendMessage(thread.Id, request, CancellationToken.None)).Result);
        await fixture.Db.InternalChatMessages.Where(item => item.Id == first.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.DeletedAt, DateTime.UtcNow));
        Assert.IsType<ConflictObjectResult>((await fixture.Controller(author).SendMessage(thread.Id, request,
            CancellationToken.None)).Result);
        Assert.Equal(2, await fixture.Db.InternalChatMessages.CountAsync());
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("database")]
    public async Task FailedAttachmentDoesNotPublishMessageOrThreadActivity(string failure)
    {
        var root = Path.Combine(Path.GetTempPath(), "pp_chat_atomic_" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var fixture = await Fixture.CreateAsync(failure == "database" ? new RejectAttachmentSave() : null);
            var author = await fixture.AddUserAsync("atomic-chat-author@example.test", "Автор");
            var created = await fixture.Controller(author).CreateChannel(new CreateInternalChatChannelRequest
            {
                Name = "Атомарное вложение"
            }, CancellationToken.None);
            var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value);
            var before = await fixture.Db.InternalChatThreads.AsNoTracking().SingleAsync(item => item.Id == thread.Id);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["FileStorage:RootPath"] = root }).Build();
            IFormFile file = failure == "copy" ? new FaultingChatFile()
                : new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "file", "document.txt")
                { Headers = new HeaderDictionary(), ContentType = "text/plain" };
            var files = failure == "copy"
                ? new List<IFormFile>
                {
                    new FormFile(new MemoryStream([4, 5, 6]), 0, 3, "file", "first.txt")
                    { Headers = new HeaderDictionary(), ContentType = "text/plain" },
                    file
                }
                : new List<IFormFile> { file };

            await Assert.ThrowsAsync<IOException>(() => fixture.Controller(author, configuration: configuration).SendMessage(
                thread.Id, new SendInternalChatMessageRequest { Text = "Документ", Files = files }, CancellationToken.None));

            Assert.False(await fixture.Db.InternalChatMessages.AsNoTracking().AnyAsync(item => item.ThreadId == thread.Id));
            Assert.False(await fixture.Db.InternalChatAttachments.AsNoTracking().AnyAsync());
            var after = await fixture.Db.InternalChatThreads.AsNoTracking().SingleAsync(item => item.Id == thread.Id);
            Assert.Equal(before.LastMessageAt, after.LastMessageAt);
            Assert.Equal(before.UpdatedAt, after.UpdatedAt);
            var attachments = Path.Combine(root, "internal-chat-files", thread.Id.ToString());
            Assert.Empty(Directory.Exists(attachments) ? Directory.EnumerateFiles(attachments) : []);
        }
        finally
        {
            Assert.StartsWith("pp_chat_atomic_", Path.GetFileName(root), StringComparison.Ordinal);
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Path.GetFullPath(root)));
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedMessageDeletionKeepsAttachmentBytesAndDatabaseLink()
    {
        var root = Path.Combine(Path.GetTempPath(), "pp_chat_delete_" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var fixture = await Fixture.CreateAsync(new RejectAttachmentDelete());
            var author = await fixture.AddUserAsync("delete-author@example.test", "Автор");
            var channel = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(
                (await fixture.Controller(author).CreateChannel(new CreateInternalChatChannelRequest
                { Name = "Удаление вложения" }, CancellationToken.None)).Result).Value);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["FileStorage:RootPath"] = root }).Build();
            var file = new FormFile(new MemoryStream([7, 8, 9]), 0, 3, "file", "evidence.txt")
            { Headers = new HeaderDictionary(), ContentType = "text/plain" };
            var message = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
                (await fixture.Controller(author, configuration: configuration).SendMessage(channel.Id,
                    new SendInternalChatMessageRequest { Text = "Документ", Files = [file] }, CancellationToken.None)).Result).Value);
            var attachment = await fixture.Db.InternalChatAttachments.AsNoTracking().SingleAsync();
            var path = Path.Combine(root, "internal-chat-files", channel.Id.ToString(), attachment.StoredFileName);

            await Assert.ThrowsAsync<IOException>(() => fixture.Controller(author, configuration: configuration)
                .DeleteMessage(message.Id, CancellationToken.None));

            Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(path));
            await using var independent = fixture.NewDb();
            Assert.Null((await independent.InternalChatMessages.AsNoTracking().SingleAsync(item => item.Id == message.Id)).DeletedAt);
            Assert.True(await independent.InternalChatAttachments.AsNoTracking().AnyAsync(item => item.Id == attachment.Id));
        }
        finally
        {
            Assert.StartsWith("pp_chat_delete_", Path.GetFileName(root), StringComparison.Ordinal);
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Path.GetFullPath(root)));
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("deleted", true)]
    [InlineData("conflict", false)]
    public async Task ManagedAttachmentDeletionUsesDomainTransactionAndNeverDeletesBytesEarly(string outcome, bool deleted)
    {
        var root = Path.Combine(Path.GetTempPath(), "pp_chat_managed_delete_" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var fixture = await Fixture.CreateAsync();
            var author = await fixture.AddUserAsync("managed-delete-author@example.test", "Автор");
            var channel = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(
                (await fixture.Controller(author).CreateChannel(new CreateInternalChatChannelRequest
                { Name = "Управляемое удаление" }, CancellationToken.None)).Result).Value);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["FileStorage:RootPath"] = root }).Build();
            var file = new FormFile(new MemoryStream([1, 3, 5]), 0, 3, "file", "evidence.txt")
            { Headers = new HeaderDictionary(), ContentType = "text/plain" };
            var message = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
                (await fixture.Controller(author, configuration: configuration).SendMessage(channel.Id,
                    new SendInternalChatMessageRequest { Text = "Материал", Files = [file] }, CancellationToken.None)).Result).Value);
            var attachment = await fixture.Db.InternalChatAttachments.AsNoTracking().SingleAsync();
            var path = Path.Combine(root, "internal-chat-files", channel.Id.ToString(), attachment.StoredFileName);
            var storage = new RecordingChatDeletion(fixture.Db, outcome);

            var result = await fixture.Controller(author, configuration: configuration, productStorageDeletion: storage)
                .DeleteMessage(message.Id, CancellationToken.None);

            if (deleted) Assert.IsType<NoContentResult>(result);
            else Assert.IsType<ObjectResult>(result);
            Assert.Equal(1, storage.Calls);
            Assert.Equal(LegacyStorageSources.InternalChat, storage.Source);
            Assert.Equal(attachment.Id, storage.FileId);
            Assert.Equal(path, storage.Path);
            Assert.Equal(new byte[] { 1, 3, 5 }, await File.ReadAllBytesAsync(path));
            await using var independent = fixture.NewDb();
            Assert.Equal(deleted, (await independent.InternalChatMessages.AsNoTracking()
                .SingleAsync(item => item.Id == message.Id)).DeletedAt.HasValue);
            Assert.Equal(!deleted, await independent.InternalChatAttachments.AsNoTracking()
                .AnyAsync(item => item.Id == attachment.Id));
        }
        finally
        {
            Assert.StartsWith("pp_chat_managed_delete_", Path.GetFileName(root), StringComparison.Ordinal);
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Path.GetFullPath(root)));
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedAttachmentRetainsItsBytesEvenIfRealtimeFails(bool failRealtime)
    {
        var root = Path.Combine(Path.GetTempPath(), "pp_chat_committed_" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var fixture = await Fixture.CreateAsync();
            var author = await fixture.AddUserAsync("committed-chat-author@example.test", "Автор");
            var created = await fixture.Controller(author).CreateChannel(new CreateInternalChatChannelRequest
            {
                Name = "Проверенное вложение"
            }, CancellationToken.None);
            var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["FileStorage:RootPath"] = root }).Build();
            var file = new FormFile(new MemoryStream([4, 5, 6]), 0, 3, "file", "document.txt")
            { Headers = new HeaderDictionary(), ContentType = "text/plain" };
            var requestId = Guid.NewGuid();
            var request = new SendInternalChatMessageRequest { Text = "Готово", Files = [file], ClientRequestId = requestId };
            var controller = fixture.Controller(author, configuration: configuration,
                realtime: failRealtime ? new RejectRealtimeUpdate() : NoOpRealtimeNotificationPublisher.Instance);
            var first = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
                (await controller.SendMessage(thread.Id, request, CancellationToken.None)).Result).Value);
            var retry = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
                (await fixture.Controller(author, configuration: configuration).SendMessage(thread.Id, request, CancellationToken.None)).Result).Value);
            Assert.Equal(first.Id, retry.Id);
            Assert.IsType<ConflictObjectResult>((await fixture.Controller(author).SendMessage(thread.Id,
                new SendInternalChatMessageRequest { Text = "Другой текст", ClientRequestId = requestId },
                CancellationToken.None)).Result);

            var saved = await fixture.Db.InternalChatMessages.AsNoTracking()
                .Include(item => item.Attachments).SingleAsync(item => item.ThreadId == thread.Id);
            var attachment = Assert.Single(saved.Attachments);
            Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(Path.Combine(root,
                "internal-chat-files", thread.Id.ToString(), attachment.StoredFileName)));
            Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "internal-chat-files", thread.Id.ToString())));
            Assert.NotNull((await fixture.Db.InternalChatThreads.AsNoTracking().SingleAsync(item => item.Id == thread.Id)).LastMessageAt);
        }
        finally
        {
            Assert.StartsWith("pp_chat_committed_", Path.GetFileName(root), StringComparison.Ordinal);
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Path.GetFullPath(root)));
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PostgreSqlAttachmentSaveFailureRollsBackMessageAndKeepsFilesOwned()
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        var name = "pp_chat_atomic_" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), name);
        var adminSettings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            {
                await command.ExecuteNonQueryAsync();
                created = true;
            }
            var settings = new NpgsqlConnectionStringBuilder(adminSettings.ConnectionString) { Database = name };
            var interceptor = new RejectAttachmentSave();
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(settings.ConnectionString).AddInterceptors(interceptor).Options);
            await db.Database.MigrateAsync();
            db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
            var role = new Role { Name = "employee", Description = "Сотрудник" };
            var author = new User
            {
                Email = "atomic-postgres@example.test",
                FullName = "Автор",
                PasswordHash = "test",
                Role = role,
                IsActive = true,
                IsApproved = true
            };
            db.Users.Add(author);
            await db.SaveChangesAsync();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["FileStorage:RootPath"] = root }).Build();
            InternalChatController Controller()
            {
                var notifications = DispatchProxy.Create<INotificationService, NotificationProxy>();
                return new InternalChatController(db, notifications, NoOpRealtimeNotificationPublisher.Instance,
                    configuration: configuration)
                {
                    ControllerContext = new ControllerContext
                    {
                        HttpContext = new DefaultHttpContext
                        {
                            User = new ClaimsPrincipal(new ClaimsIdentity(
                            [new Claim(ClaimTypes.NameIdentifier, author.Id.ToString()),
                                new Claim(ClaimTypes.Name, author.FullName)], "test"))
                        }
                    }
                };
            }
            var channel = await Controller().CreateChannel(new CreateInternalChatChannelRequest
            { Name = "PostgreSQL вложение" }, CancellationToken.None);
            var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(channel.Result).Value);
            var before = await db.InternalChatThreads.AsNoTracking().SingleAsync(item => item.Id == thread.Id);
            var file = new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "file", "document.txt")
            { Headers = new HeaderDictionary(), ContentType = "text/plain" };

            await Assert.ThrowsAsync<IOException>(() => Controller().SendMessage(thread.Id,
                new SendInternalChatMessageRequest { Text = "Документ", Files = [file] }, CancellationToken.None));
            Assert.False(await db.InternalChatMessages.AsNoTracking().AnyAsync());
            Assert.False(await db.InternalChatAttachments.AsNoTracking().AnyAsync());
            var after = await db.InternalChatThreads.AsNoTracking().SingleAsync(item => item.Id == thread.Id);
            Assert.Equal(before.LastMessageAt, after.LastMessageAt);
            Assert.Equal(before.UpdatedAt, after.UpdatedAt);
            var directory = Path.Combine(root, "internal-chat-files", thread.Id.ToString());
            Assert.Empty(Directory.Exists(directory) ? Directory.EnumerateFiles(directory) : Array.Empty<string>());
        }
        finally
        {
            if (created)
            {
                await using var command = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
                await command.ExecuteNonQueryAsync();
            }
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Path.GetFullPath(root)));
            Assert.StartsWith("pp_chat_atomic_", Path.GetFileName(root), StringComparison.Ordinal);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PostgreSqlMessageDeletionCommitsBeforeRemovingLegacyBytes()
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        var name = "pp_chat_delete_" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), name);
        var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            { await create.ExecuteNonQueryAsync(); created = true; }
            settings.Database = name;
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(settings.ConnectionString).Options);
            await db.Database.MigrateAsync();
            db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
            var author = new User
            {
                Email = "delete-postgres@example.test",
                FullName = "Автор",
                PasswordHash = "test",
                Role = new Role { Name = "employee", Description = "Сотрудник" },
                IsActive = true,
                IsApproved = true
            };
            db.Users.Add(author);
            await db.SaveChangesAsync();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["FileStorage:RootPath"] = root }).Build();
            var notifications = DispatchProxy.Create<INotificationService, NotificationProxy>();
            var controller = new InternalChatController(db, notifications, NoOpRealtimeNotificationPublisher.Instance,
                configuration: configuration)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, author.Id.ToString()),
                            new Claim(ClaimTypes.Name, author.FullName)], "test"))
                    }
                }
            };
            var createdChannel = await controller.CreateChannel(new CreateInternalChatChannelRequest
            { Name = "Удаление PostgreSQL" }, CancellationToken.None);
            var channel = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(createdChannel.Result).Value);
            var message = new InternalChatMessage { ThreadId = channel.Id, AuthorUserId = author.Id, Text = "Файл" };
            var attachment = new InternalChatAttachment
            {
                Message = message,
                FileName = "evidence.txt",
                StoredFileName = Guid.NewGuid().ToString("N") + ".txt",
                ContentType = "text/plain",
                Size = 3,
                UploadedByUserId = author.Id
            };
            db.InternalChatAttachments.Add(attachment);
            await db.SaveChangesAsync();
            var path = Path.Combine(root, "internal-chat-files", channel.Id.ToString(), attachment.StoredFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);

            Assert.IsType<NoContentResult>(await controller.DeleteMessage(message.Id, CancellationToken.None));

            db.ChangeTracker.Clear();
            Assert.NotNull((await db.InternalChatMessages.AsNoTracking().SingleAsync(item => item.Id == message.Id)).DeletedAt);
            Assert.False(await db.InternalChatAttachments.AsNoTracking().AnyAsync(item => item.Id == attachment.Id));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Assert.Matches("^pp_chat_delete_[a-f0-9]{32}$", name);
            if (created)
            {
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync();
            }
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Path.GetFullPath(root)));
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RejectRealtimeUpdate : IRealtimeNotificationPublisher
    {
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default)
            => throw new IOException("Owned synthetic realtime failure.");
    }

    private sealed class CountingRealtimeUpdate : IRealtimeNotificationPublisher
    {
        public int ChangedCount { get; private set; }
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default)
        {
            ChangedCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RejectAttachmentSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<InternalChatMessage>()
                .Any(entry => entry.State == EntityState.Added && entry.Entity.Attachments.Count > 0) == true)
                throw new IOException("Owned synthetic database save failure.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RejectAttachmentDelete : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<InternalChatAttachment>()
                .Any(entry => entry.State == EntityState.Deleted) == true)
                throw new IOException("Owned synthetic attachment delete failure.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingChatDeletion(PaymentContext db, string outcome) : IProductStorageDeletionService
    {
        public int Calls { get; private set; }
        public string? Source { get; private set; }
        public long FileId { get; private set; }
        public string? Path { get; private set; }

        public Task<string> DeleteAsync(string source, long fileId, string name, string contentType,
            string compatibilityPath, int? actorId, CancellationToken ct)
        {
            Assert.NotNull(db.Database.CurrentTransaction);
            Assert.Equal("evidence.txt", name);
            Assert.Equal("text/plain", contentType);
            Assert.True(actorId > 0);
            Calls++;
            Source = source;
            FileId = fileId;
            Path = compatibilityPath;
            return Task.FromResult(outcome);
        }
    }

    private sealed class FaultingChatFile : IFormFile
    {
        public string ContentType { get; set; } = "text/plain";
        public string ContentDisposition { get; set; } = string.Empty;
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public long Length => 3;
        public string Name => "file";
        public string FileName => "document.txt";
        public Stream OpenReadStream() => new MemoryStream([1, 2, 3]);
        public void CopyTo(Stream target) => throw new IOException("Owned synthetic copy failure.");
        public async Task CopyToAsync(Stream target, CancellationToken cancellationToken = default)
        {
            await target.WriteAsync(new byte[] { 1 }, cancellationToken);
            throw new IOException("Owned synthetic copy failure.");
        }
    }

    [Theory]
    [InlineData("available")]
    [InlineData("unavailable")]
    [InlineData("deletedObject")]
    [InlineData("outsider")]
    [InlineData("removed")]
    [InlineData("deletedMessage")]
    public async Task DownloadAttachment_ManagedCopiesKeepMembershipAndDeletionGuards(string scenario)
    {
        await using var fixture = await Fixture.CreateAsync();
        var author = await fixture.AddUserAsync("managed-chat-author@example.test", "Автор");
        var member = await fixture.AddUserAsync("managed-chat-member@example.test", "Участник");
        var outsider = await fixture.AddUserAsync("managed-chat-outsider@example.test", "Посторонний");
        var created = await fixture.Controller(author).CreateChannel(new CreateInternalChatChannelRequest
        {
            Name = "Документы",
            MemberUserIds = [member.Id],
        }, CancellationToken.None);
        var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value);
        var sent = await fixture.Controller(author).SendMessage(thread.Id,
            new SendInternalChatMessageRequest { Text = "Документ" }, CancellationToken.None);
        var message = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(sent.Result).Value);
        var attachment = new InternalChatAttachment
        {
            MessageId = message.Id,
            FileName = "managed.txt",
            StoredFileName = "missing-managed.txt",
            ContentType = "text/plain",
            Size = 3,
            UploadedByUserId = author.Id,
        };
        fixture.Db.InternalChatAttachments.Add(attachment);
        await fixture.Db.SaveChangesAsync();
        if (scenario == "removed")
            await fixture.Db.InternalChatMembers.Where(item => item.ThreadId == thread.Id && item.UserId == member.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false));
        if (scenario == "deletedMessage")
            await fixture.Db.InternalChatMessages.Where(item => item.Id == message.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.DeletedAt, DateTime.UtcNow));
        var reader = new RecordingChatProductReader(scenario switch
        {
            "unavailable" => ProductStorageReadStatus.Unavailable,
            "deletedObject" => ProductStorageReadStatus.Deleted,
            _ => ProductStorageReadStatus.Available,
        });
        var result = await fixture.Controller(scenario == "outsider" ? outsider : member, productStorageReader: reader)
            .DownloadAttachment(attachment.Id, CancellationToken.None);
        if (scenario is "outsider" or "removed")
        {
            Assert.IsType<ForbidResult>(result);
            Assert.Equal(0, reader.Calls);
        }
        else if (scenario == "deletedMessage")
        {
            Assert.IsType<NotFoundResult>(result);
            Assert.Equal(0, reader.Calls);
        }
        else
        {
            Assert.Equal(1, reader.Calls);
            Assert.Equal(attachment.Id, reader.FileId);
            if (scenario == "unavailable") Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
            else if (scenario == "deletedObject") Assert.IsType<NotFoundResult>(result);
            else
            {
                var download = Assert.IsType<FileStreamResult>(result);
                Assert.Equal(attachment.FileName, download.FileDownloadName);
                Assert.True(download.EnableRangeProcessing);
                await using var stream = download.FileStream;
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                Assert.Equal(new byte[] { 1, 2, 3 }, memory.ToArray());
            }
        }
    }

    private sealed class RecordingChatProductReader(ProductStorageReadStatus status) : IProductStorageReader
    {
        public int Calls { get; private set; }
        public long FileId { get; private set; }
        public Task<ProductStorageReadResult> OpenReadAsync(string source, long fileId, string logicalName,
            string contentType, CancellationToken cancellationToken)
        {
            Calls++;
            FileId = fileId;
            Assert.Equal(LegacyStorageSources.InternalChat, source);
            return Task.FromResult(new ProductStorageReadResult(status, status == ProductStorageReadStatus.Available
                ? new(LegacyStorageSources.GetObjectId(source, fileId), 1, contentType, logicalName, 3, new MemoryStream([1, 2, 3]), "mirror")
                : null));
        }
    }

    [Fact]
    public async Task NoReplyReminder_OnlyAuthorCanEnable_AndAnotherPersonsReplyStopsIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var author = await fixture.AddUserAsync("reminder-author@example.test", "Автор");
        var colleague = await fixture.AddUserAsync("reminder-colleague@example.test", "Коллега");
        var outsider = await fixture.AddUserAsync("reminder-outsider@example.test", "Посторонний");
        var created = await fixture.Controller(author).CreateChannel(new CreateInternalChatChannelRequest
        {
            Name = "Обсуждение",
            MemberUserIds = [colleague.Id]
        }, CancellationToken.None);
        var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value);
        var sent = await fixture.Controller(author).SendMessage(thread.Id,
            new SendInternalChatMessageRequest { Text = "Прошу ответить" }, CancellationToken.None);
        var message = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(sent.Result).Value);

        Assert.IsType<ForbidResult>((await fixture.Controller(outsider).SetNoReplyReminder(message.Id,
            new SetInternalChatNoReplyReminderRequest { Enabled = true }, CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await fixture.Controller(colleague).SetNoReplyReminder(message.Id,
            new SetInternalChatNoReplyReminderRequest { Enabled = true }, CancellationToken.None)).Result);
        var enabled = await fixture.Controller(author).SetNoReplyReminder(message.Id,
            new SetInternalChatNoReplyReminderRequest { Enabled = true }, CancellationToken.None);
        Assert.True(Assert.IsType<InternalChatMessageDto>(Assert.IsType<OkObjectResult>(enabled.Result).Value).NoReplyReminderEnabled);
        var dueAt = (await fixture.Db.InternalChatMessages.FindAsync(message.Id))!.NextNoReplyReminderAt;
        Assert.InRange(dueAt!.Value, DateTime.UtcNow.AddHours(23), DateTime.UtcNow.AddHours(25));
        await fixture.Controller(author).SetNoReplyReminder(message.Id,
            new SetInternalChatNoReplyReminderRequest { Enabled = true }, CancellationToken.None);
        Assert.Equal(dueAt, (await fixture.Db.InternalChatMessages.FindAsync(message.Id))!.NextNoReplyReminderAt);
        await fixture.Controller(author).SetNoReplyReminder(message.Id,
            new SetInternalChatNoReplyReminderRequest { Enabled = false }, CancellationToken.None);
        Assert.Null((await fixture.Db.InternalChatMessages.FindAsync(message.Id))!.NextNoReplyReminderAt);
        await fixture.Controller(author).SetNoReplyReminder(message.Id,
            new SetInternalChatNoReplyReminderRequest { Enabled = true }, CancellationToken.None);

        await fixture.Controller(author).SendMessage(thread.Id,
            new SendInternalChatMessageRequest { Text = "Сам себе отвечаю", ReplyToMessageId = message.Id }, CancellationToken.None);
        Assert.NotNull((await fixture.Db.InternalChatMessages.FindAsync(message.Id))!.NextNoReplyReminderAt);
        await fixture.Controller(colleague).SendMessage(thread.Id,
            new SendInternalChatMessageRequest { Text = "Ответ", ReplyToMessageId = message.Id }, CancellationToken.None);
        Assert.Null((await fixture.Db.InternalChatMessages.FindAsync(message.Id))!.NextNoReplyReminderAt);
        Assert.IsType<BadRequestObjectResult>((await fixture.Controller(author).SetNoReplyReminder(message.Id,
            new SetInternalChatNoReplyReminderRequest { Enabled = true }, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task NoReplyReminder_SendsOnceWhenDue_AndStopsAfterAccessRevoked()
    {
        await using var fixture = await Fixture.CreateAsync();
        var author = await fixture.AddUserAsync("due-author@example.test", "Автор");
        var colleague = await fixture.AddUserAsync("due-colleague@example.test", "Коллега");
        var created = await fixture.Controller(author).CreateChannel(new CreateInternalChatChannelRequest
        {
            Name = "Обсуждение",
            MemberUserIds = [colleague.Id]
        }, CancellationToken.None);
        var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value);
        var sent = await fixture.Controller(author).SendMessage(thread.Id,
            new SendInternalChatMessageRequest { Text = "Ожидаю ответа" }, CancellationToken.None);
        var message = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(sent.Result).Value);
        await fixture.Controller(author).SetNoReplyReminder(message.Id,
            new SetInternalChatNoReplyReminderRequest { Enabled = true }, CancellationToken.None);
        var stored = (await fixture.Db.InternalChatMessages.FindAsync(message.Id))!;
        stored.NextNoReplyReminderAt = DateTime.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();

        var notifications = DispatchProxy.Create<INotificationService, NotificationProxy>();
        using var services = new ServiceCollection()
            .AddSingleton(fixture.Db)
            .AddSingleton(notifications)
            .BuildServiceProvider();
        var service = new InternalChatNoReplyReminderService(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InternalChatNoReplyReminderService>.Instance);
        Assert.Equal(1, await service.RunOnceAsync());
        Assert.Equal(0, await service.RunOnceAsync());
        var reminderEvents = ((NotificationProxy)(object)notifications).Events;
        Assert.Single(reminderEvents);
        Assert.Equal(UserNotificationTypes.ChatNoReplyReminder, reminderEvents[0].Type);
        Assert.Equal(author.Id, reminderEvents[0].RecipientUserId);

        stored.NextNoReplyReminderAt = DateTime.UtcNow.AddMinutes(-1);
        var member = await fixture.Db.InternalChatMembers.SingleAsync(item => item.ThreadId == thread.Id && item.UserId == author.Id);
        member.IsActive = false;
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(0, await service.RunOnceAsync());
        Assert.Null(stored.NextNoReplyReminderAt);
        Assert.Single(reminderEvents);
    }

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

        var posted = await fixture.Controller(author).SendMessage(dto.Id,
            new SendInternalChatMessageRequest { Text = "Посмотрите приложенные документы" }, CancellationToken.None);
        var message = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(posted.Result).Value);
        var reopened = await fixture.Controller(assignee).OpenTaskChat(task.Id, CancellationToken.None);
        var unread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<OkObjectResult>(reopened.Result).Value);
        Assert.Equal(1, unread.UnreadCount);
        Assert.Equal(message.Id, unread.FirstUnreadMessageId);
    }

    [Fact]
    public async Task OpenTaskChat_UsesThreadCreatedByConcurrentRequest()
    {
        await using var fixture = await Fixture.CreateAsync();
        var author = await fixture.AddUserAsync("race-author@example.test", "Постановщик");
        var assignee = await fixture.AddUserAsync("race-assignee@example.test", "Исполнитель");
        var observer = await fixture.AddUserAsync("race-observer@example.test", "Наблюдатель");
        var task = await fixture.AddTaskAsync(author.Id, assignee.Id, observer.Id);

        var connection = fixture.Db.Database.GetDbConnection();
        var winnerOptions = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options;
        await using var winnerDb = new PaymentContext(winnerOptions);
        var interceptor = new ConcurrentTaskChatCreationInterceptor(async () =>
        {
            winnerDb.InternalChatThreads.Add(new InternalChatThread
            {
                Kind = InternalChatThreadKinds.Task,
                Name = task.Title,
                TaskJournalItemId = task.Id,
                CreatedByUserId = author.Id,
                Members =
                [
                    new InternalChatMember { UserId = author.Id, Role = "owner" },
                    new InternalChatMember { UserId = assignee.Id },
                    new InternalChatMember { UserId = observer.Id }
                ]
            });
            await winnerDb.SaveChangesAsync();
        });
        var contenderOptions = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).AddInterceptors(interceptor).Options;
        await using var contenderDb = new PaymentContext(contenderOptions);

        var result = await fixture.Controller(assignee, contenderDb).OpenTaskChat(task.Id, CancellationToken.None);
        var dto = Assert.IsType<InternalChatThreadDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(task.Id, dto.TaskJournalItemId);
        Assert.Equal(3, dto.MemberUserIds.Count);
        Assert.Equal(1, await winnerDb.InternalChatThreads.CountAsync(item => item.TaskJournalItemId == task.Id));
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
            MessageId = attachmentMessage.Id,
            FileName = "report.txt",
            StoredFileName = "report.txt",
            UploadedByUserId = author.Id,
            Size = 1
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
            Name = "Обсуждение",
            MemberUserIds = [colleague.Id]
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
    public async Task OpenDirect_UsesThreadCreatedByConcurrentRequest()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.AddUserAsync("direct-race-first@example.test", "Первый");
        var second = await fixture.AddUserAsync("direct-race-second@example.test", "Второй");
        var directKey = first.Id < second.Id ? $"{first.Id}:{second.Id}" : $"{second.Id}:{first.Id}";

        var connection = fixture.Db.Database.GetDbConnection();
        var winnerOptions = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options;
        await using var winnerDb = new PaymentContext(winnerOptions);
        var interceptor = new ConcurrentTaskChatCreationInterceptor(async () =>
        {
            winnerDb.InternalChatThreads.Add(new InternalChatThread
            {
                Kind = InternalChatThreadKinds.Direct,
                Name = "Первый / Второй",
                DirectKey = directKey,
                CreatedByUserId = second.Id,
                Members =
                [
                    new InternalChatMember { UserId = first.Id, Role = "member" },
                    new InternalChatMember { UserId = second.Id, Role = "member" }
                ]
            });
            await winnerDb.SaveChangesAsync();
        });
        var contenderOptions = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).AddInterceptors(interceptor).Options;
        await using var contenderDb = new PaymentContext(contenderOptions);

        var result = await fixture.Controller(first, contenderDb).OpenDirect(second.Id, CancellationToken.None);
        var dto = Assert.IsType<InternalChatThreadDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, dto.MemberUserIds.Count);
        Assert.Equal(1, await winnerDb.InternalChatThreads.CountAsync(item => item.DirectKey == directKey));
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
        var latestMessageId = await fixture.Db.InternalChatMessages.Where(item => item.ThreadId == channel.Id)
            .MaxAsync(item => item.Id);
        fixture.Db.InternalChatAttachments.AddRange(
            new InternalChatAttachment { MessageId = latestMessageId, FileName = "first.pdf", StoredFileName = "first-stored.pdf", UploadedByUserId = owner.Id },
            new InternalChatAttachment { MessageId = latestMessageId, FileName = "second.pdf", StoredFileName = "second-stored.pdf", UploadedByUserId = owner.Id });
        fixture.Db.InternalChatReactions.Add(new InternalChatReaction
        {
            MessageId = latestMessageId,
            UserId = owner.Id,
            Emoji = "👍"
        });
        fixture.Db.InternalChatMentions.Add(new InternalChatMention
        {
            MessageId = latestMessageId,
            UserId = owner.Id
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var firstResult = await fixture.Controller(owner).GetMessages(channel.Id, null, null, null, null, null, false, 50, CancellationToken.None);
        var first = Assert.IsType<InternalChatMessagePageDto>(Assert.IsType<OkObjectResult>(firstResult.Result).Value);
        Assert.Equal(50, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextBeforeId);
        var latest = first.Items[^1];
        Assert.Equal(latestMessageId, latest.Id);
        Assert.Equal(2, latest.Attachments.Count);
        Assert.Equal("👍", Assert.Single(latest.Reactions).Emoji);
        Assert.Contains(owner.Id, latest.MentionUserIds);

        var secondResult = await fixture.Controller(owner).GetMessages(channel.Id, first.NextBeforeId, null, null, null, null, false, 50, CancellationToken.None);
        var second = Assert.IsType<InternalChatMessagePageDto>(Assert.IsType<OkObjectResult>(secondResult.Result).Value);
        Assert.Equal(5, second.Items.Count);
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task UnreadMessages_ExposeReadCursorAndPageForwardWithoutSkippingMessages()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.AddUserAsync("unread-owner@example.test", "Автор");
        var reader = await fixture.AddUserAsync("unread-reader@example.test", "Читатель");
        var outsider = await fixture.AddUserAsync("unread-outsider@example.test", "Посторонний");
        var created = await fixture.Controller(owner).CreateChannel(new CreateInternalChatChannelRequest
        {
            Name = "Много непрочитанных",
            MemberUserIds = [reader.Id]
        }, CancellationToken.None);
        var channel = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value);
        fixture.Db.InternalChatMessages.AddRange(Enumerable.Range(1, 60).Select(index => new InternalChatMessage
        {
            ThreadId = channel.Id,
            AuthorUserId = owner.Id,
            Text = $"Сообщение {index}"
        }));
        await fixture.Db.SaveChangesAsync();
        var fifthId = await fixture.Db.InternalChatMessages.Where(item => item.ThreadId == channel.Id)
            .OrderBy(item => item.Id).Skip(4).Select(item => item.Id).FirstAsync();
        Assert.IsType<NoContentResult>(await fixture.Controller(reader).MarkRead(channel.Id,
            new MarkInternalChatReadRequest { MessageId = fifthId }, CancellationToken.None));

        var threads = Assert.IsAssignableFrom<IReadOnlyList<InternalChatThreadDto>>(Assert.IsType<OkObjectResult>(
            (await fixture.Controller(reader).GetThreads(CancellationToken.None)).Result).Value);
        var listed = Assert.Single(threads);
        Assert.Equal(fifthId, listed.LastReadMessageId);
        Assert.Equal(55, listed.UnreadCount);
        Assert.Equal(fifthId + 1, listed.FirstUnreadMessageId);

        var first = Assert.IsType<InternalChatMessagePageDto>(Assert.IsType<OkObjectResult>(
            (await fixture.Controller(reader).GetMessages(channel.Id, null, null, null, null, null, false, 50,
                CancellationToken.None, fifthId)).Result).Value);
        Assert.Equal(50, first.Items.Count);
        Assert.True(first.HasNewer);
        Assert.Equal("Сообщение 6", first.Items[0].Text);
        Assert.True(first.Items.Zip(first.Items.Skip(1)).All(pair => pair.First.Id < pair.Second.Id));

        var second = Assert.IsType<InternalChatMessagePageDto>(Assert.IsType<OkObjectResult>(
            (await fixture.Controller(reader).GetMessages(channel.Id, null, null, null, null, null, false, 50,
                CancellationToken.None, first.Items[^1].Id)).Result).Value);
        Assert.Equal(5, second.Items.Count);
        Assert.False(second.HasNewer);
        Assert.Equal("Сообщение 60", second.Items[^1].Text);
        Assert.IsType<BadRequestObjectResult>((await fixture.Controller(reader).GetMessages(channel.Id, fifthId,
            null, null, null, null, false, 50, CancellationToken.None, fifthId)).Result);
        Assert.IsType<ForbidResult>((await fixture.Controller(outsider).GetMessages(channel.Id, null,
            null, null, null, null, false, 50, CancellationToken.None, fifthId)).Result);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatSendReplayRequiresSameBytesBeyondFileNameAndSize(bool changedBytes)
    {
        const string prefix = "pp_chat_replay_bytes_";
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")));
        try
        {
            await using var fixture = await Fixture.CreateAsync();
            var author = await fixture.AddUserAsync("chat-replay-byte-author@example.test", "Автор");
            var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(
                (await fixture.Controller(author).CreateChannel(new CreateInternalChatChannelRequest
                { Name = "Повтор вложения" }, CancellationToken.None)).Result).Value);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:RootPath"] = root,
                ["Email:Enabled"] = "false",
                ["BackgroundServices:Enabled"] = "false"
            }).Build();
            using var originalStream = new MemoryStream([4, 5, 6]);
            using var replayStream = new MemoryStream(changedBytes ? new byte[] { 6, 5, 4 } : new byte[] { 4, 5, 6 });
            static IFormFile File(Stream stream) => new FormFile(stream, 0, 3, "file", "document.txt")
            { Headers = new HeaderDictionary(), ContentType = "text/plain" };
            var key = Guid.NewGuid();
            var original = new SendInternalChatMessageRequest { Text = "Готово", Files = [File(originalStream)], ClientRequestId = key };
            var replay = new SendInternalChatMessageRequest { Text = "Готово", Files = [File(replayStream)], ClientRequestId = key };
            var first = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
                (await fixture.Controller(author, configuration: configuration).SendMessage(thread.Id, original, CancellationToken.None)).Result).Value);
            var result = (await fixture.Controller(author, configuration: configuration).SendMessage(thread.Id, replay, CancellationToken.None)).Result;
            if (changedBytes) Assert.IsType<ConflictObjectResult>(result);
            else Assert.Equal(first.Id, Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(result).Value).Id);
            var saved = Assert.Single(await fixture.Db.InternalChatMessages.AsNoTracking().Include(message => message.Attachments)
                .Where(message => message.ThreadId == thread.Id).ToArrayAsync());
            var attachment = Assert.Single(saved.Attachments);
            var filesRoot = Path.Combine(root, "internal-chat-files", thread.Id.ToString());
            Assert.Equal(new byte[] { 4, 5, 6 }, await System.IO.File.ReadAllBytesAsync(Path.Combine(filesRoot, attachment.StoredFileName)));
            Assert.Single(Directory.EnumerateFiles(filesRoot));
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(root));
            Assert.StartsWith(prefix, Path.GetFileName(root), StringComparison.Ordinal);
            Assert.True(Guid.TryParseExact(Path.GetFileName(root)[prefix.Length..], "N", out _));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false, "exact")]
    [InlineData(true, "exact")]
    [InlineData(false, "changed")]
    [InlineData(true, "changed")]
    [InlineData(false, "managed")]
    [InlineData(true, "managed")]
    [InlineData(false, "managedChanged")]
    [InlineData(true, "managedChanged")]
    [InlineData(false, "unavailable")]
    [InlineData(true, "unavailable")]
    [InlineData(false, "deleted")]
    [InlineData(true, "deleted")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "longer")]
    [InlineData(true, "longer")]
    [InlineData(false, "outsider")]
    [InlineData(true, "outsider")]
    [InlineData(false, "revoked")]
    [InlineData(true, "revoked")]
    [InlineData(false, "deletedDuringRead")]
    [InlineData(true, "deletedDuringRead")]
    [InlineData(false, "badLength")]
    [InlineData(true, "badLength")]
    [InlineData(false, "shortBody")]
    [InlineData(true, "shortBody")]
    public async Task ChatReplayChecksBytesAndFreshAuthorityOnMigratedDatabase(bool postgres, string scenario)
    {
        const string prefix = "pp_chat_replay_boundaries_";
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")));
        try
        {
            await using var fixture = await Fixture.CreateMigratedAsync(postgres);
            var author = await fixture.AddUserAsync("replay-author@example.test", "Автор");
            var outsider = await fixture.AddUserAsync("replay-outsider@example.test", "Посторонний");
            var thread = Assert.IsType<InternalChatThreadDto>(Assert.IsType<CreatedAtActionResult>(
                (await fixture.Controller(author).CreateChannel(new CreateInternalChatChannelRequest
                { Name = "Проверка повтора" }, default)).Result).Value);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:RootPath"] = root,
                ["Email:Enabled"] = "false",
                ["BackgroundServices:Enabled"] = "false"
            }).Build();
            using var original = new MemoryStream([4, 5, 6]);
            using var replay = new MemoryStream(scenario is "changed" or "managedChanged" ? [6, 5, 4]
                : scenario == "shortBody" ? [4, 5] : [4, 5, 6]);
            static IFormFile File(Stream stream, long size = 3) => new FormFile(stream, 0, size, "file", "document.txt")
            { Headers = new HeaderDictionary(), ContentType = "text/plain" };
            var key = Guid.NewGuid();
            var first = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
                (await fixture.Controller(author, configuration: configuration).SendMessage(thread.Id,
                    new SendInternalChatMessageRequest { Text = "Готово", ClientRequestId = key, Files = [File(original)] }, default)).Result).Value);
            var attachment = await fixture.Db.InternalChatAttachments.SingleAsync();
            var directory = Path.Combine(root, "internal-chat-files", thread.Id.ToString());
            var path = Path.Combine(directory, attachment.StoredFileName);
            var before = await fixture.Db.InternalChatThreads.AsNoTracking().SingleAsync(item => item.Id == thread.Id);
            if (scenario == "missing") System.IO.File.Delete(path);
            if (scenario == "longer") await System.IO.File.WriteAllBytesAsync(path, [4, 5, 6, 7]);
            var status = scenario switch
            {
                "unavailable" => ProductStorageReadStatus.Unavailable,
                "deleted" => ProductStorageReadStatus.Deleted,
                _ => ProductStorageReadStatus.Available
            };
            var reader = new ReplayChatProductReader(status, scenario is "revoked" or "deletedDuringRead" ? async () =>
            {
                if (scenario == "revoked")
                    await fixture.Db.InternalChatMembers.Where(item => item.ThreadId == thread.Id && item.UserId == author.Id)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false));
                else
                {
                    var deletedAt = DateTime.UtcNow;
                    await fixture.Db.InternalChatMessages.Where(item => item.Id == first.Id)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.DeletedAt, deletedAt));
                }
            }
            : null);
            var useReader = scenario is "managed" or "managedChanged" or "unavailable" or "deleted" or "outsider" or "revoked" or "deletedDuringRead";
            var result = (await fixture.Controller(scenario == "outsider" ? outsider : author,
                configuration: configuration, productStorageReader: useReader ? reader : null).SendMessage(thread.Id,
                new SendInternalChatMessageRequest
                {
                    Text = "Готово",
                    ClientRequestId = key,
                    Files = [File(replay, scenario == "badLength" ? 4 : 3)]
                }, default)).Result;
            if (scenario is "changed" or "managedChanged" or "deletedDuringRead" or "badLength") Assert.IsType<ConflictObjectResult>(result);
            else if (scenario == "shortBody") Assert.IsType<BadRequestObjectResult>(result);
            else if (scenario is "outsider" or "revoked") Assert.IsType<ForbidResult>(result);
            else if (scenario is "unavailable" or "deleted" or "missing" or "longer")
                Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
            else Assert.Equal(first.Id, Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(result).Value).Id);
            Assert.Equal(useReader && scenario != "outsider" ? 1 : 0, reader.Calls);
            Assert.Equal(useReader && status == ProductStorageReadStatus.Available && scenario != "outsider" ? 1 : 0, reader.Disposals);
            Assert.Single(await fixture.Db.InternalChatMessages.AsNoTracking().ToArrayAsync());
            Assert.Single(await fixture.Db.InternalChatAttachments.AsNoTracking().ToArrayAsync());
            var after = await fixture.Db.InternalChatThreads.AsNoTracking().SingleAsync(item => item.Id == thread.Id);
            Assert.Equal(before.LastMessageAt, after.LastMessageAt);
            Assert.Equal(before.UpdatedAt, after.UpdatedAt);
            Assert.Equal(scenario == "missing" ? 0 : 1, Directory.EnumerateFiles(directory).Count());
            if (scenario != "missing") Assert.Equal(scenario == "longer" ? new byte[] { 4, 5, 6, 7 } : [4, 5, 6], await System.IO.File.ReadAllBytesAsync(path));
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(root));
            Assert.StartsWith(prefix, Path.GetFileName(root), StringComparison.Ordinal);
            Assert.True(Guid.TryParseExact(Path.GetFileName(root)[prefix.Length..], "N", out _));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class ReplayChatProductReader(ProductStorageReadStatus status, Func<Task>? onRead) : IProductStorageReader
    {
        public int Calls { get; private set; }
        public int Disposals { get; private set; }
        public async Task<ProductStorageReadResult> OpenReadAsync(string source, long fileId, string logicalName,
            string contentType, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(LegacyStorageSources.InternalChat, source);
            if (onRead is not null) await onRead();
            return new(status, status == ProductStorageReadStatus.Available
                ? new(LegacyStorageSources.GetObjectId(source, fileId), 1, contentType, logicalName, 3,
                    new DisposalObservedStream(() => Disposals++), "mirror") : null);
        }
    }

    private sealed class DisposalObservedStream(Action onDispose) : MemoryStream(new byte[] { 4, 5, 6 })
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) onDispose();
            base.Dispose(disposing);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection? _connection;
        private NpgsqlConnection? _admin;
        private string? _databaseName;
        private string? _sqliteRoot;
        private readonly Role _role;
        public PaymentContext Db { get; }

        public PaymentContext NewDb() => new(new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(_connection ?? throw new InvalidOperationException("This fixture has no shared SQLite connection.")).Options);

        private Fixture(SqliteConnection? connection, PaymentContext db, Role role)
        {
            _connection = connection;
            Db = db;
            _role = role;
        }

        public static async Task<Fixture> CreateAsync(SaveChangesInterceptor? interceptor = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var builder = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection);
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            var options = builder.Options;
            var db = new PaymentContext(options);
            await db.Database.EnsureCreatedAsync();
            var role = new Role { Name = "employee", Description = "Сотрудник" };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            return new Fixture(connection, db, role);
        }

        public static async Task<Fixture> CreateMigratedAsync(bool postgres, bool independentSqliteConnections = false, params IInterceptor[] interceptors)
        {
            SqliteConnection? sqlite = null;
            NpgsqlConnection? admin = null;
            string? name = null;
            string? sqliteRoot = null;
            PaymentContext? db = null;
            Fixture? fixture = null;
            try
            {
                if (postgres)
                {
                    var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
                    Assert.False(string.IsNullOrWhiteSpace(raw), "Chat replay requires owned local PostgreSQL.");
                    var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
                    Assert.Contains(settings.Host, new[] { "127.0.0.1", "localhost", "::1" });
                    admin = new NpgsqlConnection(settings.ConnectionString);
                    await admin.OpenAsync();
                    var candidate = "pp_chat_replay_" + Guid.NewGuid().ToString("N");
                    await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{candidate}\"", admin))
                        await create.ExecuteNonQueryAsync();
                    name = candidate;
                    settings.Database = name;
                    db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                        .UseNpgsql(settings.ConnectionString).AddInterceptors(interceptors).Options);
                }
                else
                {
                    if (independentSqliteConnections)
                    {
                        sqliteRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pp_chat_registry_" + Guid.NewGuid().ToString("N")));
                        Directory.CreateDirectory(sqliteRoot);
                    }
                    sqlite = new SqliteConnection(sqliteRoot is null ? "Data Source=:memory:"
                        : new SqliteConnectionStringBuilder { DataSource = Path.Combine(sqliteRoot, "owned.sqlite"), Pooling = false }.ConnectionString);
                    await sqlite.OpenAsync();
                    db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                        .UseSqlite(sqlite).UsePayPlannerSqliteMigrations().AddInterceptors(interceptors).Options);
                }
                var role = new Role { Name = "chat-replay-employee", Description = "Сотрудник" };
                fixture = new Fixture(sqlite, db, role) { _admin = admin, _databaseName = name, _sqliteRoot = sqliteRoot };
                await db.Database.MigrateAsync();
                Assert.False(db.Database.HasPendingModelChanges());
                var email = await db.SystemSettings.SingleOrDefaultAsync(item => item.Key == "notifications.email.enabled");
                if (email is null) db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
                else email.Value = "false";
                db.Roles.Add(role);
                await db.SaveChangesAsync();
                return fixture;
            }
            catch
            {
                if (fixture is not null) await fixture.DisposeAsync();
                else
                {
                    if (db is not null) await db.DisposeAsync();
                    if (sqlite is not null) await sqlite.DisposeAsync();
                    if (admin is not null)
                    {
                        try { if (name is not null) await DropOwnedAsync(admin, name); }
                        finally { await admin.DisposeAsync(); }
                    }
                    if (sqliteRoot is not null) RemoveOwnedSqliteRoot(sqliteRoot);
                }
                throw;
            }
        }

        private static async Task DropOwnedAsync(NpgsqlConnection admin, string name)
        {
            const string prefix = "pp_chat_replay_";
            Assert.StartsWith(prefix, name, StringComparison.Ordinal);
            Assert.True(Guid.TryParseExact(name[prefix.Length..], "N", out _));
            Assert.Contains(admin.Host, new[] { "127.0.0.1", "localhost", "::1" });
            Assert.Equal("postgres", admin.Database);
            await using var sessions = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @name", admin);
            sessions.Parameters.AddWithValue("name", name);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            long remaining;
            do
            {
                remaining = (long)(await sessions.ExecuteScalarAsync())!;
                if (remaining == 0) break;
                await Task.Delay(50);
            }
            while (DateTime.UtcNow < deadline);
            Assert.Equal(0L, remaining);
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\"", admin);
            await drop.ExecuteNonQueryAsync();
        }

        private static void RemoveOwnedSqliteRoot(string root)
        {
            const string prefix = "pp_chat_registry_";
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Path.GetFullPath(root)));
            Assert.StartsWith(prefix, Path.GetFileName(root), StringComparison.Ordinal);
            Assert.True(Guid.TryParseExact(Path.GetFileName(root)[prefix.Length..], "N", out _));
            if (Directory.Exists(root)) Directory.Delete(root, true);
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

        public InternalChatController Controller(User user, PaymentContext? db = null,
            IProductStorageReader? productStorageReader = null, IConfiguration? configuration = null,
            IRealtimeNotificationPublisher? realtime = null, INotificationService? notifications = null,
            IProductStorageDeletionService? productStorageDeletion = null,
            InternalChatOriginalWriter? chatWriter = null, IStoragePolicyService? chatPolicies = null,
            IProductStorageBindingService? chatBindings = null, StorageWorkerAvailability? storageWorkerAvailability = null)
        {
            notifications ??= DispatchProxy.Create<INotificationService, NotificationProxy>();
            var controller = new InternalChatController(db ?? Db, notifications, realtime ?? NoOpRealtimeNotificationPublisher.Instance,
                configuration: configuration, productStorageReader: productStorageReader,
                productStorageDeletion: productStorageDeletion, chatWriter: chatWriter, chatPolicies: chatPolicies,
                chatBindings: chatBindings, storageWorkerAvailability: storageWorkerAvailability)
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
            if (_connection is not null) await _connection.DisposeAsync();
            if (_admin is not null)
            {
                try { if (_databaseName is not null) await DropOwnedAsync(_admin, _databaseName); }
                finally { await _admin.DisposeAsync(); }
            }
            if (_sqliteRoot is not null) RemoveOwnedSqliteRoot(_sqliteRoot);
        }
    }

    private sealed class ConcurrentTaskChatCreationInterceptor(Func<Task> createWinner) : SaveChangesInterceptor
    {
        private bool _created;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_created && eventData.Context?.ChangeTracker.Entries<InternalChatThread>()
                    .Any(entry => entry.State == EntityState.Added) == true)
            {
                _created = true;
                await createWinner();
            }
            return result;
        }
    }

    private class NotificationProxy : DispatchProxy
    {
        public List<NotificationEventEnvelope> Events { get; } = [];
        public bool FailNext { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == nameof(INotificationService.NotifyAsync) && args?[0] is NotificationEventEnvelope envelope)
            {
                if (FailNext)
                {
                    FailNext = false;
                    throw new IOException("Имитирован сбой уведомления");
                }
                Events.Add(envelope);
            }
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
