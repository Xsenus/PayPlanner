using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Hubs;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class NotesHubTests
{
    [Fact]
    public async Task OnConnectedAsync_AddsUserGroup_WhenUserCanViewNotes()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db);
        var groups = new FakeGroupManager();
        var context = new FakeHubCallerContext(user);
        var hub = new NotesHub(db)
        {
            Context = context,
            Groups = groups,
        };

        await hub.OnConnectedAsync();

        Assert.False(context.Aborted);
        Assert.Contains(NotesHub.UserGroup(user.Id), groups.AddedGroupNames);
    }

    [Fact]
    public async Task OnConnectedAsync_Aborts_WhenUserCannotViewNotes()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, canViewNotes: false);
        var groups = new FakeGroupManager();
        var context = new FakeHubCallerContext(user);
        var hub = new NotesHub(db)
        {
            Context = context,
            Groups = groups,
        };

        await hub.OnConnectedAsync();

        Assert.True(context.Aborted);
        Assert.DoesNotContain(NotesHub.UserGroup(user.Id), groups.AddedGroupNames);
    }

    [Fact]
    public async Task OnConnectedAsync_Aborts_WhenUserIsNotApproved()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, isApproved: false);
        var groups = new FakeGroupManager();
        var context = new FakeHubCallerContext(user);
        var hub = new NotesHub(db)
        {
            Context = context,
            Groups = groups,
        };

        await hub.OnConnectedAsync();

        Assert.True(context.Aborted);
        Assert.DoesNotContain(NotesHub.UserGroup(user.Id), groups.AddedGroupNames);
    }

    [Fact]
    public async Task OnConnectedAsync_Aborts_WhenClaimUserDoesNotExist()
    {
        await using var db = await CreateContextAsync();
        var groups = new FakeGroupManager();
        var context = new FakeHubCallerContext(987654);
        var hub = new NotesHub(db)
        {
            Context = context,
            Groups = groups,
        };

        await hub.OnConnectedAsync();

        Assert.True(context.Aborted);
        Assert.DoesNotContain(NotesHub.UserGroup(987654), groups.AddedGroupNames);
    }

    [Fact]
    public async Task JoinBoard_AddsBoardGroup_WhenUserCanViewSharedBoard()
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
        var groups = new FakeGroupManager();
        var hub = new NotesHub(db)
        {
            Context = new FakeHubCallerContext(user),
            Groups = groups,
        };

        await hub.JoinBoard(board.Id);

        Assert.Contains(NotesHub.BoardGroup(board.Id), groups.AddedGroupNames);
    }

    [Fact]
    public async Task JoinBoard_DoesNotAddBoardGroup_WhenUserCannotViewNotes()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, canViewNotes: false);
        var board = new StickyNoteBoard
        {
            Title = "Общее",
            Visibility = StickyNoteBoardVisibility.Shared,
            Background = "cork",
        };
        db.StickyNoteBoards.Add(board);
        await db.SaveChangesAsync();
        var groups = new FakeGroupManager();
        var hub = new NotesHub(db)
        {
            Context = new FakeHubCallerContext(user),
            Groups = groups,
        };

        await hub.JoinBoard(board.Id);

        Assert.DoesNotContain(NotesHub.BoardGroup(board.Id), groups.AddedGroupNames);
    }

    [Fact]
    public async Task JoinBoard_DoesNotAddBoardGroup_WhenPersonalBoardIsNotVisible()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateUserAsync(db);
        var otherUser = await CreateUserAsync(db);
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
        var groups = new FakeGroupManager();
        var hub = new NotesHub(db)
        {
            Context = new FakeHubCallerContext(otherUser),
            Groups = groups,
        };

        await hub.JoinBoard(board.Id);

        Assert.DoesNotContain(NotesHub.BoardGroup(board.Id), groups.AddedGroupNames);
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
        bool canViewNotes = true,
        bool isActive = true,
        bool isApproved = true)
    {
        var role = new Role
        {
            Name = Guid.NewGuid().ToString("N"),
            Description = "Тестовая роль",
        };
        role.Permissions = RolePermissionCatalog.CreateEntities(role.Id, allowRoleManagement: false);
        var notesPermission = role.Permissions.Single(permission => permission.Section == RolePermissionCatalog.Notes);
        notesPermission.CanView = canViewNotes;

        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@example.com",
            FullName = "Тестовый пользователь",
            IsActive = isActive,
            IsApproved = isApproved,
            Role = role,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private sealed class FakeHubCallerContext : HubCallerContext
    {
        public FakeHubCallerContext(User user)
            : this(user.Id, user.FullName, user.Email)
        {
        }

        public FakeHubCallerContext(int userId)
            : this(userId, "Тестовый пользователь", $"{Guid.NewGuid():N}@example.com")
        {
        }

        private FakeHubCallerContext(int userId, string fullName, string email)
        {
            ConnectionId = Guid.NewGuid().ToString("N");
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Name, fullName),
                    new Claim(ClaimTypes.Email, email),
                },
                authenticationType: "Test"));
        }

        public bool Aborted { get; private set; }
        public override string ConnectionId { get; }
        public override string? UserIdentifier => User?.FindFirstValue(ClaimTypes.NameIdentifier);
        public override ClaimsPrincipal? User { get; }
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted { get; } = CancellationToken.None;

        public override void Abort()
        {
            Aborted = true;
        }
    }

    private sealed class FakeGroupManager : IGroupManager
    {
        public List<string> AddedGroupNames { get; } = new();

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            AddedGroupNames.Add(groupName);
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
