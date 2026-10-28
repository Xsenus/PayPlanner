using System.Security.Claims;
using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Hubs;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StickyNotesControllerTests
{
    [Fact]
    public async Task GetBoards_CreatesPersonalAndSharedBoards()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db);
        var controller = CreateController(db, user);

        var result = await controller.GetBoards(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var boards = Assert.IsAssignableFrom<IReadOnlyCollection<StickyNoteBoardDto>>(ok.Value);
        Assert.Contains(boards, board => board.Visibility == StickyNoteBoardVisibility.Personal && board.OwnerUserId == user.Id);
        Assert.Contains(boards, board => board.Visibility == StickyNoteBoardVisibility.Shared);
    }

    [Fact]
    public async Task CreateNote_RequiresCreatePermission()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, canCreate: false);
        var board = new StickyNoteBoard
        {
            Title = "Общее",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork",
        };
        db.StickyNoteBoards.Add(board);
        await db.SaveChangesAsync();

        var controller = CreateController(db, user);

        var result = await controller.CreateNote(board.Id, new CreateStickyNoteRequest { Text = "Нельзя создать" }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(await db.StickyNotes.ToListAsync());
    }

    [Fact]
    public async Task GetBoards_ReturnsCreateNotePermissionSeparately()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, canCreate: false);
        var board = new StickyNoteBoard
        {
            Title = "Общее",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork",
        };
        db.StickyNoteBoards.Add(board);
        await db.SaveChangesAsync();

        var controller = CreateController(db, user);

        var result = await controller.GetBoards(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var boards = Assert.IsAssignableFrom<IReadOnlyCollection<StickyNoteBoardDto>>(ok.Value);
        var dto = Assert.Single(boards, item => item.Id == board.Id);
        Assert.True(dto.CanEdit);
        Assert.False(dto.CanCreateNotes);
    }

    [Fact]
    public async Task GetBoards_ReturnsDeleteBoardPermissionOnlyForOwner()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db);
        var editor = await CreateUserAsync(db);
        var board = new StickyNoteBoard
        {
            Title = "Личная владельца",
            Visibility = StickyNoteBoardVisibility.Personal,
            OwnerUserId = owner.Id,
            OwnerUserName = owner.FullName,
            Background = "gray",
            Members = new List<StickyNoteBoardMember>
            {
                new() { UserId = editor.Id, Role = "editor" },
            },
        };
        db.StickyNoteBoards.Add(board);
        await db.SaveChangesAsync();

        var ownerResult = await CreateController(db, owner).GetBoards(CancellationToken.None);
        var ownerBoards = Assert.IsAssignableFrom<IReadOnlyCollection<StickyNoteBoardDto>>(Assert.IsType<OkObjectResult>(ownerResult.Result).Value);
        var ownerDto = Assert.Single(ownerBoards, item => item.Id == board.Id);
        Assert.True(ownerDto.CanDelete);
        Assert.True(ownerDto.CanDeleteNotes);
        Assert.True(ownerDto.CanUpdateSettings);

        var editorResult = await CreateController(db, editor).GetBoards(CancellationToken.None);
        var editorBoards = Assert.IsAssignableFrom<IReadOnlyCollection<StickyNoteBoardDto>>(Assert.IsType<OkObjectResult>(editorResult.Result).Value);
        var editorDto = Assert.Single(editorBoards, item => item.Id == board.Id);
        Assert.True(editorDto.CanEdit);
        Assert.False(editorDto.CanDelete);
        Assert.True(editorDto.CanDeleteNotes);
        Assert.False(editorDto.CanUpdateSettings);
    }

    [Fact]
    public async Task GetMemberCandidates_RequiresEditPermission()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, canEdit: false);
        var controller = CreateController(db, user);

        var result = await controller.GetMemberCandidates(CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetMemberCandidates_ReturnsOnlyUsersWithNotesViewPermission()
    {
        await using var db = await CreateContextAsync();
        var manager = await CreateUserAsync(db);
        var allowedUser = await CreateUserAsync(db);
        var deniedUser = await CreateUserAsync(db, canView: false);
        var controller = CreateController(db, manager);

        var result = await controller.GetMemberCandidates(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var candidates = Assert.IsAssignableFrom<IReadOnlyCollection<StickyNoteBoardMemberCandidateDto>>(ok.Value);
        Assert.Contains(candidates, candidate => candidate.UserId == manager.Id);
        Assert.Contains(candidates, candidate => candidate.UserId == allowedUser.Id);
        Assert.DoesNotContain(candidates, candidate => candidate.UserId == deniedUser.Id);
    }

    [Fact]
    public async Task CreateNote_ReturnsConflict_WhenBoardLimitReached()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db);
        var board = new StickyNoteBoard
        {
            Title = "Лимит",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork",
        };
        db.StickyNoteBoards.Add(board);
        await db.SaveChangesAsync();

        for (var index = 0; index < 200; index++)
        {
            db.StickyNotes.Add(new StickyNote
            {
                BoardId = board.Id,
                Text = $"Заметка {index}",
                Color = "yellow",
                Version = 1,
                Width = 220,
                Height = 180,
            });
        }
        await db.SaveChangesAsync();

        var controller = CreateController(db, user);

        var result = await controller.CreateNote(board.Id, new CreateStickyNoteRequest { Text = "Лишняя" }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(200, await db.StickyNotes.CountAsync(item => item.BoardId == board.Id));
    }

    [Fact]
    public async Task CreateNote_PublishesRealtimePayloadForTheSameBoard()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db);
        var board = new StickyNoteBoard
        {
            Title = "Общее",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork",
        };
        db.StickyNoteBoards.Add(board);
        await db.SaveChangesAsync();

        var hub = new FakeNotesHubContext();
        var controller = CreateController(db, user, hub);

        var result = await controller.CreateNote(board.Id, new CreateStickyNoteRequest { Text = "Проверить событие" }, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<StickyNoteDto>(created.Value);
        Assert.Equal(NotesHub.BoardChangedEvent, hub.LastMethodName);
        var realtimeEvent = Assert.IsType<StickyNoteRealtimeEventDto>(Assert.Single(hub.LastArguments));
        Assert.Equal("noteCreated", realtimeEvent.Type);
        Assert.Equal(board.Id, realtimeEvent.BoardId);
        Assert.NotNull(realtimeEvent.Note);
        Assert.Equal(board.Id, realtimeEvent.Note.BoardId);
        Assert.Equal(dto.Id, realtimeEvent.Note.Id);
        Assert.Equal(dto.Id, realtimeEvent.NoteId);
    }

    [Fact]
    public async Task CreateAndUpdateNote_PersistsAppearanceSettings()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db);
        var board = new StickyNoteBoard
        {
            Title = "Оформление",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork",
        };
        db.StickyNoteBoards.Add(board);
        await db.SaveChangesAsync();
        var controller = CreateController(db, user);

        var createResult = await controller.CreateNote(board.Id, new CreateStickyNoteRequest
        {
            Text = "Выделить важное",
            Color = "indigo",
            FontSize = 99,
            IsBold = true,
            IsItalic = true,
            IsUnderline = true,
            Opacity = 1,
        }, CancellationToken.None);

        var created = Assert.IsType<StickyNoteDto>(Assert.IsType<CreatedAtActionResult>(createResult.Result).Value);
        Assert.Equal("indigo", created.Color);
        Assert.Equal(28, created.FontSize);
        Assert.True(created.IsBold);
        Assert.True(created.IsItalic);
        Assert.True(created.IsUnderline);
        Assert.Equal(60, created.Opacity);

        var updateResult = await controller.UpdateNote(created.Id, new UpdateStickyNoteRequest
        {
            Text = created.Text,
            Color = "teal",
            X = created.X,
            Y = created.Y,
            Width = 300,
            Height = 240,
            FontSize = 12,
            IsBold = false,
            IsItalic = false,
            IsUnderline = false,
            Opacity = 85,
            ZIndex = created.ZIndex,
            Version = created.Version,
        }, CancellationToken.None);

        var updated = Assert.IsType<StickyNoteDto>(Assert.IsType<OkObjectResult>(updateResult.Result).Value);
        Assert.Equal("teal", updated.Color);
        Assert.Equal(12, updated.FontSize);
        Assert.False(updated.IsBold);
        Assert.False(updated.IsItalic);
        Assert.False(updated.IsUnderline);
        Assert.Equal(85, updated.Opacity);
        Assert.Equal(300, updated.Width);
        Assert.Equal(240, updated.Height);
    }

    [Fact]
    public async Task CreateNote_WithMention_NotifiesMentionedVisibleUser()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db);
        var mentioned = await CreateUserAsync(db);
        mentioned.Email = "anna.notes@example.com";
        mentioned.FullName = "Анна Заметки";
        var other = await CreateUserAsync(db);
        other.Email = "other.notes@example.com";
        other.FullName = "Другой сотрудник";
        var board = new StickyNoteBoard
        {
            Title = "Общее",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork",
        };
        db.StickyNoteBoards.Add(board);
        await db.SaveChangesAsync();
        var controller = CreateController(db, author, notificationService: CreateNotificationService(db));

        var result = await controller.CreateNote(
            board.Id,
            new CreateStickyNoteRequest { Text = $"@{mentioned.Email} проверь, пожалуйста" },
            CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<StickyNoteDto>(created.Value);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(mentioned.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.NoteMention, notification.Type);
        Assert.Equal("note", notification.ObjectType);
        Assert.Equal(dto.Id, notification.ObjectId);
        Assert.Contains("Вас упомянули", notification.Title);
        Assert.Contains("Общее", notification.BodyPreview);
        Assert.DoesNotContain(await db.UserNotifications.ToListAsync(), item => item.RecipientUserId == other.Id);
    }

    [Fact]
    public async Task CreateNote_WithMention_DoesNotNotifyUserWithoutBoardAccess()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db);
        var outsider = await CreateUserAsync(db);
        outsider.Email = "outsider.notes@example.com";
        outsider.FullName = "Внешний сотрудник";
        var board = new StickyNoteBoard
        {
            Title = "Личная",
            Visibility = StickyNoteBoardVisibility.Personal,
            OwnerUserId = owner.Id,
            OwnerUserName = owner.FullName,
            Background = "gray",
        };
        db.StickyNoteBoards.Add(board);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner, notificationService: CreateNotificationService(db));

        var result = await controller.CreateNote(
            board.Id,
            new CreateStickyNoteRequest { Text = $"@{outsider.Email} это личная доска" },
            CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Empty(await db.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task CreateNote_WithAssignment_StoresReminderAndNotifiesAssignee()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db);
        var assignee = await CreateUserAsync(db);
        assignee.FullName = "Ответственный сотрудник";
        var board = new StickyNoteBoard
        {
            Title = "Общее",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork",
        };
        db.StickyNoteBoards.Add(board);
        await db.SaveChangesAsync();
        var reminderAt = new DateTime(2026, 6, 17, 8, 30, 0, DateTimeKind.Utc);
        var controller = CreateController(db, author, notificationService: CreateNotificationService(db));

        var result = await controller.CreateNote(
            board.Id,
            new CreateStickyNoteRequest
            {
                Text = "Позвонить клиенту",
                AssignedUserId = assignee.Id,
                ReminderAt = reminderAt,
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<StickyNoteDto>(created.Value);
        Assert.Equal(assignee.Id, dto.AssignedUserId);
        Assert.Equal("Ответственный сотрудник", dto.AssignedUserName);
        Assert.Equal(reminderAt, dto.ReminderAt);
        Assert.Null(dto.ReminderSentAt);

        var storedNote = await db.StickyNotes.AsNoTracking().SingleAsync(item => item.Id == dto.Id);
        Assert.Equal(assignee.Id, storedNote.AssignedUserId);
        Assert.Equal("Ответственный сотрудник", storedNote.AssignedUserName);
        Assert.Equal(reminderAt, storedNote.ReminderAt);
        Assert.Null(storedNote.ReminderSentAt);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(assignee.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.NoteAssigned, notification.Type);
        Assert.Equal("note", notification.ObjectType);
        Assert.Equal(dto.Id, notification.ObjectId);
        Assert.Contains("Общее", notification.BodyPreview);
    }

    [Fact]
    public async Task CreateNote_WithReminderWithoutAssignee_ReturnsBadRequest()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db);
        var board = new StickyNoteBoard
        {
            Title = "Общее",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork",
        };
        db.StickyNoteBoards.Add(board);
        await db.SaveChangesAsync();
        var controller = CreateController(db, author, notificationService: CreateNotificationService(db));

        var result = await controller.CreateNote(
            board.Id,
            new CreateStickyNoteRequest
            {
                Text = "Напоминание без ответственного",
                ReminderAt = DateTime.UtcNow.AddHours(1),
            },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("ответственного", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await db.StickyNotes.ToListAsync());
        Assert.Empty(await db.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task UpdateNote_AssignmentChangeResetsReminderSentAtAndNotifiesAssignee()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db);
        var assignee = await CreateUserAsync(db);
        assignee.FullName = "Новый ответственный";
        var board = new StickyNoteBoard
        {
            Title = "Общее",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork",
        };
        var note = new StickyNote
        {
            Board = board,
            Text = "Старая заметка",
            Color = "yellow",
            Version = 1,
            Width = 220,
            Height = 180,
            ReminderSentAt = DateTime.UtcNow.AddMinutes(-5),
        };
        db.StickyNotes.Add(note);
        await db.SaveChangesAsync();
        var reminderAt = new DateTime(2026, 6, 18, 7, 15, 0, DateTimeKind.Utc);
        var controller = CreateController(db, author, notificationService: CreateNotificationService(db));

        var result = await controller.UpdateNote(
            note.Id,
            new UpdateStickyNoteRequest
            {
                Text = "Обновленная заметка",
                Color = "green",
                X = 10,
                Y = 20,
                Width = 230,
                Height = 190,
                ZIndex = 2,
                Version = 1,
                AssignedUserId = assignee.Id,
                ReminderAt = reminderAt,
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<StickyNoteDto>(ok.Value);
        Assert.Equal(assignee.Id, dto.AssignedUserId);
        Assert.Equal("Новый ответственный", dto.AssignedUserName);
        Assert.Equal(reminderAt, dto.ReminderAt);
        Assert.Null(dto.ReminderSentAt);

        var storedNote = await db.StickyNotes.AsNoTracking().SingleAsync(item => item.Id == note.Id);
        Assert.Equal(assignee.Id, storedNote.AssignedUserId);
        Assert.Equal("Новый ответственный", storedNote.AssignedUserName);
        Assert.Equal(reminderAt, storedNote.ReminderAt);
        Assert.Null(storedNote.ReminderSentAt);

        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(assignee.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.NoteAssigned, notification.Type);
    }

    [Fact]
    public async Task StickyNoteReminderNotificationService_RunOnce_SendsDueReminderOnce()
    {
        await using var db = await CreateContextAsync();
        var assignee = await CreateUserAsync(db);
        var board = new StickyNoteBoard
        {
            Title = "Общее",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork",
        };
        var dueNote = new StickyNote
        {
            Board = board,
            Text = "Проверить документы",
            Color = "yellow",
            Version = 1,
            Width = 220,
            Height = 180,
            AssignedUserId = assignee.Id,
            AssignedUserName = assignee.FullName,
            ReminderAt = DateTime.UtcNow.AddMinutes(-1),
        };
        db.StickyNotes.Add(dueNote);
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<INotificationService>(CreateNotificationService(db));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Notifications:StickyNoteReminders:PollSeconds"] = "15",
                ["Notifications:StickyNoteReminders:BatchSize"] = "10",
            })
            .Build());
        using var provider = services.BuildServiceProvider();
        var service = new StickyNoteReminderNotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IConfiguration>(),
            NullLogger<StickyNoteReminderNotificationService>.Instance);

        var sent = await service.RunOnceAsync(CancellationToken.None);
        var duplicate = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, sent);
        Assert.Equal(0, duplicate);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(assignee.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.NoteReminder, notification.Type);
        Assert.Equal("note", notification.ObjectType);
        Assert.Equal(dueNote.Id, notification.ObjectId);
        Assert.NotNull(await db.StickyNotes
            .Where(item => item.Id == dueNote.Id)
            .Select(item => item.ReminderSentAt)
            .SingleAsync());
    }

    [Fact]
    public async Task StickyReminder_ConcurrentNoteEditPreservesMarkerAndDoesNotDuplicateDelivery()
    {
        var settings = new SqliteConnectionStringBuilder { DataSource = "pp_sticky_reminder_" + Guid.NewGuid().ToString("N"), Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared, Pooling = false };
        await using var keeper = new SqliteConnection(settings.ConnectionString); await keeper.OpenAsync();
        DbContextOptions<PaymentContext> Options(SaveChangesInterceptor? interceptor = null)
        {
            var builder = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(settings.ConnectionString, sqlite => sqlite.MigrationsAssembly(typeof(PaymentContext).Assembly.FullName));
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            return builder.Options;
        }
        await using var setup = new PaymentContext(Options()); await setup.Database.EnsureCreatedAsync();
        var user = await CreateUserAsync(setup);
        var when = DateTime.UtcNow.AddMinutes(-1);
        var note = new StickyNote { Board = new StickyNoteBoard { Title = "Общая", Visibility = StickyNoteBoardVisibility.Shared, Background = "cork" }, Text = "Исходный текст", Color = "yellow", Version = 1, Width = 220, Height = 180, AssignedUserId = user.Id, AssignedUserName = user.FullName, ReminderAt = when };
        setup.StickyNotes.Add(note); await setup.SaveChangesAsync();
        var gate = new PauseReminderSave();
        await using var reminderDb = new PaymentContext(Options(gate));
        await using var editorDb = new PaymentContext(Options());
        var services = new ServiceCollection(); services.AddSingleton(reminderDb); services.AddSingleton<INotificationService>(CreateNotificationService(reminderDb)); services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var service = new StickyNoteReminderNotificationService(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IConfiguration>(), NullLogger<StickyNoteReminderNotificationService>.Instance);
        var pending = service.RunOnceAsync();
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            var request = new UpdateStickyNoteRequest { Text = "Уточнённый текст", Color = "yellow", Width = 220, Height = 180, ZIndex = 1, Version = 1, AssignedUserId = user.Id, ReminderAt = when };
            Assert.IsType<OkObjectResult>((await CreateController(editorDb, user).UpdateNote(note.Id, request, CancellationToken.None)).Result);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(1, await pending.WaitAsync(TimeSpan.FromSeconds(15)));
        reminderDb.ChangeTracker.Clear();
        Assert.Equal(0, await service.RunOnceAsync());
        setup.ChangeTracker.Clear();
        var saved = await setup.StickyNotes.AsNoTracking().SingleAsync(item => item.Id == note.Id);
        Assert.Equal("Уточнённый текст", saved.Text);
        Assert.NotNull(saved.ReminderSentAt);
        Assert.Single(await setup.UserNotifications.Where(item => item.Type == UserNotificationTypes.NoteReminder).ToListAsync());
    }

    [Fact]
    public async Task StickyReminder_ConcurrentRescheduleDoesNotMarkNewReminderAsSent()
    {
        var settings = new SqliteConnectionStringBuilder { DataSource = "pp_sticky_reminder_" + Guid.NewGuid().ToString("N"), Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared, Pooling = false };
        await using var keeper = new SqliteConnection(settings.ConnectionString); await keeper.OpenAsync();
        DbContextOptions<PaymentContext> Options(SaveChangesInterceptor? interceptor = null)
        {
            var builder = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(settings.ConnectionString, sqlite => sqlite.MigrationsAssembly(typeof(PaymentContext).Assembly.FullName));
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            return builder.Options;
        }
        await using var setup = new PaymentContext(Options()); await setup.Database.EnsureCreatedAsync();
        var user = await CreateUserAsync(setup);
        var when = DateTime.UtcNow.AddMinutes(-1);
        var note = new StickyNote { Board = new StickyNoteBoard { Title = "Общая", Visibility = StickyNoteBoardVisibility.Shared, Background = "cork" }, Text = "Исходный текст", Color = "yellow", Version = 1, Width = 220, Height = 180, AssignedUserId = user.Id, AssignedUserName = user.FullName, ReminderAt = when };
        setup.StickyNotes.Add(note); await setup.SaveChangesAsync();
        var gate = new PauseReminderSave();
        await using var reminderDb = new PaymentContext(Options(gate));
        await using var editorDb = new PaymentContext(Options());
        var services = new ServiceCollection(); services.AddSingleton(reminderDb); services.AddSingleton<INotificationService>(CreateNotificationService(reminderDb)); services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var service = new StickyNoteReminderNotificationService(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IConfiguration>(), NullLogger<StickyNoteReminderNotificationService>.Instance);
        var pending = service.RunOnceAsync();
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            var request = new UpdateStickyNoteRequest { Text = "Уточнённый текст", Color = "yellow", Width = 220, Height = 180, ZIndex = 1, Version = 1, AssignedUserId = user.Id, ReminderAt = when.AddDays(2) };
            Assert.IsType<OkObjectResult>((await CreateController(editorDb, user).UpdateNote(note.Id, request, CancellationToken.None)).Result);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(1, await pending.WaitAsync(TimeSpan.FromSeconds(15)));
        reminderDb.ChangeTracker.Clear();
        Assert.Equal(0, await service.RunOnceAsync());
        setup.ChangeTracker.Clear();
        var saved = await setup.StickyNotes.AsNoTracking().SingleAsync(item => item.Id == note.Id);
        Assert.Equal("Уточнённый текст", saved.Text);
        Assert.Null(saved.ReminderSentAt);
        Assert.Equal(when.AddDays(2), saved.ReminderAt);
        Assert.Single(await setup.UserNotifications.Where(item => item.Type == UserNotificationTypes.NoteReminder).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StickyReminder_PostgreSqlConcurrentEditPreservesDeliveryAndReschedule(bool reschedule)
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var lease = await template.CreateDatabaseAsync();
        DbContextOptions<PostgreSqlPaymentContext> Options(SaveChangesInterceptor? interceptor = null)
        {
            var builder = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString);
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            return builder.Options;
        }
        await using var setup = new PostgreSqlPaymentContext(Options());
        var user = await CreateUserAsync(setup);
        var initial = DateTime.UtcNow.AddMinutes(-1);
        var when = new DateTime(initial.Ticks - initial.Ticks % 10, DateTimeKind.Utc);
        var note = new StickyNote { Board = new StickyNoteBoard { Title = "Общая", Visibility = StickyNoteBoardVisibility.Shared, Background = "cork" }, Text = "Исходный текст", Color = "yellow", Version = 1, Width = 220, Height = 180, AssignedUserId = user.Id, AssignedUserName = user.FullName, ReminderAt = when };
        setup.StickyNotes.Add(note); await setup.SaveChangesAsync();
        var gate = new PauseReminderSave();
        await using var reminderDb = new PostgreSqlPaymentContext(Options(gate));
        await using var editorDb = new PostgreSqlPaymentContext(Options());
        var services = new ServiceCollection(); services.AddSingleton<PaymentContext>(reminderDb); services.AddSingleton<INotificationService>(CreateNotificationService(reminderDb)); services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var service = new StickyNoteReminderNotificationService(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IConfiguration>(), NullLogger<StickyNoteReminderNotificationService>.Instance);
        var pending = service.RunOnceAsync(); await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            var request = new UpdateStickyNoteRequest { Text = "Уточнённый текст", Color = "yellow", Width = 220, Height = 180, ZIndex = 1, Version = 1, AssignedUserId = user.Id, ReminderAt = reschedule ? when.AddDays(2) : when };
            Assert.IsType<OkObjectResult>((await CreateController(editorDb, user).UpdateNote(note.Id, request, CancellationToken.None)).Result);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(1, await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        reminderDb.ChangeTracker.Clear(); Assert.Equal(0, await service.RunOnceAsync());
        setup.ChangeTracker.Clear();
        var saved = await setup.StickyNotes.AsNoTracking().SingleAsync(item => item.Id == note.Id);
        Assert.Equal("Уточнённый текст", saved.Text);
        if (reschedule) { Assert.Null(saved.ReminderSentAt); Assert.Equal(when.AddDays(2), saved.ReminderAt); }
        else Assert.NotNull(saved.ReminderSentAt);
        Assert.Single(await setup.UserNotifications.Where(item => item.Type == UserNotificationTypes.NoteReminder).ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StickyReminder_PostgreSqlConcurrentDeleteOrReassignmentDoesNotMarkWrongReminder(bool delete)
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var lease = await template.CreateDatabaseAsync();
        DbContextOptions<PostgreSqlPaymentContext> Options(SaveChangesInterceptor? interceptor = null)
        {
            var builder = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString);
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            return builder.Options;
        }
        await using var setup = new PostgreSqlPaymentContext(Options());
        var user = await CreateUserAsync(setup); var other = await CreateUserAsync(setup);
        var initial = DateTime.UtcNow.AddMinutes(-1);
        var when = new DateTime(initial.Ticks - initial.Ticks % 10, DateTimeKind.Utc);
        var note = new StickyNote { Board = new StickyNoteBoard { Title = "Общая", Visibility = StickyNoteBoardVisibility.Shared, Background = "cork" }, Text = "Исходный текст", Color = "yellow", Version = 1, Width = 220, Height = 180, AssignedUserId = user.Id, AssignedUserName = user.FullName, ReminderAt = when };
        setup.StickyNotes.Add(note); await setup.SaveChangesAsync();
        var gate = new PauseReminderSave();
        await using var reminderDb = new PostgreSqlPaymentContext(Options(gate));
        await using var editorDb = new PostgreSqlPaymentContext(Options());
        var services = new ServiceCollection(); services.AddSingleton<PaymentContext>(reminderDb); services.AddSingleton<INotificationService>(CreateNotificationService(reminderDb)); services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var service = new StickyNoteReminderNotificationService(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IConfiguration>(), NullLogger<StickyNoteReminderNotificationService>.Instance);
        var pending = service.RunOnceAsync(); await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            var controller = CreateController(editorDb, user);
            if (delete) Assert.IsType<NoContentResult>(await controller.DeleteNote(note.Id, CancellationToken.None));
            else
            {
                var request = new UpdateStickyNoteRequest { Text = "Переданная задача", Color = "yellow", Width = 220, Height = 180, ZIndex = 1, Version = 1, AssignedUserId = other.Id, ReminderAt = when };
                Assert.IsType<OkObjectResult>((await controller.UpdateNote(note.Id, request, CancellationToken.None)).Result);
            }
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(1, await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        setup.ChangeTracker.Clear();
        var saved = await setup.StickyNotes.IgnoreQueryFilters().SingleAsync(item => item.Id == note.Id);
        Assert.Null(saved.ReminderSentAt);
        Assert.Equal(delete, setup.Entry(saved).Property<bool>("IsDeleted").CurrentValue);
        reminderDb.ChangeTracker.Clear(); Assert.Equal(delete ? 0 : 1, await service.RunOnceAsync());
        setup.ChangeTracker.Clear();
        var notices = await setup.UserNotifications.Where(item => item.Type == UserNotificationTypes.NoteReminder).ToListAsync();
        Assert.Equal(delete ? 1 : 2, notices.Count);
        Assert.Contains(notices, item => item.RecipientUserId == user.Id);
        if (!delete) Assert.Contains(notices, item => item.RecipientUserId == other.Id);
    }

    private sealed class PauseReminderSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<StickyNote>().Any(entry => entry.State == EntityState.Modified && entry.Property(item => item.ReminderSentAt).IsModified))
            {
                Reached.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task StickyNoteReminderNotificationService_RunOnce_UsesSingleCandidateQueryForDeniedReminder()
    {
        var commandInterceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(commandInterceptor);
        var assignee = await CreateUserAsync(db, canView: false);
        var board = new StickyNoteBoard
        {
            Title = "Общее",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork",
        };
        var dueNote = new StickyNote
        {
            Board = board,
            Text = "Недоступное напоминание",
            Color = "yellow",
            Version = 1,
            Width = 220,
            Height = 180,
            AssignedUserId = assignee.Id,
            AssignedUserName = assignee.FullName,
            ReminderAt = DateTime.UtcNow.AddMinutes(-1),
        };
        db.StickyNotes.Add(dueNote);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        commandInterceptor.ReaderCommands.Clear();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<INotificationService>(CreateNotificationService(db));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var service = new StickyNoteReminderNotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IConfiguration>(),
            NullLogger<StickyNoteReminderNotificationService>.Instance);

        var sent = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, sent);
        var candidateCommand = Assert.Single(
            commandInterceptor.ReaderCommands,
            command => command.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("RolePermissions", candidateCommand, StringComparison.Ordinal);
        Assert.Contains("StickyNoteBoardMembers", candidateCommand, StringComparison.Ordinal);
        Assert.Contains("EXISTS", candidateCommand, StringComparison.OrdinalIgnoreCase);
        var trackedNote = Assert.Single(db.ChangeTracker.Entries<StickyNote>(), entry => entry.Entity.Id == dueNote.Id);
        Assert.NotNull(trackedNote.Entity.ReminderSentAt);
        Assert.Empty(db.UserNotifications.Local);
    }

    [Fact]
    public async Task StickyNoteReminderNotificationService_RunOnce_NotifiesPersonalBoardMember()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db);
        var assignee = await CreateUserAsync(db);
        var board = new StickyNoteBoard
        {
            Title = "Личная доска",
            Visibility = StickyNoteBoardVisibility.Personal,
            OwnerUserId = owner.Id,
            OwnerUserName = owner.FullName,
            Background = "cork",
            Members =
            [
                new StickyNoteBoardMember
                {
                    UserId = assignee.Id,
                    Role = "editor",
                },
            ],
        };
        var dueNote = new StickyNote
        {
            Board = board,
            Text = "Напоминание участнику",
            Color = "yellow",
            Version = 1,
            Width = 220,
            Height = 180,
            AssignedUserId = assignee.Id,
            AssignedUserName = assignee.FullName,
            ReminderAt = DateTime.UtcNow.AddMinutes(-1),
        };
        db.StickyNotes.Add(dueNote);
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<INotificationService>(CreateNotificationService(db));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var service = new StickyNoteReminderNotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IConfiguration>(),
            NullLogger<StickyNoteReminderNotificationService>.Instance);

        var sent = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, sent);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(assignee.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.NoteReminder, notification.Type);
    }

    [Fact]
    public async Task StickyNoteReminderNotificationService_RunOnce_AdminBypassesSectionPermission()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, canView: false);
        admin.Role!.Name = "ADMIN";
        var dueNote = new StickyNote
        {
            Board = new StickyNoteBoard
            {
                Title = "Общее",
                Visibility = StickyNoteBoardVisibility.Shared,
                Background = "cork",
            },
            Text = "Напоминание администратору",
            Color = "yellow",
            Version = 1,
            Width = 220,
            Height = 180,
            AssignedUserId = admin.Id,
            AssignedUserName = admin.FullName,
            ReminderAt = DateTime.UtcNow.AddMinutes(-1),
        };
        db.StickyNotes.Add(dueNote);
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<INotificationService>(CreateNotificationService(db));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var service = new StickyNoteReminderNotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IConfiguration>(),
            NullLogger<StickyNoteReminderNotificationService>.Instance);

        var sent = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, sent);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(admin.Id, notification.RecipientUserId);
    }

    [Fact]
    public async Task UpdateNote_PinOnSharedBoard_NotifiesVisibleUsersOnly()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db);
        var recipient = await CreateUserAsync(db);
        var deniedUser = await CreateUserAsync(db, canView: false);
        var board = new StickyNoteBoard
        {
            Title = "Общее",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork",
        };
        var note = new StickyNote
        {
            Board = board,
            Text = "Важная информация",
            Color = "yellow",
            Version = 1,
            Width = 220,
            Height = 180,
        };
        db.StickyNotes.Add(note);
        await db.SaveChangesAsync();
        var controller = CreateController(db, author, notificationService: CreateNotificationService(db));

        var result = await controller.UpdateNote(note.Id, new UpdateStickyNoteRequest
        {
            Text = "Важная информация",
            Color = "yellow",
            X = 10,
            Y = 20,
            Width = 220,
            Height = 180,
            ZIndex = 2,
            IsPinned = true,
            Version = 1,
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.NoteImportantChanged, notification.Type);
        Assert.Equal("note", notification.ObjectType);
        Assert.Equal(note.Id, notification.ObjectId);
        Assert.Contains("закрепил", notification.BodyPreview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(await db.UserNotifications.ToListAsync(), item => item.RecipientUserId == author.Id);
        Assert.DoesNotContain(await db.UserNotifications.ToListAsync(), item => item.RecipientUserId == deniedUser.Id);
    }

    [Fact]
    public async Task UpdateNote_PinnedPersonalBoard_DoesNotNotifyMembers()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db);
        var member = await CreateUserAsync(db);
        var board = new StickyNoteBoard
        {
            Title = "Личная",
            Visibility = StickyNoteBoardVisibility.Personal,
            OwnerUserId = owner.Id,
            OwnerUserName = owner.FullName,
            Background = "gray",
            Members = new List<StickyNoteBoardMember>
            {
                new() { UserId = member.Id, Role = "editor" },
            },
        };
        var note = new StickyNote
        {
            Board = board,
            Text = "Личная важная заметка",
            Color = "yellow",
            IsPinned = true,
            Version = 1,
            Width = 220,
            Height = 180,
        };
        db.StickyNotes.Add(note);
        await db.SaveChangesAsync();
        var controller = CreateController(db, owner, notificationService: CreateNotificationService(db));

        var result = await controller.UpdateNote(note.Id, new UpdateStickyNoteRequest
        {
            Text = "Измененная личная заметка",
            Color = "yellow",
            X = 10,
            Y = 20,
            Width = 220,
            Height = 180,
            ZIndex = 2,
            IsPinned = true,
            Version = 1,
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(await db.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task DeleteNote_PinnedSharedBoardNote_NotifiesVisibleUsers()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db);
        var recipient = await CreateUserAsync(db);
        var board = new StickyNoteBoard
        {
            Title = "Общее",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork",
        };
        var note = new StickyNote
        {
            Board = board,
            Text = "Удаляемая важная заметка",
            Color = "yellow",
            IsPinned = true,
            Version = 3,
            Width = 220,
            Height = 180,
        };
        db.StickyNotes.Add(note);
        await db.SaveChangesAsync();
        var controller = CreateController(db, author, notificationService: CreateNotificationService(db));

        var result = await controller.DeleteNote(note.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var notification = await db.UserNotifications.SingleAsync();
        Assert.Equal(recipient.Id, notification.RecipientUserId);
        Assert.Equal(UserNotificationTypes.NoteImportantChanged, notification.Type);
        Assert.Equal(note.Id, notification.ObjectId);
        Assert.Contains("удалил", notification.BodyPreview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(await db.UserNotifications.ToListAsync(), item => item.RecipientUserId == author.Id);
    }

    [Fact]
    public async Task UpdateNoteLayout_DoesNotCreateMentionNotification()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db);
        var mentioned = await CreateUserAsync(db);
        mentioned.Email = "layout.notes@example.com";
        var board = new StickyNoteBoard
        {
            Title = "Общее",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork",
        };
        var note = new StickyNote
        {
            Board = board,
            Text = $"@{mentioned.Email} уже есть в тексте",
            Color = "yellow",
            Version = 1,
            Width = 220,
            Height = 180,
        };
        db.StickyNotes.Add(note);
        await db.SaveChangesAsync();
        var controller = CreateController(db, author, notificationService: CreateNotificationService(db));

        var result = await controller.UpdateNoteLayout(note.Id, new UpdateStickyNoteLayoutRequest
        {
            X = 40,
            Y = 50,
            Width = 240,
            Height = 180,
            ZIndex = 2,
            Version = 1,
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(await db.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task UpdateNote_ConcurrentWritersCannotSilentlyOverwriteSameVersion()
    {
        var settings = new SqliteConnectionStringBuilder { DataSource = "pp_sticky_race_" + Guid.NewGuid().ToString("N"), Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared, Pooling = false };
        await using var keeper = new SqliteConnection(settings.ConnectionString);
        await keeper.OpenAsync();
        DbContextOptions<PaymentContext> Options(SaveChangesInterceptor? interceptor = null)
        {
            var builder = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(settings.ConnectionString, sqlite => sqlite.MigrationsAssembly(typeof(PaymentContext).Assembly.FullName));
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            return builder.Options;
        }
        await using var setup = new PaymentContext(Options());
        await setup.Database.EnsureCreatedAsync();
        var user = await CreateUserAsync(setup);
        var note = new StickyNote { Board = new StickyNoteBoard { Title = "Общая", Visibility = StickyNoteBoardVisibility.Shared, Background = "cork" }, Text = "Исходный текст", Color = "yellow", Version = 1, Width = 220, Height = 180 };
        setup.StickyNotes.Add(note);
        await setup.SaveChangesAsync();
        var gate = new PauseStickySave();
        await using var slowDb = new PaymentContext(Options(gate));
        await using var fastDb = new PaymentContext(Options());
        UpdateStickyNoteRequest Request(string text) => new() { Text = text, Color = "yellow", Width = 220, Height = 180, ZIndex = 1, Version = 1 };
        var slow = CreateController(slowDb, user).UpdateNote(note.Id, Request("Первый черновик"), CancellationToken.None);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            var fast = await CreateController(fastDb, user).UpdateNote(note.Id, Request("Сохранённый текст"), CancellationToken.None);
            Assert.Equal(423, Assert.IsType<ObjectResult>(fast.Result).StatusCode);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>((await slow.WaitAsync(TimeSpan.FromSeconds(15))).Result);
        var retry = await CreateController(fastDb, user).UpdateNote(note.Id, Request("Сохранённый текст"), CancellationToken.None);
        var conflict = Assert.IsType<ConflictObjectResult>(retry.Result);
        Assert.Equal("Первый черновик", Assert.IsType<StickyNoteDto>(conflict.Value).Text);
        setup.ChangeTracker.Clear();
        var saved = await setup.StickyNotes.AsNoTracking().SingleAsync(item => item.Id == note.Id);
        Assert.Equal("Первый черновик", saved.Text);
        Assert.Equal(2, saved.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoteEdit_RejectsConcurrentLayoutOrDeletion(bool delete)
    {
        var settings = new SqliteConnectionStringBuilder { DataSource = "pp_sticky_race_" + Guid.NewGuid().ToString("N"), Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared, Pooling = false };
        await using var keeper = new SqliteConnection(settings.ConnectionString);
        await keeper.OpenAsync();
        DbContextOptions<PaymentContext> Options(SaveChangesInterceptor? interceptor = null)
        {
            var builder = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(settings.ConnectionString, sqlite => sqlite.MigrationsAssembly(typeof(PaymentContext).Assembly.FullName));
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            return builder.Options;
        }
        await using var setup = new PaymentContext(Options());
        await setup.Database.EnsureCreatedAsync();
        var user = await CreateUserAsync(setup);
        var note = new StickyNote { Board = new StickyNoteBoard { Title = "Общая", Visibility = StickyNoteBoardVisibility.Shared, Background = "cork" }, Text = "Исходный текст", Color = "yellow", Version = 1, Width = 220, Height = 180 };
        setup.StickyNotes.Add(note); await setup.SaveChangesAsync();
        var gate = new PauseStickySave();
        await using var slowDb = new PaymentContext(Options(gate));
        await using var fastDb = new PaymentContext(Options());
        var pending = CreateController(slowDb, user).UpdateNote(note.Id, new UpdateStickyNoteRequest { Text = "Новый текст", Color = "yellow", Width = 220, Height = 180, ZIndex = 1, Version = 1 }, CancellationToken.None);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            var controller = CreateController(fastDb, user);
            if (delete) Assert.Equal(423, Assert.IsType<ObjectResult>(await controller.DeleteNote(note.Id, CancellationToken.None)).StatusCode);
            else Assert.Equal(423, Assert.IsType<ObjectResult>((await controller.UpdateNoteLayout(note.Id, new UpdateStickyNoteLayoutRequest { X = 150, Y = 25, Width = 220, Height = 180, ZIndex = 1, Version = 1 }, CancellationToken.None)).Result).StatusCode);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>((await pending.WaitAsync(TimeSpan.FromSeconds(15))).Result);
        var retry = CreateController(fastDb, user);
        if (delete) Assert.IsType<NoContentResult>(await retry.DeleteNote(note.Id, CancellationToken.None));
        else Assert.IsType<ConflictObjectResult>((await retry.UpdateNoteLayout(note.Id, new UpdateStickyNoteLayoutRequest { X = 150, Y = 25, Width = 220, Height = 180, ZIndex = 1, Version = 1 }, CancellationToken.None)).Result);
        setup.ChangeTracker.Clear();
        var saved = await setup.StickyNotes.IgnoreQueryFilters().SingleAsync(item => item.Id == note.Id);
        Assert.Equal("Новый текст", saved.Text);
        Assert.Equal(delete ? 3 : 2, saved.Version);
        Assert.Equal(delete, setup.Entry(saved).Property<bool>("IsDeleted").CurrentValue);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("layout")]
    [InlineData("delete")]
    [InlineData("revoke")]
    [InlineData("revoke_only")]
    public async Task NoteEdit_PostgreSqlRejectsConcurrentChangeAndRechecksAccess(string scenario)
    {
        await using var template = new PostgreSqlModelTemplate();
        await template.InitializeAsync();
        await using var lease = await template.CreateDatabaseAsync();
        DbContextOptions<PostgreSqlPaymentContext> Options(SaveChangesInterceptor? interceptor = null)
        {
            var builder = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString);
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            return builder.Options;
        }
        await using var setup = new PostgreSqlPaymentContext(Options());
        var user = await CreateUserAsync(setup);
        var note = new StickyNote { Board = new StickyNoteBoard { Title = "Общая", Visibility = StickyNoteBoardVisibility.Shared, Background = "cork" }, Text = "Исходный текст", Color = "yellow", Version = 1, Width = 220, Height = 180 };
        setup.StickyNotes.Add(note); await setup.SaveChangesAsync();
        var gate = new PauseStickySave();
        await using var slowDb = new PostgreSqlPaymentContext(Options(gate));
        await using var fastDb = new PostgreSqlPaymentContext(Options());
        UpdateStickyNoteRequest Request(string text) => new() { Text = text, Color = "yellow", Width = 220, Height = 180, ZIndex = 1, Version = 1 };
        var pending = CreateController(slowDb, user).UpdateNote(note.Id, Request("Поздний черновик"), CancellationToken.None);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Task<int>? revokeTask = null;
        try
        {
            var controller = CreateController(fastDb, user);
            if (scenario == "delete") Assert.IsType<NoContentResult>(await controller.DeleteNote(note.Id, CancellationToken.None));
            else if (scenario == "layout") Assert.IsType<OkObjectResult>((await controller.UpdateNoteLayout(note.Id, new UpdateStickyNoteLayoutRequest { X = 150, Y = 25, Width = 220, Height = 180, ZIndex = 1, Version = 1 }, CancellationToken.None)).Result);
            else
            {
                if (scenario != "revoke_only") Assert.IsType<OkObjectResult>((await controller.UpdateNote(note.Id, Request("Сохранённый текст"), CancellationToken.None)).Result);
                if (scenario is "revoke" or "revoke_only")
                {
                    var permission = await fastDb.RolePermissions.SingleAsync(item => item.RoleId == user.RoleId && item.Section == RolePermissionCatalog.Notes);
                    permission.CanEdit = false;
                    revokeTask = fastDb.SaveChangesAsync();
                    Assert.NotSame(revokeTask, await Task.WhenAny(revokeTask, Task.Delay(250)));
                }
            }
        }
        finally { gate.Release.TrySetResult(); }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(15));
        if (scenario == "delete") Assert.IsType<NotFoundResult>(result.Result);
        else if (scenario == "revoke_only") Assert.IsType<OkObjectResult>(result.Result);
        else Assert.IsType<ConflictObjectResult>(result.Result);
        if (revokeTask is not null)
        {
            await revokeTask.WaitAsync(TimeSpan.FromSeconds(15));
            await using var afterRevocation = new PostgreSqlPaymentContext(Options());
            Assert.IsType<ForbidResult>((await CreateController(afterRevocation, user).UpdateNote(note.Id, Request("Недоступный текст"), CancellationToken.None)).Result);
        }
        setup.ChangeTracker.Clear();
        var saved = await setup.StickyNotes.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == note.Id);
        Assert.Equal(scenario is "edit" or "revoke" ? "Сохранённый текст" : scenario == "revoke_only" ? "Поздний черновик" : "Исходный текст", saved.Text);
        Assert.Equal(2, saved.Version);
    }

    [Fact]
    public async Task NoteEdit_PostgreSqlFailsClosedWhilePermissionRevocationIsPending()
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var lease = await template.CreateDatabaseAsync();
        DbContextOptions<PostgreSqlPaymentContext> Options() => new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options;
        await using var setup = new PostgreSqlPaymentContext(Options());
        var user = await CreateUserAsync(setup);
        var note = new StickyNote { Board = new StickyNoteBoard { Title = "Общая", Visibility = StickyNoteBoardVisibility.Shared, Background = "cork" }, Text = "Исходный текст", Color = "yellow", Version = 1, Width = 220, Height = 180 };
        setup.StickyNotes.Add(note); await setup.SaveChangesAsync();
        await using var revoker = new PostgreSqlPaymentContext(Options());
        await using var edit = new PostgreSqlPaymentContext(Options());
        await using var transaction = await revoker.Database.BeginTransactionAsync();
        var permission = await revoker.RolePermissions.SingleAsync(item => item.RoleId == user.RoleId && item.Section == RolePermissionCatalog.Notes);
        permission.CanEdit = false; await revoker.SaveChangesAsync();
        var request = new UpdateStickyNoteRequest { Text = "Недоступный текст", Color = "yellow", Width = 220, Height = 180, ZIndex = 1, Version = 1 };
        var busy = await CreateController(edit, user).UpdateNote(note.Id, request, CancellationToken.None);
        Assert.Equal(423, Assert.IsType<ObjectResult>(busy.Result).StatusCode);
        await transaction.CommitAsync();
        var denied = await CreateController(edit, user).UpdateNote(note.Id, request, CancellationToken.None);
        Assert.IsType<ForbidResult>(denied.Result);
        setup.ChangeTracker.Clear();
        var saved = await setup.StickyNotes.AsNoTracking().SingleAsync(item => item.Id == note.Id);
        Assert.Equal("Исходный текст", saved.Text); Assert.Equal(1, saved.Version);
    }

    [Theory]
    [InlineData("remove-member")]
    [InlineData("viewer")]
    [InlineData("deactivate")]
    [InlineData("make-private")]
    public async Task NoteEdit_PostgreSqlFailsClosedDuringBoardOrUserAccessChange(string scenario)
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var lease = await template.CreateDatabaseAsync();
        DbContextOptions<PostgreSqlPaymentContext> Options() => new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options;
        await using var setup = new PostgreSqlPaymentContext(Options());
        var owner = await CreateUserAsync(setup); var editor = await CreateUserAsync(setup);
        var board = new StickyNoteBoard { Title = "Проверочная доска", Visibility = scenario == "make-private" ? StickyNoteBoardVisibility.Shared : StickyNoteBoardVisibility.Personal, OwnerUserId = owner.Id, Background = "cork", Members = [new StickyNoteBoardMember { UserId = editor.Id, Role = "editor" }] };
        var note = new StickyNote { Board = board, Text = "Исходный текст", Color = "yellow", Version = 1, Width = 220, Height = 180 };
        setup.StickyNotes.Add(note); await setup.SaveChangesAsync();
        await using var revoker = new PostgreSqlPaymentContext(Options());
        await using var edit = new PostgreSqlPaymentContext(Options());
        await using var transaction = await revoker.Database.BeginTransactionAsync();
        if (scenario is "remove-member" or "viewer")
        {
            var member = await revoker.StickyNoteBoardMembers.SingleAsync(item => item.BoardId == board.Id && item.UserId == editor.Id);
            if (scenario == "viewer") member.Role = "viewer"; else revoker.StickyNoteBoardMembers.Remove(member);
        }
        else if (scenario == "deactivate") (await revoker.Users.SingleAsync(item => item.Id == editor.Id)).IsActive = false;
        else
        {
            var currentBoard = await revoker.StickyNoteBoards.SingleAsync(item => item.Id == board.Id);
            currentBoard.Visibility = StickyNoteBoardVisibility.Personal;
            var member = await revoker.StickyNoteBoardMembers.SingleAsync(item => item.BoardId == board.Id && item.UserId == editor.Id);
            revoker.StickyNoteBoardMembers.Remove(member);
        }
        await revoker.SaveChangesAsync();
        var request = new UpdateStickyNoteRequest { Text = "Недоступный текст", Color = "yellow", Width = 220, Height = 180, ZIndex = 1, Version = 1 };
        var busy = await CreateController(edit, editor).UpdateNote(note.Id, request, CancellationToken.None);
        Assert.Equal(423, Assert.IsType<ObjectResult>(busy.Result).StatusCode);
        await transaction.CommitAsync();
        var denied = await CreateController(edit, editor).UpdateNote(note.Id, request, CancellationToken.None);
        Assert.IsType<ForbidResult>(denied.Result);
        setup.ChangeTracker.Clear();
        var saved = await setup.StickyNotes.AsNoTracking().SingleAsync(item => item.Id == note.Id);
        Assert.Equal("Исходный текст", saved.Text); Assert.Equal(1, saved.Version);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NoteEdit_PostgreSqlOrdersFirstLegacyPermissionAgainstSave(bool permissionFirst)
    {
        await using var template = new PostgreSqlModelTemplate(); await template.InitializeAsync();
        await using var lease = await template.CreateDatabaseAsync();
        DbContextOptions<PostgreSqlPaymentContext> Options(SaveChangesInterceptor? interceptor = null)
        {
            var builder = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString);
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            return builder.Options;
        }
        await using var setup = new PostgreSqlPaymentContext(Options());
        var user = await CreateUserAsync(setup);
        var permissions = await setup.RolePermissions.Where(item => item.RoleId == user.RoleId).ToListAsync();
        setup.RolePermissions.RemoveRange(permissions); await setup.SaveChangesAsync();
        var note = new StickyNote { Board = new StickyNoteBoard { Title = "Общая", Visibility = StickyNoteBoardVisibility.Shared, Background = "cork" }, Text = "Исходный текст", Color = "yellow", Version = 1, Width = 220, Height = 180 };
        setup.StickyNotes.Add(note); await setup.SaveChangesAsync();
        var gate = new PauseStickySave();
        await using var edit = new PostgreSqlPaymentContext(Options(gate));
        await using var admin = new PostgreSqlPaymentContext(Options());
        var request = new UpdateStickyNoteRequest { Text = "Рабочий текст", Color = "yellow", Width = 220, Height = 180, ZIndex = 1, Version = 1 };
        if (permissionFirst)
        {
            await using var transaction = await admin.Database.BeginTransactionAsync();
            admin.RolePermissions.Add(new RolePermission { RoleId = user.RoleId, Section = RolePermissionCatalog.Notes, CanView = false, CanEdit = false });
            await admin.SaveChangesAsync();
            var busy = await CreateController(edit, user).UpdateNote(note.Id, request, CancellationToken.None);
            Assert.Equal(423, Assert.IsType<ObjectResult>(busy.Result).StatusCode);
            await transaction.CommitAsync();
            Assert.IsType<ForbidResult>((await CreateController(edit, user).UpdateNote(note.Id, request, CancellationToken.None)).Result);
        }
        else
        {
            var pending = CreateController(edit, user).UpdateNote(note.Id, request, CancellationToken.None);
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            admin.RolePermissions.Add(new RolePermission { RoleId = user.RoleId, Section = RolePermissionCatalog.Notes, CanView = false, CanEdit = false });
            var insert = admin.SaveChangesAsync();
            try { Assert.NotSame(insert, await Task.WhenAny(insert, Task.Delay(250))); }
            finally { gate.Release.TrySetResult(); }
            Assert.IsType<OkObjectResult>((await pending.WaitAsync(TimeSpan.FromSeconds(15))).Result);
            await insert.WaitAsync(TimeSpan.FromSeconds(15));
            await using var after = new PostgreSqlPaymentContext(Options());
            Assert.IsType<ForbidResult>((await CreateController(after, user).UpdateNote(note.Id, request, CancellationToken.None)).Result);
        }
        setup.ChangeTracker.Clear();
        var saved = await setup.StickyNotes.AsNoTracking().SingleAsync(item => item.Id == note.Id);
        Assert.Equal(permissionFirst ? "Исходный текст" : "Рабочий текст", saved.Text);
        Assert.Equal(permissionFirst ? 1 : 2, saved.Version);
    }

    private sealed class PauseStickySave : SaveChangesInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<StickyNote>().Any(entry => entry.State == EntityState.Modified))
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task UpdateNote_ReturnsConflict_WhenVersionIsStale()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db);
        var board = new StickyNoteBoard
        {
            Title = "Общее",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork",
        };
        var note = new StickyNote
        {
            Board = board,
            Text = "Текущий текст",
            Color = "yellow",
            Version = 3,
            Width = 220,
            Height = 180,
        };
        db.StickyNotes.Add(note);
        await db.SaveChangesAsync();

        var controller = CreateController(db, user);

        var result = await controller.UpdateNote(note.Id, new UpdateStickyNoteRequest
        {
            Text = "Старый клиент",
            Color = "blue",
            X = 10,
            Y = 20,
            Width = 220,
            Height = 180,
            ZIndex = 2,
            Version = 2,
        }, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var dto = Assert.IsType<StickyNoteDto>(conflict.Value);
        Assert.Equal(3, dto.Version);
        Assert.Equal("Текущий текст", dto.Text);
    }

    [Fact]
    public async Task DeleteNote_SoftDeletesNote()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db);
        var board = new StickyNoteBoard
        {
            Title = "Личная",
            Visibility = StickyNoteBoardVisibility.Personal,
            OwnerUserId = user.Id,
            OwnerUserName = user.FullName,
            Background = "gray",
        };
        var note = new StickyNote
        {
            Board = board,
            Text = "Удалить",
            Color = "yellow",
            Version = 1,
            Width = 220,
            Height = 180,
        };
        db.StickyNotes.Add(note);
        await db.SaveChangesAsync();

        var controller = CreateController(db, user);

        var result = await controller.DeleteNote(note.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(await db.StickyNotes.ToListAsync());
        Assert.True(await db.StickyNotes.IgnoreQueryFilters().AnyAsync(item => item.Id == note.Id && EF.Property<bool>(item, "IsDeleted")));
    }

    [Fact]
    public async Task DeleteNote_RequiresDeletePermission()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, canDelete: false);
        var board = new StickyNoteBoard
        {
            Title = "Личная",
            Visibility = StickyNoteBoardVisibility.Personal,
            OwnerUserId = user.Id,
            OwnerUserName = user.FullName,
            Background = "gray",
        };
        var note = new StickyNote
        {
            Board = board,
            Text = "Не удалять",
            Color = "yellow",
            Version = 1,
            Width = 220,
            Height = 180,
        };
        db.StickyNotes.Add(note);
        await db.SaveChangesAsync();

        var controller = CreateController(db, user);

        var result = await controller.DeleteNote(note.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Contains(await db.StickyNotes.ToListAsync(), item => item.Id == note.Id);
    }

    [Fact]
    public async Task BoardViewer_CanReadButCannotEditNotes()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db);
        var viewer = await CreateUserAsync(db);
        var board = new StickyNoteBoard
        {
            Title = "Личная владельца",
            Visibility = StickyNoteBoardVisibility.Personal,
            OwnerUserId = owner.Id,
            OwnerUserName = owner.FullName,
            Background = "gray",
            Members = new List<StickyNoteBoardMember>
            {
                new() { UserId = viewer.Id, Role = "viewer" },
            },
        };
        var note = new StickyNote
        {
            Board = board,
            Text = "Только чтение",
            Color = "yellow",
            Version = 1,
            Width = 220,
            Height = 180,
        };
        db.StickyNotes.Add(note);
        await db.SaveChangesAsync();

        var controller = CreateController(db, viewer);

        var readResult = await controller.GetNotes(board.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(readResult.Result);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<StickyNoteDto>>(ok.Value));

        var updateResult = await controller.UpdateNote(note.Id, new UpdateStickyNoteRequest
        {
            Text = "Попытка изменения",
            Color = "blue",
            X = 10,
            Y = 20,
            Width = 220,
            Height = 180,
            ZIndex = 2,
            Version = 1,
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(updateResult.Result);
    }

    [Fact]
    public async Task BoardEditor_CanUpdateSharedPersonalBoardNote()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db);
        var editor = await CreateUserAsync(db);
        var board = new StickyNoteBoard
        {
            Title = "Личная владельца",
            Visibility = StickyNoteBoardVisibility.Personal,
            OwnerUserId = owner.Id,
            OwnerUserName = owner.FullName,
            Background = "gray",
            Members = new List<StickyNoteBoardMember>
            {
                new() { UserId = editor.Id, Role = "editor" },
            },
        };
        var note = new StickyNote
        {
            Board = board,
            Text = "До",
            Color = "yellow",
            Version = 1,
            Width = 220,
            Height = 180,
        };
        db.StickyNotes.Add(note);
        await db.SaveChangesAsync();

        var controller = CreateController(db, editor);

        var result = await controller.UpdateNote(note.Id, new UpdateStickyNoteRequest
        {
            Text = "После",
            Color = "green",
            X = 10,
            Y = 20,
            Width = 220,
            Height = 180,
            ZIndex = 2,
            Version = 1,
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<StickyNoteDto>(ok.Value);
        Assert.Equal("После", dto.Text);
        Assert.Equal(2, dto.Version);
    }

    [Fact]
    public async Task RestoreNote_ReturnsSoftDeletedNote()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db);
        var board = new StickyNoteBoard
        {
            Title = "Личная",
            Visibility = StickyNoteBoardVisibility.Personal,
            OwnerUserId = user.Id,
            OwnerUserName = user.FullName,
            Background = "gray",
        };
        var note = new StickyNote
        {
            Board = board,
            Text = "Вернуть",
            Color = "yellow",
            Version = 1,
            Width = 220,
            Height = 180,
        };
        db.StickyNotes.Add(note);
        await db.SaveChangesAsync();
        db.StickyNotes.Remove(note);
        await db.SaveChangesAsync();

        var controller = CreateController(db, user);

        var result = await controller.RestoreNote(note.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<StickyNoteDto>(ok.Value);
        Assert.Equal("Вернуть", dto.Text);
        Assert.Contains(await db.StickyNotes.ToListAsync(), item => item.Id == note.Id);
    }

    [Fact]
    public async Task RestoreNote_ReturnsConflict_WhenBoardLimitReached()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db);
        var board = new StickyNoteBoard
        {
            Title = "Личная",
            Visibility = StickyNoteBoardVisibility.Personal,
            OwnerUserId = user.Id,
            OwnerUserName = user.FullName,
            Background = "gray",
        };
        var deletedNote = new StickyNote
        {
            Board = board,
            Text = "Вернуть",
            Color = "yellow",
            Version = 1,
            Width = 220,
            Height = 180,
        };
        db.StickyNotes.Add(deletedNote);
        await db.SaveChangesAsync();
        db.StickyNotes.Remove(deletedNote);
        await db.SaveChangesAsync();

        for (var index = 0; index < 200; index++)
        {
            db.StickyNotes.Add(new StickyNote
            {
                BoardId = board.Id,
                Text = $"Активная заметка {index}",
                Color = "yellow",
                Version = 1,
                Width = 220,
                Height = 180,
            });
        }
        await db.SaveChangesAsync();

        var controller = CreateController(db, user);

        var result = await controller.RestoreNote(deletedNote.Id, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        var isStillDeleted = await db.StickyNotes
            .IgnoreQueryFilters()
            .Where(item => item.Id == deletedNote.Id)
            .Select(item => EF.Property<bool>(item, "IsDeleted"))
            .SingleAsync();
        Assert.True(isStillDeleted);
        Assert.Equal(200, await db.StickyNotes.CountAsync(item => item.BoardId == board.Id));
    }

    [Fact]
    public async Task UpsertBoardMember_ReturnsNotFound_WhenUserCannotViewNotes()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db);
        var deniedUser = await CreateUserAsync(db, canView: false);
        var board = new StickyNoteBoard
        {
            Title = "Личная владельца",
            Visibility = StickyNoteBoardVisibility.Personal,
            OwnerUserId = owner.Id,
            OwnerUserName = owner.FullName,
            Background = "gray",
        };
        db.StickyNoteBoards.Add(board);
        await db.SaveChangesAsync();

        var controller = CreateController(db, owner);

        var result = await controller.UpsertBoardMember(
            board.Id,
            deniedUser.Id,
            new UpsertStickyNoteBoardMemberRequest { Role = "viewer" },
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Empty(await db.StickyNoteBoardMembers.ToListAsync());
    }

    [Fact]
    public async Task DeleteBoard_ForbidsEditorMemberFromDeletingOwnersPersonalBoard()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db);
        var editor = await CreateUserAsync(db);
        var board = new StickyNoteBoard
        {
            Title = "Личная владельца",
            Visibility = StickyNoteBoardVisibility.Personal,
            OwnerUserId = owner.Id,
            OwnerUserName = owner.FullName,
            Background = "gray",
            Members = new List<StickyNoteBoardMember>
            {
                new() { UserId = editor.Id, Role = "editor" },
            },
        };
        db.StickyNoteBoards.Add(board);
        await db.SaveChangesAsync();

        var controller = CreateController(db, editor);

        var result = await controller.DeleteBoard(board.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Contains(await db.StickyNoteBoards.ToListAsync(), item => item.Id == board.Id);
    }

    [Fact]
    public async Task DeleteBoardMember_RemovesUserFromRealtimeRecipients()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db);
        var removedUser = await CreateUserAsync(db);
        var remainingUser = await CreateUserAsync(db);
        var board = new StickyNoteBoard
        {
            Title = "Личная владельца",
            Visibility = StickyNoteBoardVisibility.Personal,
            OwnerUserId = owner.Id,
            OwnerUserName = owner.FullName,
            Background = "gray",
            Members = new List<StickyNoteBoardMember>
            {
                new() { UserId = removedUser.Id, Role = "editor" },
                new() { UserId = remainingUser.Id, Role = "viewer" },
            },
        };
        db.StickyNoteBoards.Add(board);
        await db.SaveChangesAsync();

        var hub = new FakeNotesHubContext();
        var controller = CreateController(db, owner, hub);

        var result = await controller.DeleteBoardMember(board.Id, removedUser.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.DoesNotContain(NotesHub.UserGroup(removedUser.Id), hub.SentGroupNames);
        Assert.Contains(NotesHub.UserGroup(owner.Id), hub.SentGroupNames);
        Assert.Contains(NotesHub.UserGroup(remainingUser.Id), hub.SentGroupNames);
    }

    [Fact]
    public async Task UpdateBoard_PublishesRealtimeEventToBoardRecipients()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db);
        var member = await CreateUserAsync(db);
        var board = new StickyNoteBoard
        {
            Title = "Старая доска",
            Visibility = StickyNoteBoardVisibility.Personal,
            OwnerUserId = owner.Id,
            OwnerUserName = owner.FullName,
            Background = "cork",
            Members = new List<StickyNoteBoardMember>
            {
                new() { UserId = member.Id, Role = "viewer" },
            },
        };
        db.StickyNoteBoards.Add(board);
        await db.SaveChangesAsync();

        var hub = new FakeNotesHubContext();
        var controller = CreateController(db, owner, hub);

        var result = await controller.UpdateBoard(board.Id, new UpdateStickyNoteBoardRequest
        {
            Title = "Новая доска",
            Description = "Короткое описание",
            Background = "gray",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<StickyNoteBoardDto>(ok.Value);
        Assert.Equal("Новая доска", dto.Title);
        Assert.Equal("Короткое описание", dto.Description);
        Assert.Equal("gray", dto.Background);
        Assert.Contains(NotesHub.UserGroup(owner.Id), hub.SentGroupNames);
        Assert.Contains(NotesHub.UserGroup(member.Id), hub.SentGroupNames);
    }

    [Fact]
    public async Task UpdateBoard_ForbidsEditorMemberFromChangingOwnersPersonalBoard()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db);
        var editor = await CreateUserAsync(db);
        var board = new StickyNoteBoard
        {
            Title = "Личная владельца",
            Description = "Старое описание",
            Visibility = StickyNoteBoardVisibility.Personal,
            OwnerUserId = owner.Id,
            OwnerUserName = owner.FullName,
            Background = "cork",
            Members = new List<StickyNoteBoardMember>
            {
                new() { UserId = editor.Id, Role = "editor" },
            },
        };
        db.StickyNoteBoards.Add(board);
        await db.SaveChangesAsync();

        var result = await CreateController(db, editor).UpdateBoard(board.Id, new UpdateStickyNoteBoardRequest
        {
            Title = "Чужое изменение",
            Description = "Новое описание",
            Background = "gray",
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        var unchangedBoard = await db.StickyNoteBoards.AsNoTracking().SingleAsync(item => item.Id == board.Id);
        Assert.Equal("Личная владельца", unchangedBoard.Title);
        Assert.Equal("Старое описание", unchangedBoard.Description);
        Assert.Equal("cork", unchangedBoard.Background);
    }

    private static async Task<PaymentContext> CreateContextAsync(DbCommandInterceptor? commandInterceptor = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var optionsBuilder = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection);
        if (commandInterceptor is not null)
        {
            optionsBuilder.AddInterceptors(commandInterceptor);
        }

        var db = new PaymentContext(optionsBuilder.Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        public List<string> ReaderCommands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ReaderCommands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static async Task<User> CreateUserAsync(
        PaymentContext db,
        bool canView = true,
        bool canCreate = true,
        bool canEdit = true,
        bool canDelete = true)
    {
        var role = new Role
        {
            Name = Guid.NewGuid().ToString("N"),
            Description = "Тестовая роль",
        };
        role.Permissions = RolePermissionCatalog.CreateEntities(role.Id, allowRoleManagement: false);
        var notesPermission = role.Permissions.Single(permission => permission.Section == RolePermissionCatalog.Notes);
        notesPermission.CanView = canView;
        notesPermission.CanCreate = canCreate;
        notesPermission.CanEdit = canEdit;
        notesPermission.CanDelete = canDelete;

        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@example.com",
            FullName = "Тестовый пользователь",
            IsActive = true,
            IsApproved = true,
            Role = role,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static StickyNotesController CreateController(
        PaymentContext db,
        User user,
        FakeNotesHubContext? hub = null,
        INotificationService? notificationService = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Email, user.Email),
        };

        return new StickyNotesController(db, hub ?? new FakeNotesHubContext(), notificationService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
                },
            },
        };
    }

    private static NotificationService CreateNotificationService(PaymentContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        return new NotificationService(
            db,
            configuration,
            new NoOpEmailService(),
            new NoOpTelegramNotificationService(),
            NullLogger<NotificationService>.Instance);
    }

    private sealed class FakeNotesHubContext : IHubContext<NotesHub>
    {
        private readonly FakeHubClients _clients = new();

        public IHubClients Clients => _clients;
        public IGroupManager Groups { get; } = new FakeGroupManager();
        public IReadOnlyCollection<string> SentGroupNames => _clients.SentGroupNames;
        public string? LastMethodName => _clients.LastMethodName;
        public IReadOnlyList<object?> LastArguments => _clients.LastArguments;
    }

    private sealed class FakeHubClients : IHubClients
    {
        private readonly FakeClientProxy _proxy = new();

        public IReadOnlyCollection<string> SentGroupNames => _proxy.GroupNames;
        public string? LastMethodName => _proxy.LastMethodName;
        public IReadOnlyList<object?> LastArguments => _proxy.LastArguments;

        public IClientProxy All => _proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Client(string connectionId) => _proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;
        public IClientProxy Group(string groupName)
        {
            _proxy.GroupNames.Add(groupName);
            return _proxy;
        }

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds)
        {
            _proxy.GroupNames.Add(groupName);
            return _proxy;
        }

        public IClientProxy Groups(IReadOnlyList<string> groupNames)
        {
            _proxy.GroupNames.AddRange(groupNames);
            return _proxy;
        }

        public IClientProxy User(string userId) => _proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
    }

    private sealed class FakeClientProxy : IClientProxy
    {
        public List<string> GroupNames { get; } = new();
        public string? LastMethodName { get; private set; }
        public IReadOnlyList<object?> LastArguments { get; private set; } = Array.Empty<object?>();

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            LastMethodName = method;
            LastArguments = args;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoOpEmailService : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName)
            => Task.CompletedTask;

        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName)
            => Task.CompletedTask;

        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason)
            => Task.CompletedTask;

        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail)
            => Task.CompletedTask;

        public Task SendEmailWithAttachmentAsync(
            string toEmail,
            string subject,
            string body,
            string attachmentFileName,
            byte[] attachmentBytes,
            string attachmentContentType)
            => Task.CompletedTask;
    }

    private sealed class NoOpTelegramNotificationService : ITelegramNotificationService
    {
        public Task<TelegramNotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TelegramNotificationSettingsDto());

        public Task<TelegramSendResult> SendMessageAsync(long chatId, string text, CancellationToken cancellationToken = default)
            => Task.FromResult(new TelegramSendResult(true, NotificationDeliveryStatus.Sent));

        public Task<TelegramSendResult> SendMessageWithTokenAsync(long chatId, string text, string botToken, CancellationToken cancellationToken = default, TelegramBotConnectionSettings? connection = null)
            => Task.FromResult(new TelegramSendResult(true, NotificationDeliveryStatus.Sent));
    }
}
