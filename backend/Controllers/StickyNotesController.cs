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

        return Ok(boards.Select(item => ToBoardDto(item.Board, item.NotesCount, CanEditBoard(item.Board, access.Value), access.Value.CanDelete)).ToList());
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

        return CreatedAtAction(nameof(GetNotes), new { boardId = board.Id }, ToBoardDto(board, 0, true, access.Value.CanDelete));
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
        return Ok(ToBoardDto(board, await CountNotesAsync(board.Id, ct), true, access.Value.CanDelete));
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
            .Select(note => ToNoteDto(note))
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

        var dto = ToNoteDto(note);
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

        var dto = ToNoteDto(note);
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

        var dto = ToNoteDto(note);
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

    private async Task<int> CountNotesAsync(int boardId, CancellationToken ct)
        => await _db.StickyNotes.CountAsync(note => note.BoardId == boardId, ct);

    private Task PublishAsync(int boardId, string type, StickyNoteDto? note, int? noteId, NotesAccess access, CancellationToken ct)
        => _hub.Clients
            .Group(NotesHub.BoardGroup(boardId))
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

    private static StickyNoteBoardDto ToBoardDto(StickyNoteBoard board, int notesCount, bool canEdit, bool canDelete)
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
            CreatedAt = board.CreatedAt,
            UpdatedAt = board.UpdatedAt,
        };

    private static StickyNoteDto ToNoteDto(StickyNote note)
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
