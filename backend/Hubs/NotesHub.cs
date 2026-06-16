using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Hubs;

[Authorize]
public sealed class NotesHub : Hub
{
    public const string Path = "/hubs/notes";
    public const string BoardChangedEvent = "stickyNoteBoardChanged";

    private readonly PaymentContext _db;

    public NotesHub(PaymentContext db)
    {
        _db = db;
    }

    public static string BoardGroup(int boardId) => $"sticky-notes:board:{boardId}";
    public static string UserGroup(int userId) => $"sticky-notes:user:{userId}";

    public override async Task OnConnectedAsync()
    {
        var userId = await GetAuthorizedNotesUserIdAsync();
        if (!userId.HasValue)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId.Value), Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    public async Task JoinBoard(int boardId)
    {
        if (boardId <= 0)
        {
            return;
        }

        var userId = await GetAuthorizedNotesUserIdAsync();
        if (!userId.HasValue)
        {
            return;
        }

        var canViewBoard = await _db.StickyNoteBoards
            .AsNoTracking()
            .AnyAsync(board =>
                board.Id == boardId
                && !board.IsArchived
                && (board.Visibility == StickyNoteBoardVisibility.Shared
                    || board.OwnerUserId == userId.Value
                    || board.Members.Any(member => member.UserId == userId.Value)),
                Context.ConnectionAborted);

        if (!canViewBoard)
        {
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, BoardGroup(boardId), Context.ConnectionAborted);
    }

    public Task LeaveBoard(int boardId)
    {
        if (boardId <= 0)
        {
            return Task.CompletedTask;
        }

        return Groups.RemoveFromGroupAsync(Context.ConnectionId, BoardGroup(boardId));
    }

    private async Task<int?> GetAuthorizedNotesUserIdAsync()
    {
        var userId = Context.User?.GetUserId();
        if (!userId.HasValue || userId.Value <= 0)
        {
            return null;
        }

        var canViewNotes = await _db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId.Value && user.IsActive && user.IsApproved)
            .Select(user =>
                user.Role != null
                && (user.Role.Name == "admin"
                    || user.Role.Permissions.Any(permission =>
                        permission.Section == RolePermissionCatalog.Notes
                        && permission.CanView)))
            .FirstOrDefaultAsync(Context.ConnectionAborted);

        return canViewNotes ? userId.Value : null;
    }
}
