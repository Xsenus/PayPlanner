using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Hubs;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/sticky-notes")]
public sealed class StickyNotesController : ControllerBase
{
    private const int MaxNotesPerBoard = 200;

    private static readonly HashSet<string> AllowedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "yellow",
        "blue",
        "green",
        "pink",
        "white",
        "cyan",
    };

    private static readonly HashSet<string> AllowedBackgrounds = new(StringComparer.OrdinalIgnoreCase)
    {
        "cork",
        "gray",
    };

    private readonly PaymentContext _db;
    private readonly IHubContext<NotesHub> _hub;

    public StickyNotesController(PaymentContext db, IHubContext<NotesHub> hub)
    {
        _db = db;
        _hub = hub;
    }

    [HttpGet("boards")]
    public async Task<ActionResult<IReadOnlyCollection<StickyNoteBoardDto>>> GetBoards(CancellationToken ct)
    {
        var access = await GetAccessAsync(static permission => permission.CanView, ct);
        if (access is null)
        {
            return Forbid();
        }

        await EnsureDefaultBoardsAsync(access.Value.UserId, access.Value.DisplayName, ct);

        var boards = await VisibleBoardsQuery(access.Value.UserId)
            .Select(board => new
            {
                Board = board,
                NotesCount = board.Notes.Count,
                IsMember = board.Members.Any(member => member.UserId == access.Value.UserId),
            })
            .OrderBy(item => item.Board.Visibility == StickyNoteBoardVisibility.Shared ? 0 : 1)
            .ThenBy(item => item.Board.SortOrder)
            .ThenBy(item => item.Board.Title)
            .AsNoTracking()
            .ToListAsync(ct);

        return Ok(boards
            .Select(item => ToBoardDto(
                item.Board,
                item.NotesCount,
                CanEditBoard(item.Board, access.Value),
                access.Value.CanDelete,
                CanManageMembers(item.Board, access.Value)))
            .ToList());
    }

    [HttpPost("boards")]
    public async Task<ActionResult<StickyNoteBoardDto>> CreateBoard([FromBody] CreateStickyNoteBoardRequest request, CancellationToken ct)
    {
        var access = await GetAccessAsync(static permission => permission.CanCreate, ct);
        if (access is null)
        {
            return Forbid();
        }

        var visibility = NormalizeVisibility(request.Visibility);
        var board = new StickyNoteBoard
        {
            Title = NormalizeTitle(request.Title),
            Description = NormalizeNullable(request.Description, 500),
            Visibility = visibility,
            OwnerUserId = visibility == StickyNoteBoardVisibility.Personal ? access.Value.UserId : null,
            OwnerUserName = visibility == StickyNoteBoardVisibility.Personal ? access.Value.DisplayName : string.Empty,
            Background = NormalizeBackground(request.Background),
            SortOrder = await _db.StickyNoteBoards.CountAsync(ct),
        };

        _db.StickyNoteBoards.Add(board);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetNotes), new { boardId = board.Id }, ToBoardDto(board, 0, true, access.Value.CanDelete, CanManageMembers(board, access.Value)));
    }

    [HttpPut("boards/{boardId:int}")]
    public async Task<ActionResult<StickyNoteBoardDto>> UpdateBoard(int boardId, [FromBody] UpdateStickyNoteBoardRequest request, CancellationToken ct)
    {
        var access = await GetAccessAsync(static permission => permission.CanEdit, ct);
        if (access is null)
        {
            return Forbid();
        }

        var board = await _db.StickyNoteBoards
            .Include(item => item.Members)
            .FirstOrDefaultAsync(item => item.Id == boardId, ct);
        if (board is null)
        {
            return NotFound();
        }

        if (!CanEditBoard(board, access.Value))
        {
            return Forbid();
        }

        board.Title = NormalizeTitle(request.Title);
        board.Description = NormalizeNullable(request.Description, 500);
        board.Background = NormalizeBackground(request.Background);

        await _db.SaveChangesAsync(ct);
        await PublishAsync(board.Id, "boardUpdated", null, null, access.Value, ct);
        return Ok(ToBoardDto(board, await CountNotesAsync(board.Id, ct), true, access.Value.CanDelete, CanManageMembers(board, access.Value)));
    }

    [HttpDelete("boards/{boardId:int}")]
    public async Task<IActionResult> DeleteBoard(int boardId, CancellationToken ct)
    {
        var access = await GetAccessAsync(static permission => permission.CanDelete, ct);
        if (access is null)
        {
            return Forbid();
        }

        var board = await _db.StickyNoteBoards
            .Include(item => item.Members)
            .FirstOrDefaultAsync(item => item.Id == boardId, ct);
        if (board is null)
        {
            return NotFound();
        }

        if (board.Visibility == StickyNoteBoardVisibility.Shared || !CanEditBoard(board, access.Value))
        {
            return Forbid();
        }

        _db.StickyNoteBoards.Remove(board);
        await _db.SaveChangesAsync(ct);
        await PublishAsync(board.Id, "boardDeleted", null, null, access.Value, ct);
        return NoContent();
    }

    [HttpGet("boards/{boardId:int}/members")]
    public async Task<ActionResult<IReadOnlyCollection<StickyNoteBoardMemberDto>>> GetBoardMembers(int boardId, CancellationToken ct)
    {
        var access = await GetAccessAsync(static permission => permission.CanView, ct);
        if (access is null)
        {
            return Forbid();
        }

        var board = await _db.StickyNoteBoards
            .Include(item => item.Members)
            .ThenInclude(member => member.User)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == boardId, ct);
        if (board is null)
        {
            return NotFound();
        }

        if (!CanViewBoard(board, access.Value.UserId))
        {
            return Forbid();
        }

        return Ok(ToMemberDtos(board));
    }

    [HttpGet("member-candidates")]
    public async Task<ActionResult<IReadOnlyCollection<StickyNoteBoardMemberCandidateDto>>> GetMemberCandidates(CancellationToken ct)
    {
        var access = await GetAccessAsync(static permission => permission.CanView, ct);
        if (access is null)
        {
            return Forbid();
        }

        var users = await _db.Users
            .AsNoTracking()
            .Where(user => user.IsActive && user.IsApproved)
            .OrderBy(user => user.FullName)
            .ThenBy(user => user.Email)
            .Select(user => new StickyNoteBoardMemberCandidateDto
            {
                UserId = user.Id,
                DisplayName = string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName,
                Email = user.Email,
            })
            .ToListAsync(ct);

        return Ok(users);
    }

    [HttpPut("boards/{boardId:int}/members/{userId:int}")]
    public async Task<ActionResult<StickyNoteBoardMemberDto>> UpsertBoardMember(
        int boardId,
        int userId,
        [FromBody] UpsertStickyNoteBoardMemberRequest request,
        CancellationToken ct)
    {
        var access = await GetAccessAsync(static permission => permission.CanEdit, ct);
        if (access is null)
        {
            return Forbid();
        }

        var board = await _db.StickyNoteBoards
            .Include(item => item.Members)
            .ThenInclude(member => member.User)
            .FirstOrDefaultAsync(item => item.Id == boardId, ct);
        if (board is null)
        {
            return NotFound();
        }

        if (!CanManageMembers(board, access.Value) || board.Visibility == StickyNoteBoardVisibility.Shared)
        {
            return Forbid();
        }

        if (board.OwnerUserId == userId)
        {
            return BadRequest(new { message = "Владелец доски уже имеет полный доступ." });
        }

        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == userId && item.IsActive && item.IsApproved, ct);
        if (user is null)
        {
            return NotFound();
        }

        var role = NormalizeMemberRole(request.Role);
        var member = board.Members.FirstOrDefault(item => item.UserId == userId);
        if (member is null)
        {
            member = new StickyNoteBoardMember
            {
                BoardId = board.Id,
                UserId = userId,
                Role = role,
            };
            _db.StickyNoteBoardMembers.Add(member);
        }
        else
        {
            member.Role = role;
        }

        await _db.SaveChangesAsync(ct);
        await PublishAsync(board.Id, "boardMembersChanged", null, null, access.Value, ct);
        return Ok(ToMemberDto(member, board.Id, user));
    }

    [HttpDelete("boards/{boardId:int}/members/{userId:int}")]
    public async Task<IActionResult> DeleteBoardMember(int boardId, int userId, CancellationToken ct)
    {
        var access = await GetAccessAsync(static permission => permission.CanEdit, ct);
        if (access is null)
        {
            return Forbid();
        }

        var board = await _db.StickyNoteBoards
            .Include(item => item.Members)
            .FirstOrDefaultAsync(item => item.Id == boardId, ct);
        if (board is null)
        {
            return NotFound();
        }

        if (!CanManageMembers(board, access.Value) || board.Visibility == StickyNoteBoardVisibility.Shared)
        {
            return Forbid();
        }

        if (board.OwnerUserId == userId)
        {
            return BadRequest(new { message = "Владельца доски нельзя удалить из участников." });
        }

        var member = board.Members.FirstOrDefault(item => item.UserId == userId);
        if (member is null)
        {
            return NotFound();
        }

        _db.StickyNoteBoardMembers.Remove(member);
        await _db.SaveChangesAsync(ct);
        await PublishAsync(board.Id, "boardMembersChanged", null, null, access.Value, ct);
        return NoContent();
    }

    [HttpGet("boards/{boardId:int}/notes")]
    public async Task<ActionResult<IReadOnlyCollection<StickyNoteDto>>> GetNotes(int boardId, CancellationToken ct)
    {
        var access = await GetAccessAsync(static permission => permission.CanView, ct);
        if (access is null)
        {
            return Forbid();
        }

        var board = await _db.StickyNoteBoards
            .Include(item => item.Members)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == boardId, ct);
        if (board is null)
        {
            return NotFound();
        }

        if (!CanViewBoard(board, access.Value.UserId))
        {
            return Forbid();
        }

        var notes = await _db.StickyNotes
            .AsNoTracking()
            .Where(note => note.BoardId == boardId)
            .OrderBy(note => note.ZIndex)
            .ThenBy(note => note.Id)
            .Select(note => new StickyNoteDto
            {
                Id = note.Id,
                BoardId = note.BoardId,
                Text = note.Text,
                Color = note.Color,
                X = note.X,
                Y = note.Y,
                Width = note.Width,
                Height = note.Height,
                ZIndex = note.ZIndex,
                Version = note.Version,
                IsPinned = note.IsPinned,
                AuthorUserId = note.AuthorUserId,
                AuthorUserName = note.AuthorUserName,
                LastUpdatedByUserId = EF.Property<int?>(note, "UpdatedByUserId"),
                LastUpdatedByUserName = EF.Property<string?>(note, "UpdatedByUserName") ?? note.AuthorUserName,
                CreatedAt = note.CreatedAt,
                UpdatedAt = note.UpdatedAt,
            })
            .ToListAsync(ct);

        return Ok(notes);
    }

    [HttpPost("boards/{boardId:int}/notes")]
    public async Task<ActionResult<StickyNoteDto>> CreateNote(int boardId, [FromBody] CreateStickyNoteRequest request, CancellationToken ct)
    {
        var access = await GetAccessAsync(static permission => permission.CanCreate, ct);
        if (access is null)
        {
            return Forbid();
        }

        var board = await _db.StickyNoteBoards
            .Include(item => item.Members)
            .FirstOrDefaultAsync(item => item.Id == boardId, ct);
        if (board is null)
        {
            return NotFound();
        }

        if (!CanEditBoard(board, access.Value))
        {
            return Forbid();
        }

        var notesCount = await CountNotesAsync(boardId, ct);
        if (notesCount >= MaxNotesPerBoard)
        {
            return Conflict(new { message = $"На одной доске можно хранить до {MaxNotesPerBoard} заметок." });
        }

        var maxZIndex = await _db.StickyNotes
            .Where(note => note.BoardId == boardId)
            .Select(note => (int?)note.ZIndex)
            .MaxAsync(ct) ?? 0;

        var note = new StickyNote
        {
            BoardId = boardId,
            Text = NormalizeText(request.Text),
            Color = NormalizeColor(request.Color),
            X = Clamp(request.X ?? 80, 0, 5000),
            Y = Clamp(request.Y ?? 80, 0, 5000),
            Width = Clamp(request.Width ?? 220, 140, 640),
            Height = Clamp(request.Height ?? 180, 120, 640),
            ZIndex = maxZIndex + 1,
            AuthorUserId = access.Value.UserId,
            AuthorUserName = access.Value.DisplayName,
        };

        _db.StickyNotes.Add(note);
        await _db.SaveChangesAsync(ct);

        var dto = ToNoteDto(note, access.Value.UserId, access.Value.DisplayName);
        await PublishAsync(boardId, "noteCreated", dto, note.Id, access.Value, ct);
        return CreatedAtAction(nameof(GetNotes), new { boardId }, dto);
    }

    [HttpPut("notes/{noteId:int}")]
    public async Task<ActionResult<StickyNoteDto>> UpdateNote(int noteId, [FromBody] UpdateStickyNoteRequest request, CancellationToken ct)
    {
        var access = await GetAccessAsync(static permission => permission.CanEdit, ct);
        if (access is null)
        {
            return Forbid();
        }

        var note = await _db.StickyNotes
            .Include(item => item.Board)
            .ThenInclude(board => board!.Members)
            .FirstOrDefaultAsync(item => item.Id == noteId, ct);
        if (note is null || note.Board is null)
        {
            return NotFound();
        }

        if (!CanEditBoard(note.Board, access.Value))
        {
            return Forbid();
        }

        if (request.Version.HasValue && request.Version.Value != note.Version)
        {
            return Conflict(ToNoteDto(note));
        }

        note.Text = NormalizeText(request.Text);
        note.Color = NormalizeColor(request.Color);
        note.X = Clamp(request.X, 0, 5000);
        note.Y = Clamp(request.Y, 0, 5000);
        note.Width = Clamp(request.Width, 140, 640);
        note.Height = Clamp(request.Height, 120, 640);
        note.ZIndex = Math.Max(1, request.ZIndex);
        note.IsPinned = request.IsPinned;
        note.Version++;

        await _db.SaveChangesAsync(ct);

        var dto = ToNoteDto(note, access.Value.UserId, access.Value.DisplayName);
        await PublishAsync(note.BoardId, "noteUpdated", dto, note.Id, access.Value, ct);
        return Ok(dto);
    }

    [HttpPatch("notes/{noteId:int}/layout")]
    public async Task<ActionResult<StickyNoteDto>> UpdateNoteLayout(int noteId, [FromBody] UpdateStickyNoteLayoutRequest request, CancellationToken ct)
    {
        var access = await GetAccessAsync(static permission => permission.CanEdit, ct);
        if (access is null)
        {
            return Forbid();
        }

        var note = await _db.StickyNotes
            .Include(item => item.Board)
            .ThenInclude(board => board!.Members)
            .FirstOrDefaultAsync(item => item.Id == noteId, ct);
        if (note is null || note.Board is null)
        {
            return NotFound();
        }

        if (!CanEditBoard(note.Board, access.Value))
        {
            return Forbid();
        }

        if (request.Version.HasValue && request.Version.Value != note.Version)
        {
            return Conflict(ToNoteDto(note));
        }

        note.X = Clamp(request.X, 0, 5000);
        note.Y = Clamp(request.Y, 0, 5000);
        note.Width = Clamp(request.Width, 140, 640);
        note.Height = Clamp(request.Height, 120, 640);
        note.ZIndex = Math.Max(1, request.ZIndex);
        note.Version++;

        await _db.SaveChangesAsync(ct);

        var dto = ToNoteDto(note, access.Value.UserId, access.Value.DisplayName);
        await PublishAsync(note.BoardId, "noteUpdated", dto, note.Id, access.Value, ct);
        return Ok(dto);
    }

    [HttpDelete("notes/{noteId:int}")]
    public async Task<IActionResult> DeleteNote(int noteId, CancellationToken ct)
    {
        var access = await GetAccessAsync(static permission => permission.CanDelete, ct);
        if (access is null)
        {
            return Forbid();
        }

        var note = await _db.StickyNotes
            .Include(item => item.Board)
            .ThenInclude(board => board!.Members)
            .FirstOrDefaultAsync(item => item.Id == noteId, ct);
        if (note is null || note.Board is null)
        {
            return NotFound();
        }

        if (!CanEditBoard(note.Board, access.Value))
        {
            return Forbid();
        }

        var boardId = note.BoardId;
        _db.StickyNotes.Remove(note);
        await _db.SaveChangesAsync(ct);

        await PublishAsync(boardId, "noteDeleted", null, noteId, access.Value, ct);
        return NoContent();
    }

    [HttpPost("notes/{noteId:int}/restore")]
    public async Task<ActionResult<StickyNoteDto>> RestoreNote(int noteId, CancellationToken ct)
    {
        var access = await GetAccessAsync(static permission => permission.CanDelete, ct);
        if (access is null)
        {
            return Forbid();
        }

        var note = await _db.StickyNotes
            .IgnoreQueryFilters()
            .Include(item => item.Board)
            .ThenInclude(board => board!.Members)
            .FirstOrDefaultAsync(
                item => item.Id == noteId && EF.Property<bool>(item, "IsDeleted"),
                ct);
        if (note is null || note.Board is null)
        {
            return NotFound();
        }

        if (!CanEditBoard(note.Board, access.Value))
        {
            return Forbid();
        }

        _db.Entry(note).Property("IsDeleted").CurrentValue = false;
        note.Version++;
        await _db.SaveChangesAsync(ct);

        var dto = ToNoteDto(note, access.Value.UserId, access.Value.DisplayName);
        await PublishAsync(note.BoardId, "noteRestored", dto, note.Id, access.Value, ct);
        return Ok(dto);
    }

    private async Task<NotesAccess?> GetAccessAsync(Func<RolePermissionValues, bool> isAllowed, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue || userId.Value <= 0)
        {
            return null;
        }

        var canView = await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Notes, static permission => permission.CanView, ct);
        if (!canView || !await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Notes, isAllowed, ct))
        {
            return null;
        }

        var values = await ResolvePermissionValuesAsync(userId.Value, ct);
        return new NotesAccess(
            userId.Value,
            User.GetDisplayName(),
            values.CanCreate,
            values.CanEdit,
            values.CanDelete);
    }

    private async Task<RolePermissionValues> ResolvePermissionValuesAsync(int userId, CancellationToken ct)
    {
        var permission = await _db.GetUserSectionPermissionAsync(userId, RolePermissionCatalog.Notes, ct);
        if (permission is null)
        {
            return RolePermissionCatalog.BuildDefaults(User.IsInRole("admin"))[RolePermissionCatalog.Notes];
        }

        return new RolePermissionValues(
            permission.CanView,
            permission.CanCreate,
            permission.CanEdit,
            permission.CanDelete,
            permission.CanExport,
            permission.CanViewAnalytics,
            permission.ViewScope);
    }

    private async Task EnsureDefaultBoardsAsync(int userId, string displayName, CancellationToken ct)
    {
        var hasPersonal = await _db.StickyNoteBoards
            .AnyAsync(board => board.Visibility == StickyNoteBoardVisibility.Personal && board.OwnerUserId == userId, ct);
        if (!hasPersonal)
        {
            _db.StickyNoteBoards.Add(new StickyNoteBoard
            {
                Title = "Мои заметки",
                Visibility = StickyNoteBoardVisibility.Personal,
                OwnerUserId = userId,
                OwnerUserName = displayName,
                Background = "gray",
                SortOrder = 100,
            });
        }

        var hasShared = await _db.StickyNoteBoards
            .AnyAsync(board => board.Visibility == StickyNoteBoardVisibility.Shared, ct);
        if (!hasShared)
        {
            _db.StickyNoteBoards.Add(new StickyNoteBoard
            {
                Title = "Общее",
                Description = "Общая доска для заметок сотрудников.",
                Visibility = StickyNoteBoardVisibility.Shared,
                Background = "cork",
                SortOrder = 0,
            });
        }

        if (_db.ChangeTracker.HasChanges())
        {
            await _db.SaveChangesAsync(ct);
        }
    }

    private IQueryable<StickyNoteBoard> VisibleBoardsQuery(int userId)
        => _db.StickyNoteBoards
            .Include(board => board.Notes)
            .Include(board => board.Members)
            .Where(board =>
                !board.IsArchived
                && (board.Visibility == StickyNoteBoardVisibility.Shared
                    || board.OwnerUserId == userId
                    || board.Members.Any(member => member.UserId == userId)));

    private static bool CanViewBoard(StickyNoteBoard board, int userId)
        => board.Visibility == StickyNoteBoardVisibility.Shared
            || board.OwnerUserId == userId
            || board.Members.Any(member => member.UserId == userId);

    private static bool CanEditBoard(StickyNoteBoard board, NotesAccess access)
        => access.CanEdit
            && (board.Visibility == StickyNoteBoardVisibility.Shared
                || board.OwnerUserId == access.UserId
                || board.Members.Any(member => member.UserId == access.UserId && !string.Equals(member.Role, "viewer", StringComparison.OrdinalIgnoreCase)));

    private static bool CanManageMembers(StickyNoteBoard board, NotesAccess access)
        => access.CanEdit && board.OwnerUserId == access.UserId;

    private async Task<int> CountNotesAsync(int boardId, CancellationToken ct)
        => await _db.StickyNotes.CountAsync(note => note.BoardId == boardId, ct);

    private async Task PublishAsync(int boardId, string type, StickyNoteDto? note, int? noteId, NotesAccess access, CancellationToken ct)
    {
        var recipientUserIds = await GetBoardRealtimeRecipientUserIdsAsync(boardId, ct);
        if (recipientUserIds.Count == 0)
        {
            return;
        }

        await _hub.Clients
            .Groups(recipientUserIds.Select(NotesHub.UserGroup).Distinct(StringComparer.Ordinal).ToList())
            .SendAsync(
                NotesHub.BoardChangedEvent,
                new StickyNoteRealtimeEventDto
                {
                    Type = type,
                    BoardId = boardId,
                    Note = note,
                    NoteId = noteId,
                    ActorUserId = access.UserId,
                    ActorUserName = access.DisplayName,
                    CreatedAt = DateTime.UtcNow,
                },
                ct);
    }

    private async Task<IReadOnlyList<int>> GetBoardRealtimeRecipientUserIdsAsync(int boardId, CancellationToken ct)
    {
        var board = await _db.StickyNoteBoards
            .AsNoTracking()
            .Include(item => item.Members)
            .FirstOrDefaultAsync(item => item.Id == boardId && !item.IsArchived, ct);
        if (board is null)
        {
            return Array.Empty<int>();
        }

        var query = _db.Users
            .AsNoTracking()
            .Where(user => user.IsActive && user.IsApproved);

        if (board.Visibility == StickyNoteBoardVisibility.Shared)
        {
            query = query.Where(user =>
                user.Role != null
                && (user.Role.Name == "admin"
                    || user.Role.Permissions.Any(permission =>
                        permission.Section == RolePermissionCatalog.Notes
                        && permission.CanView)));
        }
        else
        {
            var boardUserIds = board.Members
                .Select(member => member.UserId)
                .Append(board.OwnerUserId.GetValueOrDefault())
                .Where(userId => userId > 0)
                .Distinct()
                .ToArray();

            if (boardUserIds.Length == 0)
            {
                return Array.Empty<int>();
            }

            query = query.Where(user =>
                boardUserIds.Contains(user.Id)
                && user.Role != null
                && (user.Role.Name == "admin"
                    || user.Role.Permissions.Any(permission =>
                        permission.Section == RolePermissionCatalog.Notes
                        && permission.CanView)));
        }

        return await query
            .Select(user => user.Id)
            .ToListAsync(ct);
    }

    private static StickyNoteBoardDto ToBoardDto(StickyNoteBoard board, int notesCount, bool canEdit, bool canDelete, bool canManageMembers)
        => new()
        {
            Id = board.Id,
            Title = board.Title,
            Description = board.Description,
            Visibility = board.Visibility,
            OwnerUserId = board.OwnerUserId,
            OwnerUserName = board.OwnerUserName,
            Background = board.Background,
            SortOrder = board.SortOrder,
            NotesCount = notesCount,
            CanEdit = canEdit,
            CanDelete = canDelete && board.Visibility == StickyNoteBoardVisibility.Personal,
            CanDeleteNotes = canDelete && canEdit,
            CanManageMembers = canManageMembers,
            CreatedAt = board.CreatedAt,
            UpdatedAt = board.UpdatedAt,
        };

    private static IReadOnlyCollection<StickyNoteBoardMemberDto> ToMemberDtos(StickyNoteBoard board)
    {
        var members = board.Members
            .Select(member => ToMemberDto(member, board.Id, member.User))
            .OrderBy(member => member.Role == "owner" ? 0 : 1)
            .ThenBy(member => member.DisplayName)
            .ToList();

        if (board.OwnerUserId is { } ownerUserId && ownerUserId > 0)
        {
            members.Insert(0, new StickyNoteBoardMemberDto
            {
                Id = 0,
                BoardId = board.Id,
                UserId = ownerUserId,
                DisplayName = string.IsNullOrWhiteSpace(board.OwnerUserName) ? "Владелец" : board.OwnerUserName,
                Role = "owner",
            });
        }

        return members;
    }

    private static StickyNoteBoardMemberDto ToMemberDto(StickyNoteBoardMember member, int boardId, User? user)
        => new()
        {
            Id = member.Id,
            BoardId = boardId,
            UserId = member.UserId,
            DisplayName = user is null
                ? $"Пользователь #{member.UserId}"
                : string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName,
            Email = user?.Email ?? string.Empty,
            Role = NormalizeMemberRole(member.Role),
        };

    private static StickyNoteDto ToNoteDto(StickyNote note, int? lastUpdatedByUserId = null, string? lastUpdatedByUserName = null)
        => new()
        {
            Id = note.Id,
            BoardId = note.BoardId,
            Text = note.Text,
            Color = note.Color,
            X = note.X,
            Y = note.Y,
            Width = note.Width,
            Height = note.Height,
            ZIndex = note.ZIndex,
            Version = note.Version,
            IsPinned = note.IsPinned,
            AuthorUserId = note.AuthorUserId,
            AuthorUserName = note.AuthorUserName,
            LastUpdatedByUserId = lastUpdatedByUserId,
            LastUpdatedByUserName = string.IsNullOrWhiteSpace(lastUpdatedByUserName)
                ? note.AuthorUserName
                : lastUpdatedByUserName,
            CreatedAt = note.CreatedAt,
            UpdatedAt = note.UpdatedAt,
        };

    private static string NormalizeVisibility(string? value)
        => string.Equals(value, StickyNoteBoardVisibility.Shared, StringComparison.OrdinalIgnoreCase)
            ? StickyNoteBoardVisibility.Shared
            : StickyNoteBoardVisibility.Personal;

    private static string NormalizeBackground(string? value)
        => !string.IsNullOrWhiteSpace(value) && AllowedBackgrounds.Contains(value)
            ? value.Trim().ToLowerInvariant()
            : "cork";

    private static string NormalizeColor(string? value)
        => !string.IsNullOrWhiteSpace(value) && AllowedColors.Contains(value)
            ? value.Trim().ToLowerInvariant()
            : "yellow";

    private static string NormalizeMemberRole(string? value)
        => string.Equals(value, "editor", StringComparison.OrdinalIgnoreCase)
            ? "editor"
            : "viewer";

    private static string NormalizeTitle(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? "Новая доска"
            : Truncate(normalized, 120);
    }

    private static string NormalizeText(string? value)
        => Truncate((value ?? string.Empty).Trim(), 4000);

    private static string? NormalizeNullable(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Truncate(value.Trim(), maxLength);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static double Clamp(double value, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return min;
        }

        return Math.Min(max, Math.Max(min, value));
    }

    private readonly record struct NotesAccess(
        int UserId,
        string DisplayName,
        bool CanCreate,
        bool CanEdit,
        bool CanDelete);
}
