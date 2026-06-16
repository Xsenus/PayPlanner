using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Hubs;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
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

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
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

    private static StickyNotesController CreateController(PaymentContext db, User user, FakeNotesHubContext? hub = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Email, user.Email),
        };

        return new StickyNotesController(db, hub ?? new FakeNotesHubContext())
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
}
